using System.Globalization;
using System.Text.RegularExpressions;

namespace LearningAzure.CourseVerifier;

/// <summary>One verification failure, addressed to the artifact that must change.</summary>
internal sealed record Finding(string Scope, string Message)
{
    public override string ToString() => $"{Scope}: {Message}";
}

/// <summary>
/// Validates the curriculum plan, the repository state it claims, and the mentor
/// manifest that may track it.
/// </summary>
/// <remarks>
/// The verifier fails closed: it reports every finding it can and never treats an
/// unreadable, ambiguous, or unbacked claim as a pass. Its central rule is that a
/// unit may claim coverage, or be registered for mentor tracking, only when its
/// content actually exists on disk.
/// </remarks>
internal sealed partial class CourseVerification(string repositoryRoot, string planRelativePath)
{
    private const int SupportedPlanVersion = 1;

    private static readonly string[] RequiredRoles =
    [
        "learner-entry-point",
        "setup-and-troubleshooting",
        "sequenced-instructional-units",
        "practice-starter-and-solution",
        "applied-projects-and-capstones",
        "reference-and-recall",
        "environment-manifests",
        "automated-validation",
        "learning-mentor-integration",
    ];

    private static readonly string[] MeasurableVerbs =
    [
        "apply", "assign", "build", "choose", "compare", "configure", "consume", "create",
        "deploy", "design", "diagnose", "explain", "handle", "implement", "justify",
        "measure", "operate", "organize", "produce", "prove", "recover", "verify",
    ];

    private static readonly string[] UnmeasurableVerbs =
    [
        "understand", "know", "learn", "appreciate", "familiar", "aware", "grasp",
        "study", "explore", "review", "discuss", "cover", "introduce", "get to know",
    ];

    private readonly string _repositoryRoot = repositoryRoot;
    private readonly string _planRelativePath = planRelativePath;
    private readonly List<Finding> _findings = [];

    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex SlugPattern { get; }

    [GeneratedRegex(@"^[a-z0-9]+([._-][a-z0-9]+)*$")]
    private static partial Regex IdentifierPattern { get; }

    [GeneratedRegex(@"\b[0-9a-f]{7,40}\b")]
    private static partial Regex CommitHashPattern { get; }

    [GeneratedRegex("""^\s*id\s*=\s*"(?<id>(module|project|capstone)\.[^"]+)"\s*$""", RegexOptions.Multiline)]
    private static partial Regex ManifestUnitIdPattern { get; }

    [GeneratedRegex("""^\s*id\s*=\s*"(?<id>[^"]+)"\s*$""", RegexOptions.Multiline)]
    private static partial Regex ManifestAnyIdPattern { get; }

    internal IReadOnlyList<Finding> Findings => _findings;

    /// <summary>Runs every check and returns the loaded plan, or <c>null</c> when it could not be read.</summary>
    internal CurriculumPlan? Run()
    {
        CurriculumPlan plan;
        try
        {
            plan = PlanLoader.Load(Path.Combine(_repositoryRoot, _planRelativePath));
        }
        catch (PlanLoadException error)
        {
            Report(_planRelativePath, error.Message);
            return null;
        }

        CheckDocument(plan);
        CheckConventions(plan);
        CheckRoles(plan);
        var byId = CheckUnitIdentity(plan);
        CheckGraph(plan, byId);
        CheckShape(plan);
        foreach (var unit in plan.Units)
        {
            CheckOutcomes(unit);
            CheckMilestones(unit);
            CheckEvidence(plan, unit, byId);
            CheckArtifactStatus(plan, unit);
        }

        CheckManifestRegistration(plan, byId);
        CheckNarrativeCoverage(plan);
        return plan;
    }

    private void Report(string scope, string message) => _findings.Add(new Finding(scope, message));

    private string Absolute(string relativePath) =>
        Path.GetFullPath(Path.Combine(_repositoryRoot, relativePath));

    private bool Exists(string relativePath)
    {
        var absolute = Absolute(relativePath);
        return File.Exists(absolute) || Directory.Exists(absolute);
    }

    private void RequireExists(string scope, string label, string relativePath)
    {
        if (!Exists(relativePath))
        {
            Report(scope, $"{label} does not exist: {relativePath}");
        }
    }

