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
    /// <summary>Fits the computed layout and not the comments.</summary>
    Computed,

    /// <summary>Fits the comments and not the computed layout.</summary>
    Comment,

    /// <summary>Fits both: shown, but proves nothing.</summary>
    Both,
}

/// <summary>
/// One instruction of main.dol that touches the conflicting class through <c>this</c>, and
/// what it is under each hypothesis. <paramref name="Offset"/> is relative to the class.
/// </summary>
public sealed record OffsetEvidence(
    EvidenceSupport Supports,
    bool IsAccessor,
    string Method,
    uint Address,
    string Instruction,
    uint Offset,
    string UnderComputed,
    string UnderComment)
{
    public override string ToString() =>
        $"@0x{Address:X8} {Method}: {Instruction} ({(IsAccessor ? "accessor, " : "")}computed: {UnderComputed}; comments: {UnderComment})";
}

public enum CommentVerdict
{
    /// <summary>Discriminating evidence for the computation and none against it: the layout uses the computed offsets.</summary>
    ComputationConfirmed,

    /// <summary>Discriminating evidence for the comments and none against them: the layout keeps them.</summary>
    CommentConfirmed,

    /// <summary>No discriminating evidence, or evidence both ways: PAL offsets are withheld from the conflict on.</summary>
    Unverified,
}

/// <summary>What main.dol says about the first <see cref="CommentConflict"/> of a class.</summary>
public sealed record CommentCheck(CommentConflict Conflict, CommentVerdict Verdict, IReadOnlyList<OffsetEvidence> Evidence, string? Remark = null)
{
    public string ClassName => Conflict.ClassName;

    public int ForComputed => Evidence.Count(e => e.Supports == EvidenceSupport.Computed);

    public int ForComment => Evidence.Count(e => e.Supports == EvidenceSupport.Comment);

    public int NonDiscriminating => Evidence.Count(e => e.Supports == EvidenceSupport.Both);

    /// <summary>The evidence the verdict rests on, an accessor first; null when unverified.</summary>
    public OffsetEvidence? Proof => Verdict switch
    {
        CommentVerdict.ComputationConfirmed => Best(EvidenceSupport.Computed),
        CommentVerdict.CommentConfirmed => Best(EvidenceSupport.Comment),
        _ => null,
    };

    public string Summary()
    {
        var conflict = Conflict;
        var head = $"{ClassName}: {conflict.Member.Name} commented at 0x{conflict.CommentOffset:X}, computed at 0x{conflict.ComputedOffset:X} ({(conflict.Delta > 0 ? "+" : "-")}0x{Math.Abs(conflict.Delta):X})";
        var counts = $"{ForComputed} discriminating for the computation, {ForComment} for the comments, {NonDiscriminating} non-discriminating";
        var verdict = Verdict switch
        {
            CommentVerdict.ComputationConfirmed => $"computation confirmed by main.dol @0x{Proof!.Address:X8} `{Proof.Instruction}`",
            CommentVerdict.CommentConfirmed => $"comments confirmed by main.dol @0x{Proof!.Address:X8} `{Proof.Instruction}`",
            _ => "unverified",
        };
        return $"{head}: {verdict}; {counts}{(Remark is null ? "" : $" ({Remark})")}.";
    }

    /// <summary>The note on the rows the verdict moves or withholds.</summary>
    public string RowNote() => Verdict switch
    {
        CommentVerdict.ComputationConfirmed =>
            $"Header comment 0x{Conflict.CommentOffset:X} for {Conflict.Member.Name} contradicts the computation; computation confirmed by main.dol @0x{Proof!.Address:X8} `{Proof.Instruction}`.",
        CommentVerdict.CommentConfirmed =>
            $"Header comment 0x{Conflict.CommentOffset:X} for {Conflict.Member.Name} contradicts the computed 0x{Conflict.ComputedOffset:X}; comments confirmed by main.dol @0x{Proof!.Address:X8} `{Proof.Instruction}`.",
        _ =>
            $"Header comment 0x{Conflict.CommentOffset:X} for {Conflict.Member.Name} contradicts the computed 0x{Conflict.ComputedOffset:X}, and main.dol does not settle it ({ForComputed} discriminating accesses for the computation, {ForComment} for the comments).",
    };

    private OffsetEvidence? Best(EvidenceSupport side) =>
        Evidence.Where(e => e.Supports == side).OrderByDescending(e => e.IsAccessor).ThenBy(e => e.Address).FirstOrDefault();
}
