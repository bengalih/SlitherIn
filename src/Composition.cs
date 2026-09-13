using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SlitherIn.Core.Config;
using SlitherIn.Core.Diagnostics;
using SlitherIn.Core.Engine;
using SlitherIn.Core.Target;
using SlitherIn.Hook;
using SlitherIn.Input;

namespace SlitherIn
{
    /// <summary>
    /// The testable composition: owns the catalog, dispatcher, engines, abort
    /// tiers, reload policy, alert model, and input routing — and is the ONLY place
    /// that connects the Core pieces. UI-free (no P/Invoke, no System.Windows.Forms)
    /// so the test project exercises every Stage 7 rule directly.
    ///
    /// The WinForms shell (App) is a thin driver around this type: it feeds hook
    /// events into <see cref="HandleInput"/>, ticks a timer into <see cref="Tick"/>,
    /// reloads on file changes via <see cref="Reload"/>, persists tray toggles,
    /// rebuilds the tray menu on <see cref="Changed"/>, and plays sounds on
    /// <see cref="CueRequested"/>.
    ///
    /// Routing order (locked): kill → global abort (skip IgnoreAbort) → per-macro
    /// abort_keys → KeyUp trigger-up → toggle → (window gate + engine guard) trigger.
    /// The window gate consults settings.window_check + the ACTIVE profile's window
    /// target; the engine guard is the VIIPER hard-fail (blocked = nothing runs).
    /// </summary>
    public sealed class Composition : IDisposable
    {
        /// <summary>Auto-mode foreground poll cadence (ms).</summary>
        public const int AutoPollMs = 250;

        private readonly ConfigSources _startupSources;
        private ConfigSources _sources;
        private readonly Log _log;
        private readonly WallClock _clock;
        private readonly string _buildRev;
        private readonly Func<string, IKeyEngine> _engineFactory;
        private readonly Func<ForegroundInfo> _foreground;
        private readonly Dictionary<string, IKeyEngine> _engines = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<Workflow> _cueSubscribed = new();
        private readonly List<string> _alerts = new();

        private Settings _settings;
        private readonly ProfileCatalog _catalog = new();
        private readonly Dispatcher _dispatcher;
        private WindowTarget _activeTarget = WindowTarget.FromSpec(null);
        private Chord? _abortChord;
        private Chord? _killChord;
        private ModifierFlags _mods;
        private bool _suppressCues;
        private long _lastAutoPollMs = long.MinValue;

        public Composition(ConfigSources sources, Log log,
            Func<string, IKeyEngine> engineFactory = null,
            Func<ForegroundInfo> foreground = null,
            WallClock clock = null,
            string buildRev = null)
        {
            _startupSources = sources ?? throw new ArgumentNullException(nameof(sources));
            _log = log;
            _buildRev = buildRev ?? "rev-devel";
            _clock = clock ?? WallClock.Stopwatch;
            _engineFactory = engineFactory ?? (name => name == "viiper"
                ? (IKeyEngine)new ViiperEngine()
                : new SendInputEngine());
            _foreground = foreground ?? LowLevelHooks.SnapshotForeground;
            _settings = Loader.LoadSettingsFile(_startupSources.SettingsPath).Config ?? new Settings();
            _sources = _startupSources.Rebuild(_settings); // profile_dirs from the real settings
            _dispatcher = new Dispatcher(_engineFactory(Normalizer.DefaultInputEngine), _clock, log,
                defaultDelayMs: Normalizer.DefaultDelayMs);
            ApplyLogLevel();
        }

        // ==== Surface ==============================================================

        public Settings Settings => _settings;
        public ProfileCatalog Catalog => _catalog;
        public Dispatcher Dispatcher => _dispatcher;
        /// <summary>Resolved config locations (settings path, profile dirs, watch
        /// roots) — re-resolved when a reload changes settings `profile_dirs`.</summary>
        public ConfigSources Sources => _sources;
        public string ActiveProfileFile => _catalog.ActiveFile;
        public IReadOnlyList<Workflow> ActiveWorkflows => _catalog.ActiveWorkflows;
        public IReadOnlyList<string> Alerts => _alerts;

        /// <summary>True when the active profile selected an unavailable engine
        /// (VIIPER hard-fail) — the app does NOT function for that profile.</summary>
        public bool ActiveProfileBlocked { get; private set; }

        public event EventHandler Changed;
        public event EventHandler<SoundSpec> CueRequested;

