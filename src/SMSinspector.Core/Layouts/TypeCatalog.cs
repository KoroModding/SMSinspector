using System.Text.RegularExpressions;

namespace SMSinspector.Core.Layouts;

/// <summary>What a name resolved to.</summary>
public abstract record ResolvedName
{
    public sealed record Class(string QualifiedName, IReadOnlyList<ClassDecl> Declarations) : ResolvedName;

    public sealed record Typedef(TypedefDecl Declaration) : ResolvedName;

    public sealed record Enum(EnumDecl Declaration) : ResolvedName;
}

/// <summary>
/// Every declaration from every header, indexed for name lookup. Lookup follows C++
/// loosely: the enclosing scopes from the innermost out, then the bases of enclosing
/// classes, then, as a last resort, a unique match on the unqualified name anywhere.
/// </summary>
public sealed partial class TypeCatalog
{
    private readonly Dictionary<string, List<ClassDecl>> _classes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<TypedefDecl>> _typedefs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EnumDecl> _enums = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(EnumDecl Enum, Enumerator Item)>> _enumerators = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ConstantDecl>> _constants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<MacroDefinition>> _macros = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _bySimpleName = new(StringComparer.Ordinal);

    public TypeCatalog(IEnumerable<HeaderFile> headers)
    {
        Headers = headers.ToList();
        foreach (var header in Headers)
        {
            foreach (var decl in header.Classes)
            {
                Add(_classes, decl.QualifiedName, decl);
                IndexSimpleName(decl.QualifiedName);
            }

            foreach (var typedef in header.Typedefs)
            {
                // "typedef struct Foo Foo;" names the struct itself; skip the self-reference.
                if (typedef.Target.Name == LastPart(typedef.QualifiedName) && typedef.Target.TemplateArgs.Count == 0
                    && !typedef.Target.IsPointerLike && typedef.Target.Dims.Count == 0)
                {
                    continue;
                }

                Add(_typedefs, typedef.QualifiedName, typedef);
                IndexSimpleName(typedef.QualifiedName);
            }

            foreach (var decl in header.Enums)
            {
                _enums.TryAdd(decl.QualifiedName, decl);
                IndexSimpleName(decl.QualifiedName);
                foreach (var item in decl.Enumerators)
                {
                    // Unscoped enumerators live in the enclosing scope; also reachable as Enum::Name.
                    Add(_enumerators, Combine(decl.Scope, item.Name), (decl, item));
                    Add(_enumerators, Combine(decl.QualifiedName, item.Name), (decl, item));
                }
            }

            foreach (var constant in header.Constants)
            {
                Add(_constants, constant.QualifiedName, constant);
            }

            foreach (var macro in header.Macros)
            {
                Add(_macros, macro.Name, macro);
            }
        }
    }

    public IReadOnlyList<HeaderFile> Headers { get; }

    public IEnumerable<ClassDecl> AllClasses => _classes.Values.SelectMany(c => c);

    public IReadOnlyList<ClassDecl> FindClass(string qualifiedName) =>
        _classes.TryGetValue(qualifiedName, out var found) ? found : [];

    /// <summary>Resolves a name as written in <paramref name="scope"/>.</summary>
    public ResolvedName? Resolve(string name, string scope)
    {
        // Base-class lookups resolve names in turn; a malformed hierarchy must not recurse forever.
        if (_resolveDepth > 32)
        {
            return null;
        }

        _resolveDepth++;
        try
        {
            return ResolveCore(name, scope);
        }
        finally
        {
            _resolveDepth--;
        }
    }

    [ThreadStatic]
    private static int _resolveDepth;

    private ResolvedName? ResolveCore(string name, string scope)
    {
        foreach (var candidate in Candidates(name, scope))
        {
            if (_typedefs.TryGetValue(candidate, out var typedefs))
            {
                return new ResolvedName.Typedef(typedefs[0]);
            }

            if (_classes.TryGetValue(candidate, out var classes))
            {
                return new ResolvedName.Class(candidate, classes);
            }

            if (_enums.TryGetValue(candidate, out var enumDecl))
            {
                return new ResolvedName.Enum(enumDecl);
            }
        }

        // Last resort: the unqualified name matches exactly one declaration anywhere.
        if (_bySimpleName.TryGetValue(LastPart(name), out var qualified) && qualified.Count == 1 && qualified[0].EndsWith(name, StringComparison.Ordinal))
        {
            return Resolve(qualified[0], "");
        }

        return null;
    }

    /// <summary>
    /// The value of a named integer constant (enumerator, static const member or #define)
    /// for one version, or null when it is unknown or not a constant.
    /// </summary>
    public long? ResolveConstant(string name, string scope, VersionMask version, int depth = 0)
    {
        if (depth > 16)
        {
            return null;
        }

        foreach (var candidate in Candidates(name, scope))
        {
            if (_enumerators.TryGetValue(candidate, out var items))
            {
                foreach (var (enumDecl, item) in items)
                {
                    if ((item.Versions & version) != 0)
                    {
                        return EnumeratorValue(enumDecl, item, version, depth + 1);
                    }
                }
            }

            if (_constants.TryGetValue(candidate, out var constants))
            {
                var constant = constants.FirstOrDefault(c => (c.Versions & version) != 0);
                if (constant is not null)
                {
                    return Evaluate(constant.Expression, scope, version, depth + 1);
                }
            }
        }

        if (_macros.TryGetValue(name, out var macros))
        {
            var macro = macros.FirstOrDefault(m => (m.Versions & version) != 0);
            if (macro is not null)
            {
                return Evaluate(macro.Value, scope, version, depth + 1);
            }
        }

        return null;
    }

