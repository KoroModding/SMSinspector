using SMSinspector.Core.Layouts;
using SMSinspector.Core.Symbols;

namespace SMSinspector.Core.Names;

/// <summary>One access of a PAL-only method to its own object, decoded from main.dol.</summary>
public sealed record ThisAccess(uint Offset, uint Width, bool IsStore, string Mnemonic);

/// <summary>
/// Looks for PAL layouts the headers probably get wrong without any version block. A method
/// that exists only in the PAL build (in the PAL symbols, not in the JP ones) was added for
/// PAL, and the data it touches often was too. When such a method reads or writes its own
/// object with a width that does not fit the PAL layout at that offset (16 bits into a
/// pointer, say), the class is listed as a suspect, with the evidence. This is a hint, not a
/// proof: the layout is left as it is.
/// </summary>
public static class PalOnlyCodeCheck
{
    private const int ThisRegister = 3;

    private static readonly Dictionary<uint, (string Mnemonic, bool IsStore, uint Width)> Forms = new()
    {
        [34] = ("lbz", false, 1), [40] = ("lhz", false, 2), [42] = ("lha", false, 2), [32] = ("lwz", false, 4),
        [48] = ("lfs", false, 4), [50] = ("lfd", false, 8),
        [38] = ("stb", true, 1), [44] = ("sth", true, 2), [36] = ("stw", true, 4), [52] = ("stfs", true, 4), [54] = ("stfd", true, 8),
    };

