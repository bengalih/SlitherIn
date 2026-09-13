# sample-json — examples & teaching templates

These are examples for the **redesigned** SlitherIn (unified `start`/`loop`/
`schedule`/`steps` vocabulary). Nothing here is loaded by the app — that is the
point.

## How to use this folder

- **`tutorial/`** — one-file-per-topic lessons (`01-…`, `02-…`). Each file
  teaches ONE function or flow with heavy comments, so treat it as a template.
  Their file names deliberately do **not** start with `slitherin`, so copying
  this folder next to the EXE will never activate them accidentally.
- **`profiles/`** — ready-to-run profile templates (files start with
  `slitherin`, exactly like the loader's `slitherin*.json` pattern). Copy the
  ones you want next to `slitherin.exe`.
- **`slitherin.settings.sample.json`** — the global settings template (rename
  to `slitherin.settings.json` if you want one).

Rule of thumb: the loader reads `slitherin.settings.json` (settings) and
`slitherin*.json` minus the settings file (profiles). Profile folders, in search
order: the EXE's folder **always first**, then the settings `profile_dirs`
(relative entries = folders next to the settings file), then any `--profile-dir`
argument on the command line. `--settings-file <path>` moves the settings file
itself somewhere else. `profile_mode` ("manual" | "auto") and `active_profile` in
the settings choose which profile the tray wires up.

## Tutorial map (one concept each)

| File | Teaches |
|---|---|
| `01-trigger-and-keys.json` | A trigger chord + plain key steps ("press and release"). |
| `02-delays-and-wait-ms.json` | `default_delay_ms` (profile ⇽ settings ⇽ 3000) vs per-step `wait_ms`; pure `wait_ms` steps. |
| `03-loop-count.json` | `loop.count`: omitted = once, N = N times, `-1` = forever (tap trigger). |
| `04-hold-to-fire.json` | `fire_after_ms`: the trigger must stay held N ms before it counts. |
| `05-toggle-arm.json` | `toggle` arm/disarm key + `toggle_sound` cues. |
| `06-continue-on-release.json` | `loop.continue_on_release`: held vs tap triggers; `count` is always honored. |
| `07-chord-sequences.json` | A `keys` LIST = one logical action (chords fired inside the step, no pacing) + `button` steps. |
| `08-hold-for-ms.json` | Step-level `hold_for_ms`: keep one key held N ms before releasing. |
| `09-sound-steps.json` | `sound` steps: default tone, frequency/duration overrides, `.wav` files. |
| `10-wrappers.json` | Named reusable `before`/`after` step sets (the "hotbar" pattern). |
| `11-cooldown-cycle.json` | `schedule.mode: "cooldown"` + `cooldown_ms` — the old cycle/combat flow. |
| `12-abort-and-kill.json` | Abort tiers: per-macro `abort_keys`, `ignore_abort`, global `abort` + `kill`. |
| `13-mouse-input.json` | Mouse buttons (incl. side buttons) as triggers and `button` steps. |
| `14-window-gating.json` | Window targets, `window_check`, and AUTO profile switching. |
| `15-input-engines.json` | `sendinput` vs `viiper`, the per-profile override, and the hard-fail rule. |

Full reference for every switch: `ARCHITECTURE.md` §8 at the project root.