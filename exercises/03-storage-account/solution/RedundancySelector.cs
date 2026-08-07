namespace LearningAzure.Exercises.StorageAccount;

/// <summary>Chooses the cheapest redundancy that meets a stated durability requirement.</summary>
public static class RedundancySelector
{
    /// <summary>Selects a redundancy option.</summary>
    /// <param name="requirement">What the workload must survive.</param>
    /// <returns>The cheapest option that satisfies every stated need.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="requirement"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The requirement cannot be met in the chosen region.
    /// </exception>
    public static Redundancy Select(DurabilityRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);

        if (requirement.ReadDuringRegionalOutage && !requirement.SurviveRegionLoss)
        {
            throw new ArgumentException(
                "A requirement cannot ask to read a secondary region without surviving the loss of "
                + "the primary; the readable secondary only exists in the geo-redundant options.",
                nameof(requirement));
        }

        if (requirement.SurviveRegionLoss)
        {
            return (requirement.RegionSupportsAvailabilityZones, requirement.ReadDuringRegionalOutage) switch
            {
                (true, true) => Redundancy.ReadAccessGeoZoneRedundant,
                (true, false) => Redundancy.GeoZoneRedundant,
                (false, true) => Redundancy.ReadAccessGeoRedundant,
                (false, false) => Redundancy.GeoRedundant,
            };
        }

        if (requirement.SurviveZoneLoss)
        {
            if (!requirement.RegionSupportsAvailabilityZones)
            {
                // Falling back to LRS here would produce an account that looks
                // compliant on paper and loses data on the failure it was
                // provisioned to survive.
                throw new InvalidOperationException(
                    "Surviving a zone loss requires zone-redundant storage, and the chosen region "
                    + "has no availability zones. Choose a region with zones or accept the risk "
                    + "explicitly by relaxing the requirement.");
            }

            return Redundancy.ZoneRedundant;
        }

        return Redundancy.LocallyRedundant;
    }

    /// <summary>Reports whether an option keeps a readable copy outside the primary region.</summary>
    /// <param name="redundancy">The option to test.</param>
    /// <returns><c>true</c> for the read-access geo options only.</returns>
    public static bool HasReadableSecondary(Redundancy redundancy) =>
        redundancy is Redundancy.ReadAccessGeoRedundant or Redundancy.ReadAccessGeoZoneRedundant;
}
