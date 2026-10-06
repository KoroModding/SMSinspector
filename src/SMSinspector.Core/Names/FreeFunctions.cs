using System.Text.RegularExpressions;
using SMSinspector.Core.Symbols;

namespace SMSinspector.Core.Names;

/// <summary>
/// A function outside any class whose first parameter is declared as a pointer to a type:
/// on entry, r3 points at an object of that type.
/// </summary>
/// <param name="FirstParameterType">The pointed-to type as declared, for example "GXFoo" for <c>GXFoo*</c>.</param>
/// <param name="Source">Where the parameter type comes from: a prototype's file and line, or the mangled name.</param>
public sealed record FreeFunction(string Name, uint Address, uint Size, string FirstParameterType, string Source);

/// <summary>Finds the free functions of symbols.txt whose first parameter is a pointer, and its type.</summary>
public static partial class FreeFunctions
{
    private static readonly HashSet<string> NotTypes = new(StringComparer.Ordinal)
    {
        "return", "else", "case", "goto", "new", "delete", "throw", "if", "while", "for", "switch", "sizeof", "typedef", "define", "do",
    };

    /// <summary>A prototype or definition seen in one file: the function and its first parameter's pointed-to type.</summary>
    public sealed record Prototype(string Name, string FirstParameterType, string File, int Line);

    /// <summary>The prototypes in one file whose first parameter is a single pointer.</summary>
    public static IEnumerable<Prototype> Scan(string file, string text)
    {
        foreach (Match match in PrototypePattern().Matches(text))
        {
            if (NotTypes.Contains(match.Groups["ret"].Value) || NotTypes.Contains(match.Groups["name"].Value))
            {
                continue;
            }

            var line = 1 + text.AsSpan(0, match.Index).Count('\n');
            yield return new Prototype(match.Groups["name"].Value, match.Groups["type"].Value, file, line);
        }
    }

    /// <summary>
    /// C functions take their parameter type from a prototype, when every prototype of that
    /// name agrees; C++ free functions from their mangled name.
    /// </summary>
    public static IReadOnlyList<FreeFunction> Build(SymbolTable symbols, IEnumerable<Prototype> prototypes)
    {
        var byName = prototypes.GroupBy(p => p.Name, StringComparer.Ordinal)
            .Where(g => g.Select(p => p.FirstParameterType).Distinct(StringComparer.Ordinal).Count() == 1)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.File, StringComparer.Ordinal).ThenBy(p => p.Line).First(), StringComparer.Ordinal);

        var result = new List<FreeFunction>();
        foreach (var symbol in symbols.All)
        {
            if (symbol.Type != "function" || symbol.Size is not { } size)
            {
                continue;
            }

            if (CodeWarriorDemangler.TryParseFunction(symbol.Name, out var function))
            {
                if (function.Scope.Length == 0 && function.Arguments is [var first, ..] && PointedType(first) is { } type)
                {
                    result.Add(new FreeFunction(function.Name, symbol.Address, size, type, symbol.Name));
                }
            }
            else if (byName.TryGetValue(symbol.Name, out var prototype))
            {
                result.Add(new FreeFunction(symbol.Name, symbol.Address, size, prototype.FirstParameterType, $"{prototype.File}:{prototype.Line}"));
            }
        }

        return result;
    }

    // "const TFoo*" gives "TFoo"; "TFoo**", "TFoo&" and non-pointers give null.
    private static string? PointedType(string argument)
    {
        var text = argument.Replace("const ", "", StringComparison.Ordinal).Trim();
        return text.EndsWith('*') && !text.EndsWith("**", StringComparison.Ordinal) ? text[..^1].Trim() : null;
    }

    [GeneratedRegex(@"(?m)^[ \t]*(?:(?:extern|static|inline)\s+)*(?:const\s+)?(?:(?:unsigned|signed|struct|enum)\s+)?(?<ret>[A-Za-z_]\w*)[\s\*]+(?<name>[A-Za-z_]\w*)\s*\(\s*(?:const\s+)?(?:struct\s+)?(?<type>[A-Za-z_]\w*)\s*\*(?!\s*\*)")]
    private static partial Regex PrototypePattern();
}
