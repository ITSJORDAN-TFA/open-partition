using System.Management;

namespace OpenPartition.Operations;

/// <summary>
/// Extends a partition and its file system with Windows' own Storage API (the same call as
/// Resize-Partition and Disk Management's Extend Volume), which is safe on a mounted volume.
/// </summary>
public static class Resizer
{
    private const string Scope = @"root\Microsoft\Windows\Storage";

    public static Task ExtendAsync(ExtendStep step, IProgress<StepProgress>? progress, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            progress?.Report(new StepProgress(null, "Extending with Windows' Storage API"));
            using var partition = Find(step);
            using var supported = partition.InvokeMethod("GetSupportedSize", null, null);
            if (supported?["SizeMax"] is ulong max && (ulong)step.NewSize > max)
            {
                throw new OperationFailedException($"Windows allows {step.What} to grow to {Model.Units.FormatBytes((long)max)}, less than the planned {Model.Units.FormatBytes(step.NewSize)}.");
            }
            using var input = partition.GetMethodParameters("Resize");
            input["Size"] = (ulong)step.NewSize;
            using var output = partition.InvokeMethod("Resize", input, null);
            var code = Convert.ToUInt32(output?["ReturnValue"], System.Globalization.CultureInfo.InvariantCulture);
            if (code != 0)
            {
                throw new OperationFailedException($"Windows refused to extend {step.What} (MSFT_Partition.Resize returned {code}).");
            }
            progress?.Report(new StepProgress(1, "Extended"));
        }, cancellationToken);

    private static ManagementObject Find(ExtendStep step)
    {
        var scope = new ManagementScope(Scope);
        scope.Connect();
        using var searcher = new ManagementObjectSearcher(scope,
            new ObjectQuery(FormattableString.Invariant($"SELECT * FROM MSFT_Partition WHERE DiskNumber = {step.DiskNumber} AND Offset = {step.Offset}")));
        foreach (ManagementObject p in searcher.Get())
        {
            return p;
        }
        throw new OperationFailedException($"{step.What} is no longer at {Model.Units.FormatBytes(step.Offset)} on disk {step.DiskNumber}.");
    }
}
