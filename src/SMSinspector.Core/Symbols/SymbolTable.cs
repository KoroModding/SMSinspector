using System.Diagnostics.CodeAnalysis;

namespace SMSinspector.Core.Symbols;

/// <summary>
/// Symbols indexed by name and by address. Several local symbols can share a name
/// (compiler-generated <c>@123</c> constants, for instance), so lookups by name return
/// every match and <see cref="TryGetUnique"/> only succeeds when there is exactly one.
/// </summary>
public sealed class SymbolTable
{
    private readonly Dictionary<string, Symbol[]> _byName;
    private readonly Symbol[] _byAddress;
    private readonly uint[] _addresses;

    public SymbolTable(IEnumerable<Symbol> symbols)
    {
        var all = symbols.ToArray();
        _byName = all.GroupBy(s => s.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);

        // At equal addresses, sized and non-local symbols come first: they make better labels.
        _byAddress = all
            .OrderBy(s => s.Address)
            .ThenBy(s => s.Size is null)
            .ThenBy(s => s.IsLocal)
            .ToArray();
        _addresses = _byAddress.Select(s => s.Address).ToArray();
    }

    public int Count => _byAddress.Length;

    public IReadOnlyList<Symbol> All => _byAddress;

    public IReadOnlyList<Symbol> Find(string name) => _byName.TryGetValue(name, out var found) ? found : [];

    public bool TryGetUnique(string name, [NotNullWhen(true)] out Symbol? symbol)
    {
        symbol = _byName.TryGetValue(name, out var found) && found.Length == 1 ? found[0] : null;
        return symbol is not null;
    }

    /// <summary>The best symbol starting exactly at <paramref name="address"/>.</summary>
    public Symbol? At(uint address)
    {
        var index = Array.BinarySearch(_addresses, address);
        if (index < 0)
        {
            return null;
        }

        while (index > 0 && _addresses[index - 1] == address)
        {
            index--;
        }

        return _byAddress[index];
    }

    /// <summary>
    /// The sized symbol whose range covers <paramref name="address"/>, with the offset
    /// into it. Symbols without a size never match, so an address in a gap stays unknown
    /// instead of being pinned on the previous symbol.
    /// </summary>
    public bool TryFindContaining(uint address, [NotNullWhen(true)] out Symbol? symbol, out uint offset)
    {
        // Last index whose address is <= the target.
        var index = Array.BinarySearch(_addresses, address);
        if (index < 0)
        {
            index = ~index - 1;
        }
        else
        {
            while (index + 1 < _addresses.Length && _addresses[index + 1] == address)
            {
                index++;
            }
        }

        // Walk back over symbols that start at or before the address; nested or
        // overlapping symbols are rare, so this stays short.
        for (var i = index; i >= 0 && i > index - 16; i--)
        {
            var candidate = _byAddress[i];
            if (candidate.Contains(address))
            {
                // Prefer the best-ranked symbol among those starting at the same address.
                var first = i;
                while (first > 0 && _addresses[first - 1] == candidate.Address && _byAddress[first - 1].Contains(address))
                {
                    first--;
                }

                symbol = _byAddress[first];
                offset = address - symbol.Address;
                return true;
            }
        }

        symbol = null;
        offset = 0;
        return false;
    }

    /// <summary>"name+0x1C", the demangled name, or the hex address when nothing covers it.</summary>
    public string Label(uint address, bool demangle = true)
    {
        if (!TryFindContaining(address, out var symbol, out var offset))
        {
            return $"0x{address:X8}";
        }

        var name = demangle ? CodeWarriorDemangler.Demangle(symbol.Name) : symbol.Name;
        return offset == 0 ? name : $"{name}+0x{offset:X}";
    }
}
