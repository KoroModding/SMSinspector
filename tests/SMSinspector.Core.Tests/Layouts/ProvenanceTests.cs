using SMSinspector.Core.Layouts;
using SMSinspector.Core.Tests.Fakes;

namespace SMSinspector.Core.Tests.Layouts;

// Plan 5.8: every name a layout holds comes with where it was read. The headers below are
// invented and cover each way the engine produces a name: classes, members, bases, template
// instances, bit-fields, anonymous unions, nested classes, hidden pointers, PAL-only blocks.
public class ProvenanceTests
{
    private const string Header = """
        template <typename T> struct TBox {
            /* 0x0 */ T mValue;
        };

        class TBase {
        public:
            virtual ~TBase();
            /* 0x4 */ u32 mFlags;
        };

        class TShared : public virtual TBase {
        public:
            /* 0x8 */ f32 mShared;
        };

        class TFooActor : public TBase, public TBox<f32> {
        public:
            struct TInner {
                /* 0x0 */ u16 mCount;
            };

            /* 0xC */ u32 mBits : 4;
            /* 0xC */ u32 mMore : 12;
            union {
                /* 0x10 */ f32 mAsFloat;
                /* 0x10 */ u32 mAsWord;
            };
        #ifdef VERSION_GMSP01
            /* 0x14 */ u32 mPalOnly;
        #endif
            /* 0x14 */ TInner mInner;
            /* 0x18 */ u8 unk18;
        };
        """;

    [Theory]
    [InlineData(VersionMask.Jp)]
    [InlineData(VersionMask.Pal)]
    public void Every_name_in_a_computed_layout_has_a_provenance(VersionMask version)
    {
        var engine = TestHeaders.Engine(Header);
        var checkedNames = 0;

        foreach (var decl in engine.Catalog.AllClasses.Where(c => !c.IsTemplate && c.File != "prelude.h"))
        {
            var layout = engine.GetLayout(decl, version);
            Assert.NotNull(layout);
            checkedNames += CheckNames(layout, []);
        }

        // The classes, their fields, the template instance, the hidden pointers.
        Assert.True(checkedNames >= 20, $"only {checkedNames} names checked");
    }

    [Fact]
    public void Member_names_point_at_their_header_line()
    {
        var layout = TestHeaders.Engine(Header).GetLayout("TFooActor", VersionMask.Pal)!;

        var source = layout.Field("unk18").Identity.Source;
        Assert.Equal(ProvenanceKind.Header, source.Kind);
        Assert.Equal("test0.hpp", source.File);
        Assert.Equal(32, source.Line);
        Assert.Equal(16, layout.Identity.Source.Line);
    }

    [Fact]
    public void Hidden_pointers_name_the_class_the_compiler_added_them_to()
    {
        var engine = TestHeaders.Engine(Header);

        var vtable = engine.GetLayout("TBase", VersionMask.Pal)!.Fields.Single(f => f.IsVtablePointer).Identity.Source;
        Assert.Equal(ProvenanceKind.Compiler, vtable.Kind);
        Assert.Equal(5, vtable.Line);
        Assert.Contains("vtable pointer of TBase", vtable.Detail);

        var vbase = engine.GetLayout("TShared", VersionMask.Pal)!.Fields.Single(f => f.Name.StartsWith("vbase", StringComparison.Ordinal));
        Assert.Equal(ProvenanceKind.Compiler, vbase.Identity.Source.Kind);
    }

    [Fact]
    public void Template_instances_point_at_the_template()
    {
        var foo = TestHeaders.Engine(Header).GetLayout("TFooActor", VersionMask.Pal)!;

        var box = foo.Bases.Single(b => b.Layout.Name.StartsWith("TBox", StringComparison.Ordinal)).Layout;
        Assert.Equal(1, box.Identity.Source.Line);
        Assert.Contains("template instance", box.Identity.Source.Detail);
    }

    [Fact]
    public void Anonymous_members_get_a_label_with_a_source()
    {
        var foo = TestHeaders.Engine(Header).GetLayout("TFooActor", VersionMask.Pal)!;

        var anonymous = foo.Fields.Single(f => f.Name == "(anonymous)");
        Assert.Equal(24, anonymous.Identity.Source.Line);
    }

    [Fact]
    public void Names_and_provenances_cannot_be_empty()
    {
        Assert.Throws<ArgumentException>(() => new SourcedName("", Provenance.Header("a.hpp", 1)));
        Assert.Throws<ArgumentNullException>(() => new SourcedName("mFoo", null!));
        Assert.Throws<ArgumentException>(() => Provenance.Header(" ", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Provenance.Header("a.hpp", 0));
        Assert.Throws<ArgumentException>(() => Provenance.Executable("main.dol", 0x80003000, ""));
        Assert.Throws<ArgumentException>(() => Provenance.Sibling(Provenance.Executable("main.dol", 0x80003000, "blr"), "TFoo::mBar"));
    }

    [Fact]
    public void Provenance_text_says_where_to_look()
    {
        Assert.Equal("include/Foo.hpp:12", Provenance.Header("include/Foo.hpp", 12).ToString());
        Assert.Equal("sys/main.dol at 0x80003000 (lwz r3, 0x8(r3); blr)",
            Provenance.Executable("sys/main.dol", 0x80003000, "lwz r3, 0x8(r3); blr").ToString());
    }

    private static int CheckNames(ClassLayout layout, HashSet<ClassLayout> seen)
    {
        if (!seen.Add(layout))
        {
            return 0;
        }

        var count = 1;
        AssertSourced(layout.Identity, layout.Name);
        foreach (var field in layout.Fields)
        {
            AssertSourced(field.Identity, $"{layout.Name}::{field.Name}");
            count++;
        }

        foreach (var baseLayout in layout.Bases)
        {
            count += CheckNames(baseLayout.Layout, seen);
        }

        foreach (var virtualBase in layout.VirtualBases)
        {
            count += CheckNames(virtualBase, seen);
        }

        return count;
    }

    private static void AssertSourced(SourcedName name, string where)
    {
        Assert.False(string.IsNullOrWhiteSpace(name.Value), $"{where}: empty name");
        Assert.NotNull(name.Source);
        Assert.False(string.IsNullOrWhiteSpace(name.Source.File), $"{where}: no file");
        if (name.Source.Kind is ProvenanceKind.Header or ProvenanceKind.Compiler)
        {
            Assert.True(name.Source.Line > 0, $"{where}: no line");
        }
    }
}
