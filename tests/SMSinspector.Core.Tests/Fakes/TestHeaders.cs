using SMSinspector.Core.Layouts;

namespace SMSinspector.Core.Tests.Fakes;

/// <summary>Builds catalogs from headers written for the tests. Every class name is invented.</summary>
internal static class TestHeaders
{
    /// <summary>The integer and float typedefs the decomp's headers rely on.</summary>
    public const string Prelude = """
        typedef unsigned char u8;
        typedef signed char s8;
        typedef unsigned short u16;
        typedef signed short s16;
        typedef unsigned long u32;
        typedef signed long s32;
        typedef float f32;
        typedef double f64;
        """;

    public static TypeCatalog Catalog(params string[] sources) =>
        new([HeaderParser.Parse("prelude.h", Prelude), .. sources.Select((s, i) => HeaderParser.Parse($"test{i}.hpp", s))]);

    public static LayoutEngine Engine(params string[] sources) => new(Catalog(sources));

    public static FieldLayout Field(this ClassLayout layout, string name) => layout.Fields.Single(f => f.Name == name);
}
