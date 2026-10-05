using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace SMSinspector.Core.Memory.Windows;

[SupportedOSPlatform("windows")]
internal sealed class WindowsHostProcess(int processId, SafeProcessHandle handle) : IHostProcess
{
    private static readonly nuint InfoSize = (nuint)Marshal.SizeOf<NativeMethods.MemoryBasicInformation>();

    public int ProcessId { get; } = processId;

    public bool HasExited
    {
        get
        {
            try
            {
                return !NativeMethods.GetExitCodeProcess(handle, out var code) || code != NativeMethods.StillActive;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
        }
    }

    public IEnumerable<HostRegion> EnumerateRegions()
    {
        ulong address = 0;

        while (true)
        {
            NativeMethods.MemoryBasicInformation info;
            try
            {
                if (NativeMethods.VirtualQueryEx(handle, (nint)address, out info, InfoSize) == 0)
                {
                    yield break;
                }
            }
            catch (ObjectDisposedException)
            {
                yield break;
            }

            var baseAddress = (ulong)info.BaseAddress;
            var size = (ulong)info.RegionSize;

            yield return new HostRegion(
                baseAddress,
                size,
                IsCommitted: info.State == NativeMethods.MemCommit,
                IsMapped: info.Type == NativeMethods.MemMapped);

            var next = baseAddress + size;
            if (size == 0 || next <= address)
            {
                yield break;
            }

            address = next;
        }
    }

    public bool TryRead(ulong hostAddress, Span<byte> destination)
    {
        if (destination.IsEmpty)
        {
            return true;
        }

        try
        {
            return NativeMethods.ReadProcessMemory(
                    handle,
                    (nint)hostAddress,
                    ref MemoryMarshal.GetReference(destination),
                    (nuint)destination.Length,
                    out var read)
                && read == (nuint)destination.Length;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public void Dispose() => handle.Dispose();
}
