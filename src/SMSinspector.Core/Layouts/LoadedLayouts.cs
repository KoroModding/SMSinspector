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

    /// <summary>
    /// Finds a class by name and lays it out: as the user types it ("TMario", "JDrama::TNameRef",
    /// "TBox&lt;f32&gt;") or as the demangler writes it ("TParamRT&lt;unsigned char&gt;").
    /// </summary>
    public ClassLayout? Find(string name, VersionMask version)
    {
        name = name.Trim();
        if (!name.Contains('<') && Catalog.Resolve(name, "") is ResolvedName.Class cls)
        {
            return Engine.GetLayout(cls.QualifiedName, version);
        }

        return HeaderParser.TryParseType(name, out var type) ? Engine.GetLayout(type, version) : null;
    }
}
