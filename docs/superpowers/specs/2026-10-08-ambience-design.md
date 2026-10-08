# Ambience — Design

Date: 2026-10-08
Status: Approved in conversation; awaiting written-spec review

## Goal

Let the user put all or selected Hue bulbs into an "ambience": the bulbs slowly change color and white temperature through a palette, creating a calm, evolving effect over time. Available in the desktop app (new Ambience tab) and the CLI.

## Decisions (from the user)

- Two modes behind a toggle: **Drift** (each bulb wanders through the palette on its own offset and pace) and **Together** (all bulbs move through the palette in sync).
- Palettes: built-in presets **and** user-made custom palettes that are saved.
- UI: an **Ambience** tab next to a **Light** tab in the right-hand panel; the existing light list on the left chooses the bulbs.
- CLI: `huereka ambience ...` runs until Ctrl+C.
- An ambience runs only while the HueReka process that started it is running. No background service, no scheduling, no auto-stop timer.

## Non-goals

- Running after the app closes; bridge-side scenes or schedules; Hue Entertainment API streaming.
- Per-bulb brightness variation (one brightness level per ambience).
- Timed stop / fade-to-off (may come later).

## 1. Engine — `Ambience.cs` (no UI dependencies)

### PaletteEntry / Palette
- `PaletteEntry`: either a color (`R`, `G`, `B`, 0-255) or a white (`Kelvin`, 2000-6500). Exactly one kind per entry.
- `Palette`: `Name` (1-40 characters, trimmed, unique case-insensitive among all palettes) and `Entries` (2-12 entries).
- Validation happens when a palette is created or loaded. Invalid saved custom palettes are skipped when loading, never crash startup.
- Kelvin → mired: `ct = round(1_000_000 / kelvin)`, clamped to the bulb's `MinCt..MaxCt`.
- Color → Hue state uses the same RGB → hue/sat conversion as the existing `color` command and Custom color button; it is extracted into one shared helper rather than duplicated.

### Built-in presets
Sunset (amber, coral, rose, 2200 K), Ocean (deep blue, teal, sky), Forest (green, mint, 2700 K), Candlelight (2000 K, 2200 K, amber, 2400 K), Aurora (green, teal, violet), Daylight (2700 K, 4000 K, 5500 K, 6500 K). Presets are read-only. A custom palette may not reuse a preset name.

### AmbiencePlanner (pure, deterministic)
Input: palette, mode, list of lights (id + capabilities), step index, seed. Output: list of `(lightId, state)` for that step.
- **Together:** every bulb targets `entries[step % count]`.
- **Drift:** bulb *i* targets `entries[(step + offset_i) % count]`, with offsets spread evenly over the palette (bulb *i* gets offset `i * count / bulbCount`), so neighbouring bulbs show different entries.
- Each Drift bulb also has its own step length: base speed × a factor from a seeded random number in 0.75-1.25. Each bulb keeps its own schedule (next-due time), which is why the bulbs fall out of lockstep. Together mode uses the base speed for all.
- State per bulb: `on = true`, `bri` from the ambience brightness (when supported), `transitiontime` = that bulb's step length in deciseconds (max 65535), plus:
  - Color entry + color bulb → `hue`, `sat`.
  - Color entry + white-ambiance bulb → nearest white: warm hues (red/orange/yellow) use `MaxCt`, everything else uses a neutral mid ct.
  - White entry + bulb with `ct` → `ct` (clamped).
  - White entry + color-only bulb (no `ct`) → `hue`/`sat` approximation of that white.
  - Dimmable-only bulb → only `on` + `bri`.
  - Bulb without brightness or color support (plugs) → excluded from the ambience.

### AmbienceRunner
- `Task Run(Bridge bridge, AmbienceOptions options, IProgress<string> report, CancellationToken cancel)`.
- Options: palette, mode, light ids, speed (10-600 s), brightness (1-100 %), seed.
- Loop: send each bulb its state when its step is due; wait until the next due time; repeat until cancelled. The first step uses a short 2 s transition so the start is visible right away.
- Commands are sent one at a time with at least 100 ms between them, so the bridge's limit of about 10 light commands per second is never exceeded.
- `Exclude(lightId)` removes a bulb while running (thread-safe). When no bulbs are left, the runner stops on its own.
- Errors: a Hue error or unreachable result for one bulb is reported and that bulb is retried on its next step. A `WebException` (bridge unreachable) is reported ("Lost the bridge, retrying…") and retried with the normal step schedule. Cancellation ends cleanly without throwing to the caller.

## 2. Desktop UI

### Tabs
- Two glass tab buttons, **Light** and **Ambience**, at the top of the right panel. **Light** shows today's controls without changes. **Ambience** shows a new `AmbiencePanel` (new file `AmbiencePanel.cs`) in the same bounds.
- The window size and layout of the sidebar and header do not change.

