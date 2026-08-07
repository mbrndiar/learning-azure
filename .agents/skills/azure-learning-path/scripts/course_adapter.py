#!/usr/bin/env python3
"""Validate the Azure learning path and project it into mentor state JSON.

This adapter is Learning Mentor infrastructure, not part of the C#/.NET course a
learner is expected to write. It implements adapter protocol "1" described in
``.learning-mentor/skills/guided-learning/references/adapter-protocol.md``:

* ``validate`` fails closed on any manifest, graph, path, command, selector, or
  solution-lock error before the mentor may mutate learner state.
* ``state-projection`` emits the neutral acyclic graph the shared state helper
  consumes. The projection is derived from the validated manifest so the course
  never maintains a second graph authority.

At the bootstrap baseline the curriculum is intentionally content-free: an absent
trackable collection is treated as empty, so ``validate`` succeeds with
``trackable_count = 0`` and ``state-projection`` emits ``{"concepts": []}``. The
content rules below still apply in full as later stages populate the manifest,
and the accompanying tests exercise them against fixtures today.

Only the Python 3.11+ standard library is required; ``tomllib`` is stdlib from
3.11 onward, so no third-party TOML package is needed.
"""

from __future__ import annotations

import argparse
import heapq
import json
import re
import shlex
import sys
import tomllib
from collections import Counter
from collections.abc import Iterator, Sequence
from pathlib import Path
from typing import Any, TextIO

SCRIPT_PATH = Path(__file__).resolve()
ADAPTER_DIR = SCRIPT_PATH.parents[1]
REPOSITORY_ROOT = ADAPTER_DIR.parents[2]
MANIFEST_PATH = ADAPTER_DIR / "course.toml"
SKILL_RELATIVE_PATH = Path(".agents/skills/azure-learning-path/SKILL.md")

SUPPORTED_ADAPTER_PROTOCOL = "1"
SUPPORTED_MANIFEST_VERSION = 1
SUPPORTED_SCHEMA_VERSION = "1.0.0"

COURSE_LANGUAGE = "csharp"
TARGET_FRAMEWORK = "net10.0"
DOTNET_SDK_MINIMUM = "10.0"

ID_PATTERN = re.compile(r"^[a-z0-9][a-z0-9._-]*$")
# .NET selects an implementation by project path rather than by an environment
# variable, so the selector records learner and reference path segments.
SUPPORTED_SELECTOR_KIND = "project-path"

EXIT_OK = 0
EXIT_USAGE = 2
EXIT_INVALID_MANIFEST = 3
EXIT_IO = 4


class ManifestValidationError(ValueError):
    """Raised when the course manifest violates the adapter contract."""


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ManifestValidationError(message)


def load_manifest(path: Path = MANIFEST_PATH) -> dict[str, Any]:
    with path.open("rb") as manifest_file:
        return tomllib.load(manifest_file)


def records(
    container: dict[str, Any],
    key: str,
    *,
    context: str = "manifest",
    required: bool = False,
) -> list[dict[str, Any]]:
    """Return an array-of-tables field, treating an absent field as empty.

    The bootstrap manifest omits every trackable collection, so a missing key is
    an empty curriculum rather than an error. When the key is present it must be
    a well-formed array of tables. Set ``required`` for fields that must exist
    once their owner exists (for example a module's concept list).
    """
    if key not in container and not required:
        return []
    value = container.get(key)
    require(isinstance(value, list), f"{context}.{key} must be an array")
    require(
        all(isinstance(item, dict) for item in value),
        f"{context}.{key} entries must be tables",
    )
    return value


def prerequisites(record: dict[str, Any], record_id: str) -> list[str]:
    value = record.get("prerequisites", [])
    require(
        isinstance(value, list)
        and all(isinstance(item, str) and item for item in value),
        f"{record_id} prerequisites must be string IDs",
    )
    require(len(value) == len(set(value)), f"{record_id} prerequisites must be unique")
    require(record_id not in value, f"{record_id} cannot require itself")
    return value


