using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using SMSinspector.Core.Memory;
using SMSinspector.Core.Symbols;

namespace SMSinspector.Core.Discovery;

/// <summary>A polymorphic object found by its vtable pointer.</summary>
/// <param name="Address">Start of the object: the vptr's address minus the vptr offset of its class.</param>
/// <param name="VptrAddress">Where the vtable pointer was found.</param>
/// <param name="ClassName">The class, from the vtable symbol, with that symbol as provenance.</param>
/// <param name="VptrOffsetKnown">False when the class has no layout, so the vptr was assumed at +0x0.</param>
/// <param name="StaticSymbol">The symbol that holds the object when it is a static global; null on the heap.</param>
public sealed record FoundObject(uint Address, uint VptrAddress, Vtable Vtable, SourcedName ClassName, bool VptrOffsetKnown, string? StaticSymbol)
{
    public bool IsStatic => StaticSymbol is not null;
}

public sealed record VtableScanResult(
    IReadOnlyList<FoundObject> Objects,
    int SecondaryPointers,
    int UnreadableChunks,
    TimeSpan Elapsed)
{
    public ILookup<string, FoundObject> ByClass => Objects.ToLookup(o => o.ClassName.Value, StringComparer.Ordinal);

    public FoundObject? At(uint address) => Objects.FirstOrDefault(o => o.Address == address);
}

/// <summary>
/// Finds every polymorphic object in MEM1 (plan 5.4, pass 1). The game stores in each such
/// object a pointer to its class's vtable, and the decomp names every vtable
/// <c>__vt__&lt;class&gt;</c>. So any aligned word equal to a vtable symbol's address is, very
/// likely, an object's vtable pointer. Measured in M2: the pointer equals the symbol's
/// address, with no header to skip. Read-only: MEM1 is read in chunks, never written.
/// </summary>
public static class VtableScanner
{
    public const int ChunkSize = 256 * 1024;

    /// <param name="vptrOffsetOf">Where the vptr sits in a class, from its layout; null when the class has no layout.</param>
    /// <param name="symbolsFile">The symbols file as shown in provenance, relative to the decomp clone.</param>
    public static VtableScanResult Scan(
        IGameMemory memory,
        VtableIndex vtables,
        SymbolTable symbols,
        Func<string, uint?> vptrOffsetOf,
        string symbolsFile)
    {
        var watch = Stopwatch.StartNew();
        var byAddress = vtables.All.ToDictionary(v => v.Symbol.Address);
        var names = new Dictionary<uint, SourcedName>();
        var offsets = new Dictionary<string, uint?>(StringComparer.Ordinal);

        // Words pointing inside a vtable but not at its start: secondary vptrs of classes with
        // several polymorphic bases. Counted, not used (plan 5.4).
        var vtableStart = byAddress.Count == 0 ? 0 : byAddress.Keys.Min();
        var vtableEnd = byAddress.Count == 0 ? 0 : vtables.All.Max(v => v.Symbol.Address + (v.Symbol.Size ?? 4));

        var objects = new List<FoundObject>();
        var secondary = 0;
        var unreadable = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            for (var chunk = GameCube.Mem1Base; chunk < GameCube.Mem1End; chunk += ChunkSize)
            {
                var span = buffer.AsSpan(0, ChunkSize);
                if (!memory.TryRead(chunk, span))
                {
                    unreadable++;
                    continue;
                }

                for (var i = 0; i < ChunkSize; i += 4)
                {
                    var word = BinaryPrimitives.ReadUInt32BigEndian(span[i..]);
                    if (word < vtableStart || word >= vtableEnd)
                    {
                        continue;
                    }

                    var at = chunk + (uint)i;
                    if (!byAddress.TryGetValue(word, out var vtable))
                    {
                        if (vtables.TryResolve(word, out _, out var into) && into > 0)
                        {
                            secondary++;
                        }

                        continue;
                    }

                    if (!offsets.TryGetValue(vtable.ClassName, out var vptrOffset))
                    {
                        offsets[vtable.ClassName] = vptrOffset = vptrOffsetOf(vtable.ClassName);
                    }

                    if (!names.TryGetValue(word, out var className))
                    {
                        names[word] = className = new SourcedName(vtable.ClassName, Provenance.Symbol(symbolsFile, vtable.Symbol.Name, vtable.Symbol.Address));
                    }

                    var start = at - (vptrOffset ?? 0);
                    var holder = symbols.TryFindContaining(start, out var symbol, out _) ? symbol.Name : null;
                    objects.Add(new FoundObject(start, at, vtable, className, vptrOffset is not null, holder));
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return new VtableScanResult(objects, secondary, unreadable, watch.Elapsed);
    }
}

public static class VtableScanText
{
    /// <summary>A summary for the diagnostics: totals, what was set aside, the most common classes.</summary>
    public static string Describe(VtableScanResult result, int topClasses = 25)
    {
        var byClass = result.ByClass;
        var statics = result.Objects.Count(o => o.IsStatic);
        var assumed = result.Objects.Count(o => !o.VptrOffsetKnown);
        var lines = new List<string>
        {
            $"Scanned MEM1 in {result.Elapsed.TotalMilliseconds:N0} ms: {result.Objects.Count:N0} objects of {byClass.Count:N0} classes ({statics:N0} static, {result.Objects.Count - statics:N0} on the heap).",
            $"Secondary vtable pointers, not used: {result.SecondaryPointers:N0}. Unreadable chunks: {result.UnreadableChunks:N0}.",
        };

        if (assumed > 0)
        {
            lines.Add($"Objects of classes without a layout, vptr assumed at +0x0: {assumed:N0}.");
        }

        lines.Add("Most common classes:");
        foreach (var group in byClass.OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Take(topClasses))
        {
            lines.Add($"  {group.Count(),6:N0}  {group.Key}");
        }

        return string.Join(Environment.NewLine, lines);
    }
}
