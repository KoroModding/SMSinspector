using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using SMSinspector.Core.Layouts;
using SMSinspector.Core.Symbols;

namespace SMSinspector.Core.Names;

/// <summary>Reads every source the name extractor uses from the user's decomp clone.</summary>
public static partial class NameSourceLoader
{
    private static readonly string[] SourceExtensions = [".h", ".hpp", ".c", ".cpp", ".inc"];

    public static NameSources Load(LoadedDecomp decomp)
    {
        var repository = decomp.Repository;
        var version = decomp.Version.ToMask();

        var files = SourceDirectories(repository)
            .SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
            .Where(f => SourceExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToList();

        var scanned = new ConcurrentDictionary<int, (ScannedSource Source, int ParamInit, int ParamInitUnknown)>();
        Parallel.For(0, files.Count, i =>
        {
            var relative = Path.GetRelativePath(repository.Root, files[i]).Replace('\\', '/');
            var text = File.ReadAllText(files[i]);
            var paramInit = ParamInitUse().Matches(text);
            scanned[i] = (
                SourceScanner.Scan(relative, text, version),
                paramInit.Count,
                paramInit.Count(m => NameExtractor.IsUnknownName(m.Groups["member"].Value)));
        });

        var sources = new ScannedSource();
        for (var i = 0; i < files.Count; i++)
        {
            sources.Bodies.AddRange(scanned[i].Source.Bodies);
            sources.UnusedDeclarations.AddRange(scanned[i].Source.UnusedDeclarations);
        }

        IReadOnlyList<MapSymbol> map = [];
        var mapUsed = false;
        var mapMessage = decomp.LinkerMap.Message;
        if (decomp.LinkerMap.Path is { } mapPath)
        {
            try
            {
                map = LinkerMapFile.Load(mapPath);
                mapUsed = true;
                mapMessage += $" {map.Count:N0} symbols, {map.Count(m => m.IsUnused):N0} UNUSED.";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                mapMessage = $"Could not read the linker map: {e.Message}";
            }
        }

        return new NameSources(
            OriginalMethods.Build(decomp.Symbols, map, sources),
            sources,
            GameExecutable.Locate(repository, decomp.Version),
            mapMessage,
            mapUsed,
            scanned.Values.Sum(s => s.ParamInit),
            scanned.Values.Sum(s => s.ParamInitUnknown),
            files.Count,
            OtherVersionFunctions(decomp));
    }

    /// <summary>
    /// The function names of the other game version's symbols.txt, to tell which functions
    /// exist in one build only. Null when the clone has no symbols for the other version.
    /// </summary>
    private static IReadOnlySet<string>? OtherVersionFunctions(LoadedDecomp decomp)
    {
        var other = decomp.Version == Memory.GameVersion.GMSP01 ? Memory.GameVersion.GMSJ01 : Memory.GameVersion.GMSP01;
        var path = decomp.Repository.SymbolsPath(other);
        if (!File.Exists(path))
        {
            return null;
        }

        return SymbolFile.Load(path).Symbols.Where(s => s.Type == "function").Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The header folders the layouts use, plus <c>src/</c> and every <c>libs/&lt;library&gt;/src/</c>.</summary>
    public static IEnumerable<string> SourceDirectories(DecompRepository repository)
    {
        foreach (var directory in DecompHeaders.HeaderDirectories(repository))
        {
            yield return directory;
        }

        var src = Path.Combine(repository.Root, "src");
        if (Directory.Exists(src))
        {
            yield return src;
        }

        var libs = Path.Combine(repository.Root, "libs");
        if (Directory.Exists(libs))
        {
            foreach (var library in Directory.GetDirectories(libs).Order(StringComparer.Ordinal))
            {
                var librarySrc = Path.Combine(library, "src");
                if (Directory.Exists(librarySrc))
                {
                    yield return librarySrc;
                }
            }
        }
    }

    // The macro's own "#define PARAM_INIT(member, defaultValue)" line is not a use.
    [GeneratedRegex(@"(?<!#define\s+)\bPARAM_INIT\(\s*(?<member>\w+)\s*,")]
    private static partial Regex ParamInitUse();
}
