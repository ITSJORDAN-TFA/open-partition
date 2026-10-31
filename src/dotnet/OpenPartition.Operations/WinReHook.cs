using System.Diagnostics;
using System.Text.Json;
using OpenPartition.Discovery;
using OpenPartition.Discovery.Probes;
using OpenPartition.Model;

namespace OpenPartition.Operations;

public sealed record ReagentcResult(int ExitCode, string Output)
{
    public string LastLine => Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? $"exit code {ExitCode}";
}

/// <summary>WinRE was turned off for a move that has not been turned back on yet.</summary>
public sealed record WinRePending(int DiskNumber, long From, long To, string What);

/// <summary>
/// Keeps the Windows Recovery Environment working when the partition it lives on moves.
/// <para>
/// reagentc and the BCD find winre.wim by disk and partition (ReAgent.xml stores the partition
/// offset; the WinRE boot entry's ramdisk device is a partition element, which is an offset on
/// MBR), and the partition's number can change when the engine takes its entry out of the table
/// and puts it back. So rather than patch those references after the move, this follows
/// Microsoft's own procedure for relocating a recovery partition (KB5028997): turn WinRE off
/// before (<c>reagentc /disable</c> moves winre.wim to C:\Windows\System32\Recovery and removes
/// the boot entry), move, then turn it on (<c>reagentc /enable</c> stages it on the recovery
/// partition again and writes a fresh boot entry), and check <c>reagentc /info</c> shows it
/// enabled on the partition now at the target.
/// </para>
/// </summary>
public sealed class WinReHook(
    Func<string, CancellationToken, Task<ReagentcResult>> reagentc,
    Func<int, long, int?> partitionNumberAt,
    Func<bool> stagedImageExists,
    string? pendingFile)
{
    public static string StagedImage { get; } = Path.Combine(Environment.SystemDirectory, "Recovery", "Winre.wim");

    public static WinReHook Default { get; } = new(RunAsync, DiskDiscovery.PartitionNumberAt, () => File.Exists(StagedImage),
        Path.Combine(Engine.JournalFolder, "winre-pending.json"));

    public static string EnableSteps =>
        "To turn it back on, run 'reagentc /enable' as Administrator, then 'reagentc /info' and check 'Windows RE status' is Enabled. "
        + $"If /enable fails, check that {StagedImage} exists (it is a hidden system file).";

    /// <summary>Added to the error when a move fails after WinRE was turned off.</summary>
    public static string AfterFailedMove =>
        $"The Windows Recovery Environment was turned off before the move; its image is kept safe in {StagedImage}. "
        + "Once the move is finished (Resume) or undone, Open Partition Manager turns it back on; if that doesn't happen, run 'reagentc /enable' as Administrator.";

    /// <summary>
    /// Before the move: check WinRE is still enabled on the partition at <see cref="MoveStep.From"/>
    /// (as when the plan was made), then turn it off. Nothing is changed if a check fails.
    /// </summary>
    public async Task TurnOffAsync(MoveStep step, IProgress<StepProgress>? progress, CancellationToken cancellationToken = default)
    {
        progress?.Report(new StepProgress(null, "Checking the Windows Recovery Environment (reagentc /info)"));
        var before = await InfoAsync(cancellationToken).ConfigureAwait(false);
        var number = partitionNumberAt(step.DiskNumber, step.From);
        if (before.Status != WinReStatus.Enabled || before.Location is not { } location
            || location.DiskNumber != step.DiskNumber || location.PartitionNumber != number)
        {
            throw new OperationFailedException(
                $"The Windows Recovery Environment is no longer where it was when the disks were read (it is now {before.Describe()}). "
                + $"Nothing was changed. Refresh, check 'reagentc /info', and plan the move of {step.What} again.");
        }

        progress?.Report(new StepProgress(null, "Turning the Windows Recovery Environment off for the move (reagentc /disable)"));
        var disable = await reagentc("/disable", cancellationToken).ConfigureAwait(false);
        var after = await InfoAsync(cancellationToken).ConfigureAwait(false);
        if (disable.ExitCode == 0 && after.Status == WinReStatus.Disabled && stagedImageExists())
        {
            SavePending(new WinRePending(step.DiskNumber, step.From, step.To, step.What));
            return;
        }

        var what = $"The Windows Recovery Environment could not be turned off for the move (reagentc /disable: {disable.LastLine}; it is now {after.Describe()}"
            + (stagedImageExists() ? ")" : $", and {StagedImage} is missing)") + $". {step.What} was not moved.";
        if (after.Status != WinReStatus.Disabled)
        {
            throw new OperationFailedException(what + " WinRE was left as it was.");
        }
        // It did turn off, but without its image where we expect it: put it back as it was.
        var enable = await reagentc("/enable", cancellationToken).ConfigureAwait(false);
        var again = await InfoAsync(cancellationToken).ConfigureAwait(false);
        throw new OperationFailedException(again.Status == WinReStatus.Enabled
            ? what + " WinRE was turned back on where it was."
            : what + $" Turning WinRE back on failed too (reagentc /enable: {enable.LastLine}). " + EnableSteps);
    }

    /// <summary>After the move: turn WinRE on and check it is enabled on the partition now at <see cref="MoveStep.To"/>.</summary>
    public Task TurnOnAsync(MoveStep step, IProgress<StepProgress>? progress, CancellationToken cancellationToken = default) =>
        TurnOnAsync(new WinRePending(step.DiskNumber, step.From, step.To, step.What), step.To, progress, cancellationToken);

    /// <summary>
    /// After a move failed with WinRE turned off: if the partition is in the table again (the
    /// engine rolled the move back, or it finished), turn WinRE back on there. Returns what to
    /// tell the user; never throws for a WinRE problem, so the move's own error is kept.
    /// </summary>
    public async Task<string> AfterFailedMoveAsync(MoveStep step, CancellationToken cancellationToken = default)
    {
        try
        {
            var at = partitionNumberAt(step.DiskNumber, step.From) is not null ? step.From
                : partitionNumberAt(step.DiskNumber, step.To) is not null ? step.To
                : (long?)null;
            if (at is null)
            {
                return AfterFailedMove;
            }
            await TurnOnAsync(new WinRePending(step.DiskNumber, step.From, step.To, step.What), at.Value, null, cancellationToken).ConfigureAwait(false);
            return "The Windows Recovery Environment was turned back on.";
        }
        catch (Exception e) when (e is OperationFailedException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return $"The Windows Recovery Environment was turned off before the move and could not be turned back on: {e.Message}";
        }
    }

    /// <summary>The move WinRE was turned off for, if it has not been turned back on.</summary>
    public WinRePending? Pending()
    {
        if (pendingFile is null || !File.Exists(pendingFile))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<WinRePending>(File.ReadAllText(pendingFile));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// After an interrupted move of the WinRE partition has been resumed (<paramref name="undone"/>
    /// false) or undone (true): turn WinRE back on. Does nothing for any other move.
    /// </summary>
    public async Task FinishAfterRecoveryAsync(PendingMove move, int diskNumber, bool undone, IProgress<StepProgress>? progress, CancellationToken cancellationToken = default)
    {
        if (Pending() is not { } pending || pending.DiskNumber != diskNumber || pending.From != move.Source || pending.To != move.Target)
        {
            return;
        }
        await TurnOnAsync(pending, undone ? pending.From : pending.To, progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task TurnOnAsync(WinRePending pending, long at, IProgress<StepProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new StepProgress(null, "Turning the Windows Recovery Environment back on (reagentc /enable)"));
        var enable = await reagentc("/enable", cancellationToken).ConfigureAwait(false);
        var after = await InfoAsync(cancellationToken).ConfigureAwait(false);
        var number = partitionNumberAt(pending.DiskNumber, at);
        if (after.Status == WinReStatus.Enabled && after.Location is { } location
            && location.DiskNumber == pending.DiskNumber && location.PartitionNumber == number)
        {
            ClearPending();
            progress?.Report(new StepProgress(1, $"The Windows Recovery Environment is enabled on {pending.What} ({location.Path})"));
            return;
        }
        if (after.Status == WinReStatus.Enabled)
        {
            // It works, just not from the recovery partition: nothing to retry automatically.
            ClearPending();
            throw new OperationFailedException(
                $"{pending.What} was moved and its data is intact. Windows turned the Recovery Environment back on, but staged it somewhere else ({after.Describe()}, "
                + $"expected disk {pending.DiskNumber} partition {number?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}). WinRE works from there. "
                + "To put it back on the recovery partition: give the partition a temporary drive letter (R:), run 'reagentc /disable', "
                + "'reagentc /setreimage /path R:\\Recovery\\WindowsRE', 'reagentc /enable', check 'reagentc /info', then remove the letter.");
        }
        throw new OperationFailedException(
            $"{pending.What} was moved and its data is intact, but the Windows Recovery Environment could not be turned back on "
            + $"(reagentc /enable: {enable.LastLine}; reagentc /info: {after.Describe()}). Windows has no recovery environment until this is fixed. " + EnableSteps);
    }

    private async Task<WinReInfo> InfoAsync(CancellationToken cancellationToken)
    {
        var info = await reagentc("/info", cancellationToken).ConfigureAwait(false);
        return WinReProbe.Parse(info.ExitCode, info.Output);
    }

    private void SavePending(WinRePending pending)
    {
        if (pendingFile is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(pendingFile)!);
            File.WriteAllText(pendingFile, JsonSerializer.Serialize(pending));
        }
    }

    private void ClearPending()
    {
        if (pendingFile is not null)
        {
            File.Delete(pendingFile);
        }
    }

    private static async Task<ReagentcResult> RunAsync(string argument, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(WinReProbe.ReagentcPath, argument)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(start) ?? throw new OperationFailedException("reagentc.exe could not be started.");
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new ReagentcResult(process.ExitCode, stdout + await stderr.ConfigureAwait(false));
    }
}
