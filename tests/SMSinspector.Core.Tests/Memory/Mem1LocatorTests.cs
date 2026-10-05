using SMSinspector.Core.Memory;
using SMSinspector.Core.Tests.Fakes;

namespace SMSinspector.Core.Tests.Memory;

public class Mem1LocatorTests
{
    private const ulong HostA = 0x0000_0200_1000_0000;
    private const ulong HostB = 0x0000_0200_4000_0000;
    private const ulong HostC = 0x0000_0200_8000_0000;

    [Fact]
    public void Finds_the_region_with_a_header()
    {
        var process = new FakeHostProcess()
            .AddRegion(HostA, 0x10000, [1, 2, 3])
            .AddMem1(HostB, "GMSP01", revision: 1);

        var location = Mem1Locator.Locate(process);

        Assert.Equal(new Mem1Location(HostB, "GMSP01", 1), location);
    }

    [Fact]
    public void Ignores_regions_that_are_small_private_or_uncommitted()
    {
        var header = Headers.Build("GMSP01");
        var process = new FakeHostProcess()
            .AddRegion(HostA, GameCube.Mem1Size - 1, header)
            .AddRegion(HostB, GameCube.Mem1Size, header, mapped: false)
            .AddRegion(HostC, GameCube.Mem1Size, header, committed: false);

        Assert.Null(Mem1Locator.Locate(process));
    }

    [Fact]
    public void Ignores_large_regions_without_a_header()
    {
        var process = new FakeHostProcess()
            .AddRegion(HostA, GameCube.Mem1Size, Headers.Build("GMSP01", magic: 0))
            .AddRegion(HostB, GameCube.Mem1Size, new byte[0x40])
            .AddMem1(HostC, "GMSP01");

        Assert.Equal(HostC, Mem1Locator.Locate(process)?.HostBase);
    }

    [Fact]
    public void Takes_the_first_of_several_aliased_views()
    {
        var process = new FakeHostProcess()
            .AddMem1(HostA, "GMSP01")
            .AddMem1(HostB, "GMSP01");

        Assert.Equal(HostA, Mem1Locator.Locate(process)?.HostBase);
    }

    [Fact]
    public void Still_valid_until_the_game_changes()
    {
        var process = new FakeHostProcess().AddMem1(HostA, "GMSP01");
        var location = Mem1Locator.Locate(process)!;

        Assert.True(Mem1Locator.StillValid(process, location));

        process.ReplaceContent(HostA, Headers.Build("GABC01"));
        Assert.False(Mem1Locator.StillValid(process, location));

        process.RemoveRegion(HostA);
        Assert.False(Mem1Locator.StillValid(process, location));
    }
}
