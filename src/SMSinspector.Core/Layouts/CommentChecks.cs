namespace SMSinspector.Core.Layouts;

/// <summary>
/// An offset comment that contradicts the sizes of the members before it: the header says
/// <paramref name="CommentOffset"/>, the computation <paramref name="ComputedOffset"/>.
/// Offsets are relative to <paramref name="ClassName"/>.
/// </summary>
public sealed record CommentConflict(string ClassName, MemberDecl Member, uint CommentOffset, uint ComputedOffset)
{
    public long Delta => (long)ComputedOffset - CommentOffset;

    /// <summary>Where the two hypotheses start to differ.</summary>
    public uint FirstOffset => Math.Min(CommentOffset, ComputedOffset);
}

public enum EvidenceSupport
{
    /// <summary>Fits the computed layout and not the comments'.</summary>
    Computed,

    /// <summary>Fits the comments' layout and not the computed one.</summary>
    Comment,

    /// <summary>Fits both: shown, but proves nothing.</summary>
    Both,
}

/// <summary>
/// One instruction of main.dol that reaches the checked class through a pointer known to
/// point at it, and what it lands on under each hypothesis. <paramref name="Offset"/> is
/// relative to the class. The members are the class's own members the access falls in.
/// </summary>
public sealed record OffsetEvidence(
    EvidenceSupport Supports,
    bool IsAccessor,
    string Function,
    uint FunctionAddress,
    uint Address,
    string Instruction,
    uint Offset,
    string UnderComputed,
    string UnderComment,
    string? ComputedMember,
    string? CommentMember)
{
    public override string ToString() =>
        $"@0x{Address:X8} {Function}: {Instruction} ({(IsAccessor ? "accessor, " : "")}computed: {UnderComputed}; comments: {UnderComment})";
}

public enum CommentVerdict
{
    /// <summary>Evidence for the computation from at least two functions, none against: the computation is used.</summary>
    ComputationConfirmed,

    /// <summary>Evidence for the computation from one function only, none against: shown, offsets still withheld.</summary>
    ComputationProbable,

    /// <summary>Evidence for the comments from at least two functions, none against: the comments are kept.</summary>
    CommentConfirmed,

    /// <summary>Evidence for the comments from one function only, none against: shown, offsets still withheld.</summary>
    CommentProbable,

    /// <summary>Evidence both ways: no verdict; the per-member tally shows where it turns.</summary>
    Mixed,

    /// <summary>No discriminating evidence.</summary>
    Unverified,
}

/// <summary>How the evidence falls on one member of the checked class.</summary>
public sealed record MemberTally(
    string Member,
    uint? CommentOffset,
    uint? ComputedOffset,
    int ForComputed,
    int ForComment,
    int FunctionsForComputed,
    int FunctionsForComment);

/// <summary>
/// Two candidate layouts of one class and what main.dol says about them. Evidence counts per
/// distinct function: one function can repeat the same access many times.
/// </summary>
public abstract record HypothesisCheck(
    string ClassName,
    CommentVerdict Verdict,
    IReadOnlyList<OffsetEvidence> Evidence,
    IReadOnlyList<MemberTally> Members,
    string? Remark)
{
    public const int FunctionsToConfirm = 2;

    public int ForComputed => Evidence.Count(e => e.Supports == EvidenceSupport.Computed);

    public int ForComment => Evidence.Count(e => e.Supports == EvidenceSupport.Comment);

    public int NonDiscriminating => Evidence.Count(e => e.Supports == EvidenceSupport.Both);

    public int FunctionsForComputed => Functions(EvidenceSupport.Computed);

    public int FunctionsForComment => Functions(EvidenceSupport.Comment);

    public bool IsSettled => Verdict is CommentVerdict.ComputationConfirmed or CommentVerdict.CommentConfirmed;

    /// <summary>The evidence the verdict rests on, an accessor first; null without a leaning.</summary>
    public OffsetEvidence? Proof => Verdict switch
    {
        CommentVerdict.ComputationConfirmed or CommentVerdict.ComputationProbable => Best(EvidenceSupport.Computed),
        CommentVerdict.CommentConfirmed or CommentVerdict.CommentProbable => Best(EvidenceSupport.Comment),
        _ => null,
    };

    /// <summary>The verdict the evidence supports, by the number of distinct functions on each side.</summary>
    public static (CommentVerdict Verdict, string? Remark) Decide(IReadOnlyList<OffsetEvidence> evidence)
    {
        var forComputed = evidence.Where(e => e.Supports == EvidenceSupport.Computed).Select(e => e.FunctionAddress).Distinct().Count();
        var forComment = evidence.Where(e => e.Supports == EvidenceSupport.Comment).Select(e => e.FunctionAddress).Distinct().Count();
        return (forComputed, forComment) switch
        {
            (0, 0) => (CommentVerdict.Unverified, "no discriminating access found"),
            ( > 0, > 0) => (CommentVerdict.Mixed, "evidence both ways"),
            ( >= FunctionsToConfirm, 0) => (CommentVerdict.ComputationConfirmed, null),
            ( > 0, 0) => (CommentVerdict.ComputationProbable, "one function only"),
            (0, >= FunctionsToConfirm) => (CommentVerdict.CommentConfirmed, null),
            _ => (CommentVerdict.CommentProbable, "one function only"),
        };
    }

    protected string VerdictText(string computedSide, string commentSide) => Verdict switch
    {
        CommentVerdict.ComputationConfirmed => $"{computedSide} confirmed by main.dol @0x{Proof!.Address:X8} `{Proof.Instruction}`",
        CommentVerdict.ComputationProbable => $"{computedSide} probable (one function) @0x{Proof!.Address:X8} `{Proof.Instruction}`",
        CommentVerdict.CommentConfirmed => $"{commentSide} confirmed by main.dol @0x{Proof!.Address:X8} `{Proof.Instruction}`",
        CommentVerdict.CommentProbable => $"{commentSide} probable (one function) @0x{Proof!.Address:X8} `{Proof.Instruction}`",
        CommentVerdict.Mixed => "no verdict, evidence both ways",
        _ => "unverified",
    };

    protected string Counts() =>
        $"{ForComputed} discriminating accesses in {FunctionsForComputed} functions for the computation, "
        + $"{ForComment} in {FunctionsForComment} for the comments, {NonDiscriminating} non-discriminating";

    private int Functions(EvidenceSupport side) => Evidence.Where(e => e.Supports == side).Select(e => e.FunctionAddress).Distinct().Count();

    private OffsetEvidence? Best(EvidenceSupport side) =>
        Evidence.Where(e => e.Supports == side).OrderByDescending(e => e.IsAccessor).ThenBy(e => e.Address).FirstOrDefault();
}

