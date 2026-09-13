# CHANGES.md — pending changes awaiting user sign-off

> Pending-change list, kept current every session. Each item records what to
> change, why, and the verdict it's waiting on. Items here are NOT done until
> checked off. Code-affecting items get a REV bump when applied; docs-only
> items do not.

## Open items

(none — every recorded item is applied and checked off below.)

## Checked off

### C-1 (2026-09-12, REV rev-20260912-17) — Remove the "Reload configuration" tray row — applied
Deleted the manual `Reload configuration` row from `TrayMenu.cs`, the
orphaned `ReloadNow` action from `MenuActions.cs` (field + App.cs assignment).
The watcher (debounce + artifact filter + `SuppressFor`) remains the single
reload path; `Composition.Reload` is internal. Verified in-tree: no `ReloadNow`
matches in `src\`. DESIGN.md `MenuActions.cs` row mentioned "manual reload" —
lost its subject with the tray row, reworded in the same apply-sync (PLAN 4.6).

### C-2 (2026-09-12, REV rev-20260912-17) — Normalization errors must not block the active profile — applied
`ProfileCatalog.LoadAll` catches `NormalizeException` per profile
(`ProfileCatalog.cs:79`), records it as that file's error + alert, and
continues loading the rest — a profile that parses but can't be built (bad
`input_engine`, bad `hold_for_ms`, unknown wrapper) no longer aborts boot or a
hot-reload. Matches the settings/profile validation asymmetry and the user's
"bad engine = improper json config" ruling. Tests include
`Reload_NormalizeFailedActiveFile_KeepsPreviousRuntimeRunning` and the
boot-with-broken-profile case.

### C-3 (2026-09-12, REV rev-20260912-17) — wScan + extended-key SendInput (PLAN.md 4.1) — applied
`SendInputEngine.SendKey` now sets `wScan = MapVirtualKey(wVk, MAPVK_VK_TO_VSC)`
and flags `KEYEVENTF_EXTENDEDKEY` for the extended set (0x21–0x28, 0x2D, 0x2E,
0xA1, 0xA3, 0xA5), matching on KEYUP. Matches the user-supplied reference spec.

### C-5 (2026-09-12, REV rev-20260912-17) — `continue_on_release`: count always honored; held triggers only (PLAN.md 4.4) — applied
`LoopRules.ContinueOnRelease` takes the LoopSpec only (no trigger-kind param)
and returns explicit-or-false; `count` is honored at every value for both
trigger kinds; tap triggers never stop on release. Tests rewritten/added in
`LoopSpecTests` (`ContinueOnRelease_*`) and `WorkflowTests`
(`C5_TapTrigger_ExplicitContinueOnReleaseFalse_StillRunsFullCount`,
`C5_HeldTrigger_CountFive_HoldThrough_…`,
`C5_HeldTrigger_CountFive_ReleaseWithContinueTrue_…`,
`C5_HeldTrigger_CountFive_ReleaseWithContinueFalse_StopsMidRun`).

### C-6 (2026-09-12, REV rev-20260912-17) — chord-release orphan hole (option B) — applied
Release of a HELD chord trigger is now defined by down-set membership, not
exact-chord match:
- `HookModel.ModifierBitmap`/`ModifierFlagOf` (pure) fold the released
  modifier's own up/down into the sampled bitmap; `LowLevelHooks` emits the
  full new bitmap on every real change via `ModifierState` (no canonical chord
  exists for a bare modifier, so it rides a dedicated event).
- `Composition.HandleModifierState` tracks the current bitmap and calls
  `TriggerUp` for every active workflow whose trigger shares a lifted modifier —
  `Workflow.TriggerUp`'s state guards make it a no-op for taps and
  `continue_on_release: true`, and the exact-chord path is untouched for
  non-modified triggers.
- Tests: `HookEventModelTests.ModifierBitmap*`/`ModifierFlagOf*` and
  `CompositionTests.HeldChordTrigger_ReleaseModifierFirst_*` (cancel-hold,
  stops-run, continue-true-survives, unrelated-modifier-ignored). Unmatchable-
  release orphan eliminated.

**Progress:** 150 → **163 tests green** (0 fail / 0 skip), build 0 errors /
0 warnings, REV `rev-20260912-17` verified by build.ps1 (tray app relaunched).

### C-4 (2026-09-12, REV rev-20260912-16) — Implement mid-run window gating with skip-but-kept-ready catch-up (PLAN.md 4.3) — applied
Recorded design (reference\PROJECT-STATE.md:188-194, decided 2026-09-11):
"Focus gates only the INJECTION: if a macro is window-gated and the target is
not foreground when a ready step would fire, the press is skipped-but-kept-ready
and fires on the next focused opportunity (catch-up); the next countdown starts
from the actual firing time." Cooldowns/countdowns run on wall-clock, never
paused by focus changes.

Applied 2026-09-12:
- `Composition.Tick` evaluates the gate ONCE per slice from the current
  foreground + the active profile's window target and sets it on the
  dispatcher (`Dispatcher.InjectionAllowed`); attach wires each workflow to it.
- `Workflow.Advance` (Running state) consults the gate BEFORE the pacing
  (`Workflow.cs:183`): a closed gate returns without touching the pacing, so
  the ready step is held, the pacing cursor never advances, and wall-clock
  continues. Reopening fires the held step immediately (catch-up) and the next
  countdown is computed from that actual firing time (both Fixed and Cooldown
  pacing key off `PacingState.LastFireMs`, which only updates on a real fire).
- The trigger-time gate in `Composition.HandleInput` is unchanged (gates starts).
- Tests: `MidRunGate_HoldsDueStepWhileAway_FiresCatchUpOnReturn_AndReArmsFromActualFire`
  (hold while away, catch-up on return, no early pass-2, re-armed boundary);
  existing in-game tests now set the live `_foreground` field the mid-run gate
  reads. **150 tests green** (was 148). REV rev-20260912-16 verified by
  build.ps1.
