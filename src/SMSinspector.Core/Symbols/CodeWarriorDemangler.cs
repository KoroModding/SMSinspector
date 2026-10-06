using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace SMSinspector.Core.Symbols;

/// <summary>
/// Demangles CodeWarrior (Metrowerks) C++ symbol names, the scheme used by the game's
/// compiler. It is not the Itanium scheme, so standard demanglers cannot read it.
/// </summary>
/// <remarks>
/// <code>
/// update__9TFooActorFv            TFooActor::update()
///   update        member name
///   9TFooActor    class name, prefixed with its length
///   F v           function taking void
///
/// draw__Q23Gfx7TCanvasCFi         Gfx::TCanvas::draw(int) const
///   Q2            scope nested 2 levels deep
///   C             const method
///
/// __vt__16TBox&lt;9TFooActor&gt;    vtable of TBox&lt;TFooActor&gt;
/// </code>
/// Template arguments sit inside the length-prefixed name and are mangled types
/// themselves. The demangling aims at readable names, not exact signatures: anything
/// it cannot parse is returned unchanged rather than guessed.
/// </remarks>
public static class CodeWarriorDemangler
{
    private const string VtablePrefix = "__vt__";

    /// <summary>Demangles a symbol name, or returns it unchanged if it is not mangled or parsing fails.</summary>
    public static string Demangle(string symbol)
    {
        // "@32@name" is a thunk that adjusts "this" by 32 bytes before calling name.
        if (symbol.Length > 2 && symbol[0] == '@')
        {
            var end = symbol.IndexOf('@', 1);
            if (end > 1 && int.TryParse(symbol.AsSpan(1, end - 1), out var adjust))
            {
                var target = Demangle(symbol[(end + 1)..]);
                return target == symbol[(end + 1)..] ? symbol : $"{target} [thunk, this adjusted by {adjust}]";
            }
        }

        // A member name may itself end with underscores ("reset___6TThingFv" is reset_),
        // so try each "__" in turn until one parses.
        var searchFrom = symbol.StartsWith("__", StringComparison.Ordinal) ? 2 : 1;
        for (var split = symbol.IndexOf("__", searchFrom, StringComparison.Ordinal);
             split >= 0 && split + 2 < symbol.Length;
             split = symbol.IndexOf("__", split + 1, StringComparison.Ordinal))
        {
            if (TryDemangleAt(symbol[..split], symbol[(split + 2)..], out var result))
            {
                return result;
            }
        }

        return symbol;
    }

    private static bool TryDemangleAt(string member, string encoded, [NotNullWhen(true)] out string? result)
    {
        result = null;
        if (!TryParseAt(member, encoded, out var function))
        {
            return false;
        }

        var qualified = function.QualifiedName;
        result = function.Arguments is { } args
            ? $"{qualified}({FormatArguments(args)}){(function.IsConst ? " const" : "")}"
            : qualified;
        return true;
    }

    /// <summary>
    /// Splits a mangled member function into its parts. Fails on anything that is not a
    /// function, on thunks, and on names it cannot parse.
    /// </summary>
    public static bool TryParseFunction(string symbol, [NotNullWhen(true)] out DemangledFunction? function)
    {
        function = null;
        if (symbol.Length == 0 || symbol[0] == '@')
        {
            return false;
        }

        var searchFrom = symbol.StartsWith("__", StringComparison.Ordinal) ? 2 : 1;
        for (var split = symbol.IndexOf("__", searchFrom, StringComparison.Ordinal);
             split >= 0 && split + 2 < symbol.Length;
             split = symbol.IndexOf("__", split + 1, StringComparison.Ordinal))
        {
            if (TryParseAt(symbol[..split], symbol[(split + 2)..], out var parsed))
            {
                if (parsed.Arguments is null)
                {
                    return false;
                }

                function = parsed;
                return true;
            }
        }

        return false;
    }

