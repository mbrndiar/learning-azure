"""Tests for the Azure learning-path adapter.

These tests exist to prove the adapter *fails closed*. A mentor that records
progress against an invalid manifest would attach learner evidence to objectives
that do not exist, so every structural defect below must produce a nonzero exit
and no success-shaped JSON.

At the bootstrap baseline the committed ``course.toml`` has no curriculum content,
so the content rules are exercised against an on-disk fixture course built here.
The fixture lets us prove the graph, path, command, selector, and solution-lock
checks reject faults today, before any real lesson exists. The fixture is written
under the repository (never a shared temp directory) and removed on teardown.
"""

from __future__ import annotations

import copy
import io
import json
import shutil
import sys
import tempfile
import tomllib
import unittest
from pathlib import Path
from typing import Any

SKILL_DIR = Path(__file__).resolve().parents[1]
REPOSITORY_ROOT = SKILL_DIR.parents[2]
sys.path.insert(0, str(SKILL_DIR / "scripts"))

import course_adapter  # noqa: E402

_FIXTURE_BASE = SKILL_DIR / "tests" / ".fixtures"
_FIXTURE_ROOT: Path


def _write(root: Path, relative: str, content: str) -> None:
    target = root / relative
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(content, encoding="utf-8")


def _write_fixture_tree(root: Path) -> None:
    """Write the minimal file tree the fixture manifest references."""
    _write(root, "global.json", '{"sdk":{"version":"10.0.100","rollForward":"latestFeature"}}\n')
    _write(
        root,
        "Directory.Build.props",
        "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework>"
        "</PropertyGroup></Project>\n",
    )
    _write(root, "README.md", "# Fixture course\n")
    _write(root, "docs/SETUP.md", "# Setup\n")
    _write(
        root,
        ".agents/skills/azure-learning-path/references/course-schema.md",
        "# Schema\n",
    )
    # SKILL.md must document every module validation command and milestone command.
    _write(
        root,
        ".agents/skills/azure-learning-path/SKILL.md",
        "# Skill\n\n"
        "`dotnet test exercises/01-azure-data-map`\n\n"
        "`dotnet test exercises/02-sdk-foundations`\n\n"
        "`dotnet test projects/field-station/starter/tests/Domain`\n\n"
        "`dotnet test capstones/cloud-expedition-journal/starter/tests/Domain`\n",
    )

    for ordinal, slug, concept_dir in (
        (1, "01-azure-data-map", "primitives"),
        (2, "02-sdk-foundations", "client"),
    ):
        _write(
            root,
            f"lessons/{slug}/README.md",
            f"# Module {ordinal}\n\n## Review questions\n\n1. Why?\n",
        )
        _write(
            root,
            f"lessons/{slug}/{concept_dir}/{concept_dir.title()}.csproj",
            '<Project Sdk="Microsoft.NET.Sdk" />\n',
        )
        _write(root, f"exercises/{slug}/starter/Exercise.cs", "// starter\n")
        _write(root, f"exercises/{slug}/solution/Solution.cs", "// solution\n")

    _write(root, "docs/architecture/curriculum.json", json.dumps(_FIXTURE_PLAN, indent=2))

    for kind, slug in (
        ("projects", "field-station"),
        ("capstones", "cloud-expedition-journal"),
    ):
        _write(root, f"{kind}/{slug}/README.md", "# Applied\n")
        for sub in ("starter", "solution", "tests"):
            (root / kind / slug / sub).mkdir(parents=True, exist_ok=True)
            _write(root, f"{kind}/{slug}/{sub}/.keep", "")