    private void CheckDocument(CurriculumPlan plan)
    {
        if (plan.PlanVersion != SupportedPlanVersion)
        {
            Report(_planRelativePath, $"unsupported plan_version {plan.PlanVersion}; expected {SupportedPlanVersion}");
        }

        RequireExists(_planRelativePath, "schema_document", plan.SchemaDocument);
        RequireExists(_planRelativePath, "narrative_document", plan.NarrativeDocument);
        RequireExists(_planRelativePath, "manifest_document", plan.ManifestDocument);

        if (string.IsNullOrWhiteSpace(plan.EvidenceMatrixDocument))
        {
            Report(_planRelativePath, "evidence_matrix_document must be declared");
        }

        if (string.IsNullOrWhiteSpace(plan.CourseId))
        {
            Report(_planRelativePath, "course_id must be declared");
        }
    }

    private void CheckConventions(CurriculumPlan plan)
    {
        // The documented convention and the derivation in ArtifactRoles must agree,
        // otherwise the schema document would describe a layout nothing enforces.
        var expected = new (string Field, string Value, string Actual)[]
        {
            ("lesson_root", "lessons/{ordinal}-{slug}", plan.Conventions.LessonRoot),
            ("exercise_root", "exercises/{ordinal}-{slug}", plan.Conventions.ExerciseRoot),
            ("project_root", "projects/{slug}", plan.Conventions.ProjectRoot),
            ("capstone_root", "capstones/{slug}", plan.Conventions.CapstoneRoot),
            ("cli_lab", "infra/azure-cli/{slug}.sh", plan.Conventions.CliLab),
            ("powershell_lab", "infra/powershell/{slug}.ps1", plan.Conventions.PowershellLab),
        };

        foreach (var (field, value, actual) in expected)
        {
            if (!string.Equals(value, actual, StringComparison.Ordinal))
            {
                Report(_planRelativePath, $"conventions.{field} must be '{value}' to match the derived paths");
            }
        }

        if (!plan.Conventions.PracticeTrees.SequenceEqual(["starter", "solution", "tests"], StringComparer.Ordinal))
        {
            Report(_planRelativePath, "conventions.practice_trees must be starter, solution, tests");
        }
    }

