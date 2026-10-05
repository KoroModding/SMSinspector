using SMSinspector.Core.Memory;
using SMSinspector.Core.Tests.Fakes;

namespace SMSinspector.Core.Tests.Memory;

public class HeaderAndIdentityTests
{
    [Fact]
    public void Valid_header_gives_game_id_and_revision()
    {
        var header = Headers.Build("GMSP01", revision: 2);

        Assert.True(Mem1Header.TryParse(header, out var gameId, out var revision));
        Assert.Equal("GMSP01", gameId);
        Assert.Equal(2, revision);
    }

    [Fact]
    public void Wrong_magic_is_rejected()
    {
        var header = Headers.Build("GMSP01", magic: 0x12345678);

        Assert.False(Mem1Header.TryParse(header, out _, out _));
    }

    [Fact]
    public void Short_buffer_is_rejected()
    {
        var header = Headers.Build("GMSP01").AsSpan(0, Mem1Header.Length - 1).ToArray();

        Assert.False(Mem1Header.TryParse(header, out _, out _));
    }

    [Fact]
    public void Non_printable_game_id_is_rejected()
    {
        var header = Headers.Build("GMSP01");
        header[3] = 0x00;

        Assert.False(Mem1Header.TryParse(header, out _, out _));
    }

    [Theory]
    [InlineData("GMSP01", GameSupport.Supported)]
    [InlineData("GMSJ01", GameSupport.NotYetSupported)]
    [InlineData("GMSE01", GameSupport.NotInDecomp)]
    [InlineData("GABC01", GameSupport.OtherGame)]
    public void Game_ids_are_classified(string gameId, GameSupport expected)
    {
        Assert.Equal(expected, GameIdentity.Classify(gameId, out _));
    }

    [Fact]
    public void Pal_maps_to_its_version()
    {
        GameIdentity.Classify("GMSP01", out var version);

        Assert.Equal(GameVersion.GMSP01, version);
    }

    [Theory]
    [InlineData(0x80000000u, true)]
    [InlineData(0x817FFFFFu, true)]
    [InlineData(0x81800000u, false)]
    [InlineData(0x7FFFFFFFu, false)]
    [InlineData(0x00000000u, false)]
    public void Mem1_address_bounds(uint address, bool expected)
    {
        Assert.Equal(expected, GameCube.IsMem1Address(address));
    }

    [Theory]
    [InlineData(0x817FFFFCu, 4, true)]
    [InlineData(0x817FFFFCu, 5, false)]
    [InlineData(0x80000000u, 0, true)]
    [InlineData(0x80000000u, -1, false)]
    public void Mem1_range_bounds(uint address, int length, bool expected)
    {
        Assert.Equal(expected, GameCube.IsMem1Range(address, length));
    }
}