# The curriculum plan the fixture manifest must stay a subset of. Only the fields
# the adapter reads are modelled here; tools/CourseVerifier owns the full schema.
_FIXTURE_PLAN: dict[str, Any] = {
    "course_id": "learning-azure",
    "units": [
        {
            "id": "module.azure-data-map",
            "artifact_status": "present",
            "prerequisites": [],
        },
        {
            "id": "module.sdk-foundations",
            "artifact_status": "present",
            "prerequisites": ["module.azure-data-map"],
        },
        {
            "id": "project.field-station",
            "artifact_status": "present",
            "prerequisites": ["module.sdk-foundations"],
            "milestones": [
                {"id": "milestone.field-station.domain", "prerequisites": []}
            ],
        },
        {
            "id": "capstone.cloud-expedition-journal",
            "artifact_status": "present",
            "prerequisites": ["project.field-station"],
            "milestones": [
                {"id": "milestone.cloud-expedition-journal.domain", "prerequisites": []}
            ],
        },
    ],
}


def _valid_manifest() -> dict[str, Any]:
    """A content-bearing manifest that references the fixture tree."""
    return copy.deepcopy(_MANIFEST_TEMPLATE)


_MANIFEST_TEMPLATE: dict[str, Any] = {
    "manifest_version": 1,
    "schema_version": "1.0.0",
    "schema_document": ".agents/skills/azure-learning-path/references/course-schema.md",
    "curriculum_plan": "docs/architecture/curriculum.json",
    "course": {
        "id": "learning-azure",
        "version": "1.0.0",
        "title": "Learning Azure",
        "description": "Fixture.",
        "language": "csharp",
        "target_framework": "net10.0",
        "dotnet_sdk_minimum": "10.0",
        "learner_entry_point": "README.md",
        "setup_guide": "docs/SETUP.md",
        "command_working_directory": ".",
        "final_destinations": [
            {
                "id": "capstone.cloud-expedition-journal",
                "path": "capstones/cloud-expedition-journal/README.md",
                "required": True,
            }
        ],
    },
    "modules": [
        {
            "id": "module.azure-data-map",
            "ordinal": 1,
            "title": "Choose the right Azure data primitive",
            "prerequisites": [],
            "lesson_readme": "lessons/01-azure-data-map/README.md",
            "review_questions": {
                "path": "lessons/01-azure-data-map/README.md",
                "heading": "Review questions",
            },
            "exercise_starter": "exercises/01-azure-data-map/starter/Exercise.cs",
            "exercise_solution": "exercises/01-azure-data-map/solution",
            "validation_commands": ["dotnet test exercises/01-azure-data-map"],
            "untouched_starter_result": "fails",
            "solution_lock_group": "solutions.module.azure-data-map",
            "required_outcomes": ["Choose a primitive.", "Justify the choice."],
            "concepts": [
                {
                    "id": "concept.azure-data-map.primitives",
                    "title": "Storage, streams, and documents",
                    "prerequisites": [],
                    "lesson_project": "lessons/01-azure-data-map/primitives",
                    "run_command": "dotnet run --project lessons/01-azure-data-map/primitives",
                }
            ],
        },
        {
            "id": "module.sdk-foundations",
            "ordinal": 2,
            "title": "Build a testable C# Azure client",
            "prerequisites": ["module.azure-data-map"],
            "lesson_readme": "lessons/02-sdk-foundations/README.md",
            "review_questions": {
                "path": "lessons/02-sdk-foundations/README.md",
                "heading": "Review questions",
            },
            "exercise_starter": "exercises/02-sdk-foundations/starter/Exercise.cs",
            "exercise_solution": "exercises/02-sdk-foundations/solution",
            "validation_commands": ["dotnet test exercises/02-sdk-foundations"],
            "untouched_starter_result": "fails",
            "solution_lock_group": "solutions.module.sdk-foundations",
            "required_outcomes": ["Create a client.", "Inject seams."],
            "concepts": [
                {
                    "id": "concept.sdk-foundations.client",
                    "title": "Client conventions and cancellation",
                    "prerequisites": [],
                    "lesson_project": "lessons/02-sdk-foundations/client",
                    "run_command": "dotnet run --project lessons/02-sdk-foundations/client",
                }
            ],
        },
    ],
    "projects": [
        {
            "id": "project.field-station",
            "ordinal": 1,
            "title": "Applied Storage field station",
            "prerequisites": ["module.sdk-foundations"],
            "guide_path": "projects/field-station/README.md",
            "specification_paths": ["projects/field-station/README.md"],
            "starter_root": "projects/field-station/starter",
            "solution_root": "projects/field-station/solution",
            "tests_root": "projects/field-station/tests",
            "implementation_selector": {
                "kind": "project-path",
                "learner_value": "starter",
                "reference_value": "solution",
            },
            "validation_commands": ["dotnet test projects/field-station/starter"],
            "solution_lock_group": "solutions.project.field-station",
            "required_outcomes": ["Integrate Storage."],
            "milestones": [
                {
                    "id": "milestone.field-station.domain",
                    "ordinal": 1,
                    "title": "Domain and ports",
                    "prerequisites": [],
                    "required_outcome": "Define the ports.",
                    "test_command": "dotnet test projects/field-station/starter/tests/Domain",
                    "untouched_starter_result": "fails",
                }
            ],
        }
    ],
    "capstones": [
        {
            "id": "capstone.cloud-expedition-journal",
            "ordinal": 1,
            "title": "Cloud Expedition Field Journal",
            "prerequisites": ["project.field-station"],
            "guide_path": "capstones/cloud-expedition-journal/README.md",
            "specification_paths": ["capstones/cloud-expedition-journal/README.md"],
            "starter_root": "capstones/cloud-expedition-journal/starter",
            "solution_root": "capstones/cloud-expedition-journal/solution",
            "tests_root": "capstones/cloud-expedition-journal/tests",
            "implementation_selector": {
                "kind": "project-path",
                "learner_value": "starter",
                "reference_value": "solution",
            },
            "validation_commands": [
                "dotnet test capstones/cloud-expedition-journal/starter"
            ],
            "solution_lock_group": "solutions.capstone.cloud-expedition-journal",
            "required_outcomes": ["Deliver the journal."],
            "milestones": [
                {
                    "id": "milestone.cloud-expedition-journal.domain",
                    "ordinal": 1,
                    "title": "Domain and ports",
                    "prerequisites": [],
                    "required_outcome": "Model the journal.",
                    "test_command": "dotnet test capstones/cloud-expedition-journal/starter/tests/Domain",
                    "untouched_starter_result": "passes",
                    "untouched_starter_note": "Only placeholder checks run; a pass is not completion evidence.",
                }
            ],
        }
    ],
    "solution_lock_groups": [
        {
            "id": "solutions.module.azure-data-map",
            "paths": ["exercises/01-azure-data-map/solution"],
            "unlock_policy": "after-unit-validation",
            "solution_unlock_after": 1,
        },
        {
            "id": "solutions.module.sdk-foundations",
            "paths": ["exercises/02-sdk-foundations/solution"],
            "unlock_policy": "after-unit-validation",
            "solution_unlock_after": 1,
        },
        {
            "id": "solutions.project.field-station",
            "paths": ["projects/field-station/solution"],
            "unlock_policy": "after-matching-milestone-validation",
            "solution_unlock_after": 2,
        },
        {
            "id": "solutions.capstone.cloud-expedition-journal",
            "paths": ["capstones/cloud-expedition-journal/solution"],
            "unlock_policy": "after-matching-milestone-validation",
            "solution_unlock_after": 2,
        },
    ],
}


