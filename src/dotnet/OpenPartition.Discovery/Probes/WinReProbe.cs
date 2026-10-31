using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using OpenPartition.Model;

namespace OpenPartition.Discovery.Probes;

/// <summary>
/// Reads the recovery environment's state with <c>reagentc /info</c>. Discovery only ever runs
/// that one read-only command; turning WinRE off and on around a move is done by
/// OpenPartition.Operations.
/// </summary>
public static partial class WinReProbe
{
    /// <summary>The only argument discovery passes to reagentc.</summary>
    private const string InfoArgument = "/info";

    public static string ReagentcPath => Path.Combine(Environment.SystemDirectory, "reagentc.exe");

    [GeneratedRegex(@"^\s*Windows RE status:\s*(?<value>\S*)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex StatusLine();

    [GeneratedRegex(@"^\s*Windows RE location:[ \t]*(?<value>[^\r\n]*)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex LocationLine();

    [GeneratedRegex(@"^\s*Recovery image location:[ \t]*(?<value>[^\r\n]*)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex ResetImageLine();

    [GeneratedRegex(@"^\s*Boot Configuration Data \(BCD\) identifier:[ \t]*(?<value>[0-9a-fA-F-]{36})", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex BcdLine();

    [GeneratedRegex(@"^\\\\\?\\GLOBALROOT\\device\\harddisk(?<disk>\d+)\\partition(?<partition>\d+)(?:\\|$)", RegexOptions.IgnoreCase)]
    private static partial Regex PartitionPath();

    /// <summary>Runs <c>reagentc /info</c>. Never throws: a failure is reported as <see cref="WinReStatus.Unknown"/>.</summary>
    public static WinReInfo Read()
    {
        try
        {
            var (exitCode, output) = Run(InfoArgument);
            return Parse(exitCode, output);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException or TimeoutException)
        {
            return WinReInfo.Unknown($"reagentc could not be run: {e.Message}");
        }
    }

    /// <summary>
    /// Parses <c>reagentc /info</c> output. Refuses rather than guesses: anything other than a
    /// recognised status, with a partition location when enabled, is Unknown.
    /// </summary>
    public static WinReInfo Parse(int exitCode, string output)
    {
        var lastLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "no output";
        if (exitCode != 0)
        {
            return WinReInfo.Unknown($"reagentc /info failed with exit code {exitCode.ToString(CultureInfo.InvariantCulture)}: {lastLine}");
        }
        var status = StatusLine().Match(output);
        if (!status.Success)
        {
            // The labels are translated on non-English Windows; we don't guess at them.
            return WinReInfo.Unknown("the output of reagentc /info was not recognised (only English output is understood so far)");
        }
        var bcd = BcdLine().Match(output) is { Success: true } b ? b.Groups["value"].Value : null;
        var resetImage = ResetImageLine().Match(output) is { Success: true } r ? Location(r.Groups["value"].Value.Trim()) : null;
        switch (status.Groups["value"].Value.ToUpperInvariant())
        {
            case "DISABLED":
                return new WinReInfo { Status = WinReStatus.Disabled, ResetImage = resetImage };
            case "ENABLED":
                var raw = LocationLine().Match(output) is { Success: true } l ? l.Groups["value"].Value.Trim() : "";
                return Location(raw) is { } location
                    ? new WinReInfo { Status = WinReStatus.Enabled, Location = location, BcdIdentifier = bcd, ResetImage = resetImage }
                    : WinReInfo.Unknown($"WinRE is enabled, but its location '{raw}' is not a partition path reagentc normally reports");
            default:
                return WinReInfo.Unknown($"unrecognised Windows RE status '{status.Groups["value"].Value}'");
        }
    }

    private static WinReLocation? Location(string path) =>
        PartitionPath().Match(path) is { Success: true } m
            ? new WinReLocation(int.Parse(m.Groups["disk"].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups["partition"].Value, CultureInfo.InvariantCulture), path)
            : null;

    private static (int ExitCode, string Output) Run(string argument)
    {
        var start = new ProcessStartInfo(ReagentcPath, argument)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("reagentc.exe did not start.");
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(60_000))
        {
            process.Kill();
            throw new TimeoutException("reagentc /info did not finish within a minute.");
        }
        return (process.ExitCode, stdout + stderr.GetAwaiter().GetResult());
    }
}
