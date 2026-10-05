using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace SMSinspector.Core.Memory.Windows;

/// <summary>
/// The few kernel32 calls SMSinspector needs. Only query and read rights are ever requested.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class NativeMethods
{
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;

    /// <summary>The only access mask SMSinspector opens a process with.</summary>
    public const uint ReadOnlyAccess = ProcessQueryInformation | ProcessVmRead;

    public const uint MemCommit = 0x1000;
    public const uint MemMapped = 0x40000;

    public const int ErrorAccessDenied = 5;
    public const int ErrorInvalidParameter = 87;

    public const uint StillActive = 259;

    [StructLayout(LayoutKind.Sequential)]
    public struct MemoryBasicInformation
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nuint VirtualQueryEx(
        SafeProcessHandle process,
        nint address,
        out MemoryBasicInformation buffer,
        nuint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReadProcessMemory(
        SafeProcessHandle process,
        nint baseAddress,
        ref byte buffer,
        nuint size,
        out nuint bytesRead);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
}
