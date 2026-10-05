using SMSinspector.Core.Memory;
using SMSinspector.Core.Tests.Fakes;

namespace SMSinspector.Core.Tests.Memory;

public class DolphinConnectionTests
{
    private const ulong HostA = 0x0000_0200_1000_0000;
    private const ulong HostB = 0x0000_0200_9000_0000;

    [Fact]
    public void No_dolphin()
    {
        using var connection = new DolphinConnection(new FakeHostProcessSource());

        var status = connection.Poll();

        Assert.Equal(ConnectionState.NoDolphin, status.State);
        Assert.Null(connection.Memory);
    }

    [Fact]
    public void Access_denied_is_reported_with_a_hint()
    {
        var source = new FakeHostProcessSource().AddFailing(42, HostOpenError.AccessDenied);
        using var connection = new DolphinConnection(source);

        var status = connection.Poll();

        Assert.Equal(ConnectionState.CannotOpen, status.State);
        Assert.Equal(HostOpenError.AccessDenied, status.OpenError);
        Assert.Contains("administrator", status.Describe());
    }

    [Fact]
    public void Process_that_vanished_before_opening_is_ignored()
    {
        var source = new FakeHostProcessSource().AddFailing(42, HostOpenError.NotFound);
        using var connection = new DolphinConnection(source);

        Assert.Equal(ConnectionState.NoDolphin, connection.Poll().State);
    }

    [Fact]
    public void Dolphin_without_a_game()
    {
        var process = new FakeHostProcess(7).AddRegion(HostA, 0x1000);
        using var connection = new DolphinConnection(new FakeHostProcessSource().Add(process));

        var status = connection.Poll();

        Assert.Equal(ConnectionState.NoGame, status.State);
        Assert.Equal(7, status.ProcessId);
        Assert.True(process.IsDisposed);
    }

    [Theory]
    [InlineData("GMSJ01", GameSupport.NotYetSupported)]
    [InlineData("GMSE01", GameSupport.NotInDecomp)]
    [InlineData("GABC01", GameSupport.OtherGame)]
    public void Unsupported_games_are_refused(string gameId, GameSupport support)
    {
        var process = new FakeHostProcess().AddMem1(HostA, gameId);
        using var connection = new DolphinConnection(new FakeHostProcessSource().Add(process));

        var status = connection.Poll();

        Assert.Equal(ConnectionState.UnsupportedGame, status.State);
        Assert.Equal(support, status.Support);
        Assert.Contains(gameId, status.Describe());
        Assert.Null(connection.Memory);
        Assert.True(process.IsDisposed);
    }

    [Fact]
    public void Pal_game_connects()
    {
        var process = new FakeHostProcess(7).AddMem1(HostA, "GMSP01");
        using var connection = new DolphinConnection(new FakeHostProcessSource().Add(process));

        var status = connection.Poll();

        Assert.Equal(ConnectionState.Connected, status.State);
        Assert.Equal(HostA, connection.Memory?.Location.HostBase);
        Assert.Contains("GMSP01", status.Describe());
        Assert.Contains("PID 7", status.Describe());
        Assert.False(process.IsDisposed);
    }

    [Fact]
    public void Connected_poll_does_not_rescan()
    {
        var source = new FakeHostProcessSource().Add(new FakeHostProcess().AddMem1(HostA, "GMSP01"));
        using var connection = new DolphinConnection(source);

        connection.Poll();
        connection.Poll();
        connection.Poll();

        Assert.Single(source.Opened);
    }

    [Fact]
    public void Supported_dolphin_wins_over_other_instances()
    {
        var source = new FakeHostProcessSource()
            .AddFailing(1, HostOpenError.AccessDenied)
            .Add(new FakeHostProcess(2).AddMem1(HostA, "GMSJ01"))
            .Add(new FakeHostProcess(3).AddMem1(HostB, "GMSP01"));
        using var connection = new DolphinConnection(source);

        var status = connection.Poll();

        Assert.Equal(ConnectionState.Connected, status.State);
        Assert.Equal(3, status.ProcessId);
    }

    [Fact]
    public void Dolphin_exit_drops_the_connection()
    {
        var process = new FakeHostProcess(7).AddMem1(HostA, "GMSP01");
        var source = new FakeHostProcessSource().Add(process);
        using var connection = new DolphinConnection(source);
        connection.Poll();

        process.HasExited = true;
        source.Remove(7);
        var status = connection.Poll();

        Assert.Equal(ConnectionState.NoDolphin, status.State);
        Assert.Null(connection.Memory);
        Assert.True(process.IsDisposed);
    }

    [Fact]
    public void Game_restart_is_followed_to_the_new_mapping()
    {
        // First open: MEM1 at HostA. After the game restarts, Dolphin maps it at HostB.
        var first = new FakeHostProcess(7).AddMem1(HostA, "GMSP01");
        var second = new FakeHostProcess(7).AddMem1(HostB, "GMSP01");
        var opens = 0;
        var source = new FakeHostProcessSource().Add(7, () => ++opens == 1 ? first : second);
        using var connection = new DolphinConnection(source);
        connection.Poll();

        first.RemoveRegion(HostA);
        var status = connection.Poll();

        Assert.Equal(ConnectionState.Connected, status.State);
        Assert.Equal(HostB, connection.Memory?.Location.HostBase);
        Assert.True(first.IsDisposed);
    }

    [Fact]
    public void Switching_to_another_game_disconnects()
    {
        var process = new FakeHostProcess(7).AddMem1(HostA, "GMSP01");
        using var connection = new DolphinConnection(new FakeHostProcessSource().Add(7, () => process));
        connection.Poll();

        process.ReplaceContent(HostA, Headers.Build("GABC01"));
        var status = connection.Poll();

        Assert.Equal(ConnectionState.UnsupportedGame, status.State);
        Assert.Null(connection.Memory);
    }
}