def setUpModule() -> None:
    global _FIXTURE_ROOT
    _FIXTURE_BASE.mkdir(parents=True, exist_ok=True)
    _FIXTURE_ROOT = Path(tempfile.mkdtemp(dir=_FIXTURE_BASE))
    _write_fixture_tree(_FIXTURE_ROOT)


def tearDownModule() -> None:
    shutil.rmtree(_FIXTURE_ROOT, ignore_errors=True)
    if _FIXTURE_BASE.exists() and not any(_FIXTURE_BASE.iterdir()):
        _FIXTURE_BASE.rmdir()


class CommittedManifestTests(unittest.TestCase):
    """The committed manifest must validate against the real repository."""

    def test_committed_manifest_validates_against_the_repository(self) -> None:
        manifest = course_adapter.load_manifest(SKILL_DIR / "course.toml")
        projection = course_adapter.validate_manifest(manifest, REPOSITORY_ROOT)
        self.assertEqual(set(projection), {"concepts"})
        ids = [node["id"] for node in projection["concepts"]]
        self.assertEqual(len(ids), len(set(ids)))

    def test_committed_projection_is_a_topological_order(self) -> None:
        manifest = course_adapter.load_manifest(SKILL_DIR / "course.toml")
        projection = course_adapter.validate_manifest(manifest, REPOSITORY_ROOT)
        seen: set[str] = set()
        for node in projection["concepts"]:
            for prerequisite in node["prerequisites"]:
                self.assertIn(
                    prerequisite, seen, f"{node['id']} precedes {prerequisite}"
                )
            seen.add(node["id"])

    def test_declared_dotnet_support_matches_build_config(self) -> None:
        global_json = json.loads(
            (REPOSITORY_ROOT / "global.json").read_text(encoding="utf-8")
        )
        self.assertTrue(
            global_json["sdk"]["version"].startswith(
                f"{course_adapter.DOTNET_SDK_MINIMUM}."
            )
        )
        build_props = (
            REPOSITORY_ROOT / "Directory.Build.props"
        ).read_text(encoding="utf-8")
        self.assertIn(
            f"<TargetFramework>{course_adapter.TARGET_FRAMEWORK}</TargetFramework>",
            build_props,
        )


