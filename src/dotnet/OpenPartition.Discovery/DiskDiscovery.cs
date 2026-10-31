using System.ComponentModel;
using System.Security.Principal;
using OpenPartition.Discovery.Native;
using OpenPartition.Discovery.Probes;
using OpenPartition.Model;

namespace OpenPartition.Discovery;

/// <summary>
/// Read-only discovery of disks, partitions and volumes (plan Phase 1). Uses partition table
/// IOCTLs as the source of truth for the layout, and the Storage Management WMI classes for
/// disk metadata and boot/system flags. Never opens a handle with write access.
/// </summary>
public static class DiskDiscovery
{
    private const int MaxProbedDisks = 64;

    public static async Task<SystemSnapshot> CollectAsync(CancellationToken cancellationToken = default)
    {
        var notes = new List<string>();
        var elevated = IsElevated();

        var environment = EnvironmentProbe.Detect(notes);
        var azure = environment == HostEnvironment.Azure
            ? await EnvironmentProbe.AzureMetadataAsync(notes, cancellationToken).ConfigureAwait(false)
            : null;

        var volumes = VolumeProbe.Enumerate(notes);
        var pageFiles = VolumeUsageProbe.PageFileVolumes(volumes, notes);
        var hibernation = VolumeUsageProbe.HibernationVolumes(volumes);
        var (shadowCopies, shadowStorage) = VolumeUsageProbe.ShadowCopies(notes);
        var bitLocker = VolumeUsageProbe.BitLocker(volumes, elevated, notes);
        var bootVolume = VolumeProbe.VolumeOf(System.Environment.SystemDirectory);

        var volumeInfos = volumes.ToDictionary(v => v.Path, v =>
        {
            var bl = bitLocker.GetValueOrDefault(v.Path) ?? new VolumeUsageProbe.BitLockerInfo(BitLockerState.Unknown, null);
            return new VolumeInfo
            {
                Path = v.Path,
                DriveLetter = v.DriveLetter,
                MountPoints = v.MountPoints,
                FileSystem = v.FileSystem,
                Label = v.Label,
                Capacity = v.Capacity,
                FreeSpace = v.FreeSpace,
                BitLocker = bl.State,
                BitLockerDetail = bl.Detail,
                HasPageFile = pageFiles.Contains(v.Path),
                HasHibernationFile = hibernation.Contains(v.Path),
                HasShadowCopies = shadowCopies.Contains(v.Path),
                HostsShadowStorage = shadowStorage.Contains(v.Path),
                IsSpanned = v.Extents.Count > 1,
                IsAzureTemporaryStorage = environment == HostEnvironment.Azure && EnvironmentProbe.IsAzureTemporaryVolume(v),
            };
        }, StringComparer.OrdinalIgnoreCase);

        // (disk, offset) -> volume, for single-extent volumes and every extent of spanned ones.
        var volumeByExtent = new Dictionary<(int, long), VolumeInfo>();
        foreach (var v in volumes)
        {
            foreach (var extent in v.Extents)
            {
                volumeByExtent[(extent.Disk, extent.Offset)] = volumeInfos[v.Path];
            }
        }

        var wmiDisks = Wmi.Query(Wmi.Storage,
            "SELECT Number, FriendlyName, SerialNumber, BusType, PhysicalSectorSize, IsBoot, IsSystem, IsOffline, IsReadOnly, IsClustered FROM MSFT_Disk");
        var wmiPartitions = Wmi.Query(Wmi.Storage, "SELECT DiskNumber, Offset, IsBoot, IsSystem FROM MSFT_Partition");
        if (wmiDisks.Error is not null)
        {
            notes.Add($"Storage Management WMI unavailable ({wmiDisks.Error}); disk names and boot/system flags may be missing.");
        }
        var diskRows = wmiDisks.Rows
            .Where(r => r.Long("Number") is not null)
            .GroupBy(r => (int)r.Long("Number")!.Value)
            .ToDictionary(g => g.Key, g => g.First());
        var partitionFlags = wmiPartitions.Rows
            .Where(r => r.Long("DiskNumber") is not null && r.Long("Offset") is not null)
            .GroupBy(r => ((int)r.Long("DiskNumber")!.Value, r.Long("Offset")!.Value))
            .ToDictionary(g => g.Key, g => (Boot: g.First().Bool("IsBoot"), System: g.First().Bool("IsSystem")));

        var diskNumbers = diskRows.Count > 0 ? diskRows.Keys.Order().ToList() : Enumerable.Range(0, MaxProbedDisks).ToList();
        var disks = new List<DiskInfo>();
        foreach (var number in diskNumbers)
        {
            var disk = DescribeDisk(number, diskRows.GetValueOrDefault(number), partitionFlags, volumeByExtent, bootVolume, notes, probing: diskRows.Count == 0);
            if (disk is not null)
            {
                disks.Add(disk);
            }
        }

        var issues = Classifier.ForMachine(environment, elevated).ToList();
        issues.AddRange(notes.Select(n => new Issue("discovery.note", IssueSeverity.Info, n)));

        return new SystemSnapshot
        {
            CollectedAt = DateTimeOffset.Now,
            MachineName = System.Environment.MachineName,
            OsVersion = System.Environment.OSVersion.VersionString,
            IsElevated = elevated,
            Environment = environment,
            Azure = azure,
            Disks = disks,
            Issues = issues,
        };
    }

