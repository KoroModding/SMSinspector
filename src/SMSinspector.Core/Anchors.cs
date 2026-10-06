namespace SMSinspector.Core;

public enum AnchorKind
{
    /// <summary>A global symbol, looked up in symbols.txt.</summary>
    Symbol,

    /// <summary>A class member, looked up in the headers: "Class::member".</summary>
    Member,
}

/// <summary>A name some feature starts from. It is a lookup key: its value always comes from the decomp.</summary>
public sealed record Anchor(string Name, AnchorKind Kind, string UsedBy);

/// <summary>
/// Every game name written in the code (plan 5.8). Each one is resolved in the user's
/// decomp at startup; an anchor that does not resolve disables the feature that needs it.
/// Never put an address, an offset or a type here: only names.
/// </summary>
public static class Anchors
{
    public static readonly Anchor MarioPointer = new("gpMarioAddress", AnchorKind.Symbol, "diagnostics, validation V1");

    public static readonly Anchor SceneGraphInstance = new("instance__Q26JDrama11TNameRefGen", AnchorKind.Symbol, "scene graph walk");

    public static readonly Anchor SceneGraphRoot = new("JDrama::TNameRefGen::mRootNameRef", AnchorKind.Member, "scene graph walk");

    public static readonly Anchor InstanceName = new("JDrama::TNameRef::mName", AnchorKind.Member, "instance names");

    public static readonly Anchor Spine = new("TLiveActor::mSpine", AnchorKind.Member, "nerve panel");

    public static readonly Anchor CurrentNerve = new("TSpineBase<TLiveActor>::mCurrent", AnchorKind.Member, "nerve panel");

    public static readonly Anchor PreviousNerve = new("TSpineBase<TLiveActor>::mPrevious", AnchorKind.Member, "nerve panel");

    public static readonly Anchor NerveTime = new("TSpineBase<TLiveActor>::mTime", AnchorKind.Member, "nerve panel");

    public static IReadOnlyList<Anchor> All { get; } =
    [
        MarioPointer, SceneGraphInstance, SceneGraphRoot, InstanceName, Spine, CurrentNerve, PreviousNerve, NerveTime,
    ];
}
