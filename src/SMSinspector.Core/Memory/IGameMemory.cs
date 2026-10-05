namespace SMSinspector.Core.Memory;

/// <summary>
/// Read access to the emulated game's memory, addressed with GameCube addresses
/// (0x80000000 and up). Implementations never write.
/// </summary>
public interface IGameMemory
{
    /// <summary>
    /// Fills <paramref name="destination"/> with the bytes at <paramref name="address"/>.
    /// Returns false, leaving the content of <paramref name="destination"/> undefined,
    /// when the range is outside MEM1 or the read fails.
    /// </summary>
    bool TryRead(uint address, Span<byte> destination);
}
