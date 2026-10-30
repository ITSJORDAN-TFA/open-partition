using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using OpenPartition.Discovery;
using OpenPartition.Model;

namespace OpenPartition.Gui;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

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
    public static readonly Brush Data = Frozen("#2F6FDB");
    public static readonly Brush Boot = Frozen("#1F4FA8");
    public static readonly Brush Efi = Frozen("#138A72");
    public static readonly Brush Msr = Frozen("#8A8F98");
    public static readonly Brush Recovery = Frozen("#7A4FB5");
    public static readonly Brush Unknown = Frozen("#505761");
    public static readonly Brush Free = Frozen("#F2F3F5");
    public static readonly Brush Blocker = Frozen("#B3261E");
    public static readonly Brush Warning = Frozen("#9A6700");
    public static readonly Brush Info = Frozen("#4A5560");

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
    public required Brush Fill { get; init; }
    public bool IsFree { get; init; }
    /// <summary>0..1, or null when unknown.</summary>
    public double? UsedFraction { get; init; }
    public bool HasRefusals { get; init; }
    public bool HasWarnings { get; init; }
    public required string Heading { get; init; }
    public IReadOnlyList<DetailRow> Details { get; init; } = [];
    public IReadOnlyList<IssueRow> Issues { get; init; } = [];
    public string Allowed { get; init; } = "";

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}

public sealed class DiskViewModel
{
    public required string Header { get; init; }
    public required string SubHeader { get; init; }
    public IReadOnlyList<IssueRow> Issues { get; init; } = [];
    public IReadOnlyList<SegmentViewModel> Segments { get; init; } = [];
}

public sealed class MainViewModel : ObservableObject
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private string _status = "Reading disks…";
    private SegmentViewModel? _selected;
    private bool _isBusy;

    public MainViewModel()
    {
        RefreshCommand = new AsyncCommand(RefreshAsync);
    }

    public ObservableCollection<DiskViewModel> Disks { get; } = [];
    public ObservableCollection<IssueRow> Banners { get; } = [];
    public ICommand RefreshCommand { get; }
    public string Title { get; } = $"{ProductInfo.Name} {ProductInfo.Version} — read-only preview";
    public string Disclaimer { get; } = ProductInfo.Disclaimer;

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
            Selected = null;
            Disks.Clear();
            foreach (var disk in snapshot.Disks)
            {
                Disks.Add(Build(disk));
            }
            Banners.Clear();
            foreach (var issue in snapshot.Issues.Where(i => i.Severity != IssueSeverity.Info))
            {
                Banners.Add(IssueRow.From(issue));
            }
            var env = snapshot.Azure is { } a ? $"Azure {a.VmSize} {a.Location}".Trim() : snapshot.Environment.ToString();
            Status = $"{snapshot.MachineName} · {env} · {snapshot.Disks.Count} disk(s) · read at {snapshot.CollectedAt:HH:mm:ss}";
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

    private static DiskViewModel Build(DiskInfo disk)
    {
        var flags = new List<string>();
        if (disk.IsBoot) flags.Add("boot");
        if (disk.IsSystem) flags.Add("system");
        if (disk.IsAzureResourceDisk) flags.Add("Azure temporary disk");
        var sectors = disk.PhysicalSectorSize is { } ps && ps != disk.LogicalSectorSize
            ? $"{disk.LogicalSectorSize}/{ps}"
            : disk.LogicalSectorSize.ToString(Invariant);

        var segments = disk.Partitions.Select(p => (p.Offset, Segment: Partition(disk, p)))
            .Concat(disk.FreeExtents.Select(f => (f.Offset, Segment: Free(disk, f))))
            .OrderBy(x => x.Offset)
            .Select(x => x.Segment)
            .ToList();

        return new DiskViewModel
        {
            Header = $"Disk {disk.Number}  ·  {disk.FriendlyName ?? "Unknown model"}",
            SubHeader = $"{Units.FormatBytes(disk.Size)}  ·  {disk.Style.ToString().ToUpperInvariant()}  ·  {sectors}-byte sectors  ·  {disk.BusType}"
                + (flags.Count > 0 ? "  ·  " + string.Join(", ", flags) : ""),
            Issues = disk.Issues.Select(IssueRow.From).ToList(),
            Segments = segments,
        };
    }

    private static SegmentViewModel Free(DiskInfo disk, FreeExtent f) => new()
    {
        Title = "Free",
        Subtitle = Units.FormatBytes(f.Size),
        Weight = f.Size,
        Fill = Palette.Free,
        IsFree = true,
        Heading = $"Unallocated space on disk {disk.Number}",
        Details =
        [
            new("Starts at", $"{Units.FormatBytes(f.Offset)} ({f.Offset.ToString("N0", Invariant)} bytes)"),
            new("Size", $"{Units.FormatBytes(f.Size)} ({f.Size.ToString("N0", Invariant)} bytes)"),
        ],
        Issues = disk.Issues.Select(IssueRow.From).ToList(),
        Allowed = disk.Issues.Any(i => i.BlocksOperation(OperationKind.Create)) ? "Nothing" : "Create a partition",
    };

    private static SegmentViewModel Partition(DiskInfo disk, PartitionInfo p)
    {
        var v = p.Volume;
        var name = v?.DriveLetter is { } letter ? $"{letter}:" : RoleName(p);
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
        if (v is not null)
        {
            details.Add(new("Volume", v.Path));
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
        var allowed = Enum.GetValues<OperationKind>()
            .Where(k => k is not (OperationKind.None or OperationKind.All or OperationKind.Create))
            .Where(k => disk.Allows(p, k))
            .Select(k => k.ToString())
            .DefaultIfEmpty("Nothing");

        return new SegmentViewModel
        {
            Title = name,
            Subtitle = string.Join("  ", new[] { label, Units.FormatBytes(p.Size), v?.FileSystem }.Where(s => !string.IsNullOrEmpty(s))),
            Weight = p.Size,
            Fill = p.Role switch
            {
                PartitionRole.Data when p.IsBoot => Palette.Boot,
                PartitionRole.Data => Palette.Data,
                PartitionRole.EfiSystem => Palette.Efi,
                PartitionRole.MicrosoftReserved => Palette.Msr,
                PartitionRole.Recovery => Palette.Recovery,
                _ => Palette.Unknown,
            },
            UsedFraction = v is { Capacity: > 0 and var c, FreeSpace: { } f } ? (double)(c - f) / c : null,
            HasRefusals = issues.Any(i => i.Severity == IssueSeverity.Blocker),
            HasWarnings = issues.Any(i => i.Severity == IssueSeverity.Warning),
            Heading = $"Disk {disk.Number}, partition {p.Number}" + (label is null ? "" : $" — {label}"),
            Details = details,
            Issues = issues.Select(IssueRow.From).ToList(),
            Allowed = string.Join(", ", allowed),
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
