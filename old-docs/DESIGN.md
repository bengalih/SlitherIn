# DESIGN.md — SlitherIn redesign (new project)

> This document is the blueprint for the NEW implementation. It lists every file
> that will be created, its responsibility, and the shape of the code inside it.
> It is a map for implementation sessions, not prose. Read it before writing or
> restructuring anything; update it when the structure changes.
>
> Companion docs: `AGENTS.md` (this project's build/config/behavior invariants),
> `BUILDING.md` (toolchain). The agreed design rationale lives in
> `D:\SlitherIn2\PROJECT-STATE.md` (the working-memory file, read/copy only).

## 1. Non-negotiables (from the design record)

- .NET SDK 10 building for **net48** runtime, WinForms tray app, single EXE.
- Config is **declarative, strict JSON** (comments allowed via
  `JsonCommentHandling.Skip`). `schema_version` at the top level.
- ONE unified macro shape: `start` / `loop` / `schedule` / `steps`. No
  macro/cycle/idler split (idler concept dropped).
- One **cooperative dispatcher**; multiple macros run simultaneously, injection
  serialized through the current engine.
- **IKeyEngine** abstraction: `sendinput` (default) vs `viiper`. Hard-fail (no
  fallback, alert icon + message) when VIIPER is selected but unavailable.
- Triggers/toggles/abort/kill detected via ONE low-level hook set
  (WH_KEYBOARD_LL + WH_MOUSE_LL). No RegisterHotKey, no poll watchdog.
- Profile selection is a MODE: `manual <profile>` (sticky) or `auto`
  (follow-the-foreground-window). Profile switch PARKS runtime; file reload
  ABORTS+DISARMS. Wall-clock timers, never paused by focus.
- No control flow in config. No `fire_on`, no `hotbar` keyword, no text steps,
  no screen-position clicks (all rejected/parked).
- REV `rev-YYYYMMDD-NN` const, bumped every code change, verified post-build.

## 2. Directory layout

```
D:\SlytherInNew\
  AGENTS.md              project invariants (this new root)
  BUILDING.md            toolchain (exists, updated)
  DESIGN.md              this file
  build.ps1              stop → dotnet build → REV verify → relaunch
  src\                   app source (WinExe, net48)
  tests\SlitherIn.Tests\ unit tests (NUnit)
  sample-json\           fresh samples in the NEW vocabulary
  reference\             OLD app, read-only reference (do not edit)
```

All app code lives in `src\`, namespaced `SlitherIn.*`. Pure logic is isolated
under `SlitherIn.Core.*` (no WinForms / no P/Invoke) so the test project can
reference just `Core`. Everything Win32/UI/engine-concrete sits outside Core.

## 3. File-by-file structure

### 3.1 Root / project

| File | Contents |
|---|---|
| `src\SlitherIn.csproj` | SDK-style, `OutputType=WinExe`, `TargetFramework=net48`, `UseWindowsForms=true`, `AssemblyName=slitherin`. Packages: `Microsoft.NETFramework.ReferenceAssemblies` (build-time only), `System.Text.Json`. |
| `tests\SlitherIn.Tests\SlitherIn.Tests.csproj` | `net48`, `Microsoft.NET.Test.Sdk` + NUnit + adapter, `ProjectReference` to `src`. |
| `build.ps1` | Stop running slitherin; `dotnet build -c Release`; parse `REV` from `src\Program.cs`; scan built EXE bytes (UTF-16 decode, `Contains`) to verify; relaunch. |
| `sample-json\` | `slitherin.settings.sample.json` (globals) + `profiles\slitherin.json` (combat cycle in new vocabulary) + a `CharB` profile example for auto mode. |

### 3.2 Entry / composition (`SlitherIn`)

| File | Responsibility |
|---|---|
| `Program.cs` | `[STAThread]` entry point. `const string REV`. Single-instance guard, then boots `App` and runs an application message loop (hooks + tray need a WinForms context). |
| `App.cs` | Thin WinForms shell (hooks → composition, tray, timers). Feed hook events into `Composition.HandleInput`/`HandleModifierState`, tick the 10 ms driver, reload on watcher events, rebuild the tray on `Changed`, persist tray toggles. No business logic here. |
| `Startup.cs` | Platform concerns: single-instance mutex, run-at-startup (registry), `SLITHERIN_DIR` override + `RuntimeDir()` resolution (`%SLITHERIN_DIR%`, else exe dir). |

### 3.3 Core — Config (`SlitherIn.Core.Config`)

Pure data + parsing; UI-agnostic, fully unit-testable.

| File | Responsibility |
|---|---|
| `Models.cs` | POCOs mirroring the config: `Settings`, `Profile`, `Macro`, `StartSpec` (`trigger`, `fire_after_ms`, `toggle`, `toggle_sound`, `trigger_behavior`), `LoopSpec` (`count`, `continue_on_release`), `Schedule` (`mode`, `fire_delay_ms`), `Wrapper`, and `ToggleSound`/`SoundSpec`. **Steps are shape-typed raw `List<JsonElement>`** (`{keys}/{button}/{sound}/{wait_ms}`, optional `wrapper`/`cooldown_ms`) — vocabulary stays strict; typed step classes exist ONLY in the Normalizer output, never in the config model. JSON attributes define the strict schema; defaults = omitted values. |
| `KeyName.cs` | The canonical key vocabulary + parser: `Chord Parse(string)` for `"Ctrl+K"`, `"RButton"`, `"Numpad1"`; accepts **only canonical spellings** (single source of truth for names like `LButton`/`XButton1`/`MButton`), top-row digit vs `Numpad*` disambiguation. |
| `Normalizer.cs` | One-pass canonicalization of a `Profile` → `NormalizedProfile`: parses every trigger/step/chord via `KeyName`, classifies steps by shape (`keys`/`button`/`sound`/`wait_ms`), resolves wrappers (attach `before`/`after`, no stacking), folds pacing defaults. **One config step = ONE logical action**: a `{ "keys": [...] }` list is a chord SEQUENCE inside the step (no pacing within), pacing applies between steps. Strict — throws (`KeyParseException`/`NormalizeException`) when a config cannot be built. |
| `Validation.cs` | Schema/structural errors → `ValidationResult` (message + row/field). Called on load and on every profile (cross-profile check that no two profiles' window targets are ambiguous). |
| `Loader.cs` | `LoadSettings()` / `LoadProfiles(dir)`. Handles `schema_version` gate, `JsonCommentHandling.Skip`, maps file → `Models`. **Never throws** into the UI: returns a `LoadOutcome` (config + optional errors) so the tray can surface failures without a crash. |
| `Watcher.cs` | Filesystem watcher over the config dir with debounce (~300 ms). Raises `ReloadRequested` only for our files. No logic about what to do on reload — `App` decides (abort+disarm, keep-old-on-error). |

### 3.4 Core — Engine (`SlitherIn.Core.Engine`)

Pure; the heart of the redesign. Tested without a UI or real input.

| File | Responsibility |
|---|---|
| `WallClock.cs` | Abstract time source (`NowMs`), default backed by `Stopwatch`; injectable so tests can fake time. All cooldowns/countdowns go through this (never paused by focus — wall-clock rule). |
| `Cancellation.cs` | Cooperative cancel token per macro (kill / global-abort / per-macro `abort_keys`). Waits & sound check it; not the `Thread.Abort`-style old code. |
| `LoopSpec.cs` | Pure loop semantics (C-5): `count` omitted = 1, `-1` = forever, ALWAYS honored for both trigger kinds; `continue_on_release` governs HELD triggers only (`fire_after_ms`) — explicit-or-false. Tap triggers never consult it (a tap always runs its full count). One class, no side effects. |
| `Workflow.cs` | The ONE start→run→end container (per-macro state machine). States: `Idle → Armed (toggle on) → AwaitingHold (fire_after_ms) → Running → (resume/complete/cancelled)`. Runs come from the dispatcher's single loop. |
| `IPacing.cs` | Policy interface: given the step list + timers + a "ready" num, yields the next step+delay (or "wait"). Implementations decide pacing; `Workflow` doesn't. |
| `FixedPacing.cs` | Default pacing: fire steps in order with `default_delay_ms` (or step `wait_ms` override). One pass for `count: 1`; re-runs per `count`/loop. |
| `CooldownPacing.cs` | Cooldown scheduler: each pass fires EVERY step whose `cooldown_ms` expired (list order), paced by `fire_delay_ms`, then rescans. No `cooldown_ms` = always ready. This is the old cycle behavior. |
| `Dispatcher.cs` | Cooperative dispatcher: a single loop that advances every registered macro `Workflow` in small slices, calls pacing, and serializes the actual `engine.Press(...)` calls. No per-macro threads. Also drives the idle path (parked profiles are not ticked). |

### 3.5 Core — Target / profile selection (`SlitherIn.Core.Target`)

| File | Responsibility |
|---|---|
| `WindowTarget.cs` | Pure match: `exe` + `title` wildcard pattern. `Matches(exe, title)`; pattern → regex once up front. No Win32. |
| `ForegroundInfo.cs` | Snapshot struct: `Hwnd`, `ProcessExe`, `Title`, captured at event time (so matching is deterministic even if focus changes mid-decision). |
| `ProfileCatalog.cs` | Catalog of all profiles: (a) at startup loads+validates every profile (broken one = tray alert, not a crash); (b) `Resolve(ForegroundInfo)` → the owning profile for AUTO mode; (c) owns each profile's PARKED runtime (armed/running/cooldowns survive switching away). Only the active profile is wired to the dispatcher. |

### 3.6 Core — Diagnostics (`SlitherIn.Core.Diagnostics`)

| File | Responsibility |
|---|---|
| `Log.cs` | Three levels: `off` (no file at all) / `on` (REV + errors/warnings) / `debug` (adds every hook event in CANONICAL names + focused window + step firing + reloads). `KeyEvent(string canonical, hwnd, exe, title)` is the **key-finder**: what you press is the string you paste into config. |

(Beeps need MCI/P-Invoke, so they live OUTSIDE Core — see `Ui\Beeps.cs` below. Core stays
free of P/Invoke by rule.)

### 3.7 Hook (`SlitherIn.Hook`) — non-Core (Win32)

| File | Responsibility |
|---|---|
| `Win32.cs` | All P/Invoke in one place: low-level hook install/uninstall, `GetForegroundWindow` + title/exe extraction, key/mouse structs, scan code helpers. |
| `InputEvent.cs` | Provided-event model: `Kind` (KeyDown/KeyUp/MouseDown/...), canonical `Chord` (via `KeyName`), plus a `ForegroundInfo` snapshot. Hooks only *report*; `App` routes (trigger/abort/toggle vs log). |
| `LowLevelHooks.cs` | `WH_KEYBOARD_LL` + `WH_MOUSE_LL` event source. Pass-through is ALWAYS `CallNextHookEx` unchanged (≈ AHK `~`); injected events skipped. Main output: canonical `InputEvent`s; a SECOND output (C-6): `ModifierState` — the full modifier bitmap on every real change — because bare modifier keys never produce a chord event, so the chord-release feed must ride its own event. Everything (triggers, toggles, abort, kill, logging, release) listens here — the single detection path. |

### 3.8 Input (`SlitherIn.Input`)

| File | Responsibility |
|---|---|
| `IKeyEngine.cs` | `Available`, `Initialize()`, `SetState(IReadOnlyList<Chord>)` (diff-based), `ReleaseAll()`, `Dispose()`. This is the seam. |
| `SendInputEngine.cs` | Default. Translates canonical keys → VK/scancode, sends down+up pairs; never "unavailable." |
| `ViiperEngine.cs` | VIIPER HID (TCP client port from the old `SlitherIn.Viiper.cs`). `Available` checked at profile load; if selected-but-unavailable → hard-fail path (App surfaces alias-config alert). One-shot null-terminated API requests + a long-lived device report stream ([mod][count][usages]); auto-launches `viiper.exe`, throttled self-heal rebuild (unplug + replug). |
| `HidKeyMap.cs` | Canonical token → USB-HID keyboard usage + ModifierFlags → HID modifier byte for the VIIPER report (NOT the Win32 VK table — pure, tested). Mouse tokens have no usage → dropped. |

### 3.9 UI (`SlitherIn.Ui`)

| File | Responsibility |
|---|---|
| `TrayHost.cs` | `NotifyIcon` lifecycle, icon states (normal ⇄ alert), balloon text. No menu logic. |
| `TrayMenu.cs` | Builds the menu model each refresh: Profile row (click = open active profile JSON; arrow = profile-switch submenu with **Auto** entry), macro rows (trigger display), `Abort: <combo>` row, Window-check / Notifications toggles (state re-shown via label), run-at-startup, `ALERT: <msg>` row on error. Idler menu absent. |
| `MenuActions.cs` | Handlers: switch profile / mode, toggle window-check + notifications + run-at-startup (persist back to settings), open file (values-then-editor), version/exit. The watcher is the only reload trigger — no manual reload row. |
| `Beeps.cs` | MCI tone/file cues for toggle on/off and arm/disarm (`toggle_sound`); cancellable like everything else. P/Invoke lives here, deliberately OUTSIDE Core. |

### 3.10 Tests (`SlitherIn.Tests`)

`KeyNameTests` (chords, buttons, numpad, rejections), `NormalizerTests`
(wrapper + pacing expansion), `ValidationTests` (schema + ambiguous targets +
viiper mouse-output), `ModelsTests` (config POCO defaults), `LoaderTests`
(settings/profile load outcomes, schema gate, comments), `LoopSpecTests`
(count + held-only `continue_on_release` semantics), `PacingTests` (fixed +
cooldown sequences), `WindowTargetTests` (exe+title wildcards),
`WorkflowTests` (states, tap/hold counts, release rules, abort tiers, parking),
`DispatcherTests` (serialized injection, held-chord diff), `CancellationTests`
(cooperative cancel), `KeyTranslationTests` (VK ↔ canonical), `HookEventModelTests`
(ModifierBitmap feed + canonical chords), `CompositionTests` (composition:
boot-with-broken-profile, reload abort/disarm, keep-old, VIIPER hard-fail,
window-gate, AUTO switching, C-6 any-member chord release), `LogTests`,
`ProfileCatalogTests` (resolve + parked runtime survives a switch), `ViiperTests`
(mock-server VIIPER protocol + HidKeyMap table). Time is faked via `FakeClock`.

## 4. Milestones

> Execution order and one-stage-at-a-time check-ins live in **`PLAN.md`**.
> These are the design milestones the stages map onto.

1. **Skeleton compiles** — all files above, empty bodies. (This deliverable.)
2. **Core Config + tests** — `Model`/`KeyName`/`Normalizer`/`Validation`/`Loader`
   green.
3. **Core Engine + tests** — `WallClock`/`Cancellation`/`LoopSpec`/`Workflow`/
   `Pacing`/`Dispatcher` green.
4. **Target/catalog + tests** — auto-switch resolution + parked runtime.
5. **Hooks + engine backends** — real detection + SendInput delivering.
6. **Tray + composition** — tray UX, alerts, engine hard-fail, manual/auto mode
   live. First end-to-end build.
7. **Sample configs + hand-converted `slitherin.json`** from the user's old file.
8. **VIIPER engine port + verify.**

## 5. Rules for implementers

- Anything under `Core` must stay free of WinForms, P/Invoke, and `System.Drawing`.
- KeyName vocabulary + rules (locked): letters A-Z, top-row digits, F1-F24, the
  NamedKeys table; case-insensitive input canonicalized to fixed names; modifier
  order fixed to Ctrl, Alt, Shift, Win; rejections = out-of-vocabulary token,
  second key, duplicate modifier, dangling '+', keyless chord.
- The dispatcher is cooperative: no `Thread.Sleep` long enough to starve other
  macros; waits are cancellable.
- Every code change bumps `REV` in `Program.cs` before `build.ps1` runs.
- No file under `reference\` is ever edited.