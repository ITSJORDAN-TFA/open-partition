using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using OpenPartition.Discovery.Native;

namespace OpenPartition.Tests;

/// <summary>
/// Phase 1 acceptance: no write handles anywhere in discovery. Checked two ways: every IOCTL
/// code discovery can send is decoded and must not need write access, and the discovery
/// sources are scanned for anything that could write to a disk or volume.
/// </summary>
public partial class ReadOnlyEnforcementTests
{
    private const uint FileWriteAccess = 0x2;

    [Fact]
    public void EveryIoctlIsReadOnly()
    {
        var codes = typeof(IoctlCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(uint))
            .ToList();
        Assert.NotEmpty(codes);
        foreach (var field in codes)
        {
            var code = (uint)field.GetRawConstantValue()!;
            Assert.True((IoctlCodes.RequiredAccess(code) & FileWriteAccess) == 0, $"{field.Name} (0x{code:X8}) requires write access");
        }
    }

    [Fact]
    public void WriteIoctlsAreRecognisedAsWrites()
    {
        // IOCTL_DISK_SET_DRIVE_LAYOUT_EX is caught by the access bits. FSCTL_LOCK_VOLUME is not (it is
        // FILE_ANY_ACCESS), which is why the source scan below also bans FSCTL names.
        Assert.NotEqual(0u, IoctlCodes.RequiredAccess(0x0007_C054) & FileWriteAccess);
        Assert.Equal(0u, IoctlCodes.RequiredAccess(0x0009_0018) & FileWriteAccess);
    }

    [GeneratedRegex(@"GENERIC_WRITE|GenericWrite|GENERIC_ALL|0x4000_?0000(?![0-9a-fA-F_])|0x1000_?0000(?![0-9a-fA-F_])|FileAccess\.(Write|ReadWrite)|\bWriteFile\b|DeleteFile|SetDriveLayout|LOCK_VOLUME|DISMOUNT_VOLUME|FSCTL_|IOCTL_DISK_SET|File\.(Write|Delete|Create|Move|Copy|Open\w*Write)|FileMode\.(Create|Append|Truncate|OpenOrCreate)|\.Invoke(Method)?\(")]
    private static partial Regex Forbidden();

    [Fact]
    public void DiscoverySourcesContainNoWriteCapableCalls()
    {
        var files = DiscoverySources().ToList();
        Assert.NotEmpty(files);
        var offenders = files
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (f, i + 1, line)))
            .Where(x => !x.line.TrimStart().StartsWith("//", StringComparison.Ordinal) && Forbidden().IsMatch(x.line))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.Item2}: {x.line.Trim()}")
            .ToList();
        Assert.True(offenders.Count == 0, "Write-capable code in discovery:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void OnlyDeviceHandleOpensDevices()
    {
        var openers = DiscoverySources()
            .Where(f => File.ReadAllText(f).Contains("NativeMethods.CreateFile", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();
        Assert.Equal(["DeviceHandle.cs"], openers);
    }

    [Fact]
    public void DeviceHandleOnlyRequestsQueryOrReadAccess()
    {
        var source = File.ReadAllText(DiscoverySources().Single(f => Path.GetFileName(f) == "DeviceHandle.cs"));
        var accesses = AccessArguments().Matches(source).Select(m => m.Groups[1].Value).ToHashSet();
        Assert.Equal(new HashSet<string> { "QueryOnlyAccess", "NativeMethods.GenericRead" }, accesses);
    }

    [GeneratedRegex(@"=>\s*Open\(path,\s*([\w.]+)\)")]
    private static partial Regex AccessArguments();

    private static IEnumerable<string> DiscoverySources([CallerFilePath] string here = "")
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", ".."));
        var dir = Path.Combine(root, "src", "dotnet", "OpenPartition.Discovery");
        return Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }
}
