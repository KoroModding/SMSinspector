using System.Buffers.Binary;
using System.Text;
using SMSinspector.Core.Layouts;
using SMSinspector.Core.Live;
using SMSinspector.Core.Memory;
using SMSinspector.Core.Tests.Fakes;

namespace SMSinspector.Core.Tests.Live;

// Invented classes; offsets are always taken from the rows, never written here.
public class ObjectFieldsTests
{
    private const string Headers = """
        namespace Geo {
        template <typename T> struct TVec {
            T x;
            T y;
            T z;
        };
        }

        enum EMood { MOOD_CALM, MOOD_ANGRY = 4 };

        class TThing {
        public:
            virtual ~TThing();
            const char* mName;
            u16 mCode;
        };

        class TWidget : public TThing {
        public:
            Geo::TVec<f32> mPos;
            s16 mTilt;
            bool mOn;
            u32 mCount;
            u32 unk20;
            EMood mMood;
            u8 mBytes[3];
            char mLabel[8];
            TWidget* mNext;
            u32 mFlagA : 1;
            u32 mFlagB : 3;
            f32 mSpeed;
            u16 mSlotIndex;
        };
        """;

    private const uint Address = 0x80400000;

    private static (LayoutEngine Engine, ObjectFields Fields) Build(Action<LayoutEngine>? configure = null)
    {
        var engine = TestHeaders.Engine(Headers);
        configure?.Invoke(engine);
        var layout = engine.GetLayout("TWidget", VersionMask.Pal)!;
        return (engine, ObjectFields.Build(engine, layout));
    }

    private static FieldRow Row(ObjectFields fields, string path) =>
        fields.Rows.Concat(fields.Rows.SelectMany(r => r.Children)).Single(r => r.Path == path);

    private static uint Offset(ObjectFields fields, string path) => Row(fields, path).Offset!.Value;

    private sealed class Writer(ObjectFields fields)
    {
        public byte[] Bytes { get; } = new byte[fields.ReadSize];

        public Writer U32(string path, uint value)
        {
            BinaryPrimitives.WriteUInt32BigEndian(Bytes.AsSpan((int)Offset(fields, path)), value);
            return this;
        }

        public Writer U16(string path, ushort value)
        {
            BinaryPrimitives.WriteUInt16BigEndian(Bytes.AsSpan((int)Offset(fields, path)), value);
            return this;
        }

        public Writer F32(string path, float value)
        {
            BinaryPrimitives.WriteSingleBigEndian(Bytes.AsSpan((int)Offset(fields, path)), value);
            return this;
        }

        public Writer Raw(string path, params byte[] value)
        {
            value.CopyTo(Bytes.AsSpan((int)Offset(fields, path)));
            return this;
        }
    }

    private static string Text(ObjectFields fields, byte[] bytes, string path) =>
        FieldDecoder.Decode(Row(fields, path), bytes, PlausibilitySettings.Default).Text;

    private static PlausibilityFlag? Flag(ObjectFields fields, byte[] bytes, string path, PlausibilitySettings? settings = null) =>
        FieldDecoder.Decode(Row(fields, path), bytes, settings ?? PlausibilitySettings.Default).Flag;

    [Fact]
    public void Rows_follow_memory_order_with_the_base_first()
    {
        var (_, fields) = Build();
        var members = fields.Rows.Where(r => r.Kind == RowKind.Field).ToList();

        Assert.Equal(
            ["vtable", "mName", "mCode", "mPos", "mTilt", "mOn", "mCount", "unk20", "mMood", "mBytes", "mLabel", "mNext", "mFlagA", "mFlagB", "mSpeed", "mSlotIndex"],
            members.Select(r => r.Path));
        Assert.Equal(members.Select(r => r.Offset), members.Select(r => r.Offset).Order());
        Assert.Equal(fields.Layout.Size, fields.ReadSize);
        Assert.All(members, r => Assert.NotNull(r.Name));
        Assert.True(Row(fields, "unk20").HasUnknownName);
        Assert.False(Row(fields, "mCount").HasUnknownName);
    }

    [Fact]
    public void Inline_class_members_resolve_template_parameters()
    {
        var (_, fields) = Build();
        var pos = Row(fields, "mPos");

        Assert.True(pos.HasChildren);
        Assert.Equal(["mPos.x", "mPos.y", "mPos.z"], pos.Children.Select(c => c.Path));
        // Template arguments are kept without typedefs, so TVec<f32> and TVec<float> are one instance.
        Assert.All(pos.Children, c => Assert.Equal("float", c.TypeName));
        Assert.All(pos.Children, c => Assert.Equal(new DataType.Scalar(ScalarKind.Float, 4), c.Type));
        Assert.Equal([pos.Offset, pos.Offset + 4, pos.Offset + 8], pos.Children.Select(c => c.Offset));
        Assert.Equal(1, pos.Children[0].Depth);
    }

