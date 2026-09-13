# ARCHITECTURE.md — SlitherIn (the one technical document)

> **Status: CURRENT.** This is the **single consolidated technical document** for
> the redesigned SlitherIn. It supersedes the four file-based records below,
> which were retired as standalone sources of truth on 2026-09-12 after their
> durable content was folded in here. Their full pre-merge snapshots are archived
> in `old-docs/` (PLAN.md, CHANGES.md, DESIGN.md, PROJECT-STATE.md) and their
> original paths are now short pointers.
>
> Superseded records: `PLAN.md` (was the stage plan + decision record),
> `CHANGES.md` (was the change log), `DESIGN.md` (was the build/stage plan +
> file-by-file inventory + the locked decision list), `PROJECT-STATE.md`
> (was the working-memory/design-record file).
>
> Sibling documents: `AGENTS.md` (operating invariants for agents), `README.md`
> (user-facing), `BUILDING.md` (toolchain/build).

---

## Contents

1. [Non-negotiables (locked invariants)](#1-non-negotiables-locked-invariants)
2. [Directory layout & identity](#2-directory-layout--identity)
3. [Layer map](#3-layer-map)
4. [Load-bearing split](#4-load-bearing-split)
5. [Startup](#5-startup)
6. [Threading model](#6-threading-model)
7. [Config pipeline](#7-config-pipeline)
8. [Config vocabulary reference](#8-config-vocabulary-reference)
9. [Workflow engine (one container per macro)](#9-workflow-engine-one-container-per-macro)
10. [Input engines](#10-input-engines)
11. [Detection notes (why the hook layer looks the way it does)](#11-detection-notes-why-the-hook-layer-looks-the-way-it-does)
12. [Window gating](#12-window-gating)
13. [Routing order](#13-routing-order)
14. [One trigger press, end to end](#14-one-trigger-press-end-to-end)
15. [Tray & UI surface](#15-tray--ui-surface)
16. [REV, logging, runtime directory](#16-rev-logging-runtime-directory)
17. [Testing](#17-testing)
18. [Build & toolchain](#18-build--toolchain)
19. [Decisions register](#19-decisions-register)
20. [File-by-file inventory](#20-file-by-file-inventory)
21. [Gotchas & lessons (the cookbook)](#21-gotchas--lessons-the-cookbook)
22. [Change history (C-series, REV trajectory)](#22-change-history-c-series-rev-trajectory)

---

## 1. Non-negotiables (locked invariants)

Signed off across the redesign; the source and tests enforce them.

- .NET SDK 10 building for **net48** runtime, WinForms tray app, single EXE.
- Config is **declarative, strict JSON** (comments allowed via
  `JsonCommentHandling.Skip`). `schema_version` at the top level.
- **No control flow in config.** No conditionals on runtime state, no
  user-visible variables, no macro composition, no loop-until-state, no computed
  values, no `fire_on`, no text steps, no screen-position clicks (all rejected
  or parked). Config stays declarative — the hard line. New feature ⇒ build
  logic in code, expose as a clean declared option (extends a dimension, or
  consciously adds a new named, validated, sample-documented dimension; old
  configs keep loading).
- **ONE unified macro shape**: `start` / `loop` / `schedule` / `steps`. No
  macro/cycle/idler split — a sequence is a cycle with `count == 1`. The idler
  was dropped as a subsystem; it is a cycle policy, not a kind.
- The `stop` dimension was **dropped**. Release behavior is universal: release a
  held trigger → halt; count reached → ends; toggle-off / abort / kill → always
  halt. Explicit `lose_focus` and `timeout_ms` options were considered and
  rejected as redundant (window filter gates starts; kill tiers cover runaways).
- One **cooperative dispatcher**; multiple macros run simultaneously, injection
  serialized through the current engine. No per-macro threads.
- **`IKeyEngine`** abstraction: `sendinput` (default) vs `viiper`. **Hard-fail**
  (no silent fallback, alert icon + message) when VIIPER is selected but
  unavailable.
- Triggers/toggles/abort/kill detected via **ONE low-level hook set**
  (`WH_KEYBOARD_LL` + `WH_MOUSE_LL`). No `RegisterHotKey`, no `GetAsyncKeyState`
  poll watchdog. One detection path for every chord.
- Profile selection is a **MODE**: `manual <profile>` (sticky) or `auto`
  (follow-the-foreground-window). Profile switch **PARKS** runtime; file reload
  **ABORTS + DISARMS**. Wall-clock timers, never paused by focus.
- REV `rev-YYYYMMDD-NN` const, bumped on every code change, verified post-build.
- Config location is **declarative, never environment-dependent**: settings file
  = `--settings-file` else `ExeDir\slitherin.settings.json`; profile dirs =
  exe dir (always first) → settings `profile_dirs` → `--profile-dir`; first
  match wins; `%SLITHERIN_DIR%` is retired (C-7).

## 2. Directory layout & identity

```
D:\SlytherInNew\
  AGENTS.md              operating invariants for agents (this project root)
  BUILDING.md            toolchain
  ARCHITECTURE.md        THIS file — the one technical document
  README.md              user-facing doc (schema, engines, quick start)
  build.ps1              stop → dotnet build → REV verify → relaunch
  old-docs\              archived pre-consolidation snapshots of PLAN/CHANGES/
                         DESIGN/PROJECT-STATE (do not edit)
  src\                   app source (WinExe, net48), namespaces SlitherIn.*
  tests\SlitherIn.Tests\ unit tests (NUnit)
  sample-json\           fresh samples
```

All app code lives in `src\`, namespaced `SlitherIn.*`. Pure logic is isolated
under `SlitherIn.Core.*` (no WinForms / no P/Invoke / no `System.Drawing`) so the
test project can reference just Core. Everything Win32/UI/engine-concrete sits
outside Core.

## 3. Layer map

```
        Hook (Win32 low-level keyboard/mouse hooks + foreground snapshot)
                     │                    InputEvent (chord + foreground)
                     ▼
        Composition (routing, catalog, workflow runtime, engines, alerts)
                     │  SetState(chord-set) written once per change
                     ▼
        IKeyEngine ──┬─ SendInputEngine   → Win32 SendInput (keys + mouse)
                     └─ ViiperEngine      → TCP 127.0.0.1:3242 → viiper.exe
                                                         → usbip-win2 driver
                                                         → OS sees a real USB HID keyboard
```

Configuration feeds the same `Composition`: files → `Loader`
(`schema_version` gate + validation) → `Normalizer` → `ProfileCatalog` (window
targets, active profile, per-profile runtimes). The `Watcher` turns an external
save into a reload request, and the tray renders whatever `Composition` exposes
(alerts included).

## 4. Load-bearing split

Enforced in the source and in the tests:

- `App` (the WinForms shell) owns **only** drivers — winning the OS-level input
  events, the 10 ms tick, the config file watcher, the tray icon + menu, sound
  playback, and UI-thread marshalling.
- `Composition` owns **all business rules** — the profile catalog, dispatcher,
  engines, abort tiers, reload policy, routing — and is UI-free and
  test-covered.

## 5. Startup

`Program.Main(string[] args)` (STA):

1. `CliArgs.Parse(args)` — reads `--settings-file <path>` and repeatable
   `--profile-dir <dir>` (both accept `--key=value` form too); unknown/malformed
   switches are ignored.
2. `Startup.AcquireSingleInstance()` — one app per user session, or the second
   launch signals the running instance (which surfaces a balloon) and exits.
3. `App.Boot(cli)` wires everything in order: `ConfigSources.Resolve(ExeDir,
   cli.SettingsFile, cli.ProfileDirs, settings: null)` → log → `Composition` →
   `Watcher` → `LowLevelHooks` (keyboard + mouse, each with an `Installed`
   health flag) → `TrayHost` → 10 ms `Timer` → second-launch signal →
   `Composition.Boot()` (which loads the real settings and **rebuilds** the
   sources so settings-held `profile_dirs` take effect).
4. `Application.Run()` blocks on the message pump.

`Startup.ExeDir()` = `AppDomain.CurrentDomain.BaseDirectory` — the executable's
folder. Where fixes the rest:

- **Settings file** — `--settings-file <path>` if given, else
  `ExeDir\slitherin.settings.json`.
- **Profile dirs** (search order, first match wins, OrdinalIgnoreCase) — the
  exe dir **always first**, then each `profile_dirs` entry (resolved relative
  to the settings file's folder), then each `--profile-dir` (resolved against
  the current working directory).
- **Watch roots** — the settings file's folder plus every profile dir (a listed
  dir that doesn't exist yet is still watched and picked up on creation).
- The log file and external icons always live next to the exe, regardless of
  the above. The old `%SLITHERIN_DIR%` override is **retired** (C-7).

## 6. Threading model

Everything user-visible runs on the **single UI thread**:

- The low-level hooks are installed on the UI thread, so their callbacks already
  arrive on it; `App.OnInputEvent` forwards `Chord + ForegroundInfo` straight to
  `Composition.HandleInput`.
- The 10 ms `Timer` fires on the UI thread and drives `Composition.Tick()`.
- The file watcher's events come off a threadpool thread; `App` marshals
  `ReloadRequested` to the UI thread before calling `Composition.Reload` (engine
  swaps and menu rebuilds are not thread-safe).

The engine's cancellation is **cooperative**: a per-run `Cancellation` flag is
polled by the workflow/pacing loop, so an abort takes effect at the next step
boundary, not mid-injection. This is not the old `Thread.Abort`-style code.

AUTO-mode foreground polling is throttled to ~250 ms. The VIIPER `Available`
getter is bounded (2 s throttle) so it is safe to call from the UI thread.

## 7. Config pipeline

Per file: `Loader` deserializes (JSON with native comment/trailing-comma
support), checks `schema_version == 1`, and runs the `Validator`.
`Error`-severity findings reject the file; `Warning`-severity findings pass
through surfaced in the tray. Nothing is ever thrown into the UI — failures are
`LoadOutcome<T>` values (config + reasons).

Because the step vocabulary is intentionally strict (shape-kinds — see the
vocabulary below), steps load as shape-typed `JsonElement`s and are classified
by the `Normalizer` into `NormalizedStep`s. The `NormalizedMacro`/
`NormalizedProfile` forms carry canonical `Chord`s (single spelling via
`KeyName`), resolved pacing defaults, and per-file runtime bindings — this is
what the engine actually executes.

The catalog (`ProfileCatalog` per file) exposes `Mode` (manual/auto),
`ActiveFile`, and the active profile's workflows, and enforces the
window-gating/auto-switch rules. `Composition.Select(file)` swaps the active
profile and **parks** any running workflows (state retained, wall-clock keeps
counting; they resume unparked on switch-back).

Multi-dir loading (C-7): `Loader.LoadProfiles(dirs, settingsFileName)` loads
every profile dir in source order and **first wins** at the same filename; a
missing dir yields a non-blocking `LoadError` (dir noted) while other dirs still
load. The whole set aggregates into one `ProfileCatalog`, so `<N>` sorting spans
all folders together.

Reload semantics (locked): a config-file change aborts + disarms running
workflows and builds a fresh catalog; a broken **active** file keeps the
previous runtime while the error becomes a tray alert (`ProfileCatalog.KeepActive`).

## 8. Config vocabulary reference

The agreed, locked vocabulary. Defaults are expressed as empty/omitted values,
e.g. `{ "sound": {} }` = default tone (500 Hz / 1s, matching the old Sound.cs).

**Global (`slitherin.settings.json`, default for all profiles):**

| Option | Values | Notes |
|---|---|---|
| `input_engine` | `"sendinput"` (default) \| `"viiper"` | global default; per-profile override available |
| `default_delay_ms` | integer, default 3000 | global fixed-mode pacing; profile ?? settings ?? 3000; a step `wait_ms` overrides per-step |
| `log` | `off` (default) \| `on` \| `debug` | see [§16](#16-rev-logging-runtime-directory) |
| `notifications` | on/off | tray balloons; tray toggle persists to settings |
| `window_check` | on/off | tray toggle persists to settings |
| `active_profile` | `slitherin*.json` filename | which profile is active; tray switch persists to settings |
| `profile_mode` | `"manual"` (default) \| `"auto"` | tray toggle persists to settings |
| `run_at_startup` | n/a | NOT a JSON option — carried in the current user's HKCU `Run` key (tray toggle only); not part of the `Settings` model |
| `abort` | chord string, e.g. `"Ctrl+Alt+F12"` | global default abort — see Abort tiers |
| `kill` | chord string, e.g. `"Ctrl+Alt+Esc"` | unconditional kill — see Abort tiers |
| `profile_dirs` | array of folder paths | extra profile folders resolved relative to the settings file's folder; exe dir stays first (C-7) |

**Profile (top-level):** `name`, optional `input_engine` override (set it only
when this profile differs from global), optional `default_delay_ms` (paces
between every step), `window` (`exe` + optional `title`, for gating + AUTO
mode), `macros`, optional `wrappers`.

**Macro: `start` / `loop` / `schedule` / `steps`.**

- **`start`** (all optional except trigger presence is a schema rule):
  - `trigger` — flat chord string, same vocab as keys: `"Ctrl+K"`,
    `"Shift+RButton"`, `"LButton"`, `"RButton"`. Silent by default.
  - `fire_after_ms` — optional; the trigger must stay held N ms before it counts
    (the old `arm_delay_ms`). Omitted = fire on press. Releasing before the
    threshold cancels the hold.
  - `toggle` — optional arm/disarm key (e.g. `"Pause"`); off always stops.
  - `toggle_sound` — optional `{ "on": <sound>, "off": <sound> }`. Both `on` and
    `off` are optional; a PRESENT slot triggers the default beep (500 Hz / 1s);
    an omitted slot or whole block = no sound for that state. Customization via
    frequency/file overrides, same spec as a `sound` step.
- **`loop`** — `{ "count": N, "continue_on_release": bool }`, a number + one flag:
  - `count` omitted = 1 (once). `count` N = N times. `count -1` = forever.
    `count` is **ALWAYS honored at every value**, both tap and held triggers
    (C-5; the old "ignored when count = 1" wording was deleted).
  - `continue_on_release` — governs **held** triggers (`fire_after_ms`) only;
    tap triggers always run to completion (release never cancels). Default:
    held ↦ `false` (release stops), tap/combo ↦ `true` (release irrelevant;
    kept for config symmetry). Explicit override only in the non-obvious case.
    **Chord-release (C-6):** release is defined as ANY chord member leaving the
    down-set, not exact-chord match; a modifier released first also terminates
    the run, matching single-key intuition. (Drafting note: `while_held` was
    the earlier name; rejected for assuming a sustained press — a loop can be
    kicked by a plain combo. Final name `continue_on_release`.)
- **`schedule`** — optional; omitted = fixed pacing via `default_delay_ms`.
  - Cooldown scheduler: `{ "mode": "cooldown", "fire_delay_ms": 700 }` — each
    pass fires EVERY step whose cooldown has expired, in list order, with the
    delay (default 700 ms, the old `animation_delay_ms`) between consecutive
    firings, then rescans. One delay paces everything; no one-per-pass mode. A
    step without `cooldown_ms` is ALWAYS ready and fires every pass.
  - `tick_ms` is an internal detail, GONE from config.
  - **Timing vs focus (IMPLEMENTED by C-4):** cooldowns/countdowns run on
    WALL-CLOCK (`Environment.TickCount` via `WallClock`), never paused by focus
    changes. Focus gates the trigger (starts) AND the injection — see [§12](#12-window-gating).
- **`steps`** — ordered array of step objects:
  - `{ "keys": "7" }` — press+release (default patterns apply).
  - `{ "keys": ["Ctrl+6", "7"] }` — a LIST of chords fired in sequence as ONE
    logical action (e.g. "bar switch" then press — the user defines what that
    combo is; **no `hotbar` keyword exists**).
  - `{ "sound": {} }` — beep; overrides optional.
  - `{ "wait_ms": 500 }` — explicit pause step (a `wait_ms` on a key step
    overrides the default for that step; `0` forces no delay).
  - `{ "keys": "7", "wrapper": "bar6" }` — attach a named wrapper; its `before`
    steps run, then the key fires, then `after` runs — one logical action.
  - `{ "keys": "5", "cooldown_ms": 900 }` — in cooldown mode, steps are
    candidates with `cooldown_ms` (ms before that action can fire again).
    Omitted `cooldown_ms` = ALWAYS ready (fires every pass). `cooldown_ms` sits
    on the OUTER step.
  - Explicitly REJECTED: `between_ms`, `hotbar`, `default_hotbar`, `sequence`
    steps, and a `fire_all_ready` toggle (fire-all is THE behavior, not an
    option).
  - **Mouse steps** — a `button` step and mouse-key chords (see `KeyName`
    vocabulary) are legal on `sendinput`. A `viiper` profile carrying them is a
    hard VALIDATION error (a HID keyboard cannot emit mouse output) — see [§10](#10-input-engines).
- **`wrapper`** (LOCKED name) — a named, reusable set of `before` and/or `after`
  steps that a key step attaches to, to avoid bloated inline bar-switching.
  (`cycle`/`context`/`mode`/`stance`/`frame`/`nesting_action`/`handler`/
  `scaffold` were rejected; `decorator` was the runner-up.) Profile-level:
  `"wrappers": { "bar6": { "before": [...], "after": [...] } }`.
  - `before`/`after` each optional; both are step lists (keys, waits, sounds).
  - No parameters — a wrapper is a constant. Different timing per key = a
    different wrapper (`on_bar6_fast`, `on_bar6_slow`). No wrapper stacking —
    one per step.
  - The bar-switch meaning lives ONLY in the wrapper's contents and the user's
    naming (`bar6`); SlitherIn carries zero game assumptions.
- **Abort tiers** (locked 2026-09-11; detected via the SAME hook — single
  detection path):
  1. **Per-macro `abort_keys`** — optional; stops only that macro, mid-flight.
  2. **Global default abort** — settings-level; inherited by any macro that
     doesn't define its own `abort_keys`. Pressing it stops every running macro
     (default matches the old behavior, backwards-compatible).
  3. **`ignore_abort: true`** — per-macro, opt-in; this macro ignores the global
     default abort entirely (e.g. a 6-minute rebuffer cycle).
  4. **`kill_keys`** — settings-level, unconditional; stops literally everything,
     including `ignore_abort` macros. The escape hatch that can never be
     opted-out of. Without it, an `ignore_abort` macro could run away with no
     off-switch.

**Mapped scenarios (property of record):**

- Buff sequence: `trigger: "Ctrl+K"`, `steps` with `wait`/keys/sound, no loop
  (= once), no schedule.
- Hold-to-fire once: `trigger: "RButton"`, `fire_after_ms: 3000`, defaults = once.
- Combat cycle (the old AHK script): `trigger: "LButton"`, `fire_after_ms: 1000`,
  `toggle: "Pause"`, `loop: { "count": -1, "continue_on_release": false }`
  (implied by held trigger, not written), `schedule: { "mode": "cooldown",
  "fire_delay_ms": 700 }`, 5 steps with `cooldown_ms`; hotbar-6 action =
  `{ "keys": "7", "wrapper": "bar6" }` with `"wrappers": { "bar6": {
  "before": [{ "keys": "Ctrl+6" }], "after": [{ "keys": "Ctrl+1" }] } }`.
- 6-min rebuffer: `trigger: "F9"` (tap), `loop: { "count": -1 }`
  (`continue_on_release` implied true by tap), no schedule (or cooldown with
  `fire_delay_ms: 360000`).

**Hotbar decision (LOCKED, evolved):** delete `primary_hotbar` and per-action
`hotbar`, and DO NOT add a `hotbar`/`default_hotbar` keyword — any such keyword
would hardcode "hotbar = Ctrl+N", a DDO-specific (and user-keymap dependent)
convention. Bar switching is expressed ENTIRELY AS PLAIN KEY STEPS/CHORDS,
wrapped in a named `wrapper` for reuse.

**Engine behaviors surfaced as optional config (not new kinds):** trigger
passthrough (does the firing key's own press still reach the game?), re-trigger
while running (restart / resume / ignore), alternating payloads, concurrency
policy when two armed cycles collide (default: one active input source; others
wait/block), explicit fire-on-press vs fire-on-release, held-key emissions for
keys and mouse, mouse as a real step, wheel + side buttons (XButton1/2), numpad
vs top-row digit disambiguation, reload-while-armed semantics.

**Key vocabulary (locked):** letters A-Z, top-row digits (`7`), explicit numpad
(`Numpad1`, distinct from `1`), F1-F24, named key table, mouse buttons
`LButton`/`RButton`/`MButton`/`XButton1`/`XButton2` (numbered 0–4 = VK
0x01–0x05), fixed modifier order Ctrl, Alt, Shift, Win. Case-insensitive input
canonicalized to fixed names. Rejections = out-of-vocabulary token, second key,
duplicate modifier, dangling `+`, keyless chord. Numpad vs top-row is a single
source of truth in `KeyName`.

## 9. Workflow engine (one container per macro)

`Workflow` is the single state machine for every macro kind (a one-shot
sequence is just a cycle with `loop.count == 1`):

- **States** — `Idle` (has a toggle, not armed) → `Armed` → trigger down →
  `AwaitingHold` (only when `fire_after_ms` is set) → `Running`, with `Parked` used
  during profile switches.
- **Arm/disarm** — no `toggle` ⇒ always `Armed`; with a `toggle`, `TogglePressed`
  arms (cue) or disarms (cue + stops the run; "off" always stops).
- **Trigger** — a momentary tap in `Armed` starts the run immediately. A held
  trigger starts only after `fire_after_ms`; releasing before the threshold cancels
  the hold. Re-triggering while already `Running` is ignored.
- **Release** — while `Running`, `continue_on_release` governs HELD triggers
  only: omitted ⇒ `false` (release stops the run), `true` keeps it going to its
  full `count`. Tap triggers never stop on release (key-up is the end of the
  tap; only abort/kill/toggle-off end them). Releasing a chord = ANY member
  leaving the down-set: the hooks emit raw modifier bitmap deltas
  (`LowLevelHooks.ModifierState` → `Composition.HandleModifierState`), so a
  modifier lifted before the main key stops/keeps the run exactly like the
  exact-chord path would (C-6). Single-key triggers are unaffected.
- **Loop** — `count` omitted = 1, `-1` = infinite. Passes are counted against
  wall-clock; cooldowns that expire while parked fire on the first tick back.
- **Pacing** — the `Dispatcher` hands the workflow an `IPacing`: `FixedPacing`
  (steps paced by `default_delay_ms`/step `wait_ms`) or `CooldownPacing`
  (every action whose per-step `cooldown_ms` has elapsed fires each pass,
  paced by `fire_delay_ms`). The workflow only decides *when*; the `Dispatcher`
  subscribes to `StepReady` and does the actual injection. `PacingState.RunId`
  distinguishes a fresh run (re-trigger resets cooldowns/cursors) from a
  pass/park boundary (state kept); `Workflow.Unpark()` resumes the pre-park
  state.
- **Abort tiers** — per-macro `abort_keys` stops that macro; the global `abort`
  stops every macro except `ignore_abort: true`; `kill` stops everything
  unconditionally. All land on the same cooperative `Cancellation`.

## 10. Input engines

`IKeyEngine` exposes the lean contract `Available` / `Initialize()` /
`SetState(IReadOnlyList<Chord> held)` / `ReleaseAll()` / `Dispose()`. Each
`SetState` call carries the **full** current set of held keys (modifiers + key
in one chord), written once per state change. The engine translates that set
into its output medium (diff-based: what changes between calls is down/up).

- **SendInputEngine** — the default; never "unavailable." Translates each chord
  via `KeyMapping` (canonical ↔ virtual key, bidirectional) into
  `keybd_event`-style key-downs/key-ups plus mouse events for button steps.
  **SendInput events must carry BOTH `wVk` and `wScan`** (C-3; scan via
  `MapVirtualKey`) and the **extended-key flag** for PgUp/PgDn/End/Home/arrows/
  Insert/Delete and RShift/RCtrl/RAlt — otherwise games reading
  raw/direct input drop the key (the single most common "works in Notepad, not
  in the game" cause). Works almost anywhere; full-screen/elevated/anti-cheat
  contexts may ignore it.
- **ViiperEngine** — a real HID keyboard. Speaks the VIIPER TCP protocol
  (`127.0.0.1:3242`): greeting → `bus/create` → `bus/N/add keyboard` →
  a persistent streaming report connection carrying
  `[modifier byte][usage count][HID usage bytes...]`. Modifier byte order is
  mapped explicitly from `ModifierFlags` (the keyboard HID order Ctrl=0x01,
  Shift=0x02, Alt=0x04, Win=0x08 differs from the config bit order Ctrl=1,
  Alt=2, Shift=4, Win=8 — the swap is table-tested). Usages come from
  `HidKeyMap` (letters/digits/F-keys top-row, numpad, named keys; mouse = 0).
  The report is capped at 6 usages (HID boot-keyboard limit).
  - If VIIPER is not reachable the engine launches it (finding `viiper.exe`
    next to the exe, in `%LOCALAPPDATA%\VIIPER`, then `%ProgramFiles%\VIIPER`,
    prepending the USBip `bin` folder to `PATH`), and `Available` self-heals on
    a 2-second-throttled rebuild (a lost device re-adds the keyboard + reopens
    the stream) — but does **not** relaunch a dead server; only `Initialize()`
    does that.
  - A failed `bus/N/add` removes the bus it created (old code leaked it).
  - **Mouse output is impossible.** Chords/tokens that need mouse are dropped
    WHOLE at the engine (modifiers + key, so a modifier can't strand), and
    validation rejects any `viiper` profile that declares a mouse `button` step
    or mouse-key chord (incl. inside wrappers; `KeyName.IsMouseToken` is shared
    Core so Validator + KeyMapping agree). Mouse steps remain fully legal on
    `sendinput`.
- **Hard-fail policy** — an engine problem is an alert, not a fallback. If the
  active profile selects `viiper` and the engine is unavailable, that profile
  does not run and the tray icon shows the alert (`ActiveProfileBlocked`); the
  composition root gates on `engineName == "viiper" && !engine.Available`.
  There is no silent switch to `sendinput`. Engine lifecycle is driven by the
  **active profile**: select the engine on profile load/hot-reload,
  `Initialize()` it only if selected, dispose the previous engine when no longer
  needed. A user who never runs a viiper profile never needs VIIPER/usbip
  installed, launched, or warmed up.
- **Layout note:** `IKeyEngine`/`SendInputEngine`/`ViiperEngine`/`HidKeyMap`
  live under `SlitherIn.Input`; `KeyMapping` (the canonical⇄Win32 table) moved
  there too during Stage 6. `KeyName` (Core) is the vocabulary both sides share.

## 11. Detection notes (why the hook layer looks the way it does)

The low-level keyboard hook is the **single** source of detection — the key
vocabulary in `KeyName` is shared with the injection translation, so "what you
can detect is what you can inject" (the same canonical `Chord` objects flow both
ways, and the debug log prints the exact string you can paste into config).
Mouse buttons are detected via a separate low-level mouse hook; the foreground
snapshot (`ForegroundInfo`) feeds the window gate and the key-finder log line.
Hook installation failures are logged as warnings (key detection disabled) but
never abort the app. There is no win32 backstop (no `GetAsyncKeyState` polling,
no `RegisterHotKey`) — the hook is it. Pass-through is always
`CallNextHookEx` unchanged (≈ AHK `~`); injected events are skipped.

Hook-model facts (Stage 6 hard-won): `HookModel` converts raw vkCode / mouse
message + modifier state → canonical `Chord` + event kind; unknown keys and lone
modifiers produce NO event. C-6 added a second output: `ModifierState`, the
full modifier bitmap on every real change, because bare modifier keys never
produce a chord event — the chord-release feed rides its own event.

The old code's THREE redundant detectors (hook + 5 ms polling watchdog +
`RegisterHotKey`) around one global `abortRequested` flag were deleted; the
global flag became per-macro cooperative cancellation.

## 12. Window gating

- **Start (always):** a trigger only starts a run when the window gate passes —
  manual mode ignores foreground; AUTO mode requires the active profile's target
  to own the current foreground window.
- **Mid-run (C-4, option (b)):** the gate covers the INJECTION too. When a macro
  is window-gated and the target is not foreground when a ready step would fire,
  the press is **skipped-but-kept-ready** and fires on the next focused
  opportunity (catch-up). The next countdown starts from the ACTUAL firing time
  (e.g. the 6-min rebuffer keeps counting in the background; returning to the
  game fires the ready `R` immediately).
- Implementation: `Composition.Tick` snapshots the gate ONCE per 10 ms slice
  (from the live foreground + active target) and stores it on
  `Dispatcher.InjectionAllowed`; each workflow's `Advance` checks it **before**
  consulting its pacing — a closed gate returns early so the pacing cursor never
  moves and `PacingState.LastFireMs` only updates on a REAL fire. That's why
  "next countdown from actual firing" fell out of both pacing policies for free.
  The check lives at the workflow→pacing seam: gating at the dispatcher's
  injection seam would advance pacing then drop the fire (losing the step,
  breaking catch-up); folding it into a pacing policy would couple a window
  policy into the pacing layer.

## 13. Routing order

`Composition` routes every input event through this locked order:

1. `kill_keys` → stop everything, unconditionally.
2. Global default `abort` → stop every running macro except `ignore_abort: true`.
3. Per-macro `abort_keys` → stop only that macro.
4. `KeyUp` → deliver `TriggerUp` (held-trigger release / chord-release via the
   modifier-bitmap feed).
5. `toggle` → arm/disarm (off always stops).
6. Window gate + engine guard → block start if closed/hard-failed.
7. `trigger` → start the run.

## 14. One trigger press, end to end

```
 hook fires (keyboard low-level hook)
        │  InputEvent: Chord (canonical) + ForegroundInfo
        ▼
 Composition.HandleInput(e)
        │  screen: kill / global abort / per-macro abort / KeyUp / toggle /
        │  window gate + engine guard / trigger  (single detection path)
        ▼
 Workflow.TriggerDown / AwaitingHold→Running
        │  10 ms tick → Advance → Pacing decides a step is due
        ▼
 Dispatcher subscribes StepReady → engine.SetState(held-set)
        │
        ├─ sendinput ─────────────► simulated keys / mouse in the focused window
        │
        └─ viiper ──[mod][n][usages]──► viiper.exe → usbip VHCI bus
                                          → real USB HID keyboard the OS sees
        │
        ▼
 release → continue_on_release? loop next pass? aborted?
        ▼
 cleanup: held keys released, run finished, state back to Armed/Idle
```

## 15. Tray & UI surface

Tray-only for v1 — the tray is the CONTROL surface, and it lives in `Shell`.
Settings logic (model, persistence, applying them) lives in `Core`, NOT the
tray, so a future options window is just a new Shell presenter. Known trade-off
accepted: toggles re-show state via their label ("Notifications: On/Off"). No
full macro editor ever planned — macro content stays JSON-edited (the user's
workflow).

The tray restores the old-app look on the new menu: custom `icon.ico`/`alert.ico`
(extracted from the old source into `src\Ui\Icons\`, embedded in the EXE;
external files next to the exe still override); alert FLASH (500 ms
normal⇄alert toggle) + tooltip `SlitherIn - <reason>` on error; **left-click**
opens the menu (private `ShowContextMenu` reflection, carried from the old app)
+ shows the pending-error balloon (gated by Notifications); `TwoToneRenderer`
(blue `Macros:` header, blue macro names, red `Abort:`, green clickable version
row, gray disabled rows); macro rows are enabled-display with
`• name: trigger` labels + step-count tooltips; an `Open Settings
(<settings-file name>)` row (passes the actual filename, courtesy of the
`settingsFileName` Build parameter, C-7); the version row is clickable → releases page.

Menu composition (`TrayMenu.Build` per the locked list): alert rows, Profile
row (click = open active profile JSON; arrow = profile-switch submenu with
**Auto** entry), "Profiles" submenu, display-only macro + Abort rows, three
state-label toggles (Window-check / Notifications / run-at-startup), Reload
via the WATCHER only (no manual reload row), version, Exit. `TrayHost` owns
`NotifyIcon` lifecycle, icon states, balloon text; `MenuActions` is the handlers
(switch profile/mode, toggles persisted back to settings, open file
values-then-editor, version/exit).

## 16. REV, logging, runtime directory

- **REV** (`rev-YYYYMMDD-NN` in `src/Program.cs`) is the single build-identity
  constant: shown in the tray, embedded in the EXE, and verified by `build.ps1`
  after each build. Bumped on every code change.
- **Logging** — one settings option, three levels: `off` (DEFAULT; no log file
  is created at all), `on` (sparse: a startup line with REV + errors/warnings
  as they occur), `debug` (verbose trace: every hook event as a
  config-paste-ready `[input] chord -> window` line — the **key-finder** — plus
  step firing and reloads). Even when `off`, config-load failures surface
  in-session through the tray (error row + icon flash + balloon) — the log is an
  optional diagnostic, never the only error path.
- **Runtime directory** — config sources are resolved by `ConfigSources`
  (C-7): settings file = `--settings-file` else `ExeDir\slitherin.settings.json`;
  profile dirs = exe dir (always first) → `profile_dirs` entries (relative to
  the settings folder) → `--profile-dir` (CWD-relative); first match wins;
  watch roots = settings folder + every profile dir. `%SLITHERIN_DIR%` is
  retired. External icons stay next to the exe.
- **Run-at-startup** is a carried HKCU `Run` key value, read/written by the tray
  toggle — it is not part of the settings JSON.
- **Second instance** — a duplicate launch signals the running instance via a
  named `EventWaitHandle`; the primary pops an "already running" balloon (gated
  by the Notifications toggle) and logs. The duplicate exits quietly. (Formerly
  "bail quietly" — confusingly silent.)

## 17. Testing

The suite (`tests/SlitherIn.Tests/`, `dotnet test tests\SlitherIn.Tests -c
Release`) covers the UI-free core end to end. Current: **182 tests green, 0
failed, 0 skipped, 0 warnings** (rev-20260912-19). Time is faked via a
`FakeClock` (cooldowns/countdowns and parked-runtime behavior are driven by
`WallClock`).

Coverage map:
- Config — loading, normalization, validation (incl. the viiper + mouse hard
  errors), canonical key parsing: `ModelsTests`, `KeyNameTests`,
  `NormalizerTests`, `ValidationTests`, `LoaderTests`, `LogTests`,
  `ConfigSourcesTests` (C-7 resolution rules/dedupe/rebuild), `CliArgsTests`
  (C-7 CLI parsing).
- Step vocabulary, wrappers, pacing — `NormalizerTests` (wrapper + pacing
  expansion), `PacingTests` (fixed + cooldown sequences, pass boundaries),
  `LoopSpecTests` (count + held-only `continue_on_release`).
- Engine — workflow state machines, tap/hold counts, release rules, abort
  tiers, parking: `WorkflowTests` (12), `CancellationTests`, `DispatcherTests`
  (6, recording engine), `ProfileCatalogTests`.
- Translation — `KeyTranslationTests` (VK ↔ canonical via `KeyMapping`),
  `HookEventModelTests` (ModifierBitmap feed + canonical chords),
  `WindowTargetTests` (exe+title wildcards).
- Composition — `CompositionTests` (boot-with-broken-profile, reload
  abort/disarm, keep-old-on-error, VIIPER hard-fail, window gate includes
  injection, AUTO switching via foreground provider + FakeClock, C-6 any-member
  chord release).
- VIIPER — `ViiperTests` (mock TCP server speaking the real protocol +
  `HidKeyMap` table asserts + report/reconnect/hard-fail), plus an `[Explicit]`
  live smoke test (`LiveServer_Smoke`) for environments with VIIPER present.
- The UI shell (`App`, tray) is intentionally not unit-tested — its correctness
  is the manual REV/behavior check done by `build.ps1` after every build.

## 18. Build & toolchain

See `BUILDING.md` for the full runbook. Essentials:

- **.NET SDK 10** (10.0.401) building a **modern SDK-style csproj** targeting
  `net48` (`net4.8-windows`), so users still install NOTHING (.NET Framework 4.8
  is built into Windows 10/11). Deliberately NOT a .NET 8/10 runtime target.
- Reference assemblies come from the
  `Microsoft.NETFramework.ReferenceAssemblies` NuGet package (auto-downloads at
  first build; no Developer Pack install needed).
- Packages: `System.Text.Json` at runtime (latest 10.0.x supports .NET
  Framework 4.6.2+/netstandard2.0, works on 4.8); tests use `Microsoft.NET.Test.
  Sdk` + NUnit + adapter.
- `build.ps1`: stop running slitherin (the EXE-lock — a running instance blocks
  `dotnet build` with MSB3021/3027) → `dotnet build -c Release` → parse `REV`
  from `src\Program.cs` → scan the built EXE bytes (UTF-16 decode, `Contains`)
  to verify → relaunch. The manual `dotnet build` loop must remember the EXE-lock.
- Layer rule (enforced): anything under `Core` stays free of WinForms, P/Invoke,
  and `System.Drawing`; `Beeps` (MCI/P-Invoke) lives in `Ui`.
- The dispatcher is cooperative: no `Thread.Sleep` long enough to starve other
  macros; waits are cancellable.
- Type-per-file + namespaces (`SlitherIn.Core.Config/.Engine/.Target/.Diagnostics`,
  `SlitherIn.Hook`, `SlitherIn.Input`, `SlitherIn.Ui`) mapped 1:1 onto the
  architecture — NOT the old sharded `static partial class` style.
- `-selftest` headless entry (borrowed from DdoHotbar) exercises parser +
  schedulers without a message pump — verification without a debugger.

## 19. Decisions register

Locked, built, tested — nothing "open" remains.

- **Feature scope is CLOSED**: key remapping, recorder, text/command send,
  delay jitter/humanization, screen-position clicks, wheel scroll, and any other
  unagreed feature are OUT and will not be re-raised. They re-enter only if the
  user explicitly asks.
- **Input vocabulary**: locked to the schema (key chords incl. numpad vs top-row,
  mouse buttons, side buttons; sound; wait_ms; fire_after_ms; hold_for_ms;
  cooldown; wrapper).
- **Trigger cues**: a `trigger_behavior` cue field was proposed in the record
  but never carried into code — sound `steps`/`toggle_sound` are the cue
  mechanism. `fire_after_ms` is the field actually implemented for held
  triggers (the record spelled it `hold_ms`; code calls it `fire_after_ms`).
- **Parallelism**: single cooperative `Dispatcher`.
- **Settings/profiles split**: settings = `slitherin.settings.json`, profiles =
  `slitherin*.json`; CLI args never override the schema, only the locations.
- **Config-source precedence (C-7)**: the exe dir is ALWAYS the first profile
  dir (self-contained portability beats settings files that forgot their dirs);
  settings-held `profile_dirs` come next, CLI `--profile-dir` last; first match
  wins per filename. `%SLITHERIN_DIR%` env override is RETIRED — no
  environment-variable dependence for config location; the settings file and CLI
  args cover the need. `--settings-file` may point anywhere; profile dirs
  resolve relative to CWD (documented, since ExeDir is already a member of the
  search).
- **Engine unavailable**: HARD-FAIL with alert, no silent SendInput fallback
  (`ActiveProfileBlocked`).
- **VIIPER mouse output**: hard VALIDATION error for a viiper profile with mouse
  output; engine drops such a chord whole as the defense layer for the
  global-engine-viiper case validation can't see.
- **Tray UX surface**: tray-only for v1; future options window is a new Shell
  presenter.
- **Serialization**: System.Text.Json with `JsonCommentHandling.Skip`; the
  hand-rolled `StripJsonComments` pre-pass is retired.
- **Second instance**: signal + balloon, duplicate exits quietly.
- **Build**: .NET SDK 10 → net48, reference assemblies from NuGet (see §18).
- **Workspace**: new structure in `D:\SlytherInNew\`; `D:\SlitherIn2\` untouched
  (read/copy only).
- **"Keep ≠ port as-is"**: carried features are re-implemented cleanly in the
  new structure — behavior survives, not verbatim code.

## 20. File-by-file inventory

`src/` (program files; `obj/` build artifacts ignored):

**Entry / composition (`SlitherIn`):**
- `Program.cs` — STA entry; `REV` const; single-instance guard; boots `App`
  with parsed CLI args, runs the message loop.
- `CliArgs.cs` — internal CLI parser: `--settings-file <path>` (space or `=`
  form) and repeatable `--profile-dir <dir>`; unknown/malformed switches are
  ignored. Exposed to tests via `InternalsVisibleTo`.
- `App.cs` — thin WinForms shell: hooks → `Composition.HandleInput` /
  `HandleModifierState`, 10 ms tick → `Composition.Tick`, watcher with UI-thread
  marshal, `SuppressFor` before every settings save, Beeps on `CueRequested`,
  tray rebuild on `Changed`, persist tray toggles. No business logic. Re-roots
  the watcher from `Composition.Sources` on boot and reload.
- `Startup.cs` — single-instance mutex + second-launch signal, run-at-startup
  (registry), `ExeDir()` (= `AppDomain.CurrentDomain.BaseDirectory`). The old
  `%SLITHERIN_DIR%` override is retired.
- `Composition.cs` — the testable orchestration: catalog/dispatcher/engines
  (engine factory + cache, swap per profile), the locked routing order (§13),
  reload semantics (abort + disarm + fresh catalog; keep-old-on-broken-active),
  VIIPER hard-fail (`ActiveProfileBlocked`), AUTO-mode foreground polling
  (250 ms throttle), the per-slice window gate snapshot (§12). Built from a
  `ConfigSources` instance (owning `Sources` property); ctor + `Reload` load the
  real settings then `Rebuild()` the sources so `profile_dirs` changes stick.
  Public, UI-free.
- `Core\Config\ConfigSources.cs` — pure resolution of settings path + ordered
  profile dirs (dedupe + always-exe-first), `WatchRoots`, `SettingsFileName`,
  `Rebuild(settings)`, `ResolveProfilePath(file)`. Public sealed; UI-free.

**Core — Config (`SlitherIn.Core.Config`, pure):**
- `Models.cs` — POCOs mirroring the config (`Settings`, `Profile`, `Macro`,
  `StartSpec`, `LoopSpec`, `Schedule`, `Wrapper`, `ToggleSound`/`SoundSpec`).
  Steps are shape-typed raw `List<JsonElement>`; typed step classes exist ONLY
  in the Normalizer output.
- `KeyName.cs` — canonical key vocabulary + `Chord Parse(string)`; single source
  of truth for names; top-row vs `Numpad*`; `KeyName.IsMouseToken`.
- `Normalizer.cs` — one-pass canonicalization → `NormalizedProfile`; classifies
  steps by shape, resolves wrappers (no stacking), folds pacing defaults.
  One config step = ONE logical action. Strict (throws on unbuildable config).
- `Validation.cs` — schema/structural errors → `ValidationResult`; cross-profile
  ambiguous-window-target warning (warning, never a blocker).
- `Loader.cs` — `LoadSettingsFile(path)` + multi-dir `LoadProfiles(dirs,
  settingsFileName)` / `LoadProfilesDetailed`; `schema_version` gate; never
  throws into the UI (`LoadOutcome<T>`). Missing dir non-blocking; first dir
  wins per filename. **Filenames locked: settings = `slitherin.settings.json`,
  profiles = `slitherin*.json`. Control paths: `SettingsFileName`,
  `LoadSettingsFile/SaveSettings(path, settings)`.**
- `Watcher.cs` — multi-root filesystem watcher (settings folder + every profile
  dir, nonexistent dirs still tracked), 300 ms debounce, artifact filtering,
  `SuppressFor(path, ms)` self-write guard, custom-named settings file interest;
  raises `ReloadRequested`.

**Core — Engine (`SlitherIn.Core.Engine`, pure):**
- `WallClock.cs` — abstract time source (`NowMs`, `Stopwatch` default);
  injectable (`FakeClock` in tests).
- `Cancellation.cs` — cooperative cancel token per macro (kill / global abort /
  per-macro `abort_keys`); waits & sounds check it.
- `LoopSpec.cs` — pure loop semantics (C-5): `count` always honored; 
  `continue_on_release` held-only, explicit-or-false.
- `Workflow.cs` — the ONE start→run→end state machine (§9).
- `IPacing.cs` / `FixedPacing.cs` / `CooldownPacing.cs` — policy interface +
  two implementations (§9).
- `Dispatcher.cs` — cooperative dispatcher: advances every registered workflow in
  small slices, serializes engine `SetState` calls, held-chord tracking +
  `hold_for_ms` releases, settable `Engine`, `DetachAll`/`ReleaseAll`, park on
  detach, resume on re-attach.

**Core — Target (`SlitherIn.Core.Target`, pure):**
- `WindowTarget.cs` — pure exe+title wildcard match; pattern → regex once.
- `ForegroundInfo.cs` — `Hwnd`/`ProcessExe`/`Title` snapshot at event time.
- `ProfileCatalog.cs` — load+validate all profiles, `Resolve(ForegroundInfo)`
  for AUTO, owns each profile's PARKED runtime, `KeepActive` on broken active
  file. Only the active profile is wired to the dispatcher.

**Core — Diagnostics (`SlitherIn.Core.Diagnostics`, pure):**
- `Log.cs` — off/on/debug levels; `KeyEvent(...)` is the key-finder.

**Hook (`SlitherIn.Hook`, Win32):**
- `Win32.cs` — all P/Invoke in one place: hook install/uninstall,
  `GetForegroundWindow` + title/exe extraction, key/mouse structs, scan-code
  helpers (incl. the mouse-UP constants `WM_LBUTTONUP`…`WM_XBUTTONUP`).
- `InputEvent.cs` — event model: `Kind`, canonical `Chord`, `ForegroundInfo`.
- `HookModel.cs` — pure raw vkCode/mouse-message + modifier state → canonical
  `Chord` + kind; unknown keys / lone modifiers → no event;
  `ModifierBitmap`/`ModifierFlagOf` fold a released modifier's own up/down into
  the sampled bitmap (C-6).
- `LowLevelHooks.cs` — `WH_KEYBOARD_LL` + `WH_MOUSE_LL` event source; passthrough
  always `CallNextHookEx`; injected events skipped; per-event foreground
  snapshot; `ModifierState` event (C-6); clean uninstall; `Installed` health
  flags; `SnapshotForeground` public static.

**Input (`SlitherIn.Input`):**
- `IKeyEngine.cs` — the seam: `Available` / `Initialize()` / `SetState` /
  `ReleaseAll()` / `Dispose()`.
- `SendInputEngine.cs` — canonical → VK/scancode; both `wVk`+`wScan`, extended
  keys, fixed modifier press/release order; diff-based `SetState`; mouse events
  for button steps; never unavailable.
- `ViiperEngine.cs` — VIIPER HID TCP port (§10) with hard-fail surface, engine
  launch (`FindUsbipDir()` prepended to PATH), throttled self-heal, report cap
  at 6 usages, whole-chord mouse drop, `ReleaseAll` → empty report, `Dispose` →
  `bus/remove` + close (viiper.exe itself left running — it may be a shared
  system server).
- `HidKeyMap.cs` — canonical token → USB-HID keyboard usage + `ModifierFlags` →
  HID modifier byte (the Alt↔Shift swap, table-tested).
- `KeyMapping.cs` — ONE canonical⇄Win32 table shared by detection & injection
  (letters/top-row digits/F1-24/named/mouse; numpad ≠ top-row via distinct
  VK + scancode; modifiers are never chord tokens).

**UI (`SlitherIn.Ui`):**
- `TrayHost.cs` — `NotifyIcon` lifecycle, icon states (normal ⇄ alert + flash),
  balloon text; mirrors the Notifications setting.
- `TrayMenu.cs` — menu model each refresh (§15); `TwoToneRenderer`.
- `MenuActions.cs` — handlers (§15).
- `Beeps.cs` — MCI alias `slitherin_audio`, square-wave WAV synth into temp,
  PlayFile/Cancel, `PlayCue(SoundSpec)`; P/Invoke deliberately outside Core.
- `Ui\Icons\` — `icon.ico`/`alert.ico` (embedded base64 from the old source),
  embedded in the EXE; external files override.

**Tests (`SlitherIn.Tests`):** see §17.

## 21. Gotchas & lessons (the cookbook)

Hard-won, per the per-stage close-out rule ("every stage close-out appends what
went wrong or was non-obvious and the fix that landed — not just green counts").
Retro-notes for stages 1–4; live notes thereafter.

- **Stage 1 (Models + KeyName)** — no code setback; the real work was the
  vocabulary LOCK (bare digits = top-row, numpad explicit, mouse button names,
  fixed modifier order). A design call, not a bug.
- **Stage 2 (Loader/Normalizer/Validation)** — `Loader.Deserialize` never
  assigned `outcome.Config` on the success path → every valid file silently
  REJECTED. Six Loader tests caught it. Fail helpers made generic (`Fail<T>`).
- **Stage 3 (Cancellation + Workflow)** — (a) state enum authored as
  `WaitingToFire` while the docs say `AwaitingHold` — renamed before close-out
  (docs-must-match-reality rule); (b) the EXE-lock: `dotnet build` fails
  MSB3021/3027 while a slitherin instance runs — `build.ps1` handles it, the
  manual loop must remember (recurs in nearly every stage).
- **Stage 4 (Pacing + Dispatcher)** — the pass-boundary contract fight. Tests
  assumed (i) a trailing `Wait` after the final step and (ii) `Wait` during a
  cooldown rescan; the first implementation returned `RunComplete` at scan
  exhaustion. Settled: `FixedPacing` honors the final step's delay at the pass
  boundary; `CooldownPacing` documents scan → `Complete` → rescan. Mechanicals:
  NUnit 4 dropped `CollectionAssert` (repo style is
  `Assert.That(x, Is.EqualTo(y))`).
- **Stage 5 (Target/catalog + AUTO)** — one wrong EXPECTATION (mine): the first
  assert expected `ResolveOwner` to return profile B, but B's target is exe AND
  title and the title constraint correctly rejected the window → the behavior
  was right; the test now asserts the null. Also: `Loader.LoadProfiles` was
  refactored to delegate to `LoadProfilesDetailed` (byte-identical, tests
  stayed green untouched); the `ParkedRuntime`/`MacroState` scaffolding was
  DROPPED (parked runtime IS the profile's Workflow list); ambiguity rule: two
  targets overlap unless a field BOTH sides carry differs — always a warning,
  never a blocker.
- **Stage 6 (Hook + translation)** — two real bugs in the translation table,
  both caught first-run by the tests: (a) `KeyMapping.VkOf` uppercased the token
  but the switch arms were title-case, so ALL named/numpad/mouse tokens resolved
  to VK 0 — switch on the raw token; (b) the `ByToken` dictionary was
  `Ordinal` while the lookup pre-uppercased input — `OrdinalIgnoreCase`, no
  uppercasing. Also a wrong TEST of mine (asserted 0x45 — VK_E — unmapped; it's
  actually mapped; now asserts 0x88). `Win32` lacked mouse-UP constants — added.
  Reference match: `ReplicatingAHKInCSharp.md` records numpad VKs 0x60–0x69 and
  mouse 0x01–0x05; `KeyMapping` matches both.
- **Stage 6 live-run (REV -09→-10)** — integration bugs the unit tests couldn't
  catch: (a) the settings file was named `slitherin-settings.json` (hyphen) but
  `Loader.SettingsFileName` is `slitherin.settings.json` (dots) → settings
  silently failed to load; renamed. Locked the filenames so this can't drift. (b)
  `new NotifyIcon()` without an `Icon` shows NOTHING — `Icon = SystemIcons.Application`
  fixed it (now the custom ico). Plus: the single-instance mutex makes a second
  launch silently exit — looks exactly like "the exe does nothing"; now signals
  the running instance instead.
- **Stage 7 (Tray/composition)** — installed the tray look on the new menu
  (§15). Deleted the vestigial `TrayHost.ExitRequested` event to hold 0 warnings.
- **Stage 9 (VIIPER port)** — two real finds: (a) **modifier bit order** differs
  between config and HID — Ctrl=1/Alt=2/Shift=4/Win=8 (config) vs Ctrl=0x01/
  Shift=0x02/Alt=0x04/Win=0x08 (HID) — `Alt↔Shift` are SWAPPED; `HidKeyMap`
  maps explicitly and tests assert `Shift+Alt → 0x06` so it can never land
  silently. (b) a **race made the loss-detection test flaky**: resetting
  `_lastRebuildAttempt = 0` after a successful rebuild defeats the 2 s throttle,
  so a kill could rebuild inside one 10 ms poll and the transient
  `Available=false` became unobservable. Removal makes the throttle real.
- **Stage 9 live verification (real server)** — the mock couldn't expose this:
  the user's viiper was auto-started WITHOUT `usbip` on its PATH, so `bus/N/add`
  returned HTTP 409 (`exec: "usbip": executable file not found in %PATH%`) and
  no device was created. The engine's `EnsureServer` (launch with `FindUsbipDir()`
  prepended to PATH) fixes it ONLY when IT launches the server — a pre-existing
  pingable server is reused as-is and stays broken. Fix: stop the stale instance,
  let the engine relaunch viiper with the correct PATH. **User takeaway: viiper
  auto-started at logon must have usbip on its PATH (or be relaunched by a
  viiper-profile engine) — otherwise a viiper profile blocks with the hard-fail
  alert.** Re-verified same session (REV -16→-17): `viiper.exe` running
  (PID seen 32720), `usbip.exe` at `C:\Program Files\USBip\`, the usbip-win2
  driver present (`USBip 3.X Emulated Host Controller`, `ROOT\USB\0000`), and
  `LiveServer_Smoke` re-run with the real server — **passed**. Live VIHPER
  verification is CLOSED — the full loop is proven: engine → VIIPER TCP →
  usbip-win2 → Windows → our own `WH_KEYBOARD_LL` (the hook logged the virtual
  keyboard's `F13`).
- **C-4 (mid-run window gate)** — placement review: the check must live at the
  workflow→pacing seam (gating at the injection seam advances pacing then drops
  the fire; inside a pacing policy couples window policy into pacing). Two
  things bit: (a) existing Composition tests set the foreground only ON THE
  EVENT while the mid-run gate reads the tick-time foreground — those broke
  until the live `_foreground` field was set too; (b) FixedPacing's pass
  boundary COMPLETES on the crossing tick and the next pass's first step fires
  on the FOLLOWING tick (one `PacingDecision` per slice) — the new test asserted
  both in one tick. Efficiency fix (-15→-16): snapshot the gate bool once per
  slice and close the lambda over it (not one Win32 foreground snapshot per
  workflow per slice).
- **C-5 (continue_on_release)** — the OLD tests faked the removal of the
  `triggerIsHeld` parameter so badly that 3 LoopSpec + 1 Workflow test codified
  the OLD behavior (tap + explicit false *stops* on natural key-up) and failed
  the new build. **Lesson reinforced: stale tests don't just annoy, they LOCK IN
  the bug — re-assert the new contract in the same change.**
- **C-6 (chord-release orphan)** — a chord trigger whose modifier released first
  could never deliver `TriggerUp` (modifiers produce no canonical chord event,
  and the key-up resolves to a different unmatched chord) → orphaned held run
  forever. Fixed by the raw modifier-bitmap feed (`ModifierState` event). Design
  note: chose a separate event over stacking the bitmap on `InputEvent.Chord`
  (whose `Key`-typed payload can't represent "modifiers only" without a
  sentinel). Tests must drive BOTH feeds (InputEvent for the main key +
  `HandleModifierState` for the modifier), and the gate is only consulted for
  STARTING, so the release path needs no foreground fixture — which is also why
  it could not be expressed through `InputEvent` at all.
- **C-7 (config sources)** — the `%SLITHERIN_DIR%` env override was retired in
  favor of a declarative source list: `ConfigSources.Resolve(exeDir,
  cliSettingsFile, cliProfileDirs, settings)` with exe-dir-always-first +
  `profile_dirs` + `--profile-dir` + first-wins dedupe. Three things bit during
  implementation: (a) PowerShell's `Replace` on the ProfileCatalog tests file
  flattened its CRLF newlines → the file had to be rewritten whole (use the
  editor tools, not inline shell string surgery); (b) `?? Array.Empty<string>()`
  for `Settings.ProfileDirs` failed to compile — the non-null property needs a
  `List<string>` default, not an array; (c) the watcher's old single-root
  assumptions leaked into `Watcher` — it required a full rewrite to multi-root
  with `SetRoots`, and Composition's ctor had to stop resolving paths itself
  (bootstrap: resolve with `settings:null`, then `Rebuild` after the real
  settings load so `profile_dirs` take effect). The running-instance EXE-lock
  bit again (MSB3026 retries until the old exe is killed).

## 22. Change history (C-series, REV trajectory)

Stage/green/REV trajectory: stages 1–5 (→128 tests-ish, REV -01..-08),
Stage 6 hook/SendInput (126 tests), Stage 6 live-run fixes (-09→-10), Stage 7
tray/composition (132 tests, -10→-11), tray UX continuity (-11→-12),
second-instance signal (-13), Stage 9 VIIPER (148 tests, -13→-14), C-1/C-2/C-3
(150 tests, -14→-15/16), C-4 mid-run gate (-15→-16), C-5 + C-6 (163 tests,
-16→-17), C-7 config sources (182 tests, -17→-18), C-7.1 RunAtStartup field
removal (182 tests, -18→-19).

Current state (2026-09-12, REV `rev-20260912-19`): `dotnet build -c Release` →
0 errors, 0 warnings; `dotnet test -c Release` → **182 tests, all passed, 0
skipped, 0 failed**; full tray composition app with the old-app tray look, live
hooks + key-finder, profile switcher, macro triggers, toggles, watcher
auto-reload, alerts + beeps, run-at-startup, second-launch notification, the
VIIPER HID backend (bus/device setup + keyboard report stream + hard-fail/
self-heal), and:

- **C-1** — (any unresolved stage-7-era item; keep-old on broken active).
- **C-2** — normalization-failure keep-old: an unknown profile-level
  `input_engine` used to crash the load path; a failed normalize now keeps the
  previous config.
- **C-3** — SendInput wVk-only gap (PLAN audit 4.1): both `wVk` + `wScan` and
  the extended-key flag added (the reference `ReplicatingAHKInCSharp.md` rule).
- **C-4** — mid-run window gate (PLAN audit 4.3, option (b)): gate covers
  trigger AND injection with skip-but-kept-ready catch-up (§12).
- **C-5** — `continue_on_release` (PLAN audit 4.4): `count` always honored;
  flag bound to held triggers only; `triggerIsHeld` parameter deleted.
- **C-6** — chord-release orphan (PLAN audit 4.5-adjacent): any-member release
  via the `ModifierState` bitmap feed; exact-chord fast path untouched.
- **C-7** — config sources (replaces `%SLITHERIN_DIR%`): `ConfigSources` +
  `CliArgs` + multi-dir `Loader`/`Watcher`; settings `profile_dirs`; exe-dir
  always first; per-filename first-wins; watcher re-rooted by `Composition` on
  boot and reload (§5/§7/§8/§16).
- **C-7.1** — the inert `Settings.RunAtStartup` JSON field was removed
  (registry holds run-at-startup; the field was never read and a stray value
  leaked into every `SaveSettings` write). `run_at_startup` stays out of the
  Settings model by design; `ModelsTests` assertion dropped with the same edit.

Earlier audit items (PLAN §4) resolved as intended: 4.5 (`GetAsyncKeyState` per
event + unused `LLKHF_ALTDOWN`) and 4.6 (doc drift fixed in the same
apply-sync).

Docs trajectory: user-facing `README.md` + standalone `ARCHITECTURE.md` landed
2026-09-12; `PLAN.md` retired as the stage plan and became the decision record
same day; **2026-09-12 consolidation: PLAN/CHANGES/DESIGN/PROJECT-STATE merged
into THIS document; originals archived in `old-docs/` and replaced with
pointers.**