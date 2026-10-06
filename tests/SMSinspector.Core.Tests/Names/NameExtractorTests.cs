using System.Security.Cryptography;
using SMSinspector.Core;
using SMSinspector.Core.Layouts;
using SMSinspector.Core.Names;
using SMSinspector.Core.Symbols;
using SMSinspector.Core.Tests.Fakes;
using static SMSinspector.Core.Tests.Fakes.TestDol;

namespace SMSinspector.Core.Tests.Names;

// One invented class hierarchy, invented symbols and a DOL assembled by hand.
public class NameExtractorTests
{
    private const string Header = """
        class TBase {
        public:
            virtual ~TBase();
            /* 0x4 */ u32 mFlags;
        };

        class TFooActor : public TBase {
        public:
            f32 getSpeed() const { return unk8; }
            u32 getUnk10() const { return unk10; }
            void reset() { unk8 = 0.0f; unkC = 0; unk10 = 0; }

            /* 0x8 */ f32 unk8;
            /* 0xC */ s16 unkC;
            /* 0x10 */ u32 unk10;
            /* 0x14 */ u8 unk14[4];
        };

        class TBarActor : public TBase {
        public:
            /* 0x8 */ f32 mHeight;
            /* 0xC */ s16 mTimer;
        };
        """;

    private const uint Text = 0x80003000;

    private static readonly (string Name, uint Size, uint[] Code)[] Functions =
    [
        ("getSpeed__9TFooActorCFv", 8, [Lfs(1, 0x8), Blr]),
        ("setTimer__9TFooActorFs", 8, [DForm(44, 4, 3, 0xC), Blr]),
        ("getFlags__9TFooActorCFv", 8, [Lwz(3, 0x4), Blr]),
        ("getFirst__9TFooActorCFv", 8, [Lbz(3, 0x15), Blr]),
        ("getWide__9TFooActorCFv", 8, [Lhz(3, 0x10), Blr]),
        ("getAddress__9TFooActorFv", 8, [Addi(3, 3, 0x14), Blr]),
        ("reset__9TFooActorFv", 12, [Blr, Blr, Blr]),
        ("__ct__9TFooActorFl", 8, [Stw(4, 0x10), Blr]),
    ];

    private static ExtractionReport Run(bool withDol = true)
    {
        var engine = TestHeaders.Engine(Header);
        var symbols = new List<Symbol>();
        var words = new List<uint>();
        foreach (var (name, size, code) in Functions)
        {
            symbols.Add(new Symbol(name, ".text", Text + (uint)words.Count * 4, size, "function", "global"));
            words.AddRange(code);
        }

        var dol = Build(Text, [.. words]);
        var executable = withDol
            ? GameExecutable.Verify(dol, Convert.ToHexStringLower(SHA1.HashData(dol)), "main.dol", "main.dol")
            : new ExecutableLookup(ExecutableStatus.Missing, null, "main.dol", null, null, "No executable.");

        var scanned = SourceScanner.Scan("test.hpp", Header, VersionMask.Pal);
        var methods = OriginalMethods.Build(new SymbolTable(symbols), [], scanned);
        var sources = new NameSources(methods, scanned, executable, "No linker map.", false, 0, 0, 1);
        return NameExtractor.Run(engine, sources);
    }

    private static UnknownMember Member(ExtractionReport report, string name) =>
        report.Members.Single(m => m.ClassName == "TFooActor" && m.Name == name);

    [Fact]
    public void Dol_accessor_comes_first_then_body_then_sibling()
    {
        var unk8 = Member(Run(), "unk8");

        Assert.Equal(
            [LinkLevel.DolAccessor, LinkLevel.DecompBodyMatching, LinkLevel.SiblingOffset],
            unk8.Candidates.Select(c => c.Level));
        Assert.Equal("mSpeed", unk8.Candidates[0].Suggestion);
        Assert.Equal("0x80003000: lfs f1, 0x8(r3); blr", unk8.Candidates[0].Evidence);
        Assert.Contains("getSpeed__9TFooActorCFv", unk8.Candidates[0].Source);
        Assert.Equal("mHeight", unk8.Candidates[2].Suggestion);
    }