def trackable_records(
    manifest: dict[str, Any],
) -> list[tuple[str, dict[str, Any], dict[str, Any] | None]]:
    result: list[tuple[str, dict[str, Any], dict[str, Any] | None]] = []
    for module in records(manifest, "modules"):
        result.append(("module", module, None))
        for concept in records(
            module, "concepts", context=str(module.get("id", "module")), required=True
        ):
            result.append(("concept", concept, module))
    for owner_kind, child_kind in (("projects", "project"), ("capstones", "capstone")):
        for owner in records(manifest, owner_kind):
            result.append((child_kind, owner, None))
            for milestone in records(
                owner, "milestones", context=str(owner.get("id", child_kind)), required=True
            ):
                result.append(("milestone", milestone, owner))
    return result


def validate_versions(manifest: dict[str, Any]) -> None:
    require(
        manifest.get("manifest_version") == SUPPORTED_MANIFEST_VERSION,
        f"unsupported manifest_version: {manifest.get('manifest_version')!r}",
    )
    require(
        manifest.get("schema_version") == SUPPORTED_SCHEMA_VERSION,
        f"unsupported schema_version: {manifest.get('schema_version')!r}",
    )
    course = manifest.get("course")
    require(isinstance(course, dict), "course must be a table")
    assert isinstance(course, dict)
    require(
        course.get("language") == COURSE_LANGUAGE,
        f"course.language must be {COURSE_LANGUAGE}",
    )
    require(
        course.get("target_framework") == TARGET_FRAMEWORK,
        f"course.target_framework must be {TARGET_FRAMEWORK}",
    )
    require(
        course.get("dotnet_sdk_minimum") == DOTNET_SDK_MINIMUM,
        f"course.dotnet_sdk_minimum must be {DOTNET_SDK_MINIMUM}",
    )
    require(
        course.get("command_working_directory") == ".",
        "course commands must run from the repository root",
    )


def validate_dotnet_support_claim(root: Path = REPOSITORY_ROOT) -> None:
    """Keep the declared .NET boundary consistent with the build configuration.

    A support claim that no manifest backs is exactly the kind of unverifiable
    promise that must fail closed before learner state is touched. The bootstrap
    has no CI workflow yet, so the checkable authorities are ``global.json`` (the
    pinned SDK band) and ``Directory.Build.props`` (the target framework).
    """
    global_json = json.loads((root / "global.json").read_text(encoding="utf-8"))
    sdk_version = global_json.get("sdk", {}).get("version", "")
    require(
        isinstance(sdk_version, str)
        and sdk_version.startswith(f"{DOTNET_SDK_MINIMUM}."),
        f"global.json sdk.version {sdk_version!r} must be on the {DOTNET_SDK_MINIMUM} band",
    )

    build_props = (root / "Directory.Build.props").read_text(encoding="utf-8")
    require(
        f"<TargetFramework>{TARGET_FRAMEWORK}</TargetFramework>" in build_props,
        f"Directory.Build.props must target {TARGET_FRAMEWORK}",
    )


