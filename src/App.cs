using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using SlitherIn.Core.Config;
using SlitherIn.Core.Diagnostics;
using SlitherIn.Hook;
using SlitherIn.Ui;

namespace SlitherIn
{
    /// <summary>
    /// Composition root / thin WinForms shell. Layers:
    ///   Core subsystems live in <see cref="Composition"/> (catalog, dispatcher,
    ///   engines, reload policy, routing) — UI-free and test-covered.
    ///   App owns ONLY the drivers: hook install + forwarding, the 10 ms tick, the
    ///   filesystem watcher (with self-write suppression), the tray icon + menu,
    ///   sound playback, and UI-thread marshalling.
    /// Business rules (abort tiers, reload semantics, engine hard-fail, profile
    /// mode) are enforced inside Composition; App just adapts events to UI.
    /// </summary>
    internal sealed class App : IDisposable
    {
        private const int TickMs = 10;
        private const int SelfSuppressMs = 1500;

        // ---- subsystems (created by Wire, disposed in order) ----
        private readonly string _exeDir = AppDomain.CurrentDomain.BaseDirectory;
        private readonly CliArgs _cli;
        private Log _log;
        private Composition _compose;
        private Watcher _watcher;
        private LowLevelHooks _hooks;
        private TrayHost _tray;
        private System.Windows.Forms.Timer _tick;
        private EventWaitHandle _secondLaunch;
        private SynchronizationContext _ui;
        private MenuActions _menuActions;
        private readonly TrayMenu _menuBuilder = new();

        private App(CliArgs cli)
        {
            _cli = cli;
        }

        public static App Boot(CliArgs cli)
        {
            var app = new App(cli);
            app.Wire();
            return app;
        }

        private void Wire()
        {
            _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

            // Resolve config locations BEFORE loading anything: the settings file
            // (`--settings-file`, else next to the exe) plus the profile folders
            // (exe dir, settings `profile_dirs`, `--profile-dir`). Composition
            // re-resolves once real settings load, since their profile_dirs count.
            ConfigSources initial = ConfigSources.Resolve(_exeDir, _cli.SettingsFile, _cli.ProfileDirs, null);

            _log = new Log();
            _compose = new Composition(initial, _log, buildRev: Program.BuildRevision);
            _compose.Changed += (_, __) => RefreshTray();
            _compose.CueRequested += (_, spec) => Beeps.PlayCue(spec);

            _watcher = new Watcher(initial.WatchRoots, initial.SettingsFileName);
            _watcher.ReloadRequested += OnReloadRequested;
            _watcher.Start();

            _hooks = new LowLevelHooks();
            _hooks.Input += OnInputEvent;
            _hooks.ModifierState += OnModifierState;
            if (!_hooks.Install())
                _log.Warn("low-level keyboard hook failed to install — key detection disabled.");
            if (!_hooks.MouseInstalled)
                _log.Warn("low-level mouse hook failed to install — mouse chords will not be detected.");

            _tray = new TrayHost();
            _tray.Menu = BuildMenu();
            _tray.Show("SlitherIn " + Program.BuildRevision);

            _tick = new System.Windows.Forms.Timer { Interval = TickMs };
            _tick.Tick += (_, __) => OnTick();
            _tick.Start();

            // The named signal a second launch sets, so this instance can balloon it.
            _secondLaunch = Startup.CreateSecondLaunchSignal();

            // Initial load — raises Changed, which rebuilds the menu + alert state.
            _compose.Boot();
            // Boot may have re-resolved the sources (real settings: profile_dirs):
            // re-point the watcher so new folders are monitored from now on.
            // SetRoots is idempotent and cheap.
            _watcher.SetRoots(_compose.Sources.WatchRoots);
            _log.Info($"SlitherIn {Program.BuildRevision} started (settings {_compose.Sources.SettingsPath}, log {_log.Level})");
        }

        // ==== Input routing =========================================================

        private void OnTick()
        {
            if (_secondLaunch != null && _secondLaunch.WaitOne(0))
            {
                _log.Warn("a second slitherin.exe launch was ignored (one instance already runs).");
                if (_tray.NotificationsEnabled)
                    _tray.Balloon("SlitherIn", "SlitherIn is already running.", ToolTipIcon.Warning);
            }
            _compose.Tick();
        }

        private void OnInputEvent(object sender, InputEvent e)
        {
            if (_log.Level == LogLevel.Debug) _log.KeyEvent(e.Chord, e.Foreground);
            _compose.HandleInput(e);
        }

