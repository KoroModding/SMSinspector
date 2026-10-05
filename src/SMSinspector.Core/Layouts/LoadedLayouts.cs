using SMSinspector.Core.Symbols;

namespace SMSinspector.Core.Layouts;

/// <summary>The decomp's headers, parsed and laid out, with the validation report.</summary>
/// <remarks>The engine caches layouts in plain dictionaries: use it from one thread at a time.</remarks>
public sealed record LoadedLayouts(TypeCatalog Catalog, LayoutEngine Engine, LayoutReport Report)
{
    /// <summary>Parses every header and lays out every class; well under a second on the full decomp.</summary>
    public static LoadedLayouts Load(DecompRepository repository)
    {
        var catalog = DecompHeaders.Load(repository);
        var engine = new LayoutEngine(catalog);
        return new LoadedLayouts(catalog, engine, LayoutReport.Build(catalog, engine));
    }

    /// <summary>Finds a class by name as the user types it ("TMario", "JDrama::TNameRef") and lays it out.</summary>
    public ClassLayout? Find(string name, VersionMask version) =>
        Catalog.Resolve(name.Trim(), "") is ResolvedName.Class cls ? Engine.GetLayout(cls.QualifiedName, version) : null;
}