def validate_curriculum_plan(manifest: dict[str, Any], root: Path = REPOSITORY_ROOT) -> None:
    """Keep the mentor manifest a strict subset of the reviewed curriculum plan.

    ``docs/architecture/curriculum.json`` is the planning authority for the whole
    curriculum; this manifest is the *built* subset the mentor may track. A unit
    the plan does not declare, a prerequisite the two disagree about, or a unit
    whose plan record still says its artifacts are planned would let the mentor
    record progress against a course nobody has reviewed or written, so each of
    those fails closed here.
    """
    plan_path = manifest.get("curriculum_plan")
    require(
        isinstance(plan_path, str) and plan_path,
        "curriculum_plan must name the curriculum planning document",
    )
    assert isinstance(plan_path, str)
    resolved = root / plan_path
    require(resolved.is_file(), f"curriculum_plan does not exist: {plan_path}")

    try:
        plan = json.loads(resolved.read_text(encoding="utf-8"))
    except json.JSONDecodeError as error:
        raise ManifestValidationError(f"curriculum_plan is not valid JSON: {error}") from error

    require(
        plan.get("course_id") == manifest["course"]["id"],
        "curriculum_plan course_id does not match course.id",
    )
    units = plan.get("units")
    require(isinstance(units, list), "curriculum_plan units must be an array")
    planned = {unit.get("id"): unit for unit in units if isinstance(unit, dict)}

    for kind in ("modules", "projects", "capstones"):
        for owner in records(manifest, kind):
            owner_id = owner["id"]
            unit = planned.get(owner_id)
            require(unit is not None, f"{owner_id} is not declared in the curriculum plan")
            assert unit is not None
            require(
                unit.get("artifact_status") == "present",
                f"{owner_id} is registered here while the curriculum plan still marks "
                f"its artifacts as planned",
            )
            require(
                sorted(unit.get("prerequisites", []))
                == sorted(prerequisites(owner, owner_id)),
                f"{owner_id} prerequisites disagree with the curriculum plan",
            )
            if kind == "modules":
                continue
            plan_milestones = {
                milestone.get("id"): milestone
                for milestone in unit.get("milestones", [])
                if isinstance(milestone, dict)
            }
            for milestone in records(owner, "milestones", context=owner_id, required=True):
                milestone_id = milestone["id"]
                planned_milestone = plan_milestones.get(milestone_id)
                require(
                    planned_milestone is not None,
                    f"{milestone_id} is not declared by {owner_id} in the curriculum plan",
                )
                assert planned_milestone is not None
                require(
                    sorted(planned_milestone.get("prerequisites", []))
                    == sorted(prerequisites(milestone, milestone_id)),
                    f"{milestone_id} prerequisites disagree with the curriculum plan",
                )


def validate_trackable_ids_and_graph(manifest: dict[str, Any]) -> set[str]:
    entries = trackable_records(manifest)
    expected_prefix = {
        "module": "module.",
        "concept": "concept.",
        "project": "project.",
        "milestone": "milestone.",
        "capstone": "capstone.",
    }
    ids: list[str] = []
    for kind, record, _ in entries:
        record_id = record.get("id")
        require(
            isinstance(record_id, str) and ID_PATTERN.fullmatch(record_id) is not None,
            f"{kind} has an invalid id: {record_id!r}",
        )
        assert isinstance(record_id, str)
        require(
            record_id.startswith(expected_prefix[kind]),
            f"{record_id} must use the {expected_prefix[kind]} prefix",
        )
        require(
            re.search(r"\b[0-9a-f]{7,40}\b", record_id) is None,
            f"{record_id} must not embed a commit hash",
        )
        title = record.get("title")
        require(
            isinstance(title, str) and title.strip(), f"{record_id} must have a title"
        )
        prerequisites(record, record_id)
        ids.append(record_id)

    duplicates = sorted(
        record_id for record_id, count in Counter(ids).items() if count > 1
    )
    require(not duplicates, f"duplicate trackable IDs: {', '.join(duplicates)}")
    known_ids = set(ids)
    for _, record, _ in entries:
        record_id = record["id"]
        for prerequisite in prerequisites(record, record_id):
            require(
                prerequisite in known_ids,
                f"{record_id} has unknown prerequisite {prerequisite}",
            )

    modules = records(manifest, "modules")
    require(
        [module.get("ordinal") for module in modules]
        == list(range(1, len(modules) + 1)),
        "module ordinals must be unique, sequential, and manifest ordered",
    )
    for owner_kind in ("projects", "capstones"):
        owners = records(manifest, owner_kind)
        require(
            [owner.get("ordinal") for owner in owners]
            == list(range(1, len(owners) + 1)),
            f"{owner_kind} ordinals must be sequential",
        )
        for owner in owners:
            milestones = records(owner, "milestones", context=str(owner["id"]), required=True)
            require(
                [milestone.get("ordinal") for milestone in milestones]
                == list(range(1, len(milestones) + 1)),
                f"{owner['id']} milestone ordinals must be sequential",
            )

    course = manifest["course"]
    final_destinations = course.get("final_destinations")
    require(
        isinstance(final_destinations, list),
        "course.final_destinations must be an array",
    )
    for destination in final_destinations:
        require(isinstance(destination, dict), "final destinations must be tables")
        destination_id = destination.get("id")
        require(
            isinstance(destination_id, str)
            and destination_id in known_ids
            and destination_id.startswith("capstone."),
            f"unknown final destination {destination_id!r}",
        )
        require(destination.get("required") is True, f"{destination_id} must be required")
    return known_ids


