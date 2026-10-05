using SMSinspector.Core.Layouts;
using SMSinspector.Core.Tests.Fakes;

namespace SMSinspector.Core.Tests.Layouts;

public class JpLayoutTests
{
    private const VersionMask Jp = VersionMask.Jp;

    [Fact]
    public void Padding_and_alignment()
    {
        var layout = TestHeaders.Engine("""
            struct TPad {
                /* 0x0 */ u8 a;
                /* 0x4 */ u32 b;
                /* 0x8 */ u16 c;
                /* 0x10 */ f64 d;
                /* 0x18 */ u8 e[3];
            };
            """).GetLayout("TPad", Jp)!;

        Assert.Equal(5, layout.CommentsChecked);
        Assert.Equal(5, layout.CommentsMatched);
        Assert.Equal(0x20u, layout.Size);
        Assert.Equal(8u, layout.Align);
        Assert.Empty(layout.Issues);
    }

    [Fact]
    public void Vtable_pointer_goes_where_the_first_virtual_function_is_declared()
    {
        var engine = TestHeaders.Engine("""
            class TFront {
            public:
                virtual void f();
                /* 0x4 */ int a;
            };
            class TBack {
            public:
                /* 0x0 */ int a;
                /* 0x4 */ int b;
                virtual void f();
            };
            class TBackMarked {
            public:
                /* 0x0 */ int a;
                /* 0x4 */ // vt
                virtual void f();
            };
            class TDerived : public TBack {
            public:
                virtual void f();
                /* 0xC */ int c;
            };
            """);

        var front = engine.GetLayout("TFront", Jp)!;
        Assert.Equal(0u, front.VptrOffset);
        Assert.Equal(8u, front.Size);
        Assert.Equal(1, front.CommentsMatched);

        Assert.Equal(8u, engine.GetLayout("TBack", Jp)!.VptrOffset);
        Assert.Equal(0xCu, engine.GetLayout("TBack", Jp)!.Size);

        var marked = engine.GetLayout("TBackMarked", Jp)!;
        Assert.Equal(4u, marked.VptrOffset);
        Assert.DoesNotContain(marked.Issues, i => i.Kind == IssueKind.VtableMismatch);

        var derived = engine.GetLayout("TDerived", Jp)!;
        Assert.Equal(8u, derived.VptrOffset);
        Assert.Equal(1, derived.CommentsMatched);
        Assert.Equal(0x10u, derived.Size);
        Assert.DoesNotContain(derived.Fields, f => f.IsVtablePointer);
    }

    [Fact]
    public void Inherited_fields_flatten_in_memory_order()
    {
        var layout = TestHeaders.Engine("""
            class TBack {
            public:
                /* 0x0 */ int a;
                /* 0x4 */ int b;
                virtual void f();
            };
            class TDerived : public TBack {
            public:
                /* 0xC */ int c;
            };
            """).GetLayout("TDerived", Jp)!;

        var flat = layout.Flatten().Select(f => (f.Field.Name, f.AbsoluteOffset)).ToList();

        Assert.Equal([("a", 0u), ("b", 4u), ("vtable", 8u), ("c", 0xCu)], flat.Select(f => (f.Name, f.AbsoluteOffset!.Value)));
    }

    [Fact]
    public void Typedef_arrays_templates_specializations_and_defaults()
    {
        var layout = TestHeaders.Engine("""
            typedef f32 TMat[3][4];
            template <typename T> struct TVec {
                /* 0x0 */ T x;
                /* 0x4 */ T y;
                /* 0x8 */ T z;
            };
            template <> struct TVec<u8> {
                /* 0x0 */ u8 packed[4];
            };
            template <typename T, typename A = TVec<T> > struct TPairT {
                /* 0x0 */ T first;
                /* 0x4 */ A second;
            };
            struct THolder {
                /* 0x0 */ TMat mMat;
                /* 0x30 */ TVec<float> mPos;
                /* 0x3C */ TVec<u8> mColor;
                /* 0x40 */ TPairT<int> mPair;
                /* 0x50 */ TMat* mMatPtr;
            };
            """).GetLayout("THolder", Jp)!;

        Assert.Equal(5, layout.CommentsMatched);
        Assert.Equal(5, layout.CommentsChecked);
        Assert.Equal(0x54u, layout.Size);
        Assert.Equal(0x30u, layout.Field("mMat").Size);
        Assert.Equal(4u, layout.Field("mColor").Size);
        Assert.Equal(0x10u, layout.Field("mPair").Size);
    }

