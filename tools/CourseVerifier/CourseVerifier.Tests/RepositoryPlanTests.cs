namespace LearningAzure.CourseVerifier.Tests;

/// <summary>
/// Checks the shipped curriculum plan itself, so a real drift between the plan,
/// the repository, the mentor manifest, and the rendered matrix fails the build.
/// </summary>
public sealed class RepositoryPlanTests
{
    private static string RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LearningAzure.slnx")))
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);
            return directory!.FullName;
        }
    }

    [Fact]
    public void ShippedPlanVerifiesCleanly()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exitCode = CommandLine.Run(["verify", "--repository-root", RepositoryRoot], output, error);
        Assert.True(exitCode == CommandLine.ExitOk, error.ToString());
        Assert.Contains("ok", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ShippedPlanCoversTwelveModulesOneProjectAndOneCapstone()
    {
        var plan = PlanLoader.Load(Path.Combine(RepositoryRoot, "docs/architecture/curriculum.json"));
        Assert.Equal(12, plan.Units.Count(unit => unit.Kind == UnitKinds.Module));
        Assert.Equal(1, plan.Units.Count(unit => unit.Kind == UnitKinds.Project));
        Assert.Equal(1, plan.Units.Count(unit => unit.Kind == UnitKinds.Capstone));
    }

    // The shipped plan is promoted one stage at a time, so this asserts the
    // invariant rather than a snapshot of how far the build has got: a unit's
    // declared status and the files on disk agree in both directions.
    [Fact]
    public void EveryUnitStatusMatchesWhatIsOnDisk()
    {
        var plan = PlanLoader.Load(Path.Combine(RepositoryRoot, "docs/architecture/curriculum.json"));
        Assert.All(plan.Units, unit =>
        {
            var shouldExist = unit.ArtifactStatus == ArtifactStatuses.Present;
            Assert.All(ArtifactRoles.DerivePaths(unit), entry =>
            {
                var absolute = Path.Combine(RepositoryRoot, entry.Value);
                var exists = File.Exists(absolute) || Directory.Exists(absolute);
                Assert.True(
                    exists == shouldExist,
                    $"{unit.Id}.{entry.Key}: plan says {unit.ArtifactStatus} but {entry.Value} " +
                    (exists ? "exists" : "does not exist"));
            });
        });
    }

    [Fact]
    public void NoPlannedUnitClaimsEvidenceCoverage()
    {
        var plan = PlanLoader.Load(Path.Combine(RepositoryRoot, "docs/architecture/curriculum.json"));
        Assert.All(
            plan.Units.Where(unit => unit.ArtifactStatus == ArtifactStatuses.Planned)
                .SelectMany(unit => unit.Evidence),
            record => Assert.Equal(EvidenceStatuses.Planned, record.Status));
    }

    // A built unit must not leave a stage sitting at 'planned'; the applied
    // stage is allowed to be deferred to the project that has not been written.
    [Fact]
    public void NoBuiltUnitLeavesAStagePlanned()
    {
        var plan = PlanLoader.Load(Path.Combine(RepositoryRoot, "docs/architecture/curriculum.json"));
        Assert.All(
            plan.Units.Where(unit => unit.ArtifactStatus == ArtifactStatuses.Present)
                .SelectMany(unit => unit.Evidence),
            record => Assert.NotEqual(EvidenceStatuses.Planned, record.Status));
    }

    [Fact]
    public void RenderedMatrixIsCurrent()
    {
        var plan = PlanLoader.Load(Path.Combine(RepositoryRoot, "docs/architecture/curriculum.json"));
        var expected = MatrixRenderer.Render(plan);
        var actual = File.ReadAllText(Path.Combine(RepositoryRoot, plan.EvidenceMatrixDocument));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void UnknownArgumentsFailWithUsage()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(CommandLine.ExitUsage, CommandLine.Run(["explode"], output, error));
        Assert.Contains("usage:", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void MatrixCommandRendersWithoutWriting()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(CommandLine.ExitOk, CommandLine.Run(["matrix", "--repository-root", RepositoryRoot], output, error));
        Assert.Contains("Curriculum evidence matrix", output.ToString(), StringComparison.Ordinal);
    }
}
