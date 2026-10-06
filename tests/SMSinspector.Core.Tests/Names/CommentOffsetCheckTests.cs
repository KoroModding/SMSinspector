using System.Security.Cryptography;
using SMSinspector.Core.Layouts;
using SMSinspector.Core.Live;
using SMSinspector.Core.Names;
using SMSinspector.Core.Symbols;
using SMSinspector.Core.Tests.Fakes;
using static SMSinspector.Core.Tests.Fakes.TestDol;

namespace SMSinspector.Core.Tests.Names;

// An invented class whose second member's comment is 8 bytes short of what the sizes give,
// and hand-assembled methods that touch it through this.
public class CommentOffsetCheckTests
{
    // Comments: mSecond at 0x08, mThird at 0x14, mScale at 0x20, size 0x24.
    // Computation: mSecond at 0x10, mThird at 0x1C, mScale at 0x28, size 0x2C.
    private const string Header = """
        struct TInner {
            TInner* mLink;
            u16 mTag;
            f32 mValue;
        };

        class TBox {
        public:
            virtual void run();
            /* 0x04 */ TInner mFirst;
            /* 0x08 */ TInner mSecond;
            /* 0x14 */ TInner mThird;
            /* 0x20 */ f32 mScale;
        };

        class TBigBox : public TBox {
        public:
            /* 0x2C */ u32 mExtra;
        };
        """;

    private const uint Text = 0x80005000;

    private static uint Sth(uint rs, short d, uint ra = 3) => DForm(44, rs, ra, d);

    // Under the computation 0x18 is mSecond.mValue (f32); under the comments, mThird.mTag (u16).
    private static readonly uint LfsOnComputedFloat = Lfs(0, 0x18);

    // Under the computation 0x14 is mSecond.mTag (u16); under the comments, mThird.mLink (a pointer).
    private static readonly uint SthOnComputedTag = Sth(0, 0x14);

    // Under the computation 0x10 is mSecond.mLink (a pointer); under the comments, mSecond.mValue (f32).
    private static readonly uint LfsOnCommentFloat = Lfs(0, 0x10);

    // A pointer under the computation, a float under the comments: a word move fits both.
    private static readonly uint LwzOnBoth = Lwz(0, 0x1C);

    private static (LayoutEngine Engine, NameSources Sources) Setup(uint[] update, bool withAccessor = false, bool withDerivedMethod = false, bool usableDol = true)
    {
        var engine = TestHeaders.Engine(Header);
        var words = new List<uint>();
        var symbols = new List<Symbol>();

        void Add(string mangled, params uint[] code)
        {
            symbols.Add(new Symbol(mangled, ".text", Text + (uint)words.Count * 4, (uint)code.Length * 4, "function", "global"));
            words.AddRange(code);
        }

        Add("update__4TBoxFv", [.. update, Blr]);
        if (withAccessor)
        {
            Add("getScale__4TBoxCFv", Lfs(1, 0x28), Blr);
        }

        if (withDerivedMethod)
        {
            Add("tick__7TBigBoxFv", Lfs(0, 0x18), Blr);
        }

        var dol = Build(Text, [.. words]);
        var hash = usableDol ? Convert.ToHexStringLower(SHA1.HashData(dol)) : new string('0', 40);
        var executable = GameExecutable.Verify(dol, hash, "main.dol", "main.dol");
        var scanned = SourceScanner.Scan("box.hpp", Header, VersionMask.Pal);
        var methods = OriginalMethods.Build(new SymbolTable(symbols), [], scanned);
        return (engine, new NameSources(methods, scanned, executable, "No linker map.", false, 0, 0, 1));
    }

    private static uint? Offset(LayoutEngine engine, string className, string member, VersionMask version = VersionMask.Pal) =>
        engine.GetLayout(className, version)!.Flatten().Single(f => f.Field.Name == member).AbsoluteOffset;

    [Fact]
    public void The_conflict_is_recorded_with_both_offsets()
    {
        var engine = TestHeaders.Engine(Header);
        var conflict = Assert.Single(engine.GetLayout("TBox", VersionMask.Pal)!.CommentConflicts);

        Assert.Equal("mSecond", conflict.Member.Name);
        Assert.Equal(0x08u, conflict.CommentOffset);
        Assert.Equal(0x10u, conflict.ComputedOffset);
        Assert.Equal(8, conflict.Delta);
        Assert.Equal(0x28u, engine.GetComputedLayout(engine.GetLayout("TBox", VersionMask.Pal)!)!.Field("mScale").Offset);
    }

    [Fact]
    public void Discriminating_accesses_confirm_the_computation()
    {
        var (engine, sources) = Setup([LfsOnComputedFloat, SthOnComputedTag, LwzOnBoth], withAccessor: true, withDerivedMethod: true);

        var check = Assert.Single(CommentOffsetCheck.Apply(engine, sources));

        Assert.Equal(CommentVerdict.ComputationConfirmed, check.Verdict);
        Assert.Equal(4, check.ForComputed);
        Assert.Equal(0, check.ForComment);
        Assert.Equal(1, check.NonDiscriminating);
        Assert.True(check.Proof!.IsAccessor);
        Assert.Equal("lfs f1, 0x28(r3)", check.Proof.Instruction);
        Assert.Contains(check.Evidence, e => e.Method == "TBigBox::tick()" && e.Supports == EvidenceSupport.Computed);

        var both = check.Evidence.Single(e => e.Supports == EvidenceSupport.Both);
        Assert.Equal("lwz r0, 0x1C(r3)", both.Instruction);
        Assert.StartsWith("mThird.mLink", both.UnderComputed);
        Assert.StartsWith("mThird.mValue", both.UnderComment);
    }

