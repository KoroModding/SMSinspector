using SMSinspector.Core.Layouts;
using SMSinspector.Core.Tests.Fakes;

namespace SMSinspector.Core.Tests.Layouts;

public class LayoutReportTests
{
    // A consistent set of headers: every JP comment must be reproduced before PAL is trusted.
    private const string Consistent = """
        class TNode {
        public:
            virtual ~TNode();
            /* 0x4 */ const char* mName;
            /* 0x8 */ u16 mKey;
        };
        class TItem : public TNode {
        public:
            /* 0xC */ f32 mPos[3];
        #ifdef VERSION_GMSP01
            /* 0x18 */ u8 mRegion;
        #endif
            /* 0x18 */ u16 mCount;
            /* 0x1C */ TNode* mOwner;
        };
        """;

    private static LayoutReport Report(params string[] sources)
    {
        var catalog = TestHeaders.Catalog(sources);
        return LayoutReport.Build(catalog, new LayoutEngine(catalog));
    }

    [Fact]
    public void Consistent_headers_reproduce_every_jp_comment()
    {
        var report = Report(Consistent);

        Assert.Equal(report.JpCommentsChecked, report.JpCommentsMatched);
        Assert.Equal(1.0, report.JpMatchRate);
        Assert.Empty(report.JpLayoutsWithIssues);
        Assert.Empty(report.ParseProblems);
    }

    [Fact]
    public void Pal_differences_are_listed_with_their_shift()
    {
        var report = Report(Consistent);
        var text = report.ToText("0123456789ab");

        var (_, pal) = Assert.Single(report.PalAffected);
        Assert.Equal("TItem", pal.Name);
        Assert.Contains("Decomp commit: 0123456789ab", text);
        Assert.Contains("mRegion", text);
        Assert.Contains("JP 0x18 -> PAL 0x1A", text);

        // mOwner realigns to 0x1C in PAL, so it is not listed as moved.
        Assert.Equal(0x1Cu, pal.Field("mOwner").Offset);
        Assert.DoesNotContain("mOwner", text);
    }

    [Fact]
    public void Remarks_name_the_class_and_member()
    {
        var text = Report("""
            struct TOdd {
                /* 0x0 */ int a;
                /* 0x8 */ int b;
            };
            """).ToText();

        Assert.Contains("TOdd (test0.hpp:1)", text);
        Assert.Contains("Gap", text);
        Assert.Contains("b: Comment 0x8 is 0x4 bytes after the computed 0x4.", text);
    }
}