/// <summary>What main.dol says about the first <see cref="CommentConflict"/> of a class.</summary>
public sealed record CommentCheck(
    CommentConflict Conflict,
    CommentVerdict Verdict,
    IReadOnlyList<OffsetEvidence> Evidence,
    IReadOnlyList<MemberTally> Members,
    string? Remark = null)
    : HypothesisCheck(Conflict.ClassName, Verdict, Evidence, Members, Remark)
{
    public string Summary()
    {
        var conflict = Conflict;
        var head = $"{ClassName}: {conflict.Member.Name} commented at 0x{conflict.CommentOffset:X}, computed at 0x{conflict.ComputedOffset:X} ({(conflict.Delta > 0 ? "+" : "-")}0x{Math.Abs(conflict.Delta):X})";
        return $"{head}: {VerdictText("computation", "comments")}; {Counts()}{(Remark is null ? "" : $" ({Remark})")}.";
    }

    /// <summary>The note on the rows the verdict moves or withholds.</summary>
    public string RowNote() => Verdict switch
    {
        CommentVerdict.ComputationConfirmed =>
            $"Header comment 0x{Conflict.CommentOffset:X} for {Conflict.Member.Name} contradicts the computation; computation confirmed by main.dol @0x{Proof!.Address:X8} `{Proof.Instruction}` ({FunctionsForComputed} functions).",
        CommentVerdict.CommentConfirmed =>
            $"Header comment 0x{Conflict.CommentOffset:X} for {Conflict.Member.Name} contradicts the computed 0x{Conflict.ComputedOffset:X}; comments confirmed by main.dol @0x{Proof!.Address:X8} `{Proof.Instruction}` ({FunctionsForComment} functions).",
        _ =>
            $"Header comment 0x{Conflict.CommentOffset:X} for {Conflict.Member.Name} contradicts the computed 0x{Conflict.ComputedOffset:X}, and main.dol does not settle it: {VerdictText("computation", "comments")} ({FunctionsForComputed} functions for the computation, {FunctionsForComment} for the comments).",
    };
}

/// <summary>
/// A class holding a member, or deriving from a base, whose own type is unverified, tested on
/// that part's size: the size its type has by the comments, and by the computation. Only
/// accesses through the holding class count, past the part's start. When it settles, the part
/// takes that size and the holding class's offsets after it are computed; the part's type
/// stays unverified on its own.
/// </summary>
/// <param name="Member">
/// The member tested, or for a base, the holding class's first own member: where its offsets
/// start to depend on the base's size.
/// </param>
public sealed record CascadeCheck(
    string ClassName,
    MemberDecl Member,
    bool IsBase,
    string MemberType,
    uint SizeByComments,
    uint SizeByComputation,
    CommentVerdict Verdict,
    IReadOnlyList<OffsetEvidence> Evidence,
    IReadOnlyList<MemberTally> Members,
    string? Remark = null)
    : HypothesisCheck(ClassName, Verdict, Evidence, Members, Remark)
{
    /// <summary>The size the evidence gives the member, when settled.</summary>
    public uint? SettledSize => Verdict switch
    {
        CommentVerdict.ComputationConfirmed => SizeByComputation,
        CommentVerdict.CommentConfirmed => SizeByComments,
        _ => null,
    };

    /// <summary>What was tested: "member mFoo" or "base TFoo".</summary>
    public string Part => IsBase ? $"base {MemberType}" : $"{Member.Name} of unverified type {MemberType}";

    public string Summary() =>
        $"{ClassName}: {Part}, 0x{SizeByComments:X} bytes by its comments, 0x{SizeByComputation:X} by the computation: "
        + $"{VerdictText($"size 0x{SizeByComputation:X}", $"size 0x{SizeByComments:X}")}; {Counts()}{(Remark is null ? "" : $" ({Remark})")}.";

    /// <summary>The note on the member whose size was settled.</summary>
    public string RowNote() =>
        $"Size 0x{SettledSize:X} of {(IsBase ? $"base {MemberType}" : Member.Name)} settled via {ClassName} ({Math.Max(FunctionsForComputed, FunctionsForComment)} functions, @0x{Proof!.Address:X8} `{Proof.Instruction}`); {MemberType} itself stays unverified.";

    /// <summary>The note on the rows placed after it.</summary>
    public string AfterNote() =>
        $"Offset computed after {(IsBase ? $"base {MemberType}" : Member.Name)}, whose size 0x{SettledSize:X} was settled via {ClassName}.";
}
