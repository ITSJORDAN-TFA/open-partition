using OpenPartition.Discovery.Probes;
using OpenPartition.Model;

namespace OpenPartition.Tests;

public class WinReProbeTests
{
    /// <summary>Real <c>reagentc /info</c> output (Windows 11 24H2).</summary>
    internal const string Enabled = """

        Windows Recovery Environment (Windows RE) and system reset configuration
        Information:

            Windows RE status:         Enabled
            Windows RE location:       \\?\GLOBALROOT\device\harddisk2\partition3\Recovery\WindowsRE
            Boot Configuration Data (BCD) identifier: 65dfebdb-3ee2-11f0-bf06-b76f1e1a4c90
            Recovery image location:
            Recovery image index:      0
            Custom image location:
            Custom image index:        0
            Windows RE Version:        10.0.26100.9711

        REAGENTC.EXE: Operation Successful.

        """;

    internal const string Disabled = """

        Windows Recovery Environment (Windows RE) and system reset configuration
        Information:

            Windows RE status:         Disabled
            Windows RE location:
            Boot Configuration Data (BCD) identifier: 00000000-0000-0000-0000-000000000000
            Recovery image location:
            Recovery image index:      0
            Custom image location:
            Custom image index:        0

        REAGENTC.EXE: Operation Successful.

        """;

    [Fact]
    public void ReadsEnabledLocation()
    {
        var info = WinReProbe.Parse(0, Enabled);
        Assert.Equal(WinReStatus.Enabled, info.Status);
        Assert.Equal(2, info.Location!.DiskNumber);
        Assert.Equal(3, info.Location.PartitionNumber);
        Assert.Equal("65dfebdb-3ee2-11f0-bf06-b76f1e1a4c90", info.BcdIdentifier);
        Assert.Null(info.ResetImage);
    }

    [Fact]
    public void ReadsDisabled()
    {
        var info = WinReProbe.Parse(0, Disabled);
        Assert.Equal(WinReStatus.Disabled, info.Status);
        Assert.Null(info.Location);
    }

    [Fact]
    public void ReadsResetImageLocation()
    {
        var output = Enabled.Replace("Recovery image location:", @"Recovery image location:   \\?\GLOBALROOT\device\harddisk0\partition5\RecoveryImage", StringComparison.Ordinal);
        var info = WinReProbe.Parse(0, output);
        Assert.Equal(new WinReLocation(0, 5, @"\\?\GLOBALROOT\device\harddisk0\partition5\RecoveryImage"), info.ResetImage);
    }

    [Theory]
    [InlineData(2, "REAGENTC.EXE: Operation failed: 5", "exit code 2")]
    [InlineData(0, "Windows RE-Status:         Aktiviert", "not recognised")]
    [InlineData(0, "    Windows RE status:         Enabled\n    Windows RE location:       C:\\Recovery\\WindowsRE", "not a partition path")]
    [InlineData(0, "    Windows RE status:         Staged", "unrecognised")]
    public void RefusesToGuess(int exitCode, string output, string reason)
    {
        var info = WinReProbe.Parse(exitCode, output);
        Assert.Equal(WinReStatus.Unknown, info.Status);
        Assert.Contains(reason, info.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadsThisMachineWithoutThrowing()
    {
        // Read-only (reagentc /info). The result depends on the machine; it must just not throw.
        var info = WinReProbe.Read();
        Assert.True(info.Status != WinReStatus.Unknown || info.Error is not null);
    }
}
