using SMSinspector.Core.Symbols;
using SMSinspector.Core.Tests.Fakes;

namespace SMSinspector.Core.Tests.Symbols;

public class SymbolTableTests
{
    // Invented symbols in the format of the decomp's symbols.txt.
    private static readonly string[] Lines =
    [
        "update__9TFooActorFv = .text:0x80010000; // type:function size:0x40 scope:global align:4",
        "draw__9TFooActorFv = .text:0x80010040; // type:function size:0x20 scope:global align:4",
        "@42 = .sdata2:0x80300000; // type:object size:0x4 scope:local align:4 data:float",
        "@42 = .sdata2:0x80300010; // type:object size:0x8 scope:local align:8 data:double",
        "__vt__9TFooActor = .data:0x80200000; // type:object size:0x20 scope:global align:4",
        "__vt__Q23Gfx7TCanvas = .data:0x80200020; // type:object size:0x18 scope:weak align:4",
        "gpFooActor = .sbss:0x80400010; // type:object size:0x4 scope:global align:4 data:4byte",
        "lbl_80400020 = .sbss:0x80400020; // type:label",
        "_test_index_info = extabindex:0x80005000; // type:object size:0x20 scope:global",
        "",
        "this line is not a symbol",
    ];

    private static SymbolFile.ParseResult Parse() => SymbolFile.Parse(Lines);

    [Fact]
    public void Parses_symbols_and_reports_bad_lines()
    {
        var result = Parse();

        Assert.Equal(9, result.Symbols.Count);
        Assert.Equal([11], result.SkippedLines);

        var vtable = result.Symbols.Single(s => s.Name == "__vt__9TFooActor");
        Assert.Equal(".data", vtable.Section);
        Assert.Equal(0x80200000u, vtable.Address);
        Assert.Equal(0x20u, vtable.Size);
        Assert.Equal("object", vtable.Type);
        Assert.Equal("global", vtable.Scope);
    }

    [Fact]
    public void Attributes_are_optional_and_sections_may_lack_a_dot()
    {
        var result = Parse();

        var label = result.Symbols.Single(s => s.Name == "lbl_80400020");
        Assert.Null(label.Size);
        Assert.Null(label.Scope);
        Assert.Equal("label", label.Type);

        Assert.Equal("extabindex", result.Symbols.Single(s => s.Name == "_test_index_info").Section);
    }

    [Fact]
    public void Names_shared_by_local_symbols_are_not_unique()
    {
        var table = new SymbolTable(Parse().Symbols);

        Assert.Equal(2, table.Find("@42").Count);
        Assert.False(table.TryGetUnique("@42", out _));
        Assert.True(table.TryGetUnique("gpFooActor", out var symbol));
        Assert.Equal(0x80400010u, symbol.Address);
        Assert.Empty(table.Find("missing"));
    }

    [Fact]
    public void Finds_the_sized_symbol_covering_an_address()
    {
        var table = new SymbolTable(Parse().Symbols);

        Assert.True(table.TryFindContaining(0x80010044, out var symbol, out var offset));
        Assert.Equal("draw__9TFooActorFv", symbol.Name);
        Assert.Equal(4u, offset);

        Assert.True(table.TryFindContaining(0x80010000, out symbol, out offset));
        Assert.Equal("update__9TFooActorFv", symbol.Name);
        Assert.Equal(0u, offset);
    }

    [Fact]
    public void Addresses_in_gaps_or_in_unsized_symbols_stay_unknown()
    {
        var table = new SymbolTable(Parse().Symbols);

        Assert.False(table.TryFindContaining(0x80010060, out _, out _));
        Assert.False(table.TryFindContaining(0x80400024, out _, out _));
        Assert.False(table.TryFindContaining(0x7FFFFFFF, out _, out _));
    }

    [Fact]
    public void Labels_are_demangled_with_an_offset()
    {
        var table = new SymbolTable(Parse().Symbols);

        Assert.Equal("TFooActor::draw()+0x8", table.Label(0x80010048));
        Assert.Equal("draw__9TFooActorFv+0x8", table.Label(0x80010048, demangle: false));
        Assert.Equal("0x80010060", table.Label(0x80010060));
        Assert.Equal("gpFooActor", table.At(0x80400010)?.Name);
    }

    [Fact]
    public void Vtable_index_resolves_pointers_into_a_vtable()
    {
        var vtables = new VtableIndex(new SymbolTable(Parse().Symbols));

        Assert.Equal(2, vtables.Count);
        Assert.True(vtables.TryResolve(0x80200028, out var vtable, out var offset));
        Assert.Equal("Gfx::TCanvas", vtable.ClassName);
        Assert.Equal(8u, offset);
        Assert.False(vtables.TryResolve(0x80010000, out _, out _));
    }

    [Fact]
    public void Object_identifier_reads_the_vptr()
    {
        var vtables = new VtableIndex(new SymbolTable(Parse().Symbols));
        // An object at 0x80500000 whose first word points 8 bytes into TFooActor's vtable.
        var memory = new FakeGameMemory(0x80500000, [0x80, 0x20, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x12, 0x34, 0x56, 0x78]);

        var identity = ObjectIdentifier.Identify(memory, vtables, 0x80500000);

        Assert.NotNull(identity);
        Assert.Equal(0x80200008u, identity.Vptr);
        Assert.Equal("TFooActor", identity.ClassName);
        Assert.Equal(8u, identity.OffsetIntoVtable);

        var unknown = ObjectIdentifier.Identify(memory, vtables, 0x80500000, vptrOffset: 8);
        Assert.NotNull(unknown);
        Assert.Null(unknown.ClassName);

        Assert.Null(ObjectIdentifier.Identify(memory, vtables, 0x80600000));
    }
}