        // ==== Boot / reload ========================================================

        /// <summary>Initial load (Boot): same semantics as a file-triggered reload
        /// but reads fresh state — nothing to keep, nothing was running.</summary>
        public void Boot() => Reload(null, isSettings: false);

        /// <summary>
        /// Full reload — the "file reload = abort + disarm all" rule, then rebuild
        /// from disk. Reload semantics (locked): signal every run, disengage every
        /// toggle-defined macro, load settings fresh (log level may change), rebuild
        /// the catalog, and re-select the active profile — but if the ACTIVE file now
        /// fails, KEEP its previous runtime running (keep-old rule) while the error
        /// becomes a tray alert. Never throws into the caller: errors land in
        /// <see cref="Alerts"/>.
        /// </summary>
        public void Reload(string filePath, bool isSettings)
        {
            _log?.Debug($"reload requested ({(isSettings ? "settings" : "profile")} {filePath ?? "<boot>"})");

            bool prevSuppress = _suppressCues;
            _suppressCues = true;
            try
            {
                _dispatcher.AbortAll();
                foreach (Workflow wf in _catalog.ActiveWorkflows.ToList())
                    if (wf.Definition.Toggle != null && wf.State != WorkflowState.Idle)
                        wf.Disarm();
                _dispatcher.DetachAll();
            }
            finally
            {
                _suppressCues = prevSuppress;
            }

            // Snapshot the previous active runtime BEFORE the catalog rebuilds.
            string priorActive = _catalog.ActiveFile ?? _settings.ActiveProfile;
            Profile priorProfile = _catalog.ActiveProfile;
            List<Workflow> priorWorkflows = _catalog.ActiveWorkflows.Any()
                ? _catalog.ActiveWorkflows.ToList()
                : null;

            _alerts.Clear();

            // Settings — broken settings keep the old object and become an alert.
            LoadOutcome<Settings> so = Loader.LoadSettingsFile(_sources.SettingsPath);
            if (so.Succeeded)
            {
                _settings = so.Config;
                _sources = _sources.Rebuild(_settings); // profile_dirs may have changed
            }
            else
            {
                foreach (LoadError e in so.Errors)
                    _alerts.Add("settings: " + e.Message);
                _log?.Warn("settings file failed to load; keeping previous settings.");
            }
            ApplyLogLevel();

            // Profiles — broken profiles never block the app.
            LoadOutcome<List<LoadedProfile>> outcome = _catalog.LoadAll(_sources, _settings);
            foreach (LoadError e in outcome.Errors)
            {
                string src = string.IsNullOrWhiteSpace(Path.GetFileName(e.File)) ? "config" : Path.GetFileName(e.File);
                _alerts.Add($"{src}: {e.Message}");
            }

            // Reselect the active profile (manual = settings.ActiveProfile; auto =
            // the foreground owner, falling back to the prior selection).
            string target;
            if (_settings.ProfileMode == ProfileCatalog.Auto)
            {
                target = _catalog.ResolveOwner(_foreground?.Invoke() ?? ForegroundInfo.Empty) ?? priorActive;
            }
            else
            {
                target = _settings.ActiveProfile ?? priorActive;
            }

            if (target != null)
            {
                if (_catalog.HasProfile(target))
                {
                    _catalog.Select(target);
                }
                else if (SameFile(priorActive, target) && priorProfile != null)
                {
                    _log?.Warn($"profile '{target}' failed/lost — keeping its previous runtime active.");
                    _catalog.KeepActive(target, priorProfile, priorWorkflows);
                    _catalog.Select(target);
                }
                else
                {
                    _alerts.Add($"active profile '{target}' was not found on reload.");
                }
            }

            ApplyActiveState();
            AttachActive();
            _log?.Info(ActiveProfileBlocked
                ? "composition ready (active profile BLOCKED: engine unavailable)."
                : $"composition ready (active profile {(ActiveProfileFile ?? "(none)")}).");
        }

        /// <summary>Re-apply settings-derived state WITHOUT reloading files or
        /// stopping runs: engine/gate/blocked/alerts + menu refresh. Used after a
        /// tray toggle persisted settings, so a window-check flip never kills a
        /// running macro.</summary>
        public void Refresh()
        {
            ApplyLogLevel(); // pick up log-level edits
            ApplyActiveState();
            _catalog.Mode = _settings.ProfileMode;
        }

