using SMSinspector.Core.Memory;

namespace SMSinspector.Core.Layouts;

/// <summary>Which game versions a piece of a header applies to.</summary>
[Flags]
public enum VersionMask
{
    None = 0,
    Jp = 1,
    Pal = 2,
    Both = Jp | Pal,
}

public static class VersionMaskExtensions
{
    public static VersionMask ToMask(this GameVersion version) => version switch
    {
        GameVersion.GMSJ01 => VersionMask.Jp,
        GameVersion.GMSP01 => VersionMask.Pal,
        _ => throw new ArgumentOutOfRangeException(nameof(version)),
    };

    public static bool Includes(this VersionMask mask, GameVersion version) => (mask & version.ToMask()) != 0;
}
