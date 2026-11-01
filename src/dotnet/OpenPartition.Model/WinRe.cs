namespace OpenPartition.Model;

public enum WinReStatus
{
    /// <summary>reagentc could not be run, failed, or printed something we don't recognise.</summary>
    Unknown,
    Enabled,
    Disabled,
}

/// <summary>A partition named by reagentc as <c>\\?\GLOBALROOT\device\harddiskN\partitionM\...</c>.</summary>
public sealed record WinReLocation(int DiskNumber, int PartitionNumber, string Path);

/// <summary>The running Windows' recovery environment, as <c>reagentc /info</c> reports it.</summary>
public sealed record WinReInfo
{
    public required WinReStatus Status { get; init; }
    /// <summary>Where winre.wim is staged; set when <see cref="Status"/> is Enabled.</summary>
    public WinReLocation? Location { get; init; }
    public string? BcdIdentifier { get; init; }
    /// <summary>Where the system reset (push-button reset) image is registered, if anywhere on a partition.</summary>
    public WinReLocation? ResetImage { get; init; }
    /// <summary>Why <see cref="Status"/> is Unknown.</summary>
    public string? Error { get; init; }

    public static WinReInfo Unknown(string error) => new() { Status = WinReStatus.Unknown, Error = error };

    public string Describe() => Status switch
    {
        WinReStatus.Enabled when Location is { } l => $"enabled, on disk {l.DiskNumber} partition {l.PartitionNumber}",
        WinReStatus.Enabled => "enabled",
        WinReStatus.Disabled => "disabled",
        _ => $"unknown ({Error ?? "no details"})",
    };
}
