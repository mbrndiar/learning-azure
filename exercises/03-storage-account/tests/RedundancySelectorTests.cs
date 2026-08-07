namespace LearningAzure.Exercises.StorageAccount.Tests;

/// <summary>Verifies that redundancy is chosen from the failure it must survive.</summary>
public sealed class RedundancySelectorTests
{
    private static DurabilityRequirement Requirement(
        bool zone = false,
        bool region = false,
        bool read = false,
        bool zonesAvailable = true) =>
        new(zone, region, read, zonesAvailable);

    [Fact]
    public void NoStatedFailureMeansTheCheapestOption()
    {
        Assert.Equal(Redundancy.LocallyRedundant, RedundancySelector.Select(Requirement()));
    }

    [Fact]
    public void SurvivingAZoneLossRequiresZoneRedundantStorage()
    {
        Assert.Equal(Redundancy.ZoneRedundant, RedundancySelector.Select(Requirement(zone: true)));
    }

    [Fact]
    public void AZoneRequirementInARegionWithoutZonesFailsLoudly()
    {
        // Quietly returning LocallyRedundant produces an account that looks
        // compliant and loses data on exactly the failure it was provisioned for.
        var error = Assert.Throws<InvalidOperationException>(
            () => RedundancySelector.Select(Requirement(zone: true, zonesAvailable: false)));

        Assert.Contains("zone", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SurvivingARegionLossWithZonesUsesGeoZoneRedundant()
    {
        Assert.Equal(
            Redundancy.GeoZoneRedundant,
            RedundancySelector.Select(Requirement(region: true, zonesAvailable: true)));
    }

    [Fact]
    public void SurvivingARegionLossWithoutZonesUsesGeoRedundant()
    {
        Assert.Equal(
            Redundancy.GeoRedundant,
            RedundancySelector.Select(Requirement(region: true, zonesAvailable: false)));
    }

    [Fact]
    public void ReadingDuringAnOutageWithZonesUsesReadAccessGeoZoneRedundant()
    {
        Assert.Equal(
            Redundancy.ReadAccessGeoZoneRedundant,
            RedundancySelector.Select(Requirement(region: true, read: true, zonesAvailable: true)));
    }

    [Fact]
    public void ReadingDuringAnOutageWithoutZonesUsesReadAccessGeoRedundant()
    {
        Assert.Equal(
            Redundancy.ReadAccessGeoRedundant,
            RedundancySelector.Select(Requirement(region: true, read: true, zonesAvailable: false)));
    }

    [Fact]
    public void AZoneRequirementDoesNotUpgradeToAGeoOption()
    {
        // Geo-redundancy costs roughly twice as much and replicates across a
        // region boundary that may carry data-residency consequences.
        var selected = RedundancySelector.Select(Requirement(zone: true));

        Assert.False(
            selected is Redundancy.GeoRedundant
                or Redundancy.GeoZoneRedundant
                or Redundancy.ReadAccessGeoRedundant
                or Redundancy.ReadAccessGeoZoneRedundant,
            $"A zone-only requirement must not select {selected}.");
    }

    [Fact]
    public void ReadingASecondaryWithoutSurvivingTheRegionIsIncoherent()
    {
        Assert.Throws<ArgumentException>(
            () => RedundancySelector.Select(Requirement(read: true, region: false)));
    }

    [Fact]
    public void SelectRejectsANullRequirement()
    {
        Assert.Throws<ArgumentNullException>(() => RedundancySelector.Select(null!));
    }

    [Theory]
    [InlineData(Redundancy.ReadAccessGeoRedundant)]
    [InlineData(Redundancy.ReadAccessGeoZoneRedundant)]
    public void OnlyReadAccessOptionsExposeAReadableSecondary(Redundancy redundancy)
    {
        Assert.True(RedundancySelector.HasReadableSecondary(redundancy));
    }

    [Theory]
    [InlineData(Redundancy.LocallyRedundant)]
    [InlineData(Redundancy.ZoneRedundant)]
    [InlineData(Redundancy.GeoRedundant)]
    [InlineData(Redundancy.GeoZoneRedundant)]
    public void ReplicationIsNotReadability(Redundancy redundancy)
    {
        // GRS and GZRS replicate to the paired region, but the copy is unreadable
        // until a failover. "We have GRS" is not an answer to "can we serve reads
        // during an outage".
        Assert.False(RedundancySelector.HasReadableSecondary(redundancy));
    }

    [Fact]
    public void EveryRequirementThatAsksForReadAccessGetsAReadableSecondary()
    {
        var selected = RedundancySelector.Select(Requirement(region: true, read: true));

        Assert.True(RedundancySelector.HasReadableSecondary(selected));
    }
}
