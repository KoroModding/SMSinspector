using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using SMSinspector.Core.Memory;

namespace SMSinspector.Core.Symbols;

/// <summary>Where the original linker map was looked for, and what was found.</summary>
/// <param name="Path">The map to use, or null when none could be chosen.</param>
/// <param name="ConfiguredPath">The path named by the <c>map:</c> line of config.yml, if any.</param>
/// <param name="Found">Every <c>*.MAP</c> file found in <c>orig/&lt;version&gt;/files/</c>.</param>
public sealed record LinkerMapLookup(string? Path, string? ConfiguredPath, IReadOnlyList<string> Found, string Message);

/// <summary>
/// The user's local clone of the doldecomp/sms repository. SMSinspector reads symbols
/// and headers from it at runtime; nothing from it is bundled.
/// </summary>
public sealed partial class DecompRepository
{
    private DecompRepository(string root) => Root = root;

    public string Root { get; }

    public string IncludeDirectory => Path.Combine(Root, "include");

    public string ConfigDirectory(GameVersion version) => Path.Combine(Root, "config", version.ToString());

    public string SymbolsPath(GameVersion version) => Path.Combine(ConfigDirectory(version), "symbols.txt");

    /// <summary>
    /// Opens a clone after checking it looks like the decomp: a <c>configure.py</c> at the
    /// root and a <c>config/&lt;version&gt;/</c> folder holding <c>symbols.txt</c>.
    /// </summary>
    public static bool TryOpen(string root, GameVersion version, [NotNullWhen(true)] out DecompRepository? repository, out string error)
    {
        repository = null;
        var candidate = new DecompRepository(Path.GetFullPath(root));

        if (!Directory.Exists(candidate.Root))
        {
            error = "The folder does not exist.";
            return false;
        }

        if (!File.Exists(Path.Combine(candidate.Root, "configure.py")))
        {
            error = "No configure.py in this folder: pick the root of your doldecomp/sms clone.";
            return false;
        }

        if (!Directory.Exists(candidate.ConfigDirectory(version)))
        {
            error = $"No config/{version}/ folder: this clone does not support {version}.";
            return false;
        }

        if (!File.Exists(candidate.SymbolsPath(version)))
        {
            error = $"config/{version}/symbols.txt is missing.";
            return false;
        }

        repository = candidate;
        error = "";
        return true;
    }

    /// <summary>
    /// Finds the original linker map, which the decomp does not ship: the user adds it with
    /// the game files. The path comes from the <c>map:</c> line of config.yml (commented or
    /// not); if that file is missing, any <c>*.MAP</c> in <c>orig/&lt;version&gt;/files/</c> is used,
    /// as long as there is exactly one.
    /// </summary>
    public LinkerMapLookup LocateLinkerMap(GameVersion version)
    {
        string? configured = null;
        var configPath = Path.Combine(ConfigDirectory(version), "config.yml");
        if (File.Exists(configPath))
        {
            foreach (var line in File.ReadLines(configPath))
            {
                var match = MapLinePattern().Match(line);
                if (match.Success)
                {
                    configured = match.Groups["path"].Value;
                    break;
                }
            }
        }

        if (configured is not null)
        {
            var configuredFull = Path.Combine(Root, configured);
            if (File.Exists(configuredFull))
            {
                return new LinkerMapLookup(configuredFull, configured, [configuredFull], $"Using {configured} (from config.yml).");
            }
        }

        var filesDirectory = Path.Combine(Root, "orig", version.ToString(), "files");
        var found = Directory.Exists(filesDirectory)
            ? Directory.GetFiles(filesDirectory, "*.MAP", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }).Order(StringComparer.Ordinal).ToArray()
            : [];
        var relativeFiles = $"orig/{version}/files/";
        var configuredNote = configured is null ? "config.yml names no map" : $"config.yml names {configured}, which is not there";

        return found.Length switch
        {
            0 => new LinkerMapLookup(null, configured, found, $"No linker map: {configuredNote}, and {relativeFiles} has no .MAP file."),
            1 => new LinkerMapLookup(found[0], configured, found, $"Using {relativeFiles}{Path.GetFileName(found[0])} ({configuredNote})."),
            _ => new LinkerMapLookup(null, configured, found,
                $"No linker map chosen: {configuredNote}, and {relativeFiles} has several .MAP files ({string.Join(", ", found.Select(Path.GetFileName))})."),
        };
    }

    /// <summary>
    /// The commit the clone is on, read from the .git folder without running git.
    /// Null when it cannot be determined.
    /// </summary>
    public string? ReadCommitHash()
    {
        var gitDirectory = Path.Combine(Root, ".git");
        var headPath = Path.Combine(gitDirectory, "HEAD");
        if (!File.Exists(headPath))
        {
            return null;
        }

        var head = File.ReadAllText(headPath).Trim();
        if (!head.StartsWith("ref: ", StringComparison.Ordinal))
        {
            return IsHash(head) ? head : null;
        }

        var reference = head["ref: ".Length..];
        var loosePath = Path.Combine(gitDirectory, reference.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(loosePath))
        {
            var hash = File.ReadAllText(loosePath).Trim();
            return IsHash(hash) ? hash : null;
        }

        var packedPath = Path.Combine(gitDirectory, "packed-refs");
        if (File.Exists(packedPath))
        {
            foreach (var line in File.ReadLines(packedPath))
            {
                var parts = line.Split(' ', 2);
                if (parts.Length == 2 && parts[1] == reference && IsHash(parts[0]))
                {
                    return parts[0];
                }
            }
        }

        return null;
    }

    private static bool IsHash(string text) => text.Length == 40 && text.All(char.IsAsciiHexDigit);

    [GeneratedRegex(@"^\s*#?\s*map:\s*(?<path>\S+)\s*$")]
    private static partial Regex MapLinePattern();
}
