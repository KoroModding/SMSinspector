using SMSinspector.Core.Memory;

namespace SMSinspector.Core.Symbols;

public enum ProbeOutcome
{
    /// <summary>The global is not in the symbol table, or not uniquely.</summary>
    SymbolNotFound,

    ReadFailed,

    /// <summary>The global holds 0: the object does not exist yet.</summary>
    NullPointer,

    /// <summary>The global holds something that is not a MEM1 address.</summary>
    InvalidPointer,

    /// <summary>The object's first word does not point into any known vtable.</summary>
    UnknownVtable,

    Identified,
}

public sealed record ProbeResult(ProbeOutcome Outcome, Symbol? Global, uint Pointer, ObjectIdentity? Identity, string Message);

/// <summary>
/// Follows a global pointer variable (such as <c>gpMarioAddress</c>) to the object it
/// points to and identifies that object's class from its vtable pointer.
/// </summary>
public static class GlobalObjectProbe
{
    public static ProbeResult Probe(IGameMemory memory, SymbolTable symbols, VtableIndex vtables, string globalName)
    {
        if (!symbols.TryGetUnique(globalName, out var global))
        {
            return new ProbeResult(ProbeOutcome.SymbolNotFound, null, 0, null, $"{globalName} is not in symbols.txt (or not uniquely).");
        }

        if (!memory.TryReadU32(global.Address, out var pointer))
        {
            return new ProbeResult(ProbeOutcome.ReadFailed, global, 0, null, $"Could not read {globalName} at 0x{global.Address:X8}.");
        }

        if (pointer == 0)
        {
            return new ProbeResult(ProbeOutcome.NullPointer, global, 0, null,
                $"{globalName} is null: the object does not exist yet (title screen or loading).");
        }

        if (!GameCube.IsMem1Address(pointer))
        {
            return new ProbeResult(ProbeOutcome.InvalidPointer, global, pointer, null,
                $"{globalName} holds 0x{pointer:X8}, which is not a MEM1 address.");
        }

        var identity = ObjectIdentifier.Identify(memory, vtables, pointer);
        if (identity is null)
        {
            return new ProbeResult(ProbeOutcome.ReadFailed, global, pointer, null, $"Could not read the object at 0x{pointer:X8}.");
        }

        if (identity.Vtable is null)
        {
            return new ProbeResult(ProbeOutcome.UnknownVtable, global, pointer, identity,
                $"The first word of the object, 0x{identity.Vptr:X8} ({symbols.Label(identity.Vptr)}), does not point into a vtable.");
        }

        return new ProbeResult(ProbeOutcome.Identified, global, pointer, identity,
            $"{identity.ClassName}: the vptr points 0x{identity.OffsetIntoVtable:X} bytes past {identity.Vtable.Symbol.Name}.");
    }
}
