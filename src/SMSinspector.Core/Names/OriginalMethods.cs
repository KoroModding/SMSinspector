using SMSinspector.Core.Symbols;

namespace SMSinspector.Core.Names;

[Flags]
public enum NameOrigin
{
    None = 0,

    /// <summary>The decomp's symbols.txt.</summary>
    Symbols = 1,

    /// <summary>The original linker map.</summary>
    Map = 2,

    /// <summary>A <c>// UNUSED</c> comment in the decomp, which the authors copy from the map.</summary>
    UnusedComment = 4,
}

/// <summary>A member function whose name survived compilation.</summary>
/// <param name="Mangled">The mangled name, when it comes from symbols.txt or the map.</param>
/// <param name="Address">Where its code starts; null for a stripped (UNUSED) function.</param>
public sealed record OriginalMethod(
    string ClassName,
    string Name,
    string? Mangled,
    bool IsConst,
    IReadOnlyList<string>? Arguments,
    uint? Address,
    uint? Size,
    NameOrigin Origin)
{
    public bool IsUnused => Address is null;

    public string Signature => Arguments is null
        ? $"{ClassName}::{Name}"
        : $"{ClassName}::{Name}({string.Join(", ", Arguments)}){(IsConst ? " const" : "")}";
}

/// <summary>
/// Every original member function name, by class. Only these names count as original:
/// accessors that exist only in the decomp's headers were named by its authors.
/// </summary>
public sealed class OriginalMethods
{
    private readonly Dictionary<(string Class, string Name), List<OriginalMethod>> _byName = [];

    public IEnumerable<OriginalMethod> All => _byName.Values.SelectMany(m => m);

    public int Count => _byName.Values.Sum(m => m.Count);

    public bool Contains(string className, string name) => _byName.ContainsKey((className, name));

    public IReadOnlyList<OriginalMethod> Find(string className, string name) =>
        _byName.TryGetValue((className, name), out var found) ? found : [];

    public static OriginalMethods Build(SymbolTable symbols, IEnumerable<MapSymbol> map, ScannedSource sources)
    {
        var methods = new OriginalMethods();

        foreach (var symbol in symbols.All)
        {
            if (symbol.Type == "function" && CodeWarriorDemangler.TryParseFunction(symbol.Name, out var function) && function.Scope.Length > 0)
            {
                methods.Add(function, symbol.Name, symbol.Address, symbol.Size, NameOrigin.Symbols);
            }
        }

        foreach (var entry in map)
        {
            if (entry.Section != ".text" || !CodeWarriorDemangler.TryParseFunction(entry.Name, out var function) || function.Scope.Length == 0)
            {
                continue;
            }

            var known = methods.Find(function.Scope, function.Name).FirstOrDefault(m => m.Mangled == entry.Name);
            if (known is not null)
            {
                methods.Replace(known, known with { Origin = known.Origin | NameOrigin.Map });
            }
            else
            {
                methods.Add(function, entry.Name, entry.Address, entry.Size, NameOrigin.Map);
            }
        }

        var marked = sources.UnusedDeclarations.Select(d => (d.ClassName, d.Name))
            .Concat(sources.Bodies.Where(b => b.IsUnusedMarked).Select(b => (b.ClassName, b.Name)));
        foreach (var (className, name) in marked.Distinct())
        {
            if (methods.Find(className, name) is { Count: > 0 } found)
            {
                foreach (var method in found.ToList())
                {
                    methods.Replace(method, method with { Origin = method.Origin | NameOrigin.UnusedComment });
                }
            }
            else
            {
                methods.Add(new OriginalMethod(className, name, null, false, null, null, null, NameOrigin.UnusedComment));
            }
        }

        return methods;
    }

    private void Add(DemangledFunction function, string mangled, uint? address, uint? size, NameOrigin origin) =>
        Add(new OriginalMethod(function.Scope, function.Name, mangled, function.IsConst, function.Arguments, address, size, origin));

    private void Add(OriginalMethod method)
    {
        var key = (method.ClassName, method.Name);
        if (!_byName.TryGetValue(key, out var list))
        {
            _byName[key] = list = [];
        }

        list.Add(method);
    }

    private void Replace(OriginalMethod old, OriginalMethod updated)
    {
        var list = _byName[(old.ClassName, old.Name)];
        list[list.IndexOf(old)] = updated;
    }
}
