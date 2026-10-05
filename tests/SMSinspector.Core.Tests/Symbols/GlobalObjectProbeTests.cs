using SMSinspector.Core.Symbols;
using SMSinspector.Core.Tests.Fakes;

namespace SMSinspector.Core.Tests.Symbols;

public class GlobalObjectProbeTests
{
    // Invented layout: a global pointer at 0x80400000, an object at 0x80400010,
    // and a vtable symbol at 0x80200000.
    private const uint Start = 0x80400000;

    private static readonly SymbolTable Symbols = new(SymbolFile.Parse(
    [
        "gpFooActor = .sbss:0x80400000; // type:object size:0x4 scope:global",
        "__vt__9TFooActor = .data:0x80200000; // type:object size:0x20 scope:global",
        "sFooData = .data:0x80300000; // type:object size:0x40 scope:global",
    ]).Symbols);

    private static readonly VtableIndex Vtables = new(Symbols);

    private static FakeGameMemory Memory(uint pointer, uint vptr)
    {
        var bytes = new byte[0x20];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes, pointer);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0x10), vptr);
        return new FakeGameMemory(Start, bytes);
    }

    [Fact]
    public void Identifies_the_object_behind_a_global()
    {
        var result = GlobalObjectProbe.Probe(Memory(0x80400010, 0x80200008), Symbols, Vtables, "gpFooActor");

        Assert.Equal(ProbeOutcome.Identified, result.Outcome);
        Assert.Equal(0x80400010u, result.Pointer);
        Assert.Equal("TFooActor", result.Identity?.ClassName);
        Assert.Equal(8u, result.Identity?.OffsetIntoVtable);
        Assert.Contains("0x8 bytes past __vt__9TFooActor", result.Message);
    }

    [Fact]
    public void Null_global_means_no_object_yet()
    {
        Assert.Equal(ProbeOutcome.NullPointer, GlobalObjectProbe.Probe(Memory(0, 0), Symbols, Vtables, "gpFooActor").Outcome);
    }

    [Fact]
    public void Pointer_outside_mem1_is_invalid()
    {
        Assert.Equal(ProbeOutcome.InvalidPointer, GlobalObjectProbe.Probe(Memory(0x12345678, 0), Symbols, Vtables, "gpFooActor").Outcome);
    }

    [Fact]
    public void Vptr_outside_any_vtable_is_reported_with_its_label()
    {
        var result = GlobalObjectProbe.Probe(Memory(0x80400010, 0x80300004), Symbols, Vtables, "gpFooActor");

        Assert.Equal(ProbeOutcome.UnknownVtable, result.Outcome);
        Assert.Contains("sFooData+0x4", result.Message);
    }

    [Fact]
    public void Unknown_global_and_failed_reads()
    {
        Assert.Equal(ProbeOutcome.SymbolNotFound, GlobalObjectProbe.Probe(Memory(0, 0), Symbols, Vtables, "gpMissing").Outcome);
        Assert.Equal(ProbeOutcome.ReadFailed, GlobalObjectProbe.Probe(Memory(0x80500000, 0), Symbols, Vtables, "gpFooActor").Outcome);
    }
}
