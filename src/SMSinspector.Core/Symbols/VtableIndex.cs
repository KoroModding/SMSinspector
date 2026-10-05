using System.Diagnostics.CodeAnalysis;

namespace SMSinspector.Core.Symbols;

/// <summary>A class vtable from the symbol table.</summary>
public sealed record Vtable(Symbol Symbol, string ClassName);

/// <summary>
/// Every <c>__vt__</c> symbol, by address. Identifies the class of a polymorphic object
/// from the vtable pointer stored inside it.
/// </summary>
public sealed class VtableIndex
{
    private readonly Dictionary<uint, Vtable> _byAddress = [];
    private readonly SymbolTable _vtableSymbols;

    public VtableIndex(SymbolTable symbols)
    {
        var vtables = new List<Symbol>();
        foreach (var symbol in symbols.All)
        {
            if (CodeWarriorDemangler.TryGetVtableClass(symbol.Name, out var className))
            {
                _byAddress.TryAdd(symbol.Address, new Vtable(symbol, className));
                vtables.Add(symbol);
            }
        }

        _vtableSymbols = new SymbolTable(vtables);
    }

    public int Count => _byAddress.Count;

    public IEnumerable<Vtable> All => _byAddress.Values;

    /// <summary>
    /// Finds the vtable that <paramref name="vptr"/> points into, and how far into it.
    /// The offset says whether objects point at the vtable symbol itself or past a header.
    /// </summary>
    public bool TryResolve(uint vptr, [NotNullWhen(true)] out Vtable? vtable, out uint offset)
    {
        if (_vtableSymbols.TryFindContaining(vptr, out var symbol, out offset))
        {
            vtable = _byAddress[symbol.Address];
            return true;
        }

        vtable = null;
        return false;
    }
}
