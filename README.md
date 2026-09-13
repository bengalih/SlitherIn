# SlitherIn

A tiny Windows tray tool that plays back predefined **key sequences** when you
press a **trigger** hotkey, from a JSON config that hot-reloads as you save.
Input goes out through a selected engine — simulated keystrokes
(`sendinput`, the default) or a **virtual USB keyboard** (`viiper`) where
injection-based approaches fail.

```
slitherin.exe  ->  (engine)  ->  Windows input path of your choice
                                        |
   "sendinput": SendInput (simulated; works in most apps)
   "viiper":    viiper.exe -> usbip-win2 driver -> a REAL USB keyboard
```

With the `viiper` engine the keystrokes come from a genuine USB HID device, so
they work in games, anti-cheat-protected applications, remote-desktop sessions
and full-screen apps that ignore `SendInput`. The kernel side is the generic,
signed usbip-win2 driver; nothing is compiled per-game and no kernel driver
is shipped with SlitherIn.

## Purpose

You define one or more named **macros** in a JSON profile, each with a
**trigger** combo (keyboard chord or mouse button) and an ordered list of
**steps** (key presses, holds, mouse clicks, sounds, waits). When you press the
trigger the macro plays its sequence at your chosen pace — once, a set number of
times, or forever — and any of three **abort tiers** can stop it mid-run.

Use cases:

- Combat/cooldown cycles in games: hold a key, and a rotation fires every ready
  action until you release or it is toggled off.
- Multi-account play: several profiles, each bound to its own game window, with
  automatic switching by which window is foreground.
- Chained chat/emote sequences, buff routines, and any repetitive input you do
  not want to type by hand.

## Requirements

| Component | What it is | Needed when |
|---|---|---|
| **SlitherIn** (this repo) | Tray app; reads config; dispatches macros | always |
| **VIIPER** server | User-space server that creates the virtual USB keyboard over TCP `127.0.0.1:3242` | only for the `viiper` input engine |
| **usbip-win2** driver | Signed kernel USB/IP client that presents the virtual device as real hardware | only for the `viiper` input engine |

SlitherIn itself has **no runtime install**: it is a .NET Framework 4.8 WinExe,
and 4.8 is built into Windows 10/11. Building from source needs the .NET 10 SDK
(see [BUILDING.md](BUILDING.md)).

To install VIIPER **and** the usbip-win2 driver (one-time, from an
**administrator** PowerShell):

```powershell
irm https://alia5.github.io/VIIPER/stable/install.ps1 | iex
```

Reboot when asked. VIIPER installs to `%LOCALAPPDATA%\VIIPER\viiper.exe`, which
SlitherIn finds automatically (it also looks next to the exe and in
`%ProgramFiles%\VIIPER`, and prepends the USBip `bin` folder to `PATH` when it
launches the server itself).

## Quick start

1. Put the built `slitherin.exe` in any folder (run `.\build.ps1` at the repo
   root to build it; see [BUILDING.md](BUILDING.md)).
2. Create the config files next to the exe. The app **creates nothing on
   launch** — if `slitherin.settings.json` is missing, plain defaults are used,
   and if no
   profile is present nothing fires. A good start is copying the two samples
   from [`sample-json/`](sample-json/):
   - `slitherin.settings.json` — global settings (optional).
   - `slitherin.json` — your first profile (required to do anything).
   Edit them to taste, then save. Profiles are also loaded from extra folders —
   see [Config folders & command line](#config-folders--command-line) below.
3. Launch `slitherin.exe`. The tray icon appears; a second launch while one runs
   is ignored (a balloon explains — when notifications are on). Everything the
   tray does is also available
   on the JSON — nothing is tray-only except the toggles.
4. Press the trigger. Save edits anytime — profiles and settings reload as you
   save, shaping the **next** trigger press (a run already in flight finishes
   unchanged).
5. Set `"log": "debug"` in the settings to write `slitherin.log` next to the
   exe. Every hook event is logged as the exact config string plus the focused
   window — press an unknown key and read straight off the log what to paste
   into a trigger or step (the key-finder).

> **Focus matters for the `viiper` engine.** A virtual USB keyboard is a real
> keyboard: its output goes to whichever window has focus, exactly as if you
> typed it. There is no "send to window X". `sendinput` emulation follows the
> same rule. The per-profile `window` target only *gates* when a profile is
> allowed to fire — it does not redirect output.

## Config

Up to two kinds of file, both parsed as JSON with native support for `//` and
`/* */` comments and trailing commas. Every recognized key has a documented
default; anything else is a validation error shown in the tray (never a crash).

### Global settings — `slitherin.settings.json` (optional)

```jsonc
{
    "schema_version": 1,
    "input_engine": "sendinput",   // "sendinput" | "viiper"
    "abort": "Ctrl+Alt+F12",       // global abort combo
    "kill": "Ctrl+Alt+Esc",        // unconditional kill combo
    "default_delay_ms": 3000,      // pause between steps unless a step overrides
    "window_check": true,          // global on/off for per-profile window gating
    "notifications": { "enabled": false },   // tray balloon popups
    "log": "off",                  // "off" | "on" | "debug"
    "active_profile": "slitherin.json",      // kept in sync by the tray menu
    "profile_mode": "manual",      // "manual" | "auto" (switch by window)
    "profile_dirs": [ "./profiles" ]         // extra folders to load profiles from
}
```

> `run_at_startup` is **not** a JSON option — it is carried in the current
> user's `Run` registry key and toggled from the tray menu.

- `input_engine` — the default engine for every profile; a profile may override
  it with its own `input_engine`. If a `viiper` profile loads while VIIPER is
  unavailable, that profile does **not** run and the tray shows an alert —
  there is **no silent fallback** to `sendinput`.
- `default_delay_ms` — the global default pause between steps (3000). A profile
  may override it, and a step's `wait_ms` overrides it per-step (`0` = no
  delay).
