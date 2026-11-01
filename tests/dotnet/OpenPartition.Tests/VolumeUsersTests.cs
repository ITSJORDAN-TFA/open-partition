using OpenPartition.Operations;

namespace OpenPartition.Tests;

/// <summary>
/// The lock-failure message (pure formatting) and the read-only detection, which is checked
/// against files this test process opens itself. Nothing here stops a service or a process.
/// </summary>
public class VolumeUsersTests
{
    private static readonly ServiceUser Sql = new("MSSQLSERVER", "SQL Server (MSSQLSERVER)", CanStop: true, ["SQLSERVERAGENT"]);
    private static readonly ServiceUser Backup = new("BackupAgent", "Backup Agent", CanStop: true, []);

    private static LockDiagnosis Diagnosis(bool partial = false, params VolumeUser[] users) => new("E:", @"E:\", users, partial);

    [Fact]
    public void ChangedDependentsRequireNewConsent()
    {
        ServiceControl.EnsureConsentedDependents(Sql, ["sqlserveragent"]);
        ServiceControl.EnsureConsentedDependents(Sql, []);
        Assert.Throws<OperationFailedException>(() => ServiceControl.EnsureConsentedDependents(Sql, ["NewService"]));
    }

    [Fact]
    public void NamesAProgramWithAFileOpen()
    {
        var message = VolumeUsers.Format(Diagnosis(false, new VolumeUser(4321, "notepad.exe", "Notepad", [], [@"E:\notes.txt"])), StopHint.Command);
        Assert.StartsWith("E: is in use, so it can't be locked for the move. Nothing was changed by this step.", message, StringComparison.Ordinal);
        Assert.Contains(@"notepad.exe (process 4321): E:\notes.txt", message, StringComparison.Ordinal);
        Assert.Contains("close the other programs listed (this program never closes programs itself), then try again.", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Stop-Service", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Resource Monitor", message, StringComparison.Ordinal);
    }

    [Fact]
    public void FriendlyNameIsShownWhenItDiffersFromTheProgramFile()
    {
        var message = VolumeUsers.Format(Diagnosis(false, new VolumeUser(77, "WINWORD.EXE", "Microsoft Word", [], [@"E:\a.docx", @"E:\b.docx", @"E:\c.docx"])), StopHint.Command);
        Assert.Contains(@"Microsoft Word (WINWORD.EXE, process 77): E:\a.docx and 2 more files", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ServicesGetAStopServiceCommandOnTheCommandLine()
    {
        var d = Diagnosis(false,
            new VolumeUser(900, "sqlservr.exe", null, [Sql], [@"E:\data\db.mdf", @"E:\data\db.ldf"]),
            new VolumeUser(901, "agent.exe", null, [Backup], [], RunsFromVolume: true));
        var message = VolumeUsers.Format(d, StopHint.Command);
        Assert.Contains(@"service 'SQL Server (MSSQLSERVER)' (MSSQLSERVER), in sqlservr.exe (process 900): E:\data\db.mdf and 1 more file", message, StringComparison.Ordinal);
        Assert.Contains("service 'Backup Agent' (BackupAgent), in agent.exe (process 901): runs from E:", message, StringComparison.Ordinal);
        Assert.Contains("Stop the services (Stop-Service -Name MSSQLSERVER, BackupAgent -Force; -Force also stops SQLSERVERAGENT, which depend on them), then try again.", message, StringComparison.Ordinal);
        Assert.DoesNotContain("close the other programs", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ServicesPointAtTheButtonInTheGui()
    {
        var d = Diagnosis(false,
            new VolumeUser(900, "sqlservr.exe", null, [Sql], [@"E:\db.mdf"]),
            new VolumeUser(4321, "notepad.exe", null, [], [@"E:\notes.txt"]));
        var message = VolumeUsers.Format(d, StopHint.Button);
        Assert.Contains("Use \"Stop service SQL Server (MSSQLSERVER) and retry\" below to stop the service and close the other programs listed (this program never closes programs itself), then try again.", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Stop-Service", message, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyServicesInTheirOwnProcessCanBeStoppedFromHere()
    {
        var shared = new VolumeUser(1200, "svchost.exe", null, [new("A", "Service A", true, []), new("B", "Service B", true, [])], [@"E:\log.txt"]);
        var system = new VolumeUser(4, "System", null, [], [@"E:\pagefile.sys"]);
        var refuses = new VolumeUser(1300, "x.exe", null, [new("X", "Service X", CanStop: false, [])], [@"E:\x"]);
        var d = Diagnosis(false, shared, system, refuses);
        Assert.Empty(d.StoppableServices);
        var message = VolumeUsers.Format(d, StopHint.Button);
        Assert.Contains("svchost.exe (process 1200), which runs the services A, B", message, StringComparison.Ordinal);
        Assert.Contains("Windows itself (System, process 4)", message, StringComparison.Ordinal);
        Assert.Contains("move any page file off it", message, StringComparison.Ordinal);
        Assert.Contains("stop the service in a shared host", message, StringComparison.Ordinal);
        Assert.DoesNotContain("and retry", message, StringComparison.Ordinal);

        var both = Diagnosis(false, new VolumeUser(900, "sqlservr.exe", null, [Sql], []), new VolumeUser(901, "sqlservr.exe", null, [Sql], []));
        Assert.Single(both.StoppableServices);
    }

    [Fact]
    public void NothingFoundPointsToResourceMonitor()
    {
        var message = VolumeUsers.Format(Diagnosis(partial: true), StopHint.Button);
        Assert.Contains("No program could be found using it, but not every program could be checked", message, StringComparison.Ordinal);
        Assert.Contains("Resource Monitor (CPU tab, Associated Handles, search \"E:\\\")", message, StringComparison.Ordinal);

        var partial = VolumeUsers.Format(Diagnosis(true, new VolumeUser(5, "a.exe", null, [], [@"E:\f"])), StopHint.Command);
        Assert.Contains("This list may be incomplete: not every program could be checked", partial, StringComparison.Ordinal);
        Assert.Contains("Resource Monitor", partial, StringComparison.Ordinal);

        var named = VolumeUsers.Format(Diagnosis(true, new VolumeUser(5, "a.exe", null, [], [@"E:\f"])) with { Unchecked = ["busy.exe (process 77)"] }, StopHint.Command);
        Assert.Contains("This list may be incomplete: some open files of busy.exe (process 77) could not be checked", named, StringComparison.Ordinal);
    }

    [Fact]
    public void LongListsAreCut()
    {
        var users = Enumerable.Range(10, 20).Select(i => new VolumeUser(i, "p.exe", null, [], [$@"E:\{i}"])).ToArray();
        var message = VolumeUsers.Format(Diagnosis(false, users), StopHint.Command);
        Assert.Contains("and 8 more", message, StringComparison.Ordinal);
        Assert.DoesNotContain("process 29", message, StringComparison.Ordinal);
    }

    [Fact]
    public void StopButtonAndConfirmation()
    {
        Assert.Equal("Stop service SQL Server (MSSQLSERVER) and retry", VolumeUsers.StopButtonText([Sql]));
        Assert.Equal("Stop 2 services and retry", VolumeUsers.StopButtonText([Sql, Backup]));
        var confirm = VolumeUsers.StopConfirmation([Sql, Backup]);
        Assert.StartsWith("Stop these services?", confirm, StringComparison.Ordinal);
        Assert.Contains("SQL Server (MSSQLSERVER) (MSSQLSERVER)", confirm, StringComparison.Ordinal);
        Assert.Contains("also stops the services that depend on it: SQLSERVERAGENT", confirm, StringComparison.Ordinal);
        Assert.Contains("not started again automatically", confirm, StringComparison.Ordinal);
        Assert.Contains("No program is closed", confirm, StringComparison.Ordinal);
    }

    [Fact]
    public void HandleScanFindsAFileThisProcessHasOpen()
    {
        var path = Path.Combine(Path.GetTempPath(), $"opm-holder-{Guid.NewGuid():N}.tmp");
        try
        {
            using var held = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            var root = Path.GetPathRoot(Path.GetFullPath(path))!;
            var volume = OpenFiles.VolumeGuidPath(root);
            Assert.NotNull(volume);
            var scan = OpenFiles.OnVolume(volume, root, TimeSpan.FromSeconds(20), onlyProcess: Environment.ProcessId, maxFilesPerProcess: int.MaxValue);
            var mine = Assert.Single(scan.Holders);
            Assert.Equal(Environment.ProcessId, mine.ProcessId);
            Assert.Contains(Path.GetFullPath(path), mine.Files, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RestartManagerNamesTheProcessHoldingAFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"opm-holder-{Guid.NewGuid():N}.tmp");
        try
        {
            using var held = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            var entries = OpenFiles.RestartManager([path]);
            Assert.Contains(entries, e => e.ProcessId == Environment.ProcessId);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