class FixtureManifestTests(unittest.TestCase):
    """A content-bearing fixture must validate and project deterministically."""

    def setUp(self) -> None:
        self.manifest = _valid_manifest()

    def test_fixture_manifest_validates(self) -> None:
        projection = course_adapter.validate_manifest(self.manifest, _FIXTURE_ROOT)
        ids = [node["id"] for node in projection["concepts"]]
        self.assertEqual(len(ids), 8)
        self.assertEqual(len(ids), len(set(ids)))

    def test_projection_is_a_topological_order(self) -> None:
        projection = course_adapter.validate_manifest(self.manifest, _FIXTURE_ROOT)
        seen: set[str] = set()
        for node in projection["concepts"]:
            for prerequisite in node["prerequisites"]:
                self.assertIn(prerequisite, seen, f"{node['id']} precedes {prerequisite}")
            seen.add(node["id"])

    def test_projection_is_deterministic(self) -> None:
        first = course_adapter.validate_manifest(_valid_manifest(), _FIXTURE_ROOT)
        second = course_adapter.validate_manifest(_valid_manifest(), _FIXTURE_ROOT)
        self.assertEqual(
            course_adapter.compact_json(first), course_adapter.compact_json(second)
        )

    def test_every_projected_node_has_a_positive_unlock_threshold(self) -> None:
        projection = course_adapter.validate_manifest(self.manifest, _FIXTURE_ROOT)
        for node in projection["concepts"]:
            self.assertGreaterEqual(node["solution_unlock_after"], 1)

    def test_locks_never_hide_a_starter_tree_or_narrative(self) -> None:
        locked = {
            path
            for lock in self.manifest["solution_lock_groups"]
            for path in lock["paths"]
        }
        for owner_kind in ("projects", "capstones"):
            for owner in self.manifest[owner_kind]:
                self.assertNotIn(owner["starter_root"], locked)
        for module in self.manifest["modules"]:
            self.assertNotIn(module["lesson_readme"], locked)

    def test_documented_commands_appear_in_the_fixture_skill(self) -> None:
        skill = (
            _FIXTURE_ROOT / course_adapter.SKILL_RELATIVE_PATH
        ).read_text(encoding="utf-8")
        for module in self.manifest["modules"]:
            self.assertIn(f"`{module['validation_commands'][0]}`", skill)
        for owner_kind in ("projects", "capstones"):
            for owner in self.manifest[owner_kind]:
                for milestone in owner["milestones"]:
                    self.assertIn(f"`{milestone['test_command']}`", skill)


