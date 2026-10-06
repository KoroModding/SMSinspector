# Validation

These checks need a person, Dolphin and their own PAL copy of the game. They test placement: that every class, field and type SMSinspector shows sits on the right object, at the right offset. Whether a name is the right name is the decomp's business, not this file's.

- **V1. Mario's class.** Follow `gpMarioAddress` to the object it points to. Expected: the object is identified as `TMario`.
- **V2. Movement.** Move Mario along one axis, then the other, then jump. Expected: `mPosition` changes on the axis being moved and not on the others; during the jump, the Y component of `mVel` rises, then falls back.
- **V3. Spot check against Dolphin.** Pick 3 fields on any objects and open each address (object address + offset) in Dolphin's memory view. Expected: Dolphin shows the same value as SMSinspector.
- **V4. PAL-shifted classes.** Pause the game and inspect `TPauseMenu2` and `MSound`, which have PAL-only members. Expected: no value flagged by the plausibility check. Then inspect `TMarioGamePad` while moving the stick and pressing buttons. Expected: its own members after the `JUTGamePad` base change with the input.
- **V5. JP calibration.** The layout computation reproduces every JP offset comment before it produces a PAL offset. Expected: the test is green in CI on the commit being validated.

Each run gets one entry, newest first. A check the tool cannot run yet is marked "not available" until the feature it needs exists.

## 2026-10-06, end of M4

- SMSinspector: commit `227c98e`
- Dolphin: 2609
- Video mode: 50 Hz
- Scenes: Delfino Plaza; Bianco Hills, episode 8

| Check | Result | Note |
|---|---|---|
| V1 | Pass | In both scenes `gpMarioAddress` leads to an object the vtable scan identifies as `TMario`. Both scenes also still hold a `TMario` left over from an earlier scene; the scan finds it, the scene graph walk does not reach it. |
| V2 | Not available | Needs live field values (M5). |
| V3 | Pass | Game paused, four fields compared byte for byte: `mPosition.x` and `mPosition.y` of `TMario` (from `JDrama::TPlacement`), `mKeyCode` of `TConductor` (from `JDrama::TNameRef`) and `mSize` of the root name list (from `JGadget::TList`). The UI shows no field values before M5, so the values came from a read-only console program built on `SMSinspector.Core`. Dolphin's memory view showed the same bytes for all four. |
| V4 | Not available | Needs the plausibility check (M5). |
| V5 | Pass | CI green on `227c98e`, 268 tests. |