    [Fact]
    public void Array_elements_are_rows_with_the_element_type()
    {
        var (_, fields) = Build();
        var bytes = Row(fields, "mBytes");

        Assert.Equal(["mBytes[0]", "mBytes[1]", "mBytes[2]"], bytes.Children.Select(c => c.Path));
        Assert.All(bytes.Children, c => Assert.Equal("u8", c.TypeName));
        Assert.Equal(bytes.Offset + 2, bytes.Children[2].Offset);
        Assert.Equal(RowKind.Element, bytes.Children[0].Kind);
    }

    [Fact]
    public void Values_decode_big_endian()
    {
        var (_, fields) = Build();
        var bytes = new Writer(fields)
            .U32("vtable", 0x803A0000)
            .U32("mName", 0x80401000)
            .F32("mPos.x", 1.5f).F32("mPos.y", -2f).F32("mPos.z", 3f)
            .U16("mTilt", unchecked((ushort)-5))
            .Raw("mOn", 1)
            .U32("mCount", 133)
            .U32("mMood", 4)
            .Raw("mBytes", 1, 2, 255)
            .Raw("mLabel", [.. Encoding.ASCII.GetBytes("Bolt"), 0, (byte)'x'])
            .U32("mFlagA", 0xB0000000)
            .Bytes;

        Assert.Equal("0x80401000", Text(fields, bytes, "mName"));
        Assert.Equal("(1.5, -2, 3)", Text(fields, bytes, "mPos"));
        Assert.Equal("-2", Text(fields, bytes, "mPos.y"));
        Assert.Equal("-5", Text(fields, bytes, "mTilt"));
        Assert.Equal("true", Text(fields, bytes, "mOn"));
        Assert.Equal("133 (0x00000085)", Text(fields, bytes, "mCount"));
        Assert.Equal("4 (MOOD_ANGRY)", Text(fields, bytes, "mMood"));
        Assert.Equal("[1, 2, 255 (0xFF)]", Text(fields, bytes, "mBytes"));
        Assert.Equal("\"Bolt\"", Text(fields, bytes, "mLabel"));
        Assert.Equal("null", Text(fields, bytes, "mNext"));
        Assert.Equal("1", Text(fields, bytes, "mFlagA"));
        Assert.Equal("3", Text(fields, bytes, "mFlagB"));
    }

    [Fact]
    public void Bytes_no_member_covers_are_gap_rows()
    {
        var (_, fields) = Build();
        var afterOn = Offset(fields, "mOn") + 1;
        var gap = fields.Rows.Single(r => r.Kind == RowKind.Gap && r.Offset == afterOn);
        var bytes = new byte[fields.ReadSize];
        bytes[afterOn] = 0xAB;

        Assert.Equal(Offset(fields, "mCount") - afterOn, gap.Size);
        Assert.Null(gap.Name);
        Assert.Equal("AB", FieldDecoder.Decode(gap, bytes, PlausibilitySettings.Default).Text);
    }

    [Fact]
    public void One_object_takes_one_read()
    {
        var (_, fields) = Build();
        var memory = new CountingMemory(new FakeGameMemory(Address, new byte[0x100]));
        var buffer = new byte[fields.ReadSize];

        Assert.True(fields.TryRead(memory, Address, buffer));
        Assert.Equal([(Address, (int)fields.ReadSize)], memory.Reads);
        Assert.False(fields.TryRead(memory, Address, new byte[fields.ReadSize - 1]));
    }

    [Fact]
    public void Null_pointers_pass_and_pointers_outside_mem1_are_flagged()
    {
        var (_, fields) = Build();
        var bytes = new Writer(fields).U32("mName", 0x12345678).Bytes;

        Assert.Null(Flag(fields, bytes, "mNext"));
        Assert.Equal("pointer range", Flag(fields, bytes, "mName")?.Rule);
        Assert.DoesNotContain("physical", Flag(fields, bytes, "mName")?.Message);
    }

    [Fact]
    public void A_flagged_member_flags_its_inline_class_and_array()
    {
        var (_, fields) = Build();
        var bytes = new Writer(fields).F32("mPos.z", float.NaN).Bytes;

        Assert.Equal("f32 NaN", Flag(fields, bytes, "mPos")?.Rule);
        Assert.Null(Flag(fields, bytes, "mPos.x"));
    }

