using OpenPartition.Model;

namespace OpenPartition.Operations;

/// <summary>
/// Where a queue run stopped, for the caller to report. <paramref name="Lock"/> says who held a
/// volume open when that is why the step failed.
/// </summary>
public sealed record RunResult(int Completed, int Total, PlannedStep? FailedStep, string? Error, bool Stopped = false, LockDiagnosis? Lock = null)
{
    public bool Succeeded => FailedStep is null && !Stopped;
}

/// <summary>
/// Runs queued steps in order. Each step re-checks the write gate against the disk it changes,
/// and the run stops at the first failure: steps already done stay done (each is complete and
/// consistent on its own), and the rest are not attempted.
/// </summary>
public static class Executor
{
    public static async Task<RunResult> RunAsync(
        SystemSnapshot snapshot,
        IReadOnlyList<PlannedStep> steps,
        string? snapshotName,
        IProgress<(int Index, StepProgress Progress)>? progress,
        Func<bool>? stopRequested = null,
        CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            // Only between steps: a move is never interrupted on purpose.
            if (i > 0 && stopRequested?.Invoke() == true)
            {
                return new RunResult(i, steps.Count, null, null, Stopped: true);
            }
            var step = steps[i];
            var index = i;
            var stepProgress = new Progress<StepProgress>(p => progress?.Report((index, p)));
            try
            {
                var disk = snapshot.Disks.FirstOrDefault(d => d.Number == step.DiskNumber)
                    ?? throw new OperationFailedException($"Disk {step.DiskNumber} is no longer there.");
                var verdict = WriteGate.For(snapshot, disk);
                if (verdict == WriteVerdict.Refused || (verdict == WriteVerdict.NeedsSnapshot && string.IsNullOrWhiteSpace(snapshotName)))
                {
                    throw new OperationFailedException(WriteGate.Explain(verdict, disk));
                }
                var name = verdict == WriteVerdict.NeedsSnapshot ? snapshotName : null;
                switch (step)
                {
                    case MoveStep move:
                        await Engine.MoveAsync(move, name, stepProgress, cancellationToken).ConfigureAwait(false);
                        break;
                    case ExtendStep { Container: true } grow:
                        await Engine.GrowExtendedAsync(grow, name, stepProgress, cancellationToken).ConfigureAwait(false);
                        break;
                    case ExtendStep extend:
                        await Resizer.ExtendAsync(extend, stepProgress, cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        throw new OperationFailedException($"Unknown step {step.GetType().Name}.");
                }
            }
            catch (Exception e) when (e is OperationFailedException or System.Management.ManagementException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                var message = e.Message;
                var users = await Task.Run(() => VolumeUsers.Diagnose(message, snapshot), CancellationToken.None).ConfigureAwait(false);
                return new RunResult(i, steps.Count, step, users is null ? message : VolumeUsers.Format(users, StopHint.Command), Lock: users);
            }
        }
        return new RunResult(steps.Count, steps.Count, null, null);
    }
}
