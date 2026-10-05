using SMSinspector.Core.Layouts;
using SMSinspector.Core.Tests.Fakes;

namespace SMSinspector.Core.Tests.Layouts;

public class LayoutTextTests
{
    private const string Source = """
        class TNode {
        public:
            virtual ~TNode();
            /* 0x4 */ u16 mKey;
        };
        class TItem : public TNode {
        public:
            /* 0x8 */ u8 mFlag;
        #ifdef VERSION_GMSP01
            /* 0xC */ u32 mRegion;
        #endif
            /* 0xC */ u32 mCount;
            /* 0x14 */ u8 mTail;
        };
        """;

    private static string Describe(VersionMask version) =>
        LayoutText.Describe(TestHeaders.Engine(Source).GetLayout("TItem", version)!);

    [Fact]
    public void Lists_inherited_fields_first_with_their_owner()
    {
        var lines = Describe(VersionMask.Pal).Split(Environment.NewLine);

        Assert.StartsWith("TItem (PAL), size 0x1C", lines[0]);
        Assert.Contains(lines, l => l.StartsWith("0x0 ") && l.Contains("vtable") && l.Contains("TNode"));
        Assert.Contains(lines, l => l.StartsWith("0x4 ") && l.Contains("mKey") && l.Contains("TNode, comment, verified"));
    }

    [Fact]
    public void Separates_padding_from_undocumented_gaps()
    {
        var text = Describe(VersionMask.Pal);

        // 0x9..0xC is padding before the u32; 0x6..0x8 is the end of TNode.
        Assert.Contains("0x9      0x3    (padding)", text);
        Assert.Contains("0x6      0x2    (padding)", text);

        // In JP, mTail's comment (0x14) leaves 4 bytes after mCount that nothing explains.
        Assert.Contains("0x10     0x4    (gap)", Describe(VersionMask.Jp));
    }

    [Fact]
    public void Marks_pal_only_and_moved_members()
    {
        var text = Describe(VersionMask.Pal);

        Assert.Contains("mRegion", text);
        Assert.Contains("PAL only, comment, verified", text);
        Assert.Contains("moved from JP 0xC", text);
    }
}