class FailClosedTests(unittest.TestCase):
    """Each mutation below must be rejected before any state could be written."""

    def setUp(self) -> None:
        self.manifest = _valid_manifest()

    def assert_rejected(self, manifest: dict[str, Any], fragment: str) -> None:
        with self.assertRaises(course_adapter.ManifestValidationError) as caught:
            course_adapter.validate_manifest(manifest, _FIXTURE_ROOT)
        self.assertIn(fragment, str(caught.exception))

    def test_unsupported_schema_version_is_rejected(self) -> None:
        self.manifest["schema_version"] = "9.9.9"
        self.assert_rejected(self.manifest, "unsupported schema_version")

    def test_unsupported_manifest_version_is_rejected(self) -> None:
        self.manifest["manifest_version"] = 2
        self.assert_rejected(self.manifest, "unsupported manifest_version")

    def test_wrong_language_is_rejected(self) -> None:
        self.manifest["course"]["language"] = "fsharp"
        self.assert_rejected(self.manifest, "course.language must be csharp")

    def test_wrong_target_framework_is_rejected(self) -> None:
        self.manifest["course"]["target_framework"] = "net9.0"
        self.assert_rejected(self.manifest, "course.target_framework must be net10.0")

    def test_duplicate_ids_are_rejected(self) -> None:
        modules = self.manifest["modules"]
        modules[1]["id"] = modules[0]["id"]
        modules[1]["prerequisites"] = []
        self.assert_rejected(self.manifest, "duplicate trackable IDs")

    def test_unknown_prerequisite_is_rejected(self) -> None:
        self.manifest["modules"][1]["prerequisites"] = ["module.does-not-exist"]
        self.assert_rejected(self.manifest, "unknown prerequisite")

    def test_prerequisite_cycle_is_rejected(self) -> None:
        modules = self.manifest["modules"]
        modules[0]["prerequisites"] = [modules[1]["id"]]
        modules[1]["prerequisites"] = [modules[0]["id"]]
        self.assert_rejected(self.manifest, "cycle")

    def test_id_embedding_a_commit_hash_is_rejected(self) -> None:
        self.manifest["modules"][0]["id"] = "module.map-9fde6a4"
        self.assert_rejected(self.manifest, "must not embed a commit hash")

    def test_missing_repository_path_is_rejected(self) -> None:
        self.manifest["modules"][0]["lesson_readme"] = "lessons/missing/README.md"
        self.assert_rejected(self.manifest, "does not exist")

    def test_path_escaping_the_repository_is_rejected(self) -> None:
        self.manifest["modules"][0]["lesson_readme"] = "../evil/README.md"
        self.assert_rejected(self.manifest, "escapes the repository root")

    def test_missing_review_heading_is_rejected(self) -> None:
        self.manifest["modules"][0]["review_questions"]["heading"] = "No such heading"
        self.assert_rejected(self.manifest, "review heading is missing")

    def test_command_with_a_shell_operator_is_rejected(self) -> None:
        self.manifest["modules"][0]["validation_commands"] = [
            "dotnet test exercises/01-azure-data-map && rm -rf /"
        ]
        self.assert_rejected(self.manifest, "must not contain shell operators")

    def test_command_not_using_dotnet_is_rejected(self) -> None:
        self.manifest["modules"][0]["concepts"][0]["run_command"] = (
            "python3 lessons/01-azure-data-map/primitives"
        )
        self.assert_rejected(self.manifest, "must invoke the dotnet CLI")

    def test_concept_command_targeting_another_project_is_rejected(self) -> None:
        self.manifest["modules"][0]["concepts"][0]["run_command"] = (
            "dotnet run --project lessons/02-sdk-foundations/client"
        )
        self.assert_rejected(self.manifest, "must target its lesson project")

    def test_milestone_command_targeting_the_reference_is_rejected(self) -> None:
        self.manifest["projects"][0]["milestones"][0]["test_command"] = (
            "dotnet test projects/field-station/solution/tests/Domain"
        )
        self.assert_rejected(self.manifest, "must target the learner starter tree")

    def test_unsupported_selector_kind_is_rejected(self) -> None:
        self.manifest["projects"][0]["implementation_selector"]["kind"] = "environment"
        self.assert_rejected(self.manifest, "selector kind must be")

    def test_lock_group_covering_a_starter_tree_is_rejected(self) -> None:
        project = self.manifest["projects"][0]
        for lock in self.manifest["solution_lock_groups"]:
            if lock["id"] == project["solution_lock_group"]:
                lock["paths"] = [project["starter_root"]]
        self.assert_rejected(self.manifest, "must lock exactly")

    def test_shared_lock_group_is_rejected(self) -> None:
        modules = self.manifest["modules"]
        modules[1]["solution_lock_group"] = modules[0]["solution_lock_group"]
        self.assert_rejected(self.manifest, "must lock exactly")

    def test_out_of_range_unlock_threshold_is_rejected(self) -> None:
        self.manifest["solution_lock_groups"][0]["solution_unlock_after"] = 0
        self.assert_rejected(self.manifest, "solution_unlock_after must be between")

    def test_empty_required_outcomes_are_rejected(self) -> None:
        self.manifest["modules"][0]["required_outcomes"] = []
        self.assert_rejected(self.manifest, "required_outcomes must be a non-empty list")

    def test_non_capstone_final_destination_is_rejected(self) -> None:
        self.manifest["course"]["final_destinations"][0]["id"] = "module.azure-data-map"
        self.assert_rejected(self.manifest, "unknown final destination")

    def test_missing_untouched_starter_baseline_is_rejected(self) -> None:
        del self.manifest["modules"][0]["untouched_starter_result"]
        self.assert_rejected(self.manifest, "untouched_starter_result must be")

    def test_missing_milestone_baseline_is_rejected(self) -> None:
        del self.manifest["projects"][0]["milestones"][0]["untouched_starter_result"]
        self.assert_rejected(self.manifest, "untouched_starter_result must be")

    def test_passing_baseline_without_a_note_is_rejected(self) -> None:
        milestone = self.manifest["projects"][0]["milestones"][0]
        milestone["untouched_starter_result"] = "passes"
        milestone.pop("untouched_starter_note", None)
        self.assert_rejected(self.manifest, "must explain in")

    def test_passing_baseline_note_must_disclaim_completion(self) -> None:
        milestone = self.manifest["projects"][0]["milestones"][0]
        milestone["untouched_starter_result"] = "passes"
        milestone["untouched_starter_note"] = "The starter passes."
        self.assert_rejected(self.manifest, "not completion evidence")


