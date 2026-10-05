using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using SMSinspector.Core.Memory;

namespace SMSinspector.Core.Tests.Fakes;

/// <summary>
/// A fake emulator process. Each region may carry content for its first bytes; reads past
/// that content, or outside any region, fail like an unreadable page would.
/// </summary>
internal sealed class FakeHostProcess(int processId = 1234) : IHostProcess
{
    private readonly List<(HostRegion Region, byte[] Content)> _regions = [];

    public int ProcessId { get; } = processId;

    public bool HasExited { get; set; }

    public bool IsDisposed { get; private set; }

    public FakeHostProcess AddRegion(ulong baseAddress, ulong size, byte[]? content = null, bool committed = true, bool mapped = true)
    {
        _regions.Add((new HostRegion(baseAddress, size, committed, mapped), content ?? []));
        return this;
    }

    /// <summary>Adds a 24 MiB committed mapped region that starts with a valid header.</summary>
    public FakeHostProcess AddMem1(ulong baseAddress, string gameId, byte revision = 0, int extraBytes = 0x100) =>
        AddRegion(baseAddress, GameCube.Mem1Size, Headers.Build(gameId, revision, extraBytes));

    public void ReplaceContent(ulong baseAddress, byte[] content)
    {
        var index = _regions.FindIndex(r => r.Region.BaseAddress == baseAddress);
        _regions[index] = (_regions[index].Region, content);
    }

    public void RemoveRegion(ulong baseAddress) => _regions.RemoveAll(r => r.Region.BaseAddress == baseAddress);

    public IEnumerable<HostRegion> EnumerateRegions() => _regions.Select(r => r.Region).ToList();

    public bool TryRead(ulong hostAddress, Span<byte> destination)
    {
        foreach (var (region, content) in _regions)
        {
            if (hostAddress >= region.BaseAddress && hostAddress - region.BaseAddress + (ulong)destination.Length <= (ulong)content.Length)
            {
                content.AsSpan((int)(hostAddress - region.BaseAddress), destination.Length).CopyTo(destination);
                return true;
            }
        }

        return false;
    }

    public void Dispose() => IsDisposed = true;
}

internal sealed class FakeHostProcessSource : IHostProcessSource
{
    private readonly Dictionary<int, Func<FakeHostProcess>> _processes = [];
    private readonly Dictionary<int, HostOpenError> _failures = [];

    public List<FakeHostProcess> Opened { get; } = [];

    /// <summary>Registers a process; each open returns the instance the factory gives.</summary>
    public FakeHostProcessSource Add(int processId, Func<FakeHostProcess> factory)
    {
        _processes[processId] = factory;
        return this;
    }

    public FakeHostProcessSource Add(FakeHostProcess process) => Add(process.ProcessId, () => process);

    public FakeHostProcessSource AddFailing(int processId, HostOpenError error)
    {
        _failures[processId] = error;
        return this;
    }

    public void Remove(int processId)
    {
        _processes.Remove(processId);
        _failures.Remove(processId);
    }

    public IReadOnlyList<int> FindDolphinProcessIds() => _failures.Keys.Concat(_processes.Keys).Order().ToList();

    public bool TryOpen(int processId, [NotNullWhen(true)] out IHostProcess? process, out HostOpenError error)
    {
        if (_failures.TryGetValue(processId, out error))
        {
            process = null;
            return false;
        }

        if (!_processes.TryGetValue(processId, out var factory))
        {
            error = HostOpenError.NotFound;
            process = null;
            return false;
        }

        var opened = factory();
        Opened.Add(opened);
        process = opened;
        error = HostOpenError.None;
        return true;
    }
}

internal static class Headers
{
    /// <summary>A MEM1 header with the given game ID, followed by <paramref name="extraBytes"/> zero bytes.</summary>
    public static byte[] Build(string gameId, byte revision = 0, int extraBytes = 0, uint magic = Mem1Header.Magic)
    {
        var bytes = new byte[Mem1Header.Length + extraBytes];
        Encoding.ASCII.GetBytes(gameId).CopyTo(bytes, Mem1Header.GameIdOffset);
        bytes[Mem1Header.RevisionOffset] = revision;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(Mem1Header.MagicOffset), magic);
        return bytes;
    }
}
