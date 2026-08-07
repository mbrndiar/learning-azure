namespace LearningAzure.Exercises.StorageAccount;

/// <summary>
/// Records which account behaviors Azurite can prove and which ones only a live
/// checkpoint can.
/// </summary>
/// <remarks>
/// This is not trivia. Treating an emulator result as evidence for a behavior the
/// emulator does not implement is how a design decision gets made on a fact that
/// is not true.
/// </remarks>
public static class EmulatorParity
{
    /// <summary>Reports whether Azurite can be used as evidence for a capability.</summary>
    /// <param name="capability">
    /// One of <c>blob-crud</c>, <c>queue-crud</c>, <c>table-crud</c>, <c>shared-key-auth</c>,
    /// <c>entra-rbac</c>, <c>redundancy</c>, <c>access-tiers</c>, <c>lifecycle-rules</c>,
    /// <c>network-rules</c>, <c>throttling</c>, <c>file-shares</c>.
    /// </param>
    /// <returns><c>true</c> when Azurite implements the capability faithfully enough to rely on.</returns>
    /// <exception cref="ArgumentException">The capability is not one of the listed names.</exception>
    public static bool IsVerifiableLocally(string capability) =>
        // GAP 8 — Encode the parity table.
        //
        // Verifiable locally: blob-crud, queue-crud, table-crud, shared-key-auth.
        // NOT verifiable locally: entra-rbac, redundancy, access-tiers,
        //   lifecycle-rules, network-rules, throttling, file-shares.
        //
        // An unknown capability name is an ArgumentException, not a false. A
        // silent "no" would let a typo quietly downgrade a capability into
        // "needs the live checkpoint" and nobody would notice.
        throw new NotImplementedException(
            "GAP 8: implement EmulatorParity.IsVerifiableLocally. See "
            + "lessons/03-storage-account/README.md#what-azurite-cannot-tell-you.");

    /// <summary>Every capability the live checkpoint exists to confirm.</summary>
    /// <returns>The capability names Azurite cannot verify, in a stable order.</returns>
    public static IReadOnlyList<string> RequiresLiveCheckpoint() =>
        // GAP 9 — Return exactly the capabilities IsVerifiableLocally rejects, in
        // this order: entra-rbac, redundancy, access-tiers, lifecycle-rules,
        // network-rules, throttling, file-shares.
        //
        // Derive it from the same table GAP 8 uses. Two hand-maintained lists
        // drift, and the one that drifts is always the one nobody reads.
        throw new NotImplementedException(
            "GAP 9: implement EmulatorParity.RequiresLiveCheckpoint. See "
            + "lessons/03-storage-account/README.md#what-azurite-cannot-tell-you.");
}
