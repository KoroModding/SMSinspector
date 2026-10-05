using SMSinspector.Core.Memory;
using SMSinspector.Core.Tests.Fakes;

namespace SMSinspector.Core.Tests.Memory;

public class GameMemoryExtensionsTests
{
    private const uint Start = 0x80400000;

    private static FakeGameMemory MemoryWith(params byte[] bytes) => new(Start, bytes);

    [Fact]
    public void Integers_are_read_big_endian()
    {
        var memory = MemoryWith(0x12, 0x34, 0x56, 0x78);

        Assert.Equal(0x12, memory.ReadU8(Start));
        Assert.Equal(0x1234, memory.ReadU16(Start));
        Assert.Equal(0x12345678u, memory.ReadU32(Start));
    }

    [Fact]
    public void Signed_integers_keep_their_sign()
    {
        var memory = MemoryWith(0xFF, 0xFF, 0xFF, 0xFE);

        Assert.Equal(-1, memory.ReadS16(Start));
        Assert.Equal(-2, memory.ReadS32(Start));
        Assert.True(memory.TryReadS8(Start, out var s8));
        Assert.Equal(-1, s8);
    }

    [Fact]
    public void Floats_are_read_big_endian()
    {
        // 3.0f is 0x40400000; 3.0 as a double is 0x4008000000000000.
        var memory = MemoryWith(0x40, 0x40, 0x00, 0x00, 0x40, 0x08, 0, 0, 0, 0, 0, 0);

        Assert.Equal(3.0f, memory.ReadF32(Start));
        Assert.Equal(3.0, memory.ReadF64(Start + 4));
    }

    [Fact]
    public void Failed_read_returns_false_or_throws()
    {
        var memory = MemoryWith(0x00, 0x01);

        Assert.False(memory.TryReadU32(Start, out var value));
        Assert.Equal(0u, value);

        var ex = Assert.Throws<GameMemoryReadException>(() => memory.ReadU32(Start));
        Assert.Equal(Start, ex.Address);
        Assert.Equal(4, ex.Length);
    }

    [Theory]
    [InlineData(new byte[] { 0x80, 0x40, 0x12, 0x30 }, true)]
    [InlineData(new byte[] { 0x00, 0x00, 0x00, 0x00 }, false)]
    [InlineData(new byte[] { 0x3F, 0x80, 0x00, 0x00 }, false)]
    public void Pointers_must_land_in_mem1(byte[] bytes, bool expected)
    {
        Assert.Equal(expected, MemoryWith(bytes).TryReadPointer(Start, out _));
    }
}
