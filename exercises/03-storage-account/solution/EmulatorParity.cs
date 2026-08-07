using System.Collections.ObjectModel;

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
    /// <summary>
    /// The single parity table. Both public members read it, so they cannot drift
    /// apart the way two hand-maintained lists always do.
    /// </summary>
    private static readonly (string Capability, bool VerifiableLocally)[] Table =
    [
        ("blob-crud", true),
        ("queue-crud", true),
        ("table-crud", true),
        ("shared-key-auth", true),
        ("entra-rbac", false),
        ("redundancy", false),
        ("access-tiers", false),
        ("lifecycle-rules", false),
        ("network-rules", false),
        ("throttling", false),
        ("file-shares", false),
    ];

    /// <summary>Reports whether Azurite can be used as evidence for a capability.</summary>
    /// <param name="capability">A capability name from the parity table.</param>
    /// <returns><c>true</c> when Azurite implements the capability faithfully enough to rely on.</returns>
    /// <exception cref="ArgumentException">The capability is not one of the listed names.</exception>
    public static bool IsVerifiableLocally(string capability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);

        foreach (var (name, verifiable) in Table)
        {
            if (string.Equals(name, capability, StringComparison.Ordinal))
            {
                return verifiable;
            }
        }

        // A silent false would let a typo quietly downgrade a capability into
        // "needs the live checkpoint", and nobody would ever notice.
        throw new ArgumentException(
            $"'{capability}' is not a known storage capability. Known names: "
            + string.Join(", ", Table.Select(entry => entry.Capability)) + ".",
            nameof(capability));
    }

    /// <summary>Every capability the live checkpoint exists to confirm.</summary>
    /// <returns>The capability names Azurite cannot verify, in a stable order.</returns>
    public static IReadOnlyList<string> RequiresLiveCheckpoint() =>
        new ReadOnlyCollection<string>(
            [.. Table.Where(entry => !entry.VerifiableLocally).Select(entry => entry.Capability)]);
}
