using OpenPartition.Model;

namespace OpenPartition.Discovery;

/// <summary>
/// Turns discovered facts into refusals and warnings (handover section 7: refuse unsupported
/// layouts, never "try anyway"). Pure functions, so every rule is unit tested.
/// </summary>
public static class Classifier
{
    private const OperationKind DestructiveOrMoving =
        OperationKind.Move | OperationKind.Delete | OperationKind.Format;

    public static IReadOnlyList<Issue> ForDisk(DiskInfo disk)
    {
        var issues = new List<Issue>();
        if (disk.Partitions.Any(p => p.Role is PartitionRole.DynamicData or PartitionRole.DynamicMetadata))
        {
            issues.Add(new("disk.dynamic", IssueSeverity.Blocker,
                "Dynamic disk. Dynamic disks are not supported; manage them with Disk Management.", OperationKind.All));
        }
        if (string.Equals(disk.BusType, "Storage Spaces", StringComparison.OrdinalIgnoreCase)
            || disk.Partitions.Any(p => p.Role == PartitionRole.StorageSpaces))
        {
            issues.Add(new("disk.storage-spaces", IssueSeverity.Blocker,
                "Part of Storage Spaces. Manage it with Storage Spaces tools instead.", OperationKind.All));
        }
        if (disk.Partitions.Any(p => p.Role == PartitionRole.StorageReplica))
        {
            issues.Add(new("disk.storage-replica", IssueSeverity.Blocker,
                "Used by Storage Replica. Not supported.", OperationKind.All));
        }
        if (disk.IsClustered)
        {
            issues.Add(new("disk.clustered", IssueSeverity.Blocker,
                "Clustered disk. Changes must go through Failover Cluster Manager.", OperationKind.All));
        }
        if (disk.IsOffline)
        {
            issues.Add(new("disk.offline", IssueSeverity.Blocker,
                "Disk is offline. Bring it online to see or change its volumes.", OperationKind.All));
        }
        if (disk.IsReadOnly)
        {
            issues.Add(new("disk.read-only", IssueSeverity.Blocker, "Disk is read-only.", OperationKind.All));
        }
        if (disk.Style is PartitionStyle.Raw or PartitionStyle.Unknown)
        {
            issues.Add(new("disk.uninitialized", IssueSeverity.Info,
                "Disk is not initialised (no partition table). Initialise it in Disk Management first.", OperationKind.All));
        }
        if (disk.IsAzureResourceDisk)
        {
            issues.Add(new("azure.temporary-disk", IssueSeverity.Warning,
                "Azure temporary (resource) disk. Its contents are lost when the VM is redeployed, resized or moved to another host. Never keep data here that you need."));
        }
        return issues;
    }