- `abort` — interrupts all running macros at any point, except macros with
  `"ignore_abort": true` (they only stop on `kill`, toggle-off, or their own
  `abort_keys`).
- `kill` — unconditional stop of **everything**, including `ignore_abort`
  macros. The escape hatch that can never be opted out of.
- `window_check` — master switch for the per-profile `window` filter (tray
  toggle). Off = profiles fire anywhere (handy while testing in Notepad).
- `notifications` — `{ "enabled": true|false }`; gates the tray balloons (also
  tray-toggleable). Off by default.
- `log` — `"off"` (default; no log file created) | `"on"` (REV + errors/
  warnings) | `"debug"` (every event as a config-paste-ready line — the
  key-finder).
- `active_profile` / `profile_mode` — which profile is active, and whether
  profiles switch automatically by foreground window. Both are updated on disk
  when you pick in the tray Profile menu.
- `run_at_startup` — carried, not a JSON option: stored in the current user's
  `Run` registry key, tray-toggleable.
- `profile_dirs` — optional list of folders to load profiles from **in addition
  to** the folder next to the exe. Each entry may be absolute or relative to the
  settings file's folder; a missing folder is skipped (non-blocking).
  `Sources`-wise the search order is: exe folder first, then each `profile_dirs`
  entry in order, then any `--profile-dir` CLI paths. The first folder that
  contains a given profile name wins, so `slitherin.json` in the exe folder
  shadows a same-named file in a listed folder (use distinct names to keep them
  all).

### Config folders & command line

Where config comes from:

| Source | Precedence |
|---|---|
| `slitherin.exe` folder | **always first** for both settings and profiles |
| `profile_dirs` in the settings file | next; entries resolve relative to the settings file's folder |
| `--profile-dir <dir>` on the command line | last, repeatable; resolves against the current working directory |

The settings file itself is `slitherin.settings.json` next to the exe, unless
overridden with `--settings-file <path>`. The folder containing the settings
file is always a watched root (alongside every profile dir), and a listed
profile dir is watched even if it doesn't exist yet (it is picked up as soon as
it is created). Relative `--profile-dir` paths are resolved against where the
app was launched, not the exe folder — run the app from the folder you mean, or
give absolute paths. The log file always lives next to the exe regardless of
the above, and so do the external icon overrides. **Sound files are not
location-resolved** — a relative `sound.file` is taken as-is against the app's
current directory; there is no exe-dir or `C:\Windows\Media` lookup.

### Profile — `slitherin.json`, or `slitherin.<N>.<label>.json` for more

Additional profiles follow `slitherin.<N>.<anything>.json`. `<N>` only sorts
them in the tray Profile menu (0, 1, 2 …); the `anything` part is ignored; the
name shown is the `name` field inside each file. (The settings file itself is
never treated as a profile.) Profiles may live next to the exe or in any
`profile_dirs` folder — `slitherin.<N>.<label>.json` files found across all
folders together are sorted by `<N>`.

```jsonc
{
    "schema_version": 1,
    "name": "Main (CharacterA - Sarlona)",

    // Window gate + AUTO-selection target. exe and/or title; both optional.
    // exe-only is the common multi-account shape ("dndclient64.exe").
    "window": { "exe": "dndclient64.exe", "title": "CharacterA - Sarlona" },

    // Reusable before/after step sets, referenced from steps by name.
    "wrappers": {
        "bar6": {
            "before": [ { "keys": "Ctrl+6" } ],
            "after":  [ { "keys": "Ctrl+1" } ]
        }
    },

    "macros": [
        {
            "name": "Combat Cycle",
            "trigger": "LButton",
            "fire_after_ms": 1000,      // hold the trigger this long before starting
            "toggle": "Pause",          // arm/disarm key (beeps; see toggle_sound)
            "toggle_sound": {
                "on":  { "frequency": 1500, "duration_ms": 80 },
                "off": { "frequency": 1000, "duration_ms": 80 }
            },
            "loop": { "count": -1 },    // omitted = 1; -1 = forever
            "schedule": { "mode": "cooldown", "fire_delay_ms": 700 },
            "steps": [
                { "keys": "0", "cooldown_ms": 3000 },
                { "keys": "7", "cooldown_ms": 15000, "wrapper": "bar6" }
            ]
        }
    ]
}
```

