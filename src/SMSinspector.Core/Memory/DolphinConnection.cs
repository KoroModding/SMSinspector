namespace SMSinspector.Core.Memory;

/// <summary>Connection states, ordered from least to most useful.</summary>
public enum ConnectionState
{
    /// <summary>No Dolphin process is running.</summary>
    NoDolphin,

    /// <summary>Dolphin runs but could not be opened for reading.</summary>
    CannotOpen,

    /// <summary>Dolphin runs but no game is booted.</summary>
    NoGame,

    /// <summary>A game is booted, but not one SMSinspector reads.</summary>
    UnsupportedGame,

    Connected,
}

public sealed record ConnectionStatus(
    ConnectionState State,
    int? ProcessId = null,
    HostOpenError OpenError = HostOpenError.None,
    Mem1Location? Location = null,
    GameSupport? Support = null)
{
    public static ConnectionStatus Disconnected { get; } = new(ConnectionState.NoDolphin);

    /// <summary>One line for the user, in English.</summary>
    public string Describe() => State switch
    {
        ConnectionState.NoDolphin => "Dolphin is not running.",
        ConnectionState.CannotOpen when OpenError == HostOpenError.AccessDenied =>
            $"Dolphin (PID {ProcessId}) refused access. If Dolphin runs as administrator, run SMSinspector as administrator too.",
        ConnectionState.CannotOpen => $"Dolphin (PID {ProcessId}) could not be opened ({OpenError}).",
        ConnectionState.NoGame => $"Dolphin (PID {ProcessId}) is running, but no GameCube game is booted.",
        ConnectionState.UnsupportedGame => Support switch
        {
            GameSupport.NotYetSupported => $"{Location!.GameId} is not supported yet. SMSinspector reads GMSP01 (PAL) for now.",
            GameSupport.NotInDecomp => $"{Location!.GameId} is not covered by the decomp. SMSinspector reads GMSP01 (PAL).",
            _ => $"{Location!.GameId} is not Super Mario Sunshine.",
        },
        ConnectionState.Connected =>
            $"Dolphin found (PID {ProcessId}), {Location!.GameId} rev {Location.Revision}, MEM1 at host 0x{Location.HostBase:X}.",
        _ => State.ToString(),
    };
}

/// <summary>
/// Keeps a read-only connection to a Dolphin running a supported game. Call
/// <see cref="Poll"/> regularly (about once a second): it notices when Dolphin exits,
/// stops the game or boots another one, and connects again when it can.
/// Not thread-safe; callers serialize access.
/// </summary>
public sealed class DolphinConnection(IHostProcessSource source) : IDisposable
{
    private IHostProcess? _process;

    /// <summary>The game's memory while <see cref="Status"/> is Connected, otherwise null.</summary>
    public DolphinGameMemory? Memory { get; private set; }

    public ConnectionStatus Status { get; private set; } = ConnectionStatus.Disconnected;

    public ConnectionStatus Poll()
    {
        if (_process is not null && Memory is not null)
        {
            if (!_process.HasExited && Mem1Locator.StillValid(_process, Memory.Location))
            {
                return Status;
            }

            Drop();
        }

        Status = Connect();
        return Status;
    }

    public void Dispose() => Drop();

    private ConnectionStatus Connect()
    {
        var best = ConnectionStatus.Disconnected;

        foreach (var pid in source.FindDolphinProcessIds())
        {
            if (!source.TryOpen(pid, out var process, out var error))
            {
                // The process vanished between listing and opening: nothing to report.
                if (error != HostOpenError.NotFound)
                {
                    best = Best(best, new ConnectionStatus(ConnectionState.CannotOpen, pid, error));
                }

                continue;
            }

            var location = Mem1Locator.Locate(process);
            if (location is null)
            {
                process.Dispose();
                best = Best(best, new ConnectionStatus(ConnectionState.NoGame, pid));
                continue;
            }

            var support = GameIdentity.Classify(location.GameId, out _);
            if (support != GameSupport.Supported)
            {
                process.Dispose();
                best = Best(best, new ConnectionStatus(ConnectionState.UnsupportedGame, pid, Location: location, Support: support));
                continue;
            }

            _process = process;
            Memory = new DolphinGameMemory(process, location);
            return new ConnectionStatus(ConnectionState.Connected, pid, Location: location, Support: support);
        }

        return best;
    }

    private void Drop()
    {
        Memory = null;
        _process?.Dispose();
        _process = null;
        Status = ConnectionStatus.Disconnected;
    }

    private static ConnectionStatus Best(ConnectionStatus a, ConnectionStatus b) => b.State > a.State ? b : a;
}
