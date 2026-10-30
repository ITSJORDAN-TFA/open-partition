using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenPartition.Discovery.Native;

/// <summary>
/// The only place discovery opens disks or volumes. Phase 1 never asks for write access:
/// handles are query-only (access 0) or read-only, and always share read and write so
/// nothing else on the machine is disturbed.
/// </summary>
internal static class DeviceHandle
{
    private const uint QueryOnlyAccess = 0;

    /// <summary>Open for IOCTL queries only. Works without elevation for most queries.</summary>
    public static SafeFileHandle OpenForQuery(string path) => Open(path, QueryOnlyAccess);

    /// <summary>Open for reading sectors (needs elevation).</summary>
    public static SafeFileHandle OpenRead(string path) => Open(path, NativeMethods.GenericRead);

    private static SafeFileHandle Open(string path, uint access)
    {
        var handle = NativeMethods.CreateFile(
            path, access, NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
            IntPtr.Zero, NativeMethods.OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error, $"Could not open {path}");
        }
        return handle;
    }

    /// <summary>Send a query IOCTL with no input, growing the output buffer until it fits.</summary>
    public static unsafe byte[] Query(SafeFileHandle handle, uint ioctl, int initialSize = 4096, int maxSize = 4 * 1024 * 1024)
    {
        for (var size = initialSize; size <= maxSize; size *= 2)
        {
            var buffer = new byte[size];
            fixed (byte* p = buffer)
            {
                if (NativeMethods.DeviceIoControl(handle, ioctl, null, 0, p, (uint)size, out var returned, IntPtr.Zero))
                {
                    return buffer.AsSpan(0, (int)returned).ToArray();
                }
            }
            var error = Marshal.GetLastPInvokeError();
            if (error is not (NativeMethods.ErrorInsufficientBuffer or NativeMethods.ErrorMoreData))
            {
                throw new Win32Exception(error, $"IOCTL 0x{ioctl:X8} failed");
            }
        }
        throw new InvalidOperationException($"IOCTL 0x{ioctl:X8} output exceeds {maxSize} bytes");
    }

    /// <summary>Read <paramref name="length"/> bytes at a sector-aligned offset of a handle opened with <see cref="OpenRead"/>.</summary>
    public static byte[] ReadAt(SafeFileHandle handle, long offset, int length)
    {
        var buffer = new byte[length];
        var read = RandomAccess.Read(handle, buffer, offset);
        return buffer.AsSpan(0, read).ToArray();
    }
}