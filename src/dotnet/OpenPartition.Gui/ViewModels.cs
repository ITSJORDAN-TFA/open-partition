using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using OpenPartition.Discovery;
using OpenPartition.Model;
using OpenPartition.Operations;

namespace OpenPartition.Gui;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}

public sealed class AsyncCommand(Func<Task> execute) : ICommand
{
    private bool _running;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_running;

    public async void Execute(object? parameter)
    {
        _running = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await execute();
        }
        finally
        {
            _running = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

public sealed record DetailRow(string Name, string Value);

public sealed record IssueRow(string Severity, string Message, Brush Brush)
{
    public static IssueRow From(Issue issue) => issue.Severity switch
    {
        IssueSeverity.Blocker => new("Refused", issue.Message, Palette.Blocker),
        IssueSeverity.Warning => new("Warning", issue.Message, Palette.Warning),
        _ => new("Note", issue.Message, Palette.Info),
    };
}

internal static class Palette
{
    public static readonly Brush Data = Frozen("#22A06B");
    public static readonly Brush Boot = Frozen("#1F6FEB");
    public static readonly Brush Efi = Frozen("#4F8DF7");
    public static readonly Brush Msr = Frozen("#9AA3AE");
    public static readonly Brush Recovery = Frozen("#8B5CF6");
    public static readonly Brush Extended = Frozen("#6366F1");
    public static readonly Brush Unknown = Frozen("#64748B");
    public static readonly Brush Free = Frozen("#C9D0D8");
    /// <summary>Data partitions take these in turn, so neighbours are told apart at a glance.</summary>
    public static readonly Brush[] DataCycle =
        [Frozen("#22A06B"), Frozen("#3B82F6"), Frozen("#8B5CF6"), Frozen("#14B8A6"), Frozen("#F59E0B"), Frozen("#EC4899")];
    public static readonly Brush Blocker = Frozen("#B3261E");
    public static readonly Brush Warning = Frozen("#9A6700");
    public static readonly Brush Info = Frozen("#4A5560");
    public static readonly Brush Planned = Frozen("#2E7D32");

    /// <summary>The pale tint of <paramref name="accent"/> used for a usage meter's track.</summary>
    public static Brush Tint(Brush accent)
    {
        var c = ((SolidColorBrush)accent).Color;
        var brush = new SolidColorBrush(Color.FromArgb(0x40, c.R, c.G, c.B));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

public sealed class SegmentViewModel : ObservableObject
{
    private bool _isSelected;

    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public required double Weight { get; init; }
    public required Brush Fill { get; set; }

    /// <summary>Give the tile another colour (data partitions take turns in the palette).</summary>
    public void Recolour(Brush fill)
    {
        Fill = fill;
        OnPropertyChanged(nameof(Fill));
        OnPropertyChanged(nameof(Track));
    }

    /// <summary>Pale tint of <see cref="Fill"/>: the usage meter's track.</summary>
    public Brush Track => Palette.Tint(Fill);
    /// <summary>Name shown in the details pane header, e.g. "C: (Windows)".</summary>
    public string DisplayName { get; init; } = "";
    /// <summary>Second and third line of the map tile.</summary>
    public string SizeLine { get; init; } = "";
    public string KindLine { get; init; } = "";
    /// <summary>Roles and state, e.g. "Boot, page file".</summary>
    public string StatusLine { get; init; } = "";
    // Columns of the partition table.
    public string ColumnVolume { get; init; } = "";
    public string ColumnLabel { get; init; } = "";
    public string ColumnFileSystem { get; init; } = "";
    public string ColumnCapacity { get; init; } = "";
    public string ColumnUsed { get; init; } = "";
    public string ColumnFree { get; init; } = "";
    public string ColumnType { get; init; } = "";
    public string ColumnStatus { get; init; } = "";
    public bool IsFree { get; init; }
    /// <summary>0..1, or null when unknown.</summary>
    public double? UsedFraction { get; init; }
    public bool HasRefusals { get; init; }
    public bool HasWarnings { get; init; }
    public required string Heading { get; init; }
    public IReadOnlyList<DetailRow> Details { get; init; } = [];
    public IReadOnlyList<IssueRow> Issues { get; init; } = [];
    /// <summary>Changed by a planned operation (the map shows the disk as it will be).</summary>
    public bool IsPlanned { get; init; }
    public DiskInfo? Disk { get; init; }
    public PartitionInfo? Partition { get; init; }
    public string MoveBlocked { get; init; } = "";
    public string ExtendBlocked { get; init; } = "";
    public bool CanMove => Partition is not null && MoveBlocked.Length == 0;
    public bool CanExtend => Partition is not null && ExtendBlocked.Length == 0;
    public bool IsPartition => Partition is not null;

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}

public sealed class DiskViewModel
{
    public required string Header { get; init; }
    /// <summary>Left block of a disk row: "Disk 0", "Basic (GPT)", size, bus and role.</summary>
    public string Name { get; init; } = "";
    public string StyleLine { get; init; } = "";
    public string SizeLine { get; init; } = "";
    public string StateLine { get; init; } = "";
    public string Preview { get; init; } = "";
    public required string SubHeader { get; init; }
    public IReadOnlyList<IssueRow> Issues { get; init; } = [];
    public IReadOnlyList<SegmentViewModel> Segments { get; init; } = [];
}

/// <summary>A row of the partition table: a disk heading, or one partition or free extent.</summary>
public sealed record TableRow(string? DiskHeading, SegmentViewModel? Segment)
{
    public bool IsHeading => DiskHeading is not null;
    public string Automation => Segment is null ? DiskHeading ?? "" : $"Row: {Segment.Heading}";
}

/// <summary>A summary card above the disk map.</summary>
public sealed record StatCard(string Glyph, Brush Accent, string Value, string Name, string Detail);

/// <summary>A planned change in the queue.</summary>
public sealed record QueueItem(int Number, PlannedStep Step)
{
    public string Text => $"{Number}. {Step.Describe()}";
    public string Description => Step.Describe();
}

/// <summary>An interrupted move found in the journal folder, with the disk it belongs to.</summary>
public sealed record PendingRow(PendingMove Move, DiskInfo? Disk)
{
    public string Text => Disk is null
        ? $"An interrupted move was found, but its disk is not attached ({System.IO.Path.GetFileName(Move.Journal)})."
        : $"A move on disk {Disk.Number} was interrupted ({Units.FormatBytes(Move.Length)} from {Units.FormatBytes(Move.Source)} to {Units.FormatBytes(Move.Target)}, {Move.Chunks} chunks copied). The data is safe, but the partition is offline until the move is finished{(Move.CanUndo ? " or undone" : "")}. Nothing else can be changed on that disk until then.";
    public bool CanResume => Disk is not null;
    public bool CanUndo => Disk is not null && Move.CanUndo;
}

public sealed class MainViewModel : ObservableObject
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private string _status = "Reading disks…";
    private SegmentViewModel? _selected;
    private bool _isBusy;
    private SystemSnapshot? _snapshot;

    public MainViewModel()
    {
        RefreshCommand = new AsyncCommand(RefreshAsync);
    }

    public ObservableCollection<DiskViewModel> Disks { get; } = [];
    public ObservableCollection<IssueRow> Banners { get; } = [];
    public ObservableCollection<QueueItem> Queue { get; } = [];
    public ObservableCollection<PendingRow> Pending { get; } = [];
    public ObservableCollection<StatCard> Stats { get; } = [];
    public ObservableCollection<TableRow> Table { get; } = [];
    public ObservableCollection<DetailRow> SystemInfo { get; } = [];
    public ICommand RefreshCommand { get; }
    public string ProductName { get; } = ProductInfo.Name;
    public int QueueCount => Queue.Count;
    public string QueueSummary => Queue.Count switch
    {
        0 => "No planned changes",
        1 => "1 planned change",
        var n => $"{n} planned changes",
    };
    /// <summary>False while an interrupted move or a machine-wide warning needs attention.</summary>
    public bool AllClear => Pending.Count == 0 && Banners.Count == 0;

    private bool _showPlan;

    /// <summary>Which page the main area shows: the disk overview or the planned changes.</summary>
    public bool ShowPlan
    {
        get => _showPlan;
        set
        {
            if (Set(ref _showPlan, value))
            {
                OnPropertyChanged(nameof(ShowOverview));
            }
        }
    }

    public bool ShowOverview => !_showPlan;
    public string Title { get; } = $"{ProductInfo.Name} {ProductInfo.Version} — preview";
    public string Disclaimer { get; } = ProductInfo.Disclaimer;
    public SystemSnapshot? Snapshot => _snapshot;
    public bool HasQueue => Queue.Count > 0;

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    public SegmentViewModel? Selected
    {
        get => _selected;
        set
        {
            if (_selected is not null)
            {
                _selected.IsSelected = false;
            }
            if (Set(ref _selected, value) && value is not null)
            {
                value.IsSelected = true;
            }
        }
    }

    public async Task RefreshAsync()
    {
        IsBusy = true;
        Status = "Reading disks…";
        try
        {
            // WMI and IOCTLs block; keep them off the UI thread.
            var snapshot = await Task.Run(() => DiskDiscovery.CollectAsync());
            var pending = await Engine.FindPendingAsync();
            _snapshot = snapshot;
            Queue.Clear();
            Pending.Clear();
            foreach (var move in pending)
            {
                Pending.Add(new PendingRow(move, snapshot.Disks.FirstOrDefault(d => Engine.DiskIdOf(d) == move.DiskId && d.Size == move.DiskSize)));
            }
            Banners.Clear();
            foreach (var issue in snapshot.Issues.Where(i => i.Severity != IssueSeverity.Info))
            {
                Banners.Add(IssueRow.From(issue));
            }
            Rebuild();
            BuildSummary(snapshot);
            var env = snapshot.Azure is { } a ? $"Azure {a.VmSize} {a.Location}".Trim() : snapshot.Environment.ToString();
            Status = (AllClear ? "All disks read" : "Disks read; see the notices above")
                + $" · {snapshot.MachineName} · {env} · {snapshot.CollectedAt:HH:mm:ss}";
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Status = $"Discovery failed: {e.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Enqueue(PlannedStep step)
    {
        var original = _snapshot?.Disks.FirstOrDefault(d => d.Number == step.DiskNumber);
        var steps = original is null
            ? [.. Queue.Select(q => q.Step), step]
            : Planner.Append(original, Queue.Select(q => q.Step).ToList(), step);
        Queue.Clear();
        foreach (var s in steps)
        {
            Queue.Add(new QueueItem(Queue.Count + 1, s));
        }
        Rebuild();
    }

    public void UndoLast()
    {
        if (Queue.Count > 0)
        {
            Queue.RemoveAt(Queue.Count - 1);
            Rebuild();
        }
    }

    public void ClearQueue()
    {
        Queue.Clear();
        Rebuild();
    }

    /// <summary>The disks as they will be after every queued step.</summary>
    public IReadOnlyList<DiskInfo> Projected() =>
        _snapshot is null ? [] : _snapshot.Disks.Select(d => Queue.Where(q => q.Step.DiskNumber == d.Number)
            .Aggregate(d, (disk, q) => Planner.Apply(disk, q.Step))).ToList();

    private void Rebuild()
    {
        if (_snapshot is null)
        {
            return;
        }
        Selected = null;
        Disks.Clear();
        Table.Clear();
        foreach (var (original, projected) in _snapshot.Disks.Zip(Projected()))
        {
            var blocked = Pending.Any(p => p.Disk?.Number == original.Number);
            var disk = Build(original, projected, blocked);
            Disks.Add(disk);
            Table.Add(new TableRow($"{disk.Name}  ({disk.StyleLine}, {disk.SizeLine})", null));
            foreach (var segment in disk.Segments)
            {
                Table.Add(new TableRow(null, segment));
            }
        }
        if (Queue.Count == 0)
        {
            ShowPlan = false;
        }
        foreach (var name in new[] { nameof(HasQueue), nameof(QueueCount), nameof(QueueSummary), nameof(AllClear) })
        {
            OnPropertyChanged(name);
        }
    }

    /// <summary>The summary cards and the system information panel, from the disks as they are now.</summary>
    private void BuildSummary(SystemSnapshot snapshot)
    {
        var partitions = snapshot.Disks.SelectMany(d => d.Partitions).Where(p => p.Role != PartitionRole.Extended).ToList();
        var volumes = partitions.Select(p => p.Volume).OfType<VolumeInfo>().Where(v => v.Capacity > 0).ToList();
        long capacity = volumes.Sum(v => v.Capacity ?? 0);
        long free = volumes.Sum(v => v.FreeSpace ?? 0);
        long unallocated = snapshot.Disks.Sum(d => d.FreeExtents.Sum(f => f.Size));
        var fileSystems = volumes.Select(v => v.FileSystem).Where(f => f is not null).Distinct().Count();
        var disksWithSpace = snapshot.Disks.Count(d => d.FreeExtents.Count > 0);
        Stats.Clear();
        Stats.Add(new StatCard("\uEDA2", Palette.Boot, snapshot.Disks.Count.ToString(Invariant), snapshot.Disks.Count == 1 ? "Disk" : "Disks",
            $"{Units.FormatBytes(snapshot.Disks.Sum(d => d.Size))} total"));
        Stats.Add(new StatCard("\uE8A9", Palette.Efi, partitions.Count.ToString(Invariant), "Partitions",
            fileSystems == 1 ? "1 file system" : $"{fileSystems} file systems"));
        Stats.Add(new StatCard("\uEB05", Palette.Data, Units.FormatBytes(capacity - free), "Used space",
            capacity > 0 ? $"{(double)(capacity - free) / capacity:P0} of the volumes" : "no volumes"));
        Stats.Add(new StatCard("\uE9F9", Palette.Free, Units.FormatBytes(unallocated), "Unallocated",
            unallocated == 0 ? "no free space on any disk" : $"on {disksWithSpace} disk(s), ready to use"));

        SystemInfo.Clear();
        SystemInfo.Add(new("Host", snapshot.MachineName));
        SystemInfo.Add(new("OS", snapshot.OsVersion));
        SystemInfo.Add(new("Machine", snapshot.Azure is not null ? "Azure VM" : snapshot.Environment.ToString()));
        if (snapshot.Azure is { } azure)
        {
            SystemInfo.Add(new("Size", azure.VmSize ?? "?"));
            SystemInfo.Add(new("Region", azure.Location ?? "?"));
        }
        SystemInfo.Add(new("Elevated", snapshot.IsElevated ? "Yes" : "No"));
    }

    private static string Key(PartitionInfo p) => p.Volume?.Path ?? $"{p.Number}-{p.Role}-{p.IsLogical}";

    private static DiskViewModel Build(DiskInfo original, DiskInfo disk, bool pendingMove)
    {
        var flags = new List<string>();
        if (disk.IsBoot) flags.Add("boot");
        if (disk.IsSystem) flags.Add("system");
        if (disk.IsAzureResourceDisk) flags.Add("Azure temporary disk");
        var sectors = disk.PhysicalSectorSize is { } ps && ps != disk.LogicalSectorSize
            ? $"{disk.LogicalSectorSize}/{ps}"
            : disk.LogicalSectorSize.ToString(Invariant);
        var before = original.Partitions.ToDictionary(Key, p => (p.Offset, p.Size));
        var changed = disk.Partitions.Count(p => before.TryGetValue(Key(p), out var b) && b != (p.Offset, p.Size));

        var segments = disk.Partitions.Select(p => (p.Offset, Segment: Partition(disk, p,
                planned: before.TryGetValue(Key(p), out var b) && b != (p.Offset, p.Size), pendingMove)))
            .Concat(disk.FreeExtents.Select(f => (f.Offset, Segment: Free(disk, f))))
            .OrderBy(x => x.Offset)
            .Select(x => x.Segment)
            .ToList();

        var colour = 0;
        foreach (var segment in segments.Where(s => s.Partition is { Role: PartitionRole.Data, IsBoot: false }))
        {
            segment.Recolour(Palette.DataCycle[colour++ % Palette.DataCycle.Length]);
        }

        return new DiskViewModel
        {
            Name = $"Disk {disk.Number}",
            StyleLine = $"Basic ({disk.Style.ToString().ToUpperInvariant()})",
            SizeLine = Units.FormatBytes(disk.Size),
            StateLine = string.Join(", ", new[] { disk.BusType ?? "Unknown bus" }.Concat(flags)),
            Header = $"Disk {disk.Number}  ·  {disk.FriendlyName ?? "Unknown model"}",
            Preview = changed > 0 ? $"Preview: shown as it will be after the planned changes ({changed} partition(s) change). Nothing has been changed yet." : "",
            SubHeader = $"{Units.FormatBytes(disk.Size)}  ·  {disk.Style.ToString().ToUpperInvariant()}  ·  {sectors}-byte sectors  ·  {disk.BusType}"
                + (flags.Count > 0 ? "  ·  " + string.Join(", ", flags) : ""),
            Issues = disk.Issues.Select(IssueRow.From).ToList(),
            Segments = segments,
        };
    }

    private static SegmentViewModel Free(DiskInfo disk, FreeExtent f) => new()
    {
        Title = "Unallocated",
        Subtitle = Units.FormatBytes(f.Size),
        DisplayName = "Unallocated space",
        SizeLine = Units.FormatBytes(f.Size),
        KindLine = "Free",
        StatusLine = "Ready to use",
        ColumnVolume = "*",
        ColumnLabel = "Unallocated",
        ColumnFileSystem = "–",
        ColumnCapacity = Units.FormatBytes(f.Size),
        ColumnUsed = "–",
        ColumnFree = Units.FormatBytes(f.Size),
        ColumnType = "–",
        ColumnStatus = "–",
        Weight = f.Size,
        Fill = Palette.Free,
        IsFree = true,
        Disk = disk,
        Heading = $"Unallocated space on disk {disk.Number}",
        Details =
        [
            new("Starts at", $"{Units.FormatBytes(f.Offset)} ({f.Offset.ToString("N0", Invariant)} bytes)"),
            new("Size", $"{Units.FormatBytes(f.Size)} ({f.Size.ToString("N0", Invariant)} bytes)"),
        ],
        Issues = disk.Issues.Select(IssueRow.From).ToList(),
    };

    private static SegmentViewModel Partition(DiskInfo disk, PartitionInfo p, bool planned, bool pendingMove)
    {
        var v = p.Volume;
        var name = p.Role == PartitionRole.Extended ? "Extended"
            : v?.DriveLetter is { } letter ? $"{letter}:"
            : v?.Label is { Length: > 0 } volumeLabel ? volumeLabel
            : RoleName(p);
        var label = v?.Label;
        var details = new List<DetailRow>
        {
            new("Type", RoleName(p) + (p.IsLogical ? " (logical)" : "")),
            new("Starts at", $"{Units.FormatBytes(p.Offset)} ({p.Offset.ToString("N0", Invariant)} bytes)"),
            new("Size", $"{Units.FormatBytes(p.Size)} ({p.Size.ToString("N0", Invariant)} bytes)"),
        };
        if (p.PartitionGuid is { } guid) details.Add(new("Partition GUID", guid.ToString("B")));
        if (p.GptType is { } type) details.Add(new("GPT type", type.ToString("B")));
        if (p.GptAttributes != 0) details.Add(new("GPT attributes", $"0x{p.GptAttributes:X16}"));
        if (p.MbrType is { } mbr) details.Add(new("MBR type", $"0x{mbr:X2}{(p.IsActive ? " (active)" : "")}"));
        if (p.IsBoot) details.Add(new("Role", "Windows (boot) volume"));
        if (p.IsSystem) details.Add(new("Role", "System partition (boot manager)"));
        if (p.Role == PartitionRole.Extended)
        {
            var logicals = Planner.MovingWith(disk, p).Skip(1).Select(Planner.Name).ToList();
            details.Add(new("Holds", logicals.Count == 0 ? "no logical drives" : string.Join(", ", logicals)));
        }
        if (v is not null)
        {
            details.Add(new("Volume", v.Path));
            foreach (var a in v.ShadowStorage) details.Add(new("Shadow storage", $"Source {a.SourceVolume} on {a.StorageVolume}"));
            if (v.DriveLetter is { } l) details.Add(new("Drive letter", $"{l}:"));
            foreach (var m in v.MountPoints) details.Add(new("Mounted at", m));
            details.Add(new("File system", v.FileSystem ?? "none / RAW"));
            if (v.Label is not null) details.Add(new("Label", v.Label));
            if (v.Capacity is { } cap && v.FreeSpace is { } free)
            {
                details.Add(new("Used", $"{Units.FormatBytes(cap - free)} of {Units.FormatBytes(cap)}"));
            }
            details.Add(new("BitLocker", v.BitLocker + (v.BitLockerDetail is null ? "" : $" ({v.BitLockerDetail})")));
        }

        var issues = disk.Issues.Concat(p.Issues).ToList();
        const string Pending = "An interrupted move on this disk must be resumed first (see the banner above).";
        var moveBlocked = pendingMove ? Pending : string.Join(" ", Planner.MoveBlockers(disk, p));
        var extendBlocked = pendingMove ? Pending : string.Join(" ", Planner.ExtendBlockers(disk, p));

        var roles = new List<string>();
        if (p.IsBoot) roles.Add("Boot");
        if (p.IsSystem) roles.Add("System");
        if (p.IsActive) roles.Add("Active");
        if (v?.HasPageFile == true) roles.Add("Page file");
        if (p.IsLogical) roles.Add("Logical drive");
        if (planned) roles.Add("Planned change");
        var used = v is { Capacity: { } capacity, FreeSpace: { } freeSpace } ? (Used: capacity - freeSpace, Free: freeSpace) : default((long Used, long Free)?);
        var typeColumn = p.IsBoot ? "Boot" : p.IsSystem ? "System" : p.IsLogical ? "Logical" : p.Role switch
        {
            PartitionRole.Data => "Primary",
            PartitionRole.MicrosoftReserved => "Reserved",
            _ => RoleName(p),
        };

        return new SegmentViewModel
        {
            DisplayName = v?.DriveLetter is { } dl ? $"{dl}:" + (label is null ? "" : $" ({label})") : label ?? RoleName(p),
            SizeLine = Units.FormatBytes(p.Size),
            KindLine = p.Role == PartitionRole.Extended ? "Extended (MBR)" : v?.FileSystem ?? RoleName(p),
            StatusLine = roles.Count == 0 ? (issues.Any(i => i.Severity == IssueSeverity.Blocker) ? "Some operations refused" : "Healthy") : string.Join(", ", roles),
            ColumnVolume = v?.DriveLetter is { } letterColumn ? $"{letterColumn}:" : "*",
            ColumnLabel = label ?? RoleName(p),
            ColumnFileSystem = v is null ? "–" : v.FileSystem ?? "RAW",
            ColumnCapacity = Units.FormatBytes(p.Size),
            ColumnUsed = used is { } u1 ? Units.FormatBytes(u1.Used) : "–",
            ColumnFree = used is { } u2 ? Units.FormatBytes(u2.Free) : "–",
            ColumnType = typeColumn,
            ColumnStatus = planned ? "Planned" : issues.Any(i => i.Severity == IssueSeverity.Blocker) ? "Restricted" : "Healthy",
            Title = name + (planned ? " ✱" : ""),
            Subtitle = string.Join("  ", new[] { label, Units.FormatBytes(p.Size), v?.FileSystem }.Where(s => !string.IsNullOrEmpty(s))),
            Weight = p.Role == PartitionRole.Extended ? 0 : p.Size,
            Fill = p.Role switch
            {
                PartitionRole.Data when p.IsBoot => Palette.Boot,
                PartitionRole.Data => Palette.Data,
                PartitionRole.EfiSystem => Palette.Efi,
                PartitionRole.MicrosoftReserved => Palette.Msr,
                PartitionRole.Recovery => Palette.Recovery,
                PartitionRole.Extended => Palette.Extended,
                _ => Palette.Unknown,
            },
            UsedFraction = v is { Capacity: > 0 and var c, FreeSpace: { } f } ? (double)(c - f) / c : null,
            HasRefusals = issues.Any(i => i.Severity == IssueSeverity.Blocker),
            HasWarnings = issues.Any(i => i.Severity == IssueSeverity.Warning),
            IsPlanned = planned,
            Disk = disk,
            Partition = p,
            MoveBlocked = moveBlocked,
            ExtendBlocked = extendBlocked,
            Heading = $"Disk {disk.Number}, " + (p.Role == PartitionRole.Extended ? "extended partition" : $"partition {p.Number}") + (label is null ? "" : $" — {label}"),
            Details = details,
            Issues = issues.Select(IssueRow.From).ToList(),
        };
    }

    private static string RoleName(PartitionInfo p) => p.Role switch
    {
        PartitionRole.Data => "Data",
        PartitionRole.EfiSystem => "EFI System",
        PartitionRole.MicrosoftReserved => "Reserved (MSR)",
        PartitionRole.Recovery => "Recovery",
        PartitionRole.DynamicData or PartitionRole.DynamicMetadata => "Dynamic",
        PartitionRole.StorageSpaces => "Storage Spaces",
        PartitionRole.StorageReplica => "Storage Replica",
        PartitionRole.Extended => "Extended",
        PartitionRole.MoveInProgress => "Move in progress",
        _ => "Unknown",
    };
}
