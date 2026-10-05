using SMSinspector.Core.Memory;
using SMSinspector.Core.Tests.Fakes;

namespace SMSinspector.Core.Tests.Memory;

public class DolphinGameMemoryTests
{
    private const ulong Host = 0x0000_0300_0000_0000;

    [Fact]
    public void Game_address_maps_to_host_base_plus_offset()
    {
        var content = Headers.Build("GMSP01", extraBytes: 0x100);
        content[0x40] = 0xAB;
        content[0x41] = 0xCD;
        var process = new FakeHostProcess().AddRegion(Host, GameCube.Mem1Size, content);
        var memory = new DolphinGameMemory(process, new Mem1Location(Host, "GMSP01", 0));

        Assert.Equal(0xABCD, memory.ReadU16(0x80000040));
        Assert.Equal(Mem1Header.Magic, memory.ReadU32(0x8000001C));
    }

    [Theory]
    [InlineData(0x7FFFFFFCu, 4)]
    [InlineData(0x817FFFFEu, 4)]
    [InlineData(0x81800000u, 1)]
    [InlineData(0x00000040u, 4)]
    public void Reads_outside_mem1_fail_without_touching_the_process(uint address, int length)
    {
        var process = new CountingProcess();
        var memory = new DolphinGameMemory(process, new Mem1Location(Host, "GMSP01", 0));

        Assert.False(memory.TryRead(address, new byte[length]));
        Assert.Equal(0, process.Reads);
    }

    [Fact]
    public void Last_bytes_of_mem1_map_to_the_end_of_the_region()
    {
        var process = new CountingProcess();
        var memory = new DolphinGameMemory(process, new Mem1Location(Host, "GMSP01", 0));

        Assert.True(memory.TryRead(0x817FFFFC, new byte[4]));
        Assert.Equal(Host + GameCube.Mem1Size - 4, process.LastAddress);
    }

    private sealed class CountingProcess : IHostProcess
    {
        public int Reads { get; private set; }

        public ulong LastAddress { get; private set; }

        public int ProcessId => 1;

        public bool HasExited => false;

        public IEnumerable<HostRegion> EnumerateRegions() => [];

        public bool TryRead(ulong hostAddress, Span<byte> destination)
        {
            Reads++;
            LastAddress = hostAddress;
            return true;
        }

        public void Dispose()
        {
        }
    }
}
