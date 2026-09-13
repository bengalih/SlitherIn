# PLAN.md — superseded by the design-decisions record

> **Conversion note (2026-09-12):** this file used to be the per-stage build
> plan (stages 1–9) with "stop and check in after each stage" working
> agreement. All implementation stages are closed (see
> `reference\PROJECT-STATE.md`, "Redesign implementation status"). Per the
> user's request this file now documents the **design decisions** that shaped
> the redesigned SlitherIn — including decisions that were never written down
> explicitly and were reconstructed from the code (§3).
>
> Companion docs: `DESIGN.md` (file blueprint), `AGENTS.md` (invariants),
> `ARCHITECTURE.md` (runtime flow), `README.md` (user-facing),
> `reference\PROJECT-STATE.md` (working memory). The old per-stage detail is
> retained in PROJECT-STATE, not here.

## §1. Status snapshot (what "done" means today)

- All implementation stages 1–9 closed, plus change items C-1…C-6 applied
  (2026-09-12): build 0 errors / 0 warnings, **163 tests pass**, 0 `[Ignore]`,
  0 fail, REV `rev-20260912-17` verified inside the EXE by `build.ps1`, app
  end-to-end in the tray.
- The §4 audit is fully resolved (4.1→C-3, 4.2→C-2, 4.3→C-4, 4.4→C-5 fixed;
  4.5/4.6 resolved; every ledger row Resolved/FIXED).
