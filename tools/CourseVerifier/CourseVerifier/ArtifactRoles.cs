namespace LearningAzure.CourseVerifier;

/// <summary>Repository role keys and the paths derived from a unit's kind, ordinal, and slug.</summary>
/// <remarks>
/// Paths are derived rather than declared so a unit cannot drift from the
/// starter/solution/shared-evaluator convention documented in
/// <c>docs/architecture/curriculum-plan-schema.md</c>.
/// </remarks>
internal static class ArtifactRoles
{
    internal const string LessonReadme = "lesson_readme";
    internal const string LessonProjectsRoot = "lesson_projects_root";
    internal const string ExerciseStarter = "exercise_starter";
    internal const string ExerciseSolution = "exercise_solution";
    internal const string ExerciseTests = "exercise_tests";
    internal const string CliLab = "cli_lab";
    internal const string PowerShellLab = "powershell_lab";
    internal const string ProjectGuide = "project_guide";
    internal const string ProjectStarter = "project_starter";
    internal const string ProjectSolution = "project_solution";
    internal const string ProjectTests = "project_tests";
    internal const string CapstoneGuide = "capstone_guide";
    internal const string CapstoneStarter = "capstone_starter";
    internal const string CapstoneSolution = "capstone_solution";
    internal const string CapstoneTests = "capstone_tests";

    /// <summary>Returns every path the unit owns, keyed by role.</summary>
    internal static IReadOnlyDictionary<string, string> DerivePaths(CurriculumUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);

        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        switch (unit.Kind)
        {
            case UnitKinds.Module:
                var lessonRoot = $"lessons/{unit.Ordinal:00}-{unit.Slug}";
                var exerciseRoot = $"exercises/{unit.Ordinal:00}-{unit.Slug}";
                paths[LessonProjectsRoot] = lessonRoot;
                paths[LessonReadme] = $"{lessonRoot}/README.md";
                paths[ExerciseStarter] = $"{exerciseRoot}/starter";
                paths[ExerciseSolution] = $"{exerciseRoot}/solution";
                paths[ExerciseTests] = $"{exerciseRoot}/tests";
                if (unit.ManagementLabs)
                {
                    paths[CliLab] = $"infra/azure-cli/{unit.Slug}.sh";
                    paths[PowerShellLab] = $"infra/powershell/{unit.Slug}.ps1";
                }

                break;

            case UnitKinds.Project:
                AddPracticeTree(paths, $"projects/{unit.Slug}", ProjectGuide, ProjectStarter, ProjectSolution, ProjectTests);
                break;

            case UnitKinds.Capstone:
                AddPracticeTree(paths, $"capstones/{unit.Slug}", CapstoneGuide, CapstoneStarter, CapstoneSolution, CapstoneTests);
                break;

            default:
                break;
        }

        return paths;
    }

    private static void AddPracticeTree(
        Dictionary<string, string> paths,
        string root,
        string guide,
        string starter,
        string solution,
        string tests)
    {
        paths[guide] = $"{root}/README.md";
        paths[starter] = $"{root}/starter";
        paths[solution] = $"{root}/solution";
        paths[tests] = $"{root}/tests";
    }

    /// <summary>True when a role key is owned by the single project or the single capstone.</summary>
    internal static bool IsSharedRole(string role) =>
        role.StartsWith("project_", StringComparison.Ordinal)
        || role.StartsWith("capstone_", StringComparison.Ordinal);
}