    [Fact]
    public void Enums_unions_bitfields_and_trailing_arrays()
    {
        var layout = TestHeaders.Engine("""
            enum EMode { MODE_A, MODE_B };
            union UValue {
                int i;
                f32 f;
                u8 bytes[8];
            };
            struct TMisc {
                /* 0x0 */ EMode mMode;
                /* 0x4 */ UValue mValue;
                /* 0xC */ u16 mFlagA : 1;
                /* 0xC */ u16 mFlagB : 15;
                /* 0xE */ u16 mFlagC : 4;
                /* 0x10 */ int mCount;
                /* 0x14 */ u8 mData[];
            };
            """).GetLayout("TMisc", Jp)!;

        Assert.Equal(layout.CommentsChecked, layout.CommentsMatched);
        Assert.Equal(7, layout.CommentsChecked);
        Assert.Equal(8u, layout.Field("mValue").Size);
        Assert.Equal((0, 1), (layout.Field("mFlagA").BitOffset, layout.Field("mFlagA").BitWidth));
        Assert.Equal((1, 15), (layout.Field("mFlagB").BitOffset, layout.Field("mFlagB").BitWidth));
        Assert.Equal(0, layout.Field("mFlagC").BitOffset);
        Assert.Equal(0x14u, layout.Size);
    }

    [Fact]
    public void Disagreements_with_comments_are_reported()
    {
        var layout = TestHeaders.Engine("""
            struct TOdd {
                /* 0x0 */ int a;
                /* 0x8 */ int b;
                /* 0x8 */ int c;
                /* 0x4 */ int d;
                /* 0x10 */ TMissing e;
            };
            """).GetLayout("TOdd", Jp)!;

        Assert.Contains(layout.Issues, i => i.Kind == IssueKind.Gap && i.Member == "b");
        Assert.Contains(layout.Issues, i => i.Kind == IssueKind.Overlap && i.Member == "c");
        Assert.Contains(layout.Issues, i => i.Kind == IssueKind.OutOfOrder && i.Member == "d");
        Assert.Contains(layout.Issues, i => i.Kind == IssueKind.UnknownType && i.Member == "e");

        // The comment stays the displayed offset even when the computation disagrees.
        Assert.Equal(0x8u, layout.Field("b").Offset);
        Assert.Equal(OffsetSource.Comment, layout.Field("b").Source);
        Assert.Null(layout.Size);
    }

    [Fact]
    public void Virtual_bases_are_shared_and_placed_at_the_end()
    {
        var engine = TestHeaders.Engine("""
            class TRoot {
            public:
                virtual void calc();
            };
            class TLeftV : public virtual TRoot {
            public:
                virtual void calc();
                int a;
            };
            class TRightV : public virtual TRoot {
            public:
                virtual void calc();
                int b;
            };
            class TJoin : public TLeftV, public TRightV {
            public:
                /* 0x18 */ int c;
            };
            """);

        var left = engine.GetLayout("TLeftV", Jp)!;
        Assert.Equal(0xCu, left.NonVirtualSize);
        Assert.Equal(0x10u, left.Size);

        var join = engine.GetLayout("TJoin", Jp)!;
        Assert.Equal(1, join.CommentsMatched);
        Assert.Equal(0x1Cu, join.NonVirtualSize);
        Assert.Equal(0x20u, join.Size);
        Assert.Equal("TRoot", Assert.Single(join.VirtualBases).Name);
    }

    [Fact]
    public void Aligned_attribute_on_a_member()
    {
        var layout = TestHeaders.Engine("""
            struct TAligned {
                /* 0x0 */ u8 a __attribute__((aligned(4)));
                /* 0x1 */ u8 b;
                /* 0x4 */ u8 c __attribute__((aligned(4)));
            };
            """).GetLayout("TAligned", Jp)!;

        Assert.Equal(3, layout.CommentsMatched);
        Assert.Equal(8u, layout.Size);
    }

    [Fact]
    public void Members_without_comments_get_computed_offsets()
    {
        var layout = TestHeaders.Engine("""
            struct TPlain {
                u8 a;
                u32 b;
                u8 c;
            };
            """).GetLayout("TPlain", Jp)!;

        Assert.Equal(4u, layout.Field("b").Offset);
        Assert.Equal(8u, layout.Field("c").Offset);
        Assert.Equal(OffsetSource.Computed, layout.Field("c").Source);
        Assert.Equal(0xCu, layout.Size);
    }
}