    public static IReadOnlyList<Issue> ForPartition(PartitionInfo p)
    {
        var issues = new List<Issue>();
        var v = p.Volume;

        switch (p.Role)
        {
            case PartitionRole.EfiSystem:
                issues.Add(new("partition.efi", IssueSeverity.Blocker,
                    "EFI System partition. The machine cannot boot without it; it is never changed.", OperationKind.All));
                break;
            case PartitionRole.MicrosoftReserved:
                issues.Add(new("partition.msr", IssueSeverity.Blocker,
                    "Microsoft Reserved partition. Windows manages it; it is never changed.", OperationKind.All));
                break;
            case PartitionRole.Recovery:
                issues.Add(new("partition.recovery", IssueSeverity.Blocker,
                    "Windows Recovery partition. Changing it is not supported in this version; partitions around it can still be changed.", OperationKind.All));
                break;
            case PartitionRole.Extended:
                issues.Add(new("partition.extended", IssueSeverity.Blocker,
                    "MBR extended partition (a container for logical drives). Not supported.", OperationKind.All));
                break;
            case PartitionRole.MoveInProgress:
                issues.Add(new("partition.move-in-progress", IssueSeverity.Blocker,
                    "A move of this partition was interrupted. It is hidden from Windows until the move is recovered; do not delete or format it.", OperationKind.All));
                break;
            case PartitionRole.Unknown:
                issues.Add(new("partition.unknown-type", IssueSeverity.Blocker,
                    "Unrecognised partition type, possibly used by another operating system. It is left alone.", OperationKind.All));
                break;
            case PartitionRole.DynamicData or PartitionRole.DynamicMetadata or PartitionRole.StorageSpaces or PartitionRole.StorageReplica:
                // Covered by the disk-level blocker.
                break;
        }

        if ((p.GptAttributes & GptAttributes.PlatformRequired) != 0)
        {
            issues.Add(new("partition.platform-required", IssueSeverity.Blocker,
                "Marked as required by the platform (GPT attribute). It is never changed.", OperationKind.All));
        }
        if (p.IsLogical)
        {
            issues.Add(new("partition.logical", IssueSeverity.Blocker,
                "Logical drive inside an MBR extended partition. Moving logical drives is not supported.", OperationKind.Move));
        }
        if (p.IsSystem && p.Role != PartitionRole.EfiSystem)
        {
            issues.Add(new("partition.system", IssueSeverity.Blocker,
                "System partition: holds the boot manager. Moving it needs the boot-time runner (not available yet).", DestructiveOrMoving));
        }
        if (p.IsBoot)
        {
            issues.Add(new("partition.boot", IssueSeverity.Blocker,
                "Windows is running from this volume. It can be extended or shrunk online; moving it needs the boot-time runner (not available yet).", DestructiveOrMoving));
        }

        if (v is null)
        {
            if (p.Role == PartitionRole.Data)
            {
                issues.Add(new("volume.none", IssueSeverity.Info, "No volume is mounted from this partition."));
            }
            return issues;
        }

        if (v.IsSpanned)
        {
            issues.Add(new("volume.spanned", IssueSeverity.Blocker,
                "Volume spans several disk extents (dynamic or Storage Spaces volume). Not supported.", OperationKind.All));
        }
        if (v.HasPageFile)
        {
            issues.Add(new("volume.page-file", IssueSeverity.Blocker,
                "Holds a page file, so it cannot be locked while Windows runs. Moving it needs the boot-time runner (not available yet).", DestructiveOrMoving));
        }
        if (v.HasHibernationFile)
        {
            issues.Add(new("volume.hibernation-file", IssueSeverity.Blocker,
                "Holds hiberfil.sys. Turn hibernation off (powercfg /h off) before moving it.", DestructiveOrMoving));
        }
        switch (v.BitLocker)
        {
            case BitLockerState.Encrypted:
                issues.Add(new("volume.bitlocker", IssueSeverity.Blocker,
                    $"BitLocker is on ({v.BitLockerDetail ?? "encrypted"}). Decrypt the volume before moving it.", OperationKind.Move));
                break;
            case BitLockerState.Unknown:
                issues.Add(new("volume.bitlocker-unknown", IssueSeverity.Blocker,
                    $"BitLocker status could not be determined{(v.BitLockerDetail is null ? "" : $" ({v.BitLockerDetail})")}. Moving is refused until it is known.", OperationKind.Move));
                break;
        }
        if (string.Equals(v.FileSystem, "ReFS", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new("volume.refs", IssueSeverity.Blocker,
                "ReFS volume. ReFS cannot be shrunk, and moving ReFS volumes is not supported.", OperationKind.Move | OperationKind.Shrink));
        }
        if (v.HasShadowCopies)
        {
            issues.Add(new("volume.shadow-copies", IssueSeverity.Blocker,
                "Has Volume Shadow Copies (previous versions / restore points). Moving is refused while they exist.", OperationKind.Move));
        }
        if (v.HostsShadowStorage)
        {
            issues.Add(new("volume.shadow-storage", IssueSeverity.Blocker,
                "Stores shadow copies for one or more volumes. Moving is refused while it does.", OperationKind.Move));
        }
        if (v.FileSystem is null && p.Role == PartitionRole.Data)
        {
            issues.Add(new("volume.raw", IssueSeverity.Info, "No recognised file system (RAW or not formatted)."));
        }
        if (v.IsAzureTemporaryStorage)
        {
            issues.Add(new("azure.temporary-volume", IssueSeverity.Warning,
                "Azure temporary storage: wiped on redeploy, resize or host maintenance."));
        }
        return issues;
    }

    public static IReadOnlyList<Issue> ForMachine(HostEnvironment environment, bool elevated)
    {
        var issues = new List<Issue>();
        if (environment == HostEnvironment.Azure)
        {
            issues.Add(new("azure.snapshot", IssueSeverity.Warning,
                "Running on Azure. Take a snapshot of every managed disk you are going to change before you start."));
        }
        if (!elevated)
        {
            issues.Add(new("machine.not-elevated", IssueSeverity.Warning,
                "Not running as Administrator. BitLocker status and some volume details cannot be read, so moves would be refused."));
        }
        return issues;
    }
}