### Ambience panel
- **Palette** dropdown: presets first, then custom palettes.
- Swatch strip previewing the palette entries (whites drawn as a warm-to-cool tint).
- **New palette…**, **Edit…** and **Delete** (Edit and Delete only for custom palettes).
- **Mode** toggle: Drift / Together.
- **Speed** slider: 10 s to 10 min per change (non-linear scale, so the short end has fine control), with a label such as "every 2 min".
- **Brightness** slider (1-100 %).
- **Start ambience** / **Stop** primary button.
- Applies to: the lights selected on the left; if none are selected, all reachable lights. A caption shows which ("3 selected lights" / "all 7 lights").
- While running, the status line shows e.g. "Ambience: Sunset on 3 lights, every 2 min." Errors from the runner appear there too.

### Palette editor (`PaletteEditor.cs`, modal glass dialog)
- Name field, list of entries with swatches, **Add color…** (Windows color dialog), **Add white** (with warm/neutral/cool choices of 2200 K / 4000 K / 6500 K), **Remove**, **Move up/down**, **Save**, **Cancel**.
- **Save** stays disabled with a short reason until the palette is valid (name, 2-12 entries, unique name).

### Interaction with existing behaviour
- The ambience runs outside the existing `busy` gate in `Run(...)`, so the normal controls, refresh and pairing stay usable.
- A manual change from HueReka (On, Off, brightness, color, warm, cool, double-click/Space toggle, All lights on/off) calls `Exclude` for the affected bulbs. **All lights off** stops the ambience entirely.
- Changes made in other apps (the Hue app, a wall switch) are not detected. The next ambience step overrides them.
- Switching bridges or pairing again stops the ambience.
- Closing the window cancels the ambience. Bulbs keep their last state.
- Auto-refresh keeps running. It only reads state and does not interfere.

### Persistence
`Settings` gains `CustomPalettes` (list), `AmbiencePalette`, `AmbienceTogether`, `AmbienceSpeedSeconds`, `AmbienceBrightness`. They are stored in the existing per-user DPAPI-encrypted `connection.dat`. Missing fields in older files fall back to defaults (Sunset, Drift, 120 s, 70 %).

## 3. CLI

```
huereka palettes
huereka ambience <palette> [id ...] [--together] [--speed <10-600>] [--brightness <1-100>]
```
- `<palette>` matches built-in or saved custom names, case-insensitive. Quote names with spaces.
- No ids → all lights that support brightness or color. Ids are validated like the other commands (numeric, must exist, must support at least brightness).
- Defaults: Drift, 120 s, 70 %.
- Prints one line when it starts and one line per error; runs until Ctrl+C, then prints "Ambience stopped." and exits 0.
- Exit codes: 2 for an unknown palette, bad option or bad id; 1 if the bridge can't be reached at startup.
- `CLI-GUIDE.md`, `README.md`, the `help` text and `CHANGELOG.md` are updated.

## 4. Testing

The project's custom harness (`Tests.cs`, run with `--test`) and the CLI `--self-test` are extended:
- Planner: Together rotation; Drift offsets give neighbouring bulbs different entries; per-bulb pace stays within 0.75-1.25 and is the same for the same seed; capability fallbacks (white-only, color-only, dimmable, plug excluded); ct clamping; Kelvin conversion; `transitiontime` capped at 65535.
- Palette validation: name rules, entry count, duplicate and preset names, invalid saved palettes skipped.
- Runner against the simulated bridge transport: sends `transitiontime`; respects the ≥100 ms spacing; cancellation stops cleanly; an unreachable bulb is retried; `Exclude` removes a bulb; it stops when empty.
- Settings round-trip with custom palettes; old settings files without the new fields load with defaults.
- Form construction: the Ambience tab exists, switching tabs shows the right panel, and Start uses selection or all lights.
- CLI: argument validation, `palettes` output, ambience start against the simulated bridge with cancellation.

No test sends commands to physical lights.

## 5. Files

| File | Change |
|------|--------|
| `Ambience.cs` | New: palette model, presets, planner, runner, RGB/Kelvin helpers |
| `AmbiencePanel.cs` | New: Ambience tab UI |
| `PaletteEditor.cs` | New: palette editor dialog |
| `MainWindow.cs` / `MainWindow.Actions.cs` | Tabs, Exclude on manual changes, stop on close/rebind, use shared color helper |
| `HueReka.cs` | Settings fields |
| `Cli.cs` | `palettes`, `ambience` commands |
| `Tests.cs` | New tests (may split into `AmbienceTests.cs` to keep files under 500 lines) |
| `build.ps1` | Add new source files |
| `README.md`, `CLI-GUIDE.md`, `CHANGELOG.md` | Docs |
