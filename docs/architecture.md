# Architecture

SMSinspector has two projects. `SMSinspector.Core` holds everything that does not draw pixels: memory access, symbols, layouts, the name extractor, and later object discovery. `SMSinspector.App` is the Avalonia front end. Tests cover Core only, against fakes, so they run anywhere without Dolphin or the game.

This page grows with each milestone. Right now it covers memory access, symbols, class layouts, the name extractor, where names come from, and object discovery.

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

## Class layouts

### What is read

Every header under `include/` and `libs/*/include/` in the user's clone, including the C++ library headers that have no extension (`cstdint`). Parsing and laying out the whole decomp takes well under a second, so nothing is cached.

`HeaderParser` is not a C++ parser. It knows namespaces, class and struct definitions with their bases, data members with their offset comments, enums, typedefs, `using` aliases, templates with default arguments and explicit specializations, and plain integer constants. Function bodies, initializers and anything else are skipped by matching brackets. A statement in a class body that looks like a data member but cannot be read is listed in the report rather than guessed.

### Versions

The headers describe two builds at once. Code inside `#ifdef VERSION_GMSP01` exists only in PAL, its `#else` branch only in JP. `HeaderLexer` keeps both branches and tags every token with the versions it belongs to; other preprocessor conditions are evaluated once, the way the game's compiler saw them. `VERSION_SELECT(GMSJ01(a), GMSP01(b))` is expanded per version wherever a constant is evaluated, for example in an array size.

The offset comments follow one convention, which the engine relies on: a comment outside any version block, or inside a JP-only block, is a JP offset. A comment inside a PAL-only block is a PAL offset.

### JP: checking the comments

`LayoutEngine` walks each class and computes every member's offset from what precedes it: base classes, hidden pointers, the sizes and alignments of earlier members. Where a comment exists, it compares. The comment stays the displayed offset either way, since it is what the decomp says; the comparison measures how far the size model can be trusted, and every disagreement goes into the report.

The rules it applies are those of the game's compiler (Metrowerks CodeWarrior for PowerPC):

- Pointers, references and function pointers take 4 bytes. Enums take 4 bytes unless they name another type.
- A class that is the first in its hierarchy to have virtual functions gets its vtable pointer where its first virtual function is declared, after the data members declared before it. `JDrama::TNameRef` declares its virtual functions first, so its vtable pointer is at offset 0; `TSpineBase` declares them last, so it is after the members. A `/* 0x24 */ // vt` comment in a header confirms the position.
- A class with a virtual base keeps a pointer to it; the virtual base itself is placed once, at the end of the complete object. A class used as a base takes its size without virtual bases.
- Consecutive bit-fields of the same type share a storage unit of that type, filled from the most significant bit.
- `__attribute__((aligned(N)))` raises a member's alignment. An empty base takes no room. A trailing `T data[]` takes none either.

### PAL: deriving the offsets

A class whose PAL layout cannot differ (no version block in it, and no base or member type whose size differs between versions) keeps its JP offsets.

Otherwise the engine walks it again with the PAL members. Comments inside PAL-only blocks are checked like JP comments are. A member present in both versions moves by the shift accumulated before it, which is exact as long as that shift keeps the member's alignment. When it does not (a 2-byte insertion before a 4-byte field, say), the offset is recomputed from the preceding sizes, but only if the JP walk reproduced that member's comment, which proves the sizes involved. If neither holds, the engine stops: the class says "PAL offsets unverified after 0xNN" and later offsets are left blank instead of guessed.

### Report

`LayoutReport` covers every class: how many JP comments the computation reproduces, the classes whose PAL layout differs and how each member moves, and every disagreement found (gaps the header does not account for, overlaps, comments out of order, types that could not be sized). A disagreement can be a wrong comment, a wrong type in the header, or a rule this tool gets wrong; the report does not decide which. Saved from the app, it goes to `%APPDATA%\SMSinspector\reports\`, never into the repository.

## Name extractor

### Original names

The game was compiled from code whose member names are lost, but method names survived in the symbols: `symbols.txt` has about ten thousand member functions. The linker map, when the user has it, adds the methods the linker removed. `OriginalMethods` collects both, plus methods the decomp marks with a `// UNUSED` comment (the authors copy those from the map). Only these names count as original. The decomp's headers also define accessors such as `getUnk1C()`; their authors invented them, so the extractor ignores them.

