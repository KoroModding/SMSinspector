using System.Buffers.Binary;
using SMSinspector.Core.Discovery;
using SMSinspector.Core.Memory;
using SMSinspector.Core.Symbols;
using SMSinspector.Core.Tests.Fakes;

namespace SMSinspector.Core.Tests.Discovery;

// One readable chunk of invented memory, with invented vtables and a static object.
public class VtableScannerTests
{
    private const uint Window = 0x80400000;
    private const uint FooVtable = 0x803D0000;
    private const uint BarVtable = 0x803D0020;
    private const uint OddVtable = 0x803D0040;
    private const string SymbolsFile = "config/GMSP01/symbols.txt";

    private static readonly Symbol[] Symbols =
    [
        new("__vt__9TFooActor", ".data", FooVtable, 0x20, "object", "global"),
        new("__vt__9TBarActor", ".data", BarVtable, 0x20, "object", "global"),
        new("__vt__9TOddActor", ".data", OddVtable, 0x20, "object", "global"),
        new("sFooInstance", ".bss", Window + 0x100, 0x40, "object", "global"),
    ];

    private static VtableScanResult Scan(Action<byte[]> fill)
    {
        var bytes = new byte[VtableScanner.ChunkSize];
        fill(bytes);
        var memory = new FakeGameMemory(Window, bytes);
        var table = new SymbolTable(Symbols);
        uint? Offset(string name) => name switch
        {
            "TFooActor" => 0,
            "TBarActor" => 4,
            _ => null,
        };

        return VtableScanner.Scan(memory, new VtableIndex(table), table, Offset, SymbolsFile);
    }

    private static void Put(byte[] bytes, uint address, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan((int)(address - Window)), value);

    [Fact]
    public void Finds_objects_by_their_vtable_pointer()
    {
        var result = Scan(b =>
        {
            Put(b, Window + 0x100, FooVtable);
            Put(b, Window + 0x2000, FooVtable);
            Put(b, Window + 0x3004, BarVtable);
        });

        Assert.Equal(3, result.Objects.Count);
        Assert.Equal(2, result.ByClass["TFooActor"].Count());

        var bar = Assert.Single(result.ByClass["TBarActor"]);
        Assert.Equal(Window + 0x3000, bar.Address);
        Assert.Equal(Window + 0x3004, bar.VptrAddress);
        Assert.True(bar.VptrOffsetKnown);
    }

    [Fact]
    public void Class_names_carry_the_vtable_symbol_as_provenance()
    {
        var result = Scan(b => Put(b, Window + 0x2000, FooVtable));

        var source = result.Objects[0].ClassName.Source;
        Assert.Equal(ProvenanceKind.Symbol, source.Kind);
        Assert.Equal(SymbolsFile, source.File);
        Assert.Equal("__vt__9TFooActor", source.Detail);
        Assert.Equal(FooVtable, source.Address);
    }

    [Fact]
    public void Static_objects_name_their_symbol()
    {
        var result = Scan(b =>
        {
            Put(b, Window + 0x100, FooVtable);
            Put(b, Window + 0x2000, FooVtable);
        });

        Assert.Equal("sFooInstance", result.At(Window + 0x100)!.StaticSymbol);
        Assert.False(result.At(Window + 0x2000)!.IsStatic);
    }

    [Fact]
    public void Pointers_inside_a_vtable_are_counted_not_used()
    {
        var result = Scan(b =>
        {
            Put(b, Window + 0x10, FooVtable + 8);
            Put(b, Window + 0x14, 0x80123456);
        });

        Assert.Empty(result.Objects);
        Assert.Equal(1, result.SecondaryPointers);
    }

    [Fact]
    public void Classes_without_a_layout_are_flagged()
    {
        var result = Scan(b => Put(b, Window + 0x40, OddVtable));

        var odd = Assert.Single(result.Objects);
        Assert.False(odd.VptrOffsetKnown);
        Assert.Equal(Window + 0x40, odd.Address);
        Assert.Contains("vptr assumed at +0x0: 1", VtableScanText.Describe(result));
    }

    [Fact]
    public void Chunks_outside_the_readable_memory_are_counted()
    {
        var result = Scan(_ => { });

        Assert.Equal(GameCube.Mem1Size / VtableScanner.ChunkSize - 1, (uint)result.UnreadableChunks);
    }

    [Fact]
    public void Summary_lists_the_most_common_classes_first()
    {
        var result = Scan(b =>
        {
            Put(b, Window + 0x2000, FooVtable);
            Put(b, Window + 0x2100, FooVtable);
            Put(b, Window + 0x3004, BarVtable);
        });

        var text = VtableScanText.Describe(result);
        Assert.Contains("3 objects of 2 classes (0 static, 3 on the heap)", text);
        Assert.True(text.IndexOf("TFooActor", StringComparison.Ordinal) < text.IndexOf("TBarActor", StringComparison.Ordinal));
    }
}
