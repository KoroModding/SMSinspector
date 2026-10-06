using SMSinspector.Core.Layouts;
using SMSinspector.Core.Tests.Fakes;

namespace SMSinspector.Core.Tests.Layouts;

// Template instances looked up by name, as the user types them and as the demangler writes them.
public class TemplateLookupTests
{
    private static LoadedLayouts Load()
    {
        var catalog = TestHeaders.Catalog("""
            template <typename T> class TBox {
            public:
                /* 0x0 */ T mValue;
                virtual void update();
            };

            class TFooActor {
            public:
                virtual ~TFooActor();
            };
            """);
        var engine = new LayoutEngine(catalog);
        return new LoadedLayouts(catalog, engine, LayoutReport.Build(catalog, engine));
    }

    [Fact]
    public void Header_and_demangled_spellings_meet_on_one_instance()
    {
        var layouts = Load();

        var typed = layouts.Find("TBox<f32>", VersionMask.Pal);
        var demangled = layouts.Find("TBox<float>", VersionMask.Pal);

        Assert.NotNull(typed);
        Assert.Same(typed, demangled);
        Assert.Equal(0x4u, typed.VptrOffset);
    }

    [Fact]
    public void Multi_word_builtins_and_class_arguments_resolve()
    {
        var layouts = Load();

        Assert.Equal(0x4u, layouts.Find("TBox<unsigned char>", VersionMask.Pal)?.VptrOffset);
        Assert.Equal(0x4u, layouts.Find("TBox<TFooActor*>", VersionMask.Pal)?.VptrOffset);
    }

    [Theory]
    [InlineData("TMissing<int>")]
    [InlineData("TBox<")]
    [InlineData("TBox<int>*")]
    public void Unknown_or_malformed_names_give_nothing(string name)
    {
        Assert.Null(Load().Find(name, VersionMask.Pal));
    }

    [Fact]
    public void Plain_classes_still_resolve()
    {
        Assert.Equal("TFooActor", Load().Find(" TFooActor ", VersionMask.Pal)?.Name);
    }
}
