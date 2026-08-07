using LearningAzure.Exercises.SecureOperableCloud;

namespace LearningAzure.Exercises.SecureOperableCloud.Tests;

/// <summary>
/// Checks that a composed name survives the service's own rules, that the part
/// which keeps two runs apart is never the part that gets cut, and that the
/// tags a teardown depends on are actually there.
/// </summary>
public sealed class ResourceNamingTests
{
    private const string RunId = "a7f39c";

    [Fact]
    public void Compose_BuildsAStorageAccountNameOutOfLettersAndDigitsOnly()
    {
        var name = ResourceNaming.Compose(AzureService.BlobStorage, "st-Expedition Checkpoint", RunId);

        Assert.True(name.IsValid, name.Violation);
        Assert.StartsWith("stexpedition", name.Name, StringComparison.Ordinal);
        Assert.EndsWith(RunId, name.Name, StringComparison.Ordinal);
        Assert.DoesNotContain("-", name.Name, StringComparison.Ordinal);
        Assert.All(name.Name, character => Assert.True(char.IsAsciiLetterOrDigit(character)));
    }

    [Fact]
    public void Compose_LowerCasesAStorageAccountName()
    {
        var name = ResourceNaming.Compose(AzureService.BlobStorage, "StExpedition", RunId);

        Assert.Equal(name.Name, name.Name.ToLowerInvariant(), StringComparer.Ordinal);
    }

