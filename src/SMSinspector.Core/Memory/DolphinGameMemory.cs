namespace SMSinspector.Core.Memory;

/// <summary>
/// <see cref="IGameMemory"/> over a located MEM1: a GameCube address maps to
/// <c>HostBase + (address - 0x80000000)</c> in the emulator process.
/// </summary>
public sealed class DolphinGameMemory(IHostProcess process, Mem1Location location) : IGameMemory
{
    public Mem1Location Location { get; } = location;

    public bool TryRead(uint address, Span<byte> destination) =>
        GameCube.IsMem1Range(address, destination.Length)
        && process.TryRead(Location.HostBase + GameCube.ToMem1Offset(address), destination);
}
