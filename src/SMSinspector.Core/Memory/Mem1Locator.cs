namespace SMSinspector.Core.Memory;

/// <summary>Where MEM1 sits inside the emulator process, and which game it holds.</summary>
public sealed record Mem1Location(ulong HostBase, string GameId, byte Revision);

/// <summary>
/// Finds MEM1 inside a Dolphin process. Dolphin maps the emulated RAM as shared memory,
/// at an address that changes on every launch, and maps it more than once. Any committed
/// mapped region of at least 24 MiB is a candidate; the GameCube header at its start
/// decides.
/// </summary>
public static class Mem1Locator
{
    public static Mem1Location? Locate(IHostProcess process)
    {
        Span<byte> header = stackalloc byte[Mem1Header.Length];

        foreach (var region in process.EnumerateRegions())
        {
            if (!region.IsCommitted || !region.IsMapped || region.Size < GameCube.Mem1Size)
            {
                continue;
            }

            // Several views alias the same memory, so the first valid one is as good as any.
            if (process.TryRead(region.BaseAddress, header)
                && Mem1Header.TryParse(header, out var gameId, out var revision))
            {
                return new Mem1Location(region.BaseAddress, gameId, revision);
            }
        }

        return null;
    }

    /// <summary>
    /// Re-reads the header at a known location. Cheap enough to call on every poll, to
    /// notice that Dolphin stopped the game or loaded another one.
    /// </summary>
    public static bool StillValid(IHostProcess process, Mem1Location location)
    {
        Span<byte> header = stackalloc byte[Mem1Header.Length];
        return process.TryRead(location.HostBase, header)
            && Mem1Header.TryParse(header, out var gameId, out var revision)
            && gameId == location.GameId
            && revision == location.Revision;
    }
}