    [Fact]
    public void Compose_KeepsHyphensWhereTheServiceAllowsThem()
    {
        var name = ResourceNaming.Compose(AzureService.EventHubs, "ehns-expedition", RunId);

        Assert.True(name.IsValid, name.Violation);
        Assert.Contains("-", name.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_KeepsTheRunIdWhenTheNameHasToBeTruncated()
    {
        // The whole point of the run id is that two learners in one
        // subscription do not collide. Truncating the tail deletes exactly the
        // characters that were doing that work.
        var name = ResourceNaming.Compose(
            AzureService.BlobStorage,
            "expeditionfieldstationcheckpointstorage",
            RunId);

        Assert.True(name.IsValid, name.Violation);
        Assert.Equal(24, name.Name.Length);
        Assert.EndsWith(RunId, name.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_ProducesDifferentNamesForDifferentRunsOfTheSameLongPrefix()
    {
        const string prefix = "expeditionfieldstationcheckpointstorage";

        var first = ResourceNaming.Compose(AzureService.BlobStorage, prefix, "a7f39c");
        var second = ResourceNaming.Compose(AzureService.BlobStorage, prefix, "b1e402");

        Assert.NotEqual(first.Name, second.Name);
    }

    [Fact]
    public void Compose_PadsANameTheServiceWouldRejectAsTooShort()
    {
        var name = ResourceNaming.Compose(AzureService.EventHubs, "e", "x");

        Assert.True(name.IsValid, name.Violation);
        Assert.True(name.Name.Length >= 6);
    }

    [Fact]
    public void Compose_RefusesARunIdWithNothingTheServiceAccepts()
    {
        var name = ResourceNaming.Compose(AzureService.BlobStorage, "stexpedition", "___");

        Assert.False(name.IsValid);
        Assert.NotNull(name.Violation);
    }

    [Fact]
    public void Compose_RefusesARunIdLongerThanTheWholeName()
    {
        var name = ResourceNaming.Compose(AzureService.BlobStorage, "st", new string('a', 30));

        Assert.False(name.IsValid);
    }

    [Fact]
    public void Compose_RefusesAnEmptyPrefixOrRunId()
    {
        Assert.Throws<ArgumentException>(() => ResourceNaming.Compose(AzureService.BlobStorage, " ", RunId));
        Assert.Throws<ArgumentException>(() => ResourceNaming.Compose(AzureService.BlobStorage, "st", ""));
    }

    [Fact]
    public void Compose_StartsAnEventHubsNamespaceWithALetter()
    {
        var name = ResourceNaming.Compose(AzureService.EventHubs, "9expedition", RunId);

        Assert.True(char.IsLetter(name.Name[0]) || !name.IsValid);
    }

    [Fact]
    public void Compose_NeverEndsANameWithASeparator()
    {
        var name = ResourceNaming.Compose(AzureService.CosmosNoSql, "cosmos-expedition-", RunId);

        Assert.True(name.IsValid, name.Violation);
        Assert.DoesNotContain("--", name.Name, StringComparison.Ordinal);
        Assert.False(name.Name.EndsWith('-'));
    }

    [Theory]
    [InlineData(AzureService.BlobStorage, 3, 24)]
    [InlineData(AzureService.EventHubs, 6, 50)]
    [InlineData(AzureService.CosmosNoSql, 3, 44)]
    public void RuleFor_ReproducesThePublishedLimits(AzureService service, int minimum, int maximum)
    {
        var rule = ResourceNaming.RuleFor(service);

        Assert.Equal(minimum, rule.MinimumLength);
        Assert.Equal(maximum, rule.MaximumLength);
        Assert.True(rule.GloballyUnique);
    }

    [Fact]
    public void RuleFor_GivesBlobsQueuesAndTablesTheSameRule()
    {
        // They are the same storage account, so they cannot have different
        // naming rules.
        Assert.Equal(
            ResourceNaming.RuleFor(AzureService.BlobStorage),
            ResourceNaming.RuleFor(AzureService.QueueStorage));
        Assert.Equal(
            ResourceNaming.RuleFor(AzureService.BlobStorage),
            ResourceNaming.RuleFor(AzureService.TableStorage));
    }

    [Fact]
    public void ValidateTags_AcceptsACompliantResource()
    {
        Assert.Empty(ResourceNaming.ValidateTags(Fixtures.Tags(), new DateOnly(2026, 1, 1)));
    }

    [Fact]
    public void ValidateTags_TreatsNoTagsAsASingleUnambiguousProblem()
    {
        var problems = ResourceNaming.ValidateTags(null, new DateOnly(2026, 1, 1));

        Assert.Single(problems);
    }

    [Fact]
    public void ValidateTags_RejectsAForeignManagedByValue()
    {
        var tags = Fixtures.Tags() with { ManagedBy = "terraform" };

        var problems = ResourceNaming.ValidateTags(tags, new DateOnly(2026, 1, 1));

        Assert.Contains(problems, problem => problem.Contains("managed-by", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateTags_RejectsAnEmptyOwner()
    {
        var problems = ResourceNaming.ValidateTags(Fixtures.Tags() with { Owner = "  " }, new DateOnly(2026, 1, 1));

        Assert.Contains(problems, problem => problem.Contains("owner", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateTags_RejectsAnEmptyPurpose()
    {
        var problems = ResourceNaming.ValidateTags(Fixtures.Tags() with { Purpose = "" }, new DateOnly(2026, 1, 1));

        Assert.Contains(problems, problem => problem.Contains("purpose", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateTags_FlagsAResourceThatOutlivedItsOwnExpiryDate()
    {
        var problems = ResourceNaming.ValidateTags(
            Fixtures.Tags(new DateOnly(2025, 6, 1)),
            new DateOnly(2025, 6, 2));

        Assert.Contains(problems, problem => problem.Contains("2025-06-01", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateTags_AcceptsAResourceExpiringToday()
    {
        Assert.Empty(ResourceNaming.ValidateTags(
            Fixtures.Tags(new DateOnly(2025, 6, 1)),
            new DateOnly(2025, 6, 1)));
    }

    [Fact]
    public void ValidateTags_ReportsEveryProblemAtOnce()
    {
        // One fix per run is a slow way to learn what the tag contract is.
        var tags = new ResourceTags("", "terraform", "", new DateOnly(2020, 1, 1));

        Assert.Equal(4, ResourceNaming.ValidateTags(tags, new DateOnly(2026, 1, 1)).Count);
    }
}