        /// <summary>Manual profile switch (tray). Parks the outgoing runtime (state
        /// retained, wall-clock keeps counting) and resumes the incoming. Persists
        /// <c>active_profile</c> in-memory; the App writes it back to disk.</summary>
        public void Select(string file)
        {
            if (string.IsNullOrWhiteSpace(file) || !_catalog.HasProfile(file)) return;
            _settings.ActiveProfile = file;
            SwitchSelection(file);
        }

        /// <summary>
        /// Modifier-bitmap feed from the hooks (C-6). A HELD chord-trigger run is
        /// released when ANY chord member leaves the down-set — including a
        /// modifier lifted first, which produces no canonical chord event of its
        /// own (HookModel filters modifiers out of <see cref="InputEvent"/>). Keeps
        /// the exact-chord fast path for non-modified triggers unchanged: their
        /// <see cref="Chord.Modifiers"/> is empty, so no modifier lift can touch them.
        /// </summary>
        public void HandleModifierState(ModifierFlags now)
        {
            ModifierFlags removed = _mods & ~now;
            _mods = now;
            if (removed == ModifierFlags.None) return;

            long t = _clock.NowMs;
            foreach (Workflow wf in _catalog.ActiveWorkflows)
                if ((wf.Definition.Trigger.Modifiers & removed) != 0)
                    wf.TriggerUp(wf.Definition.Trigger, t);
        }

        // ==== Driver loop ==========================================================

        /// <summary>One driver slice: snapshot the mid-run injection gate ONCE from
        /// the current foreground + active target, advance the dispatcher, then (in
        /// AUTO mode, throttled) re-resolve the foreground owner. Called by the App's
        /// 10 ms timer. Every workflow reads the same per-slice gate — a closed gate
        /// holds ready steps without pausing any countdown, and the Win32 foreground
        /// snapshot happens once per slice, not once per workflow.</summary>
        public void Tick()
        {
            bool allowed = WindowGate(_foreground?.Invoke() ?? ForegroundInfo.Empty);
            _dispatcher.InjectionAllowed = () => allowed;
            _dispatcher.Tick(_clock.NowMs);
            PollAuto();
        }

        /// <summary>
        /// Routes a low-level hook event through the lock  ed tier order. Always
        /// passes through (the hook already guarantees passthrough).
        /// </summary>
        public void HandleInput(InputEvent e)
        {
            bool isDown = e.IsKeyDown || e.Kind == InputEventKind.MouseDown;

            if (_killChord.HasValue && e.Chord.Equals(_killChord.Value) && isDown)
            {
                _log?.Warn("kill combo — aborting everything.");
                _dispatcher.AbortAll();
                return;
            }

            if (_abortChord.HasValue && e.Chord.Equals(_abortChord.Value) && isDown)
            {
                _log?.Info("global abort — interrupting every running macro (except ignore_abort).");
                foreach (Workflow wf in _catalog.ActiveWorkflows)
                    if (wf.Definition.IgnoreAbort) continue;
                    else wf.Abort();
                _dispatcher.ReleaseAll();
                return;
            }

            foreach (Workflow wf in _catalog.ActiveWorkflows)
            {
                if (wf.Definition.AbortKeys.HasValue && wf.Definition.AbortKeys.Value.Equals(e.Chord) && isDown)
                {
                    _log?.Debug($"per-macro abort '{wf.Definition.Name}'.");
                    wf.Abort();
                    return;
                }
            }

            if (!isDown)
            {
                foreach (Workflow wf in _catalog.ActiveWorkflows)
                    if (wf.Definition.Trigger.Equals(e.Chord))
                        wf.TriggerUp(e.Chord, _clock.NowMs);
                return;
            }

            foreach (Workflow wf in _catalog.ActiveWorkflows)
            {
                if (wf.Definition.Toggle.HasValue && wf.Definition.Toggle.Value.Equals(e.Chord))
                {
                    wf.TogglePressed();
                    return;
                }
            }

            if (!WindowGate(e.Foreground)) return;

            foreach (Workflow wf in _catalog.ActiveWorkflows)
                if (wf.Definition.Trigger.Equals(e.Chord) && !ActiveProfileBlocked)
                    wf.TriggerDown(e.Chord, _clock.NowMs);
        }

        // ==== Internals ============================================================

        private void SwitchSelection(string file)
        {
            _catalog.Select(file);
            _dispatcher.DetachAll();
            ApplyActiveState();
            AttachActive();
        }

