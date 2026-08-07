using System.Text.Json.Serialization;

namespace LearningAzure.CourseVerifier;

/// <summary>Unit kinds. Values match the semantic ID prefix.</summary>
internal static class UnitKinds
{
    internal const string Module = "module";
    internal const string Project = "project";
    internal const string Capstone = "capstone";

    internal static readonly string[] All = [Module, Project, Capstone];
}

/// <summary>Stages of the quality contract's coverage progression.</summary>
internal static class EvidenceStages
{
    internal const string Named = "named";
    internal const string Explained = "explained";
    internal const string Demonstrated = "demonstrated";
    internal const string Practiced = "practiced";
    internal const string Applied = "applied";

    /// <summary>Ordered stages; every unit declares exactly one record per stage.</summary>
    internal static readonly string[] Ordered = [Named, Explained, Demonstrated, Practiced, Applied];
}

/// <summary>Evidence statuses. See <c>docs/architecture/curriculum-plan-schema.md</c>.</summary>
internal static class EvidenceStatuses
{
    internal const string Planned = "planned";
    internal const string Deferred = "deferred";
    internal const string Covered = "covered";
    internal const string NotApplicable = "not-applicable";

    internal static readonly string[] All = [Planned, Deferred, Covered, NotApplicable];
}

/// <summary>Whether a unit's artifacts are expected on disk yet.</summary>
internal static class ArtifactStatuses
{
    internal const string Planned = "planned";
    internal const string Present = "present";

    internal static readonly string[] All = [Planned, Present];
}

internal sealed record ArtifactReference
{
    public string? Role { get; init; }

    public string? Path { get; init; }

    public string? Anchor { get; init; }
}

internal sealed record CurriculumOutcome
{
    public string Id { get; init; } = string.Empty;

    public string Statement { get; init; } = string.Empty;

    public string MeasuredBy { get; init; } = string.Empty;
}

internal sealed record CurriculumMilestone
{
    public string Id { get; init; } = string.Empty;

    public int Ordinal { get; init; }

    public string Title { get; init; } = string.Empty;

    public IReadOnlyList<string> Prerequisites { get => field; init => field = value ?? []; } = [];

    public string RequiredOutcome { get; init; } = string.Empty;
}

internal sealed record EvidenceRecord
{
    public string Stage { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public ArtifactReference? Artifact { get; init; }

    public string? Note { get; init; }

    public string? Rationale { get; init; }

    public string? DeferredTo { get; init; }
}

internal sealed record CurriculumUnit
{
    public string Id { get; init; } = string.Empty;

    public string Kind { get; init; } = string.Empty;

    public string Slug { get; init; } = string.Empty;

    public int Ordinal { get; init; }

    public int Sequence { get; init; }

    public string Title { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public IReadOnlyList<string> Prerequisites { get => field; init => field = value ?? []; } = [];

    public IReadOnlyList<string> Environments { get => field; init => field = value ?? []; } = [];

    public bool ManagementLabs { get; init; }

    public string ArtifactStatus { get; init; } = string.Empty;

    public string? SplitRationale { get; init; }

    public bool FinalDestination { get; init; }

    public IReadOnlyList<CurriculumOutcome> Outcomes { get => field; init => field = value ?? []; } = [];

    public IReadOnlyList<CurriculumMilestone> Milestones { get => field; init => field = value ?? []; } = [];

    public IReadOnlyList<EvidenceRecord> Evidence { get => field; init => field = value ?? []; } = [];
}

internal sealed record RepositoryRole
{
    public string Role { get; init; } = string.Empty;

    public string ContractReference { get; init; } = string.Empty;

    public string Path { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string? Note { get; init; }
}

internal sealed record PlanConventions
{
    public string LessonRoot { get; init; } = string.Empty;

    public string ExerciseRoot { get; init; } = string.Empty;

    public string ProjectRoot { get; init; } = string.Empty;

    public string CapstoneRoot { get; init; } = string.Empty;

    public IReadOnlyList<string> PracticeTrees { get => field; init => field = value ?? []; } = [];

    public string CliLab { get; init; } = string.Empty;

    public string PowershellLab { get; init; } = string.Empty;
}

internal sealed record CurriculumPlan
{
    public int PlanVersion { get; init; }

    public string SchemaDocument { get; init; } = string.Empty;

    public string CourseId { get; init; } = string.Empty;

    public string NarrativeDocument { get; init; } = string.Empty;

    public string EvidenceMatrixDocument { get; init; } = string.Empty;

    public string ManifestDocument { get; init; } = string.Empty;

    public PlanConventions Conventions { get => field; init => field = value ?? new(); } = new();

    public IReadOnlyList<RepositoryRole> Roles { get => field; init => field = value ?? []; } = [];

    public IReadOnlyList<CurriculumUnit> Units { get => field; init => field = value ?? []; } = [];
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip)]
[JsonSerializable(typeof(CurriculumPlan))]
internal sealed partial class PlanSerializerContext : JsonSerializerContext;
