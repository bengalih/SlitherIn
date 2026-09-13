using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using SlitherIn.Core.Config;
using SlitherIn.Core.Diagnostics;
using SlitherIn.Core.Engine;
using SlitherIn.Core.Target;
using SlitherIn.Hook;
using SlitherIn.Input;

namespace SlitherIn.Tests
{
    /// <summary>
    /// Composition root (Stage 7): boot-with-broken-profile, reload abort+disarm,
    /// keep-old runtime on a broken active file, VIIPER hard-fail blocking, settings
    /// toggle persistence, and AUTO profile switching driven by a fake foreground +
    /// fake clock. Uses the real catalog/dispatcher/engines, RecordingEngine for
    /// injection, and an UnavailableEngine for the hard-fail path.
    /// </summary>
    [TestFixture]
    public class CompositionTests
    {
        private const string SettingsName = "slitherin.settings.json";
        private const string ActiveProfile = "slitherin.json";

        private string _dir;
        private FakeClock _clock;
        private RecordingEngine _engine;
        private UnavailableEngine _viiper;
        private ForegroundInfo _foreground;
        private List<string> _cues;
        private Composition _compose;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "slitherin-composition-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _clock = new FakeClock();
            _engine = new RecordingEngine();
            _viiper = new UnavailableEngine();
            _foreground = ForegroundInfo.Empty;
            _cues = new List<string>();
        }

        [TearDown]
        public void TearDown()
        {
            _compose?.Dispose();
            _compose = null;
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }

        private Composition NewComposition(bool auto = false)
        {
            var compose = new Composition(
                ConfigSources.Resolve(_dir, null, null, null),
                new Log(),
                engineFactory: name => name == "viiper" ? _viiper : _engine,
                foreground: () => _foreground,
                clock: _clock);
            if (auto)
            {
                compose.Settings.ProfileMode = ProfileCatalog.Auto;
                Loader.SaveSettings(compose.Sources.SettingsPath, compose.Settings);
            }
            compose.CueRequested += (_, spec) => _cues.Add(CueName(spec));
            compose.Boot();
            _engine.Log.Clear(); // steady state: Boot's abort-and-release isn't an injection
            return compose;
        }

        private string Write(string name, string contents)
        {
            string path = Path.Combine(_dir, name);
            File.WriteAllText(path, contents);
            return path;
        }

        private void WriteSettings(bool windowCheck = true, string mode = "manual")
        {
            Write(SettingsName, @"{
                ""schema_version"": 1,
                ""window_check"": " + (windowCheck ? "true" : "false") + @",
                ""abort"": ""Ctrl+Alt+F12"",
                ""kill"": ""Ctrl+Alt+Esc"",
                ""active_profile"": """ + ActiveProfile + @""",
                ""profile_mode"": """ + mode + @"""
            }");
        }

        private void WriteProfile(string macroBody)
        {
            Write(ActiveProfile, @"{
                ""schema_version"": 1,
                ""name"": ""Main"",
                ""window"": { ""exe"": ""game.exe"" },
                ""macros"": [ " + macroBody + @" ]
            }");
        }