def validate_required_outcomes(manifest: dict[str, Any]) -> None:
    for owner_kind in ("modules", "projects", "capstones"):
        for owner in records(manifest, owner_kind):
            owner_id = owner["id"]
            outcomes = owner.get("required_outcomes")
            require(
                isinstance(outcomes, list) and outcomes,
                f"{owner_id}.required_outcomes must be a non-empty list",
            )
            require(
                all(
                    isinstance(outcome, str) and outcome.strip() for outcome in outcomes
                ),
                f"{owner_id}.required_outcomes must contain non-empty strings",
            )
            normalized = [outcome.strip() for outcome in outcomes]
            require(
                len(normalized) == len(set(normalized)),
                f"{owner_id}.required_outcomes must not contain duplicates",
            )
            if owner_kind == "modules":
                validate_untouched_starter_baseline(owner, owner_id)
                continue
            for milestone in records(owner, "milestones", context=owner_id, required=True):
                outcome = milestone.get("required_outcome")
                require(
                    isinstance(outcome, str) and outcome.strip(),
                    f"{milestone['id']}.required_outcome must be a non-empty string",
                )
                validate_untouched_starter_baseline(milestone, milestone["id"])


def validate_untouched_starter_baseline(record: dict[str, Any], record_id: str) -> None:
    """Require every practice unit to declare what its untouched starter does.

    A focused command that exits zero on an untouched starter is a legitimate
    scaffold only when it cannot be mistaken for completion. Declaring the
    baseline makes that explicit, and a ``passes`` baseline additionally has to
    say why a pass is not evidence — otherwise the mentor could read a vacuous
    ``ok`` as a finished objective.
    """
    result = record.get("untouched_starter_result")
    require(
        result in {"fails", "passes"},
        f"{record_id}.untouched_starter_result must be 'fails' or 'passes'",
    )
    note = record.get("untouched_starter_note")
    if result == "fails":
        require(
            note is None,
            f"{record_id} declares a failing baseline and needs no note",
        )
        return
    require(
        isinstance(note, str) and note.strip(),
        f"{record_id} declares a passing baseline, so it must explain in "
        f"untouched_starter_note why that pass is not completion evidence",
    )
    assert isinstance(note, str)
    require(
        "not completion evidence" in note.casefold(),
        f"{record_id}.untouched_starter_note must state that the pass is not "
        f"completion evidence",
    )


def declared_paths(manifest: dict[str, Any]) -> Iterator[tuple[str, object]]:
    yield "schema_document", manifest.get("schema_document")
    course = manifest["course"]
    for key in ("learner_entry_point", "setup_guide", "command_working_directory"):
        yield f"course.{key}", course.get(key)
    for index, destination in enumerate(course.get("final_destinations", [])):
        yield f"course.final_destinations[{index}].path", destination.get("path")

    for module in records(manifest, "modules"):
        module_id = module["id"]
        for key in ("lesson_readme", "exercise_starter", "exercise_solution"):
            yield f"{module_id}.{key}", module.get(key)
        supplements = module.get("solution_supplements", [])
        require(
            isinstance(supplements, list),
            f"{module_id}.solution_supplements must be an array",
        )
        for index, path in enumerate(supplements):
            yield f"{module_id}.solution_supplements[{index}]", path
        review_questions = module.get("review_questions")
        require(
            isinstance(review_questions, dict),
            f"{module_id}.review_questions must be a table",
        )
        assert isinstance(review_questions, dict)
        yield f"{module_id}.review_questions.path", review_questions.get("path")
        for concept in records(module, "concepts", context=module_id, required=True):
            yield f"{concept['id']}.lesson_project", concept.get("lesson_project")

    for owner_kind in ("projects", "capstones"):
        for owner in records(manifest, owner_kind):
            owner_id = owner["id"]
            for key in ("guide_path", "starter_root", "solution_root", "tests_root"):
                yield f"{owner_id}.{key}", owner.get(key)
            specification_paths = owner.get("specification_paths")
            require(
                isinstance(specification_paths, list) and specification_paths,
                f"{owner_id}.specification_paths must be a non-empty array",
            )
            for index, path in enumerate(specification_paths):
                yield f"{owner_id}.specification_paths[{index}]", path

    for lock in records(manifest, "solution_lock_groups"):
        lock_paths = lock.get("paths")
        require(
            isinstance(lock_paths, list) and lock_paths,
            f"{lock.get('id', 'solution lock')}.paths must be non-empty",
        )
        for index, path in enumerate(lock_paths):
            yield f"{lock['id']}.paths[{index}]", path