        private void OnModifierState(object sender, ModifierFlags mods)
            => _compose.HandleModifierState(mods);

        // ==== File reload ===========================================================

        private void OnReloadRequested(object sender, ReloadRequestedEventArgs e)
        {
            // Watcher events arrive on a threadpool thread; reload must run on the
            // UI thread (engine swaps, menu rebuilds).
            if (_ui != null && SynchronizationContext.Current != _ui)
            {
                _ui.Post(_ => SafeReload(e), null);
                return;
            }
            SafeReload(e);
        }

        private void SafeReload(ReloadRequestedEventArgs e)
        {
            try
            {
                _compose.Reload(e.FilePath, e.IsSettings);
                // The reload may have re-resolved sources (settings profile_dirs
                // changed) — keep the watcher monitoring every current source dir.
                _watcher.SetRoots(_compose.Sources.WatchRoots);
            }
            catch (Exception ex)
            {
                _log.Error("reload failed: " + ex);
            }
        }

        // ==== Tray ==================================================================

        private void RefreshTray()
        {
            _tray.Menu = BuildMenu();
            bool alert = _compose.Alerts.Count > 0;
            _tray.SetAlert(alert, alert ? _compose.Alerts[0] : null);
            _tray.NotificationsEnabled = _compose.Settings.Notifications?.Enabled ?? false;
        }

        private ContextMenuStrip BuildMenu()
            => _menuBuilder.Build(_compose.Catalog, _compose.Settings, _compose.Alerts, Actions(), _compose.Sources.SettingsFileName);

        private MenuActions Actions()
            => _menuActions ??= new MenuActions
            {
                SwitchProfile = file =>
                {
                    SuppressThenSave(() => _compose.Select(file));
                },
                SetMode = mode =>
                {
                    SuppressThenSave(() => _compose.Settings.ProfileMode = mode);
                },
                ToggleWindowCheck = () =>
                {
                    bool value = !_compose.Settings.WindowCheck;
                    SuppressThenSave(() => _compose.Settings.WindowCheck = value);
                    return _compose.Settings.WindowCheck;
                },
                ToggleNotifications = () =>
                {
                    bool value = !(_compose.Settings.Notifications?.Enabled ?? false);
                    SuppressThenSave(() => _compose.Settings.Notifications.Enabled = value);
                    return _compose.Settings.Notifications.Enabled;
                },
                ToggleRunAtStartup = () =>
                {
                    bool value = !Startup.RunAtStartup;
                    Startup.RunAtStartup = value;
                    _compose.Refresh(); // menu re-reads the carried setting
                    return value;
                },
                OpenActiveProfileJson = () => OpenJson(ActiveProfilePath()),
                OpenSettingsJson = () => OpenJson(_compose.Sources.SettingsPath),
                OpenReleasesPage = () => OpenUrl("https://github.com/bengalih/SlitherIn/releases"),
                Exit = () => Exit(),
            };

        /// <summary>Mutate settings, persist to disk, suppress our own watcher event,
        /// then re-apply WITHOUT stopping runs (window_check flips must not kill a
        /// running macro).</summary>
        private void SuppressThenSave(Action mutate)
        {
            string settingsPath = _compose.Sources.SettingsPath;
            _watcher?.SuppressFor(settingsPath, SelfSuppressMs);
            try
            {
                mutate();
                Loader.SaveSettings(settingsPath, _compose.Settings);
            }
            finally
            {
                _compose.Refresh();
            }
        }

        private string ActiveProfilePath()
        {
            string file = _compose.ActiveProfileFile;
            return string.IsNullOrWhiteSpace(file) ? null : _compose.Sources.ResolveProfilePath(file);
        }

        private void OpenJson(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                _log.Warn("cannot open json: file not found (" + path + ")");
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _log.Error("cannot open json: " + ex.Message);
            }
        }

        private void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _log.Error("cannot open url: " + ex.Message);
            }
        }

        private void Exit()
        {
            _tick?.Stop();
            Application.Exit();
        }

        public void Dispose()
        {
            _tick?.Dispose();
            _tick = null;
            _secondLaunch?.Dispose();
            _secondLaunch = null;
            if (_hooks != null) _hooks.Input -= OnInputEvent;
            _hooks?.Dispose();
            _watcher?.Dispose();
            _compose?.Dispose();
            _tray?.Dispose();
            _log?.Info($"clean shutdown ({Program.BuildRevision})");
            _log?.Dispose();
        }
    }
}