using SMSinspector.Core.Layouts;
using SMSinspector.Core.Tests.Fakes;

namespace SMSinspector.Core.Tests.Layouts;

// Offset comments outside version blocks are JP offsets; inside PAL-only blocks they are PAL offsets.
public class PalLayoutTests
{
    private const VersionMask Jp = VersionMask.Jp;
    private const VersionMask Pal = VersionMask.Pal;

    [Fact]
    public void Member_inserted_in_pal_shifts_the_rest()
    {
        var engine = TestHeaders.Engine("""
            class TMenu {
            public:
                virtual void draw();
                /* 0x4 */ u8* mScreen;
            #ifdef VERSION_GMSP01
                /* 0x8 */ u8* mExtra;
            #endif
                /* 0x8 */ u16 mIndex;
                /* 0xA */ u8 mFlag;
                /* 0xC */ f32 mAlpha;
            };
            """);

        var jp = engine.GetLayout("TMenu", Jp)!;
        Assert.Equal(4, jp.CommentsMatched);
        Assert.Equal(0x10u, jp.Size);

        var pal = engine.GetLayout("TMenu", Pal)!;
        Assert.True(pal.IsVersionAffected);
        Assert.Equal(1, pal.CommentsMatched);
        Assert.Equal(0x8u, pal.Field("mExtra").Offset);
        Assert.Equal(0xCu, pal.Field("mIndex").Offset);
        Assert.Equal(0xEu, pal.Field("mFlag").Offset);
        Assert.Equal(0x10u, pal.Field("mAlpha").Offset);
        Assert.Equal(0x14u, pal.Size);
        Assert.Null(pal.PalUnverifiedAfter);
    }

    [Fact]
    public void Block_replaced_in_pal()
    {
        var pal = TestHeaders.Engine("""
            class TSound {
            public:
                /* 0x0 */ int mA;
            #ifdef VERSION_GMSP01
                /* 0x4 */ s32 mPalA;
                /* 0x8 */ bool mPalB;
                /* 0xA */ u16 mShared;
            #else
                /* 0x4 */ u16 mShared;
            #endif
                /* 0x8 */ void* mNext;
                /* 0xC */ u8 mTail;
            };
            """).GetLayout("TSound", Pal)!;

        Assert.Equal(3, pal.CommentsMatched);
        Assert.Equal(0xAu, pal.Field("mShared").Offset);
        Assert.Equal(0xCu, pal.Field("mNext").Offset);
        Assert.Equal(0x10u, pal.Field("mTail").Offset);
        Assert.Equal(0x14u, pal.Size);
    }

    [Fact]
    public void Shift_that_breaks_alignment_is_recomputed_from_verified_sizes()
    {
        var pal = TestHeaders.Engine("""
            struct TShifty {
                /* 0x0 */ u16 a;
            #ifdef VERSION_GMSP01
                /* 0x2 */ u16 palOnly;
            #endif
                /* 0x2 */ u8 b;
                /* 0x4 */ u32 c;
            };
            """).GetLayout("TShifty", Pal)!;

        Assert.Equal(4u, pal.Field("b").Offset);
        Assert.Equal(8u, pal.Field("c").Offset);
        Assert.Equal(0xCu, pal.Size);
    }

    [Fact]
    public void Reordered_members_keep_the_size()
    {
        var pal = TestHeaders.Engine("""
            struct TOrder {
                /* 0x0 */ int a;
            #ifdef VERSION_GMSP01
                /* 0x4 */ u8* q;
                /* 0x8 */ u8 r;
                /* 0xC */ u16* p;
            #else
                /* 0x4 */ u16* p;
                /* 0x8 */ u8* q;
                /* 0xC */ u8 r;
            #endif
                /* 0x10 */ int z;
            };
            """).GetLayout("TOrder", Pal)!;

        Assert.Equal(3, pal.CommentsMatched);
        Assert.Equal(0xCu, pal.Field("p").Offset);
        Assert.Equal(0x10u, pal.Field("z").Offset);
        Assert.Equal(0x14u, pal.Size);
    }

    [Fact]
    public void Pal_tail_member_moves_derived_and_embedding_classes()
    {
        var engine = TestHeaders.Engine("""
            class TPanel {
            public:
                /* 0x0 */ int a;
            #ifdef VERSION_GMSP01
                /* 0x4 */ int palTail;
            #endif
            };
            class TSubPanel : public TPanel {
            public:
                /* 0x4 */ u8 b;
                /* 0x8 */ int c;
            };
            struct TOuter {
                /* 0x0 */ TPanel mPanel;
                /* 0x4 */ u16 mAfter;
            };
            """);

        var sub = engine.GetLayout("TSubPanel", Pal)!;
        Assert.True(sub.IsVersionAffected);
        Assert.Equal(8u, sub.Field("b").Offset);
        Assert.Equal(0xCu, sub.Field("c").Offset);

        var outer = engine.GetLayout("TOuter", Pal)!;
        Assert.Equal(8u, outer.Field("mAfter").Offset);
    }

    [Fact]
    public void Version_select_in_an_array_size()
    {
        var engine = TestHeaders.Engine("""
            struct TArr {
                /* 0x0 */ u8 mBuf[VERSION_SELECT(GMSJ01(4), GMSP01(8))];
                /* 0x4 */ int mAfter;
            };
            """);

        Assert.Equal(4u, engine.GetLayout("TArr", Jp)!.Field("mAfter").Offset);
        Assert.Equal(8u, engine.GetLayout("TArr", Pal)!.Field("mAfter").Offset);
    }

    [Fact]
    public void Unknown_size_after_a_pal_difference_withholds_later_offsets()
    {
        var pal = TestHeaders.Engine("""
            struct TBlind {
                /* 0x0 */ u8 a;
            #ifdef VERSION_GMSP01
                /* 0x1 */ u8 palOnly;
            #endif
                /* 0x4 */ TMissing m;
                /* 0x8 */ u32 z;
            };
            """).GetLayout("TBlind", Pal)!;

        Assert.Equal(0x1u, pal.PalUnverifiedAfter);
        Assert.Null(pal.Field("m").Offset);
        Assert.Null(pal.Field("z").Offset);
        Assert.Contains(pal.Issues, i => i.Kind == IssueKind.PalUnverified);
    }

    [Fact]
    public void Untouched_classes_keep_jp_offsets_and_pal_only_classes_exist_only_in_pal()
    {
        var engine = TestHeaders.Engine("""
            struct TStable {
                /* 0x0 */ u8 a;
                /* 0x4 */ u32 b;
            };
            #ifdef VERSION_GMSP01
            class TPalThing {
            public:
                /* 0x0 */ int a;
                /* 0x4 */ int b;
            };
            #endif
            """);

        var stable = engine.GetLayout("TStable", Pal)!;
        Assert.False(stable.IsVersionAffected);
        Assert.Equal(4u, stable.Field("b").Offset);

        Assert.Null(engine.GetLayout("TPalThing", Jp));
        var palThing = engine.GetLayout("TPalThing", Pal)!;
        Assert.Equal(2, palThing.CommentsMatched);
        Assert.Equal(8u, palThing.Size);
    }
}
