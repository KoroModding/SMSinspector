using System.Diagnostics.CodeAnalysis;

namespace SMSinspector.Core.Memory;

/// <summary>A region of the emulator's own address space, as the OS reports it.</summary>
public readonly record struct HostRegion(ulong BaseAddress, ulong Size, bool IsCommitted, bool IsMapped);

/// <summary>
/// The emulator process seen from the outside: its memory regions and raw reads at host
/// addresses. The OS-specific backend implements this; tests use a fake.
/// </summary>
public interface IHostProcess : IDisposable
{
    int ProcessId { get; }

    bool HasExited { get; }

    IEnumerable<HostRegion> EnumerateRegions();

    /// <summary>Reads exactly <c>destination.Length</c> bytes, or returns false.</summary>
    bool TryRead(ulong hostAddress, Span<byte> destination);
}

public enum HostOpenError
{
    None,
    AccessDenied,
    NotFound,
    Other,
}

/// <summary>Finds running Dolphin processes and opens them for reading.</summary>
public interface IHostProcessSource
{
    IReadOnlyList<int> FindDolphinProcessIds();

    bool TryOpen(int processId, [NotNullWhen(true)] out IHostProcess? process, out HostOpenError error);
}
