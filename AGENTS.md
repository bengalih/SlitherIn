# AGENTS.md — SlitherIn (working conventions)

> This file is the recovery anchor for this project at `D:\SlytherInNew\`.
> Read **`ARCHITECTURE.md`** (the one technical document) plus `BUILDING.md`
> (toolchain) at the start of every session, before doing anything.
> `PLAN.md`, `CHANGES.md`, and `DESIGN.md` are
> retired stubs — their content lives in `ARCHITECTURE.md`, with pre-merge
> snapshots archived in `old-docs\`.
> DIRECTORY ANCHOR (hard rule): opencode resolves this project by its working
> directory, and THIS file is the compaction-proof memory. Sessions MUST be
> launched from `D:\SlytherInNew\` — never from a generic catch-all folder
> (e.g. `C:\Users\bhendin\Documents\Default Project`). If this session's working
> directory is not `D:\SlytherInNew\`, redirect every tool call's working
> directory here immediately and re-read `ARCHITECTURE.md` and `BUILDING.md`.

## Build

- Toolchain: .NET SDK 10 (10.0.401+), no Visual Studio. Output targets **.NET
  Framework 4.8** (`net48`) so end users install nothing.
- Build via `build.ps1` from this root:
  1. Stop any running `slitherin.exe` (`Get-Process slitherin | Stop-Process -Force`).
  2. `dotnet build -c Release` (restores NuGet: reference assemblies + System.Text.Json on first run; needs internet once).
  3. REV-verify: parse `const string REV = "rev-YYYYMMDD-NN"` from `src\Program.cs`, read the built EXE bytes, and assert the REV string's UTF-16 byte pattern appears anywhere in the binary (alignment-agnostic search — see `build.ps1`). Every code change must bump REV so the check proves "the build that just ran really redid the compile."
  4. `Start-Process` the built EXE.
- Manual build equivalent: `dotnet build src -c Release`.
- Output: `src\bin\Release\net48\slitherin.exe`. Remember the EXE-lock:
  a running slitherin blocks the build (MSB3021/3027); `build.ps1` stops it.

## Non-negotiable conventions

- `REV` lives in `src\Program.cs`; bump on EVERY code change before building. It
  is the tray version label and the build-ran proof.
- Config = declarative, strict JSON, comments allowed. `schema_version` gate.
- Config LOCATION is declarative too (C-7, `%SLITHERIN_DIR%` RETIRED): settings
  file = `--settings-file <path>` else next to the exe
  (`slitherin.settings.json`); profile dirs = exe dir (always first) → settings
  `profile_dirs` (relative to the settings folder) → CLI `--profile-dir`
  (CWD-relative); first match wins per filename; watch roots = settings folder +
  every profile dir. Resolution logic lives in `Core\Config\ConfigSources.cs`;
  parsing in `src\CliArgs.cs`.
- No macro/cycle/idler split — one unified shape (`start`/`loop`/`schedule`/`steps`).
- No control flow in config. Fields we explicitly rejected: `fire_on`,
  `hotbar`, `primary_hotbar`, `between_ms`, `sequence`, text steps,
  screen-position clicks. (Full rejection log in `ARCHITECTURE.md` §8.)
- One cooperative dispatcher; injection serialized through the active engine.
- Profile selection is a MODE: `manual <profile>` (sticky) or `auto`
  (foreground-following). Profile switch PARKS runtime; file reload ABORTS +
  DISARMS. Cooldowns/countdowns run on wall-clock, never paused by focus.
- Abort tiers: per-macro `abort_keys` < global default abort (settings) <
  `ignore_abort: true` < `kill_keys` (unconditional). All via the one low-level
  hook set; no RegisterHotKey, no poll watchdog.
- Input engines: `sendinput` (default, always available) / `viiper` (hard-fail
  when selected but unavailable → alert icon + tray message + log detail).
- Code under `src\Core\` stays free of WinForms/P-Invoke/System.Drawing so the
  test project can reference it alone.

## Tests

- `tests\SlitherIn.Tests\` — NUnit + `Microsoft.NET.Test.Sdk`.
- Run: `dotnet test tests\SlitherIn.Tests -c Release`.
- UI and tray are manually verified; only engine/config logic is unit-tested.

## Working agreements

- The user leads the design; the assistant challenges, doesn't take over.
- Never overwrite the user's active config files without checking first.
- Never edit anything under `old-docs\` (read-only archives).
- Docs must match reality: update `ARCHITECTURE.md` at every step (design
  decisions + current code/test/build state; keep the change-history and
  gotchas sections live). Read `ARCHITECTURE.md` + `BUILDING.md` first and
  reconcile before writing new code.
- One change at a time: build → 0 errors, test → all green, REV bumped and
  verified by `build.ps1` — then STOP and check in before the next change.
- Challenges are part of the record: at every close-out, the gotchas section in
  `ARCHITECTURE.md` MUST capture what went wrong or was non-obvious (naming
  drift, contract mismatches, test-only surprises, build environment hiccups)
  and the fix that landed. "It worked" is not a note; "it didn't work, here is
  why, and what changed" is.
- Design decisions get recorded in `ARCHITECTURE.md` (§19); build facts live in
  this file and `BUILDING.md`; user docs in `README.md` (project root).