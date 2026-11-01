namespace OpenPartition.Model;

public enum PartitionStyle
{
    Unknown,
    Mbr,
    Gpt,
    Raw,
}

public enum PartitionRole
{
    Data,
    EfiSystem,
    MicrosoftReserved,
    Recovery,
    DynamicMetadata,
    DynamicData,
    StorageSpaces,
    StorageReplica,
    Extended,
    /// <summary>Hidden by Open Partition Manager while it is being moved; recovery must finish it.</summary>
    MoveInProgress,
    Unknown,
}

public enum BitLockerState
{
    /// <summary>Status could not be determined (for example, not running elevated).</summary>
    Unknown,
    NotEncrypted,
    /// <summary>Encrypted, partly encrypted, or encryption/decryption in progress or paused.</summary>
    Encrypted,
}

public enum HostEnvironment
{
    Unknown,
    Physical,
    HyperV,
    Azure,
    OtherVirtual,
}

/// <summary>Byte ranges are half-open: [Offset, Offset + Size).</summary>
public sealed record FreeExtent(long Offset, long Size)
{
    public long End => Offset + Size;
}

public sealed record VolumeInfo
{
    /// <summary>Volume GUID path, e.g. <c>\\?\Volume{...}\</c>.</summary>
    public required string Path { get; init; }
    public char? DriveLetter { get; init; }
    /// <summary>Folder mount points (drive letter roots excluded).</summary>
    public IReadOnlyList<string> MountPoints { get; init; } = [];
    public string? FileSystem { get; init; }
    public string? Label { get; init; }
    public long? Capacity { get; init; }
    public long? FreeSpace { get; init; }
    public BitLockerState BitLocker { get; init; }
    public string? BitLockerDetail { get; init; }
    public bool HasPageFile { get; init; }
    public bool HasHibernationFile { get; init; }
    public bool HasShadowCopies { get; init; }
    /// <summary>This volume stores shadow copies for one or more volumes.</summary>
    public bool HostsShadowStorage { get; init; }
    /// <summary>The volume spans more than one disk extent (dynamic or Storage Spaces).</summary>
    public bool IsSpanned { get; init; }
    public bool IsAzureTemporaryStorage { get; init; }
}

public sealed record PartitionInfo
{
    public required int Number { get; init; }
    public required long Offset { get; init; }
    public required long Size { get; init; }
    public required PartitionRole Role { get; init; }
    public Guid? GptType { get; init; }
    public Guid? PartitionGuid { get; init; }
    public ulong GptAttributes { get; init; }
    public string? GptName { get; init; }
    public byte? MbrType { get; init; }
    public bool IsActive { get; init; }
    /// <summary>MBR logical partition inside an extended partition.</summary>
    public bool IsLogical { get; init; }
    /// <summary>For a logical drive: where its EBR (the table describing it) sits. The first
    /// logical drive's EBR is at the start of the extended partition; the others move with
    /// their drive.</summary>
    public long? EbrOffset { get; init; }
    /// <summary>Holds the running Windows installation.</summary>
    public bool IsBoot { get; init; }
    /// <summary>Holds the boot manager (EFI System partition or active MBR partition).</summary>
    public bool IsSystem { get; init; }
    public VolumeInfo? Volume { get; init; }
    public IReadOnlyList<Issue> Issues { get; init; } = [];

    public long End => Offset + Size;
}

public sealed record DiskInfo
{
    public required int Number { get; init; }
    public string? FriendlyName { get; init; }
    public string? SerialNumber { get; init; }
    public string? BusType { get; init; }
    public required long Size { get; init; }
    public required int LogicalSectorSize { get; init; }
    public int? PhysicalSectorSize { get; init; }
    public required PartitionStyle Style { get; init; }
    public Guid? GptDiskId { get; init; }
    public uint? MbrSignature { get; init; }
    /// <summary>First byte that may hold a partition (GPT: after the primary table).</summary>
    public long UsableStart { get; init; }
    /// <summary>End of the usable area (GPT: before the backup table).</summary>
    public long UsableEnd { get; init; }
    public bool IsBoot { get; init; }
    public bool IsSystem { get; init; }
    public bool IsOffline { get; init; }
    public bool IsReadOnly { get; init; }
    public bool IsClustered { get; init; }
    public bool IsAzureResourceDisk { get; init; }
    public IReadOnlyList<PartitionInfo> Partitions { get; init; } = [];
    public IReadOnlyList<FreeExtent> FreeExtents { get; init; } = [];
    public IReadOnlyList<Issue> Issues { get; init; } = [];
}

public sealed record AzureInfo
{
    public string? VmId { get; init; }
    public string? VmName { get; init; }
    public string? VmSize { get; init; }
    public string? Location { get; init; }
    public string? ResourceGroup { get; init; }
}

public sealed record SystemSnapshot
{
    public required DateTimeOffset CollectedAt { get; init; }
    public required string MachineName { get; init; }
    public required string OsVersion { get; init; }
    public required bool IsElevated { get; init; }
    public HostEnvironment Environment { get; init; }
    public AzureInfo? Azure { get; init; }
    public IReadOnlyList<DiskInfo> Disks { get; init; } = [];
    /// <summary>Machine-wide notes, plus anything discovery could not determine.</summary>
    public IReadOnlyList<Issue> Issues { get; init; } = [];
}
