using LearningAzure.Exercises.DataMap;

namespace LearningAzure.Exercises.DataMap.Tests;

/// <summary>
/// Judges the naming and cleanup discipline every later module depends on.
/// </summary>
/// <remarks>
/// These are not style checks. A storage account name that breaks Azure's rules
/// fails at deployment time, a name that loses its uniqueness discriminator
/// collides globally, and a deployment whose resources escape their resource
/// group cannot be deleted in one operation — which is how a course leaves a
/// bill behind.
/// </remarks>
public sealed class ExpeditionNamingTests
{
    private static readonly DateOnly Today = new(2026, 7, 6);

    [Fact]
    public void The_resource_group_is_derived_from_the_expedition_identity()
    {
        Assert.Equal(
            "rg-expedition-north-ridge-dev",
            ExpeditionNaming.ResourceGroup("north-ridge", "dev"));
    }

    [Theory]
    [InlineData("North-Ridge", "uppercase is rejected")]
    [InlineData("north_ridge", "underscores are rejected")]
    [InlineData("-north", "a leading hyphen is rejected")]
    [InlineData("north-", "a trailing hyphen is rejected")]
    [InlineData("n", "a one-character segment is rejected")]
    [InlineData("north-ridge-expedition-alpha", "a segment over 24 characters is rejected")]
    public void An_invalid_expedition_segment_is_rejected_with_an_actionable_message(
        string expedition,
        string because)
    {
        var error = Assert.Throws<ArgumentException>(() => ExpeditionNaming.ResourceGroup(expedition, "dev"));

        Assert.Equal("expedition", error.ParamName);
        Assert.Contains(expedition, error.Message, StringComparison.Ordinal);
        Assert.True(error.Message.Length > 20, because);
    }

    [Fact]
    public void An_invalid_environment_segment_names_the_environment_parameter()
    {
        var error = Assert.Throws<ArgumentException>(
            () => ExpeditionNaming.ResourceGroup("north-ridge", "Production"));

        Assert.Equal("environment", error.ParamName);
    }

    [Fact]
    public void A_storage_account_name_is_lowercase_alphanumeric_and_within_the_azure_limit()
    {
        var name = ExpeditionNaming.StorageAccount("north-ridge", "dev", "k3f9");

        Assert.Equal("stk3f9northridgedev", name);
        Assert.InRange(name.Length, 3, ExpeditionNaming.MaxStorageAccountNameLength);
        Assert.All(name, character => Assert.True(char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character)));
    }

    /// <summary>
    /// Truncation is the interesting case: the name must stay inside the limit
    /// *and* keep the discriminator, or two expeditions collide on a globally
    /// unique name and the second deployment simply fails.
    /// </summary>
    [Fact]
    public void Truncation_never_removes_the_uniqueness_discriminator()
    {
        var name = ExpeditionNaming.StorageAccount("north-ridge-survey", "production", "k3f9a7c1");

        Assert.Equal(ExpeditionNaming.MaxStorageAccountNameLength, name.Length);
        Assert.StartsWith("stk3f9a7c1", name, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("k3f")]
    [InlineData("k3f9a7c1d")]
    [InlineData("K3F9")]
    [InlineData("k3-9")]
    public void An_unusable_discriminator_is_rejected(string discriminator)
    {
        var error = Assert.Throws<ArgumentException>(
            () => ExpeditionNaming.StorageAccount("north-ridge", "dev", discriminator));

        Assert.Equal("discriminator", error.ParamName);
    }

    [Fact]
    public void Required_tags_make_a_deployment_attributable_and_expirable()
    {
        var tags = ExpeditionNaming.RequiredTags("north-ridge", "dev", Today, TimeSpan.FromDays(2));

        Assert.Equal(
            ["environment", "expedition", "expires-on", "managed-by"],
            tags.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("north-ridge", tags["expedition"]);
        Assert.Equal("dev", tags["environment"]);
        Assert.Equal("2026-07-08", tags["expires-on"]);
        Assert.Equal(ExpeditionNaming.ManagedByTagValue, tags["managed-by"]);
    }

    [Fact]
    public void A_partial_day_lifetime_still_expires_after_today()
    {
        var tags = ExpeditionNaming.RequiredTags("north-ridge", "dev", Today, TimeSpan.FromHours(4));

        Assert.Equal("2026-07-07", tags["expires-on"]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_deployment_may_not_be_created_already_expired(int days)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(
            () => ExpeditionNaming.RequiredTags("north-ridge", "dev", Today, TimeSpan.FromDays(days)));

        Assert.Equal("lifetime", error.ParamName);
    }

    [Fact]
    public void One_command_removes_everything_the_deployment_created()
    {
        var group = ExpeditionNaming.ResourceGroup("north-ridge", "dev");

        Assert.Equal(
            "az group delete --name rg-expedition-north-ridge-dev --yes --no-wait",
            ExpeditionNaming.TeardownCommand(group));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_teardown_command_without_a_scope_is_refused(string resourceGroup)
    {
        Assert.Throws<ArgumentException>(() => ExpeditionNaming.TeardownCommand(resourceGroup));
    }
}
