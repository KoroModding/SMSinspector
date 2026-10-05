namespace SMSinspector.Core.Memory;

/// <summary>Game versions known to the doldecomp/sms decompilation.</summary>
public enum GameVersion
{
    /// <summary>Japanese release. The decomp's default version.</summary>
    GMSJ01,

    /// <summary>European release.</summary>
    GMSP01,
}

public enum GameSupport
{
    /// <summary>SMSinspector reads this version.</summary>
    Supported,

    /// <summary>A Super Mario Sunshine version the decomp covers but SMSinspector does not read yet.</summary>
    NotYetSupported,

    /// <summary>A Super Mario Sunshine version the decomp does not cover.</summary>
    NotInDecomp,

    /// <summary>Some other game.</summary>
    OtherGame,
}

/// <summary>Maps a game ID from the MEM1 header to what SMSinspector can do with it.</summary>
public static class GameIdentity
{
    /// <summary>The prefix shared by every Super Mario Sunshine game ID.</summary>
    private const string SunshinePrefix = "GMS";

    public static GameSupport Classify(string gameId, out GameVersion version)
    {
        switch (gameId)
        {
            case "GMSP01":
                version = GameVersion.GMSP01;
                return GameSupport.Supported;
            case "GMSJ01":
                version = GameVersion.GMSJ01;
                return GameSupport.NotYetSupported;
            default:
                version = default;
                return gameId.StartsWith(SunshinePrefix, StringComparison.Ordinal)
                    ? GameSupport.NotInDecomp
                    : GameSupport.OtherGame;
        }
    }
}