def path_declaration(label: str, value: object) -> tuple[str, str]:
    if isinstance(value, str):
        require(value != "", f"{label} must not be empty")
        return value, "repository"
    require(isinstance(value, dict), f"{label} must be a path string or table")
    assert isinstance(value, dict)
    path = value.get("path")
    availability = value.get("availability")
    require(isinstance(path, str) and path, f"{label}.path must be a string")
    require(
        availability in {"repository", "command-created"},
        f"{label}.availability is unsupported",
    )
    if availability == "command-created":
        require(
            isinstance(value.get("created_by"), str) and value["created_by"],
            f"{label}.created_by must name a command",
        )
    assert isinstance(path, str) and isinstance(availability, str)
    return path, availability


def validate_paths(manifest: dict[str, Any], root: Path = REPOSITORY_ROOT) -> None:
    resolved_root = root.resolve()
    for label, value in declared_paths(manifest):
        raw_path, availability = path_declaration(label, value)
        relative_path = Path(raw_path)
        require(not relative_path.is_absolute(), f"{label} must be repository relative")
        resolved_path = (resolved_root / relative_path).resolve()
        try:
            resolved_path.relative_to(resolved_root)
        except ValueError as error:
            raise ManifestValidationError(
                f"{label} escapes the repository root: {raw_path}"
            ) from error
        if availability == "repository":
            require(resolved_path.exists(), f"{label} does not exist: {raw_path}")

    for module in records(manifest, "modules"):
        review_questions = module["review_questions"]
        heading = review_questions.get("heading")
        require(
            isinstance(heading, str) and heading.strip(),
            f"{module['id']} review heading must be named",
        )
        review_path, _ = path_declaration(
            f"{module['id']}.review_questions.path", review_questions["path"]
        )
        review_text = (root / review_path).read_text(encoding="utf-8")
        require(
            any(
                line.startswith("#") and line.lstrip("#").strip() == heading
                for line in review_text.splitlines()
            ),
            f"{module['id']} review heading is missing from {review_path}",
        )
        # A lesson companion is a .NET project directory, not a single script.
        for concept in records(module, "concepts", context=module["id"], required=True):
            project_path = root / concept["lesson_project"]
            require(
                project_path.is_dir(),
                f"{concept['id']}.lesson_project must be a directory",
            )
            require(
                any(project_path.glob("*.csproj")),
                f"{concept['id']}.lesson_project contains no .csproj",
            )