A method name alone does not say which member it touches. The extractor ties it to a member in three ways, and a fourth hint comes from sibling classes.

### Accessors in the executable

`GameExecutable` finds `main.dol` from `object_base` and `object` in `config/GMSP01/config.yml`, the same keys the decomp's build reads. It hashes the file and compares it with the `.dol` line of `config/GMSP01/build.sha1`. A matching build is byte-identical to the original, so that line is the original's hash; any other file is refused. `DolImage` maps game addresses to file offsets through the 18 section entries in the DOL header.

`AccessorDecoder` reads the code of each original method at its symbol address and accepts one shape only, two instructions:

```
lwz   r3, 0x78(r3)    load the word at this + 0x78 into the return register
blr                   return
```

PowerPC passes `this` in `r3` and returns integers in `r3` and floats in `f1`. The decoder accepts the loads `lbz lhz lha lwz lfs lfd` into the return register and the stores `stb sth stw stfs stfd` of the first argument (`r4` or `f1`), always with `r3` as the base. The 16-bit displacement is the member offset. Anything else is classified and counted, never interpreted; that includes `addi r3, r3, d`, which returns a member's address, and two-instruction bodies of other shapes.

The offset is matched against the flattened PAL layout of the method's class (bases first, as in the layout view). A link is made only when a member called `unkXX` starts exactly there. The instruction width is compared with the member's size, and a difference goes into the candidate's note.

### Bodies in the decomp

`SourceScanner` reads every header and source file of the clone (`include/`, `src/`, and the same folders under `libs/`) with the header lexer, keeping the tokens of one game version. It tracks namespaces and class bodies by matching braces, and records each member function definition with its class, parameters, constness and body tokens, both inline in a class and as `TClass::name(...) { ... }` outside it. It also records which declarations carry a `// UNUSED` comment, on the line before or at the end of the line.

`BodyAnalyzer` keeps bodies of original methods only. A body that is exactly `return unkXX;` or `unkXX = <parameter>;` (with or without `this->`) gives a "matching" link. A body of at most two statements, without a nested block, that uses `unkXX` some other way gives an "indirect" link. A name read after another object's `.` or `->` belongs to that object and is skipped. Constructors and destructors are skipped here and in `main.dol`: they set up many members and name none of them.

### Sibling offsets

For each `unkXX` member a class declares, the extractor looks at the other classes with the same first base. If one of them declares a named member at the same offset with the same size, that name is a hint. Subclasses of a common base often put unrelated members at the same offset, so this level ranks last.

### main.dol against the layouts

`DolLayoutCheck` reuses both sources to check the layouts themselves. For each original accessor that decodes to the strict shape and whose decomp body names a single member (`BodyAnalyzer.SingleMember`, any member name), it compares the decoded offset with where the PAL layout of the accessor's class puts that member. Agreement confirms the layout at that point.

A disagreement becomes a `PalContradiction` for the class that declares the member, starting at the smaller of the two offsets. `LayoutEngine.SetPalContradictions` stores them and drops the cached PAL layouts concerned. The next time one is computed, its fields from that offset on lose their offset, its size becomes unknown, and `PalUnverifiedAfter` and `PalUnverifiedReason` say from where and why, as for a class the engine cannot place on its own. JP layouts are not touched. Several contradictions in one class keep the earliest.

The app runs the check right after loading the layouts, and the extractor runs it again before collecting candidates, so no candidate lands on a withheld offset. Withheld `unkXX` members are counted in the report instead.

### PAL suspects

`PalOnlyCodeCheck` looks for PAL layouts that are probably wrong although no version block says so. It takes the methods that exist only in the PAL build (in `config/GMSP01/symbols.txt` and not in `config/GMSJ01/symbols.txt`), reads their code in `main.dol`, and follows `this`: it starts in `r3`, the compiler copies it with `addi rX, r3, 0` or `mr`, and the volatile registers are lost at each call. Every load or store relative to `this` is compared with the PAL layout. A pointer read or written with anything but one aligned 32-bit access, or an access that runs past the end of its member, makes the class a suspect. The engine marks the layout (`PalSuspect`), the layout view prints the range and the reason, and the layout report lists the suspects. Offsets are kept: this is a hint, weaker than a contradiction.

On the current decomp one class comes out: `TCardLoad`, whose PAL-only `setupTitleScreen` writes 16-bit and 8-bit values at `0x29E..0x2D1` over members the header declares as pointers.

