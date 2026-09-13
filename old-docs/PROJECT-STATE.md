# PROJECT-STATE.md — AI working memory

> Purpose: this is the assistant's working-memory file. It captures where the
> SlitherIn project stands and the agreed redesign direction, so a fresh session
> can rebuild context quickly. **Keep it current** — update it whenever a design
> decision lands or the code state materially changes. Read it before doing
> anything in this repo. Companion files: `AGENTS.md` (build/config/behavior
> invariants) and `README.md` (user-facing product docs).
>
> Last updated: 2026-09-12.

## TL;DR

SlitherIn is a working Windows tray tool that emits real USB-HID keystrokes (via
VIIPER) for gaming macros. The user is **happy with the functionality but not
the implementation** — it's messy and bloated. We're redesigning the internals
around ONE unified macro shape (a sequence is a cycle that runs once), a
declarative, closable config schema, and no control-flow-in-config. The redesign
is being IMPLEMENTED in the new root `D:\SlytherInNew\` (`.NET SDK 10` →
`net48`, so app users install nothing) — see "Redesign implementation status"
below for the per-stage inventory mapped onto `PLAN.md`. The user leads the
design; I challenge assumptions and ask questions, but don't take over the
conversation.

## Confirmed ground truth (do not re-litigate)

- The user's AHK v2 script (hold LButton → cycle hotbar actions off cooldown,
  Pause to arm/disarm, beeps 1500/1000 Hz) **works against current DDO**.
  `SendInput` reaches the active window. This invalidated the original claim that
  "SendInput can't reach DDO" — the premise that spawned the whole VIIPER/usbip
  path. Empirically, current-client DDO accepts SendInput for single-digit keys
  and Ctrl+digit hotbar switching.
- The script's **active** actions (the only ones to replicate), in order:
  1. `0` — cooldown 3000 ms
  2. `6` — cooldown 3000 ms
  3. `7` — cooldown 10000 ms
  4. `8` — cooldown 15000 ms
  5. `7` — cooldown 15000 ms, on **hotbar 6**
  Prior generated configs wrongly included commented-out/example actions
  (`LAlt`, `5`, `9`). Corrected active config = the list above.

## Current implementation (as-built, pre-redesign)

- C# .NET Framework 4.x, WinForms tray app, classic MSBuild, no NuGet, no tests.
  7 `static partial class` files in `src/`, compiled by `SlitherIn.csproj` into
  `slitherin.exe` (repo root). Build via `build.ps1` (stop app → MSBuild →
  REV-verify → relaunch). REV convention: `rev-YYYYMMDD-NN` at top of
  `src/SlitherIn.App.cs`, bump every code change.
- File map: `SlitherIn.App.cs` (Main, `PollLoop` 15ms god-loop, hot-reload,
  `FireSequence`, cycle runner, logging), `Config.cs` (~1450 lines: hand-rolled
  comment-stripping + `JavaScriptSerializer`, models, validation), `Native.cs`
  (user32, abort hook/watchdog/RegisterHotKey), `Viiper.cs` (TCP HID bus client),
  `Sound.cs` (MCI tones/files), `Idler.cs` (idler engine + window watch),
  `Tray.cs` (menu, balloons).
- Config: `slitherin-settings.json` (global: abort combo, default delay, debug,
  idler, notifications, window_check, active_profile) + profiles
  `slitherin.json` / `slitherin.<N>.<label>.json`. All hot-reload (~0.3s) via a
  watcher; invalid file keeps old config (writes the error to the log only with
  `debug:true`). Profile state is created fresh on each reload (so armed cycles
  reset to disarmed on reload).
- A macro is either a **sequence** (`steps`) or a **cycle** (`cycle` block —
  the newer cooldown scheduler port of the AHK script). `cycle` fields today:
  `arm_delay_ms` (TheDelayBeforeStarting), `animation_delay_ms` (AnimationDelay),
  `primary_hotbar`, `tick_ms`, `actions[]` (`keys`, `cooldown_ms`, `hotbar`,
  `delay_ms`), plus `trigger_key` (mouse allowed) and `toggle_key` (e.g. Pause).
  The cycle also exists as sample `sample-json/08-cycle.json` and as the current
  active profile `slitherin.json`. Idler is a separate subsystem (tray Idler
  menu; a macro can carry an `idler` block to be selected as the idler).
- Detailed build/config/behavior invariants live in `AGENTS.md`; the config
  language and samples are documented in `README.md`.

## Why the redesign (agreed problems with current implementation)

1. `Macro` conflates config and runtime state (`CycleArmed`, `CycleRunning`,
   `TogglePrevDown`, `CycleLastUsed` next to parsed config).
2. `HasCycle` bifurcates one class into two behaviors (sequence vs cycle) with
   two dispatch systems (poll loop + runner thread) coordinating via skips.
3. `PollLoop` is the app: watcher, cycle toggles, triggers, abort, idler, window
   checks, VIIPER device keep-alive are all `if`s in one 15ms loop — untestable,
   uncomposable.
4. Input delivery hardwired to VIIPER (the premise is now doubtful; SendInput
   works). Should be an abstraction with swappable backends.
5. Hand-rolled JSON parsing + validation scattered through a 1450-line file.
6. Thread-per-macro timing + shared mutable flags (`macroActive`/`macroHeld`) —
   subtle-race territory.
7. Hardcoded hotbar logic (primary_hotbar + per-action hotbar switching) — the
   user wants this GONE; hotbar switching should be expressed as ordinary chord
   actions in the cycle (`Ctrl+6`), not hardcoded semantics.

## Redesign direction (agreed)

- **Core functions (user's framing):**
  1. Execute a series of keystrokes and/or mouse clicks when triggered (standard
     macro / sequence).
  2. Execute a cycle of keystrokes — repetitive, fixed count, ongoing, or until
     aborted.
- **One unified shape, not multiple flows.** A sequence is a cycle with
  repeat-count 1. Do NOT build separate macro/cycle/idler flows. One execution
  engine up front; on top, sample recipes act as discoverable "kinds" (no schema
  split). The idler is a cycle policy (armed start, no external trigger), not a
  subsystem.
- **5 dimensions ≈ user's mental model (as refined):** `start` (trigger/toggle/
  hold), `loop` (a number + one flag), `steps` (the body), `schedule` (pacing).
  "How it ends" was collapsed out into universal behavior — see the schema
  section below for the exact vocabulary we landed on.
- **`stop` dimension was DROPPED.** Release behavior is universal: release a held
  trigger → halt; count reached → ends; toggle-off / abort / kill → always halt.
  Explicit `lose_focus` and `timeout_ms` options were considered and rejected as
  redundant (window filter already gates starts; kill tiers already cover runaway
  cycles).
- **Abort tiers (locked 2026-09-11):** three escalation scopes for stopping macros.
  Without an unconditional master kill, an `ignore_abort` macro could run away
  with no off-switch. Tiers:
  1. `abort_keys` — **per-macro, optional**: stops only that macro, mid-flight.
  2. Global default abort — **settings-level**, inherited by any macro that
     doesn't define its own `abort_keys`. Pressing it stops every running macro
     (default matches current behavior, so backwards-compatible).
  3. `ignore_abort: true` — **per-macro, opt-in**: this macro ignores the global
     default abort entirely (e.g. a 6-minute rebuffer cycle).
  4. `kill_keys` — **settings-level, unconditional**: stops literally everything,
     including `ignore_abort` macros. The escape hatch that can never be
     opted-out of.
- **Abort detection & cancellation (decided 2026-09-11):** the old code has THREE
  redundant detectors (low-level hook + a 5 ms `GetAsyncKeyState` polling
  watchdog + `RegisterHotKey`) around one global `abortRequested` flag — bloat
  born of defensive trial-and-error. The redesign uses **one low-level keyboard
  hook as the entire detection path** for every chord (triggers, toggles,
  per-macro abort, global abort, kill); the watchdog and `RegisterHotKey` are
  deleted. The global flag is replaced by **per-macro cooperative cancellation**:
  kill signals all macros, global abort signals all except `ignore_abort`
  macros, a per-macro `abort_keys` signals only that macro. Waits are
  cancellable and playing sounds cut on cancel.
- **Config stays declarative — the hard line.** No control flow in config:
  no conditionals on runtime state, no user-visible variables, no macro
  composition, no loop-until-state, no computed values. That = reinventing a
  worse AHK. New feature ⇒ build logic in code, expose as a clean declared
  option (extends a dimension, or consciously adds a new named dimension,
  validated + sample-documented, old configs keep loading). Add a top-level
  `schema_version` integer to the config to make forward-compat manageable.
- **Config format: JSON, strict** (the user's choice — well-known, debuggable).
  No shorthand dialects (`{ "sound" }` and `"7"`/`"sound"` string steps were
  proposed and REJECTED — a dictionary of shorthand gets cumbersome and breaks
  standard parseability). Defaults are expressed as empty/omitted values, e.g.
  `{ "sound": {} }` = default tone (500 Hz / 1s, matching current Sound.cs).
- **Config schema (agreed 2026-09-11, full vocabulary so far):**

  Global (slitherin-settings.json, default for all profiles):
  - `input_engine` — `"sendinput"` (default) | `"viiper"`

  Profile (top-level):
  - `name`
  - `input_engine` — **optional override** of the global value; set it only when
    this profile differs from global (see Input backend section)
  - `default_delay_ms` — optional; paces between every step
  - `macros` — array

  Macro: `start` / `loop` / `schedule` / `steps`
  - **start** (all optional except trigger presence is a schema rule):
    - `trigger` — flat chord string, same vocab as keys: `"Ctrl+K"`,
      `"Shift+RButton"`, `"LButton"`, `"RButton"`. Silent by default.
    - `trigger_behavior` — optional cue when the trigger fires. Exactly like a
      sound step (`{ "sound": {} }`, tone with overrides, or file).
    - `hold_ms` — optional; the trigger must stay held N ms before it counts
      (the old `arm_delay_ms`). Omitted = fire on press.
    - `toggle` — optional arm/disarm key (e.g. `"Pause"`); off always stops.
    - `toggle_sound` — optional `{ "on": <sound>, "off": <sound> }`. Both `on`
      and `off` are optional; a PRESENT slot triggers the default beep
      (500 Hz / 1s); an omitted slot or whole block = no sound for that state.
      Customization via frequency/file overrides, same spec as a `sound` step.
  - **loop** — `{ "count": N, "continue_on_release": bool }`, a number + one
    flag:
    - `count` omitted = 1 (once). `count` N = N times. `count -1` = forever.
      `count` is ALWAYS honored at every value, both tap and held triggers
      (C-5 decided 2026-09-12; deleted "Ignored when count = 1").
    - `continue_on_release` — governs held triggers (`fire_after_ms`) only; tap
      triggers always run to completion (release never cancels). Default:
      held ↦ `false` (release stops), tap/combo ↦ `true` (release irrelevant;
      kept for config symmetry). Explicit override only in the non-obvious case.
      **Chord-release (C-6 decided 2026-09-12):** release is defined as ANY
      chord member leaving the down-set, not exact-chord match; a modifier
      release also terminates the run, matching single-key intuition.
    - (Drafting note: `while_held` was the earlier name; user rejected it for
      assuming a sustained press — a loop can be kicked by a plain combo. Final
      name `continue_on_release`. Keep it for now, may revisit.)
  - **schedule** — optional; omitted = fixed pacing via `default_delay_ms`.
    Cooldown scheduler: `{ "mode": "cooldown", "fire_delay_ms": 700 }` — each
    pass fires EVERY step whose cooldown has expired, in list order, with the
    delay (default 700 ms, the old `animation_delay_ms`) between consecutive
    firings, then rescans. One delay paces everything; no one-per-pass mode.
    A step without `cooldown_ms` is ALWAYS ready and fires every pass.
    `tick_ms` is an internal detail, GONE from config.
  - **Timing vs focus (decided 2026-09-11; IMPLEMENTED by C-4 2026-09-12):**
    cooldowns/countdowns run on WALL-CLOCK (`Environment.TickCount`), never
    paused by focus changes. Focus gates the trigger (starts) AND the
    INJECTION: if a macro is window-gated and the target is not foreground when
    a ready step would fire, the press is skipped-but-kept-ready and fires on
    the next focused opportunity (catch-up); the next countdown starts from the
    actual firing time (e.g. the 6-min rebuffer keeps counting in the
    background; returning to the game fires the ready `R` immediately).
  - **steps** — ordered array of step objects:
    - `{ "keys": "7" }` — press+release (default patterns apply)
    - `{ "keys": ["Ctrl+6", "7"] }` — a LIST of chords fired in sequence as ONE
      logical action (e.g. "bar switch" then press — the user defines what that
      combo is; no `hotbar` keyword exists)
    - `{ "sound": {} }` — beep; overrides optional
    - `{ "wait_ms": 500 }` — explicit pause step (a `wait_ms` on a key step
      overrides the default for that step; `0` forces no delay)
    - `{ "keys": "7", "wrapper": "bar6" }` — attach a named wrapper (see below).
      The wrapper's `before` steps run, then the key fires, then `after` runs —
      one logical action.
    - In cooldown mode, steps are candidates with `cooldown_ms` (number of ms
      before that action can fire again). Omitted `cooldown_ms` = ALWAYS ready
      (fires every pass). `cooldown_ms` sits on the OUTER step.
    - Explicitly REJECTED: `between_ms`, `hotbar`, `default_hotbar`, `sequence`
      steps, and a `fire_all_ready` toggle (fire-all is THE behavior, not an
      option). (See notes below.)
  - **wrapper** (LOCKED name 2026-09-11) — a named, reusable set of `before`
    and/or `after` steps that a key step attaches to, to avoid bloated inline
    bar-switching. User rejected `cycle`/`context`/`mode`/`stance`/`frame`/
    `nesting_action`/`handler`/`scaffold`; landed on **`wrapper`** (intuitive,
    no programming-pattern cost; decorator was the runner-up — precise but
    requires a software background). Profile-level:
    `"wrappers": { "bar6": { "before": [...], "after": [...] } }`.
    - `before`/`after` each optional; both are step lists (keys, waits, sounds).
    - No parameters — a wrapper is a constant. Different timing per key = a
      different wrapper (`on_bar6_fast`, `on_bar6_slow`). No wrapper stacking —
      one per step.
    - The bar-switch meaning lives ONLY in the wrapper's contents and the user's
      naming (`bar6`); SlitherIn carries zero game assumptions.
  - **abort** — `abort_keys` (per-macro, stops only this macro); `ignore_abort:
    true` (ignore the global default abort). See Abort tiers above.

  Mapped scenarios (property of record):
  - Buff sequence: `trigger: "Ctrl+K"`, `steps` with `wait`/keys/sound, no loop
    (= once), no schedule.
  - Hold-to-fire once: `trigger: "RButton"`, `hold_ms: 3000`, defaults = once.
  - Combat cycle (the AHK script): `trigger: "LButton"`, `hold_ms: 1000`,
    `toggle: "Pause"`, `loop: { "count": -1, "continue_on_release": false }`
    (implied by held trigger, not written), `schedule: { "mode": "cooldown",
    "fire_delay_ms": 700 }`, 5 steps with `cooldown_ms`; hotbar-6 action =
    `{ "keys": "7", "wrapper": "bar6" }` with `"wrappers": { "bar6": {
    "before": [{ "keys": "Ctrl+6" }], "after": [{ "keys": "Ctrl+1" }] } }`.
  - 6-min rebuffer: `trigger: "F9"` (tap), `loop: { "count": -1 }`
    (`continue_on_release` implied true by tap), no schedule (or cooldown with
    `fire_delay_ms: 360000`).
- **Hotbar decision (LOCKED 2026-09-11, evolved):** delete `primary_hotbar` and
  per-action `hotbar` AND do NOT add a `hotbar`/`default_hotbar` keyword. Any
  such keyword would hardcode "hotbar = Ctrl+N" — a DDO-specific (and user-keymap
  dependent) convention. Bar switching is EXPRESSED ENTIRELY AS PLAIN KEY
  STEPS/CHORDS, wrapped in a named `wrapper` for reuse. E.g. the hotbar-6 action
  = a key step `{ "keys": "7", "wrapper": "bar6" }` where `bar6`'s `before`
  presses `Ctrl+6` and `after` presses `Ctrl+1` — whatever combo the user's game
  actually uses. SlitherIn carries zero game-specific assumptions. [NOT YET
  IMPLEMENTED — no code touched for this yet.]
- **Engine behaviors to fold in** (surfaced as optional config, not new kinds):
  - Trigger passthrough: does the firing key's own press still reach the game?
  - Re-trigger while running: restart / resume / ignore.
  - Alternating payloads (same trigger advances to next defined sequence).
  - Concurrency policy when two armed cycles collide (default: one active input
    source; others wait/block).
  - Explicit fire-on-press vs fire-on-release.
  - Held-key emissions (press-and-hold-N-ms) for keys and mouse.
  - Mouse as a real step (buttons, hold duration, screen-position targeting).
  - Wheel + side buttons (XButton1/2) as triggers/steps.
  - Numpad vs top-row digit disambiguation.
  - Define reload-while-armed semantics explicitly.
- **Input backend abstraction (engine choice):** `IKeyEngine` with two impls:
  **SendInput** (default) and **VIIPER HID** (opt-in escape hatch for games that
  block direct input). Per the user (2026-09-11):
  - `input_engine` is set in **global settings as the default**, with an
    **optional per-profile override** (next to `macros`). A user who never uses
    VIIPER sets the global to `sendinput` and is done; a profile needing VIIPER
    (profile-for-game-B on viiper vs profile-for-game-A on sendinput) overrides
    it locally.
  - Engine lifecycle is driven by the **active profile**: select the engine on
    profile load/hot-reload, `Initialize()` it only if selected, dispose the
    previous one when no longer needed. A user who runs no VIIPER profile never
    needs VIIPER/usbip installed, launched, or warmed up — nothing is installed
    "just in case."
  - Reload semantics follow the app-wide rule: if a profile fails to load, keep
    the previous config AND the previous engine.
  - The seam already exists: all input flows through `PressKey(mod, keys)` →
    `viiper.SendState(...)` (`SlitherIn.App.cs:157`, `SlitherIn.Viiper.cs:227`),
    called from `SendCycleAction`, `SwitchToHotbar`, idler; `SendAllKeysUp` is
    the abort path. Interface: `Available` / `Initialize()` / `SetState(mods,
    hidKeys)` (diff-based) / `ReleaseAll()`. SendInput impl must translate HID
    scancode → VK incl. implicit-shift chars (`LookupKey`); output is NOT
    byte-identical to the HID device, so engines keep their own pacing.
  - Decision pending: soft-fallback vs hard-fail when a selected engine is
    unavailable.
- **Target architecture (2026-09-11, agreed layering):** ONE workflow container
  (start → run → end: trigger/toggle/hold, loop.count, continue_on_release,
  abort tiers, all uniform across modes) that calls upon TWO pacing policies
  inside a run — `schedule.mode: "fixed"` (cursor: fire step by step in order)
  and `"cooldown"` (scan: fire every step whose cooldown elapsed, spaced).
  Different policy function on the SAME state machine — no class bifurcation
  (avoids re-creating the `HasCycle` problem). Both policies emit through the
  input backend abstraction (`IKeyEngine`: SendInput default, VIIPER opt-in).
  Layer order: model+serializer (pure) → runtime engine (one state machine per
  macro; the two pacing policies) → input backend / thin UI facade.
  Mid-sized mechanical refactor, not a behavior rewrite.
- **Build & toolchain (decided 2026-09-11, stood up):** switch from the in-box
  framework `csc` (C# 5 ceiling, classic MSBuild 4.0 csproj) to the **.NET SDK 10
  (10.0.401, installed, smoke-verified)** with a **modern SDK-style csproj**.
  Target `net48` (`net4.8-windows`) so users still install NOTHING — .NET
  Framework 4.8 is built into Windows 10/11. Deliberately NOT a .NET 8/10
  runtime target (that would force a user runtime install or a fat self-contained
  exe; DdoHotbar's csproj claims net8.0 but is actually built by framework csc —
  unbuildable here as net8.0, don't follow that path). Reference assemblies come
  from the `Microsoft.NETFramework.ReferenceAssemblies` NuGet package (auto-
  downloads at first build; no Developer Pack install needed). Gains: modern C#
  (records/POCOs for the config model, string interpolation, nullable reference
  types, switch expressions) = cleaner expression of the layering; far better
  compiler diagnostics; scriptable `dotnet build` loop. Cost: one-time SDK
  install; build script changes. App users: zero change.
- **Code structure (decided 2026-09-11):** build the new app **type-per-file +
  namespaces** mapped 1:1 onto the architecture — NOT the sharded static-partial
  style. Structural review: SlitherIn today = ONE `static partial class
  SlitherIn` across 7 files (file = concern, everything visible to everything,
  implicit deps, so the layering cannot be expressed structurally); DdoHotbar =
  many small named types (file = type, explicit deps — better organised, though
  still static-global-coupled). Adopted layout: `SlitherIn.Core` (pure: config
  POCOs, serializer, validation, the two pacing policies + the macro-runner
  container), `SlitherIn.Input` (`IKeyEngine` + `SendInputEngine` +
  `ViiperEngine`), `SlitherIn.Shell` (hooks, tray, app wiring). Borrow
  DdoHotbar's `-selftest` entry point: a headless mode exercising parser +
  schedulers without a message pump — our verification without a debugger.
- **Workspace (decided 2026-09-11):** the new structure builds in
  `D:\SlytherInNew\` (user-created); `D:\SlitherIn2\` stays the unchanged,
  still-running app (nothing in it is modified during the redesign — read/copy
  only). Read-only copies of the current src, sample-json, and docs are staged
  under `D:\SlytherInNew\reference\`. No redesign code written yet.
- **Reference material — Win32/AHK building blocks:** staged copy
  `D:\SlytherInNew\reference\ReplicatingAHKInCSharp.md` (from `keytest\DdoHotbar`).
  Binds the Input/Shell layers; hard facts it records that we adopt:
  - SendInput events must carry **both `wVk` and `wScan`** (derive scan via
    `MapVirtualKey`) or games reading raw input/direct input drop the key — the
    single most common "works in Notepad, not in the game" cause.
  - **Extended-key flag** needed for PgUp/PgDn/End/Home/arrows/Insert/Delete and
    RShift/RCtrl/RAlt (matches the `extended` column in our key table).
  - **Mouse buttons 0–4 = VK 0x01–0x05** (Left/Right/Middle/XButton1/XButton2)
    — matches the agreed `Mouse0`–`Mouse4` numbering.
  - Cooldown timing = `Environment.TickCount` (monotonic, safe in a hold loop).
  - Low-level hook pitfalls to honor: keep the callback delegate in a static
    field; the hook thread must run a message pump; edge-detect mouse via
    `GetAsyncKeyState` polling; chords are explicit down→up pairs (no stuck mods).
  - UIPI/elevation: `SendInput` returns 0 when the foreground window is
    elevated and we are not — run the app at the game's integrity level.
- **UI surface v1 (decided 2026-09-11, provisional):** tray-only for v1.
  The tray is the CONTROL surface (profile, macros incl. click-to-start/stop,
  abort, exit, version, config-error) and stays in `Shell`. A future Settings
  WINDOW (toggles/options that today suffer the tray's close-on-click quirk) is
  deliberately deferred — but settings logic (model, persistence, applying them)
  lives in `Core`, NOT the tray, so a later window is just a new Shell presenter.
  Known trade-off accepted: toggles that exist re-show state via their label
  ("Notifications: On/Off"). No full macro editor ever planned — macro content
  stays JSON-edited (the user's workflow).
- **Logging (decided 2026-09-11):** a single settings option with three levels —
  `off` (DEFAULT; no log file is created at all), `on` (sparse: a startup line
  with REV + errors/warnings as they occur), `debug` (verbose trace: key events,
  step firing, reloads). Even when `off`, config-load failures are still
  surfaced in-session through the tray (error row + icon flash + balloon), so
  the log is an optional diagnostic, never the only error path.
- **Other carried settings (decided 2026-09-11):** run-at-startup, tray balloon
  notifications (default off), `SLITHERIN_DIR`/`RuntimeDir()` override, and the
  REV version label/build-verify are all kept.

## Redesign implementation status (2026-09-11, reconciled with PLAN.md)

> Process note: this file fell stale while code was written and check-ins between
> PLAN stages were skipped. Recovery (locked): one stage at a time, REV bump on
> every code change, this file updated at each step, and STOP to check in before
> the next stage. The inventory below is what's actually on disk, not intent.

- **Stage 1 (Config A: Models + KeyName)** — DONE. `Models.cs`, `KeyName.cs`;
  `KeyNameTests` + `ModelsTests` green.
- **Stage 2 (Config B: Loader/Normalizer/Validation)** — DONE. One bug fixed
  today: `Loader.Deserialize` failed to assign `outcome.Config` on the success
  path, so every valid file was silently rejected (six Loader tests caught it).
  The `Fail` helpers are now generic (`Fail<T>`).
- **Stage 3 (Engine A: Cancellation + Workflow)** — DONE. `Cancellation.cs`
  (cooperative token, `Never`, `ThrowIfCancelled`) + `Workflow.cs` implemented
  (Idle → Armed → AwaitingHold → Running → Parked; tap/hold triggers,
  `continue_on_release`, loop count, per-macro abort, run/pacing seam via
  `IPacing` + `StepReady`/`RunFinished` events); `CancellationTests` + the 12
  `WorkflowTests` green. State name `AwaitingHold` matches PLAN/DESIGN
  vocabulary.
- **Stage 4 (Engine B: pacing + dispatcher)** — DONE. `FixedPacing` (list order,
  step `wait_ms` wins, pass boundary honores the final step's delay, next pass
  fires on press) + `CooldownPacing` (per-scan: fire EVERY expired step in list
  order spaced by `fire_delay_ms`, rescans; no `cooldown_ms` = always ready;
  absolute wall-clock cooldowns) + `Dispatcher` (single cooperative Tick advancing
  every attached workflow; serializes injection through `IKeyEngine`; hold_for_ms
  released by the loop on its due time; unified held-chord tracking; detach =
  park, re-attach resumes). Semantics landed: `PacingState.RunId` distinguishes a
  fresh run (re-trigger resets cooldowns/cursors) from a pass/park boundary
  (state kept); `Workflow.Unpark()` resumes the pre-park state. `PacingTests`
  (6, un-`[Ignore]`d) + `DispatcherTests` (6, new, recording-engine) + new
  `WorkflowTests.Unpark` case green.
- **Stage 5 (Target/catalog + auto resolve)** — DONE. `ProfileCatalog`
  implemented: `LoadAll` (load + validate every profile via new
  `Loader.LoadProfilesDetailed`, build per-profile Workflow runtimes, run the
  cross-profile check), `Select` (parks outgoing runtime, resumes incoming),
  `ResolveOwner(ForegroundInfo)` for AUTO mode, `ActiveWorkflows` surface for the
  dispatcher. Cross-profile ambiguous-window-target warning added to
  `Validator.ValidateAll` (warning, never a blocker). `ProfileCatalogTests`
  un-`[Ignore]`d (auto-resolve by title owner; parked armed/running macro
  survives switch-away-and-back — cooldowns kept counting) and the
  `ValidationTests.AmbiguousWindowTargets` case un-`[Ignore]`d.
- **Stage 6 (Hook + SendInput)** — DONE (2026-09-11). `KeyMapping` (ONE
  canonical⇄Win32 table for detection AND injection: letters / top-row digits /
  F1-24 / named keys / mouse buttons; numpad ≠ top-row via distinct VK +
  scancode; modifiers are never chord tokens). `HookModel` (pure raw
  vkCode/mouse-message + modifier state → canonical `Chord` + event kind;
  unknown keys and lone modifiers produce NO event). Real `LowLevelHooks`
  (installs `WH_KEYBOARD_LL` + `WH_MOUSE_LL`, per-event foreground snapshot →
  `InputEvent`, clean uninstall). `SendInputEngine` (canonical chord → coded
  down+up SendInput events carrying vk + scancode, diff-based `SetState`,
  `ReleaseAll`, fixed modifier press/release order). Key-finder log wiring:
  `Log.Start` + `Log.Parse`, `Startup.RuntimeDir`, `App` now boots the hook +
  tray key-finder entry. New tests: `KeyTranslationTests` (6) +
  `HookEventModelTests` (8) → 126 total green (was 112).
- **Stage 7 (Tray/composition)** — DONE (2026-09-11, REV -10→-11). The full
  end-to-end app. New **public `Composition`** (`src\Composition.cs`) is the
  testable orchestration — owns catalog/dispatcher/engines (engine factory +
  cache, swap per profile), the locked routing order (kill → global abort
  (skip IgnoreAbort) → per-macro abort_keys → KeyUp trigger-up → toggle →
  window gate + engine guard → trigger), reload semantics (abort + disarm
  toggles + fresh catalog; broken ACTIVE file → `ProfileCatalog.KeepActive`
  keeps the previous runtime while the error becomes an alert), the VIIPER
  hard-fail (`ActiveProfileBlocked`), and AUTO-mode foreground polling
  (250 ms throttle). Infra changes: `Dispatcher.Engine` settable + `DetachAll`
  + `ReleaseAll` + `CreatePacing` uses `macro.DefaultDelayMs ?? default`;
  `NormalizedMacro` +`DefaultDelayMs` + `ToggleSound` (Normalizer sets both);
  `Watcher` realized (300 ms debounce, artifact filtering, `SuppressFor(path,
  ms)` self-write guard — the settings-toggle loop guard); `TrayHost.SetAlert`
  (SystemIcons.Warning/Application swap); `Startup.RunAtStartup` (HKCU Run key,
  value "SlitherIn"); `Loader.SaveSettings`; `LowLevelHooks.SnapshotForeground`
  → public static. `Beeps` (MCI alias `slitherin_audio`, square-wave WAV synth
  into temp, PlayFile, Cancel, `PlayCue(SoundSpec)`). `TrayMenu.Build` per the
  locked UX list (alert rows, Profile click=open JSON, "Profiles" submenu with
  Auto + per-file, display-only macro + Abort rows, three state-label toggles,
  Reload, version, Exit). `App` rewritten as a thin WinForms shell (hooks →
  HandleInput + key-finder, 10 ms tick → `Composition.Tick`, watcher with
  UI-thread marshal via SynchronizationContext, `SuppressFor` before every
  settings save, Beeps on CueRequested). New `CompositionTests` (6: broken
  profile alerts, reload abort+disarm, keep-old on broken active, viiper
  blocked no-injection, window-check toggle persists + gates, AUTO switch via
  foreground provider + FakeClock). Green: **132 tests** (was 126).
- **Stage 7 follow-up (tray UX continuity, rev -11→-12)** — user: "it sure
  doesn't look finished; you aren't using the proper icons, the color coding on
  the tray is gone." Restored the old-app tray look on top of the new menu:
  custom `icon.ico`/`alert.ico` (extracted from the old source's embedded base64
  into `src\Ui\Icons\`, embedded in the exe; external files next to the exe still
  override) replacing the `SystemIcons.Application/Warning` placeholders; alert
  **flash** (500 ms normal⇄alert toggle) + tooltip `SlitherIn - <reason>` on
  error; **left-click** opens the menu (private `ShowContextMenu` reflection,
  carried from the old app) + shows the pending-error balloon (gated by the
  Notifications setting, which TrayHost now mirrors); **TwoToneRenderer**
  ported (blue `Macros:` header, blue macro names, red `Abort:`, green
  clickable version row, gray disabled rows); macro rows now enabled-display
  with `• name: trigger` labels + step-count tooltips; `Open Settings
  (slitherin-settings.json)` row restored; version row clickable → releases
  page; warning/info glyph images on ALERT + Profile rows. Also deleted the
  vestigial unused `TrayHost.ExitRequested` event (Exit row calls `App.Exit`
  directly) to hold 0 warnings.
- **Stage 9 (VIIPER)** — DONE (2026-09-12, REV -13→-14). `ViiperEngine.cs`
  implemented (was a `NotImplementedException` stub) + a new `HidKeyMap`
  (`src\Input\HidKeyMap.cs`, canonical token → USB-HID keyboard usage + ModifierFlags
  → HID modifier byte). ViiperEngine is the TCP protocol port of the old
  `SlitherIn.Viiper.cs` with the hard-fail surface already wired (Composition's
  `ApplyActiveState` checks `Available` after `EngineFor` → `Initialize`).
  Transport ported 1:1: one-shot NUL-terminated requests (`ping`, `bus/create`,
  `bus/N/add {"type":"keyboard"}`, `bus/remove N`), a long-lived device report
  stream (`bus/N/devId\0` handshake, then [mod][count][usages…] frames), a
  background drain loop that flags a lost device, `SendReport` of the full held
  set per SetState (the dispatcher already passes the union of held chords),
  `ReleaseAll` → empty report, `Dispose` → `bus/remove` + close (the viiper.exe
  process itself is left running — it may be a shared/system server). Port
  improvements: System.Text.Json replaces JavaScriptSerializer; a failed device
  add now removes the bus (old code leaked it); `SetState` caps the report at 6
  usages (HID boot-keyboard limit); a mouse-keyboard chord is dropped WHOLE
  (modifiers + key) so it can't strand a modifier. `Available` self-heals: a lost
  device rebuilds (re-add keyboard + reopen stream) when the server still pings,
  throttled 2s; it never relaunches the server (that's Initialize's job), keeping
  the getter bounded for the UI thread. Validation now hard-errors a profile that
  declares `input_engine: "viiper"` AND carries mouse output (`button` step or a
  mouse-key chord in `keys`, incl. wrapper contents) — the keyboard report cannot
  emit it; the engine's chord-drop is the belt-and-braces layer only. New key
  vocabulary fact: `KeyName.IsMouseToken` (Core, so Validator/KeyMapping both use
  it; `KeyMapping.IsMouseToken` now delegates). New tests: `ViiperTests` (mock
  TCP VIIPER server + HidKeyMap table asserts + protocol/report/reconnect/hard-fail
  cases, 8 tests) + 5 `ValidationTests` viiper-mouse cases. **148 tests green**
  (was 132); 0 warnings.

Current green as of 2026-09-12 (all stages 1–9 closed + C-1..C-6 applied):
`dotnet build -c Release` → 0 errors, 0 warnings; `dotnet test -c Release` →
**163 tests, all passed, 0 skipped, 0 failed** (was 150); EXE carries REV
`rev-20260912-17` (via `build.ps1`) — full tray composition app with the old-app
tray look (custom + flashing alert icons, color-coded menu rows, Open Settings
row, clickable green version, left-click menu), live hooks + key-finder, profile
switcher, macro triggers, toggles, watcher auto-reload, alerts + beeps,
run-at-startup, second-launch notification, the VIIPER HID backend (bus/device
setup + keyboard report stream + hard-fail/self-heal), mid-run window gating
(C-4: skip-but-kept-ready catch-up, next countdown from actual firing),
normalization-failure keep-old (C-2), wScan + extended-key SendInput (C-3),
count-always-honored `continue_on_release` bound to held triggers (C-5), and
any-member chord release via a raw modifier-bitmap feed (C-6).
User-facing docs landed 2026-09-12: `README.md` (new app) + `ARCHITECTURE.md`
(runtime doc) at the project root. Same-day conversion (2026-09-12): `PLAN.md`
retired as the stage plan and rewritten as the **design-decisions record** —
§2 recorded chain of decisions, §3 inferred (never explicitly written) decisions
reconstructed from code, §4 audit = decisions vs. actual code with open verdicts
4.1 (SendInput wVk-only, wScan/extended-key rule unimplemented — **FIXED by
C-3**), 4.2 (unknown profile-level `input_engine` unvalidated → crashes load
path — **FIXED by C-2**), 4.3 (window gate gates trigger, not injection —
**FIXED by C-4**), 4.4 (`continue_on_release` "ignored when count = 1" comment
vs code — **FIXED by C-5**), 4.5 (`GetAsyncKeyState` per event + unused
`LLKHF_ALTDOWN` — resolved as intended/a triviality), 4.6 (doc drift: DESIGN
`App.cs`/`IKeyEngine`/`MenuActions` rows, AGENTS README line — **resolved in
the same apply-sync**).

## Per-stage notes & challenges (hard rule since 2026-09-11)

> Every stage close-out appends what went wrong or was non-obvious and the fix
> that landed — not just green counts. Retro-fitting stages 1–4 now from the
> records/transcript.

- **Stage 1 (Models + KeyName)** — no code setback. The real work was the
  vocabulary LOCK from discussion: bare digits = top-row only, numpad is
  explicit (`Numpad1`), mouse buttons `LButton`/`XButton1`/…, fixed modifier
  order. Purely a design call; nothing in code bit back.
- **Stage 2 (Loader/Normalizer/Validation)** — the one real bug of Config:
  `Loader.Deserialize` never assigned `outcome.Config` on the success path, so
  every valid file was silently REJECTED. Six Loader tests caught it. Fail
  helpers made generic (`Fail<T>`). Documented in the status section too.
- **Stage 3 (Cancellation + Workflow)** — (a) naming drift: the state enum was
  authored as `WaitingToFire`, but PLAN/DESIGN spell it `AwaitingHold` — renamed
  before close-out (docs-must-match-reality rule). (b) Environment: a running
  `slitherin.exe` locks the built EXE, so `dotnet build` fails MSB3021/3027
  until the process is stopped — `build.ps1` already does this; the manual loop
  must remember it.
- **Stage 4 (Pacing + Dispatcher)** — the pacing PASS-BOUNDARY contract fight.
  Tests were written to expectations that assumed (i) a trailing `Wait` after
  the final step and (ii) `Wait` during a cooldown rescan; the first
  implementation returned `RunComplete` at scan exhaustion instead. Two equally
  valid readings existed. Settled by: (i) `FixedPacing` now honors the final
  step's delay at the pass boundary — strictly better for infinite fixed loops
  (the wrap-around pause makes the cadence symmetric); (ii) `CooldownPacing`
  documents scan → `Complete` → rescan semantics and the always-ready cadence
  test asserts fire-delay spacing across rescans. Also pure mechanicals: a
  named-argument case mismatch and NUnit 4 dropping `CollectionAssert` (repo
  style is `Assert.That(x, Is.EqualTo(y))`) — caught on the first test build,
  zero design cost. The EXE-lock build hiccup recurred.
- **Stage 5 (Target/catalog + AUTO resolution)** — one real surprise, and it was
  MINE not the code's: the first AutoResolve assertion asked
  `ResolveOwner("ple", "ManualOnly")` to return profile B, but B's target is
  exe AND title (`"Main"`) — the title constraint correctly rejected the window
  and resolved to null. The behavior was right; my expectation was wrong; the
  test now asserts the null. Other notes: (a) `Loader.LoadProfiles` was
  refactored to delegate to a new `LoadProfilesDetailed` (file⇒profile mapping)
  — byte-identical behavior, LoaderTests stayed green untouched; (b) the
  scaffolding `ParkedRuntime`/`MacroState` dictionary was DROPPED: the parked
  runtime IS the profile's Workflow list, and `Park()`/`Unpark()` (Stage 4)
  already retain state while absolute wall-clock makes cooldowns keep counting
  while away (the catalog test drives this end-to-end); (c) the ambiguity rule:
  two window targets overlap unless a field BOTH sides carry differs — shared
  exe + different titles (the *-Sarlona vs *-Khyber character split) or different
  exes separate them; always a warning, never a load blocker; (d) warning count
  dropped 18 → 17 with the removed stub class.
- **Stage 5+** — append a bullet here at each close-out.
- **Stage 6 (Hook + translation)** — two real bugs, both in the translation
  table, both caught first-run by the new tests: (a) `KeyMapping.VkOf` switched
  on `token.ToUpperInvariant()` while every switch arm was title-case
  (`"LButton"`, `"Numpad0"`…), so ALL named/numpad/mouse tokens silently
  resolved to VK 0 — forward lookups returned vk=0 and reverse lookup had no
  0x61 for `Numpad1` (the "must not be 0" and null-chord asserts caught it).
  Fix: switch on the raw token. (b) the `ByToken` dictionary was
  `StringComparer.Ordinal` but the lookup pre-uppercased the input, so
  mixed-case names never matched; fix: `OrdinalIgnoreCase`, no uppercasing.
  Also fixed a WRONG TEST (mine, not the code's): I asserted
  `TryGetCanonical(0x45)` was false, but 0x45 is VK_E — the assertion AND its
  comment were both wrong; it now asserts 0x88 (unmapped, past F24). Build
  roster: `Win32` lacked the mouse-UP constants (`WM_LBUTTONUP` … `WM_XBUTTONUP`)
  — added. Hook behavior itself is runtime-only (callback + foreground
  snapshot), untested-by-design; the translation table carries all the unit
  proof. One historical note surfaced: the reference `ReplicatingAHKInCSharp.md`
  records numpad VKs as 0x60-0x69 and mouse buttons as 0x01-0x05 — KeyMapping
  matches both, so the "no bytes differ from the reference" check holds.
- **Stage 6 live-run (2026-09-11, REV bumps -09→-10)** — the first real launch
  exposed TWO integration bugs the unit tests couldn't catch:
  (a) **wrong settings filename**: I told the user to (and did) create
  `slitherin-settings.json`, but `Loader.SettingsFileName` is
  `slitherin.settings.json` (`Loader.cs:44`) — settings silently failed to load,
  log stayed `off`, no file written. Fix: rename to `slitherin.settings.json`.
  Captured so the filenames never drift again: settings = `slitherin.settings.json`,
  profiles = `slitherin*.json` (minus the settings file).
(b) **no tray icon at all**: `TrayHost` created `new NotifyIcon()` without an
   `Icon`, so `Visible=true` shows NOTHING on Windows — the user saw "no tray".
   Fix: `Icon = SystemIcons.Application`. Also learned the single-instance mutex
   makes a second launch silently exit (`Program.cs:28` returns quietly) — an
   already-running instance looks exactly like "the exe does nothing".
- **Stage 9 (VIIPER port)** — two real finds, one mine, one the tests':
  (a) **modifier bit order differs between our enum and HID**: `ModifierFlags`
  is Ctrl=1, Alt=2, Shift=4, Win=8 but the HID/old-VIIPER modifier byte is
  Ctrl=0x01, Shift=0x02, Alt=0x04, Win=0x08 — Alt↔Shift are swapped. `HidKeyMap.
  Modifiers` maps explicitly and the tests assert `Shift+Alt → 0x06` so the swap
  can never silently land. (b) **race made the loss-detection test flaky**: I set
  `_lastRebuildAttempt = 0` after a successful rebuild to "let a fresh failure
  rebuild immediately" — that defeats the 2s throttle, so after `KillDeviceStreams`
  the engine could flag the device dead and rebuild all inside one 10 ms poll
  interval, making the transient `Available=false` unobservable. Removal of the
  reset makes the throttle real (loss stays observable ≥2 s) and the test
  deterministic. Also noted: `Build` first failed with the EXE-lock (running tray
  instance) despite AGENTS.md warning — `build.ps1` still stops it first; the
  manual `dotnet build` loop must remember it (recurring, per stage 3/4 notes).
- **Stage 9 live verification (2026-09-12)** — ran against the REAL server, and
  it exposed an environment gotcha the mock couldn't: the user's viiper was
  auto-started WITHOUT `usbip` on its PATH, so `bus/N/add` returned HTTP 409
  (`exec: "usbip": executable file not found in %PATH%`) and no device was
  created. `usbip.exe` exists at `C:\Program Files\USBip\` and the usbip-win2
  driver is installed (a "USBip 3.X Emulated Host Controller" is present) — the
  server just couldn't FIND usbip because it wasn't launched with the dir on its
  PATH. The engine's `EnsureServer` (launch `viiper.exe` with `FindUsbipDir()`
  prepended to PATH — the old app's trick) DOES fix this, but only when it
  launches the server itself; a pre-existing pingable server is reused as-is and
  stays broken. Fix in practice: stopped the stale instance, ran the live smoke,
  the engine relaunched viiper with the correct PATH, attach now succeeds
  (`{"busId":1,"devId":"1","vid":"0x2e8a","pid":"0x0010","type":"keyboard"}`).
  Full loop proof: the running app's key-finder hook logged the virtual
  keyboard's `F13` press+release (`[input] F13 -> ...`) — engine → VIIPER TCP →
  usbip-win2 → Windows → our own WH_KEYBOARD_LL hook. The live smoke is kept as
  an `[Explicit]` NUnit test (`LiveServer_Smoke`) so normal runs stay hermetic.
  Takeaway for the user: viiper auto-started at logon must have usbip on its
  PATH (or be relaunched by a viiper-profile engine) — otherwise a profile
  selecting viiper blocks with the hard-fail alert.
  **Re-verified (2026-09-12, same session as C-5/C-6):** the earlier "only
  remaining item = live VIIPER hardware verification" wording in PLAN was
  wrong — the setup is fully present on this machine NOW. Checked live:
  `viiper.exe` at `%LOCALAPPDATA%\VIIPER\viiper.exe` and RUNNING (PID seen
  32720), `usbip.exe` at `C:\Program Files\USBip\`, usbip-win2 driver present
  as an OK `USBip 3.X Emulated Host Controller` (`ROOT\USB\0000`), and the
  `LiveServer_Smoke` `[Explicit]` test re-run with the real server — **passed**
  (1s, engine → bus/create + keyboard add → handshake → HID F13 report →
  release). VIIPER is installed and functional; no live-verification item
  remains open.
- **C-4 (mid-run window gate, 2026-09-12, REV -14→-16)** — verdict on PLAN.md
  4.3 implemented (option (b)): the gate now covers trigger AND injection.
  Implementation notes: `Composition.Tick` snapshots the gate ONCE per slice
  (from the live foreground + active target) and stores it on
  `Dispatcher.InjectionAllowed`; each workflow's `Advance` checks it before
  consulting its pacing — a closed gate returns early so the pacing cursor never
  moves and `PacingState.LastFireMs` only updates on a REAL fire, which is
  exactly why "next countdown from actual firing" fell out of both pacing
  policies for free. Placement review (user: no tacked-on fixes; architecture
  standards): the check must live at the workflow→pacing seam — gating at the
  dispatcher's injection seam would advance pacing then drop the fire (losing
  the step, breaking catch-up), and folding it into a pacing policy would
  couple a window-focus policy into the pacing layer; the Workflow-level
  pre-pacing check is the correct orthogonal layering and composes with any
  pacing. Two non-obvious things bit during the change: (a) the existing
  Composition tests that simulate an in-game run only set the foreground ON THE
  EVENT, while the mid-run gate reads the tick-time foreground — those tests
  broke until the live `_foreground` field was set too; (b) FixedPacing's pass
  boundary COMPLETES on the tick that crosses the due time and the next pass's
  first step fires on the FOLLOWING tick (one `PacingDecision` per slice) — my
  first version of the new gating test expected both to happen in one tick and
  asserted too early. Efficiency fix within C-4 (REV -15→-16): the first
  implementation set `InjectionAllowed = () => WindowGate(_foreground?.Invoke())`
  — the Win32 foreground snapshot ran once per workflow per 10 ms slice, not
  once per slice as the doc claimed; now the gate bool is evaluated once and the
  lambda closes over it.
- **C-5 (continue_on_release, 2026-09-12, REV -16→-17)** — locked semantics:
  `count` always honored at every value for BOTH trigger kinds; the flag
  governs HELD triggers only (`fire_after_ms`); taps run their full count and
  releasing never cancels. Applied by deleting the obsolete
  `triggerIsHeld` parameter from `LoopRules.ContinueOnRelease` (now
  explicit-or-false, the derived "tap ↦ true" default exists only as a wording
  note) and narrowing the release-stop branch in `Workflow.TriggerUp` to held
  triggers. What bit during implementation: the OLD tests faked the removal of
  the parameter so badly that 3 LoopSpec + 1 Workflow test codified the OLD
  behavior (tap + explicit false *stops* on the natural key-up) — they passed
  because they asserted the pre-fix contract, then failed the new build.
  Lesson reinforced: stale tests aren't just annoying, they LOCK IN the bug;
  the fix is literally re-asserting the new contract in the same PR.
- **C-6 (chord-release orphan, 2026-09-12, REV -16→-17)** — a chord trigger
  whose modifier was released FIRST could never deliver `TriggerUp` (modifiers
  produce no canonical chord event, and the K-up that follows resolves to a
  different, unmatchable chord), orphaning a held run forever. Applied via a
  raw modifier-bitmap feed: `HookModel.ModifierBitmap`/`ModifierFlagOf` fold
  the released modifier's own up/down into the sampled bitmap (correct even
  while `GetAsyncKeyState` still shows the key down mid-transition — the 
  event's own down/up is authoritative for that key); `LowLevelHooks` emits
  the new bitmap on every real change as a dedicated `ModifierState` event
  (chose a separate event over stacking the bitmap on `InputEvent.Chord`,
  whose `Key`-typed payload can't represent "modifiers only" without a
  sentinel); `Composition.HandleModifierState` diff-releases: any active
  workflow whose trigger shares a lifted modifier gets `TriggerUp`, and
  `Workflow.TriggerUp`'s existing state guards make it a no-op for taps and
  `continue_on_release:true`. The exact-chord fast path is untouched, so
  single-key triggers never see a modifier event. Test lesson: the C-6
  composition tests must drive BOTH the InputEvent feed for the main key AND
  the new `HandleModifierState` feed for the modifier, and the window gate is
  only consulted for STARTING a run, so the modifier-release path needs no
  foreground fixture — which is also why it could not be expressed through
  `InputEvent` at all.

## Decisions register (updated 2026-09-11 — the old "Open decisions" list is retired)

Everything previously flagged "open" is settled; the locked vocabulary lives in
the "Config schema (agreed 2026-09-11)" section above. Rules:

- **Feature scope is CLOSED**: key remapping, recorder, text/command send,
  delay jitter/humanization, screen-position clicks, wheel scroll, and any
  other unagreed feature are OUT and will not be re-raised. They re-enter only
  if the user explicitly asks for one.
- **Input vocabulary**: LOCKED to the schema above (key chords incl. numpad vs
  top-row, mouse buttons, side buttons; sound; wait_ms; hold_ms; hold_for_ms;
  cooldown; wrapper). No pending confirmation.
- **Parallelism**: single cooperative `Dispatcher` (decided, built, tested).
- **Testing**: dedicated test project (decided, exists — 132 tests).
- **Settings/profiles split**: kept — settings = `slitherin.settings.json`,
  profiles = `slitherin*.json` (decided, built).
- **Engine unavailable**: HARD-FAIL with alert, no silent SendInput fallback
  (decided, built — `ActiveProfileBlocked`).
- **VIIPER mouse output (decided 2026-09-12)**: the VIIPER backend is a HID
  KEYBOARD device — a `viiper` profile that declares mouse output (`button` step
  or a mouse-key chord in `keys`, incl. inside wrappers) is a hard VALIDATION
  error (the press would silently vanish). The engine also drops such a chord
  whole (modifiers + key) as the defense layer for the global-engine-viiper case
  that validation can't see. Mouse steps remain fully legal on `sendinput`.
- **Tray UX surface**: tray-only for v1 (decided, built). A future options
  window is a new Shell presenter, not part of this app.
- **Serialization (DECIDED 2026-09-11)**: move to **System.Text.Json** (latest
  stable 10.0.x supports .NET Framework 4.6.2+/netstandard2.0, works on 4.8).
  Runtime NuGet packages are accepted. JSON comments handled natively via
  `JsonCommentHandling.Skip` — the hand-rolled `StripJsonComments` pre-pass is
  retired. (Build-time NuGet was already accepted: see reference-assemblies
  under Build & toolchain. Build requirements are documented in
  `D:\SlytherInNew\BUILDING.md`.)
- **Second instance (implemented 2026-09-11, REV -13)**: a duplicate launch
  signals the running instance via a named `EventWaitHandle`; the primary pops
  a "already running" balloon (gated by the Notifications toggle) and logs. The
  duplicate exits quietly. (Was `Program.cs` "bail quietly" TODO.)

## Current live state (verify by reading files, don't trust this blindly)

- The process running in the tray right now is the NEW app built from
  `D:\SlytherInNew\` (`src\bin\Release\net48\slitherin.exe`, REV
  `rev-20260912-14`, relaunched by `build.ps1` this session). It installs the
  real low-level hooks; with `log: "debug"` in `slitherin.settings.json` every
  key/mouse press (incl. side buttons + numpad) appears in `slitherin.log` as
  its canonical config string + the focused window — the key-finder. Full tray
  composition (Stage 7): profile switcher, macro triggers + Abort rows, toggles,
  watcher auto-reload, alerts + beeps, run-at-startup; a duplicate launch pops a
  "already running" balloon (Notifications ON) instead of bailing silently. The
  VIIPER HID backend (Stage 9) is implemented; it only activates when a profile
  actually selects `"input_engine": "viiper"` (hard-fail with alert when
  unavailable — no silent SendInput fallback). The OLD app (`D:\SlitherIn2\`) is
  untouched (read/copy only).
- Docs (2026-09-12, doc-only — no code change, no REV bump): new root
  `README.md` now documents the REDESIGNED app (schema, engines, quick start;
  template = old `reference\README.md`); new standalone `ARCHITECTURE.md`
  presents the runtime layering/flow (detection → composition → workflow engine
  → `IKeyEngine` hard-fail → OS) and the one-trigger-press path, referencing
  DESIGN.md/PLAN.md without duplicating the build plan.
- New build root `D:\SlytherInNew\` holds the redesign code + tests; `reference\`
  underneath holds read-only copies of the old src / sample-json / docs AND this
  working-memory file (living document — keep current). The old 5-action cycle
  config note above is historical context for the old app.

## Working agreements (how we operate)

- The USER leads the design conversation; the assistant challenges assumptions
  and asks question, but does not take the lead or lecture.
- If a file changes and the assistant didn't make the change, assume the user
  did it. Don't burn time reconstructing who did what.
- Never overwrite the user's active config files without checking first (the
  assistant once clobbered `slitherin.json`; avoided going forward).
- When stuck or at a fork: ask a direct question and take the answer — don't
  second-guess internally for long stretches.
- Stop when the user says stop. Don't keep working or "verifying" past a halt.
- Design discussion happens in PROJECT-STATE.md; build/config facts in AGENTS.md;
  user docs in README.md. Keep each in its lane.
- **Keep ≠ port as-is (2026-09-11):** any feature carried over from the current
  codebase is re-implemented cleanly in the new structure. If the old code is
  inefficient or can use a reusable structure, improve it during the port — no
  100% verbatim ports. "Keep" means the feature/behavior survives, not the code.