using SMSinspector.Core.Memory;

namespace SMSinspector.Core.Tests.Fakes;

/// <summary>A window of game memory starting at <paramref name="start"/>. Reads outside it fail.</summary>
internal sealed class FakeGameMemory(uint start, byte[] bytes) : IGameMemory
{
    public byte[] Bytes { get; } = bytes;

    public bool TryRead(uint address, Span<byte> destination)
    {
        if (address < start || (ulong)address - start + (ulong)destination.Length > (ulong)Bytes.Length)
        {
            return false;
        }

        Bytes.AsSpan((int)(address - start), destination.Length).CopyTo(destination);
        return true;
    }
}
