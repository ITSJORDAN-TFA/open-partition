using OpenPartition.Operations;

namespace OpenPartition.Tests;

/// <summary>The WinRE hook around a move, against a scripted reagentc (nothing real is run).</summary>
public sealed class WinReHookTests : IDisposable
{
    private const long From = 127 * 1024L * 1024 * 1024;
    private const long To = 255 * 1024L * 1024 * 1024;

    private readonly string _pending = Path.Combine(Path.GetTempPath(), $"opm-winre-{Guid.NewGuid():N}.json");
    private readonly List<string> _calls = [];
    private readonly Dictionary<long, int> _partitions = new() { [From] = 4 };
    private bool _enabled = true;
    private int _enabledOn = 4;
    private bool _imageStaged;

    /// <summary>What reagentc /enable does; by default it stages WinRE on the partition at the target.</summary>
    private Func<(int ExitCode, int? Partition)> _enable;

    public WinReHookTests() => _enable = () => (0, _partitions.GetValueOrDefault(To, _partitions.GetValueOrDefault(From)));

    public void Dispose() => File.Delete(_pending);

    private static MoveStep Step => new(0, "the recovery partition", From, To, 750L * 1024 * 1024, WinRe: true);

    private WinReHook Hook() => new(Reagentc, (disk, offset) => disk == 0 && _partitions.TryGetValue(offset, out var n) ? n : null, () => _imageStaged, _pending);

    private Task<ReagentcResult> Reagentc(string argument, CancellationToken cancellationToken)
    {
        _calls.Add(argument);
        switch (argument)
        {
            case "/disable":
                _enabled = false;
                _imageStaged = true;
                return Task.FromResult(new ReagentcResult(0, "REAGENTC.EXE: Operation Successful."));
            case "/enable":
                var (code, partition) = _enable();
                if (code == 0 && partition is { } p)
                {
                    _enabled = true;
                    _enabledOn = p;
                    _imageStaged = false;
                    return Task.FromResult(new ReagentcResult(0, "REAGENTC.EXE: Operation Successful."));
                }
                return Task.FromResult(new ReagentcResult(code, "REAGENTC.EXE: The Windows RE image was not found."));
            default:
                return Task.FromResult(new ReagentcResult(0, _enabled
                    ? WinReProbeTests.Enabled.Replace(@"harddisk2\partition3", $@"harddisk0\partition{_enabledOn}", StringComparison.Ordinal)
                    : WinReProbeTests.Disabled));
        }
    }

    /// <summary>What the engine does to the table: the entry comes back at the target, possibly renumbered.</summary>
    private void Move(int newNumber = 5)
    {
        _partitions.Remove(From);
        _partitions[To] = newNumber;
    }

    [Fact]
    public async Task DisablesBeforeAndReEnablesOnTheMovedPartition()
    {
        var hook = Hook();
        await hook.TurnOffAsync(Step, null);
        Assert.Equal(["/info", "/disable", "/info"], _calls);
        Assert.NotNull(hook.Pending());

        Move();
        await hook.TurnOnAsync(Step, null);
        Assert.Equal(["/info", "/disable", "/info", "/enable", "/info"], _calls);
        Assert.True(_enabled);
        Assert.Equal(5, _enabledOn);
        Assert.Null(hook.Pending());
    }

    [Fact]
    public async Task RefusesWithoutChangingAnythingIfWinReMovedSincePlanning()
    {
        _enabledOn = 7;
        var e = await Assert.ThrowsAsync<OperationFailedException>(() => Hook().TurnOffAsync(Step, null));
        Assert.Contains("Nothing was changed", e.Message, StringComparison.Ordinal);
        Assert.Equal(["/info"], _calls);
    }

    [Fact]
    public async Task PutsWinReBackIfItsImageIsNotWhereExpectedAfterDisabling()
    {
        var hook = new WinReHook(async (a, c) =>
        {
            var r = await Reagentc(a, c);
            _imageStaged = false;
            return r;
        }, (_, offset) => _partitions.TryGetValue(offset, out var n) ? n : null, () => _imageStaged, _pending);
        var e = await Assert.ThrowsAsync<OperationFailedException>(() => hook.TurnOffAsync(Step, null));
        Assert.Contains("was not moved", e.Message, StringComparison.Ordinal);
        Assert.Contains("turned back on where it was", e.Message, StringComparison.Ordinal);
        Assert.True(_enabled);
        Assert.Null(hook.Pending());
    }

    [Fact]
    public async Task ReportsRecoveryStepsWhenEnableFails()
    {
        var hook = Hook();
        await hook.TurnOffAsync(Step, null);
        Move();
        _enable = () => (2, null);
        var e = await Assert.ThrowsAsync<OperationFailedException>(() => hook.TurnOnAsync(Step, null));
        Assert.Contains("data is intact", e.Message, StringComparison.Ordinal);
        Assert.Contains("reagentc /enable", e.Message, StringComparison.Ordinal);
        Assert.Contains(WinReHook.StagedImage, e.Message, StringComparison.Ordinal);
        Assert.NotNull(hook.Pending());
    }

    [Fact]
    public async Task ReportsWhenWindowsStagesWinReSomewhereElse()
    {
        var hook = Hook();
        await hook.TurnOffAsync(Step, null);
        Move();
        _enable = () => (0, 3);
        var e = await Assert.ThrowsAsync<OperationFailedException>(() => hook.TurnOnAsync(Step, null));
        Assert.Contains("somewhere else", e.Message, StringComparison.Ordinal);
        Assert.Contains("/setreimage", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RolledBackMoveTurnsWinReBackOnWhereItWas()
    {
        var hook = Hook();
        await hook.TurnOffAsync(Step, null);
        var note = await hook.AfterFailedMoveAsync(Step);
        Assert.Equal("The Windows Recovery Environment was turned back on.", note);
        Assert.Equal(4, _enabledOn);
    }

    [Fact]
    public async Task InterruptedMoveIsFinishedByResume()
    {
        var hook = Hook();
        await hook.TurnOffAsync(Step, null);
        _partitions.Clear(); // out of the table while the move is interrupted
        Assert.Equal(WinReHook.AfterFailedMove, await hook.AfterFailedMoveAsync(Step));
        Assert.False(_enabled);

        var pending = new PendingMove("j", "id", 0, From, To, Step.Length, "3/12");
        await hook.FinishAfterRecoveryAsync(pending with { Target = To + 1 }, 0, undone: false, null);
        Assert.False(_enabled); // a different move: left alone

        Move(newNumber: 4);
        await hook.FinishAfterRecoveryAsync(pending, 0, undone: false, null);
        Assert.True(_enabled);
        Assert.Null(hook.Pending());
    }
}
