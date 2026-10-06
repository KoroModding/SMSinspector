using SMSinspector.Core.Names;
using static SMSinspector.Core.Tests.Fakes.TestDol;

namespace SMSinspector.Core.Tests.Names;

public class AccessorDecoderTests
{
    [Fact]
    public void Word_getter_gives_offset_and_width()
    {
        var code = AccessorDecoder.Decode(Lwz(3, 0x78), Blr);

        Assert.Equal(AccessorShape.Load, code.Shape);
        Assert.Equal(0x78u, code.Offset);
        Assert.Equal(4u, code.Width);
        Assert.False(code.IsFloat);
        Assert.Equal("lwz r3, 0x78(r3)", code.Instruction);
    }

    [Fact]
    public void Float_getter_loads_into_f1()
    {
        var code = AccessorDecoder.Decode(Lfs(1, 0x1A4), Blr);

        Assert.Equal(AccessorShape.Load, code.Shape);
        Assert.True(code.IsFloat);
        Assert.Equal("lfs f1, 0x1A4(r3)", code.Instruction);
    }

    [Fact]
    public void Halfword_and_byte_getters_have_their_width()
    {
        Assert.Equal(2u, AccessorDecoder.Decode(Lhz(3, 0x10), Blr).Width);
        Assert.Equal(1u, AccessorDecoder.Decode(Lbz(3, 0x11), Blr).Width);
    }

    [Fact]
    public void Setter_stores_the_first_argument()
    {
        var word = AccessorDecoder.Decode(Stw(4, 0x30), Blr);
        Assert.Equal(AccessorShape.Store, word.Shape);
        Assert.Equal("stw r4, 0x30(r3)", word.Instruction);

        var single = AccessorDecoder.Decode(Stfs(1, 0x34), Blr);
        Assert.Equal(AccessorShape.Store, single.Shape);
        Assert.Equal("stfs f1, 0x34(r3)", single.Instruction);
    }

    [Theory]
    [InlineData("store of the second argument")]
    [InlineData("load into a register that is not returned")]
    [InlineData("base register other than r3")]
    [InlineData("negative displacement")]
    [InlineData("second instruction is not blr")]
    [InlineData("instruction that is not a load or store")]
    public void Anything_else_is_not_interpreted(string what)
    {
        var (first, second) = what switch
        {
            "store of the second argument" => (Stw(5, 0x30), Blr),
            "load into a register that is not returned" => (Lwz(4, 0x30), Blr),
            "base register other than r3" => (Lwz(3, 0x30, ra: 4), Blr),
            "negative displacement" => (Lwz(3, -8), Blr),
            "second instruction is not blr" => (Lwz(3, 0x30), Lwz(3, 0x4)),
            _ => (0x7C632378u, Blr),
        };

        Assert.Equal(AccessorShape.OtherTwoInstructions, AccessorDecoder.Decode(first, second).Shape);
    }

    [Fact]
    public void Address_of_a_member_is_counted_apart()
    {
        var code = AccessorDecoder.Decode(Addi(3, 3, 0x40), Blr);

        Assert.Equal(AccessorShape.AddressOf, code.Shape);
        Assert.False(code.IsAccessor);
        Assert.Equal(0x40u, code.Offset);
    }

    [Fact]
    public void Size_decides_before_the_code_is_read()
    {
        uint? Never(uint address) => throw new InvalidOperationException("should not read");

        Assert.Equal(AccessorShape.Longer, AccessorDecoder.Decode(0x80001000, 0xC, Never).Shape);
        Assert.Equal(AccessorShape.Unreadable, AccessorDecoder.Decode(0x80001000, null, Never).Shape);
        Assert.Equal(AccessorShape.Empty, AccessorDecoder.Decode(0x80001000, 4, _ => Blr).Shape);
        Assert.Equal(AccessorShape.Unreadable, AccessorDecoder.Decode(0x80001000, 8, _ => null).Shape);
    }
}
