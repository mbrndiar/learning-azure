using System.Text.Json.Nodes;

namespace LearningAzure.CourseVerifier.Tests;

/// <summary>Proves the verifier accepts a well-formed plan and fails closed on every rule.</summary>
public sealed class CourseVerificationTests
{
    private static void AssertNoFindings(IReadOnlyList<Finding> findings) =>
        Assert.True(findings.Count == 0, string.Join("\n", findings.Select(finding => finding.ToString())));

    private static void AssertFinding(IReadOnlyList<Finding> findings, string fragment) =>
        Assert.True(
            findings.Any(finding => finding.Message.Contains(fragment, StringComparison.Ordinal)),
            $"expected a finding containing '{fragment}', got:\n{string.Join("\n", findings.Select(f => f.ToString()))}");

    [Fact]
    public void BaselineFixtureIsValid()
    {
        using var fixture = FixtureRepository.Create();
        AssertNoFindings(fixture.Verify());
    }

    [Fact]
    public void RejectsUnknownFields()
    {
        using var fixture = FixtureRepository.Create();
        fixture.Unit("module.alpha")["totally_unexpected"] = true;
        AssertFinding(fixture.Verify(), "not valid JSON");
    }

    [Fact]
    public void RejectsPrerequisiteCycle()
    {
        using var fixture = FixtureRepository.Create();
        fixture.Unit("module.alpha")["prerequisites"] = new JsonArray("module.beta");
        AssertFinding(fixture.Verify(), "cycle");
    }

    [Fact]
    public void RejectsUnknownPrerequisite()
    {
        using var fixture = FixtureRepository.Create();
        fixture.Unit("module.beta")["prerequisites"] = new JsonArray("module.nowhere");
        AssertFinding(fixture.Verify(), "unknown prerequisite");
    }

    [Fact]
    public void RejectsPrerequisiteTaughtLater()
    {
        using var fixture = FixtureRepository.Create();
        fixture.Unit("module.alpha")["sequence"] = 2;
        fixture.Unit("module.beta")["sequence"] = 1;
        AssertFinding(fixture.Verify(), "is taught at or after this unit");
    }

    [Fact]
    public void RejectsRedundantPrerequisite()
    {
        using var fixture = FixtureRepository.Create();
        fixture.Unit("project.gamma")["prerequisites"] = new JsonArray("module.alpha", "module.beta");
        AssertFinding(fixture.Verify(), "is redundant");
    }

    [Fact]
    public void RejectsIdentityDerivedFromOrder()
    {
        using var fixture = FixtureRepository.Create();
        fixture.Unit("module.alpha")["id"] = "module.01-alpha";
        var findings = fixture.Verify();
        AssertFinding(findings, "id must be 'module.alpha'");
    }

    [Fact]
    public void RejectsCommitHashInId()
    {
        using var fixture = FixtureRepository.Create();
        var unit = fixture.Unit("module.alpha");
        unit["id"] = "module.9fde6a4";
        unit["slug"] = "9fde6a4";
        AssertFinding(fixture.Verify(), "must not embed a commit hash");
    }

    [Fact]
    public void RejectsUnmeasurableOutcome()
    {
        using var fixture = FixtureRepository.Create();
        fixture.Unit("module.alpha")["outcomes"]![0]!["statement"] = "Understand how blob storage works.";
        var findings = fixture.Verify();
        AssertFinding(findings, "unmeasurable term 'understand'");
        AssertFinding(findings, "must start with a measurable verb");
    }

    [Fact]
    public void AcceptsRecoverWhichMerelyContainsABannedSubstring()
    {
        using var fixture = FixtureRepository.Create();
        fixture.Unit("module.alpha")["outcomes"]![0]!["statement"] = "Implement recovery after a restart.";
        AssertNoFindings(fixture.Verify());
    }

    [Fact]
    public void RejectsOutcomeMeasuredByANonEvaluatorRole()
    {
        using var fixture = FixtureRepository.Create();
        fixture.Unit("module.alpha")["outcomes"]![0]!["measured_by"] = "lesson_readme";
        AssertFinding(fixture.Verify(), "is not a feedback-path role");
    }

    [Fact]
    public void RejectsMissingEvidenceStage()
    {
        using var fixture = FixtureRepository.Create();
        var evidence = fixture.Unit("module.alpha")["evidence"]!.AsArray();
        evidence.RemoveAt(evidence.Count - 1);
        AssertFinding(fixture.Verify(), "exactly one record per stage");
    }

    [Fact]
    public void RejectsCoverageClaimForContentThatDoesNotExist()
    {
        using var fixture = FixtureRepository.Create();
        fixture.Evidence("module.alpha", "explained")["status"] = "covered";
        AssertFinding(fixture.Verify(), "claims coverage but lessons/01-alpha/README.md does not exist");
    }

