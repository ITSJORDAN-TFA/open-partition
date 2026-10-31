using OpenPartition.Model;

namespace OpenPartition.Operations;

public enum WriteVerdict
{
    /// <summary>A test virtual disk (VHD/VHDX): always allowed.</summary>
    Allowed,
    /// <summary>A real disk on an Azure VM: allowed once the user names the snapshot they took.</summary>
    NeedsSnapshot,
    /// <summary>Not allowed in this version.</summary>
    Refused,
}

/// <summary>
/// Which disks this version may change (compatibility matrix option A). The engine enforces the
/// same rule itself for moves, from SMBIOS; this is the GUI/CLI side of it, and the only check
/// for extends, which Windows performs.
/// </summary>
public static class WriteGate
{
    public const string VirtualBusType = "File Backed Virtual";

    public static WriteVerdict For(SystemSnapshot snapshot, DiskInfo disk) =>
        string.Equals(disk.BusType, VirtualBusType, StringComparison.OrdinalIgnoreCase) ? WriteVerdict.Allowed
        : snapshot.Environment == HostEnvironment.Azure ? WriteVerdict.NeedsSnapshot
        : WriteVerdict.Refused;

    public static string Explain(WriteVerdict verdict, DiskInfo disk) => verdict switch
    {
        WriteVerdict.Allowed => $"Disk {disk.Number} is a test virtual disk.",
        WriteVerdict.NeedsSnapshot => $"Disk {disk.Number} is a real Azure disk. Take a snapshot of it in the Azure portal first, then name that snapshot here.",
        _ => $"Disk {disk.Number} is a real disk and this machine is not an Azure VM. This version only changes real disks on Azure VMs, after a snapshot.",
    };
}