    [Fact]
    public void A_confirmed_computation_moves_the_class_and_its_derived_classes()
    {
        var (engine, sources) = Setup([LfsOnComputedFloat]);

        var checks = CommentOffsetCheck.Apply(engine, sources);

        Assert.Equal(["TBox"], checks.Select(c => c.ClassName));
        Assert.Equal(0x10u, Offset(engine, "TBox", "mSecond"));
        Assert.Equal(0x28u, Offset(engine, "TBox", "mScale"));
        Assert.Equal(0x10u, Offset(engine, "TBox", "mSecond", VersionMask.Jp));

        // The derived class's comment assumed the right base size all along.
        Assert.Equal(0x2Cu, Offset(engine, "TBigBox", "mExtra"));
        Assert.Empty(engine.GetLayout("TBigBox", VersionMask.Pal)!.CommentConflicts);
    }

    [Fact]
    public void Rows_after_the_conflict_name_the_proof()
    {
        var (engine, sources) = Setup([LfsOnComputedFloat]);
        CommentOffsetCheck.Apply(engine, sources);

        var fields = ObjectFields.Build(engine, engine.GetLayout("TBox", VersionMask.Pal)!);

        Assert.Equal($"Header comment 0x8 for mSecond contradicts the computation; computation confirmed by main.dol @0x{Text:X8} `lfs f0, 0x18(r3)`.",
            fields.Rows.Single(r => r.Path == "mThird").Note);
        Assert.Null(fields.Rows.Single(r => r.Path == "mFirst").Note);
    }

    [Fact]
    public void Discriminating_accesses_can_confirm_the_comments()
    {
        var (engine, sources) = Setup([LfsOnCommentFloat]);

        var check = CommentOffsetCheck.Apply(engine, sources).Single(c => c.ClassName == "TBox");

        Assert.Equal(CommentVerdict.CommentConfirmed, check.Verdict);
        Assert.Equal(0x08u, Offset(engine, "TBox", "mSecond"));
        Assert.StartsWith("mSecond.mLink", check.Proof!.UnderComputed);
        Assert.StartsWith("mSecond.mValue", check.Proof.UnderComment);
    }

    [Fact]
    public void Accesses_that_fit_both_layouts_leave_the_class_unverified()
    {
        var (engine, sources) = Setup([LwzOnBoth]);

        var check = CommentOffsetCheck.Apply(engine, sources).Single(c => c.ClassName == "TBox");

        Assert.Equal(CommentVerdict.Unverified, check.Verdict);
        Assert.Equal("no discriminating access found", check.Remark);
        Assert.Equal(1, check.NonDiscriminating);
        Assert.Equal(0x04u, Offset(engine, "TBox", "mFirst"));
        Assert.Null(Offset(engine, "TBox", "mSecond"));
        Assert.Contains("main.dol does not settle it", engine.GetLayout("TBox", VersionMask.Pal)!.PalUnverifiedReason);

        // JP keeps the comments: only PAL offsets are withheld.
        Assert.Equal(0x08u, Offset(engine, "TBox", "mSecond", VersionMask.Jp));
    }

    [Fact]
    public void Evidence_both_ways_leaves_the_class_unverified()
    {
        var (engine, sources) = Setup([LfsOnComputedFloat, LfsOnCommentFloat]);

        var check = CommentOffsetCheck.Apply(engine, sources).Single(c => c.ClassName == "TBox");

        Assert.Equal(CommentVerdict.Unverified, check.Verdict);
        Assert.Equal("evidence both ways", check.Remark);
        Assert.Null(check.Proof);
    }

    [Fact]
    public void Without_main_dol_nothing_is_confirmed()
    {
        var (engine, sources) = Setup([LfsOnComputedFloat], usableDol: false);

        var check = CommentOffsetCheck.Apply(engine, sources).Single(c => c.ClassName == "TBox");

        Assert.Equal(CommentVerdict.Unverified, check.Verdict);
        Assert.Equal("main.dol not available", check.Remark);
    }

    [Fact]
    public void The_report_lists_every_check()
    {
        var (engine, sources) = Setup([LfsOnComputedFloat]);
        CommentOffsetCheck.Apply(engine, sources);

        var report = LayoutReport.Build(engine.Catalog, engine);

        Assert.Contains("Offset comments contradicting the computation: 1 classes; main.dol confirms the computation for 1, the comments for 0, 0 unverified.", report.Summary());
        Assert.Contains("== Offset comments against the computation", report.ToText());
        Assert.Contains("TBox: mSecond commented at 0x8, computed at 0x10 (+0x8): computation confirmed", report.ToText());
    }
}