    [Fact]
    public void Every_candidate_carries_a_provenance_to_check()
    {
        var unk8 = Member(Run(), "unk8");

        var dol = unk8.Candidates[0].Origin;
        Assert.Equal(ProvenanceKind.Executable, dol.Kind);
        Assert.Equal(0x80003000u, dol.Address);
        Assert.Equal("lfs f1, 0x8(r3); blr", dol.Detail);

        var body = unk8.Candidates[1].Origin;
        Assert.Equal(ProvenanceKind.DecompBody, body.Kind);
        Assert.Equal("test.hpp", body.File);
        Assert.Equal(9, body.Line);
        Assert.Equal("TFooActor::getSpeed", body.Detail);

        var sibling = unk8.Candidates[2].Origin;
        Assert.Equal(ProvenanceKind.Sibling, sibling.Kind);
        Assert.Equal(21, sibling.Line);
        Assert.Equal("TBarActor::mHeight", sibling.Detail);

        Assert.Equal(new SourcedName("mSpeed", dol), unk8.Candidates[0].SuggestedName);
    }

    [Fact]
    public void Setter_links_the_stored_member()
    {
        var unkC = Member(Run(), "unkC");

        var setter = unkC.Candidates[0];
        Assert.Equal(LinkLevel.DolAccessor, setter.Level);
        Assert.Equal("mTimer", setter.Suggestion);
        Assert.Null(setter.Note);
    }

    [Fact]
    public void Width_mismatch_is_kept_with_a_note()
    {
        var unk10 = Member(Run(), "unk10");

        var candidate = Assert.Single(unk10.Candidates);
        Assert.Equal(LinkLevel.DolAccessor, candidate.Level);
        Assert.Equal("mWide", candidate.Suggestion);
        Assert.Contains("2 bytes", candidate.Note);
    }

    [Fact]
    public void Accessors_that_do_not_land_on_an_unknown_member_are_counted()
    {
        var report = Run();

        Assert.Equal(3, report.Stats.AccessorLinked);
        Assert.Equal(1, report.Stats.AccessorOnNamedMember);
        Assert.Equal(1, report.Stats.AccessorInsideMember);
        Assert.Equal(1, report.Stats.Shapes[AccessorShape.AddressOf]);
        Assert.Equal(1, report.Stats.Shapes[AccessorShape.Longer]);
        Assert.Empty(Member(report, "unk14").Candidates);
    }

    [Fact]
    public void Accessors_named_by_the_decomp_only_are_ignored()
    {
        var report = Run();

        Assert.DoesNotContain(Member(report, "unk10").Candidates, c => c.Source.Contains("getUnk10"));
        Assert.Equal(1, report.Stats.BodiesNotOriginal);
    }

    [Fact]
    public void Constructors_are_not_accessors()
    {
        var unk10 = Member(Run(), "unk10");

        Assert.DoesNotContain(unk10.Candidates, c => c.Source.Contains("__ct__"));
    }

    [Fact]
    public void Long_original_bodies_are_counted_not_linked()
    {
        var report = Run();

        Assert.Equal(1, report.Stats.BodiesTooLong);
        Assert.DoesNotContain(report.Members.SelectMany(m => m.Candidates), c => c.Source.Contains("reset__"));
    }

    [Fact]
    public void Without_the_dol_only_bodies_and_siblings_remain()
    {
        var report = Run(withDol: false);

        Assert.Equal(LinkLevel.DecompBodyMatching, Member(report, "unk8").Candidates[0].Level);
        Assert.Equal(LinkLevel.SiblingOffset, Assert.Single(Member(report, "unkC").Candidates).Level);
        Assert.Empty(report.Stats.Shapes);
        Assert.Contains("main.dol was not used", report.ToText());
    }

    [Fact]
    public void Report_text_and_json_carry_levels_and_sources()
    {
        var report = Run();

        var text = report.ToText("abc123");
        Assert.Contains("TFooActor::unk8 (+0x8, f32, 0x4 bytes)", text);
        Assert.Contains("[dol accessor] mSpeed  from getSpeed__9TFooActorCFv, size 0x8", text);

        var json = report.ToJson("abc123");
        Assert.Contains("\"level\": \"dol accessor\"", json);
        Assert.Contains("\"suggestion\": \"mSpeed\"", json);
    }

    [Theory]
    [InlineData("getRollSpeed", "mRollSpeed")]
    [InlineData("isHidden", "mHidden")]
    [InlineData("onWaterHit", "mWaterHit")]
    [InlineData("update", null)]
    [InlineData("getter", null)]
    public void Accessor_names_suggest_member_names(string method, string? expected)
    {
        Assert.Equal(expected, NameExtractor.SuggestMemberName(method));
    }

    [Theory]
    [InlineData("unk1A4", true)]
    [InlineData("field_0x10", true)]
    [InlineData("unknown", false)]
    [InlineData("mUnk4", false)]
    public void Unknown_names_follow_the_decomp_convention(string name, bool expected)
    {
        Assert.Equal(expected, NameExtractor.IsUnknownName(name));
    }
}