def validate_solution_locks(manifest: dict[str, Any]) -> dict[str, dict[str, Any]]:
    lock_records = records(manifest, "solution_lock_groups")
    lock_ids = [lock.get("id") for lock in lock_records]
    require(
        all(
            isinstance(lock_id, str)
            and lock_id.startswith("solutions.")
            and ID_PATTERN.fullmatch(lock_id) is not None
            for lock_id in lock_ids
        ),
        "solution lock IDs are invalid",
    )
    duplicates = sorted(
        str(lock_id) for lock_id, count in Counter(lock_ids).items() if count > 1
    )
    require(not duplicates, f"duplicate solution lock IDs: {', '.join(duplicates)}")
    locks = {lock["id"]: lock for lock in lock_records}

    owners: list[tuple[dict[str, Any], list[str], str]] = [
        (
            module,
            [module["exercise_solution"], *module.get("solution_supplements", [])],
            "after-unit-validation",
        )
        for module in records(manifest, "modules")
    ]
    for owner_kind in ("projects", "capstones"):
        owners.extend(
            (owner, [owner["solution_root"]], "after-matching-milestone-validation")
            for owner in records(manifest, owner_kind)
        )

    referenced_locks: list[str] = []
    locked_paths: list[str] = []
    for owner, expected_paths, expected_policy in owners:
        owner_id = owner["id"]
        lock_id = owner.get("solution_lock_group")
        require(
            isinstance(lock_id, str) and lock_id in locks,
            f"{owner_id} references unknown solution lock {lock_id!r}",
        )
        assert isinstance(lock_id, str)
        referenced_locks.append(lock_id)
        lock = locks[lock_id]
        lock_paths = [
            path_declaration(f"{lock_id}.paths", path)[0] for path in lock["paths"]
        ]
        require(
            lock_paths == expected_paths,
            f"{lock_id} must lock exactly {', '.join(expected_paths)}",
        )
        locked_paths.extend(lock_paths)
        require(
            lock.get("unlock_policy") == expected_policy,
            f"{lock_id} has an invalid unlock_policy",
        )
        unlock_after = lock.get("solution_unlock_after")
        require(
            type(unlock_after) is int and 1 <= unlock_after <= 100,
            f"{lock_id} solution_unlock_after must be between 1 and 100",
        )

    require(
        len(referenced_locks) == len(set(referenced_locks)),
        "solution lock groups must not be shared by owners",
    )
    require(
        set(referenced_locks) == set(locks),
        "solution lock groups must be referenced exactly once",
    )
    require(
        len(locked_paths) == len(set(locked_paths)),
        "solution paths must belong to one lock group",
    )
    # A lock must never hide a learner-facing starter tree or lesson narrative.
    for owner_kind in ("projects", "capstones"):
        for owner in records(manifest, owner_kind):
            starter_root = owner["starter_root"]
            require(
                all(
                    not starter_root.startswith(f"{path}/") and path != starter_root
                    for path in locked_paths
                ),
                f"{owner['id']} starter tree must never be locked",
            )
    for module in records(manifest, "modules"):
        readme = module["lesson_readme"]
        require(
            all(not readme.startswith(f"{path}/") and path != readme for path in locked_paths),
            f"{module['id']} lesson narrative must never be locked",
        )
    return locks


def validate_command_text(label: str, command: object) -> str:
    require(isinstance(command, str), f"{label} must be a string")
    assert isinstance(command, str)
    require(command == command.strip() and command, f"{label} must not be blank")
    require(
        not any(character in command for character in ("\n", "\r", "\0")),
        f"{label} must be one line",
    )
    require(
        not any(token in command for token in ("&&", "||", "|", ";", ">", "<", "$(", "`")),
        f"{label} must not contain shell operators",
    )
    try:
        tokens = shlex.split(command)
    except ValueError as error:
        raise ManifestValidationError(f"{label} is not valid shell syntax") from error
    require(bool(tokens) and tokens[0] == "dotnet", f"{label} must invoke the dotnet CLI")
    return command