        private static string ToggleMacro(string trigger, string toggle, string name = "Master", string loop = "-1")
            => @"{ ""name"": """ + name + @""", ""trigger"": """ + trigger + @""",
                    ""toggle"": """ + toggle + @""",
                    ""loop"": { ""count"": " + loop + @" },
                    ""steps"": [ { ""keys"": ""7"" } ] }";

        private static string PlainMacro(string trigger, string name = "Burst")
            => @"{ ""name"": """ + name + @""", ""trigger"": """ + trigger + @""",
                    ""loop"": { ""count"": 1 },
                    ""steps"": [ { ""keys"": ""7"" } ] }";

        // ==== Boot =================================================================

        [Test]
        public void BootWithBrokenProfile_LoadsValidProfiles_AndAlerts()
        {
            WriteSettings();
            WriteProfile(ToggleMacro("F8", "Pause", loop: "-1"));
            Write("slitherin.bad.json", @"{ ""schema_version"": 1, ""name"": "); // truncated → JSON error

            _compose = NewComposition();

            Assert.That(_compose.Alerts, Has.Count.GreaterThan(0), "the broken file must become a tray alert");
            Assert.That(_compose.Alerts.Any(a => a.Contains("slitherin.bad.json")), Is.True);
            Assert.That(_compose.Catalog.ActiveFile, Is.EqualTo(ActiveProfile), "valid active profile still loads");
            Assert.That(_compose.ActiveWorkflows, Has.Count.EqualTo(1));
        }

        // ==== Reload: abort + disarm ===============================================

        [Test]
        public void Reload_AbortsRunningMacro_AndDisarmsToggles()
        {
            WriteSettings();
            WriteProfile(ToggleMacro("F8", "Pause"));
            _compose = NewComposition();
            Workflow old = AssertOne(_compose.ActiveWorkflows);

            _compose.HandleInput(Down(KeyName.Parse("Pause")));
            var fgGame = new ForegroundInfo(IntPtr.Zero, "game.exe", null);
            _foreground = fgGame; // the live foreground (mid-run gate reads it per tick)
            _compose.HandleInput(Down(KeyName.Parse("F8"), fgGame));
            _clock.Time = 0;
            _compose.Tick();
            Assert.That(_engine.Log, Is.EqualTo(new[] { "7", "<up>" }), "armed + triggered → run fires on first tick");
            Assert.That(old.State, Is.EqualTo(WorkflowState.Running));

            // Edit the file (valid), then reload: previous run aborted, all released.
            Write(ActiveProfile, @"{
                ""schema_version"": 1,
                ""name"": ""Main"",
                ""macros"": [ " + ToggleMacro("Ctrl+9", "ScrollLock", name: "Changed") + @" ]
            }");
            string path = Path.Combine(_dir, ActiveProfile);
            _compose.Reload(path, isSettings: false);

            Assert.That(old.Cancel.IsRequested, Is.True, "reload aborts the previous run");
            Assert.That(_engine.Log, Does.Contain("release-all"), "reload releases every pressed key");
            Assert.That(ReferenceEquals(_compose.ActiveWorkflows.First(), old), Is.False, "fresh runtime replaces the old");
            Assert.That(_compose.ActiveWorkflows.Single().State, Is.EqualTo(WorkflowState.Idle),
                "a toggle-defined macro comes back disarmed after reload");
        }

        // ==== Reload: keep-old rule ================================================

        [Test]
        public void Reload_BrokenActiveFile_KeepsPreviousRuntimeRunning()
        {
            WriteSettings();
            WriteProfile(ToggleMacro("F8", "Pause"));
            _compose = NewComposition();
            Workflow old = AssertOne(_compose.ActiveWorkflows);

            _compose.HandleInput(Down(KeyName.Parse("Pause")));   // arm it
            Assert.That(old.State, Is.EqualTo(WorkflowState.Armed));

            // The active file becomes broken → reload must NOT drop its runtime.
            Write(ActiveProfile, @"{ ""schema_version"": 1, ""name"": ");
            _compose.Reload(Path.Combine(_dir, ActiveProfile), isSettings: false);

            Assert.That(_compose.Alerts.Any(a => a.Contains(ActiveProfile)), Is.True, "the reload error is surfaced");
            Assert.That(_compose.Catalog.ActiveFile, Is.EqualTo(ActiveProfile));
            Assert.That(ReferenceEquals(_compose.ActiveWorkflows.Single(), old), Is.True,
                "the previous workflow instance is kept alive");
            Assert.That(old.Cancel.IsRequested, Is.True, "the kept run was signalled (aborted)");
            Assert.That(old.State, Is.EqualTo(WorkflowState.Idle),
                "the kept toggle macro is disarmed until the user re-arms it");

            // Recover: fixing the file and reloading swaps in a fresh runtime.
            WriteProfile(ToggleMacro("F8", "Pause"));
            _compose.Reload(null, isSettings: false);
            Assert.That(ReferenceEquals(_compose.ActiveWorkflows.Single(), old), Is.False, "fixed file → fresh runtime");
        }

        // ==== Reload: normalize-failure keep-old (C-2) =============================

        [Test]
        public void Reload_NormalizeFailedActiveFile_KeepsPreviousRuntimeRunning()
        {
            WriteSettings();
            WriteProfile(ToggleMacro("F8", "Pause"));
            _compose = NewComposition();
            Workflow old = AssertOne(_compose.ActiveWorkflows);

            _compose.HandleInput(Down(KeyName.Parse("Pause")));   // arm it
            Assert.That(old.State, Is.EqualTo(WorkflowState.Armed));

            // The active file now PARSES but cannot be BUILT (unknown input_engine)
            // — the normalization failure must not drop its runtime.
            Write(ActiveProfile, @"{
                ""schema_version"": 1,
                ""name"": ""Main"",
                ""input_engine"": ""nasal"",
                ""macros"": [ " + ToggleMacro("F8", "Pause") + @" ]
            }");
            _compose.Reload(Path.Combine(_dir, ActiveProfile), isSettings: false);

            Assert.That(_compose.Alerts.Any(a => a.Contains("input_engine") && a.Contains(ActiveProfile)), Is.True,
                "the normalization error is surfaced as an alert");
            Assert.That(_compose.Catalog.ActiveFile, Is.EqualTo(ActiveProfile));
            Assert.That(ReferenceEquals(_compose.ActiveWorkflows.Single(), old), Is.True,
                "the previous workflow instance is kept alive on a normalize failure");
            Assert.That(old.Cancel.IsRequested, Is.True, "the kept run was signalled (aborted)");
            Assert.That(old.State, Is.EqualTo(WorkflowState.Idle),
                "the kept toggle macro is disarmed until the user re-arms it");

            // Recover: fixing the file and reloading swaps in a fresh runtime.
            WriteProfile(ToggleMacro("F8", "Pause"));
            _compose.Reload(null, isSettings: false);
            Assert.That(ReferenceEquals(_compose.ActiveWorkflows.Single(), old), Is.False, "fixed file → fresh runtime");
        }

        // ==== VIIPER hard-fail =====================================================

        [Test]
        public void ViiperUnavailable_BlocksActiveProfile_NoInjection()
        {
            WriteSettings();
            Write(ActiveProfile, @"{
                ""schema_version"": 1,
                ""name"": ""ViiperMain"",
                ""input_engine"": ""viiper"",
                ""macros"": [ " + PlainMacro("F9") + @" ]
            }");

            _compose = NewComposition();

            Assert.That(_compose.ActiveProfileBlocked, Is.True, "viiper unavailable → hard-fail");
            Assert.That(_compose.Alerts.Any(a => a.IndexOf("VIIPER", StringComparison.OrdinalIgnoreCase) >= 0), Is.True,
                "the blocker is an alert");

            var wf = AssertOne(_compose.ActiveWorkflows);
            _compose.HandleInput(Down(KeyName.Parse("F9")));
            _clock.Time = 0;
            _compose.Tick();

            Assert.That(wf.State, Is.EqualTo(WorkflowState.Armed), "the blocked macro never runs");
            Assert.That(_engine.Log, Is.Empty, "nothing reaches the engine while blocked");
        }

        // ==== Settings toggle persistence + window gate ============================

        [Test]
        public void WindowCheckToggle_PersistsToSettingsFile_AndGatesRuns()
        {
            WriteSettings(windowCheck: true);
            WriteProfile(PlainMacro("F9"));
            _compose = NewComposition();

            var fgGame = new ForegroundInfo(IntPtr.Zero, "game.exe", null);
            var fgOther = new ForegroundInfo(IntPtr.Zero, "other.exe", null);

            // Window-check ON: only the profile's window may run.
            _foreground = fgGame; // live foreground matches → mid-run gate open
            _compose.HandleInput(Down(KeyName.Parse("F9"), fgGame));
            _clock.Time = 0;
            _compose.Tick();
            Assert.That(_engine.Log, Is.EqualTo(new[] { "7", "<up>" }), "matching window runs");

            _compose.HandleInput(Down(KeyName.Parse("F9"), fgOther));
            _compose.Tick();
            Assert.That(_engine.Log, Is.EqualTo(new[] { "7", "<up>" }), "non-matching window is gated off");

            // Toggle window_check off the way App does: mutate → persist → reload.
            _compose.Settings.WindowCheck = false;
            Loader.SaveSettings(_compose.Sources.SettingsPath, _compose.Settings);
            string settingsText = File.ReadAllText(Path.Combine(_dir, SettingsName));
            Assert.That(settingsText, Does.Contain("\"window_check\":false"), "the toggle persists to the settings JSON");
            _compose.Reload(null, isSettings: false);

            // Window-check OFF: any window runs.
            _compose.HandleInput(Down(KeyName.Parse("F9"), fgOther));
            _compose.Tick();
            Assert.That(_engine.Log, Is.EqualTo(new[] { "7", "<up>", "release-all", "7", "<up>" }),
                "gate disabled → anywhere runs (reload's release-all sits between the two passes)");
        }

        // ==== C-4: mid-run window gate ============================================

        [Test]
        public void MidRunGate_HoldsDueStepWhileAway_FiresCatchUpOnReturn_AndReArmsFromActualFire()
        {
            WriteSettings(); // window_check ON (written by default)
            WriteProfile(@"{ ""name"": ""Cycle"", ""trigger"": ""F9"", ""loop"": { ""count"": 2 },
                    ""steps"": [ { ""keys"": ""7"", ""wait_ms"": 300 }, { ""keys"": ""8"", ""wait_ms"": 400 } ] }");
            _compose = NewComposition();

            var fgGame = new ForegroundInfo(IntPtr.Zero, "game.exe", null);
            var fgOther = new ForegroundInfo(IntPtr.Zero, "other.exe", null);

            // Run starts in-game; step 0 ("7") fires on press.
            _foreground = fgGame;
            _compose.HandleInput(Down(KeyName.Parse("F9"), fgGame));
            _clock.Time = 0;
            _compose.Tick();
            Assert.That(_engine.Log, Is.EqualTo(new[] { "7", "<up>" }), "step 0 fires on press");

            // Step 1 ("8") comes due at ~300 ms — but focus moved away: held, and the
            // run is NOT paused (wall-clock keeps counting, the due time passes).
            _foreground = fgOther;
            _clock.Time = 400;
            _compose.Tick();
            Assert.That(_engine.Log, Is.EqualTo(new[] { "7", "<up>" }), "due step held while away");

            _clock.Time = 800;
            _compose.Tick();
            Assert.That(_engine.Log, Is.EqualTo(new[] { "7", "<up>" }), "still held after the due time passes");

            // Focus returns → gate reopens → the READY step fires immediately (catch-up).
            _foreground = fgGame;
            _clock.Time = 801;
            _compose.Tick();
            Assert.That(_engine.Log, Is.EqualTo(new[] { "7", "<up>", "8", "<up>" }), "held step fires as soon as the gate opens");

            // The next countdown re-arms from the ACTUAL fire (801 + 400 = 1201), not from
            // the missed due time (300 + 400 = 700) — so nothing fires early.
            _clock.Time = 1000;
            _compose.Tick();
            Assert.That(_engine.Log, Is.EqualTo(new[] { "7", "<up>", "8", "<up>" }), "no early pass-2: countdown anchored to the true fire time");

            // 1201 is the re-armed pass boundary; the tick that reaches it completes the
            // pass (no fire), and pass 2 begins on the next tick.
            _clock.Time = 1400;
            _compose.Tick();
            Assert.That(_engine.Log, Is.EqualTo(new[] { "7", "<up>", "8", "<up>" }), "pass boundary completes without firing");

            _clock.Time = 1500;
            _compose.Tick();
            Assert.That(_engine.Log, Is.EqualTo(new[] { "7", "<up>", "8", "<up>", "7", "<up>" }),
                "pass 2 begins after the re-armed boundary (1201) completes");
        }

        // ==== C-6: chord-release orphan hole ======================================

        private static string HeldChordMacro(string trigger, bool continueOnRelease)
            => @"{ ""name"": ""ChordHeld"", ""trigger"": """ + trigger + @""",
                    ""fire_after_ms"": 1000, ""loop"": { ""count"": -1, ""continue_on_release"": "
               + (continueOnRelease ? "true" : "false") + @" },
                    ""steps"": [ { ""keys"": ""7"" } ] }";

        private static void PressChordHeld(HeldChordMacroContext c, out Workflow wf)
        {
            wf = AssertOne(c.Compose.ActiveWorkflows);
            c.Compose.HandleModifierState(ModifierFlags.Ctrl);              // Ctrl goes down
            c.Compose.HandleInput(Down(KeyName.Parse("Ctrl+K"), c.Game));   // trigger presses
            Assert.That(wf.State, Is.EqualTo(WorkflowState.AwaitingHold));
        }

        private static void DriveIntoRun(HeldChordMacroContext c, Workflow wf)
        {
            c.Clock.Time = 1000; // ≥ fire_after_ms → the hold starts the run
            c.Compose.Tick();
            Assert.That(wf.State, Is.EqualTo(WorkflowState.Running));
        }

        [Test]
        public void HeldChordTrigger_ReleaseModifierFirst_CancelsTheHold()
        {
            WriteSettings();
            WriteProfile(HeldChordMacro("Ctrl+K", continueOnRelease: false));
            var c = StartHeldChord();                    // release BEFORE the threshold
            PressChordHeld(c, out Workflow wf);

            c.Compose.HandleModifierState(ModifierFlags.None); // Ctrl up, K still held
            Assert.That(wf.State, Is.EqualTo(WorkflowState.Armed), "a pre-threshold modifier release cancels the hold");
        }

        [Test]
        public void HeldChordTrigger_ReleaseModifierFirst_StopsTheRun()
        {
            WriteSettings();
            WriteProfile(HeldChordMacro("Ctrl+K", continueOnRelease: false));
            var c = StartHeldChord();
            PressChordHeld(c, out Workflow wf);
            DriveIntoRun(c, wf);
            bool finished = false;
            wf.RunFinished += _ => finished = true;

            c.Compose.HandleModifierState(ModifierFlags.None); // Ctrl up first, K still held
            Assert.That(finished, Is.True, "release-to-stop must fire even when the MODIFIER is the member released");
            Assert.That(wf.State, Is.EqualTo(WorkflowState.Armed),
                "any chord member leaving the down-set ends a continue_on_release:false run");
        }

        [Test]
        public void HeldChordTrigger_ReleaseModifierFirst_WithContinueTrue_KeepsRunning()
        {
            WriteSettings();
            WriteProfile(HeldChordMacro("Ctrl+K", continueOnRelease: true));
            var c = StartHeldChord();
            PressChordHeld(c, out Workflow wf);
            DriveIntoRun(c, wf);
            bool finished = false;
            wf.RunFinished += _ => finished = true;

            c.Compose.HandleModifierState(ModifierFlags.None); // Ctrl up first, K still held
            Assert.That(finished, Is.False, "continue_on_release:true survives the modifier lift");
            Assert.That(wf.State, Is.EqualTo(WorkflowState.Running));
        }

        [Test]
        public void HeldChordTrigger_ReleaseUnrelatedModifier_DoesNotStopTheRun()
        {
            WriteSettings();
            WriteProfile(HeldChordMacro("Ctrl+K", continueOnRelease: false));
            var c = StartHeldChord();
            PressChordHeld(c, out Workflow wf);
            DriveIntoRun(c, wf);

            c.Compose.HandleModifierState(ModifierFlags.Ctrl | ModifierFlags.Alt); // Alt pressed
            c.Compose.HandleModifierState(ModifierFlags.Ctrl);                     // …and released
            Assert.That(wf.State, Is.EqualTo(WorkflowState.Running),
                "a modifier not in the trigger's chord must not release the run");
        }

        private HeldChordMacroContext StartHeldChord() => new(NewComposition(), _clock, Game);

        private ForegroundInfo Game => new(IntPtr.Zero, "game.exe", null);

        private readonly struct HeldChordMacroContext
        {
            public readonly Composition Compose;
            public readonly FakeClock Clock;
            public readonly ForegroundInfo Game;

            public HeldChordMacroContext(Composition compose, FakeClock clock, ForegroundInfo game)
            {
                Compose = compose;
                Clock = clock;
                Game = game;
            }
        }

        // ==== AUTO mode ============================================================

        [Test]
        public void AutoMode_SwitchesProfile_WhenForegroundOwnershipChanges()
        {
            WriteSettings(mode: "auto");
            Write("slitherin.a.json", @"{ ""schema_version"": 1, ""name"": ""A"",
                ""window"": { ""exe"": ""gamea.exe"" },
                ""macros"": [ " + PlainMacro("F1", "A") + @" ] }");
            Write("slitherin.b.json", @"{ ""schema_version"": 1, ""name"": ""B"",
                ""window"": { ""exe"": ""gameb.exe"" },
                ""macros"": [ " + PlainMacro("F2", "B") + @" ] }");

            _foreground = new ForegroundInfo(IntPtr.Zero, "gamea.exe", null);
            _compose = NewComposition(auto: true);
            Assert.That(_compose.Catalog.ActiveFile, Is.EqualTo("slitherin.a.json"), "boot in AUTO resolves the foreground owner");

            // Focus moves to the second game's window; the poll picks it up on the next tick.
            _foreground = new ForegroundInfo(IntPtr.Zero, "gameb.exe", null);
            _clock.Time = 300; // > AutoPollMs
            _compose.Tick();

            Assert.That(_compose.Catalog.ActiveFile, Is.EqualTo("slitherin.b.json"), "AUTO switches on foreground change");
            Assert.That(_compose.Settings.ActiveProfile, Is.EqualTo("slitherin.b.json"), "the switch is remembered");
        }

        // ==== C-7: settings profile_dirs ==============================================

        [Test]
        public void Boot_SettingsProfileDirs_LoadsAuxFolderProfiles()
        {
            string aux = Path.Combine(Path.GetTempPath(), "slitherin-aux-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(aux);
                File.WriteAllText(Path.Combine(aux, "slitherin.aux.json"), @"{
                    ""schema_version"": 1,
                    ""name"": ""Aux"",
                    ""window"": { ""exe"": ""game.exe"" },
                    ""macros"": [ { ""name"": ""Burst"", ""trigger"": ""F9"", ""steps"": [ { ""keys"": ""7"" } ] } ]
                }");
                Write(SettingsName, @"{
                    ""schema_version"": 1,
                    ""active_profile"": ""slitherin.aux.json"",
                    ""profile_dirs"": [ """ + aux.Replace("\\", "/") + @""" ]
                }");

                _compose = NewComposition();

                Assert.That(_compose.Catalog.HasProfile("slitherin.aux.json"), Is.True,
                    "a profile from a settings profile_dirs folder is in the catalog");
                Assert.That(_compose.Catalog.ActiveFile, Is.EqualTo("slitherin.aux.json"),
                    "the cross-folder profile activates");
                Assert.That(_compose.ActiveWorkflows, Has.Count.EqualTo(1));
            }
            finally
            {
                try { if (Directory.Exists(aux)) Directory.Delete(aux, recursive: true); } catch { }
            }
        }

        // ==== Helpers ==============================================================

        private static Workflow AssertOne(IReadOnlyList<Workflow> list)
        {
            Assert.That(list.Count, Is.EqualTo(1));
            return list[0];
        }

        private static InputEvent Down(Chord chord, ForegroundInfo fg)
            => new(InputEventKind.KeyDown, chord, fg);

        private static InputEvent Down(Chord chord)
            => new(InputEventKind.KeyDown, chord, ForegroundInfo.Empty);

        private static InputEvent Up(Chord chord)
            => new(InputEventKind.KeyUp, chord, ForegroundInfo.Empty);

        private static string CueName(SoundSpec spec)
            => spec?.File ?? $"{spec.Frequency}Hz/{spec.DurationMs}ms";
    }

    /// <summary>An engine that is registered but never available — the VIIPER
    /// hard-fail double.</summary>
    internal sealed class UnavailableEngine : IKeyEngine
    {
        public bool Available => false;
        public string Name => "viiper";
        public void Initialize() { }
        public void SetState(IReadOnlyList<Chord> pressed) { }
        public void ReleaseAll() { }
        public void Dispose() { }
    }
}