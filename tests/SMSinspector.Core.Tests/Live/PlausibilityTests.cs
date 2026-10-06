using SMSinspector.Core.Live;

namespace SMSinspector.Core.Tests.Live;

public class PlausibilityTests
{
    private static readonly PlausibilitySettings Defaults = PlausibilitySettings.Default;

    [Theory]
    [InlineData(0x00000000u)]
    [InlineData(0x80000000u)]
    [InlineData(0x817FFFFCu)]
    public void Null_and_mem1_pointers_pass(uint value) => Assert.Null(Plausibility.CheckPointer(value, Defaults));

    [Theory]
    [InlineData(0x00000001u)]
    [InlineData(0x12345678u)]
    [InlineData(0x81800000u)]
    public void Other_pointers_are_flagged_with_the_range(uint value)
    {
        var flag = Plausibility.CheckPointer(value, Defaults);

        Assert.NotNull(flag);
        Assert.Equal(FlagSeverity.Warning, flag.Severity);
        Assert.Contains("0x80000000..0x81800000", flag.Message);
    }

    [Fact]
    public void Low_pointers_are_explained_as_physical_addresses()
    {
        var flag = Plausibility.CheckPointer(0x00400000, Defaults);

        Assert.Equal("pointer range", flag?.Rule);
        Assert.EndsWith("Value below 0x01800000: probably a physical address.", flag?.Message);
    }

    [Theory]
    [InlineData("mCount")]
    [InlineData("mObjNum")]
    [InlineData("mSlotIndex")]
    [InlineData("mSize")]
    [InlineData("len")]
    public void Counter_names_are_recognised(string name) => Assert.True(Plausibility.IsCounterName(name));

    [Theory]
    [InlineData("mNameHash")]
    [InlineData("mStateFlags")]
    [InlineData("unk20")]
    public void Other_names_are_not_counters(string name) => Assert.False(Plausibility.IsCounterName(name));

    [Theory]
    [InlineData(0f)]
    [InlineData(-1234.5f)]
    [InlineData(1e7f)]
    public void Ordinary_floats_pass(float value) => Assert.Null(Plausibility.CheckFloat(value, Defaults));

    [Theory]
    [InlineData(float.NaN, "f32 NaN")]
    [InlineData(float.PositiveInfinity, "f32 infinite")]
    [InlineData(-2e7f, "f32 too large")]
    public void Odd_floats_are_flagged(float value, string rule) => Assert.Equal(rule, Plausibility.CheckFloat(value, Defaults)?.Rule);

    [Fact]
    public void Denormal_floats_are_flagged()
    {
        var flag = Plausibility.CheckFloat(BitConverter.Int32BitsToSingle(1), Defaults);

        Assert.Equal("f32 denormal", flag?.Rule);
        Assert.Contains("1.1754944E-38", flag?.Message);
    }

    [Fact]
    public void The_float_limit_is_a_setting_and_the_message_names_it()
    {
        var settings = new PlausibilitySettings { FloatLimit = 100f };

        Assert.Null(Plausibility.CheckFloat(100f, settings));
        Assert.Contains("larger than 100 ", Plausibility.CheckFloat(200f, settings)?.Message);
    }

    [Fact]
    public void Bools_other_than_0_or_1_are_flagged()
    {
        Assert.Null(Plausibility.CheckBool(0));
        Assert.Null(Plausibility.CheckBool(1));
        Assert.Equal("bool holds 2; expected 0 or 1.", Plausibility.CheckBool(2)?.Message);
    }

    [Fact]
    public void A_set_top_bit_is_only_a_suspect()
    {
        var flag = Plausibility.CheckUnsigned(0x8000, 2);

        Assert.Equal(FlagSeverity.Suspect, flag?.Severity);
        Assert.Contains("-32768", flag?.Message);
        Assert.Equal(FlagSeverity.Suspect, Plausibility.CheckUnsigned(0x80000000, 4)?.Severity);
        Assert.Null(Plausibility.CheckUnsigned(0x7FFFFFFF, 4));
    }

    [Fact]
    public void Bytes_are_not_checked_for_the_top_bit() => Assert.Null(Plausibility.CheckUnsigned(0xFF, 1));
}
