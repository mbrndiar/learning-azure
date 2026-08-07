namespace LearningAzure.Exercises.StorageAccount;

/// <summary>The four data services a general-purpose v2 storage account hosts.</summary>
public enum StorageService
{
    /// <summary>Block, append, and page blobs.</summary>
    Blob,

    /// <summary>Storage queues.</summary>
    Queue,

    /// <summary>Table Storage entities.</summary>
    Table,

    /// <summary>SMB and NFS file shares.</summary>
    File,
}

/// <summary>Where an account lives: the emulator or a real Azure region.</summary>
public enum StorageEnvironment
{
    /// <summary>Azurite, on loopback, with per-service ports and a path-style account.</summary>
    Emulator,

    /// <summary>A real account in the Azure public cloud.</summary>
    LiveAzure,
}

/// <summary>How many copies Azure keeps of every write, and where.</summary>
public enum Redundancy
{
    /// <summary>Three copies in one datacenter. Cheapest; a datacenter loss is a data loss.</summary>
    LocallyRedundant,

    /// <summary>Three copies across three availability zones in one region.</summary>
    ZoneRedundant,

    /// <summary>LRS locally plus an asynchronous copy in the paired region, readable only after failover.</summary>
    GeoRedundant,

    /// <summary>GRS with the secondary region readable at all times.</summary>
    ReadAccessGeoRedundant,

    /// <summary>ZRS locally plus an asynchronous copy in the paired region.</summary>
    GeoZoneRedundant,

    /// <summary>GZRS with the secondary region readable at all times.</summary>
    ReadAccessGeoZoneRedundant,
}

/// <summary>What a workload needs to survive, and what the region can offer.</summary>
/// <param name="SurviveZoneLoss">A single datacenter or availability zone can be lost without data loss.</param>
/// <param name="SurviveRegionLoss">An entire Azure region can be lost without data loss.</param>
/// <param name="ReadDuringRegionalOutage">The secondary copy must be readable before any failover.</param>
/// <param name="RegionSupportsAvailabilityZones">The chosen region actually offers zones.</param>
public sealed record DurabilityRequirement(
    bool SurviveZoneLoss,
    bool SurviveRegionLoss,
    bool ReadDuringRegionalOutage,
    bool RegionSupportsAvailabilityZones);

/// <summary>Blob access tiers, cheapest-at-rest last.</summary>
public enum AccessTier
{
    /// <summary>Highest storage price, lowest access price, no minimum retention.</summary>
    Hot,

    /// <summary>Lower storage price, higher access price, 30-day minimum retention.</summary>
    Cool,

    /// <summary>Lower still, 90-day minimum retention.</summary>
    Cold,

    /// <summary>Cheapest at rest, offline: reads require rehydration measured in hours.</summary>
    Archive,
}

/// <summary>How often an artifact is read after it is written, and how fast a read must be.</summary>
/// <param name="ReadsPerMonth">Expected reads of one artifact per month once it has settled.</param>
/// <param name="MinimumRetentionDays">How long the artifact is kept before it may be deleted.</param>
/// <param name="ReadMustBeImmediate">A read has to complete in milliseconds, not hours.</param>
public sealed record AccessPattern(
    int ReadsPerMonth,
    int MinimumRetentionDays,
    bool ReadMustBeImmediate);

/// <summary>The security-relevant settings of a storage account.</summary>
/// <param name="AllowSharedKeyAccess">Whether the account keys can authorize data-plane requests.</param>
/// <param name="AllowBlobPublicAccess">Whether containers may be configured for anonymous read.</param>
/// <param name="RequireHttpsOnly">Whether plain HTTP is refused.</param>
/// <param name="MinimumTlsVersion">Lowest accepted TLS version, such as <c>TLS1_2</c>.</param>
/// <param name="DefaultNetworkActionIsDeny">Whether the firewall denies by default.</param>
/// <param name="InfrastructureEncryptionEnabled">Whether a second encryption layer is applied.</param>
public sealed record AccountConfiguration(
    bool AllowSharedKeyAccess,
    bool AllowBlobPublicAccess,
    bool RequireHttpsOnly,
    string MinimumTlsVersion,
    bool DefaultNetworkActionIsDeny,
    bool InfrastructureEncryptionEnabled);

/// <summary>One thing an account gets wrong, with the setting that fixes it.</summary>
/// <param name="Setting">The account property at fault.</param>
/// <param name="Consequence">What an attacker or an accident can do because of it.</param>
public sealed record BaselineViolation(string Setting, string Consequence);