def validate_commands_and_selectors(
    manifest: dict[str, Any],
    root: Path = REPOSITORY_ROOT,
) -> None:
    skill_text = (root / SKILL_RELATIVE_PATH).read_text(encoding="utf-8")

    for module in records(manifest, "modules"):
        module_id = module["id"]
        commands = module.get("validation_commands")
        require(
            isinstance(commands, list) and commands,
            f"{module_id}.validation_commands must be a non-empty array",
        )
        for index, command in enumerate(commands):
            validate_command_text(f"{module_id}.validation_commands[{index}]", command)
            require(
                f"`{command}`" in skill_text,
                f"{module_id} validation command is not documented in SKILL.md",
            )
        for concept in records(module, "concepts", context=module_id, required=True):
            command = validate_command_text(
                f"{concept['id']}.run_command", concept.get("run_command")
            )
            require(
                f"./{concept['lesson_project']}" in shlex.split(command)
                or concept["lesson_project"] in shlex.split(command),
                f"{concept['id']} run command must target its lesson project",
            )

    for owner_kind in ("projects", "capstones"):
        for owner in records(manifest, owner_kind):
            owner_id = owner["id"]
            selector = owner.get("implementation_selector")
            require(
                isinstance(selector, dict),
                f"{owner_id}.implementation_selector must be a table",
            )
            assert isinstance(selector, dict)
            require(
                selector.get("kind") == SUPPORTED_SELECTOR_KIND,
                f"{owner_id} selector kind must be {SUPPORTED_SELECTOR_KIND}",
            )
            learner_value = selector.get("learner_value")
            reference_value = selector.get("reference_value")
            require(
                (learner_value, reference_value) == ("starter", "solution"),
                f"{owner_id} selector values are unsupported",
            )
            require(
                owner["starter_root"].endswith(f"/{learner_value}")
                and owner["solution_root"].endswith(f"/{reference_value}"),
                f"{owner_id} selector values do not match implementation roots",
            )

            starter_root = owner["starter_root"]
            validation_commands = owner.get("validation_commands")
            require(
                isinstance(validation_commands, list) and validation_commands,
                f"{owner_id}.validation_commands must be non-empty",
            )
            for index, command in enumerate(validation_commands):
                validate_command_text(f"{owner_id}.validation_commands[{index}]", command)
            require(
                any(
                    any(starter_root in token for token in shlex.split(command))
                    for command in validation_commands
                ),
                f"{owner_id} must validate the learner starter tree",
            )

            for milestone in records(owner, "milestones", context=owner_id, required=True):
                command = validate_command_text(
                    f"{milestone['id']}.test_command", milestone.get("test_command")
                )
                require(
                    any(starter_root in token for token in shlex.split(command)),
                    f"{milestone['id']} must target the learner starter tree",
                )
                require(
                    f"`{command}`" in skill_text,
                    f"{milestone['id']} command is not documented in SKILL.md",
                )


def unique(values: list[str]) -> list[str]:
    return list(dict.fromkeys(values))


def flatten_state_projection(manifest: dict[str, Any]) -> dict[str, object]:
    locks = validate_solution_locks(manifest)
    nodes: list[dict[str, Any]] = []

    def add_node(
        record: dict[str, Any],
        *,
        inherited_prerequisites: list[str] | None = None,
        lock_id: str,
    ) -> None:
        inherited = [] if inherited_prerequisites is None else inherited_prerequisites
        node_prerequisites = unique([*inherited, *prerequisites(record, record["id"])])
        nodes.append(
            {
                "id": record["id"],
                "title": record["title"],
                "prerequisites": node_prerequisites,
                "solution_unlock_after": locks[lock_id]["solution_unlock_after"],
                "_rank": len(nodes),
            }
        )

    for module in sorted(records(manifest, "modules"), key=lambda item: item["ordinal"]):
        inherited = prerequisites(module, module["id"])
        for concept in sorted(
            records(module, "concepts", context=module["id"], required=True),
            key=lambda item: item["id"],
        ):
            add_node(
                concept,
                inherited_prerequisites=inherited,
                lock_id=module["solution_lock_group"],
            )
        add_node(module, lock_id=module["solution_lock_group"])

    for owner_kind in ("projects", "capstones"):
        for owner in sorted(
            records(manifest, owner_kind),
            key=lambda item: (item["ordinal"], item["id"]),
        ):
            inherited = prerequisites(owner, owner["id"])
            for milestone in sorted(
                records(owner, "milestones", context=owner["id"], required=True),
                key=lambda item: (item["ordinal"], item["id"]),
            ):
                add_node(
                    milestone,
                    inherited_prerequisites=inherited,
                    lock_id=owner["solution_lock_group"],
                )
            add_node(owner, lock_id=owner["solution_lock_group"])

    by_id = {node["id"]: node for node in nodes}
    require(len(by_id) == len(nodes), "projection contains duplicate trackable IDs")
    dependents: dict[str, list[str]] = {record_id: [] for record_id in by_id}
    indegrees: dict[str, int] = {}
    for node in nodes:
        node_id = node["id"]
        indegrees[node_id] = len(node["prerequisites"])
        for prerequisite in node["prerequisites"]:
            require(
                prerequisite in by_id,
                f"{node_id} has unknown prerequisite {prerequisite}",
            )
            dependents[prerequisite].append(node_id)

    ready = [(node["_rank"], node["id"]) for node in nodes if indegrees[node["id"]] == 0]
    heapq.heapify(ready)
    ordered_ids: list[str] = []
    while ready:
        _, node_id = heapq.heappop(ready)
        ordered_ids.append(node_id)
        for dependent in dependents[node_id]:
            indegrees[dependent] -= 1
            if indegrees[dependent] == 0:
                heapq.heappush(ready, (by_id[dependent]["_rank"], dependent))

    if len(ordered_ids) != len(nodes):
        cycle_ids = sorted(node_id for node_id, degree in indegrees.items() if degree > 0)
        raise ManifestValidationError(
            f"prerequisite cycle detected involving: {', '.join(cycle_ids)}"
        )

    projected = [
        {
            "id": node_id,
            "title": by_id[node_id]["title"],
            "order": index * 10,
            "prerequisites": by_id[node_id]["prerequisites"],
            "solution_unlock_after": by_id[node_id]["solution_unlock_after"],
        }
        for index, node_id in enumerate(ordered_ids, start=1)
    ]
    return {"concepts": projected}