    private static bool TryParseAt(string member, string encoded, out DemangledFunction function)
    {
        function = null!;
        var pos = 0;
        if (!TryParseScope(encoded, ref pos, out var scope, out var className))
        {
            return false;
        }

        var name = member switch
        {
            "__ct" when className is not null => className,
            "__dt" when className is not null => "~" + className,
            "__vt" => "vtable",
            "__RTTI" => "RTTI",
            _ => DemangleIdentifier(member),
        };

        var isConst = encoded.AsSpan(pos).StartsWith("CF");
        if (isConst)
        {
            pos++;
        }

        if (pos >= encoded.Length)
        {
            function = new DemangledFunction(scope, name, isConst, null);
            return true;
        }

        if (encoded[pos] != 'F')
        {
            return false;
        }

        pos++;
        var args = new List<string>();
        while (pos < encoded.Length)
        {
            if (!TryParseType(encoded, ref pos, out var arg))
            {
                return false;
            }

            args.Add(arg);
        }

        function = new DemangledFunction(scope, name, isConst, args is ["void"] ? [] : args);
        return true;
    }

    private static string FormatArguments(IReadOnlyList<string> args) => args is ["void"] ? "" : string.Join(", ", args);

    /// <summary>
    /// For a vtable symbol (<c>__vt__...</c>), gives the demangled name of the class it belongs to.
    /// </summary>
    public static bool TryGetVtableClass(string symbol, [NotNullWhen(true)] out string? className)
    {
        className = null;
        if (!symbol.StartsWith(VtablePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var encoded = symbol[VtablePrefix.Length..];
        var pos = 0;
        if (!TryParseScope(encoded, ref pos, out var scope, out _) || pos != encoded.Length || scope.Length == 0)
        {
            return false;
        }

        className = scope;
        return true;
    }

    /// <summary>
    /// Reads a scope: nothing (free function), one length-prefixed name, or Qn followed
    /// by n names. <paramref name="innermost"/> is the last name without template
    /// arguments, used to name constructors and destructors.
    /// </summary>
    private static bool TryParseScope(string text, ref int pos, out string scope, out string? innermost)
    {
        scope = "";
        innermost = null;

        if (pos >= text.Length || text[pos] == 'F' || (text[pos] == 'C' && pos + 1 < text.Length && text[pos + 1] == 'F'))
        {
            return true;
        }

        var count = 1;
        if (text[pos] == 'Q' && pos + 1 < text.Length && char.IsAsciiDigit(text[pos + 1]))
        {
            count = text[pos + 1] - '0';
            pos += 2;
        }

        var parts = new string[count];
        for (var i = 0; i < count; i++)
        {
            if (!TryReadLengthPrefixed(text, ref pos, out var raw))
            {
                return false;
            }

            parts[i] = DemangleIdentifier(raw);
            innermost = TemplateBase(raw);
        }

        scope = string.Join("::", parts);
        return true;
    }

    private static bool TryReadLengthPrefixed(string text, ref int pos, [NotNullWhen(true)] out string? identifier)
    {
        identifier = null;
        var start = pos;
        while (pos < text.Length && char.IsAsciiDigit(text[pos]))
        {
            pos++;
        }

        if (pos == start || !int.TryParse(text.AsSpan(start, pos - start), out var length) || length == 0 || pos + length > text.Length)
        {
            pos = start;
            return false;
        }

        identifier = text.Substring(pos, length);
        pos += length;
        return true;
    }

    /// <summary>Turns "TBox&lt;10TFooActor,f&gt;" into "TBox&lt;TFooActor, float&gt;".</summary>
    private static string DemangleIdentifier(string raw)
    {
        var open = raw.IndexOf('<');
        if (open < 0 || raw[^1] != '>')
        {
            return raw;
        }

        var args = SplitTopLevel(raw.AsSpan(open + 1, raw.Length - open - 2));
        if (args is null)
        {
            return raw;
        }

        var result = new StringBuilder(raw.Length);
        result.Append(raw, 0, open).Append('<');
        for (var i = 0; i < args.Count; i++)
        {
            if (i > 0)
            {
                result.Append(", ");
            }

            result.Append(DemangleTemplateArgument(args[i]));
        }

        return result.Append('>').ToString();
    }

    private static string DemangleTemplateArgument(string arg)
    {
        // Non-type arguments are plain numbers.
        if (arg.Length > 0 && arg.AsSpan().TrimStart('-').ContainsAnyExcept("0123456789") is false)
        {
            return arg;
        }

        var pos = 0;
        return TryParseType(arg, ref pos, out var type) && pos == arg.Length ? type : arg;
    }

    private static List<string>? SplitTopLevel(ReadOnlySpan<char> text)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '<':
                    depth++;
                    break;
                case '>':
                    depth--;
                    if (depth < 0)
                    {
                        return null;
                    }

                    break;
                case ',' when depth == 0:
                    parts.Add(text[start..i].ToString());
                    start = i + 1;
                    break;
            }
        }

