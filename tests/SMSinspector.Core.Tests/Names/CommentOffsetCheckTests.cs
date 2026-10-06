using System.Security.Cryptography;
using SMSinspector.Core.Layouts;
using SMSinspector.Core.Live;
using SMSinspector.Core.Names;
using SMSinspector.Core.Symbols;
using SMSinspector.Core.Tests.Fakes;
using static SMSinspector.Core.Tests.Fakes.TestDol;

namespace SMSinspector.Core.Tests.Names;

// Invented classes whose offset comments disagree with the sizes, and hand-assembled
// functions that touch them through this or through a pointer parameter.
public class CommentOffsetCheckTests
{
    // TBox. Comments: mSecond at 0x08, mThird at 0x14, mScale at 0x20, size 0x24.
    // Computation: mSecond at 0x10, mThird at 0x1C, mScale at 0x28, size 0x2C.
    // TRack: mPair commented at 0x06, computed at 0x08.
    // THolder holds a TPart, whose comments make it 0xC bytes and the computation 0x8; TPartUser derives from it.
    // TLamp: mTime commented at 0x0C, computed at 0x08, so mGlow (a 4-byte structure) moves from 0x10 to 0x0C.
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
            /* 0x30 */ f32 mRate;
            /* 0x34 */ u16 mCount;
        };

        struct TPair {
            u32 mA;
            u32 mB;
        };

        class TRack {
        public:
            virtual void run();
            /* 0x04 */ u8 mFlag;
            /* 0x06 */ TPair mPair;
        };

        struct TPart {
            /* 0x0 */ u32 mA;
            /* 0x8 */ u32 mB;
        };

        class THolder {
        public:
            virtual void run();
            /* 0x04 */ TPart mPart;
            f32 mSpeed;
        };

        class TPartUser : public TPart {
        public:
            f32 mSpeed;
        };

        struct TRgba {
            u8 r;
            u8 g;
            u8 b;
            u8 a;
        };

        class TLamp {
        public:
            virtual void run();
            /* 0x04 */ TRgba mColor;
            /* 0x0C */ u32 mTime;
            /* 0x10 */ TRgba mGlow;
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

    private sealed class Program
    {
        public List<uint> Words { get; } = [];

        public List<Symbol> Symbols { get; } = [];

        public List<FreeFunction> Free { get; } = [];

        public Program Method(string mangled, params uint[] code)
        {
            Symbols.Add(new Symbol(mangled, ".text", Text + (uint)Words.Count * 4, (uint)(code.Length + 1) * 4, "function", "global"));
            Words.AddRange([.. code, Blr]);
            return this;
        }

        public Program Function(string name, string pointedType, params uint[] code)
        {
            Free.Add(new FreeFunction(name, Text + (uint)Words.Count * 4, (uint)(code.Length + 1) * 4, pointedType, "test.h:1"));
            Words.AddRange([.. code, Blr]);
            return this;
        }
    }

    private static readonly CommentCheckOptions WithoutCopies = new(StructureCopies: false);

    private static readonly CommentCheckOptions WithoutOrder = new(DependencyOrder: false);

    private static (LayoutEngine Engine, NameSources Sources) Setup(Program program, bool usableDol = true)
    {
        var engine = TestHeaders.Engine(Header);
        var dol = Build(Text, [.. program.Words]);
        var hash = usableDol ? Convert.ToHexStringLower(SHA1.HashData(dol)) : new string('0', 40);
        var executable = GameExecutable.Verify(dol, hash, "main.dol", "main.dol");
        var scanned = SourceScanner.Scan("box.hpp", Header, VersionMask.Pal);
        var methods = OriginalMethods.Build(new SymbolTable(program.Symbols), [], scanned);
        return (engine, new NameSources(methods, scanned, executable, "No linker map.", false, 0, 0, 1, null, program.Free));
    }

    private static CommentCheck Check(CommentCheckResult result, string className) => result.Comments.Single(c => c.ClassName == className);

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
    public void Two_functions_confirm_the_computation()
    {
        var (engine, sources) = Setup(new Program()
            .Method("update__4TBoxFv", LfsOnComputedFloat, SthOnComputedTag, LwzOnBoth)
            .Method("getScale__4TBoxCFv", Lfs(1, 0x28))
            .Method("tick__7TBigBoxFv", Lfs(0, 0x18)));

        var check = Check(CommentOffsetCheck.Apply(engine, sources), "TBox");

        Assert.Equal(CommentVerdict.ComputationConfirmed, check.Verdict);
        Assert.Equal(4, check.ForComputed);
        Assert.Equal(3, check.FunctionsForComputed);
        Assert.Equal(0, check.ForComment);
        Assert.Equal(1, check.NonDiscriminating);
        Assert.True(check.Proof!.IsAccessor);
        Assert.Equal("lfs f1, 0x28(r3)", check.Proof.Instruction);
        Assert.Contains(check.Evidence, e => e.Function == "TBigBox::tick()" && e.Supports == EvidenceSupport.Computed);

        var both = check.Evidence.Single(e => e.Supports == EvidenceSupport.Both);
        Assert.Equal("lwz r0, 0x1C(r3)", both.Instruction);
        Assert.StartsWith("mThird.mLink", both.UnderComputed);
        Assert.StartsWith("mThird.mValue", both.UnderComment);
    }

    [Fact]
    public void A_confirmed_computation_moves_the_class_and_its_derived_classes()
    {
        var (engine, sources) = Setup(new Program()
            .Method("update__4TBoxFv", LfsOnComputedFloat)
            .Method("tick__7TBigBoxFv", SthOnComputedTag));

        var result = CommentOffsetCheck.Apply(engine, sources);

        Assert.Equal(CommentVerdict.ComputationConfirmed, Check(result, "TBox").Verdict);
        Assert.DoesNotContain(result.Comments, c => c.ClassName == "TBigBox");
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
        var (engine, sources) = Setup(new Program()
            .Method("update__4TBoxFv", LfsOnComputedFloat)
            .Method("tick__7TBigBoxFv", SthOnComputedTag));
        CommentOffsetCheck.Apply(engine, sources);

        var fields = ObjectFields.Build(engine, engine.GetLayout("TBox", VersionMask.Pal)!);

        Assert.Equal($"Header comment 0x8 for mSecond contradicts the computation; computation confirmed by main.dol @0x{Text:X8} `lfs f0, 0x18(r3)` (2 functions).",
            fields.Rows.Single(r => r.Path == "mThird").Note);
        Assert.Null(fields.Rows.Single(r => r.Path == "mFirst").Note);
    }

    [Fact]
    public void One_function_is_only_probable_and_moves_nothing()
    {
        var (engine, sources) = Setup(new Program().Method("update__4TBoxFv", LfsOnComputedFloat, SthOnComputedTag));

        var check = Check(CommentOffsetCheck.Apply(engine, sources), "TBox");

        Assert.Equal(CommentVerdict.ComputationProbable, check.Verdict);
        Assert.Equal(2, check.ForComputed);
        Assert.Equal(1, check.FunctionsForComputed);
        Assert.Equal("lfs f0, 0x18(r3)", check.Proof!.Instruction);
        Assert.Contains("computation probable (one function)", check.Summary());
        Assert.Null(Offset(engine, "TBox", "mSecond"));
        Assert.Contains("does not settle it", engine.GetLayout("TBox", VersionMask.Pal)!.PalUnverifiedReason);
    }

    [Fact]
    public void Two_functions_can_confirm_the_comments()
    {
        var (engine, sources) = Setup(new Program()
            .Method("update__4TBoxFv", LfsOnCommentFloat)
            .Method("tick__7TBigBoxFv", LfsOnCommentFloat));

        var check = Check(CommentOffsetCheck.Apply(engine, sources), "TBox");

        Assert.Equal(CommentVerdict.CommentConfirmed, check.Verdict);
        Assert.Equal(0x08u, Offset(engine, "TBox", "mSecond"));
        Assert.StartsWith("mSecond.mLink", check.Proof!.UnderComputed);
        Assert.StartsWith("mSecond.mValue", check.Proof.UnderComment);
    }

    [Fact]
    public void Accesses_that_fit_both_layouts_leave_the_class_unverified()
    {
        var (engine, sources) = Setup(new Program().Method("update__4TBoxFv", LwzOnBoth));

        var check = Check(CommentOffsetCheck.Apply(engine, sources), "TBox");

        Assert.Equal(CommentVerdict.Unverified, check.Verdict);
        Assert.Equal("no discriminating access found", check.Remark);
        Assert.Equal(1, check.NonDiscriminating);
        Assert.Equal(0x04u, Offset(engine, "TBox", "mFirst"));
        Assert.Null(Offset(engine, "TBox", "mSecond"));

        // JP keeps the comments: only PAL offsets are withheld.
        Assert.Equal(0x08u, Offset(engine, "TBox", "mSecond", VersionMask.Jp));
    }

    [Fact]
    public void A_word_inside_a_structure_may_be_a_copy_and_proves_nothing()
    {
        // Under the computation 0x14 is inside mSecond (a halfword member there); under the comments, mThird.mLink.
        var program = new Program().Method("update__4TBoxFv", Lwz(0, 0x14));

        var (engine, sources) = Setup(program);
        Assert.Empty(Check(CommentOffsetCheck.Apply(engine, sources), "TBox").Evidence);

        // Without the rule the same word looks like evidence for the comments: the rule only takes evidence away.
        (engine, sources) = Setup(program);
        Assert.Equal(EvidenceSupport.Comment, Assert.Single(Check(CommentOffsetCheck.Apply(engine, sources, WithoutCopies), "TBox").Evidence).Supports);
    }

    [Fact]
    public void A_word_at_the_start_of_a_four_byte_structure_fits_it()
    {
        // 0x0C is mTime (u32) by the comments and the start of mGlow (four bytes) by the computation.
        var program = new Program().Method("update__5TLampFv", Lwz(0, 0xC));

        var (engine, sources) = Setup(program);
        var evidence = Assert.Single(Check(CommentOffsetCheck.Apply(engine, sources), "TLamp").Evidence);
        Assert.Equal(EvidenceSupport.Both, evidence.Supports);
        Assert.Equal("start of mGlow (4-byte structure)", evidence.UnderComputed);

        (engine, sources) = Setup(program);
        Assert.Equal(EvidenceSupport.Comment, Assert.Single(Check(CommentOffsetCheck.Apply(engine, sources, WithoutCopies), "TLamp").Evidence).Supports);
    }

    [Fact]
    public void A_derived_class_waits_for_its_base()
    {
        // TBox grows by 8 bytes, so TBigBox's own comments were right all along. Taken before TBox is
        // settled, TBigBox has a conflict of its own (its members computed 8 bytes early) and evidence
        // for its comments: its code reads mExtra as a word at 0x2C, where its computation has mCount (u16).
        var program = new Program()
            .Method("update__4TBoxFv", LfsOnComputedFloat)
            .Method("tick__4TBoxFv", SthOnComputedTag)
            .Method("show__7TBigBoxFv", Lwz(0, 0x2C))
            .Method("hide__7TBigBoxFv", Lwz(0, 0x2C));

        var (engine, sources) = Setup(program);
        var result = CommentOffsetCheck.Apply(engine, sources);
        Assert.Equal(CommentVerdict.ComputationConfirmed, Check(result, "TBox").Verdict);
        Assert.DoesNotContain(result.Comments, c => c.ClassName == "TBigBox");
        Assert.Equal(0x2Cu, Offset(engine, "TBigBox", "mExtra"));

        // Without the order, TBigBox gets a verdict of its own in the same round as TBox, on a base it assumed wrong.
        (engine, sources) = Setup(program);
        Assert.Equal(CommentVerdict.CommentConfirmed, Check(CommentOffsetCheck.Apply(engine, sources, WithoutOrder), "TBigBox").Verdict);
    }

    [Fact]
    public void A_class_deriving_from_an_unverified_base_is_tested_on_the_base_size()
    {
        // TPart stays unverified; TPartUser's own member says how big the base is.
        var (engine, sources) = Setup(new Program()
            .Method("move__9TPartUserFv", Lfs(0, 0x8))
            .Method("stop__9TPartUserFv", Lfs(1, 0x8)));

        var result = CommentOffsetCheck.Apply(engine, sources);

        Assert.Equal(CommentVerdict.Unverified, Check(result, "TPart").Verdict);
        var cascade = result.Cascades.Single(c => c.ClassName == "TPartUser");
        Assert.True(cascade.IsBase);
        Assert.Equal(CommentVerdict.ComputationConfirmed, cascade.Verdict);
        Assert.Equal(0x8u, Offset(engine, "TPartUser", "mSpeed"));
        var fields = ObjectFields.Build(engine, engine.GetLayout("TPartUser", VersionMask.Pal)!);
        Assert.Equal("Offset computed after base TPart, whose size 0x8 was settled via TPartUser.", fields.Rows.Single(r => r.Path == "mSpeed").Note);
    }

    [Fact]
    public void Evidence_both_ways_gives_no_verdict_and_a_tally_per_member()
    {
        var (engine, sources) = Setup(new Program()
            .Method("update__4TBoxFv", LfsOnComputedFloat)
            .Method("tick__7TBigBoxFv", LfsOnCommentFloat));

        var check = Check(CommentOffsetCheck.Apply(engine, sources), "TBox");

        Assert.Equal(CommentVerdict.Mixed, check.Verdict);
        Assert.Null(check.Proof);
        var second = check.Members.Single(m => m.Member == "mSecond");
        Assert.Equal((0x08u, 0x10u, 1, 1), (second.CommentOffset!.Value, second.ComputedOffset!.Value, second.ForComputed, second.ForComment));

        var text = LayoutReport.Build(engine.Catalog, engine).ToText();
        Assert.Contains("for computation    for comments", text);
        Assert.Contains("mSecond", text[text.IndexOf("for computation    for comments", StringComparison.Ordinal)..]);
    }

    [Fact]
    public void A_free_function_taking_a_pointer_to_the_class_counts()
    {
        var (engine, sources) = Setup(new Program()
            .Function("ResetBox", "TBox", LfsOnComputedFloat)
            .Method("update__4TBoxFv", SthOnComputedTag));

        var check = Check(CommentOffsetCheck.Apply(engine, sources), "TBox");

        Assert.Equal(CommentVerdict.ComputationConfirmed, check.Verdict);
        Assert.Contains(check.Evidence, e => e.Function.StartsWith("ResetBox (first parameter TBox*", StringComparison.Ordinal));
    }

    [Fact]
    public void Taking_the_address_of_a_structure_member_counts_when_it_discriminates()
    {
        // The computation puts mPair at 0x08; the comments put it at 0x06, so 0x08 is inside mPair.mA.
        var (engine, sources) = Setup(new Program().Method("reset__5TRackFv", Addi(4, 3, 0x8)));

        var check = Check(CommentOffsetCheck.Apply(engine, sources), "TRack");

        var evidence = Assert.Single(check.Evidence);
        Assert.Equal(EvidenceSupport.Computed, evidence.Supports);
        Assert.Equal("addi r4, r3, 0x8", evidence.Instruction);
        Assert.Equal("start of mPair", evidence.UnderComputed);
    }

    [Fact]
    public void A_class_holding_an_unverified_member_is_tested_on_that_size()
    {
        // TPart has no method: it stays unverified. THolder's own accesses say how big it is.
        var (engine, sources) = Setup(new Program()
            .Method("move__7THolderFv", Lfs(0, 0xC))
            .Method("stop__7THolderFv", Lfs(1, 0xC)));

        var result = CommentOffsetCheck.Apply(engine, sources);

        Assert.Equal(CommentVerdict.Unverified, Check(result, "TPart").Verdict);
        var cascade = result.Cascades.Single(c => c.ClassName == "THolder");
        Assert.Equal(CommentVerdict.ComputationConfirmed, cascade.Verdict);
        Assert.Equal((0xCu, 0x8u), (cascade.SizeByComments, cascade.SizeByComputation));
        Assert.Equal(0x8u, cascade.SettledSize);

        Assert.Equal(0xCu, Offset(engine, "THolder", "mSpeed"));
        Assert.Null(Offset(engine, "TPart", "mB"));
        var fields = ObjectFields.Build(engine, engine.GetLayout("THolder", VersionMask.Pal)!);
        Assert.StartsWith("Size 0x8 of mPart settled via THolder (2 functions", fields.Rows.Single(r => r.Path == "mPart").Note);
        Assert.EndsWith("TPart itself stays unverified.", fields.Rows.Single(r => r.Path == "mPart").Note);
    }

    [Fact]
    public void Without_main_dol_nothing_is_confirmed()
    {
        var (engine, sources) = Setup(new Program().Method("update__4TBoxFv", LfsOnComputedFloat), usableDol: false);

        var check = Check(CommentOffsetCheck.Apply(engine, sources), "TBox");

        Assert.Equal(CommentVerdict.Unverified, check.Verdict);
        Assert.Equal("main.dol not available", check.Remark);
    }

    [Fact]
    public void The_report_lists_every_check()
    {
        var (engine, sources) = Setup(new Program()
            .Method("update__4TBoxFv", LfsOnComputedFloat)
            .Method("tick__7TBigBoxFv", SthOnComputedTag));
        CommentOffsetCheck.Apply(engine, sources);

        var report = LayoutReport.Build(engine.Catalog, engine);

        Assert.Contains("Offset comments contradicting the computation:", report.Summary());
        Assert.Contains("== Offset comments against the computation", report.ToText());
        Assert.Contains("TBox: mSecond commented at 0x8, computed at 0x10 (+0x8): computation confirmed", report.ToText());
    }

    [Fact]
    public void Prototypes_give_the_first_parameter_of_c_functions()
    {
        const string source = """
            void ResetBox(TBox* box, int mode);
            static inline const TBox* PeekBox(const struct TBox *box)
            {
                return box;
            }
            void Unrelated(int value);
            void TwoLevels(TBox** boxes);
            """;
        var prototypes = FreeFunctions.Scan("box.h", source).ToList();
        var symbols = new SymbolTable([
            new Symbol("ResetBox", ".text", 0x80001000, 0x20, "function", "global"),
            new Symbol("PeekBox", ".text", 0x80001020, 0x8, "function", "local"),
            new Symbol("Unrelated", ".text", 0x80001028, 0x8, "function", "global"),
            new Symbol("ShowBox__FP4TBox", ".text", 0x80001030, 0x8, "function", "global"),
        ]);

        var functions = FreeFunctions.Build(symbols, prototypes);

        Assert.Equal(["ResetBox:TBox:box.h:1", "PeekBox:TBox:box.h:2", "ShowBox:TBox:ShowBox__FP4TBox"],
            functions.Select(f => $"{f.Name}:{f.FirstParameterType}:{f.Source}"));
    }
}