class CurriculumPlanAlignmentTests(unittest.TestCase):
    """The manifest must be a strict subset of the reviewed curriculum plan."""

    def setUp(self) -> None:
        self.manifest = _valid_manifest()

    def assert_rejected(self, manifest: dict[str, Any], fragment: str) -> None:
        with self.assertRaises(course_adapter.ManifestValidationError) as caught:
            course_adapter.validate_manifest(manifest, _FIXTURE_ROOT)
        self.assertIn(fragment, str(caught.exception))

    def test_missing_curriculum_plan_declaration_is_rejected(self) -> None:
        del self.manifest["curriculum_plan"]
        self.assert_rejected(self.manifest, "curriculum_plan must name")

    def test_nonexistent_curriculum_plan_is_rejected(self) -> None:
        self.manifest["curriculum_plan"] = "docs/architecture/nowhere.json"
        self.assert_rejected(self.manifest, "curriculum_plan does not exist")

    def test_unit_absent_from_the_plan_is_rejected(self) -> None:
        self.manifest["modules"][0]["id"] = "module.invented"
        self.manifest["modules"][0]["solution_lock_group"] = "solutions.module.invented"
        self.manifest["solution_lock_groups"][0]["id"] = "solutions.module.invented"
        self.manifest["modules"][1]["prerequisites"] = ["module.invented"]
        self.assert_rejected(self.manifest, "not declared in the curriculum plan")

    def test_prerequisites_disagreeing_with_the_plan_are_rejected(self) -> None:
        self.manifest["modules"][1]["prerequisites"] = []
        self.assert_rejected(self.manifest, "prerequisites disagree with the curriculum plan")

    def test_milestone_absent_from_the_plan_is_rejected(self) -> None:
        self.manifest["projects"][0]["milestones"][0]["id"] = "milestone.field-station.invented"
        self.assert_rejected(self.manifest, "not declared by project.field-station")

    def test_registering_a_unit_the_plan_calls_planned_is_rejected(self) -> None:
        plan_path = _FIXTURE_ROOT / "docs/architecture/curriculum.json"
        original = plan_path.read_text(encoding="utf-8")
        plan = json.loads(original)
        plan["units"][0]["artifact_status"] = "planned"
        plan_path.write_text(json.dumps(plan, indent=2), encoding="utf-8")
        try:
            self.assert_rejected(
                self.manifest,
                "still marks its artifacts as planned",
            )
        finally:
            plan_path.write_text(original, encoding="utf-8")

    def test_plan_for_a_different_course_is_rejected(self) -> None:
        plan_path = _FIXTURE_ROOT / "docs/architecture/curriculum.json"
        original = plan_path.read_text(encoding="utf-8")
        plan = json.loads(original)
        plan["course_id"] = "learning-something-else"
        plan_path.write_text(json.dumps(plan, indent=2), encoding="utf-8")
        try:
            self.assert_rejected(self.manifest, "course_id does not match")
        finally:
            plan_path.write_text(original, encoding="utf-8")


