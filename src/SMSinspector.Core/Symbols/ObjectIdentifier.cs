using SMSinspector.Core.Memory;

namespace SMSinspector.Core.Symbols;

/// <summary>What the vtable pointer of an object says about it.</summary>
/// <param name="Vptr">The 32-bit value read at the object's vtable slot.</param>
/// <param name="Vtable">The vtable it points into, if any.</param>
/// <param name="OffsetIntoVtable">How far past the vtable symbol the pointer lands.</param>
public sealed record ObjectIdentity(uint ObjectAddress, uint VptrOffset, uint Vptr, Vtable? Vtable, uint OffsetIntoVtable)
{
    public string? ClassName => Vtable?.ClassName;
}

public static class ObjectIdentifier
{
    /// <summary>
    /// Reads the vtable pointer at <paramref name="objectAddress"/> + <paramref name="vptrOffset"/>
    /// and resolves it. Returns null when the read fails.
    /// </summary>
    public static ObjectIdentity? Identify(IGameMemory memory, VtableIndex vtables, uint objectAddress, uint vptrOffset = 0)
    {
        if (!memory.TryReadU32(objectAddress + vptrOffset, out var vptr))
        {
            return null;
        }

        return vtables.TryResolve(vptr, out var vtable, out var offset)
            ? new ObjectIdentity(objectAddress, vptrOffset, vptr, vtable, offset)
            : new ObjectIdentity(objectAddress, vptrOffset, vptr, null, 0);
    }
}