### Report

`NameExtractor` collects the candidates per member and orders them by level. `ExtractionReport` writes them as text and JSON with the source of every candidate (mangled name and size, decoded instruction or body with its file and line) and a section that counts what was read but not interpreted. The app saves it to `%APPDATA%\SMSinspector\reports\`. Like the layout report, it is never committed.

## Object discovery

### Vtable scan

Every object of a class with virtual functions holds a pointer to its class's vtable, and the decomp names each vtable `__vt__<class>`. `VtableScanner` reads MEM1 in 256 KiB chunks and checks every aligned word against the set of vtable addresses. A match is, almost certainly, an object's vtable pointer. The check measured in M2 holds: the pointer equals the symbol's address, with no header to skip. A full scan takes about 50 ms.

The vtable pointer is not always the first word of an object. A class that declares its virtual functions after its data members gets its pointer after them, and so do its subclasses: `TSpineBase<TLiveActor>` keeps it at `+0x24`. The scanner subtracts the vptr offset of the class's PAL layout to find where the object starts. Template classes are looked up through the demangled name (`TParamRT<unsigned char>`), which `LoadedLayouts.Find` parses and canonicalises, so it meets the header's spelling (`TParamRT<u8>`). A class without a layout keeps the vptr at `+0x0` and is flagged.

Two kinds of words are counted but not used: pointers into the middle of a vtable, which are the secondary vptrs of classes with several polymorphic bases, and chunks that could not be read. An object inside a known symbol, such as a static nerve instance, is marked static with the symbol's name; everything else is on the heap.

Each class name carries its `__vt__` symbol as provenance (kind "symbol": the file, the mangled name and the address).

The scan cannot tell a live object from a freed one: freed heap memory keeps its old vtable pointers until something overwrites it. In a test in Delfino Plaza it found two `TMario` objects, one of them left over from an earlier scene. The scene graph walk, the next pass, separates what the game still uses from what it does not.

### Scene graph walk

`SceneGraphWalker` starts from two anchors: the symbol `instance__Q26JDrama11TNameRefGen` holds the name generator, and its `mRootNameRef` points at the root of the graph. From there it follows two kinds of edges, breadth first, so every object keeps the shortest path to it as its parent.

- **Lists.** A class with `JGadget::TList_pointer<T*>` among its bases holds children in a doubly linked list. The walk finds that base in the layout, then the `JGadget::TList` inside it: `mSize`, and the sentinel node `oEnd_`. It starts at `oEnd_.pNext_` and follows `pNext_` until it is back at the sentinel. The value sits right after each node, so its offset is the size of `TNode_`. A list whose length differs from `mSize`, or that leaves MEM1, stops there and is counted.
- **Member pointers.** A data member whose declared type is a pointer, or a fixed array of pointers, to a class derived from `JDrama::TNameRef` is followed. The type comes from the header (`LayoutEngine.GetMemberTypeLayout` resolves it from the declaring class, so nested types work), not from the member's name, which may still be `unkXX`. The edge records the member with its header line.

Before following any pointer, the walk checks it: inside MEM1, aligned on 4, a word at the declared type's vptr offset that is exactly a `__vt__` symbol's address, and a class with a layout that derives from `JDrama::TNameRef`. A pointer that fails is not followed; it is counted by reason, with a few samples that show the declared and the found class. When the class derives from `JDrama::TNameRef` but not from the declared type, the edge is followed and the node is marked as a type mismatch, with both types; the summary lists these apart. Objects already visited are skipped, so cycles end. A depth limit and a time budget stop runaway walks; the result then says what cut it and how many objects were reached. A member of type `T**` is followed only when its class also holds the length anchor `TObjManager::mObjNum` and declares no other such member: the array must lie in MEM1 and be aligned, its length must stay under 4096, or the whole array is rejected; each entry is then checked like any edge. Other `T**` members and array containers are counted and not followed.

`DiscoveryMerge` puts the scan next to the walk. Scanned objects of graph classes that the walk did not reach are listed as "not reached (stale, or held by an array not followed)"; scanned objects of other classes are outside the graph by nature. Instance names (`mName`) are Shift-JIS strings read from memory, with the address they were read at as provenance.

In Delfino Plaza (PAL) the walk reaches about 1,070 objects in under 30 ms. The live `TMario` is among them, under the strategy's groups; the freed one the scan also finds is not. About 27 edges are type mismatches: the strategy's groups are declared to hold `THitActor`, yet they also hold `TMap`, `TSky` and the cube managers, and a list declared for `TViewObj` holds a plain name list. Either the declared types are too narrow or the game casts; the walk follows them and says so.

## Field values

`ObjectFields` turns a PAL layout into the rows of a field grid, once per class. Bases come first, then the class's own members, in memory order. A member whose type is a class stored inline (a vector, a block of parameters) gets its members as child rows, and an array gets one child row per element; both are built only when asked for. Bytes no member covers, padding included, become gap rows of at most 16 bytes, shown raw. Members whose PAL offset is withheld come last, with the reason, and are never read.

To decode a member, the layout engine resolves its type down to what the bytes hold (`DataType`): an integer, float or bool of some size, a pointer, an enum, an inline class, an array, or raw bytes when the type does not resolve. It follows typedefs, template parameters and enums. A plain `char` is signed, since the game is built with `-char signed`.

One object costs one memory read: `ReadSize` bytes from its address, which is the class size when it is known. `FieldDecoder` then decodes every row from that buffer, big-endian.

### Plausibility check

Every decoded value goes through the rules in `Plausibility`. A value that fails one carries a flag naming the rule and its threshold, for the tooltip.

| Rule | Fires on | Severity |
|---|---|---|
| pointer range | a non-null pointer outside `0x80000000..0x81800000`; below `0x01800000` the message adds that it is probably a physical address, the kind the graphics hardware takes | warning |
| f32 NaN, infinite, denormal, too large | an `f32` that is NaN, infinite, denormal, or above `1e7` in absolute value | warning |
| bool range | a `bool` other than 0 or 1 | warning |
| unsigned top bit | an unsigned 16- or 32-bit member named like a counter, an index or a size (`Num`, `Count`, `Cnt`, `Index`, `Idx`, `Size`, `Len`) with its top bit set | suspect |

A null pointer is never flagged. The pointer range and the float limit are settings.

The top-bit rule looks at names because of a measurement. In Bianco Hills (episode 8, PAL), applied to every named unsigned member, it raised 1,383 suspects over 1,358 objects, nearly all of them on name hashes (`mKeyCode`) and flag words (`mHitFlags`). With the name filter it raised none there. A suspect is weaker than a warning anyway: it says "look here", nothing more.

## Where names come from

Every class, field and symbol name SMSinspector shows comes from the user's decomp clone, read at startup. Without a clone, the window says "decomp folder required" and shows no name from the game.

A name is never a bare string. `SourcedName` pairs the text with a `Provenance`, and the layout types only accept that pair: `ClassLayout.Identity` and `FieldLayout.Identity` hold it, and `Name` is a read-only view of the text. Each kind of provenance has a factory that demands the facts the user needs to check it, and rejects an empty file or a line 0:

| Kind | Points at | Example |
|---|---|---|
| header | file and line of the declaration | a class, a member, a template instance (the template's line) |
| compiler | the class the compiler added a hidden pointer to | `vtable`, `vbase ...` |
| executable | address in `main.dol` and the decoded instructions | a `dol accessor` candidate |
| decomp body | file, line and function | a `decomp body` candidate |
| sibling | the sibling member's header line | a `sibling offset` hint |

An anonymous union or struct has no name in the header; its field is labelled "(anonymous)" with the line of the `union` or `struct` keyword. Extractor candidates carry their provenance as `Candidate.Origin`, and the JSON report writes it out. `ProvenanceTests` lays out invented classes covering every case above, for JP and PAL, and fails if any name lacks a file or a line.

The code holds no game name, with one exception: `Anchors.cs`. Some features have to start from a known place (the diagnostics follow `gpMarioAddress`; object discovery will start from the scene graph root; the nerve panel will read the spine fields), and those names are anchors. An anchor is only a lookup key. The file holds the name, its kind (symbol or member) and the feature that uses it, never an address, an offset or a type: those are resolved in `symbols.txt` and the headers. If an anchor does not resolve, the feature that needs it is turned off and says which anchor failed.

`SourceGuardTests` enforces this. It reads every string literal under `src/` (regular, verbatim, interpolated and raw strings) and the text attributes of the XAML files, and fails on any identifier shaped like a decomp name (`TFoo`, `gpFoo`, `mFoo`, a mangled name with a `Q` scope) outside `Anchors.cs`. Comments are not checked: they may cite decomp names as examples.
