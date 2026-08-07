using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LearningAzure.CourseVerifier.Tests;

/// <summary>
/// Builds a throwaway repository on disk containing a synthetic curriculum plan.
/// </summary>
/// <remarks>
/// Fixtures are written under the test binary's output directory, which is build
/// output and therefore never tracked. Every rule the verifier enforces is proven
/// against a fixture, so the evaluator is known to fail closed today — before any
/// course content exists to exercise it.
/// </remarks>
internal sealed class FixtureRepository : IDisposable
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private FixtureRepository(string root, JsonObject plan)
    {
        Root = root;
        Plan = plan;
    }

    internal string Root { get; }

    internal JsonObject Plan { get; }

    internal const string PlanRelativePath = "docs/architecture/curriculum.json";

    internal static FixtureRepository Create()
    {
        var root = Path.Combine(AppContext.BaseDirectory, ".fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new FixtureRepository(root, BuildPlan());
    }

    /// <summary>Writes the plan and every document it declares, then verifies it.</summary>
    internal IReadOnlyList<Finding> Verify()
    {
        Write("LearningAzure.slnx", "<Solution />\n");
        Write("docs/architecture/curriculum-plan-schema.md", "# schema\n");
        Write(".agents/skills/azure-learning-path/course.toml", ManifestText);
        Write(PlanRelativePath, Plan.ToJsonString(WriteOptions));
        Write("docs/architecture/curriculum.md", RenderNarrative());

        var verification = new CourseVerification(Root, PlanRelativePath);
        verification.Run();
        return verification.Findings;
    }

    /// <summary>The mentor manifest text written into the fixture; tests may replace it.</summary>
    internal string ManifestText { get; set; } = "[course]\nid = \"fixture-course\"\n";

    internal void Write(string relativePath, string contents)
    {
        var absolute = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, contents);
    }

    internal JsonObject Unit(string id) =>
        Plan["units"]!.AsArray().Single(unit => (string?)unit!["id"] == id)!.AsObject();

    internal JsonObject Evidence(string unitId, string stage) =>
        Unit(unitId)["evidence"]!.AsArray().Single(record => (string?)record!["stage"] == stage)!.AsObject();

    private string RenderNarrative()
    {
        var builder = new StringBuilder("# fixture curriculum\n\n");
        foreach (var unit in Plan["units"]!.AsArray())
        {
            builder.Append("## ").Append((string?)unit!["id"]).Append('\n');
            foreach (var outcome in unit["outcomes"]!.AsArray())
            {
                builder.Append("- ").Append((string?)outcome!["statement"]).Append('\n');
            }
        }

        return builder.ToString();
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    private static JsonObject BuildPlan()
    {
        var roles = new JsonArray();
        foreach (var role in new[]
                 {
                     "learner-entry-point", "setup-and-troubleshooting", "sequenced-instructional-units",
                     "practice-starter-and-solution", "applied-projects-and-capstones", "reference-and-recall",
                     "environment-manifests", "automated-validation", "learning-mentor-integration",
                 })
        {
            roles.Add(new JsonObject
            {
                ["role"] = role,
                ["contract_reference"] = "QUALITY_CONTRACT.md#4-required-repository-roles",
                ["path"] = $"planned/{role}",
                ["status"] = "planned",
                ["note"] = "fixture role",
            });
        }

        return new JsonObject
        {
            ["plan_version"] = 1,
            ["schema_document"] = "docs/architecture/curriculum-plan-schema.md",
            ["course_id"] = "fixture-course",
            ["narrative_document"] = "docs/architecture/curriculum.md",
            ["evidence_matrix_document"] = "docs/architecture/evidence-matrix.md",
            ["manifest_document"] = ".agents/skills/azure-learning-path/course.toml",
            ["conventions"] = new JsonObject
            {
                ["lesson_root"] = "lessons/{ordinal}-{slug}",
                ["exercise_root"] = "exercises/{ordinal}-{slug}",
                ["project_root"] = "projects/{slug}",
                ["capstone_root"] = "capstones/{slug}",
                ["practice_trees"] = new JsonArray("starter", "solution", "tests"),
                ["cli_lab"] = "infra/azure-cli/{slug}.sh",
                ["powershell_lab"] = "infra/powershell/{slug}.ps1",
            },
            ["roles"] = roles,
            ["units"] = new JsonArray(
                Module("alpha", ordinal: 1, sequence: 1, prerequisites: []),
                Module("beta", ordinal: 2, sequence: 2, prerequisites: ["module.alpha"]),
                Practice("project", "gamma", ordinal: 1, sequence: 3, prerequisites: ["module.beta"]),
                Practice("capstone", "delta", ordinal: 1, sequence: 4, prerequisites: ["project.gamma"])),
        };
    }

    private static JsonObject Module(string slug, int ordinal, int sequence, string[] prerequisites) =>
        new()
        {
            ["id"] = $"module.{slug}",
            ["kind"] = "module",
            ["slug"] = slug,
            ["ordinal"] = ordinal,
            ["sequence"] = sequence,
            ["title"] = $"Module {slug}",
            ["summary"] = $"Teaches {slug}.",
            ["prerequisites"] = ToArray(prerequisites),
            ["environments"] = new JsonArray("local"),
            ["management_labs"] = false,
            ["artifact_status"] = "planned",
            ["outcomes"] = new JsonArray(new JsonObject
            {
                ["id"] = $"outcome.{slug}.primary",
                ["statement"] = $"Build a working {slug} component.",
                ["measured_by"] = "exercise_tests",
            }),
            ["evidence"] = Evidence("lesson_readme", "exercise_tests", "project_guide"),
        };

    private static JsonObject Practice(string kind, string slug, int ordinal, int sequence, string[] prerequisites)
    {
        var unit = new JsonObject
        {
            ["id"] = $"{kind}.{slug}",
            ["kind"] = kind,
            ["slug"] = slug,
            ["ordinal"] = ordinal,
            ["sequence"] = sequence,
            ["title"] = $"Practice {slug}",
            ["summary"] = $"Applies {slug}.",
            ["prerequisites"] = ToArray(prerequisites),
            ["environments"] = new JsonArray("emulator"),
            ["management_labs"] = false,
            ["artifact_status"] = "planned",
            ["outcomes"] = new JsonArray(new JsonObject
            {
                ["id"] = $"outcome.{slug}.primary",
                ["statement"] = $"Build the {slug} system end to end.",
                ["measured_by"] = $"{kind}_tests",
            }),
            ["milestones"] = new JsonArray(
                new JsonObject
                {
                    ["id"] = $"milestone.{slug}.first",
                    ["ordinal"] = 1,
                    ["title"] = "First",
                    ["prerequisites"] = new JsonArray(),
                    ["required_outcome"] = "Define the contracts.",
                },
                new JsonObject
                {
                    ["id"] = $"milestone.{slug}.second",
                    ["ordinal"] = 2,
                    ["title"] = "Second",
                    ["prerequisites"] = new JsonArray($"milestone.{slug}.first"),
                    ["required_outcome"] = "Implement the contracts.",
                }),
            ["evidence"] = Evidence($"{kind}_guide", $"{kind}_tests", $"{kind}_starter"),
        };

        if (kind == "capstone")
        {
            unit["final_destination"] = true;
        }

        return unit;
    }

    private static JsonArray Evidence(string narrativeRole, string practiceRole, string appliedRole) =>
        new(
            Record("named", narrativeRole),
            Record("explained", narrativeRole),
            Record("demonstrated", practiceRole),
            Record("practiced", practiceRole),
            Record("applied", appliedRole));

    private static JsonObject Record(string stage, string role) =>
        new()
        {
            ["stage"] = stage,
            ["status"] = "planned",
            ["artifact"] = new JsonObject { ["role"] = role },
            ["note"] = $"fixture note for {stage}",
        };

    private static JsonArray ToArray(string[] values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }
}