**Macro keys**

| Key | Meaning |
|---|---|
| `name` | Shown in tray; optional |
| `trigger` | Chord or mouse button that starts the macro (canonical spelling, e.g. `Ctrl+K`, `LButton`) |
| `fire_after_ms` | Optional. The trigger must be **held** this long; releasing earlier cancels (a tap macro fires on press instead) |
| `toggle` | Optional arm/disarm combos. With a toggle the macro starts disarmed; pressing the toggle arms it (beep), again disarms and stops it. Without a toggle the macro is always armed |
| `toggle_sound` | Optional `{ "on":..., "off":... }` sound specs; a **present** slot beeps, an absent one is silent |
| `loop` | `{ "count": N }` — omitted = 1 pass, `-1` = forever. `continue_on_release` governs HELD triggers only: `false` (default) stops the run when you release, `true` keeps the whole `count` running. Tap triggers always run their full count; release never stops them |
| `schedule` | `{ "mode": "fixed"|"cooldown" }`. Omitted = fixed pacing at `default_delay_ms`. `cooldown` fires every action whose per-step `cooldown_ms` has elapsed each pass, paced by `fire_delay_ms` |
| `steps` | Ordered step list (shapes below) |
| `abort_keys` | Stops **only this macro** |
| `ignore_abort` | `true` = the global `abort` does not stop this macro (it still yields to `kill` and toggling off) |

For a held trigger with `fire_after_ms`, releasing mid-run stops it (derived
`continue_on_release: false`); set `continue_on_release: true` to let it run its
full `count` to completion after you release. A momentary tap always runs to
completion — key-up is the end of the tap, never a stop. Releasing a chord is
any of its members leaving the down-set (drop the modifier first, drop the main
key first, either order). A `loop.count` of `-1` keeps cycling until toggle-off,
release (when the loop says so), abort, or kill.

**Step shapes** — exactly one of these, in any order inside an object:

```jsonc
{ "keys": "Ctrl+6" }                    // press a chord
{ "keys": "W", "hold_for_ms": 2000 }    // keep it held that long,
                                        //   then release
{ "button": "LButton" }                 // mouse click (LButton/RButton/
                                        //   MButton/XButton1/XButton2)
{ "sound": {} }                         // beep: default 500 Hz, 1 s
{ "sound": { "frequency": 800, "duration_ms": 1000 } }
{ "sound": { "file": "Alarm01.wav" } }  // wav/mp3 path used as-is (a bare name
                                        //   resolves against the app's current
                                        //   folder; no special lookup)
{ "wait_ms": 500 }                      // pause (replaces the default delay)
```

Shared step options: `cooldown_ms` (min cooldown between firings of that step in
`cooldown` mode), `wrapper` (name of a `wrappers` set whose `before` steps run
first and `after` steps run last), and `wait_ms` on a key/button step (overrides
the default inter-step delay after it; `0` = no delay).

**Key vocabulary** (canonical; input is case-insensitive, output canonical):

- Single letters `A`–`Z`, top-row digits `0`–`9`.
- `F1`–`F24`.
- Numpad (explicit only): `Numpad0`–`Numpad9`, `NumpadAdd`, `NumpadSubtract`,
  `NumpadMultiply`, `NumpadDivide`.
- Named keys: `Space`, `Enter`, `Tab`, `Esc`, `Backspace`, `CapsLock`, `Pause`,
  `Home`, `End`, `PageUp`, `PageDown`, `Insert`, `Delete`, `Up`, `Down`,
  `Left`, `Right`, `PrintScreen`, `ScrollLock`, `NumLock`.
- Mouse buttons: `LButton`, `RButton`, `MButton`, `XButton1`, `XButton2`.
- Modifiers `Ctrl`, `Alt`, `Shift`, `Win` combine with any key via `+`
  (`Ctrl+Shift+K`). Anything else (`~`, media keys, punctuation, wheel scroll,
  a second key token, an unknown word) is a validation error.
- Punctuation: there is no punctuation vocabulary. Shifted symbols come from a
  shifted key, e.g. `Shift+1` types `!` — the config holds the physical key,
  not the character it produces.

