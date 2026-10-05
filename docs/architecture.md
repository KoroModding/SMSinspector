# Architecture

SMSinspector has two projects. `SMSinspector.Core` holds everything that does not draw pixels: memory access, and later symbols, layouts and object discovery. `SMSinspector.App` is the Avalonia front end. Tests cover Core only, against fakes, so they run anywhere without Dolphin or the game.

This page grows with each milestone. Right now it covers memory access and symbols.

## Memory access

### Where the game's RAM is

The GameCube has 24 MiB of main RAM, called MEM1. The game sees it at addresses `0x80000000` to `0x817FFFFF`; every pointer you find in the game's data lives in that range.

Dolphin keeps MEM1 in its own process as a shared memory section and maps it at an address that changes every launch. It maps it more than once (fastmem views), so several regions of Dolphin's address space show the same bytes. SMSinspector does not need Dolphin's cooperation to find it:

1. List Dolphin's memory regions with `VirtualQueryEx`.
2. Keep the committed, mapped regions of at least 24 MiB.
3. Read the first 32 bytes of each. The game's disc header is copied there at boot: a 6-character game ID at offset 0 (`GMSP01` for the PAL release), a revision byte at offset 7, and the magic word `0xC2339F3D` at offset `0x1C`. The first region that carries a valid header is MEM1.

From then on, game address `A` is at host address `base + (A - 0x80000000)`. All values are big-endian, since the GameCube's PowerPC CPU is.

The code for this is `Mem1Locator` and `DolphinGameMemory`.

### Layers

| Type | Role |
|---|---|
| `IHostProcess` | The emulator process: its regions and raw reads at host addresses. |
| `IHostProcessSource` | Finds Dolphin processes and opens them. |
| `WindowsHostProcess`, `WindowsHostProcessSource` | The Windows implementation of the two above. |
| `Mem1Locator` | Finds MEM1 in a process and checks that it is still there. |
| `IGameMemory` | Reads at game addresses. Everything above this layer uses only this. |
| `GameMemoryExtensions` | Big-endian typed reads (`TryReadU32`, `ReadF32`, ...) on `IGameMemory`. |
| `DolphinConnection` | Keeps a connection alive across Dolphin restarts and game changes. |

A Linux backend would add another pair of `IHostProcess` and `IHostProcessSource` implementations. Nothing above them depends on Windows.

### Read-only by construction

The Windows backend opens Dolphin with `PROCESS_QUERY_INFORMATION | PROCESS_VM_READ` and nothing else, and imports no function that writes to another process. CI scans the tree for write APIs and fails the build if one appears. With those rights, Windows itself refuses any write, so a bug in SMSinspector cannot corrupt the game.

If Dolphin runs as administrator, Windows refuses even read access to a normal process. SMSinspector then says so and suggests running it as administrator too.

### Supported games

`GameIdentity` maps the game ID to what SMSinspector can do:

| Game ID | Result |
|---|---|
| `GMSP01` | Supported. |
| `GMSJ01` | Covered by the decomp, not supported yet: nobody has tested it on a JP copy. |
| other `GMS...` IDs | Not covered by the decomp. |
| anything else | Not Super Mario Sunshine. |

### Following Dolphin over time

`DolphinConnection.Poll()` runs about once a second. While connected, it only re-reads the 32-byte header at the known location. If the header is gone or different (game stopped, another game booted) or Dolphin exited, it drops the connection and scans again on the same call. While disconnected, each poll rescans; with no game running this costs a few milliseconds.

The class is not thread-safe. The app polls from one task at a time.

### Reading without allocating

`IGameMemory.TryRead` fills a caller-provided `Span<byte>`. The typed reads use `stackalloc` buffers and `BinaryPrimitives`, so reading a field in the refresh loop allocates nothing. The Try forms return false on failure; the plain forms throw `GameMemoryReadException`, which is easier for one-off reads.

## Symbols

### Where they come from

The game's executable carries no symbol names. The doldecomp/sms project keeps one in `config/GMSP01/symbols.txt`, one line per symbol:

```
update__9TFooActorFv = .text:0x80010000; // type:function size:0x40 scope:global align:4
```

SMSinspector reads that file from the user's clone at startup (`DecompRepository`, `SymbolFile`). It never ships a copy. The example above, like every name in the tests, is invented.

`SymbolTable` indexes the symbols by name and by address. Names are not unique: the compiler emits many local constants called `@123`, so a lookup by name returns every match, and `TryGetUnique` only succeeds when there is one. A lookup by address uses the sizes from the file, so an address that falls in a gap between two symbols stays unknown instead of being blamed on the previous one.

### Demangling

The game was built with Metrowerks CodeWarrior, whose name mangling differs from the GCC and Clang one, so `c++filt` cannot read it. `CodeWarriorDemangler` handles what the game's symbols use:

| Mangled | Demangled |
|---|---|
| `update__9TFooActorFv` | `TFooActor::update()` |
| `draw__Q23Gfx7TCanvasCFi` | `Gfx::TCanvas::draw(int) const` (Q2: two nested scopes) |
| `__vt__20TStack<PC9TFooActor>` | vtable of `TStack<const TFooActor*>` |
| `@16@__dt__9TFooActorFv` | `TFooActor::~TFooActor()`, through a thunk that adjusts `this` |

The number before each name is its length. Template arguments sit inside that length and are mangled types themselves, so the demangler parses them recursively. When a name does not parse, it is shown as it is rather than guessed.

### Vtables and class identification

A C++ object with virtual functions holds a pointer to its class's table of virtual functions, the vtable. For the classes SMSinspector cares about, everything derived from `JDrama::TNameRef`, that pointer is the object's first word. Classes outside that hierarchy may keep it elsewhere, and classes with several polymorphic bases have more than one. The decomp names each vtable `__vt__<class>`. `VtableIndex` collects them, and `ObjectIdentifier` reads the first word of an object and finds which vtable it points into. That gives the object's class without any other information.

The pointer does not have to land exactly on the vtable symbol: the compiler can put a small header before the function pointers. `VtableIndex.TryResolve` returns the offset into the vtable, and the diagnostic window shows it for Mario. That measured offset decides how object discovery will match vtable pointers later.

`GlobalObjectProbe` chains these steps for a global pointer variable: look up `gpMarioAddress` in the symbols, read it, follow it to the object, identify the object.

### Linker map

The original linker map lists functions the linker removed because nothing called them (marked UNUSED). The decomp does not ship it; users who have it put it with their game files. `DecompRepository.LocateLinkerMap` hardcodes no file name: it reads the `map:` line of `config/GMSP01/config.yml`, and if that file does not exist it looks for a single `*.MAP` in `orig/GMSP01/files/`. The diagnostic window says which file it used, or why it used none.
