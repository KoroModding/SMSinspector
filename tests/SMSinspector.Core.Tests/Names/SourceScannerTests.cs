using SMSinspector.Core.Layouts;
using SMSinspector.Core.Names;

namespace SMSinspector.Core.Tests.Names;

// Sources written for the tests; every class and member name is invented.
public class SourceScannerTests
{
    private static ScannedSource Scan(string source, VersionMask version = VersionMask.Pal) =>
        SourceScanner.Scan("test.cpp", source, version);

    [Fact]
    public void Inline_bodies_belong_to_the_enclosing_class()
    {
        var scanned = Scan("""
            class TFooActor : public TBase {
            public:
                f32 getSpeed() const { return unk1C; }
                void setSpeed(f32 speed) { unk1C = speed; }
                /* 0x1C */ f32 unk1C;
            };
            """);

        Assert.Collection(scanned.Bodies,
            getter =>
            {
                Assert.Equal("TFooActor", getter.ClassName);
                Assert.Equal("getSpeed", getter.Name);
                Assert.True(getter.IsConst);
                Assert.Empty(getter.Parameters);
            },
            setter =>
            {
                Assert.Equal("setSpeed", setter.Name);
                Assert.False(setter.IsConst);
                Assert.Equal(["speed"], setter.Parameters);
            });
    }

    [Fact]
    public void Out_of_class_definitions_use_their_qualifier_and_namespace()
    {
        var scanned = Scan("""
            namespace Gfx {
            void TCanvas::setColor(u8 red, u8 green) { unk4 = red; }
            }
            int TPlain::count() const { return unk8; }
            static void helper(int a) { a++; }
            """);

        Assert.Equal(2, scanned.Bodies.Count);
        Assert.Equal("Gfx::TCanvas", scanned.Bodies[0].ClassName);
        Assert.Equal(["red", "green"], scanned.Bodies[0].Parameters);
        Assert.Equal("TPlain", scanned.Bodies[1].ClassName);
        Assert.True(scanned.Bodies[1].IsConst);
    }

    [Fact]
    public void Nested_classes_get_a_qualified_name()
    {
        var scanned = Scan("""
            class TOuter {
                class TInner {
                    int get() { return unk0; }
                };
                int value() { return unk4; }
            };
            """);

        Assert.Equal("TOuter::TInner", scanned.Bodies[0].ClassName);
        Assert.Equal("TOuter", scanned.Bodies[1].ClassName);
    }

    [Fact]
    public void Unused_comments_mark_the_next_or_trailed_declaration()
    {
        var scanned = Scan("""
            class TFooActor {
            public:
                // UNUSED
                void stopAll();
                bool isReady() const; // UNUSED
                void update();
                // UNUSED (inlined in the original)
                int getCount() const { return unk10; }
            };
            """);

        Assert.Equal(["stopAll", "isReady"], scanned.UnusedDeclarations.Select(d => d.Name));
        Assert.True(Assert.Single(scanned.Bodies).IsUnusedMarked);
    }

    [Fact]
    public void Initializers_and_enums_are_not_bodies()
    {
        var scanned = Scan("""
            static int sTable[] = { sizeof(int), 2 };
            enum TMode { MODE_A, MODE_B };
            class TFoo { int get() { return unk0; } };
            """);

        Assert.Equal("get", Assert.Single(scanned.Bodies).Name);
    }

    [Fact]
    public void Template_headers_are_skipped()
    {
        var scanned = Scan("""
            template <typename T> class TBox {
                T get() { return unk0; }
            };
            """);

        Assert.Equal("TBox", Assert.Single(scanned.Bodies).ClassName);
    }

    [Fact]
    public void Only_the_requested_version_is_read()
    {
        const string source = """
            class TFoo {
            #ifdef VERSION_GMSP01
                int getPal() { return unk4; }
            #else
                int getJp() { return unk4; }
            #endif
            };
            """;

        Assert.Equal("getPal", Assert.Single(Scan(source, VersionMask.Pal).Bodies).Name);
        Assert.Equal("getJp", Assert.Single(Scan(source, VersionMask.Jp).Bodies).Name);
    }

    [Fact]
    public void Body_analysis_keeps_own_members_only()
    {
        var body = Scan("class TFoo { void f() { unk4->unk8 = this->unkC + mOther.unk10; } };").Bodies[0];

        Assert.Equal(["unk4", "unkC"], BodyAnalyzer.UnknownReferences(body.Body));
    }

    [Theory]
    [InlineData("int f() { return unk4; }", BodyShape.Matching)]
    [InlineData("int f() { return this->unk4; }", BodyShape.Matching)]
    [InlineData("void f(int v) { unk4 = v; }", BodyShape.Matching)]
    [InlineData("void f(int v) { unk4 = 1; }", BodyShape.Short)]
    [InlineData("int f() { return unk4 != 0; }", BodyShape.Short)]
    [InlineData("void f() { unk4 = 1; unk8 = 2; unk10 = 3; }", BodyShape.Long)]
    [InlineData("void f() { if (unk4) { unk8 = 2; } }", BodyShape.Long)]
    public void Bodies_are_classified_by_shape(string method, BodyShape expected)
    {
        var body = Scan($"class TFoo {{ {method} }};").Bodies[0];

        Assert.Equal(expected, BodyAnalyzer.Classify(body));
    }

    [Fact]
    public void Body_text_reads_naturally()
    {
        var body = Scan("class TFoo { int f(int i) { return unk4->get(i) >> 8 == 0; } };").Bodies[0];

        Assert.Equal("return unk4->get(i) >> 8 == 0;", BodyAnalyzer.Format(body.Body));
    }
}
