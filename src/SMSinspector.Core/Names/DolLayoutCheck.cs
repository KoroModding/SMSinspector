using SMSinspector.Core.Layouts;
using SMSinspector.Core.Symbols;

namespace SMSinspector.Core.Names;

/// <summary>
/// One original accessor whose code in main.dol and whose body in the decomp both name a
/// single member. <paramref name="LayoutOffset"/> is where the PAL layout puts that member,
/// counted from the start of the accessor's class like <paramref name="DolOffset"/>.
/// </summary>
public sealed record AccessorCheck(
    string ClassName,
    string Method,
    string Instruction,
    uint DolOffset,
    string Member,
    uint? LayoutOffset,
    string BodyLocation)
{
    public bool Agrees => LayoutOffset == DolOffset;

    public bool IsComparable => LayoutOffset is not null;
}

public sealed record LayoutCheckResult(IReadOnlyList<AccessorCheck> Checks, IReadOnlyList<PalContradiction> Contradictions, bool Ran)
{
    public static LayoutCheckResult NotRun { get; } = new([], [], false);

    public int Agreements => Checks.Count(c => c.Agrees);

    public IEnumerable<AccessorCheck> Disagreements => Checks.Where(c => c.IsComparable && !c.Agrees);

    /// <summary>One contradiction per class, the earliest, as the layout engine keeps them.</summary>
    public IEnumerable<PalContradiction> Withheld => Contradictions
        .GroupBy(c => c.ClassName, StringComparer.Ordinal)
        .Select(g => g.MinBy(c => c.FirstOffset)!)
        .OrderBy(c => c.ClassName, StringComparer.Ordinal);

    public string Summary() => Ran
        ? $"main.dol accessors against decomp bodies: {Checks.Count(c => c.IsComparable):N0} compared, {Agreements:N0} agree, {Disagreements.Count():N0} disagree."
        : "main.dol accessors against decomp bodies: not checked (main.dol not used).";
}

/// <summary>
/// Checks the PAL layouts against the game's code. An original accessor compiled to one
/// load or store at <c>d(r3)</c> touches the member at offset d. When the decomp's body of
/// the same accessor names a member the layout places elsewhere, the layout (or the
/// header behind it) is wrong for PAL, and the class's PAL offsets are withheld from the
/// first offset in disagreement.
/// </summary>
public static class DolLayoutCheck
{
    /// <summary>Runs the check and hands its contradictions to the engine, replacing earlier ones.</summary>
    public static LayoutCheckResult Apply(LayoutEngine engine, NameSources sources)
    {
        engine.SetPalContradictions([]);
        if (!sources.Executable.IsUsable)
        {
            return LayoutCheckResult.NotRun;
        }

        var result = Run(engine, sources);
        engine.SetPalContradictions(result.Contradictions);
        return result;
    }

    /// <summary>Compares without touching the engine.</summary>
    public static LayoutCheckResult Run(LayoutEngine engine, NameSources sources)
    {
        var image = sources.Executable.Image!;
        var bodies = sources.Sources.Bodies.ToLookup(b => (b.ClassName, b.Name));
        var checks = new List<AccessorCheck>();
        var contradictions = new List<PalContradiction>();

        foreach (var method in sources.Methods.All)
        {
            if (method.Address is not { } address || method.Mangled is null)
            {
                continue;
            }

            var candidates = bodies[(method.ClassName, method.Name)].ToList();
            var body = candidates.FirstOrDefault(b => b.IsConst == method.IsConst) ?? (candidates.Count == 1 ? candidates[0] : null);
            if (body is null || BodyAnalyzer.SingleMember(body) is not { } member)
            {
                continue;
            }

            var code = AccessorDecoder.Decode(address, method.Size, a => image.TryReadWord(a, out var word) ? word : null);
            if (!code.IsAccessor || engine.GetLayout(method.ClassName, VersionMask.Pal) is not { } layout)
            {
                continue;
            }

            var flat = layout.Flatten();
            var field = flat.FirstOrDefault(f => f.Field.Name == member && f.Field.Member?.Kind == MemberKind.Data && ReferenceEquals(f.Owner, layout))
                ?? flat.FirstOrDefault(f => f.Field.Name == member && f.Field.Member?.Kind == MemberKind.Data);
            if (field is null)
            {
                continue;
            }

            var location = $"{body.File}:{body.Line}";
            var check = new AccessorCheck(method.ClassName, method.Mangled, code.Instruction, code.Offset, member, field.AbsoluteOffset, location);
            checks.Add(check);
            if (!check.IsComparable || check.Agrees || field.Field.Offset is not { } ownOffset)
            {
                continue;
            }

            // Offsets relative to the class that declares the member, which is where the layout is wrong.
            var origin = field.AbsoluteOffset!.Value - ownOffset;
            var first = Math.Min(ownOffset, code.Offset >= origin ? code.Offset - origin : ownOffset);
            var demangled = CodeWarriorDemangler.Demangle(method.Mangled);
            contradictions.Add(new PalContradiction(
                field.Owner.Name,
                first,
                $"main.dol has {demangled} as `{code.Instruction}`, but the decomp body ({location}) uses {member}, which the layout puts at 0x{field.AbsoluteOffset:X}."));
        }

        return new LayoutCheckResult(checks, contradictions, true);
    }
}
