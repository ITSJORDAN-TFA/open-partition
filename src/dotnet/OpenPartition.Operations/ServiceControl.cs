using System.Management;

namespace OpenPartition.Operations;

/// <summary>
/// Stops services the user chose to stop so a volume can be locked. Only services, and only on
/// request: processes are never stopped. Services that depend on one are stopped first (Windows
/// requires it) and are listed to the user before anything is stopped.
/// </summary>
public static class ServiceControl
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Running services that depend on <paramref name="name"/>, directly or not, in the order to stop them.</summary>
    internal static IReadOnlyList<string> RunningDependents(ManagementScope scope, string name)
    {
        var order = new List<string>();
        Visit(name, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name });
        return order;

        void Visit(string service, HashSet<string> seen)
        {
            foreach (var dependent in DirectDependents(scope, service))
            {
                if (seen.Add(dependent))
                {
                    Visit(dependent, seen);
                    order.Add(dependent);
                }
            }
        }
    }

    /// <summary>Stop <paramref name="services"/> and their running dependents, waiting until each has stopped.</summary>
    public static Task StopAsync(IReadOnlyList<ServiceUser> services, IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var scope = new ManagementScope(@"root\cimv2");
            scope.Connect();
            foreach (var service in services)
            {
                var dependents = RunningDependents(scope, service.Name);
                EnsureConsentedDependents(service, dependents);
                foreach (var dependent in dependents)
                {
                    Stop(scope, dependent, progress, cancellationToken);
                }
                Stop(scope, service.Name, progress, cancellationToken);
            }
        }, cancellationToken);

    internal static void EnsureConsentedDependents(ServiceUser service, IReadOnlyList<string> current)
    {
        if (current.Any(name => !service.RunningDependents.Contains(name, StringComparer.OrdinalIgnoreCase)))
        {
            throw new OperationFailedException($"The dependent services for {service.Name} changed. Retry detection and review the service list before stopping services. Some previously approved services may already have stopped.");
        }
    }

    private static List<string> DirectDependents(ManagementScope scope, string service)
    {
        var query = new ObjectQuery($"ASSOCIATORS OF {{Win32_Service.Name='{Escape(service)}'}} WHERE AssocClass = Win32_DependentService Role = Antecedent");
        using var searcher = new ManagementObjectSearcher(scope, query);
        var names = new List<string>();
        foreach (ManagementObject s in searcher.Get())
        {
            using (s)
            {
                if (s["State"] as string is "Running" or "Start Pending" or "Continue Pending" or "Paused" && s["Name"] is string n)
                {
                    names.Add(n);
                }
            }
        }
        return names;
    }

    private static void Stop(ManagementScope scope, string name, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        using var service = new ManagementObject(scope, new ManagementPath($"Win32_Service.Name='{Escape(name)}'"), null);
        service.Get();
        if (State(service) == "Stopped")
        {
            return;
        }
        progress?.Report($"Stopping service {service["DisplayName"] ?? name}…");
        var code = Convert.ToUInt32(service.InvokeMethod("StopService", null) ?? 0u, System.Globalization.CultureInfo.InvariantCulture);
        // 5: "can't accept control" also covers a service that is already stopping.
        if (code is not (0 or 5))
        {
            throw new OperationFailedException($"Windows would not stop service {name} ({Reason(code)}). Some previously approved services may already have stopped; no disk operation was retried. Stop it from Services (services.msc), then try again.");
        }
        var deadline = DateTime.UtcNow + StopTimeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            service.Get();
            if (State(service) == "Stopped")
            {
                return;
            }
            if (DateTime.UtcNow > deadline)
            {
                throw new OperationFailedException($"Service {name} did not stop within {StopTimeout.TotalSeconds:0} seconds (it is '{State(service)}'). Some previously approved services may already have stopped; no disk operation was retried. Check it in Services (services.msc), then try again.");
            }
            Thread.Sleep(500);
        }
    }

    private static string State(ManagementObject service) => service["State"] as string ?? "";

    private static string Escape(string name) => name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);

    private static string Reason(uint code) => code switch
    {
        2 => "access denied: run as Administrator",
        3 => "other running services depend on it",
        _ => $"error {code}",
    };
}
