using System.Security.Cryptography;
using SMSinspector.Core.Layouts;
using SMSinspector.Core.Names;
using SMSinspector.Core.Symbols;
using SMSinspector.Core.Tests.Fakes;
using static SMSinspector.Core.Tests.Fakes.TestDol;

namespace SMSinspector.Core.Tests.Names;

// Hand-assembled code for an invented class: which accesses count as "this", and when a
// PAL-only method makes the class a suspect.
public class PalOnlyCodeCheckTests
{
    private const uint Bl = 0x48000001;

    private static uint Sth(uint rs, short d, uint ra) => DForm(44, rs, ra, d);

    private static uint Stb(uint rs, short d, uint ra) => DForm(38, rs, ra, d);

    private static uint Mr(uint ra, uint rs) => 31u << 26 | rs << 21 | ra << 16 | rs << 11 | 444u << 1;

    private static List<ThisAccess> Decode(params uint[] code) =>
        PalOnlyCodeCheck.Accesses(0x80003000, (uint)code.Length * 4, a => code[(a - 0x80003000) / 4]);

    [Fact]
    public void This_is_followed_through_copies_and_lost_at_calls()
    {
        var accesses = Decode(
            Addi(31, 3, 0),
            Sth(0, 0x10, 31),
            Mr(30, 31),
            Bl,
            Stw(0, 0x20, 3),
            Stw(0, 0x24, 31),
            Addi(4, 31, 0x30),
            Stb(0, 2, 4),
            Lwz(0, 0x8, 30),
            Blr);

        Assert.Equal(
            ["sth@0x10", "stw@0x24", "stb@0x32", "lwz@0x8"],
            accesses.Select(a => $"{a.Mnemonic}@0x{a.Offset:X}"));
    }

    [Fact]
    public void A_register_overwritten_by_a_load_stops_being_this()
    {
        var accesses = Decode(Lwz(3, 0x4), Stw(0, 0x10, 3), Blr);

        Assert.Equal(["lwz@0x4"], accesses.Select(a => $"{a.Mnemonic}@0x{a.Offset:X}"));
    }

    private const string Header = """
        class TPanel {
        public:
            virtual void draw();
            /* 0x4 */ TPanel* mNext;
            /* 0x8 */ u32 mCount;
        };
        """;

    private static (LayoutEngine Engine, NameSources Sources) Setup(bool withOtherVersion = true)
    {
        var engine = TestHeaders.Engine(Header);
        const uint text = 0x80004000;
        var symbols = new[]
        {
            new Symbol("setTitle__6TPanelFv", ".text", text, 12, "function", "global"),
            new Symbol("draw__6TPanelFv", ".text", text + 12, 8, "function", "global"),
            new Symbol("count__6TPanelFv", ".text", text + 20, 8, "function", "global"),
        };
        var dol = Build(text,
            Addi(31, 3, 0), Sth(0, 0x6, 31), Blr,
            Sth(0, 0x4, 3), Blr,
            Stw(0, 0x8, 3), Blr);
        var executable = GameExecutable.Verify(dol, Convert.ToHexStringLower(SHA1.HashData(dol)), "main.dol", "main.dol");
        var scanned = SourceScanner.Scan("panel.hpp", Header, VersionMask.Pal);
        var methods = OriginalMethods.Build(new SymbolTable(symbols), [], scanned);
        IReadOnlySet<string>? other = withOtherVersion ? new HashSet<string> { "draw__6TPanelFv" } : null;
        return (engine, new NameSources(methods, scanned, executable, "No linker map.", false, 0, 0, 1, other));
    }

    [Fact]
    public void Pal_only_code_that_does_not_fit_the_layout_makes_a_suspect()
    {
        var (engine, sources) = Setup();

        var suspect = Assert.Single(PalOnlyCodeCheck.Run(engine, sources));

        Assert.Equal("TPanel", suspect.ClassName);
        Assert.Equal(0x6u, suspect.FirstOffset);
        Assert.Equal(0x7u, suspect.LastOffset);
        Assert.Contains("PAL-only TPanel::setTitle()", suspect.Reason);
        Assert.Contains("writes 16-bit values at 0x6..0x7", suspect.Reason);
        Assert.Contains("mNext (TPanel*)", suspect.Reason);
        Assert.DoesNotContain("draw", suspect.Reason);
        Assert.DoesNotContain("count", suspect.Reason);
    }

    [Fact]
    public void Suspects_mark_the_layout_and_the_report_without_withholding_offsets()
    {
        var (engine, sources) = Setup();
        engine.SetPalSuspects(PalOnlyCodeCheck.Run(engine, sources));

        var pal = engine.GetLayout("TPanel", VersionMask.Pal)!;
        Assert.NotNull(pal.PalSuspect);
        Assert.Equal(0x4u, pal.Field("mNext").Offset);
        Assert.Null(pal.PalUnverifiedAfter);
        Assert.Contains("PAL suspect at 0x6..0x7: PAL-only TPanel::setTitle()", LayoutText.Describe(pal));

        var report = LayoutReport.Build(engine.Catalog, engine);
        Assert.Contains("== PAL suspects", report.ToText());
        Assert.Contains("PAL suspects (PAL-only code that does not fit the layout): TPanel.", report.Summary());
    }

    [Fact]
    public void Without_the_other_version_nothing_is_called_pal_only()
    {
        var (engine, sources) = Setup(withOtherVersion: false);

        Assert.Empty(PalOnlyCodeCheck.Run(engine, sources));
    }
}
