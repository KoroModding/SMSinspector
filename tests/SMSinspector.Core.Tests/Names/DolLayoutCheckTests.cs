using System.Security.Cryptography;
using SMSinspector.Core.Layouts;
using SMSinspector.Core.Names;
using SMSinspector.Core.Symbols;
using SMSinspector.Core.Tests.Fakes;
using static SMSinspector.Core.Tests.Fakes.TestDol;

namespace SMSinspector.Core.Tests.Names;

// An invented console class whose header is 4 bytes short in PAL from unk8 on, as the
// game code (a DOL assembled by hand) shows.
public class DolLayoutCheckTests
{
    private const string Header = """
        class TConsole {
        public:
            int getTime() { return unk8; }
            void setTime(int time) { unk8 = time; }
            u32 getFlags() const { return mFlags; }

            /* 0x0 */ u32 mFlags;
            /* 0x4 */ u32 unk4;
            /* 0x8 */ int unk8;
            /* 0xC */ u32 unkC;
            /* 0x10 */ u8 unk10;
        };
        """;

    private const uint Text = 0x80004000;

    private static (LayoutEngine Engine, NameSources Sources) Setup(short timeOffset)
    {
        var engine = TestHeaders.Engine(Header);
        var symbols = new[]
        {
            new Symbol("getTime__8TConsoleFv", ".text", Text, 8, "function", "global"),
            new Symbol("setTime__8TConsoleFi", ".text", Text + 8, 8, "function", "global"),
            new Symbol("getFlags__8TConsoleCFv", ".text", Text + 16, 8, "function", "global"),
        };
        var dol = Build(Text, Lwz(3, timeOffset), Blr, Stw(4, timeOffset), Blr, Lwz(3, 0x0), Blr);
        var executable = GameExecutable.Verify(dol, Convert.ToHexStringLower(SHA1.HashData(dol)), "main.dol", "main.dol");
        var scanned = SourceScanner.Scan("console.hpp", Header, VersionMask.Pal);
        var methods = OriginalMethods.Build(new SymbolTable(symbols), [], scanned);
        return (engine, new NameSources(methods, scanned, executable, "No linker map.", false, 0, 0, 1));
    }

    [Fact]
    public void Matching_code_leaves_the_layout_alone()
    {
        var (engine, sources) = Setup(0x8);

        var result = DolLayoutCheck.Apply(engine, sources);

        Assert.Equal(3, result.Agreements);
        Assert.Empty(result.Contradictions);
        Assert.Null(engine.GetLayout("TConsole", VersionMask.Pal)!.PalUnverifiedAfter);
    }

    [Fact]
    public void Contradicting_code_withholds_pal_offsets_from_the_first_disagreement()
    {
        var (engine, sources) = Setup(0xC);

        var result = DolLayoutCheck.Apply(engine, sources);

        Assert.Equal(2, result.Disagreements.Count());
        var contradiction = Assert.Single(engine.PalContradictions);
        Assert.Equal(0x8u, contradiction.FirstOffset);

        var pal = engine.GetLayout("TConsole", VersionMask.Pal)!;
        Assert.Equal(0x4u, pal.PalUnverifiedAfter);
        Assert.Equal(0x4u, pal.Field("unk4").Offset);
        Assert.Null(pal.Field("unk8").Offset);
        Assert.Null(pal.Field("unk10").Offset);
        Assert.Equal(OffsetSource.Unknown, pal.Field("unkC").Source);
        Assert.Null(pal.Size);
        Assert.Contains("lwz r3, 0xC(r3)", pal.PalUnverifiedReason);
        Assert.Contains("unk8", pal.PalUnverifiedReason);
    }

    [Fact]
    public void Layout_text_shows_the_reason()
    {
        var (engine, sources) = Setup(0xC);
        DolLayoutCheck.Apply(engine, sources);

        var text = LayoutText.Describe(engine.GetLayout("TConsole", VersionMask.Pal)!);

        Assert.Contains("PAL offsets unverified after 0x4: main.dol has TConsole::", text);
    }

    [Fact]
    public void Jp_layout_is_not_touched()
    {
        var (engine, sources) = Setup(0xC);
        DolLayoutCheck.Apply(engine, sources);

        var jp = engine.GetLayout("TConsole", VersionMask.Jp)!;
        Assert.Equal(0x8u, jp.Field("unk8").Offset);
        Assert.Null(jp.PalUnverifiedAfter);
    }

    [Fact]
    public void Applying_again_replaces_earlier_contradictions()
    {
        var (engine, contradicting) = Setup(0xC);
        DolLayoutCheck.Apply(engine, contradicting);

        var (_, matching) = Setup(0x8);
        DolLayoutCheck.Apply(engine, matching);

        Assert.Empty(engine.PalContradictions);
        Assert.Equal(0x8u, engine.GetLayout("TConsole", VersionMask.Pal)!.Field("unk8").Offset);
    }

    [Fact]
    public void Extractor_puts_no_candidate_on_a_withheld_offset()
    {
        var (engine, sources) = Setup(0xC);

        var report = NameExtractor.Run(engine, sources);

        Assert.DoesNotContain(report.Members, m => m.Name is "unk8" or "unkC");
        Assert.Equal(3, report.Stats.UnknownWithoutOffset);
        Assert.Equal(2, report.LayoutCheck.Contradictions.Count);
        Assert.Single(report.LayoutCheck.Withheld);
        Assert.Contains("PAL offsets withheld from 0x8", report.ToText());
    }

    [Fact]
    public void Without_main_dol_nothing_is_checked()
    {
        var (engine, sources) = Setup(0xC);
        var missing = sources with { Executable = new ExecutableLookup(ExecutableStatus.Missing, null, "main.dol", null, null, "No executable.") };

        var result = DolLayoutCheck.Apply(engine, missing);

        Assert.False(result.Ran);
        Assert.Empty(engine.PalContradictions);
    }
}
