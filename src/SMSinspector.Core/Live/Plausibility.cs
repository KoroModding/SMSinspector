using System.Globalization;
using System.Text.RegularExpressions;
using SMSinspector.Core.Memory;

namespace SMSinspector.Core.Live;

public enum FlagSeverity
{
    /// <summary>The value does not fit its type: shown in orange.</summary>
    Warning,

    /// <summary>A weaker hint that legitimate values also trigger, such as flag fields.</summary>
    Suspect,
}

/// <summary>Why a value looks wrong: the rule that fired and, in the message, its threshold.</summary>
public sealed record PlausibilityFlag(string Rule, FlagSeverity Severity, string Message);

/// <summary>The thresholds of the plausibility check (plan 5.5). The defaults are the plan's starting rules.</summary>
public sealed record PlausibilitySettings
{
    public static PlausibilitySettings Default { get; } = new();

    /// <summary>First address a non-null pointer may hold.</summary>
    public uint PointerStart { get; init; } = GameCube.Mem1Base;

    /// <summary>One past the last address a non-null pointer may hold.</summary>
    public uint PointerEnd { get; init; } = GameCube.Mem1End;

    /// <summary>An f32 larger than this in absolute value is flagged.</summary>
    public float FloatLimit { get; init; } = 1e7f;
}

/// <summary>
/// The rules that turn a value orange. A flag means "look here", not "this is wrong": one
/// wrong offset makes a whole run of fields fail them, which is what they are for.
/// </summary>
public static partial class Plausibility
{
    // The smallest normal f32; anything nonzero below it is denormal.
    private const float MinNormal = 1.17549435E-38f;

    public static PlausibilityFlag? CheckPointer(uint value, PlausibilitySettings settings)
    {
        if (value == 0 || (value >= settings.PointerStart && value < settings.PointerEnd))
        {
            return null;
        }

        var message = $"Non-null pointer outside 0x{settings.PointerStart:X8}..0x{settings.PointerEnd:X8}.";
        if (value < GameCube.Mem1Size)
        {
            // The graphics hardware takes physical addresses, which are MEM1 addresses minus 0x80000000.
            message += $" Value below 0x{GameCube.Mem1Size:X8}: probably a physical address.";
        }

        return new("pointer range", FlagSeverity.Warning, message);
    }

    public static PlausibilityFlag? CheckFloat(float value, PlausibilitySettings settings)
    {
        if (float.IsNaN(value))
        {
            return new("f32 NaN", FlagSeverity.Warning, "f32 is NaN.");
        }

        if (float.IsInfinity(value))
        {
            return new("f32 infinite", FlagSeverity.Warning, "f32 is infinite.");
        }

        if (float.IsSubnormal(value))
        {
            return new("f32 denormal", FlagSeverity.Warning,
                $"f32 is denormal: not zero, and smaller than {Format(MinNormal)} in absolute value.");
        }

        if (Math.Abs(value) > settings.FloatLimit)
        {
            return new("f32 too large", FlagSeverity.Warning, $"f32 is larger than {Format(settings.FloatLimit)} in absolute value.");
        }

        return null;
    }

    public static PlausibilityFlag? CheckBool(ulong value) =>
        value <= 1 ? null : new("bool range", FlagSeverity.Warning, $"bool holds {value}; expected 0 or 1.");

    /// <summary>
    /// An unsigned 16- or 32-bit value with its top bit set would be negative as a signed
    /// one, which a counter or an index never is. Flag fields and hashes often hold such
    /// values, so this is only a suspect, and the caller applies it only to members whose
    /// name says they count or index something (<see cref="IsCounterName"/>).
    /// </summary>
    public static PlausibilityFlag? CheckUnsigned(ulong value, uint size)
    {
        if (size is not (2 or 4))
        {
            return null;
        }

        var bits = (int)size * 8;
        if ((value >> (bits - 1) & 1) == 0)
        {
            return null;
        }

        var signed = (long)value - (1L << bits);
        return new("unsigned top bit", FlagSeverity.Suspect,
            $"Top bit set: {signed} if read as signed. Flag fields often look like this; it is a hint, not an error.");
    }

    /// <summary>
    /// True for a member named like a counter, an index or a size: <c>mCount</c>,
    /// <c>mObjNum</c>, <c>mSlotIndex</c>, <c>len</c>. Unnamed members (<c>unkXX</c>) never match.
    /// </summary>
    public static bool IsCounterName(string name) => CounterName().IsMatch(name);

    [GeneratedRegex("Num|Count|Cnt|Index|Idx|Size|Len|^(?:num|count|cnt|index|idx|size|len)")]
    private static partial Regex CounterName();

    private static string Format(float value) => value.ToString("R", CultureInfo.InvariantCulture);
}
