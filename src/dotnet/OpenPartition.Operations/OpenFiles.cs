using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenPartition.Operations;

/// <summary>Files one process has open on the volume.</summary>
internal sealed record OpenFileHolder(int ProcessId, IReadOnlyList<string> Files);

/// <summary>What the handle scan found, and whether it could look everywhere.</summary>
internal sealed record OpenFileScan(IReadOnlyList<OpenFileHolder> Holders, bool Partial);

/// <summary>A process Restart Manager says is using the files.</summary>
internal sealed record RestartManagerEntry(int ProcessId, string AppName, string ServiceName);

/// <summary>
/// Finds the processes that have files open on a volume, without changing anything: every open
/// handle in the system is listed (<c>NtQuerySystemInformation</c>), each file handle is copied
/// into this process just long enough to ask for its path, and the copy is closed again. Only
/// handles to disk files are asked for a path (pipes can block that query forever), and every
/// query runs under a watchdog so one stuck handle can't hang the program.
/// </summary>
internal static partial class OpenFiles
{
    private const int SystemExtendedHandleInformation = 64;
    private const uint StatusInfoLengthMismatch = 0xC000_0004;
    private const uint ProcessDupHandle = 0x0040;
    private const uint DuplicateSameAccess = 0x2;
    private const uint FileTypeDisk = 1;
    private const uint VolumeNameNt = 0x2;
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(1);

    [StructLayout(LayoutKind.Sequential)]
    private struct HandleEntry
    {
        public IntPtr Object;
        public UIntPtr UniqueProcessId;
        public UIntPtr HandleValue;
        public uint GrantedAccess;
        public ushort CreatorBackTraceIndex;
        public ushort ObjectTypeIndex;
        public uint HandleAttributes;
        public uint Reserved;
    }

    /// <summary>
    /// Processes with files open on the volume <paramref name="volumeGuidPath"/>
    /// (<c>\\?\Volume{…}\</c>). Paths are returned under <paramref name="displayRoot"/> (for
    /// example <c>E:\</c>). Only <paramref name="onlyProcess"/> is looked at when given, and at most
    /// <paramref name="maxFilesPerProcess"/> files are listed for each process.
    /// </summary>
    public static OpenFileScan OnVolume(string volumeGuidPath, string displayRoot, TimeSpan budget, int? onlyProcess = null, int maxFilesPerProcess = 50)
    {
        var device = DeviceName(volumeGuidPath);
        if (device is null)
        {
            return new OpenFileScan([], Partial: true);
        }
        // A file this process holds open, found in the list, gives the object type number of
        // files on this Windows build.
        var path = Environment.ProcessPath ?? typeof(OpenFiles).Assembly.Location;
        using var probe = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var entries = Handles();
        var self = Environment.ProcessId;
        var probeValue = (long)probe.DangerousGetHandle();
        var fileType = entries.Where(e => (int)e.UniqueProcessId == self && (long)e.HandleValue == probeValue)
            .Select(e => (ushort?)e.ObjectTypeIndex).FirstOrDefault();
        if (fileType is null)
        {
            return new OpenFileScan([], Partial: true);
        }
        var candidates = entries
            .Where(e => e.ObjectTypeIndex == fileType && ((long)e.HandleValue != probeValue || (int)e.UniqueProcessId != self))
            .Where(e => onlyProcess is null || (int)e.UniqueProcessId == onlyProcess)
            .Select(e => ((int)e.UniqueProcessId, checked((IntPtr)(long)e.HandleValue)))
            .ToList();
        return new Scan(device.TrimEnd('\\') + "\\", displayRoot, candidates, budget, maxFilesPerProcess).Run();
    }

