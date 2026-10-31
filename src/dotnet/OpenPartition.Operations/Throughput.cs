using System.Globalization;
using OpenPartition.Model;

namespace OpenPartition.Operations;

/// <summary>
/// How long a move takes: an estimate before it starts, from the read speed <c>opm-engine bench</c>
/// measured, and the live speed and time left while it runs, from the engine's progress lines.
/// </summary>
public static class Throughput
{
    /// <summary>
    /// A verified move reads the data once to fingerprint it, reads and writes it once to copy it,
    /// and reads the copy once to check it: four transfers of every byte that is copied.
    /// </summary>
    public const int MovePasses = 4;

    /// <summary>Locking volumes, updating the partition table and journal: an eight-second allowance per move (VHDX timing includes device re-enumeration).</summary>
    public static readonly TimeSpan MoveOverhead = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Conservative bytes to copy: allow the full range until a locked-volume bitmap is known.
    /// </summary>
    public static long BytesToCopy(SystemSnapshot snapshot, MoveStep step)
    {
        // Discovery free-space counts do not prove that a locked-volume bitmap is available.
        return step.Length;
    }

    /// <summary>Time a verified move of <paramref name="bytesToCopy"/> takes on a disk that reads at <paramref name="readBytesPerSecond"/>.</summary>
    public static TimeSpan EstimateMove(long bytesToCopy, double readBytesPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytesToCopy);
        if (!double.IsFinite(readBytesPerSecond) || readBytesPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(readBytesPerSecond));
        return MoveOverhead + TimeSpan.FromSeconds((double)bytesToCopy * MovePasses / readBytesPerSecond);
    }

    /// <summary>"under a minute", "about 4 minutes", "about 1 h 20 min".</summary>
    public static string FormatDuration(TimeSpan time) => time.TotalSeconds switch
    {
        < 60 => "under a minute",
        < 90 => "about a minute",
        < 3600 => $"about {Math.Round(time.TotalMinutes).ToString(CultureInfo.CurrentCulture)} minutes",
        _ => $"about {(int)time.TotalHours} h {time.Minutes} min",
    };

    public static string FormatRate(double bytesPerSecond) => $"{Units.FormatBytes((long)bytesPerSecond)}/s";

    /// <summary>"142.0 MB/s, about 3 minutes left": what to add to a progress line, or empty if not known yet.</summary>
    public static string Describe(double? bytesPerSecond, TimeSpan? remaining) => (bytesPerSecond, remaining) switch
    {
        ({ } rate, { } left) => $" ({FormatRate(rate)}, {FormatDuration(left)} left)",
        ({ } rate, null) => $" ({FormatRate(rate)})",
        _ => "",
    };

    /// <summary>Transfers per progress byte in a stage: copying reads, writes and reads back; other stages only read.</summary>
    internal static int Weight(string? stage) => stage == "copying" ? 3 : 1;

    /// <summary>The stages of a verified move still to come after <paramref name="stage"/>.</summary>
    internal static IEnumerable<string> StagesAfter(string? stage) => stage switch
    {
        "hashing" => ["copying"],
        "copying" => [],
        _ => [],
    };
}

/// <summary>
/// Live speed and time left of a running move, from its progress lines. The speed is averaged
/// over the last <see cref="Window"/>, so it follows changes without jumping at every chunk.
/// The time left covers the rest of this stage and the stages after it, at the same disk speed.
/// </summary>
public sealed class ThroughputMeter
{
    private readonly Queue<(TimeSpan At, long Done)> samples = new();
    private string? stage;

    public TimeSpan Window { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Record that <paramref name="stage"/> reached <paramref name="done"/> of <paramref name="total"/> bytes at <paramref name="at"/>.</summary>
    public (double? BytesPerSecond, TimeSpan? Remaining) Sample(string? stage, long done, long total, TimeSpan at)
    {
        if (stage != this.stage || (samples.Count > 0 && done < samples.Last().Done))
        {
            samples.Clear();
            this.stage = stage;
        }
        samples.Enqueue((at, done));
        // Keep one sample older than the window, so a stage with slow chunks still has a rate.
        while (samples.Count > 2 && at - samples.ElementAt(1).At >= Window)
        {
            samples.Dequeue();
        }
        var first = samples.Peek();
        var seconds = (at - first.At).TotalSeconds;
        if (samples.Count < 2 || seconds <= 0 || done <= first.Done)
        {
            return (null, null);
        }
        var rate = (done - first.Done) / seconds;
        // The disk's speed in transfers, so later stages (which transfer more or less per byte) can be timed.
        var transfers = rate * Throughput.Weight(stage);
        var left = (total - done) / rate + Throughput.StagesAfter(stage).Sum(s => (double)total * Throughput.Weight(s) / transfers);
        return (rate, TimeSpan.FromSeconds(left));
    }
}
