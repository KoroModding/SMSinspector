using System.Globalization;
using System.Text.RegularExpressions;

namespace SMSinspector.Core.Symbols;

/// <summary>
/// Parses the decomp's <c>config/&lt;version&gt;/symbols.txt</c>. Each line looks like
/// <c>name = .section:0x80001234; // type:object size:0x4 scope:global</c>.
/// </summary>
public static partial class SymbolFile
{
    public sealed record ParseResult(IReadOnlyList<Symbol> Symbols, IReadOnlyList<int> SkippedLines);

    public static ParseResult Load(string path) => Parse(File.ReadLines(path));

    public static ParseResult Parse(IEnumerable<string> lines)
    {
        var symbols = new List<Symbol>();
        var skipped = new List<int>();
        var lineNumber = 0;

        foreach (var line in lines)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (TryParseLine(line, out var symbol))
            {
                symbols.Add(symbol);
            }
            else
            {
                skipped.Add(lineNumber);
            }
        }

        return new ParseResult(symbols, skipped);
    }

    public static bool TryParseLine(string line, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Symbol? symbol)
    {
        symbol = null;
        var match = LinePattern().Match(line);
        if (!match.Success
            || !uint.TryParse(match.Groups["address"].ValueSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address))
        {
            return false;
        }

        uint? size = null;
        string? type = null;
        string? scope = null;

        foreach (var attribute in match.Groups["attributes"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = attribute.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var value = attribute[(colon + 1)..];
            switch (attribute[..colon])
            {
                case "size" when value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    && uint.TryParse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed):
                    size = parsed;
                    break;
                case "type":
                    type = value;
                    break;
                case "scope":
                    scope = value;
                    break;
            }
        }

        symbol = new Symbol(match.Groups["name"].Value, match.Groups["section"].Value, address, size, type, scope);
        return true;
    }

    [GeneratedRegex(@"^(?<name>\S+)\s*=\s*(?<section>[^:\s]+):0x(?<address>[0-9A-Fa-f]{1,8});(?:\s*//(?<attributes>.*))?$")]
    private static partial Regex LinePattern();
}