    /// <summary>The volume GUID path (<c>\\?\Volume{…}\</c>) of a mount point such as <c>C:\</c>, or null.</summary>
    internal static string? VolumeGuidPath(string mountPoint)
    {
        var buffer = new char[64];
        if (!GetVolumeNameForVolumeMountPoint(mountPoint, buffer, (uint)buffer.Length))
        {
            return null;
        }
        var end = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, end < 0 ? buffer.Length : end);
    }

    /// <summary>The NT device name behind a volume GUID path, e.g. <c>\Device\HarddiskVolume5</c>.</summary>
    private static string? DeviceName(string volumeGuidPath)
    {
        var name = volumeGuidPath.TrimEnd('\\');
        if (!name.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return null;
        }
        var buffer = new char[1024];
        if (QueryDosDevice(name[4..], buffer, (uint)buffer.Length) == 0)
        {
            return null;
        }
        var end = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, end < 0 ? buffer.Length : end);
    }

    private static unsafe HandleEntry[] Handles()
    {
        var size = 4 << 20;
        while (true)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = NtQuerySystemInformation(SystemExtendedHandleInformation, (void*)buffer, (uint)size, out var needed);
                if (status == StatusInfoLengthMismatch)
                {
                    size = (int)Math.Min(Math.Max(size * 2L, needed + (1L << 20)), 512L << 20);
                    if (size >= 512 << 20)
                    {
                        throw new Win32Exception("The system handle list is too large to read.");
                    }
                    continue;
                }
                if (status != 0)
                {
                    throw new Win32Exception($"Could not list open handles (status 0x{status:X8}).");
                }
                // SYSTEM_HANDLE_INFORMATION_EX: count, reserved, then the entries.
                var count = (long)*(UIntPtr*)buffer;
                return new ReadOnlySpan<HandleEntry>((byte*)buffer + (2 * IntPtr.Size), checked((int)count)).ToArray();
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <summary>
    /// Path queries on a worker thread. If one query takes too long the worker is abandoned
    /// (it is a background thread) and a new one carries on after the stuck handle.
    /// </summary>
    private sealed class Scan(string prefix, string displayRoot, List<(int Pid, IntPtr Handle)> candidates, TimeSpan budget, int maxFilesPerProcess)
    {
        private readonly Lock gate = new();
        private readonly Dictionary<int, List<string>> found = [];
        private readonly Dictionary<int, SafeProcessHandle?> processes = [];
        private int next;
        private int generation;
        private bool partial;

        public OpenFileScan Run()
        {
            var deadline = DateTime.UtcNow + budget;
            var abandoned = 0;
            try
            {
                while (true)
                {
                    int mine;
                    int at;
                    lock (gate)
                    {
                        mine = generation;
                        at = next;
                    }
                    if (at >= candidates.Count)
                    {
                        break;
                    }
                    using var done = new ManualResetEventSlim();
                    new Thread(() => Work(mine, done)) { IsBackground = true, Name = "Volume user scan" }.Start();
                    var since = DateTime.UtcNow;
                    var stuck = false;
                    while (!done.Wait(TimeSpan.FromMilliseconds(100)))
                    {
                        int now;
                        lock (gate)
                        {
                            now = next;
                        }
                        if (now != at)
                        {
                            at = now;
                            since = DateTime.UtcNow;
                        }
                        if (DateTime.UtcNow >= deadline || DateTime.UtcNow - since > QueryTimeout)
                        {
                            stuck = true;
                            break;
                        }
                    }
                    if (!stuck)
                    {
                        break;
                    }
                    lock (gate)
                    {
                        // Leave that worker behind and skip the handle it is stuck on.
                        generation++;
                        next++;
                        partial = true;
                    }
                    if (++abandoned >= 3 || DateTime.UtcNow >= deadline)
                    {
                        break;
                    }
                }
                lock (gate)
                {
                    generation++;
                    return new OpenFileScan(
                        found.OrderBy(f => f.Key).Select(f => new OpenFileHolder(f.Key, f.Value.Order(StringComparer.OrdinalIgnoreCase).ToList())).ToList(),
                        partial);
                }
            }
            finally
            {
                lock (gate)
                {
                    // A worker left behind still holding one of these keeps it alive until it
                    // returns (the marshaller adds a reference), so closing here is safe.
                    foreach (var p in processes.Values)
                    {
                        p?.Dispose();
                    }
                }
            }
        }

        private void Work(int mine, ManualResetEventSlim done)
        {
            try
            {
                while (true)
                {
                    (int Pid, IntPtr Handle) candidate;
                    SafeProcessHandle? process;
                    lock (gate)
                    {
                        if (generation != mine || next >= candidates.Count)
                        {
                            return;
                        }
                        candidate = candidates[next];
                        if (!processes.TryGetValue(candidate.Pid, out process))
                        {
                            process = Open(candidate.Pid);
                            processes[candidate.Pid] = process;
                            partial |= process is null;
                        }
                        if (process is null || (found.TryGetValue(candidate.Pid, out var already) && already.Count >= maxFilesPerProcess))
                        {
                            next++;
                            continue;
                        }
                    }
                    var path = PathOf(process, candidate.Handle);
                    lock (gate)
                    {
                        if (generation != mine)
                        {
                            return;
                        }
                        next++;
                        if (path is not null && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!found.TryGetValue(candidate.Pid, out var files))
                            {
                                found[candidate.Pid] = files = [];
                            }
                            var shown = displayRoot + path[prefix.Length..];
                            if (!files.Contains(shown, StringComparer.OrdinalIgnoreCase))
                            {
                                files.Add(shown);
                            }
                        }
                    }
                }
            }
            finally
            {
                // The coordinator may have given up on this worker and disposed the event.
                try
                {
                    done.Set();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        private static SafeProcessHandle? Open(int pid)
        {
            var handle = OpenProcess(ProcessDupHandle, false, (uint)pid);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                return null;
            }
            return handle;
        }

        /// <summary>The NT path of a handle in another process, or null.</summary>
        private static string? PathOf(SafeProcessHandle owner, IntPtr remote)
        {
            IntPtr copy;
            try
            {
                if (!DuplicateHandle(owner, remote, GetCurrentProcess(), out copy, 0, false, DuplicateSameAccess))
                {
                    return null;
                }
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
            using var file = new SafeFileHandle(copy, ownsHandle: true);
            if (GetFileType(file) != FileTypeDisk)
            {
                return null;
            }
            var buffer = new char[1024];
            var length = GetFinalPathNameByHandle(file, buffer, (uint)buffer.Length, VolumeNameNt);
            if (length >= buffer.Length)
            {
                buffer = new char[length + 1];
                length = GetFinalPathNameByHandle(file, buffer, (uint)buffer.Length, VolumeNameNt);
            }
            return length == 0 || length >= buffer.Length ? null : new string(buffer, 0, (int)length);
        }
    }

    // ---- Restart Manager ----

    private const int CchRmSessionKey = 32;
    private const int CchRmMaxAppName = 255;
    private const int CchRmMaxSvcName = 63;
    private const uint ErrorMoreData = 234;

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct RmProcessInfo
    {
        // RM_UNIQUE_PROCESS: process id and its start time (a FILETIME, 4-byte aligned).
        public int ProcessId;
        public uint StartTimeLow;
        public uint StartTimeHigh;
        public fixed char AppName[CchRmMaxAppName + 1];
        public fixed char ServiceShortName[CchRmMaxSvcName + 1];
        public int ApplicationType;
        public uint AppStatus;
        public uint TsSessionId;
        public int Restartable;
    }

    /// <summary>Ask Restart Manager which processes and services use <paramref name="files"/>. Read-only.</summary>
    public static unsafe IReadOnlyList<RestartManagerEntry> RestartManager(IReadOnlyCollection<string> files)
    {
        if (files.Count == 0)
        {
            return [];
        }
        var key = stackalloc char[CchRmSessionKey + 1];
        if (RmStartSession(out var session, 0, key) != 0)
        {
            return [];
        }
        try
        {
            if (RmRegisterResources(session, (uint)files.Count, [.. files], 0, IntPtr.Zero, 0, null) != 0)
            {
                return [];
            }
            var count = 16u;
            while (true)
            {
                var info = new RmProcessInfo[count];
                uint result;
                uint needed;
                fixed (RmProcessInfo* p = info)
                {
                    result = RmGetList(session, out needed, ref count, p, out _);
                }
                if (result == ErrorMoreData && needed > info.Length && needed < 10_000)
                {
                    count = needed;
                    continue;
                }
                if (result != 0)
                {
                    return [];
                }
                var list = new List<RestartManagerEntry>();
                for (var i = 0; i < count; i++)
                {
                    fixed (char* app = info[i].AppName)
                    fixed (char* service = info[i].ServiceShortName)
                    {
                        list.Add(new RestartManagerEntry(info[i].ProcessId, new string(app), new string(service)));
                    }
                }
                return list;
            }
        }
        finally
        {
            _ = RmEndSession(session);
        }
    }

    // ---- Imports. Nothing here opens anything for writing. ----

    [LibraryImport("ntdll.dll")]
    private static unsafe partial uint NtQuerySystemInformation(int infoClass, void* info, uint length, out uint returnLength);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint QueryDosDevice(string deviceName, [Out] char[] targetPath, uint max);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumeNameForVolumeMountPoint(string mountPoint, [Out] char[] volumeName, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateHandle(SafeProcessHandle sourceProcess, IntPtr sourceHandle, IntPtr targetProcess, out IntPtr targetHandle, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetFileType(SafeFileHandle file);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetFinalPathNameByHandle(SafeFileHandle file, [Out] char[] path, uint length, uint flags);

    [LibraryImport("rstrtmgr.dll")]
    private static unsafe partial uint RmStartSession(out uint session, uint flags, char* sessionKey);

    [LibraryImport("rstrtmgr.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RmRegisterResources(uint session, uint fileCount, string[] files, uint applicationCount, IntPtr applications, uint serviceCount, string[]? services);

    [LibraryImport("rstrtmgr.dll")]
    private static unsafe partial uint RmGetList(uint session, out uint needed, ref uint count, RmProcessInfo* info, out uint rebootReasons);

    [LibraryImport("rstrtmgr.dll")]
    private static partial uint RmEndSession(uint session);
}
