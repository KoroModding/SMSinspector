using SMSinspector.Core.Memory;

namespace SMSinspector.Core.Symbols;

/// <summary>Everything read from the decomp clone for one game version.</summary>
public sealed record LoadedDecomp(
    DecompRepository Repository,
    GameVersion Version,
    string? CommitHash,
    SymbolTable Symbols,
    VtableIndex Vtables,
    int SkippedSymbolLines,
    LinkerMapLookup LinkerMap)
{
    /// <summary>Reads symbols.txt and builds the indexes. Takes a fraction of a second on the full file.</summary>
    public static LoadedDecomp Load(DecompRepository repository, GameVersion version)
    {
        var parsed = SymbolFile.Load(repository.SymbolsPath(version));
        var symbols = new SymbolTable(parsed.Symbols);
        return new LoadedDecomp(
            repository,
            version,
            repository.ReadCommitHash(),
            symbols,
            new VtableIndex(symbols),
            parsed.SkippedLines.Count,
            repository.LocateLinkerMap(version));
    }
}