- **VIIPER is set up and functional on this machine** (verified 2026-09-12):
  `viiper.exe` installed (`%LOCALAPPDATA%\VIIPER\viiper.exe`) and running,
  `usbip.exe` present (`C:\Program Files\USBip\`), the usbip-win2 driver
  installed and present as an OK device (`USBip 3.X Emulated Host
  Controller`), and the live smoke test `LiveServer_Smoke` (real
  bus/dev add + HID report round-trip) **passed** against the live server.
  Nothing is pending.
- The old stage list (Config A/B, Engine A/B, Target, Hook+SendInput, Tray,
  real configs, VIIPER) is preserved modulo this doc in PROJECT-STATE; nothing
  here says "next stage" because there is no next stage.

## §2. Design decisions (reconstructed as the record)

### Direction & ground truth
- Old app (`D:\SlitherIn`, `D:\SlitherIn2`) is read/copy-only; the redesign
  lives at `D:\SlytherInNew\` with `reference\` holding immutable copies.
- Current DDO empirically accepts SendInput for single digits + Ctrl+digit
  hotbars → **SendInput is the default engine**; VIIPER is a second,
  explicitly-selected engine, not the only path (a reversal of the original
  premise, recorded in PROJECT-STATE).
- Engine choice is per-deployment: `sendinput` (default) or `viiper`; a
  **profile can override** `settings.input_engine`.
- `IKeyEngine` contract: `Initialize()` (once, on selection), `Available`,
  `Name`, `SetState(held chord set)` (diff-based), `ReleaseAll()`, `Dispose()`.
  The dispatcher never talks to the OS; the engine is the only input side.

### Config format (locked)
- Strict JSON with comments + trailing commas (`JsonCommentHandling.Skip`);
  `schema_version` (currently 1) gate; **no templates auto-created** (the old
  "create defaults" behavior was dropped).
- One unified macro shape: `trigger / fire_after_ms / toggle / loop / schedule /
  steps`. No idler (concept dropped), no `fire_on`, no `hotbar` keyword, no
  text steps, no screen-position clicks.
- Step vocabulary: exactly `keys | button | sound | wait_ms`; modifiers are
  chord flags (never key tokens); mouse tokens are the five buttons; numpad
  explicit; punctuation rejected; wheel rejected. Canonical spelling checked
  via `KeyName` ("what you log is what you paste").
- A macro's trigger fires a **run**; `toggle` arms/disarms; `loop.count`
  repeats; `schedule.mode` is `fixed` or `cooldown`.
- Cooldown mode ("the combat-cycle behavior"): each pass fires every step whose
  `cooldown_ms` expired, paced by `fire_delay_ms`; unused steps skipped.
- Fixed mode: steps fire in order with `wait_ms` (default `default_delay_ms`).

### Runtime model (locked)
- **One cooperative dispatcher** advancing every registered workflow on a 10 ms
  UI-thread tick; no per-macro threads, no busy-waits; injections serialized.
- Profile switch **PARKS** the outgoing runtime (cooldowns keep counting on the
  wall clock); returning resumes it. File reload **ABORTS + DISARMS** all.
- Routing order (locked): kill → global abort (skips `ignore_abort`) → per-macro
  `abort_keys` → trigger-up → toggle → (window gate + engine guard) trigger.
- Abort tiers: per-macro `abort_keys`; global `abort` (everything except
  `ignore_abort`); `kill` (unconditional). Cancellation is cooperative.
- Profile selection is a **mode**: `manual` (sticky selection) or `auto`
  (follow foreground window, 250 ms poll) — AUTO is a convenience resolver, not
  a second runtime (no per-profile rules or per-game hotbars).
- Window gate: computed from the active profile's `window` block (exe-name +
  title match); gates the **trigger** (starts) AND the **injection** (mid-run:
  skip-but-kept-ready catch-up). Applied by C-4 (2026-09-12) — see §4.3.
- **Wall-clock semantics**: cooldowns/timers never pause while parked or while
  focus leaves; `WallClock` = `Stopwatch`.
- Hard-fail semantics: if VIIPER is selected and unavailable, that profile does
  NOT run (tray alert + blocked) — **no silent SendInput fallback**.

### Detection (locked)
- Single detection path: one `WH_KEYBOARD_LL` + one `WH_MOUSE_LL` hook set
  delivering events on the UI thread; no `RegisterHotKey`, no polling watchdog.
  Modifier state for a chord is read at event time with `GetAsyncKeyState`
  (per-event, inside the hook callback — not a background poll loop).
- All hook events **pass through** unchanged (AHK `~` semantics); injected
  events (`LLKHF_INJECTED`/`LLMHF_INJECTED`) are skipped.
- Key translation: "what you detect is what you can inject" — detection and
  injection are symmetric on the virtual key; the scan code is a
  translation-layer concern (see §4.1).
- Key-finder: `log: "debug"` writes every hook event as its canonical chord
  string + foreground window; that string is what you paste into config.

### Tray / UX (locked)
- One tray menu: Profile row (with "Auto (switch by window)" entry), macro rows
  (trigger names), Abort row, Window-check + Notifications toggles, version
  line `SlitherIn rev-…`; left-click pops the menu; alerts flash
  `alert.ico` + tooltip + pending-error balloon.
- Toggles persist into `slitherin.settings.json` (settings carry state;
  re-show semantics are the whole UX surface v1 — no settings window).
- Sound: `toggle_sound` cues + per-macro sounds via Media Player (`Beeps`);
  beeps suppressed during reload bursts.
- Single instance per user session; second launch → balloon on the running
  instance, then quiet exit.
- Run-at-startup: HKCU `Run` value toggled from the menu (not in settings JSON).

### Build / ops (locked)
- .NET Framework 4.8 (net48) with the .NET SDK 10 toolchain; `build.ps1` CI:
  `dotnet build` → 0 warnings required, `dotnet test` all green, REV embedded
  and verified.
- `REV` = `rev-YYYYMMDD-NN` const in `Program.cs`, bumped on **every code
  change**. Docs-only changes do not bump REV.

## §3. Decisions that were never explicitly written down (reconstructed from code)

> These were inferred from the implementation + tests, not found verbatim in
> DESIGN/AGENTS/PROJECT-STATE. Flag anything that reads wrong.
>
> **Review ledger (2026-09-12, user walked through each one at a time):**
>
> 1. Dispatcher owns the union — user probed alternatives (per-macro injection,
>    engine-side refcounts, full serialization); agreed dispatcher-ownership is
>    a simplicity/swappability choice, not the flutter fix per se. **CONFIRMED.**
> 2. Engines cached by name — user: "why would you not want that?" — obvious
>    engineering. **CONFIRMED.**
> 3. First step fires on press — user confirmed; noted `wait_ms` first step
>    provides delay. **CONFIRMED.**
> 4. Parking — **corrected, it was already a stated design decision, not an
>    inference.** Keep in this record but flagged.
> 5. Reload keep-old — user confirmed the 3-good-1-bad scenario must never let a
>    bad non-active profile affect the active one; surfaced 4.2/C-2 (normalize
>    throw). **CONFIRMED, with C-2 fix required.**
> 6. Normalize once per profile at load — user OK "as long as errors in
>    non-active profiles don't prevent active functioning" → C-2. **CONFIRMED.**
> 7. Hard-fail = blocked not halted — **CONFIRMED.**
> 8. Toggle→Refresh vs file→Reload distinction — **CONFIRMED.**
> 9. Watcher-only reload, marshaled to UI thread — user asked why alternatives;
>    considered polling (never-miss, costs CPU), decided keep watcher, revisit
>    only if issues arise. **CONFIRMED.** Line below updated to match.
> 10. Modifier order — **not a decision, dropped from deliberation.**
> 11. `toggle_sound` present/null semantics — **CONFIRMED.**

- **The dispatcher owns the held-chord union, not the engines.** Dispatcher
  tracks what's logically down and hands the FULL current chord set to
  `engine.SetState()`; each engine diffs against its own prior state and
  injects only the deltas. Chords shared by two running macros are injected
  once and held until neither needs them.
- **Engines are cached by name in the composition root** (a `sendinput` falls
  back only when the requested engine is unknown — see §4.2). The dispatcher
  starts on `sendinput` and is swapped when the active profile's engine
  changes; the swap happens in `ApplyActiveState`, so a manual profile-switch
  or tray toggle re-resolves the engine without a file reload.
- **`first step of a run/pass fires on press — no initial delay`** in fixed
  (and cooldown) pacing; a loop's pass boundary honors the final step's delay.
- **Parking is a dispatcher-suppression, not a teardown.** Workflows stay in
  state; on return they catch up ("fires on the first opportunity after
  unparking"), so a 6-min rebuffer cooldown continues counting while on another
  profile. **(Corrected 2026-09-12: this is a stated design decision, NOT an
  inference — remove from the inferred list.)**
- **Reload = abort + disarm + rebuild, but keep-old applies ONLY to the active
  file.** A broken non-active profile becomes a tray alert and never blocks
  load; the previous active **runtime** survives when the active file now fails
  (catalog keeps loading the rest).
- **Normalize happens at load, once per profile** (`ProfileCatalog.LoadAll`),
  producing `NormalizedProfile`/`Workflow` instances that the catalog then
  switches between. Per-profile `default_delay_ms` resolves at normalize time
  as `profile ?? settings ?? 3000`.
- **Hard-fail is "blocked", not "halted".** A blocked active profile still gets
  a dispatched engine (VIIPER's); `ActiveProfileBlocked` only gates **new**
  triggers — running workflows finish, and the alert is rebuilt on every state
  change.
- **`toggle` persistence writes settings (not the profile); reload preserves
  toggles by re-reading saved settings.** File-edits → `Refresh`/`Reload`
  distinction: tray toggles call `Refresh()` (applies engine/gate/blocked
  without halting runs); file changes call `Reload()` (abort all).
- **The watcher is the only reload trigger** (the "Reload configuration" tray
  row is slated for removal — CHANGES.md C-1); watcher events are marshaled onto
  the UI thread via `SynchronizationContext.Post`.
- **Modifier chord order is a fixed one code relies on twice:** parsing joins
  as Ctrl, Alt, Shift, Win and the SendInput engine presses in that order and
  releases in reverse (same as `Chord.ToString`). **(2026-09-12: not a real
  decision — canonicalize once and reuse; dropped from deliberation.)**
- **`toggle_sound` semantics**: a present cue slot plays its tone; omitted/null
  cue = silent — and loaded configs that reference `toggle_sound.on` play the
  default 500 Hz/1000 ms tone.

## §4. Audit (2026-09-12): decisions in this record vs. the actual code

> Each item below is a finding from reading the source + tests this session.
> 4.1–4.2 need a **verdict** (4.3 was FIXED by C-4, 2026-09-12). Items 4.4–4.6
> are flagged for confirmation.

### 4.1 (FIXED by C-3, rev-20260912-17) — SendInput injects `wVk` only; the adopted "carry wVk AND wScan" rule is not implemented
The record's key-translation section (borrowed from the old-app notes) says a
mapping layer must send **both** `wVk` and `wScan` (scan derived via
`MapVirtualKey`) and set the **extended-key flag** for PgUp/PgDn/End/Home/arrow
keys and right-side modifiers, because some games read raw scancode input and
fail otherwise.
**Code:** `SendInputEngine.SendKey` (`src\Input\SendInputEngine.cs:89`) builds
`KEYINPUT` with `wVk` set, `wScan` left 0, `KEYEVENTF_SCANCODE` and
`KEYEVENTF_EXTENDEDKEY` never set. `KeyMapping` exposes `ScanCodeOf` (in the
translation layer) but the engine never calls it.
**Observed behavior:** works for the current DDO hotkeys (digits, Ctrl+digit);
an extended-key edge (arrow/Nav keys) would rely on Windows auto-scanning.
**Options:** (a) implement wScan + extended-key flags in SendInputEngine and
test the Nav-key case; (b) accept wVk-only as the deliberate behavior and
correct the doc; (c) leave code as-is and only document the limitation.

### 4.2 (FIXED by C-2, rev-20260912-17) — unknown profile-level `input_engine` is neither validated nor soft-handled; it crashes the load path
`Validator.Validate(Profile)` checks `schema_version`/name/macro fields but
**not** `profile.input_engine`. `Normalizer.Normalize` throws
`NormalizeException` for anything outside `{sendinput, viiper}`
(`Normalizer.cs:112`), and `ProfileCatalog.LoadAll` normalizes **every** profile
at load (`ProfileCatalog.cs:70`) with no per-file try/catch.
**Boot path:** `Program.Main` has `try/finally` with no catch → a profile with
`"input_engine": "nasal"` throws out of `LoadAll` (via `Composition.Boot`) and
**crashes startup**. **Reload path:** `App.SafeReload` swallows it as a bare
"reload failed" log with no tray alert. This contradicts two locked rules —
"never crash from bad config, tray-alert instead" and "a broken profile never
blocks the app" — and is asymmetric with settings-level `input_engine`, which
IS validated (`Validation.cs:48`, tested by `ValidationTests.cs:135`).
**Options:** (a) add the engine check to `Validator.Validate(Profile)` and/or
catch `NormalizeException` per profile inside `LoadAll`, surfacing it as a
per-file error; (b) treat unknown engine like unknown everything (validation
error, keep-old). Recommend (a).

### 4.3 (FIXED by C-4, rev-20260912-16) — window gate gates trigger AND injection
The former record described focus gating the **injection** with
"skipped-but-kept-ready catch-up" mid-run, but the code initially gates the
**trigger only** (`Composition.HandleInput`, before `TriggerDown`): once a run
starts, steps kept firing regardless of focus. C-4 (user verdict: FIX, the
Discord example) implemented the recorded mid-run gating: the gate is evaluated
once per tick from the current foreground + active target, a closed gate holds
ready steps without advancing the pacing cursor (wall-clock keeps counting), a
reopened gate fires the held step as catch-up, and the next countdown starts from
the actual firing time.

### 4.4 (FIXED by C-5, rev-20260912-17) — `continue_on_release` "ignored when count = 1" is documented but not implemented
`LoopSpec.cs:10` says continue-on-release is "Ignored when count = 1", but
`LoopRules.ContinueOnRelease` never consults `count` — for a once-run held
trigger, releasing **stops** the run mid-way. No test covers count=1 +
continue_on_release. If the intent was "a once-run always completes", the code
is wrong; if the intent is the code's behavior, the comment is wrong.

### 4.5 (resolved, rev-20260912-17) — modifier reads use `GetAsyncKeyState` per event, and `LLKHF_ALTDOWN` is unused
Detection reads Ctrl/Alt/Shift/Win state with `GetAsyncKeyState` per event
(incl. from the mouse hook) — inside the hook callback, not a poll loop, so the
"no polling watchdog" rule holds, but if the intent was "no GetAsyncKeyState at
all", that's not what's built. Also `Win32.LLKHF_ALTDOWN` is defined but never
used (Alt is read via `GetAsyncKeyState` like the others).

### 4.6 (resolved, rev-20260912-17) — doc-drift, no code change needed
- `DESIGN.md` §3.2 describes `App.cs` as the composition root; the composition
  root is now `Composition.cs` (App is a thin shell). — **fixed**
- `DESIGN.md` §3.8 sketches `IKeyEngine.SetState(mods, keys)`; the actual
  contract is `SetState(IReadOnlyList<Chord>)` (`IKeyEngine.cs`). — **fixed**
- `AGENTS.md` still says the README is "to be written for the new project";
  `README.md` now exists. — **fixed**
- `DESIGN.md` §1 still lists rejected `fire_on` and idler — consistent with the
  code; no action.
- `DESIGN.md` `MenuActions.cs` row mentioned "manual reload" — C-1 removed the
  tray row, so the row's wording was stale; **fixed** in the apply-sync.
- `ARCHITECTURE.md` 105-107 + `README.md` 186/192-195 carried the OLD derived
  release rule and were silent on C-6. — **fixed** with the C-5/C-6 semantics
  (flag governs held triggers only; release = any chord member).

Verified against the code, not just the docs (2026-09-12, user noted the doc
set is maintained by us — no user verdict needed). All drift claims confirm;
every item above is folded into the apply-sync (rev-20260912-17, docs-only —
no REV bump beyond the session's code work).

### 4.7 Complete candidate ledger (2026-09-12) — every item checked this session

> The 4.1–4.6 list is the surviving set. This ledger is everything that was
> examined and either resolved as matching the code/decision or is open.
> Kept here so rejected candidates stay visible and reviewable.

| Ref | Candidate checked | Open (verdict needed) / Resolved |
|---|---|---|
| D1 | SendInput sends `wVk` only; recorded rule "must carry BOTH wVk and wScan + extended-key flag (PgUp/PgDn/End/Home/arrows/Insert/Delete, RShift/RCtrl/RAlt)" | **FIXED by C-3** (rev-20260912-17) |
| D2 | Tap triggers fire on **press** (old app: on release); PROJ-STATE "fire-on-press vs fire-on-release" listed as optional config | Resolved — `fire_on` rejected (DESIGN/AGENTS); behavior change vs old app noted |
| D3 | Modifier state via `GetAsyncKeyState` per event (incl. mouse hook); `Win32.LLKHF_ALTDOWN` defined but unused | Resolved — per-event, not a watchdog; LLKHF_ALTDOWN unused = trivia |
| D4 | Global abort default "backwards-compatible" | Resolved — trivial |
| D5 | "Focus gates only the INJECTION … skip-but-kept-ready catch-up" vs code gates only at trigger | **FIXED by C-4** (rev-20260912-16) |
| D6 | dup of D2 | n/a |
| D7 | `continue_on_release` "Ignored when count = 1" vs `LoopRules.ContinueOnRelease` ignores count | **FIXED by C-5** (rev-20260912-17) |
| D8 | Reload = abort + disarm all | Resolved — matches |
| D9 | Pending-error balloon gated by Notifications | Resolved — confirmed |
| D10 | AUTO poll 250 ms throttle | Resolved — `AutoPollMs = 250` |
| D11 | Engine `Initialize()` only when engine selected | Resolved — dispatcher news-up `sendinput` (no-op); VIIPER only on selection |
| D12 | VIIPER server not killed on engine dispose | Resolved — matches ("may be a shared/system server") |
| D13 | `Available` self-heal re-probed after ~2 s | Resolved — matches |
| D14 | HID report capped at 6 usages | Resolved — matches |
| D15 | settings sample engine note | Resolved — fine |
| D16 | once-run buff sequence semantics | Resolved — matches |
| D17 | No template-file creation | Resolved — matches |
| D18 | Triggers always pass through | Resolved — `CallNextHookEx` unchanged, always |
| D19 | Injected events skipped (sendinput self-feed); VIIPER output not flagged → self-detected | Resolved — matches intent |
| D20 | build.ps1 UTF-16 detection | Resolved — alignment-agnostic hex search |
| D21 | sample `*-Sarlona` titles / ambiguity rule | Resolved — distinct titles, no overlap |
| D22 | Window gate uses event-time snapshot | Resolved — matches |
| D23 | No-toggle macros always armed | Resolved — `_alwaysArmed => Toggle == null` |
| D24 | Second-launch notification | Resolved — matches |
| D25 | Watcher lives in `Core\Config` despite "deferred to Stage 7" | Resolved — location fine |
| D26 | DESIGN §3.2 App row (pre-Stage-7 architecture) | **Doc drift → 4.6** |
| D27 | DESIGN §3.8 `SetState(mods, keys)` vs actual | **Doc drift → 4.6** |
| D28 | `default_delay_ms` profile ?? settings ?? 3000 | Resolved — matches |
| D29 | loop `count: 0` fires nothing | Resolved — validation warning + `StartRun` guard |
| D30 | settings `abort`/`kill` combos | Resolved — matches |
| — | Unknown `profile.input_engine` unvalidated → `NormalizeException` crashes load | **FIXED by C-2** (rev-20260912-17) |
- `DESIGN.md` §3.2/§3.8 and the `AGENTS.md` README line above were the third
  drift set folded into the §4.6 apply-sync (all fixed, rev-20260912-17).

## §5. Standing position

- Implementation closed; tests green (**163** pass, 0 fail / 0 skip /
  0 `[Ignore]`, 2026-09-12); REV `rev-20260912-17` verified by `build.ps1`.
- §4 audit: 4.1–4.4 FIXED (C-3 / C-2 / C-4 / C-5), 4.5–4.6 resolved; the
  complete candidate ledger (4.7) has no OPEN rows.
- VIIPER: installed, driver present, server running, live smoke re-run and
  **passed** (2026-09-12) — verified functional, nothing pending.
