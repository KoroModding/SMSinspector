# SMSinspector

SMSinspector is a Windows desktop tool that reads the memory of Super Mario Sunshine running in Dolphin and lists every live actor, with its fields named after the [doldecomp/sms](https://github.com/doldecomp/sms) decompilation.

Instead of reading `0x8123A4C0 + 0x1A4` by hand and guessing, you see the class, the field name from the decomp headers, its type and its live value. It is meant for people naming unknown fields in the decomp, and for modders debugging actors.

## Status

Early work. SMSinspector finds a running Dolphin, checks that it runs the PAL game, reads its memory, loads the symbols from your decomp clone, and identifies Mario's object from its vtable. The window only shows diagnostics for now. Still to come, in order: layouts from the decomp headers, a name extractor that collects original names that survived compilation, object discovery, the live UI, then tools to help name fields (timeline, what changed between two moments, CSV export, evidence notes for decomp PRs).

Setup and use: [docs/usage.md](docs/usage.md). How it works: [docs/architecture.md](docs/architecture.md).

## Scope

- Game version: PAL (GMSP01) only for now. JP (GMSJ01) is planned once someone can test it on a copy they own. US (GMSE01) is not supported by the decomp.
- Platform: Windows. The memory access sits behind an interface so a Linux backend can be added later.

## Safety model

SMSinspector only reads. It opens the Dolphin process with query and read rights, and the code contains no memory write path. CI fails the build if a write API ever appears in the source.

## What is not in this repository

No game data, no decomp headers, no `symbols.txt`. You point SMSinspector at your own clone of the decomp and your own copy of the game; everything is read from there at runtime.

## Building

Requires the .NET 10 SDK.

```
dotnet build SMSinspector.sln
dotnet test SMSinspector.sln
```

## License

GPL-3.0. See [LICENSE](LICENSE).