A macro whose steps "type" into a `viiper` profile must be keyboard-only: the
`viiper` engine is a HID keyboard device and cannot emit mouse output. Declaring
a mouse `button` step, a mouse-key chord, or mouse inside a wrapper in a
`viiper` profile is a hard validation error (the profile is rejected at load),
and `sendinput` handles both keyboards and the mouse.

Use a **`sendinput`** profile for mouse output, or an engine that supports it;
use **`viiper`** when you need a real HID keyboard and your macro is
keyboard-only.

**Window gating.** The optional `window` block (`exe` and/or `title`) restricts
the profile to its target, and is what AUTO mode selects on. With
`profile_mode: "auto"` the app switches to whichever profile's `window` block
matches the foreground window — no manual selection. Both `exe` and `title`
support `*` / `?` wildcards (`"dndclient64*"`, `"*-Sarlona"`), so a title that
varies (session ids, decimals) can still be matched with a pattern; an
`exe`-only target is the loosest shape (matches any window of that process).

### Sample files

The [`sample-json/`](sample-json/) folder holds ready-to-read examples. They are
**never loaded by the app** — copy one next to `slitherin.exe` as a real
profile (`slitherin.json` or `slitherin.<N>.<label>.json`) and edit.

| File | What it demonstrates |
|---|---|
| `slitherin.settings.sample.json` | Every global option with comments |
| `profiles/slitherin.json` | A combat-cycle macro: LButton hold-to-fire, toggle arm, cooldown pacing, `cooldown_ms` per step, and a `wrapper` |
| `profiles/slitherin.char-b.json` | A second account profile: title-specific window target for AUTO switching, a one-shot macro, a `hold_for_ms` key step, and a profile-level `default_delay_ms` |
| `profiles/slitherin.viiper.json` | The same combat cycle on the `viiper` engine — a per-profile `input_engine` override with the keyboard-only rule in force |

### Tray menu

- Alert rows (warning glyph) — config load errors / `viiper` hard-fail.
- `Profile: <name>` — click to open the active profile JSON.
- `Profiles` submenu — `Auto (switch by window)` (checked when on) + one entry
  per profile file (checked when active).
- `Macros:` blue header, then one `• <name>: <trigger>` row per macro (the
  tooltip carries the step count).
- `Abort: <combo>` — display only.
- `Window-check`, `Notifications`, `Run at startup` — on/off toggles.
- `Open Settings (<settings-file name>)` (passes the actual filename).
- The green version row (click for the releases page), and `Exit`. There is no
  manual reload row — the file watcher is the only reload trigger.

## Architecture

Runtime flow, component layout, the locked config vocabulary, decisions, and
history are all documented in [ARCHITECTURE.md](ARCHITECTURE.md) — the single
technical document. (`PLAN.md` / `CHANGES.md` / `DESIGN.md` / `PROJECT-STATE.md` are retired stubs;
archives in `old-docs/`.)

## Build from source

[BUILDING.md](BUILDING.md) covers the toolchain (Windows 10/11 + .NET 10 SDK,
net48 target). The one-command flow:

```powershell
.\build.ps1
```

which stops any running instance, builds the solution (app + tests), verifies
the built EXE contains the current `REV`, and relaunches the app. The output is
`src\bin\Release\net48\slitherin.exe`. Run the tests directly with
`dotnet test tests\SlitherIn.Tests -c Release`.

## Files

| Path | Purpose |
|---|---|
| `src/` | Source (WinForms shell `App`, UI-free core `Composition`, `Core/`, `Hook/`, `Input/`, `Ui/`) |
| `src/Program.cs` | Entry point + the single `REV` build-identity constant |
| `tests/SlitherIn.Tests/` | Unit tests (config, validation, workflows, engines) |
| `build.ps1` | Build + REV-verify + relaunch |
| `BUILDING.md` | Toolchain and build instructions |
| `ARCHITECTURE.md` | The one technical document: runtime, config vocabulary, decisions, inventory, history |
| `old-docs/` | Archived snapshots of the retired PLAN/CHANGES/DESIGN/PROJECT-STATE records |
| `sample-json/` | Example configs — **not loaded**. `README.md` is the index; `tutorial/` is one-file-per-concept teaching; `profiles/` + the settings sample are ready-to-run templates (copy next to the exe to use) |
| `LICENSE` / `NOTICE` | GNU GPL v3; third-party attribution |
| `slitherin.settings.json` | Global settings — per-user, not committed |
| `slitherin*.json` | Profiles — per-user, not committed |

## License

SlitherIn is free software under the **GNU GPL v3-or-later**. You may fork,
modify, and redistribute it — forks must keep the copyright notice and must
stay free (any distributed modified version must be GPL with its source
available). Attribution for the components SlitherIn talks to lives in
`NOTICE` (VIIPER GPL-3.0, usbip-win2 BSD-2-Clause).