    private void CheckRoles(CurriculumPlan plan)
    {
        var declared = plan.Roles.Select(role => role.Role).ToList();
        foreach (var missing in RequiredRoles.Except(declared, StringComparer.Ordinal))
        {
            Report("roles", $"required repository role '{missing}' is not mapped");
        }

        foreach (var unexpected in declared.Except(RequiredRoles, StringComparer.Ordinal))
        {
            Report("roles", $"unknown repository role '{unexpected}'");
        }

        foreach (var duplicate in declared.GroupBy(role => role, StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            Report("roles", $"repository role '{duplicate.Key}' is mapped more than once");
        }

        foreach (var role in plan.Roles)
        {
            var scope = $"roles.{role.Role}";
            if (string.IsNullOrWhiteSpace(role.ContractReference))
            {
                Report(scope, "contract_reference must cite the governing requirement");
            }

            if (string.IsNullOrWhiteSpace(role.Note))
            {
                Report(scope, "note must describe how the role is fulfilled");
            }

            switch (role.Status)
            {
                case "present" when !Exists(role.Path):
                    Report(scope, $"claims status 'present' but {role.Path} does not exist");
                    break;
                case "planned" when Exists(role.Path):
                    Report(scope, $"claims status 'planned' but {role.Path} already exists; promote the role record");
                    break;
                case "present":
                case "planned":
                    break;
                default:
                    Report(scope, $"status '{role.Status}' must be 'present' or 'planned'");
                    break;
            }
        }
    }

    private Dictionary<string, CurriculumUnit> CheckUnitIdentity(CurriculumPlan plan)
    {
        var byId = new Dictionary<string, CurriculumUnit>(StringComparer.Ordinal);
        foreach (var unit in plan.Units)
        {
            var scope = string.IsNullOrWhiteSpace(unit.Id) ? "units[?]" : unit.Id;
            if (!byId.TryAdd(unit.Id, unit))
            {
                Report(scope, "duplicate unit id");
            }

            if (!UnitKinds.All.Contains(unit.Kind, StringComparer.Ordinal))
            {
                Report(scope, $"kind '{unit.Kind}' must be module, project, or capstone");
                continue;
            }

            if (!SlugPattern.IsMatch(unit.Slug))
            {
                Report(scope, $"slug '{unit.Slug}' must be lowercase kebab-case");
            }

            if (!string.Equals(unit.Id, $"{unit.Kind}.{unit.Slug}", StringComparison.Ordinal))
            {
                Report(scope, $"id must be '{unit.Kind}.{unit.Slug}' so identity never depends on order or file name");
            }

            if (CommitHashPattern.IsMatch(unit.Id))
            {
                Report(scope, "id must not embed a commit hash");
            }

            if (string.IsNullOrWhiteSpace(unit.Title))
            {
                Report(scope, "title must be a non-empty learner-facing name");
            }

            if (string.IsNullOrWhiteSpace(unit.Summary))
            {
                Report(scope, "summary must describe what the unit teaches");
            }

            if (!ArtifactStatuses.All.Contains(unit.ArtifactStatus, StringComparer.Ordinal))
            {
                Report(scope, $"artifact_status '{unit.ArtifactStatus}' must be 'planned' or 'present'");
            }

            if (unit.Environments.Count == 0)
            {
                Report(scope, "environments must name at least one of local, emulator, live-checkpoint");
            }

            foreach (var environment in unit.Environments.Where(value =>
                         value is not ("local" or "emulator" or "live-checkpoint")))
            {
                Report(scope, $"unknown environment '{environment}'");
            }
        }

        return byId;
    }

    private void CheckGraph(CurriculumPlan plan, Dictionary<string, CurriculumUnit> byId)
    {
        foreach (var kind in UnitKinds.All)
        {
            var ordinals = plan.Units.Where(unit => unit.Kind == kind).Select(unit => unit.Ordinal).ToList();
            if (!ordinals.OrderBy(value => value).SequenceEqual(Enumerable.Range(1, ordinals.Count)))
            {
                Report("units", $"{kind} ordinals must be unique and gap-free from 1");
            }
        }

        var sequences = plan.Units.Select(unit => unit.Sequence).ToList();
        if (!sequences.OrderBy(value => value).SequenceEqual(Enumerable.Range(1, sequences.Count)))
        {
            Report("units", "sequence values must be unique and gap-free from 1 across every unit");
        }

        foreach (var unit in plan.Units)
        {
            if (unit.Prerequisites.Distinct(StringComparer.Ordinal).Count() != unit.Prerequisites.Count)
            {
                Report(unit.Id, "prerequisites must be unique");
            }

            foreach (var prerequisite in unit.Prerequisites)
            {
                if (string.Equals(prerequisite, unit.Id, StringComparison.Ordinal))
                {
                    Report(unit.Id, "a unit cannot require itself");
                    continue;
                }

                if (!byId.TryGetValue(prerequisite, out var required))
                {
                    Report(unit.Id, $"unknown prerequisite '{prerequisite}'");
                    continue;
                }

                if (required.Sequence >= unit.Sequence)
                {
                    Report(unit.Id, $"prerequisite '{prerequisite}' is taught at or after this unit (sequence {required.Sequence} >= {unit.Sequence})");
                }
            }
        }

        CheckAcyclic(
            "units",
            plan.Units.Select(unit => unit.Id),
            id => byId.TryGetValue(id, out var unit) ? unit.Prerequisites : []);

        CheckTransitivelyReduced(plan, byId);
    }

    private void CheckTransitivelyReduced(CurriculumPlan plan, Dictionary<string, CurriculumUnit> byId)
    {
        foreach (var unit in plan.Units)
        {
            foreach (var prerequisite in unit.Prerequisites)
            {
                var others = unit.Prerequisites.Where(other => !string.Equals(other, prerequisite, StringComparison.Ordinal));
                foreach (var other in others)
                {
                    if (Reaches(other, prerequisite, byId, []))
                    {
                        Report(
                            unit.Id,
                            $"prerequisite '{prerequisite}' is redundant: it is already reachable through '{other}'");
                    }
                }
            }
        }
    }

    private static bool Reaches(
        string from,
        string target,
        Dictionary<string, CurriculumUnit> byId,
        HashSet<string> visited)
    {
        if (!visited.Add(from) || !byId.TryGetValue(from, out var unit))
        {
            return false;
        }

        return unit.Prerequisites.Any(prerequisite =>
            string.Equals(prerequisite, target, StringComparison.Ordinal)
            || Reaches(prerequisite, target, byId, visited));
    }

    private void CheckAcyclic(string scope, IEnumerable<string> nodes, Func<string, IReadOnlyList<string>> edges)
    {
        var remaining = nodes.ToHashSet(StringComparer.Ordinal);
        var progress = true;
        while (progress && remaining.Count > 0)
        {
            progress = false;
            foreach (var node in remaining.ToList())
            {
                if (edges(node).All(edge => !remaining.Contains(edge)))
                {
                    remaining.Remove(node);
                    progress = true;
                }
            }
        }

        if (remaining.Count > 0)
        {
            Report(scope, $"prerequisite cycle involving: {string.Join(", ", remaining.Order(StringComparer.Ordinal))}");
        }
    }

    private void CheckShape(CurriculumPlan plan)
    {
        var projects = plan.Units.Where(unit => unit.Kind == UnitKinds.Project).ToList();
        var capstones = plan.Units.Where(unit => unit.Kind == UnitKinds.Capstone).ToList();

        if (projects.Count != 1)
        {
            Report("units", $"the course declares exactly one applied project; found {projects.Count}");
        }

        if (capstones.Count != 1)
        {
            Report("units", $"the course declares exactly one capstone; found {capstones.Count}");
        }

        foreach (var unit in plan.Units)
        {
            var isCapstone = unit.Kind == UnitKinds.Capstone;
            if (isCapstone && !unit.FinalDestination)
            {
                Report(unit.Id, "the capstone must be declared as the required final destination");
            }

            if (!isCapstone && unit.FinalDestination)
            {
                Report(unit.Id, "only a capstone may be a final destination");
            }
        }

        var last = plan.Units.OrderByDescending(unit => unit.Sequence).FirstOrDefault();
        if (last is not null && last.Kind != UnitKinds.Capstone)
        {
            Report("units", $"the capstone must be taught last; '{last.Id}' is sequenced after it");
        }
    }

    private void CheckOutcomes(CurriculumUnit unit)
    {
        if (unit.Outcomes.Count == 0)
        {
            Report(unit.Id, "at least one measurable outcome is required");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var derived = ArtifactRoles.DerivePaths(unit);
        foreach (var outcome in unit.Outcomes)
        {
            var scope = $"{unit.Id}.{outcome.Id}";
            if (!seen.Add(outcome.Id))
            {
                Report(scope, "duplicate outcome id");
            }

            if (!IdentifierPattern.IsMatch(outcome.Id) || !outcome.Id.StartsWith($"outcome.{unit.Slug}.", StringComparison.Ordinal))
            {
                Report(scope, $"outcome id must be 'outcome.{unit.Slug}.<name>'");
            }

            var statement = outcome.Statement.Trim();
            if (statement.Length == 0)
            {
                Report(scope, "statement must not be empty");
                continue;
            }

            if (!statement.EndsWith('.'))
            {
                Report(scope, "statement must be a complete sentence ending in a period");
            }

            var firstWord = statement.Split(' ', 2)[0].ToLowerInvariant().TrimEnd(',');
            if (!MeasurableVerbs.Contains(firstWord, StringComparer.Ordinal))
            {
                Report(scope, $"statement must start with a measurable verb; '{firstWord}' is not one of: {string.Join(", ", MeasurableVerbs)}");
            }

            var lowered = statement.ToLowerInvariant();
            foreach (var vague in UnmeasurableVerbs.Where(verb => UsesTerm(lowered, verb)))
            {
                Report(scope, $"statement uses the unmeasurable term '{vague}'");
            }

            var measuredBy = outcome.MeasuredBy;
            var resolved = ResolveRoleForOutcome(unit, measuredBy, derived);
            if (resolved is null)
            {
                Report(scope, $"measured_by '{measuredBy}' is not a feedback-path role this unit owns");
            }
        }
    }

    /// <summary>Matches a term on word boundaries so "recover" does not trip the ban on "cover".</summary>
    private static bool UsesTerm(string statement, string term) =>
        Regex.IsMatch(statement, $@"\b{Regex.Escape(term)}\w*\b", RegexOptions.None, TimeSpan.FromSeconds(1));

    private static string? ResolveRoleForOutcome(
        CurriculumUnit unit,
        string role,
        IReadOnlyDictionary<string, string> derived)
    {
        var feedbackRoles = unit.Kind switch
        {
            UnitKinds.Module => new[] { ArtifactRoles.ExerciseTests },
            UnitKinds.Project => [ArtifactRoles.ProjectTests],
            UnitKinds.Capstone => [ArtifactRoles.CapstoneTests],
            _ => [],
        };

        return feedbackRoles.Contains(role, StringComparer.Ordinal) && derived.TryGetValue(role, out var path)
            ? path
            : null;
    }

    private void CheckMilestones(CurriculumUnit unit)
    {
        if (unit.Kind == UnitKinds.Module)
        {
            if (unit.Milestones.Count > 0)
            {
                Report(unit.Id, "modules are graded by one exercise, not by milestones");
            }

            return;
        }

        if (unit.Milestones.Count < 2)
        {
            Report(unit.Id, "an applied project or capstone must be staged into at least two milestones");
        }

        var ordinals = unit.Milestones.Select(milestone => milestone.Ordinal).ToList();
        if (!ordinals.OrderBy(value => value).SequenceEqual(Enumerable.Range(1, ordinals.Count)))
        {
            Report(unit.Id, "milestone ordinals must be unique and gap-free from 1");
        }

        var byId = unit.Milestones.ToDictionary(milestone => milestone.Id, StringComparer.Ordinal);
        foreach (var milestone in unit.Milestones)
        {
            var scope = $"{unit.Id}.{milestone.Id}";
            if (!IdentifierPattern.IsMatch(milestone.Id) || !milestone.Id.StartsWith($"milestone.{unit.Slug}.", StringComparison.Ordinal))
            {
                Report(scope, $"milestone id must be 'milestone.{unit.Slug}.<name>'");
            }

            if (string.IsNullOrWhiteSpace(milestone.Title))
            {
                Report(scope, "title must not be empty");
            }

            if (string.IsNullOrWhiteSpace(milestone.RequiredOutcome))
            {
                Report(scope, "required_outcome must state what completion means");
            }

            foreach (var prerequisite in milestone.Prerequisites)
            {
                if (!byId.TryGetValue(prerequisite, out var required))
                {
                    Report(scope, $"milestone prerequisite '{prerequisite}' is not declared by {unit.Id}");
                    continue;
                }

                if (required.Ordinal >= milestone.Ordinal)
                {
                    Report(scope, $"milestone prerequisite '{prerequisite}' is not staged earlier");
                }
            }
        }

        CheckAcyclic(
            unit.Id,
            unit.Milestones.Select(milestone => milestone.Id),
            id => byId.TryGetValue(id, out var milestone) ? milestone.Prerequisites : []);
    }

    private void CheckEvidence(CurriculumPlan plan, CurriculumUnit unit, Dictionary<string, CurriculumUnit> byId)
    {
        var stages = unit.Evidence.Select(record => record.Stage).ToList();
        if (!stages.SequenceEqual(EvidenceStages.Ordered, StringComparer.Ordinal))
        {
            Report(
                unit.Id,
                $"evidence must declare exactly one record per stage in order: {string.Join(" -> ", EvidenceStages.Ordered)}");
        }

        foreach (var record in unit.Evidence)
        {
            var scope = $"{unit.Id}.evidence.{record.Stage}";
            if (!EvidenceStatuses.All.Contains(record.Status, StringComparer.Ordinal))
            {
                Report(scope, $"status '{record.Status}' must be one of: {string.Join(", ", EvidenceStatuses.All)}");
                continue;
            }

            if (record.Status != EvidenceStatuses.NotApplicable && string.IsNullOrWhiteSpace(record.Note))
            {
                Report(scope, "note must describe what satisfies this stage");
            }

            switch (record.Status)
            {
                case EvidenceStatuses.NotApplicable:
                    if (string.IsNullOrWhiteSpace(record.Rationale))
                    {
                        Report(scope, "a not-applicable stage requires a rationale");
                    }

                    if (record.Artifact is not null)
                    {
                        Report(scope, "a not-applicable stage must not cite an artifact");
                    }

                    continue;

                case EvidenceStatuses.Deferred:
                    CheckDeferred(scope, unit, record, byId);
                    continue;

                case EvidenceStatuses.Planned when unit.ArtifactStatus == ArtifactStatuses.Present:
                    Report(scope, "this unit's artifacts are present, so the stage must be covered, partial, missing, deferred, or explicitly not applicable");
                    continue;

                case EvidenceStatuses.Partial:
                    Report(scope, "partial evidence blocks curriculum completion; finish the stage or classify it explicitly");
                    continue;

                case EvidenceStatuses.Missing:
                    Report(scope, "missing evidence blocks curriculum completion");
                    continue;

                default:
                    break;
            }

            var resolved = ResolveArtifact(plan, unit, record.Artifact, scope);
            if (resolved is null)
            {
                continue;
            }

            if (record.Status == EvidenceStatuses.Covered)
            {
                if (!Exists(resolved))
                {
                    Report(scope, $"claims coverage but {resolved} does not exist");
                }
                else if (record.Artifact?.Anchor is { } anchor && !HasHeading(resolved, anchor))
                {
                    Report(scope, $"claims coverage at '{resolved}#{anchor}' but no such heading exists");
                }
            }
        }
    }

    private void CheckDeferred(
        string scope,
        CurriculumUnit unit,
        EvidenceRecord record,
        Dictionary<string, CurriculumUnit> byId)
    {
        if (record.Stage != EvidenceStages.Applied)
        {
            Report(scope, "only the applied stage may be deferred to a later unit");
            return;
        }

        if (record.DeferredTo is not { } target || !byId.TryGetValue(target, out var deferred))
        {
            Report(scope, "a deferred stage must name an existing later unit in deferred_to");
            return;
        }

        if (deferred.Sequence <= unit.Sequence)
        {
            Report(scope, $"deferred_to '{target}' is not taught later than {unit.Id}");
        }

        if (deferred.ArtifactStatus == ArtifactStatuses.Present)
        {
            Report(scope, $"deferred_to '{target}' is already built, so this stage must be resolved to covered");
        }
    }

    private string? ResolveArtifact(
        CurriculumPlan plan,
        CurriculumUnit unit,
        ArtifactReference? artifact,
        string scope)
    {
        if (artifact is null)
        {
            Report(scope, "an artifact reference is required");
            return null;
        }

        if (artifact.Role is null && artifact.Path is null)
        {
            Report(scope, "artifact must declare a role or a path");
            return null;
        }

        if (artifact.Role is not null && artifact.Path is not null)
        {
            Report(scope, "artifact must declare either a role or a path, not both");
            return null;
        }

        if (artifact.Path is { } explicitPath)
        {
            if (Path.IsPathRooted(explicitPath) || explicitPath.Contains("..", StringComparison.Ordinal))
            {
                Report(scope, $"artifact path '{explicitPath}' must be repository relative");
                return null;
            }

            return explicitPath;
        }

        var role = artifact.Role!;
        var owner = unit;
        if (ArtifactRoles.IsSharedRole(role) && unit.Kind == UnitKinds.Module)
        {
            var ownerKind = role.StartsWith("project_", StringComparison.Ordinal) ? UnitKinds.Project : UnitKinds.Capstone;
            var shared = plan.Units.SingleOrDefault(candidate => candidate.Kind == ownerKind);
            if (shared is null)
            {
                Report(scope, $"role '{role}' cannot be resolved without exactly one {ownerKind}");
                return null;
            }

            owner = shared;
        }

        var derived = ArtifactRoles.DerivePaths(owner);
        if (!derived.TryGetValue(role, out var path))
        {
            Report(scope, $"role '{role}' is not owned by {owner.Id}");
            return null;
        }

        return path;
    }

    private bool HasHeading(string relativePath, string anchor)
    {
        var absolute = Absolute(relativePath);
        if (!File.Exists(absolute))
        {
            return false;
        }

        foreach (var line in File.ReadLines(absolute))
        {
            if (!line.StartsWith('#'))
            {
                continue;
            }

            if (string.Equals(Slugify(line.TrimStart('#').Trim()), anchor, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Converts a markdown heading into its GitHub anchor slug.</summary>
    internal static string Slugify(string heading)
    {
        ArgumentNullException.ThrowIfNull(heading);

        var builder = new System.Text.StringBuilder(heading.Length);
        foreach (var character in heading.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character) || character == '-')
            {
                builder.Append(character);
            }
            else if (char.IsWhiteSpace(character))
            {
                builder.Append('-');
            }
        }

        return builder.ToString();
    }

    private void CheckArtifactStatus(CurriculumPlan plan, CurriculumUnit unit)
    {
        var derived = ArtifactRoles.DerivePaths(unit);
        foreach (var (role, path) in derived.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            var scope = $"{unit.Id}.{role}";
            switch (unit.ArtifactStatus)
            {
                case ArtifactStatuses.Present when !Exists(path):
                    Report(scope, $"declared present but {path} does not exist");
                    break;

                // Content that lands without a matching plan update would let the course
                // advertise coverage it has never reviewed, so silence is not tolerated.
                case ArtifactStatuses.Planned when Exists(path):
                    Report(scope, $"declared planned but {path} exists; promote artifact_status to 'present' and update the evidence records");
                    break;

                default:
                    break;
            }
        }

        _ = plan;
    }

    private void CheckManifestRegistration(CurriculumPlan plan, Dictionary<string, CurriculumUnit> byId)
    {
        var manifestPath = Absolute(plan.ManifestDocument);
        if (!File.Exists(manifestPath))
        {
            return;
        }

        var manifest = File.ReadAllText(manifestPath);
        var registered = ManifestUnitIdPattern.Matches(manifest)
            .Select(match => match.Groups["id"].Value)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var id in registered.Order(StringComparer.Ordinal))
        {
            if (!byId.TryGetValue(id, out var unit))
            {
                Report(plan.ManifestDocument, $"registers '{id}', which the curriculum plan does not declare");
                continue;
            }

            if (unit.ArtifactStatus != ArtifactStatuses.Present)
            {
                Report(plan.ManifestDocument, $"registers '{id}' while the plan still marks its artifacts as planned");
            }
        }

        foreach (var unit in plan.Units.Where(unit => unit.ArtifactStatus == ArtifactStatuses.Present))
        {
            if (!registered.Contains(unit.Id))
            {
                Report(plan.ManifestDocument, $"'{unit.Id}' is built but is not registered for mentor tracking");
            }
        }

        var courseId = ManifestAnyIdPattern.Matches(manifest)
            .Select(match => match.Groups["id"].Value)
            .FirstOrDefault(id => !id.Contains('.', StringComparison.Ordinal));
        if (courseId is not null && !string.Equals(courseId, plan.CourseId, StringComparison.Ordinal))
        {
            Report(plan.ManifestDocument, $"course id '{courseId}' does not match the plan's '{plan.CourseId}'");
        }
    }

    private void CheckNarrativeCoverage(CurriculumPlan plan)
    {
        var narrativePath = Absolute(plan.NarrativeDocument);
        if (!File.Exists(narrativePath))
        {
            return;
        }

        var narrative = File.ReadAllText(narrativePath);
        foreach (var unit in plan.Units)
        {
            if (!narrative.Contains(unit.Id, StringComparison.Ordinal))
            {
                Report(plan.NarrativeDocument, $"does not mention '{unit.Id}'; the human curriculum has drifted from the plan");
            }

            foreach (var outcome in unit.Outcomes)
            {
                if (!narrative.Contains(outcome.Statement, StringComparison.Ordinal))
                {
                    Report(plan.NarrativeDocument, FormattableString.Invariant($"does not state outcome '{outcome.Id}' verbatim"));
                }
            }
        }
    }

    internal static string FormatCount(int value) => value.ToString(CultureInfo.InvariantCulture);
}