class CommittedPlanTests(unittest.TestCase):
    """The committed manifest and the committed plan must agree."""

    def test_committed_manifest_points_at_the_committed_plan(self) -> None:
        manifest = course_adapter.load_manifest()
        plan_path = REPOSITORY_ROOT / manifest["curriculum_plan"]
        self.assertTrue(plan_path.is_file())
        plan = json.loads(plan_path.read_text(encoding="utf-8"))
        self.assertEqual(plan["course_id"], manifest["course"]["id"])

    def test_no_planned_unit_is_registered_for_tracking(self) -> None:
        manifest = course_adapter.load_manifest()
        plan = json.loads(
            (REPOSITORY_ROOT / manifest["curriculum_plan"]).read_text(encoding="utf-8")
        )
        built = {
            unit["id"]
            for unit in plan["units"]
            if unit.get("artifact_status") == "present"
        }
        registered = {
            record["id"]
            for key in ("modules", "projects", "capstones")
            for record in manifest.get(key, [])
        }
        self.assertEqual(registered, built)


class ProcessBoundaryTests(unittest.TestCase):
    """The CLI is the boundary the mentor uses; its statuses must be trustworthy."""

    def test_validate_emits_success_shaped_json_on_stdout(self) -> None:
        stdout = io.StringIO()
        status = course_adapter.main(["validate"], stdout=stdout, stderr=io.StringIO())
        self.assertEqual(status, course_adapter.EXIT_OK)
        payload = json.loads(stdout.getvalue())
        self.assertEqual(payload["status"], "valid")
        self.assertEqual(payload["adapter_protocol"], "1")
        manifest = course_adapter.load_manifest()
        expected = sum(
            1 + len(module.get("concepts", []))
            for module in manifest.get("modules", [])
        ) + sum(
            1 + len(owner.get("milestones", []))
            for key in ("projects", "capstones")
            for owner in manifest.get(key, [])
        )
        self.assertEqual(payload["trackable_count"], expected)

    def test_state_projection_emits_only_the_graph(self) -> None:
        stdout = io.StringIO()
        status = course_adapter.main(
            ["state-projection"], stdout=stdout, stderr=io.StringIO()
        )
        self.assertEqual(status, course_adapter.EXIT_OK)
        payload = json.loads(stdout.getvalue())
        self.assertEqual(set(payload), {"concepts"})
        manifest = course_adapter.load_manifest()
        registered = {
            record["id"]
            for key in ("modules", "projects", "capstones")
            for record in manifest.get(key, [])
        }
        projected = {node["id"] for node in payload["concepts"]}
        self.assertTrue(registered <= projected)
        for node in payload["concepts"]:
            self.assertEqual(
                set(node),
                {"id", "title", "prerequisites", "order", "solution_unlock_after"},
            )

    def test_missing_manifest_file_exits_nonzero_without_success_json(self) -> None:
        broken = SKILL_DIR / "tests" / "does-not-exist.toml"
        stdout, stderr = io.StringIO(), io.StringIO()
        status = course_adapter.main(
            ["--manifest", str(broken), "validate"], stdout=stdout, stderr=stderr
        )
        self.assertEqual(status, course_adapter.EXIT_IO)
        self.assertEqual(stdout.getvalue(), "")
        self.assertIn("course-adapter:", stderr.getvalue())

    def test_malformed_toml_exits_nonzero_without_success_json(self) -> None:
        broken = _FIXTURE_ROOT / "malformed.toml.tmp"
        broken.write_text("manifest_version = [", encoding="utf-8")
        self.addCleanup(broken.unlink)
        stdout, stderr = io.StringIO(), io.StringIO()
        status = course_adapter.main(
            ["--manifest", str(broken), "validate"], stdout=stdout, stderr=stderr
        )
        self.assertEqual(status, course_adapter.EXIT_INVALID_MANIFEST)
        self.assertEqual(stdout.getvalue(), "")
        self.assertIn("invalid TOML", stderr.getvalue())


