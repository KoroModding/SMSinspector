# Using SMSinspector

## What you need

- Windows, 64-bit.
- Dolphin, running your own copy of Super Mario Sunshine, PAL version (game ID `GMSP01`).
- A clone of the [doldecomp/sms](https://github.com/doldecomp/sms) repository. SMSinspector takes every class, field and symbol name from it.
- To build SMSinspector yourself: the .NET 10 SDK.

## Pointing SMSinspector at the decomp

```
git clone https://github.com/doldecomp/sms.git
```

In SMSinspector, click **Choose decomp folder...** and pick the root of the clone: the folder that contains `configure.py` and `config/GMSP01/`. SMSinspector checks both, loads `config/GMSP01/symbols.txt`, and shows the clone's commit and how many symbols and vtables it read.

The path is saved in `%APPDATA%\SMSinspector\settings.json`, so you only pick it once. If you pull new commits into the clone, restart SMSinspector to reload the symbols.

## Connecting to Dolphin

Start Dolphin and boot the game. SMSinspector checks for Dolphin once a second and connects on its own; there is nothing to click. The top of the window says what it found:

- **Dolphin is not running.**
- **... is running, but no GameCube game is booted.** Boot the game.
- **... is not supported yet** / **is not Super Mario Sunshine.** SMSinspector reads GMSP01 only for now.
- **refused access.** Dolphin runs as administrator. Run SMSinspector as administrator too, or start Dolphin normally.
- **Dolphin found (PID ...), GMSP01 ...** Connected.

If you stop the emulation or restart Dolphin, SMSinspector notices and reconnects when the game is back.

SMSinspector only reads Dolphin's memory. It cannot change anything in the game.

## Class layouts

Once the decomp folder is set, SMSinspector reads every header of the clone and lays out every class, for JP and PAL. The **Layouts** section shows a summary: how many classes, how many JP offset comments the computation reproduces, how many classes differ in PAL.

Type a class name (`TMario`, `JDrama::TNameRef`, or `TVec3<f32>` for a template) and press **Show PAL layout**. You get every field in memory order, base classes first, with its offset, size, type and a note:

- **comment, verified**: the offset comes from the header and the computation reproduces it.
- **comment**: from the header, but nothing before it could be sized to check it.
- **computed**: no comment in the header; the offset follows from the sizes before it.
- **moved from JP 0x..**: PAL places this member elsewhere because of a version block before it.
- **PAL only**: the member exists only in the PAL build.
- **(padding)**: bytes an alignment explains. **(gap)**: bytes nothing in the header explains.

When PAL offsets cannot be derived safely, the layout ends with "PAL offsets unverified after 0x.." and the later offsets show as `?`.

**Save full report** writes the complete validation report, with every disagreement between the headers and the computation, to `%APPDATA%\SMSinspector\reports\layout-report-<commit>.txt`. It can be useful to the decomp project as it is.

## Optional: the original linker map

The original linker map adds the functions the linker removed (UNUSED), which the name extractor will use. It is not part of the decomp. If you have it, put it with your game files under `orig/GMSP01/files/` in the clone. SMSinspector looks for it at the path named by the `map:` line of `config/GMSP01/config.yml`, then for any `.MAP` file in `orig/GMSP01/files/`, and the **Decomp** section says which file it used or why it used none.

## Optional: the disassembly

The name extractor can also read the string literals that each class's functions use. Those come from the disassembly the decomp produces when you build it. In the clone, with your game files extracted into `orig/GMSP01/` as the decomp's README explains, and Python and ninja installed, run:

```
python configure.py --version GMSP01
ninja
```

The disassembly lands in `build/GMSP01/asm/`. Without it, the name extractor skips this source and says so.

Neither the map nor the disassembly is needed for anything that exists today; they matter once the name extractor lands.
