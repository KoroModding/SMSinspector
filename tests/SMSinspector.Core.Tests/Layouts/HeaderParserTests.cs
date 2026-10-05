using SMSinspector.Core.Layouts;

namespace SMSinspector.Core.Tests.Layouts;

public class HeaderParserTests
{
    private const string Source = """
        namespace Gfx {
        class TBase {
        public:
            virtual ~TBase();
            /* 0x4 */ int mA;
        };
        }

        class TWidget : public Gfx::TBase {
        public:
            TWidget() : mB(0) { mC = 1; }
            void update();
            int get() const { return mB; }
            static int sCount;
            const static int cTable[];
            enum { MAX_ROWS = 4 };

            /* 0x8 */ u8 mB, mC;
            /* 0xC */ void (*mCallback)(int, int);
            /* 0x10 */ TWidget* (*mRows)[3];
            /* 0x14 */ struct { s16 a; s16 b; } mPair;
            /* 0x18 */ u32 mFlags : 3;
            /* 0x18 */ u32 mMode : 5;
            /* 0x1C */ Unknown::TThing<
                int, f32> mLong;
            /* 0x20 */ // vt
        };

        typedef struct TagPoint {
            f32 x, y;
        } TPoint;
        typedef f32 TMat[3][4];
        using TAlias = TPoint;
        namespace Std { typedef unsigned long TSize; }
        using Std::TSize;

        template <typename T, typename A = TAlloc<T> > class TBox {
            /* 0x0 */ T mValue;
        };
        template <> struct TBox<f32> {
            /* 0x0 */ f32 mValue;
        };
        """;

    private static HeaderFile Parse() => HeaderParser.Parse("widget.hpp", Source);

    private static ClassDecl Class(string name) => Parse().Classes.Single(c => c.QualifiedName == name && c.SpecializationArgs is null);

    [Fact]
    public void Namespaces_bases_and_virtual_position()
    {
        var baseClass = Class("Gfx::TBase");
        Assert.True(baseClass.DeclaresVirtual);
        Assert.Equal(0, baseClass.FirstVirtualAfterMembers);
        Assert.Equal(0x4u, baseClass.Members.Single().CommentOffset);

        var widget = Class("TWidget");
        Assert.Equal("Gfx::TBase", Assert.Single(widget.Bases).Type.Name);
        Assert.False(widget.DeclaresVirtual);
    }

    [Fact]
    public void Only_data_members_are_kept_in_order()
    {
        var names = Class("TWidget").Members.Select(m => m.Name).ToList();

        Assert.Equal(["mB", "mC", "mCallback", "mRows", "mPair", "mFlags", "mMode", "mLong", "vt"], names);
    }

    [Fact]
    public void Offset_comment_goes_to_the_first_declarator_only()
    {
        var members = Class("TWidget").Members;

        Assert.Equal(0x8u, members.Single(m => m.Name == "mB").CommentOffset);
        Assert.Null(members.Single(m => m.Name == "mC").CommentOffset);
    }

    [Fact]
    public void Pointer_declarators()
    {
        var members = Class("TWidget").Members;

        Assert.True(members.Single(m => m.Name == "mCallback").Type.IsFunctionPointer);
        var rows = members.Single(m => m.Name == "mRows").Type;
        Assert.Equal(2, rows.PointerDepth);
        Assert.Empty(rows.Dims);
    }

    [Fact]
    public void Inline_struct_bitfields_multiline_types_and_markers()
    {
        var members = Class("TWidget").Members;

        Assert.StartsWith("TWidget::<anonymous@", members.Single(m => m.Name == "mPair").Type.Name);
        Assert.Equal("3", members.Single(m => m.Name == "mFlags").BitWidth);
        Assert.Equal(2, members.Single(m => m.Name == "mLong").Type.TemplateArgs.Count);

        var marker = members.Single(m => m.Kind == MemberKind.VtableMarker);
        Assert.Equal(0x20u, marker.CommentOffset);
    }

    [Fact]
    public void Enums_typedefs_and_using()
    {
        var file = Parse();

        Assert.Contains(file.Enums, e => e.Enumerators.Any(i => i.Name == "MAX_ROWS"));
        Assert.Equal("TagPoint", file.Typedefs.Single(t => t.QualifiedName == "TPoint").Target.Name);
        Assert.Equal(["3", "4"], file.Typedefs.Single(t => t.QualifiedName == "TMat").Target.Dims);
        Assert.Equal("TPoint", file.Typedefs.Single(t => t.QualifiedName == "TAlias").Target.Name);
        Assert.Equal("Std::TSize", file.Typedefs.Single(t => t.QualifiedName == "TSize").Target.Name);
        Assert.Equal(2, file.Classes.Single(c => c.QualifiedName == "TagPoint").Members.Count);
    }

    [Fact]
    public void Templates_with_defaults_and_specializations()
    {
        var file = Parse();
        var primary = file.Classes.Single(c => c.QualifiedName == "TBox" && c.IsTemplate);
        Assert.Equal(["T", "A"], primary.TemplateParams);
        Assert.Null(primary.TemplateParamDefaults[0]);
        Assert.Equal("TAlloc<T>", primary.TemplateParamDefaults[1]?.ToString());

        var specialization = file.Classes.Single(c => c.QualifiedName == "TBox" && c.SpecializationArgs is not null);
        Assert.Equal("f32", Assert.Single(specialization.SpecializationArgs!).ToString());
    }

    [Fact]
    public void Nothing_left_unread()
    {
        Assert.Empty(Parse().Problems);
    }
}