class DescriptorTests(unittest.TestCase):
    """The course descriptor is what the shared mentor reads first."""

    @classmethod
    def setUpClass(cls) -> None:
        with (REPOSITORY_ROOT / ".learning-mentor.toml").open("rb") as stream:
            cls.descriptor = tomllib.load(stream)

    def test_descriptor_declares_the_supported_protocol(self) -> None:
        self.assertEqual(self.descriptor["schema_version"], 1)
        self.assertEqual(
            self.descriptor["adapter"]["protocol"],
            course_adapter.SUPPORTED_ADAPTER_PROTOCOL,
        )

    def test_descriptor_selects_exactly_this_course_skill(self) -> None:
        skill = Path(self.descriptor["adapter"]["skill"])
        self.assertEqual(skill, course_adapter.SKILL_RELATIVE_PATH)
        self.assertTrue((REPOSITORY_ROOT / skill).is_file())

    def test_descriptor_commands_are_argument_vectors(self) -> None:
        for section in ("adapter", "state"):
            command = self.descriptor[section]["command"]
            self.assertIsInstance(command, list)
            self.assertTrue(command)
            for argument in command:
                self.assertIsInstance(argument, str)
                self.assertTrue(argument)
                self.assertNotIn(" ", argument)
            self.assertTrue((REPOSITORY_ROOT / command[-1]).is_file())

    def test_state_command_targets_the_shared_helper(self) -> None:
        self.assertEqual(
            self.descriptor["state"]["command"][-1],
            ".agents/skills/guided-learning/scripts/learning_state.py",
        )


if __name__ == "__main__":
    unittest.main()