        if (depth != 0)
        {
            return null;
        }

        parts.Add(text[start..].ToString());
        return parts;
    }

    private static string TemplateBase(string raw)
    {
        var open = raw.IndexOf('<');
        return open < 0 ? raw : raw[..open];
    }

    private static bool TryParseType(string text, ref int pos, [NotNullWhen(true)] out string? type)
    {
        type = null;
        var start = pos;
        var qualifiers = new List<char>();
        while (pos < text.Length && text[pos] is 'P' or 'R' or 'C')
        {
            qualifiers.Add(text[pos]);
            pos++;
        }

        if (pos >= text.Length)
        {
            pos = start;
            return false;
        }

        string? baseType = null;
        if (pos + 1 < text.Length && text[pos] is 'U' or 'S')
        {
            baseType = (text[pos], text[pos + 1]) switch
            {
                ('U', 'c') => "unsigned char",
                ('U', 's') => "unsigned short",
                ('U', 'i') => "unsigned int",
                ('U', 'l') => "unsigned long",
                ('U', 'x') => "unsigned long long",
                ('S', 'c') => "signed char",
                _ => null,
            };
            if (baseType is not null)
            {
                pos += 2;
            }
        }

        if (baseType is null)
        {
            baseType = text[pos] switch
            {
                'v' => "void",
                'b' => "bool",
                'c' => "char",
                'w' => "wchar_t",
                's' => "short",
                'i' => "int",
                'l' => "long",
                'x' => "long long",
                'f' => "float",
                'd' => "double",
                'e' => "...",
                _ => null,
            };
            if (baseType is not null)
            {
                pos++;
            }
        }

        if (baseType is null && text[pos] == 'A')
        {
            // A<n>_<type>: array of n elements. "A3_A4_f" is float[3][4].
            pos++;
            var digits = pos;
            while (pos < text.Length && char.IsAsciiDigit(text[pos]))
            {
                pos++;
            }

            if (pos == digits || pos >= text.Length || text[pos] != '_')
            {
                pos = start;
                return false;
            }

            var count = text[digits..pos];
            pos++;
            if (!TryParseType(text, ref pos, out var element))
            {
                pos = start;
                return false;
            }

            var bracket = element.IndexOf('[');
            baseType = bracket < 0 ? $"{element}[{count}]" : element.Insert(bracket, $"[{count}]");
        }
        else if (baseType is null && text[pos] == 'F')
        {
            // F<args>_<return>: function type, seen behind pointers to callbacks.
            pos++;
            var args = new List<string>();
            while (pos < text.Length && text[pos] != '_')
            {
                if (!TryParseType(text, ref pos, out var arg))
                {
                    pos = start;
                    return false;
                }

                args.Add(arg);
            }

            if (pos >= text.Length)
            {
                pos = start;
                return false;
            }

            pos++;
            if (!TryParseType(text, ref pos, out var returnType))
            {
                pos = start;
                return false;
            }

            baseType = $"{returnType}({FormatArguments(args)})";
        }

        if (baseType is null)
        {
            if (!char.IsAsciiDigit(text[pos]) && text[pos] != 'Q')
            {
                pos = start;
                return false;
            }

            if (!TryParseScope(text, ref pos, out var scope, out _) || scope.Length == 0)
            {
                pos = start;
                return false;
            }

            baseType = scope;
        }

        for (var i = qualifiers.Count - 1; i >= 0; i--)
        {
            baseType = qualifiers[i] switch
            {
                'P' => baseType + "*",
                'R' => baseType + "&",
                _ => "const " + baseType,
            };
        }

        type = baseType;
        return true;
    }
}

/// <summary>A demangled name split into parts.</summary>
/// <param name="Scope">The enclosing classes and namespaces, "" for a free function: "Gfx::TCanvas".</param>
/// <param name="Name">The member name: "draw", or the class name for a constructor.</param>
/// <param name="Arguments">Demangled argument types, empty for "(void)"; null when the symbol is not a function.</param>
public sealed record DemangledFunction(string Scope, string Name, bool IsConst, IReadOnlyList<string>? Arguments)
{
    public string QualifiedName => Scope.Length > 0 ? Scope + "::" + Name : Name;
}
