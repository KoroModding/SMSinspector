namespace SMSinspector.Core.Names;

public enum AccessorShape
{
    /// <summary>One load into the return register from <c>d(r3)</c>, then <c>blr</c>.</summary>
    Load,

    /// <summary>One store of the first argument to <c>d(r3)</c>, then <c>blr</c>.</summary>
    Store,

    /// <summary><c>addi r3, r3, d</c> then <c>blr</c>: returns the address of a member. Counted, not interpreted.</summary>
    AddressOf,

    /// <summary>Two instructions ending in <c>blr</c> that match none of the above.</summary>
    OtherTwoInstructions,

    /// <summary>A single <c>blr</c>: an empty function.</summary>
    Empty,

    /// <summary>Anything longer than two instructions.</summary>
    Longer,

    /// <summary>The size is unknown or the code could not be read.</summary>
    Unreadable,
}

/// <summary>What a short function does, as far as the strict accessor shapes go.</summary>
/// <param name="Width">Bytes moved by the load or store.</param>
/// <param name="Instruction">The decoded instruction, for example "lwz r3, 0x78(r3)".</param>
public sealed record AccessorCode(AccessorShape Shape, uint Offset = 0, uint Width = 0, bool IsFloat = false, string Instruction = "")
{
    public bool IsAccessor => Shape is AccessorShape.Load or AccessorShape.Store;
}

/// <summary>
/// Recognises the two-instruction accessors the compiler emits for trivial getters and
/// setters. PowerPC D-form instructions put the primary opcode in the top 6 bits, then the
/// target or source register (5 bits), the base register (5 bits) and a signed 16-bit
/// displacement. <c>lwz r3, 0x78(r3)</c> followed by <c>blr</c> returns the word at
/// <c>this + 0x78</c>, because <c>r3</c> holds <c>this</c> on entry and the return value
/// on exit.
/// </summary>
public static class AccessorDecoder
{
    public const uint Blr = 0x4E800020;

    private const int ThisRegister = 3;
    private const int IntReturnRegister = 3;
    private const int FloatReturnRegister = 1;
    private const int FirstIntArgument = 4;
    private const int FirstFloatArgument = 1;
    private const uint AddiOpcode = 14;

    private sealed record Form(string Mnemonic, bool IsStore, uint Width, bool IsFloat);

    private static readonly Dictionary<uint, Form> Forms = new()
    {
        [34] = new("lbz", false, 1, false),
        [40] = new("lhz", false, 2, false),
        [42] = new("lha", false, 2, false),
        [32] = new("lwz", false, 4, false),
        [48] = new("lfs", false, 4, true),
        [50] = new("lfd", false, 8, true),
        [38] = new("stb", true, 1, false),
        [44] = new("sth", true, 2, false),
        [36] = new("stw", true, 4, false),
        [52] = new("stfs", true, 4, true),
        [54] = new("stfd", true, 8, true),
    };

    /// <summary>Classifies a function from its size and a way to read its code.</summary>
    public static AccessorCode Decode(uint address, uint? size, Func<uint, uint?> readWord)
    {
        switch (size)
        {
            case null or 0:
                return new AccessorCode(AccessorShape.Unreadable);
            case > 8:
                return new AccessorCode(AccessorShape.Longer);
            case 4:
                return readWord(address) == Blr ? new AccessorCode(AccessorShape.Empty) : new AccessorCode(AccessorShape.Unreadable);
            case not 8:
                return new AccessorCode(AccessorShape.Unreadable);
        }

        if (readWord(address) is not { } first || readWord(address + 4) is not { } second)
        {
            return new AccessorCode(AccessorShape.Unreadable);
        }

        return Decode(first, second);
    }

    /// <summary>Classifies a two-instruction function.</summary>
    public static AccessorCode Decode(uint first, uint second)
    {
        if (second != Blr)
        {
            return new AccessorCode(AccessorShape.OtherTwoInstructions);
        }

        var opcode = first >> 26;
        var target = (int)(first >> 21) & 0x1F;
        var baseRegister = (int)(first >> 16) & 0x1F;
        var displacement = (short)(first & 0xFFFF);

        if (baseRegister != ThisRegister || displacement < 0)
        {
            return new AccessorCode(AccessorShape.OtherTwoInstructions);
        }

        var offset = (uint)displacement;
        if (opcode == AddiOpcode && target == ThisRegister)
        {
            return new AccessorCode(AccessorShape.AddressOf, offset, 0, false, $"addi r3, r3, 0x{offset:X}");
        }

        if (!Forms.TryGetValue(opcode, out var form))
        {
            return new AccessorCode(AccessorShape.OtherTwoInstructions);
        }

        // A getter loads into the return register; a setter stores its first argument.
        var expected = (form.IsStore, form.IsFloat) switch
        {
            (false, false) => IntReturnRegister,
            (false, true) => FloatReturnRegister,
            (true, false) => FirstIntArgument,
            (true, true) => FirstFloatArgument,
        };
        if (target != expected)
        {
            return new AccessorCode(AccessorShape.OtherTwoInstructions);
        }

        var register = form.IsFloat ? $"f{target}" : $"r{target}";
        return new AccessorCode(
            form.IsStore ? AccessorShape.Store : AccessorShape.Load,
            offset,
            form.Width,
            form.IsFloat,
            $"{form.Mnemonic} {register}, 0x{offset:X}(r3)");
    }
}