        private void PollAuto()
        {
            if (_settings.ProfileMode != ProfileCatalog.Auto) return;
            long now = _clock.NowMs;
            long delta = now - _lastAutoPollMs;
            if (delta >= 0 && delta < AutoPollMs) return; // within the throttle window
            _lastAutoPollMs = now;

            ForegroundInfo fg = _foreground?.Invoke() ?? ForegroundInfo.Empty;
            string owner = _catalog.ResolveOwner(fg);
            if (owner != null && !SameFile(owner, _catalog.ActiveFile))
            {
                _log?.Info($"auto-mode switch: foreground now matches '{owner}'.");
                _settings.ActiveProfile = owner;
                SwitchSelection(owner);
            }
        }

        /// <summary>Window gate: off when settings.window_check is false or the
        /// active profile has no target; otherwise a real target match on the
        /// event's foreground snapshot.</summary>
        private bool WindowGate(ForegroundInfo fg)
        {
            if (!_settings.WindowCheck) return true;
            if (!_activeTarget.HasAny) return true;
            return _activeTarget.Matches(fg.ProcessExe, fg.Title);
        }

        /// <summary>Resolve the active profile's engine (profile override ?? settings
        /// ?? sendinput), swap the dispatcher onto it, and evaluate the hard-fail.</summary>
        private void ApplyActiveState()
        {
            _catalog.Mode = _settings.ProfileMode;

            Profile p = _catalog.ActiveProfile;
            _activeTarget = p != null ? WindowTarget.FromSpec(p.Window) : WindowTarget.FromSpec(null);
            _killChord = ParseCombo(_settings.Kill);
            _abortChord = ParseCombo(_settings.Abort);

            string engineName = p?.InputEngine ?? _settings.InputEngine ?? Normalizer.DefaultInputEngine;
            if (!Normalizer.IsSupportedEngine(engineName)) engineName = Normalizer.DefaultInputEngine;
            IKeyEngine engine = EngineFor(engineName);
            _dispatcher.Engine = engine;

            ActiveProfileBlocked = engineName == "viiper" && !engine.Available;
            if (ActiveProfileBlocked)
            {
                _log?.Error($"profile '{ActiveProfileFile ?? "(none)"}' selected viiper but the engine is unavailable — BLOCKED (hard-fail).");
                RebuildAlerts();
            }
            else
            {
                RebuildAlerts();
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }

        private void RebuildAlerts()
        {
            if (_alerts.Count == 0 && ActiveProfileBlocked)
                _alerts.Add($"VIIPER unavailable — profile '{ActiveProfileFile ?? "(none)"}' is blocked.");
            if (_alerts.Count > 0 && _log != null)
                _log.Warn(string.Join(" | ", _alerts));
        }

        private void AttachActive()
        {
            foreach (Workflow wf in _catalog.ActiveWorkflows)
            {
                if (ActiveProfileBlocked)
                {
                    _log?.Debug($"not attaching '{wf.Definition.Name}' — active profile blocked.");
                    continue;
                }
                if (_cueSubscribed.Add(wf))
                {
                    Workflow captured = wf;
                    captured.ToggleArmed += s => Cue(s.Definition.ToggleSound?.On);
                    captured.ToggleDisarmed += s => Cue(s.Definition.ToggleSound?.Off);
                }
                _dispatcher.Attach(wf);
            }
        }

        private void Cue(SoundSpec spec)
        {
            if (spec == null || _suppressCues) return;
            CueRequested?.Invoke(this, spec);
        }

        private IKeyEngine EngineFor(string name)
        {
            if (!_engines.TryGetValue(name, out IKeyEngine engine))
            {
                engine = _engineFactory(name);
                _engines[name] = engine;
                engine.Initialize();
                _log?.Debug($"engine '{name}' initialized.");
            }
            return engine;
        }

        private static Chord? ParseCombo(string spec)
            => !string.IsNullOrWhiteSpace(spec) && KeyName.TryParse(spec, out Chord chord) ? chord : (Chord?)null;

        private static bool SameFile(string a, string b)
            => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private void ApplyLogLevel()
        {
            if (_log == null) return;
            LogLevel next = Log.Parse(_settings.Log);
            if (_log.Level != next || _log.FilePath == null)
                _log.Start(next, _buildRev, _sources.ExeDir);
        }

        public void Dispose()
        {
            _dispatcher?.Dispose();
            foreach (IKeyEngine engine in _engines.Values) engine.Dispose();
            _engines.Clear();
            _catalog.Dispose();
        }
    }
}