def validate_manifest(
    manifest: dict[str, Any],
    root: Path = REPOSITORY_ROOT,
) -> dict[str, object]:
    try:
        validate_versions(manifest)
        validate_dotnet_support_claim(root)
        validate_trackable_ids_and_graph(manifest)
        validate_required_outcomes(manifest)
        validate_paths(manifest, root)
        validate_solution_locks(manifest)
        validate_commands_and_selectors(manifest, root)
        projection = flatten_state_projection(manifest)
        # Checked last: alignment with the curriculum plan is only meaningful once
        # the manifest is internally sound, and its diagnostics must not mask a
        # structural defect such as a cycle or an unknown prerequisite.
        validate_curriculum_plan(manifest, root)
        return projection
    except ManifestValidationError:
        raise
    except (KeyError, TypeError) as error:
        raise ManifestValidationError(
            f"missing or invalid required field: {error}"
        ) from error


def compact_json(payload: object) -> str:
    return json.dumps(payload, sort_keys=True, separators=(",", ":"))


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="course-adapter")

    def add_source_options(target: argparse.ArgumentParser) -> None:
        target.add_argument(
            "--manifest",
            type=Path,
            default=argparse.SUPPRESS,
            help="course manifest path",
        )
        target.add_argument(
            "--repository-root",
            type=Path,
            default=argparse.SUPPRESS,
            help="repository root used to resolve manifest paths",
        )

    add_source_options(parser)
    subparsers = parser.add_subparsers(dest="command", required=True)
    for name, help_text in (
        ("validate", "validate the course manifest"),
        ("state-projection", "emit state-helper concepts JSON"),
    ):
        add_source_options(subparsers.add_parser(name, help=help_text))
    return parser


def run(arguments: argparse.Namespace, *, stdout: TextIO) -> None:
    manifest_path = getattr(arguments, "manifest", MANIFEST_PATH)
    repository_root = getattr(arguments, "repository_root", REPOSITORY_ROOT)
    manifest = load_manifest(manifest_path)
    projection = validate_manifest(manifest, repository_root)
    if arguments.command == "validate":
        payload: object = {
            "adapter_protocol": SUPPORTED_ADAPTER_PROTOCOL,
            "manifest_version": manifest["manifest_version"],
            "schema_version": manifest["schema_version"],
            "status": "valid",
            "trackable_count": len(projection["concepts"]),
        }
    else:
        payload = projection
    stdout.write(f"{compact_json(payload)}\n")


def main(
    argv: Sequence[str] | None = None,
    *,
    stdout: TextIO | None = None,
    stderr: TextIO | None = None,
) -> int:
    output = sys.stdout if stdout is None else stdout
    errors = sys.stderr if stderr is None else stderr
    arguments = build_parser().parse_args(argv)
    try:
        run(arguments, stdout=output)
    except tomllib.TOMLDecodeError as error:
        print(f"course-adapter: invalid TOML: {error}", file=errors)
        return EXIT_INVALID_MANIFEST
    except ManifestValidationError as error:
        print(f"course-adapter: invalid manifest: {error}", file=errors)
        return EXIT_INVALID_MANIFEST
    except OSError as error:
        print(f"course-adapter: I/O error: {error}", file=errors)
        return EXIT_IO
    return EXIT_OK


if __name__ == "__main__":
    raise SystemExit(main())
