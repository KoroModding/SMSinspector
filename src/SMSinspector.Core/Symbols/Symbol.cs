namespace SMSinspector.Core.Symbols;

/// <summary>One line of a decomp <c>symbols.txt</c>.</summary>
/// <param name="Name">Mangled name, as written in the file.</param>
/// <param name="Section">Section name, for example ".data" or ".sbss".</param>
/// <param name="Size">Size in bytes, when the file gives one.</param>
/// <param name="Type">"function", "object" or "label", when given.</param>
/// <param name="Scope">"global", "local" or "weak", when given.</param>
public sealed record Symbol(string Name, string Section, uint Address, uint? Size, string? Type, string? Scope)
{
    public bool IsLocal => Scope == "local";

    /// <summary>True when <paramref name="address"/> falls inside this symbol. Needs a known size.</summary>
    public bool Contains(uint address) => Size is { } size && address >= Address && address - Address < size;
}
