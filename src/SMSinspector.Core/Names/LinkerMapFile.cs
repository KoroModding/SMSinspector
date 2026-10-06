using System.Globalization;
using System.Text.RegularExpressions;

namespace SMSinspector.Core.Names;

/// <summary>One symbol of a section layout in a linker map.</summary>
/// <param name="Address">Load address; null for a symbol the linker stripped.</param>
public sealed record MapSymbol(string Name, string Section, uint Size, uint? Address)
{
    public bool IsUnused => Address is null;
}

/// <summary>
/// Reads the section layouts of a CodeWarrior linker map. Under a "<c>.text section layout</c>"
/// heading, each symbol line gives the start offset, size, load address (sometimes a file
/// offset), alignment and name. Stripped symbols read <c>UNUSED</c> instead of the start
/// offset and dots instead of the address.
/// </summary>
public static partial class LinkerMapFile
{
    public static IReadOnlyList<MapSymbol> Load(string path) => Parse(File.ReadLines(path));

    public static IReadOnlyList<MapSymbol> Parse(IEnumerable<string> lines)
    {
        var symbols = new List<MapSymbol>();
        string? section = null;

        foreach (var line in lines)
        {
            var heading = SectionHeading().Match(line);
            if (heading.Success)
            {
                section = heading.Groups["section"].Value;
                continue;
            }

            if (section is null)
            {
                continue;
            }

            var match = SymbolLine().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var name = match.Groups["name"].Value;
            if (name.StartsWith('.'))
            {
                // A section label for the object file, not a symbol.
                continue;
            }

            var size = uint.Parse(match.Groups["size"].ValueSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            uint? address = match.Groups["address"].Success
                ? uint.Parse(match.Groups["address"].ValueSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : null;
            symbols.Add(new MapSymbol(name, section, size, address));
        }

        return symbols;
    }

    [GeneratedRegex(@"^(?<section>\.\S+) section layout\s*$")]
    private static partial Regex SectionHeading();

    [GeneratedRegex(@"^\s+(?:UNUSED\s+(?<size>[0-9A-Fa-f]+)\s+\.+|[0-9A-Fa-f]{8}\s+(?<size>[0-9A-Fa-f]+)\s+(?<address>[0-9A-Fa-f]{8})(?:\s+[0-9A-Fa-f]{8})?\s+\d+)\s+(?<name>\S+)")]
    private static partial Regex SymbolLine();
}