    [Fact]
    public void RejectsCoverageClaimWithAMissingHeading()
    {
        using var fixture = FixtureRepository.Create();
        fixture.Write("lessons/01-alpha/README.md", "# Module alpha\n\n## Summary\n");
        var record = fixture.Evidence("module.alpha", "named");
        record["status"] = "covered";
        record["artifact"] = new JsonObject { ["role"] = "lesson_readme", ["anchor"] = "objectives" };
        AssertFinding(fixture.Verify(), "no such heading exists");
    }

    [Fact]
    public void AcceptsCoverageClaimWithARealHeading()
    {
        using var fixture = FixtureRepository.Create();
        fixture.Write("lessons/01-alpha/README.md", "# Module alpha\n\n## Objectives\n");
        var record = fixture.Evidence("module.alpha", "named");
        record["status"] = "covered";
        record["artifact"] = new JsonObject { ["role"] = "lesson_readme", ["anchor"] = "objectives" };
        var findings = fixture.Verify();
        Assert.DoesNotContain(findings, finding => finding.Message.Contains("heading", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsPlannedUnitWhoseContentHasSilentlyLanded()
    {
        using var fixture = FixtureRepository.Create();
        fixture.Write("lessons/01-alpha/README.md", "# Module alpha\n");
        AssertFinding(fixture.Verify(), "declared planned but lessons/01-alpha/README.md exists");
    }

    [Fact]
    public void RejectsPresentUnitWhoseContentIsMissing()
    {
        using var fixture = FixtureRepository.Create();
        fixture.Unit("module.alpha")["artifact_status"] = "present";
        var findings = fixture.Verify();
        AssertFinding(findings, "declared present but lessons/01-alpha/README.md does not exist");
        AssertFinding(findings, "declared present but exercises/01-alpha/tests does not exist");
    }

    [Fact]
    public void RejectsPlannedEvidenceOnABuiltUnit()
    {
        using var fixture = FixtureRepository.Create();
        var unit = fixture.Unit("module.alpha");
        unit["artifact_status"] = "present";
        foreach (var path in new[] { "lessons/01-alpha/README.md", "exercises/01-alpha/starter/keep", "exercises/01-alpha/solution/keep", "exercises/01-alpha/tests/keep" })
        {
            fixture.Write(path, "placeholder\n");
        }

        AssertFinding(fixture.Verify(), "must be covered, deferred, or explicitly not applicable");
    }

    [Fact]
    public void RejectsNotApplicableWithoutRationale()
    {
        using var fixture = FixtureRepository.Create();
        var record = fixture.Evidence("module.alpha", "applied");
        record["status"] = "not-applicable";
        AssertFinding(fixture.Verify(), "requires a rationale");
    }

    [Fact]
    public void RejectsDeferralOfANonAppliedStage()
    {
        using var fixture = FixtureRepository.Create();
        var record = fixture.Evidence("module.alpha", "demonstrated");
        record["status"] = "deferred";
        record["deferred_to"] = "project.gamma";
        AssertFinding(fixture.Verify(), "only the applied stage may be deferred");
    }

    [Fact]
    public void RejectsDeferralToAUnitThatIsAlreadyBuilt()
    {
        using var fixture = FixtureRepository.Create();
        foreach (var path in new[] { "projects/gamma/README.md", "projects/gamma/starter/keep", "projects/gamma/solution/keep", "projects/gamma/tests/keep" })
        {
            fixture.Write(path, "placeholder\n");
        }

        var gamma = fixture.Unit("project.gamma");
        gamma["artifact_status"] = "present";
        foreach (var stage in new[] { "named", "explained", "demonstrated", "practiced", "applied" })
        {
            fixture.Evidence("project.gamma", stage)["status"] = "covered";
        }

        var record = fixture.Evidence("module.alpha", "applied");
        record["status"] = "deferred";
        record["deferred_to"] = "project.gamma";
        AssertFinding(fixture.Verify(), "already built, so this stage must be resolved to covered");
    }

    [Fact]
    public void RejectsManifestRegistrationOfAnUnbuiltUnit()
    {
        using var fixture = FixtureRepository.Create();
        fixture.ManifestText = "[course]\nid = \"fixture-course\"\n\n[[modules]]\nid = \"module.alpha\"\n";
        AssertFinding(fixture.Verify(), "while the plan still marks its artifacts as planned");
    }

    [Fact]
    public void RejectsManifestRegistrationOfAnUndeclaredUnit()
    {
        using var fixture = FixtureRepository.Create();
        fixture.ManifestText = "[course]\nid = \"fixture-course\"\n\n[[modules]]\nid = \"module.ghost\"\n";
        AssertFinding(fixture.Verify(), "which the curriculum plan does not declare");
    }

    [Fact]
    public void RejectsABuiltUnitThatIsNotRegistered()
    {
        using var fixture = FixtureRepository.Create();
        var unit = fixture.Unit("module.alpha");
        unit["artifact_status"] = "present";
        foreach (var path in new[] { "lessons/01-alpha/README.md", "exercises/01-alpha/starter/keep", "exercises/01-alpha/solution/keep", "exercises/01-alpha/tests/keep" })
        {
            fixture.Write(path, "placeholder\n");
        }

        AssertFinding(fixture.Verify(), "is built but is not registered for mentor tracking");
    }

    [Fact]
    public void RejectsMismatchedCourseId()
    {
        using var fixture = FixtureRepository.Create();
        fixture.ManifestText = "[course]\nid = \"some-other-course\"\n";
        AssertFinding(fixture.Verify(), "does not match the plan's");
    }

    [Fact]
    public void RejectsMissingRepositoryRole()
    {
        using var fixture = FixtureRepository.Create();
        var roles = fixture.Plan["roles"]!.AsArray();
        roles.RemoveAt(0);
        AssertFinding(fixture.Verify(), "is not mapped");
    }

    [Fact]
    public void RejectsRolePlannedWhenItsPathAlreadyExists()
    {
        using var fixture = FixtureRepository.Create();
        fixture.Write("planned/reference-and-recall", "placeholder\n");
        AssertFinding(fixture.Verify(), "promote the role record");
    }

    [Fact]
    public void RejectsMilestoneOutOfOrder()
    {
        using var fixture = FixtureRepository.Create();
        var milestones = fixture.Unit("project.gamma")["milestones"]!.AsArray();
        milestones[0]!["prerequisites"] = new JsonArray("milestone.gamma.second");
        var findings = fixture.Verify();
        AssertFinding(findings, "is not staged earlier");
        AssertFinding(findings, "cycle");
    }

    [Fact]
    public void RejectsModuleWithMilestones()
    {
        using var fixture = FixtureRepository.Create();
        fixture.Unit("module.alpha")["milestones"] = new JsonArray(new JsonObject
        {
            ["id"] = "milestone.alpha.first",
            ["ordinal"] = 1,
            ["title"] = "First",
            ["prerequisites"] = new JsonArray(),
            ["required_outcome"] = "Do the thing.",
        });
        AssertFinding(fixture.Verify(), "modules are graded by one exercise");
    }

    [Fact]
    public void RejectsCapstoneThatIsNotTaughtLast()
    {
        using var fixture = FixtureRepository.Create();
        fixture.Unit("capstone.delta")["sequence"] = 3;
        fixture.Unit("project.gamma")["sequence"] = 4;
        fixture.Unit("project.gamma")["prerequisites"] = new JsonArray("module.beta");
        fixture.Unit("capstone.delta")["prerequisites"] = new JsonArray("module.beta");
        AssertFinding(fixture.Verify(), "capstone must be taught last");
    }

    [Fact]
    public void RejectsCapstoneThatIsNotTheFinalDestination()
    {
        using var fixture = FixtureRepository.Create();
        fixture.Unit("capstone.delta")["final_destination"] = false;
        AssertFinding(fixture.Verify(), "required final destination");
    }

    [Fact]
    public void RejectsConventionDrift()
    {
        using var fixture = FixtureRepository.Create();
        fixture.Plan["conventions"]!["exercise_root"] = "practice/{slug}";
        AssertFinding(fixture.Verify(), "conventions.exercise_root must be");
    }

    [Fact]
    public void RejectsNarrativeThatOmitsAUnit()
    {
        using var fixture = FixtureRepository.Create();
        fixture.Verify();
        fixture.Write("docs/architecture/curriculum.md", "# empty\n");
        var verification = new CourseVerification(fixture.Root, FixtureRepository.PlanRelativePath);
        verification.Run();
        AssertFinding(verification.Findings, "does not mention 'module.alpha'");
    }

    [Fact]
    public void RejectsAMissingPlan()
    {
        using var fixture = FixtureRepository.Create();
        var verification = new CourseVerification(fixture.Root, "docs/architecture/nowhere.json");
        Assert.Null(verification.Run());
        AssertFinding(verification.Findings, "curriculum plan not found");
    }

    [Theory]
    [InlineData("Objectives", "objectives")]
    [InlineData("## What you will be able to do", "what-you-will-be-able-to-do")]
    [InlineData("Cost & cleanup", "cost--cleanup")]
    public void SlugifyMatchesGitHubAnchors(string heading, string expected) =>
        Assert.Equal(expected, CourseVerification.Slugify(heading.TrimStart('#').Trim()));
}