    /// <summary>Evaluates a constant expression for one version, expanding the decomp's VERSION_SELECT.</summary>
    public long? Evaluate(string expression, string scope, VersionMask version, int depth = 0)
    {
        var expanded = ExpandVersionMacros(expression, version);
        return ConstantExpression.TryEvaluate(expanded, n => ResolveConstant(n, scope, version, depth + 1), out var value) ? value : null;
    }

    private long? EnumeratorValue(EnumDecl decl, Enumerator target, VersionMask version, int depth)
    {
        long next = 0;
        foreach (var item in decl.Enumerators)
        {
            if ((item.Versions & version) == 0)
            {
                continue;
            }

            long value;
            if (item.Expression is null)
            {
                value = next;
            }
            else if (Evaluate(item.Expression, decl.Scope, version, depth) is { } evaluated)
            {
                value = evaluated;
            }
            else
            {
                return null;
            }

            if (ReferenceEquals(item, target))
            {
                return value;
            }

            next = value + 1;
        }

        return null;
    }

    /// <summary>
    /// Replaces <c>GMSJ01(x)</c> and <c>GMSP01(x)</c> by x for their own version and by
    /// nothing otherwise, and drops the <c>VERSION_SELECT</c> wrapper, as include/version.h does.
    /// </summary>
    public static string ExpandVersionMacros(string expression, VersionMask version)
    {
        if (!expression.Contains("GMS", StringComparison.Ordinal))
        {
            return expression;
        }

        var text = expression;
        foreach (var (macro, mask) in new[] { ("GMSJ01", VersionMask.Jp), ("GMSP01", VersionMask.Pal) })
        {
            int start;
            while ((start = FindMacroCall(text, macro)) >= 0)
            {
                var open = text.IndexOf('(', start);
                var close = MatchingParen(text, open);
                if (close < 0)
                {
                    break;
                }

                var inner = text[(open + 1)..close];
                text = text[..start] + ((version & mask) != 0 ? $"({inner})" : "") + text[(close + 1)..];
            }
        }

        return VersionSelectPattern().Replace(text, "").Replace(",", " ");
    }

    private static int FindMacroCall(string text, string macro)
    {
        var index = 0;
        while ((index = text.IndexOf(macro, index, StringComparison.Ordinal)) >= 0)
        {
            var after = index + macro.Length;
            while (after < text.Length && text[after] == ' ')
            {
                after++;
            }

            if (after < text.Length && text[after] == '(' && (index == 0 || !char.IsLetterOrDigit(text[index - 1])))
            {
                return index;
            }

            index = after;
        }

        return -1;
    }

    private static int MatchingParen(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            depth += text[i] == '(' ? 1 : text[i] == ')' ? -1 : 0;
            if (depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private IEnumerable<string> Candidates(string name, string scope)
    {
        if (name.StartsWith("::", StringComparison.Ordinal))
        {
            yield return name[2..];
            yield break;
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = scope;
        while (true)
        {
            var candidate = Combine(current, name);
            if (visited.Add(candidate))
            {
                yield return candidate;
            }

            // Names inherited from the bases of an enclosing class.
            foreach (var inherited in BaseScopes(current, 0))
            {
                var fromBase = Combine(inherited, name);
                if (visited.Add(fromBase))
                {
                    yield return fromBase;
                }
            }

            if (current.Length == 0)
            {
                yield break;
            }

            var cut = LastSeparator(current);
            current = cut < 0 ? "" : current[..cut];
        }
    }

    private IEnumerable<string> BaseScopes(string classScope, int depth)
    {
        if (depth > 8 || !_classes.TryGetValue(classScope, out var decls))
        {
            yield break;
        }

        foreach (var spec in decls[0].Bases)
        {
            if (Resolve(spec.Type.Name, decls[0].Scope) is ResolvedName.Class baseClass)
            {
                yield return baseClass.QualifiedName;
                foreach (var further in BaseScopes(baseClass.QualifiedName, depth + 1))
                {
                    yield return further;
                }
            }
        }
    }

    private void IndexSimpleName(string qualified)
    {
        var simple = LastPart(qualified);
        if (!_bySimpleName.TryGetValue(simple, out var list))
        {
            _bySimpleName[simple] = list = [];
        }

        if (!list.Contains(qualified))
        {
            list.Add(qualified);
        }
    }

    private static void Add<T>(Dictionary<string, List<T>> map, string key, T value)
    {
        if (!map.TryGetValue(key, out var list))
        {
            map[key] = list = [];
        }

        list.Add(value);
    }

    internal static string Combine(string scope, string name) => scope.Length == 0 ? name : $"{scope}::{name}";

    /// <summary>Index of the last "::" outside template arguments.</summary>
    private static int LastSeparator(string name)
    {
        var depth = 0;
        for (var i = name.Length - 1; i > 0; i--)
        {
            depth += name[i] == '>' ? 1 : name[i] == '<' ? -1 : 0;
            if (depth == 0 && name[i] == ':' && name[i - 1] == ':')
            {
                return i - 1;
            }
        }

        return -1;
    }

    internal static string LastPart(string name)
    {
        var cut = LastSeparator(name);
        return cut < 0 ? name : name[(cut + 2)..];
    }

    [GeneratedRegex(@"VERSION_SELECT\s*")]
    private static partial Regex VersionSelectPattern();
}
