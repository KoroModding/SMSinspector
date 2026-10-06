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

Until a decomp folder is set, the **Decomp** section says "decomp folder required" and SMSinspector shows no name from the game at all: every class, field and symbol name comes from the clone.

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

When PAL offsets cannot be derived safely, the layout ends with "PAL offsets unverified after 0x.." and the later offsets show as `?`. The same happens when the game's own code contradicts the layout (see "Checking the layouts against main.dol" below); the line then says why.

**Save full report** writes the complete validation report, with every disagreement between the headers and the computation, to `%APPDATA%\SMSinspector\reports\layout-report-<commit>.txt`. It can be useful to the decomp project as it is.

## Name extractor

Most members the decomp has not named yet are called `unkXX`, after their offset. Some of the game's original names survived compilation, mostly as method names. The name extractor puts those names next to the members they probably belong to. It renames nothing: each candidate is a lead for you to check in game.

Click **Run name extractor** in the **Names** section once the layouts are loaded. It takes about a second. The summary says how many `unkXX` members have at least one candidate, and which sources were used or refused and why. **Save name report** writes the full list, as text and JSON, to `%APPDATA%\SMSinspector\reports\name-report-<commit>.txt` and `.json`.

Each candidate has a level, strongest first:

- **dol accessor**: an original method in your `main.dol` is two instructions, a load or store at an offset from `this` followed by a return. That offset is the member. `getSpeed`, compiled to `lfs f1, 0x8(r3)` then `blr`, returns the float at `+0x8`.
- **decomp body, matching**: the decomp's code for an original method is exactly `return unkXX;` or `unkXX = argument;`.
- **decomp body, indirect**: the decomp's code for an original method is at most two statements and uses `unkXX` some other way.
- **sibling offset**: another class with the same base names its own member at the same offset and size. Often a coincidence; read it as a hint.

The suggested name comes from the method: `getSpeed` suggests `mSpeed`. "Original" means the name is in `symbols.txt`, in the linker map, or marked `// UNUSED` in the decomp. Accessors that the decomp authors wrote themselves, such as `getUnk1C()`, are ignored. The report also counts everything it read but did not interpret (other code shapes, accessors on members that already have a name, long bodies), so nothing is dropped without a trace.

### Adding main.dol

The dol accessor level needs the game's executable, from your own disc. The decomp expects it in `orig/GMSP01/sys/main.dol` inside the clone, and SMSinspector reads it there:

1. In Dolphin, right-click Super Mario Sunshine (PAL) and open **Properties**, then the **Filesystem** tab.
2. Right-click the disc at the top of the tree and choose **Extract System Data**.
3. Pick `orig/GMSP01/` in your decomp clone. Dolphin writes `sys/main.dol` there, with a few other system files.

Before using it, SMSinspector compares the file's SHA-1 with the one the decomp lists in `config/GMSP01/build.sha1`. If they differ (another revision, a modded executable), the extractor does not use the file and the summary says so. The file stays where you put it; SMSinspector never copies it.

Run the extractor again after adding the file: it reads its sources on every run.

### Discovery

Once Dolphin runs the game and the decomp is loaded, the **Discovery** section has a **Scan vtables** button. It reads MEM1 once and lists how many polymorphic objects it found, of how many classes, how many are static, and the most common classes. The last line follows `gpMarioAddress` and says whether the scan found that object, and as which class: it should say `TMario`.

The scan also finds objects the game has freed but not yet overwritten, so counts can include leftovers from an earlier scene. Scan in a level, not on the title screen, and scan again after a scene change.

**Walk scene graph** follows the game's own object tree from its root, through lists and member pointers, and prints the first levels with each object's class and instance name (in Japanese, as the game stores it). It then compares with a scan: objects of graph classes that the walk did not reach are listed as "not reached (stale, or held by an array not followed)". Manager arrays (a `T**` with a length) are followed when the decomp names their length. The summary also counts the pointers it refused to follow and why, and says whether the object `gpMarioAddress` points to is in the graph.

### Checking the layouts against main.dol

With a verified `main.dol`, SMSinspector also checks the PAL layouts against the game's code. It takes every original accessor that is two instructions in `main.dol` and whose decomp body returns or assigns a single member, and compares the offset the code uses with the offset the layout gives that member. On the current decomp almost all of them agree.

When they disagree, the header or the layout is wrong for PAL there. The class then shows "PAL offsets unverified after 0x..", with the method, the decoded instruction and the decomp line as the reason, and its later offsets show as `?`. The **Layouts** summary names those classes and the name report lists each disagreement. SMSinspector does not decide which side is right.

The check runs when the layouts load and again on each **Run name extractor**, so a `main.dol` added later is picked up by running the extractor.

A weaker signal is listed too, as "PAL suspects": methods that exist only in the PAL build and access their own object in a way the PAL layout does not fit, such as 16-bit writes into a member declared as a pointer. The layout view shows the range and the reason under the class; offsets are kept.

### Optional: the original linker map

The original linker map adds the functions the linker removed (UNUSED), and with them a few more original names. It is not part of the decomp. If you have it, put it with your game files under `orig/GMSP01/files/` in the clone. SMSinspector looks for it at the path named by the `map:` line of `config/GMSP01/config.yml`, then for any `.MAP` file in `orig/GMSP01/files/`. The **Decomp** section and the name report say which file was used, or why none was.
