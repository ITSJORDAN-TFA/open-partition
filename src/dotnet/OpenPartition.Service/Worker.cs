namespace OpenPartition.Service;

// Placeholder. Phase 2 adds the named-pipe host; Phase 3 adds the incomplete-journal check on start-up.
public sealed partial class Worker(ILogger<Worker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "OpenPartition service started")]
    private static partial void LogStarted(ILogger logger);
}
