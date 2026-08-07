namespace LearningAzure.CourseVerifier;

/// <summary>Command-line entry point for the course verifier.</summary>
/// <remarks>
/// <para><c>verify</c> — validate the curriculum plan, the repository state it
/// claims, the mentor manifest registration, and the rendered evidence matrix.</para>
/// <para><c>matrix [--write]</c> — render the evidence matrix, optionally
/// updating <c>docs/architecture/evidence-matrix.md</c>.</para>
/// </remarks>
internal static class CommandLine
{
    internal const int ExitOk = 0;
    internal const int ExitFailed = 1;
    internal const int ExitUsage = 2;

    private const string DefaultPlanPath = "docs/architecture/curriculum.json";
    private const string RootMarker = "LearningAzure.slnx";

    internal static int Run(string[] args, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        string? command = null;
        string? repositoryRoot = null;
        var planPath = DefaultPlanPath;
        var write = false;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--repository-root" when index + 1 < args.Length:
                    repositoryRoot = args[++index];
                    break;
                case "--plan" when index + 1 < args.Length:
                    planPath = args[++index];
                    break;
                case "--write":
                    write = true;
                    break;
                case "verify":
                case "matrix":
                    if (command is not null)
                    {
                        error.WriteLine($"course-verifier: unexpected second command '{argument}'");
                        return ExitUsage;
                    }

                    command = argument;
                    break;
                default:
                    error.WriteLine($"course-verifier: unrecognized argument '{argument}'");
                    error.WriteLine("usage: course-verifier <verify|matrix> [--write] [--repository-root PATH] [--plan PATH]");
                    return ExitUsage;
            }
        }

        if (command is null)
        {
            error.WriteLine("usage: course-verifier <verify|matrix> [--write] [--repository-root PATH] [--plan PATH]");
            return ExitUsage;
        }

        var root = repositoryRoot ?? FindRepositoryRoot();
        if (root is null)
        {
            error.WriteLine($"course-verifier: could not locate the repository root ({RootMarker} not found)");
            return ExitUsage;
        }

        var verification = new CourseVerification(root, planPath);
        var plan = verification.Run();

        if (command == "matrix")
        {
            if (plan is null)
            {
                WriteFindings(verification, error);
                return ExitFailed;
            }

            var rendered = MatrixRenderer.Render(plan);
            if (write)
            {
                var target = Path.Combine(root, plan.EvidenceMatrixDocument);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, rendered);
                output.WriteLine($"wrote {plan.EvidenceMatrixDocument}");
                return ExitOk;
            }

            output.Write(rendered);
            return ExitOk;
        }

        if (plan is null)
        {
            WriteFindings(verification, error);
            return ExitFailed;
        }

        var findings = verification.Findings.ToList();
        findings.AddRange(CheckMatrixIsCurrent(root, plan));

        if (findings.Count > 0)
        {
            foreach (var finding in findings)
            {
                error.WriteLine(finding.ToString());
            }

            error.WriteLine($"course-verifier: {CourseVerification.FormatCount(findings.Count)} finding(s)");
            return ExitFailed;
        }

        var built = plan.Units.Count(unit => unit.ArtifactStatus == ArtifactStatuses.Present);
        output.WriteLine(
            $"course-verifier: ok — {CourseVerification.FormatCount(plan.Units.Count)} units, "
            + $"{CourseVerification.FormatCount(plan.Units.Sum(unit => unit.Outcomes.Count))} outcomes, "
            + $"{CourseVerification.FormatCount(built)} built, "
            + $"{CourseVerification.FormatCount(plan.Units.Count - built)} planned");
        return ExitOk;
    }

    private static IEnumerable<Finding> CheckMatrixIsCurrent(string root, CurriculumPlan plan)
    {
        var target = Path.Combine(root, plan.EvidenceMatrixDocument);
        var expected = MatrixRenderer.Render(plan);
        if (!File.Exists(target))
        {
            yield return new Finding(plan.EvidenceMatrixDocument, "the evidence matrix has not been rendered; run 'matrix --write'");
            yield break;
        }

        if (!string.Equals(File.ReadAllText(target), expected, StringComparison.Ordinal))
        {
            yield return new Finding(plan.EvidenceMatrixDocument, "the evidence matrix is stale; run 'matrix --write'");
        }
    }

    private static void WriteFindings(CourseVerification verification, TextWriter error)
    {
        foreach (var finding in verification.Findings)
        {
            error.WriteLine(finding.ToString());
        }
    }

    private static string? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, RootMarker)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, RootMarker)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}

internal static class Program
{
    private static int Main(string[] args) => CommandLine.Run(args, Console.Out, Console.Error);
}