    [Fact]
    public void The_top_bit_rule_fires_on_counters_and_indexes()
    {
        var (_, fields) = Build();
        var bytes = new Writer(fields).U32("mCount", 0x80000000).U16("mSlotIndex", 0xFFFF).Bytes;

        Assert.Equal(FlagSeverity.Suspect, Flag(fields, bytes, "mCount")?.Severity);
        Assert.Equal("unsigned top bit", Flag(fields, bytes, "mSlotIndex")?.Rule);
        Assert.Contains("-1 if read as signed", Flag(fields, bytes, "mSlotIndex")?.Message);
    }

    [Fact]
    public void The_top_bit_rule_skips_other_names_unnamed_members_and_bytes()
    {
        var (_, fields) = Build();
        var bytes = new Writer(fields).U16("mCode", 0x8000).U32("unk20", 0x80000000).Raw("mBytes", 0x80).Bytes;

        Assert.Null(Flag(fields, bytes, "mCode"));
        Assert.Null(Flag(fields, bytes, "unk20"));
        Assert.Null(Flag(fields, bytes, "mBytes"));
    }

    [Fact]
    public void Bools_and_floats_follow_the_settings()
    {
        var (_, fields) = Build();
        var bytes = new Writer(fields).Raw("mOn", 2).F32("mSpeed", 200f).Bytes;

        Assert.Equal("2", Text(fields, bytes, "mOn"));
        Assert.Equal("bool range", Flag(fields, bytes, "mOn")?.Rule);
        Assert.Null(Flag(fields, bytes, "mSpeed"));
        Assert.Equal("f32 too large", Flag(fields, bytes, "mSpeed", new PlausibilitySettings { FloatLimit = 100f })?.Rule);
    }

    [Fact]
    public void A_pal_suspect_range_is_noted_on_its_rows()
    {
        var plain = Build().Fields;
        var first = Offset(plain, "mCount");
        var last = Offset(plain, "unk20");

        var (_, fields) = Build(e => e.SetPalSuspects([new PalSuspect("TWidget", first, last, "invented reason")]));

        Assert.StartsWith("PAL suspect", Row(fields, "mCount").Note);
        Assert.EndsWith("invented reason", Row(fields, "unk20").Note);
        Assert.Null(Row(fields, "mOn").Note);
        Assert.Null(Row(fields, "mMood").Note);
    }

    [Fact]
    public void Withheld_offsets_are_listed_last_and_not_read()
    {
        var plain = Build().Fields;
        var from = Offset(plain, "mMood");

        var (_, fields) = Build(e => e.SetPalContradictions([new PalContradiction("TWidget", from, "invented reason")]));
        var mood = Row(fields, "mMood");

        Assert.Null(mood.Offset);
        Assert.Contains("unverified", mood.Note);
        Assert.Equal("mSlotIndex", fields.Rows[^1].Path);
        Assert.Equal(Offset(plain, "unk20") + 4, fields.ReadSize);
        Assert.Equal("offset unknown", FieldDecoder.Decode(mood, new byte[fields.ReadSize], PlausibilitySettings.Default).Text);
    }

    [Fact]
    public void Field_types_resolve_through_typedefs_pointers_and_hidden_members()
    {
        var (engine, fields) = Build();
        var layout = fields.Layout;
        DataType TypeOf(string name) => engine.DescribeField(Row(fields, name).Name is { } n
            ? layout.Flatten().Single(f => f.Field.Name == n.Value).Owner
            : layout, layout.Flatten().Single(f => f.Field.Name == name).Field);

        Assert.Equal(new DataType.Pointer(null), TypeOf("vtable"));
        Assert.Equal(new DataType.Scalar(ScalarKind.Unsigned, 2), TypeOf("mCode"));
        Assert.Equal(new DataType.Scalar(ScalarKind.Bool, 1), TypeOf("mOn"));
        Assert.Equal("TWidget", Assert.IsType<DataType.Pointer>(TypeOf("mNext")).Target?.Name);
        Assert.Equal(new DataType.ArrayOf(new DataType.Scalar(ScalarKind.Signed, 1), 8), TypeOf("mLabel"));
        Assert.IsType<DataType.Enumeration>(TypeOf("mMood"));
    }

    private sealed class CountingMemory(IGameMemory inner) : IGameMemory
    {
        public List<(uint Address, int Length)> Reads { get; } = [];

        public bool TryRead(uint address, Span<byte> destination)
        {
            Reads.Add((address, destination.Length));
            return inner.TryRead(address, destination);
        }
    }
}