    /// <summary>Lists suspects; empty when main.dol or the other version's symbols are missing.</summary>
    public static IReadOnlyList<PalSuspect> Run(LayoutEngine engine, NameSources sources)
    {
        if (!sources.Executable.IsUsable || sources.OtherVersionFunctions is not { } otherVersion)
        {
            return [];
        }

        var image = sources.Executable.Image!;
        var findings = new Dictionary<string, List<(string Method, ThisAccess Access, FieldLayout Field)>>(StringComparer.Ordinal);
        foreach (var method in sources.Methods.All)
        {
            if (method.Mangled is not { } mangled || method.Address is not { } address || method.Size is not { } size
                || !method.Origin.HasFlag(NameOrigin.Symbols) || otherVersion.Contains(mangled)
                || engine.GetLayout(method.ClassName, VersionMask.Pal) is not { } layout)
            {
                continue;
            }

            var flat = layout.Flatten();
            foreach (var access in Accesses(address, size, a => image.TryReadWord(a, out var word) ? word : null))
            {
                if (Misfit(flat, access) is { } field)
                {
                    if (!findings.TryGetValue(layout.Name, out var list))
                    {
                        findings[layout.Name] = list = [];
                    }

                    list.Add((CodeWarriorDemangler.Demangle(mangled), access, field));
                }
            }
        }

        return findings.Select(f => Describe(f.Key, f.Value)).OrderBy(s => s.ClassName, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Accesses relative to <c>this</c>, reading the code in order. <c>this</c> starts in r3 and
    /// may be copied with <c>mr</c> or <c>addi</c>; volatile registers are lost at a call. Branches are
    /// not followed, so this is an approximation that errs towards missing accesses.
    /// </summary>
    public static List<ThisAccess> Accesses(uint address, uint size, Func<uint, uint?> readWord)
    {
        var result = new List<ThisAccess>();

        // Registers known to hold this + offset.
        var aliases = new Dictionary<int, uint> { [ThisRegister] = 0 };
        for (var at = address; at < address + size; at += 4)
        {
            if (readWord(at) is not { } word)
            {
                break;
            }

            var opcode = word >> 26;
            var rt = (int)(word >> 21) & 0x1F;
            var ra = (int)(word >> 16) & 0x1F;
            var rb = (int)(word >> 11) & 0x1F;
            var displacement = (short)(word & 0xFFFF);

            // bl: r0 and r3..r12 do not survive a call.
            if (opcode == 18 && (word & 1) == 1)
            {
                foreach (var volatileRegister in aliases.Keys.Where(r => r is 0 or >= 3 and <= 12).ToList())
                {
                    aliases.Remove(volatileRegister);
                }

                continue;
            }

            // mr rA, rS is "or rA, rS, rS" (primary 31, extended 444); here rt holds rS.
            if (opcode == 31 && ((word >> 1) & 0x3FF) == 444)
            {
                if (rt == rb && aliases.TryGetValue(rt, out var copied))
                {
                    aliases[ra] = copied;
                }
                else
                {
                    aliases.Remove(ra);
                }

                continue;
            }

            // addi rD, rA, d: the compiler's usual copy of this (d = 0), or the address of a member.
            if (opcode == 14)
            {
                if (ra != 0 && aliases.TryGetValue(ra, out var baseOffset) && baseOffset + displacement >= 0)
                {
                    aliases[rt] = (uint)(baseOffset + displacement);
                }
                else
                {
                    aliases.Remove(rt);
                }

                continue;
            }

            if (Forms.TryGetValue(opcode, out var form))
            {
                if (aliases.TryGetValue(ra, out var offset) && offset + displacement >= 0)
                {
                    result.Add(new ThisAccess((uint)(offset + displacement), form.Width, form.IsStore, form.Mnemonic));
                }

                if (!form.IsStore && form.Mnemonic is not ("lfs" or "lfd"))
                {
                    aliases.Remove(rt);
                }

                continue;
            }

            // Other D-form integer instructions write a register (ori, rlwinm...); X-form ones
            // are left alone, which can keep a stale alias but never invents one from r3.
            if (opcode is 7 or 8 or 12 or 13 or 15 or 20 or 21 or >= 23 and <= 29)
            {
                aliases.Remove(opcode is >= 24 and <= 29 or 20 or 21 or 23 ? ra : rt);
            }
        }

        return result;
    }

    /// <summary>The field an access does not fit, or null when it fits.</summary>
    private static FieldLayout? Misfit(List<FlatField> flat, ThisAccess access)
    {
        var holder = flat.LastOrDefault(f => f.AbsoluteOffset is { } start && f.Field.Size is { } size
            && f.Field.Member is { Kind: MemberKind.Data } && access.Offset >= start && access.Offset < start + size);
        if (holder is null)
        {
            return null;
        }

        var member = holder.Field.Member!;
        var start = holder.AbsoluteOffset!.Value;
        var size = holder.Field.Size!.Value;
        var element = member.Type.Dims.Count > 0 && member.Type.IsPointerLike ? 4u : size;

        // A pointer, or an element of an array of pointers, is only ever moved as one word.
        if (member.Type.IsPointerLike && (access.Width != 4 || (access.Offset - start) % element != 0 || access.Mnemonic.StartsWith("lf") || access.Mnemonic.StartsWith("stf")))
        {
            return holder.Field;
        }

        // An access that runs past the end of the field spills into the next one.
        return access.Offset + access.Width > start + size ? holder.Field : null;
    }

    private static PalSuspect Describe(string className, List<(string Method, ThisAccess Access, FieldLayout Field)> findings)
    {
        var first = findings.Min(f => f.Access.Offset);
        var last = findings.Max(f => f.Access.Offset + f.Access.Width - 1);
        var methods = string.Join(", ", findings.Select(f => f.Method).Distinct());
        var kinds = string.Join("; ", findings.GroupBy(f => f.Access.IsStore).OrderByDescending(g => g.Key).Select(g =>
            $"{(g.Key ? "writes" : "reads")} {string.Join(" and ", g.Select(f => f.Access.Width * 8).Distinct().Order())}-bit values"));
        var fields = string.Join(", ", findings.Select(f => $"{f.Field.Name} ({f.Field.TypeName})").Distinct());
        return new PalSuspect(className, first, last,
            $"PAL-only {methods} {kinds} at 0x{first:X}..0x{last:X}, over {fields} as the header declares them.");
    }
}