    private static DiskInfo? DescribeDisk(
        int number,
        IReadOnlyDictionary<string, object?>? wmi,
        Dictionary<(int, long), (bool Boot, bool System)> partitionFlags,
        Dictionary<(int, long), VolumeInfo> volumeByExtent,
        string? bootVolume,
        List<string> notes,
        bool probing)
    {
        RawLayout layout;
        int sectorSize;
        long diskSize;
        try
        {
            using var handle = DeviceHandle.OpenForQuery($@"\\.\PhysicalDrive{number}");
            (sectorSize, diskSize) = LayoutParser.ParseGeometry(DeviceHandle.Query(handle, IoctlCodes.DiskGetDriveGeometryEx, 256));
            layout = LayoutParser.ParseLayout(DeviceHandle.Query(handle, IoctlCodes.DiskGetDriveLayoutEx, 64 * 1024));
        }
        catch (Win32Exception e)
        {
            if (!probing)
            {
                notes.Add($"Disk {number}: could not read the partition table ({e.Message}).");
            }
            return null;
        }

        var extended = layout.Partitions.Where(p => p.MbrType is { } t && MbrTypes.IsExtended(t)).ToList();
        var partitions = new List<PartitionInfo>();
        foreach (var raw in layout.Partitions.OrderBy(p => p.Offset))
        {
            var role = raw.GptType is { } gptType ? GptTypes.RoleOf(gptType) : MbrTypes.RoleOf(raw.MbrType ?? 0);
            var isLogical = role != PartitionRole.Extended
                && extended.Any(e => raw.Offset >= e.Offset && raw.Offset + raw.Length <= e.Offset + e.Length);
            var volume = volumeByExtent.GetValueOrDefault((number, raw.Offset));
            var flags = partitionFlags.GetValueOrDefault((number, raw.Offset));
            var partition = new PartitionInfo
            {
                Number = raw.Number,
                Offset = raw.Offset,
                Size = raw.Length,
                Role = role,
                GptType = raw.GptType,
                PartitionGuid = raw.PartitionId,
                GptAttributes = raw.GptAttributes,
                GptName = string.IsNullOrEmpty(raw.GptName) ? null : raw.GptName,
                MbrType = raw.MbrType,
                IsActive = raw.BootIndicator,
                IsLogical = isLogical,
                EbrOffset = isLogical ? raw.EbrOffset : null,
                IsBoot = flags.Boot || (volume is not null && string.Equals(volume.Path, bootVolume, StringComparison.OrdinalIgnoreCase)),
                IsSystem = flags.System || role == PartitionRole.EfiSystem && wmi is null,
                Volume = volume,
            };
            partitions.Add(partition with { Issues = Classifier.ForPartition(partition) });
        }

        var (usableStart, usableEnd) = layout.Style == PartitionStyle.Gpt && layout.GptStartingUsableOffset is { } start && layout.GptUsableLength is { } length
            ? (start, start + length)
            : (sectorSize, diskSize);

        var disk = new DiskInfo
        {
            Number = number,
            FriendlyName = wmi?.String("FriendlyName"),
            SerialNumber = wmi?.String("SerialNumber"),
            BusType = wmi?.Long("BusType") is { } bus ? BusTypeName(bus) : null,
            Size = diskSize,
            LogicalSectorSize = sectorSize,
            PhysicalSectorSize = wmi?.Long("PhysicalSectorSize") is { } physical ? (int)physical : null,
            Style = layout.Style,
            GptDiskId = layout.GptDiskId,
            MbrSignature = layout.MbrSignature,
            UsableStart = usableStart,
            UsableEnd = usableEnd,
            IsBoot = wmi?.Bool("IsBoot") ?? partitions.Any(p => p.IsBoot),
            IsSystem = wmi?.Bool("IsSystem") ?? partitions.Any(p => p.IsSystem),
            IsOffline = wmi?.Bool("IsOffline") ?? false,
            IsReadOnly = wmi?.Bool("IsReadOnly") ?? false,
            IsClustered = wmi?.Bool("IsClustered") ?? false,
            IsAzureResourceDisk = partitions.Any(p => p.Volume?.IsAzureTemporaryStorage == true),
            Partitions = partitions,
            FreeExtents = layout.Style is PartitionStyle.Gpt or PartitionStyle.Mbr ? FreeSpace.Compute(usableStart, usableEnd, partitions) : [],
        };
        return disk with { Issues = Classifier.ForDisk(disk) };
    }

    /// <summary>MSFT_Disk.BusType values.</summary>
    internal static string BusTypeName(long busType) => busType switch
    {
        1 => "SCSI",
        2 => "ATAPI",
        3 => "ATA",
        4 => "1394",
        5 => "SSA",
        6 => "Fibre Channel",
        7 => "USB",
        8 => "RAID",
        9 => "iSCSI",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        14 => "Virtual",
        15 => "File Backed Virtual",
        16 => "Storage Spaces",
        17 => "NVMe",
        18 => "SCM",
        19 => "UFS",
        _ => "Unknown",
    };

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
