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
    public static Redundancy Select(DurabilityRequirement requirement) =>
        // GAP 3 — Apply the rules in order; the first match wins.
        //
        //   1. ReadDuringRegionalOutage implies SurviveRegionLoss. A requirement
        //      that asks to read the secondary but not to survive the region is
        //      incoherent — throw ArgumentException rather than guessing.
        //   2. SurviveRegionLoss + zones available + read access  -> ReadAccessGeoZoneRedundant
        //   3. SurviveRegionLoss + zones available                -> GeoZoneRedundant
        //   4. SurviveRegionLoss + read access                    -> ReadAccessGeoRedundant
        //   5. SurviveRegionLoss                                  -> GeoRedundant
        //   6. SurviveZoneLoss + zones available                  -> ZoneRedundant
        //   7. SurviveZoneLoss without zones                      -> InvalidOperationException
        //   8. otherwise                                          -> LocallyRedundant
        //
        // Rule 7 is the one that matters. Silently returning LRS for a workload
        // that asked to survive a zone loss produces an account that looks
        // compliant and is not. Fail, and name the region constraint.
        throw new NotImplementedException(
            "GAP 3: implement RedundancySelector.Select. See "
            + "lessons/03-storage-account/README.md#redundancy-is-a-promise-about-failure.");

    /// <summary>Reports whether an option keeps a readable copy outside the primary region.</summary>
    /// <param name="redundancy">The option to test.</param>
    /// <returns><c>true</c> for the read-access geo options only.</returns>
    public static bool HasReadableSecondary(Redundancy redundancy) =>
        // GAP 4 — Only the read-access variants expose a readable secondary
        // endpoint ({account}-secondary.blob.core.windows.net). GRS and GZRS
        // replicate, but the copy is unreadable until Microsoft or you fail over,
        // which is why "we have GRS" is not an answer to "can we serve reads
        // during an outage".
        throw new NotImplementedException(
            "GAP 4: implement RedundancySelector.HasReadableSecondary. See "
            + "lessons/03-storage-account/README.md#redundancy-is-a-promise-about-failure.");
}
