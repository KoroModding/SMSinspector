using System.Collections.Concurrent;
using SMSinspector.Core.Symbols;

namespace SMSinspector.Core.Layouts;

/// <summary>Parses every header of a decomp clone into a <see cref="TypeCatalog"/>.</summary>
public static class DecompHeaders
{
    private static readonly string[] Extensions = [".h", ".hpp"];

    /// <summary>
    /// Header folders: <c>include/</c> and every <c>libs/&lt;library&gt;/include/</c>.
    /// </summary>
    public static IEnumerable<string> HeaderDirectories(DecompRepository repository)
    {
        if (Directory.Exists(repository.IncludeDirectory))
        {
            yield return repository.IncludeDirectory;
        }

        var libs = Path.Combine(repository.Root, "libs");
        if (Directory.Exists(libs))
        {
            foreach (var library in Directory.GetDirectories(libs).Order(StringComparer.Ordinal))
            {
                var include = Path.Combine(library, "include");
                if (Directory.Exists(include))
                {
                    yield return include;
                }
            }
        }
    }

    public static TypeCatalog Load(DecompRepository repository)
    {
        var files = HeaderDirectories(repository)
            .SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
            // The C++ library headers (cstdint, new, ...) have no extension.
            .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase) || Path.GetExtension(f).Length == 0)
            .Order(StringComparer.Ordinal)
            .ToList();

        var parsed = new ConcurrentDictionary<int, HeaderFile>();
        Parallel.For(0, files.Count, i =>
        {
            var relative = Path.GetRelativePath(repository.Root, files[i]).Replace('\\', '/');
            parsed[i] = HeaderParser.Parse(relative, File.ReadAllText(files[i]));
        });

        // Keep file order stable so that lookups that take "the first" declaration are deterministic.
        return new TypeCatalog(Enumerable.Range(0, files.Count).Select(i => parsed[i]));
    }
}
