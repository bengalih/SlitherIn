using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using SlitherIn.Core.Config;
using SlitherIn.Core.Diagnostics;
using SlitherIn.Core.Engine;
using SlitherIn.Core.Target;

namespace SlitherIn.Tests
{
    /// <summary>All-profiles catalog: AUTO resolution + parked-runtime survival
    /// across switches + startup validation of every profile. Stage 5.</summary>
    [TestFixture]
    public class ProfileCatalogTests
    {
        private const string ProfileA = "slitherin.a.json";
        private const string ProfileB = "slitherin.b.json";
        private const string ProfileNone = "slitherin.manual-only.json";

        private string _dir;
        private ProfileCatalog _catalog;
        private FakeClock _clock;
        private RecordingEngine _engine;
        private Dispatcher _dispatcher;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "slitherin-catalog-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _catalog = new ProfileCatalog();
            _clock = new FakeClock();
            _engine = new RecordingEngine();
            _dispatcher = new Dispatcher(_engine, _clock, new Log(), defaultDelayMs: 1000);
        }

        [TearDown]
        public void TearDown()
        {
            _dispatcher.Dispose();
            _catalog.Dispose();
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }

        private string Write(string name, string contents)
        {
            string path = Path.Combine(_dir, name);
            File.WriteAllText(path, contents);
            return path;
        }

        private ConfigSources Sources() => ConfigSources.Resolve(_dir, null, null, null);

        [Test]
        public void AutoResolve_MatchesOwningProfile_ByWindow()
        {
            Write(ProfileA, @"{
                ""schema_version"": 1,
                ""name"": ""CharacterA"",
                ""window"": { ""exe"": ""dndclient64"", ""title"": ""*-Sarlona"" },
                ""macros"": []
            }");
            Write(ProfileB, @"{
                ""schema_version"": 1,
                ""name"": ""CharacterB"",
                ""window"": { ""exe"": ""ple"", ""title"": ""Main"" },
                ""macros"": []
            }");
            Write(ProfileNone, @"{
                ""schema_version"": 1,
                ""name"": ""ManualOnly"",
                ""macros"": []
            }");

            var outcome = _catalog.LoadAll(Sources());
            Assert.That(outcome.Succeeded, Is.True);
            Assert.That(outcome.Errors, Is.Empty);
            Assert.That(_catalog.ProfileFiles, Is.EquivalentTo(new[] { ProfileA, ProfileB, ProfileNone }));

            string owner = _catalog.ResolveOwner(new ForegroundInfo(IntPtr.Zero, "dndclient64", "Bob-Sarlona"));
            Assert.That(owner, Is.EqualTo(ProfileA), "auto-resolve picks the title owner");

            // The exe alone is not enough — the title pattern is part of the match.
            Assert.That(_catalog.ResolveOwner(new ForegroundInfo(IntPtr.Zero, "dndclient64", "Some-Other-Window")), Is.Null);

            Assert.That(_catalog.ResolveOwner(new ForegroundInfo(IntPtr.Zero, "ple", "Main")), Is.EqualTo(ProfileB));
            Assert.That(_catalog.ResolveOwner(new ForegroundInfo(IntPtr.Zero, "unknown.exe", "Main")), Is.Null);

            // A profile without a window target (ProfileNone) can never be
            // auto-selected: it is absent from _targets, so the owner is whoever
            // actually matched — here ProfileB, never the manual-only file.
            Assert.That(_catalog.ResolveOwner(new ForegroundInfo(IntPtr.Zero, "ple", "ManualOnly")), Is.Null);
        }

        [Test]
        public void ParkedRuntime_SurvivesSwitchAwayAndBack()
        {
            Write(ProfileA, @"{
                ""schema_version"": 1,
                ""name"": ""A"",
                ""window"": { ""exe"": ""gamea"" },
                ""macros"": [
                    { ""name"": ""cycle"", ""trigger"": ""F9"",
                      ""loop"": { ""count"": -1 },
                      ""schedule"": { ""mode"": ""cooldown"", ""fire_delay_ms"": 100 },
                      ""steps"": [ { ""keys"": ""7"", ""cooldown_ms"": 500 } ] }
                ]
            }");
            Write(ProfileB, @"{
                ""schema_version"": 1,
                ""name"": ""B"",
                ""window"": { ""exe"": ""gameb"" },
                ""macros"": [
                    { ""name"": ""cycle"", ""trigger"": ""F10"",
                      ""loop"": { ""count"": -1 },
                      ""schedule"": { ""mode"": ""cooldown"", ""fire_delay_ms"": 100 },
                      ""steps"": [ { ""keys"": ""8"", ""cooldown_ms"": 500 } ] }
                ]
            }");

            var outcome = _catalog.LoadAll(Sources());
            Assert.That(outcome.Succeeded, Is.True);
            Assert.That(outcome.Config, Has.Count.EqualTo(2), "both profiles load and validate");
            Assert.That(_catalog.ProfileFiles, Is.EquivalentTo(new[] { ProfileA, ProfileB }));

            _catalog.Select(ProfileA);
            Workflow wf = AssertOne(_catalog.ActiveWorkflows);
            _dispatcher.Attach(wf);

            // Start the run; the cooldown step fires on press, then cools down.
            _clock.Time = 0;
            wf.TriggerDown(KeyName.Parse("F9"), 0);
            _dispatcher.Tick(0);
            Assert.That(_engine.Log, Is.EqualTo(new[] { "7", "<up>" }));

            _clock.Time = 300;
            _dispatcher.Tick(300);
            Assert.That(_engine.Log, Is.EqualTo(new[] { "7", "<up>" }), "still cooling, nothing fires");

            // Switch away: detach from the dispatcher; the catalog parks the runtime.
            _dispatcher.Detach(wf);
            _catalog.Select(ProfileB);
            Assert.That(wf.State, Is.EqualTo(WorkflowState.Parked), "outgoing runtime is parked, not destroyed");

            // 1700 ms of unattended wall-clock pass — the 500 ms cooldown elapses
            // entirely while A is away (timers never pause).
            _clock.Time = 2000;

            // Switch back: the catalog resumes A's runtime at its pre-park state.
            _catalog.Select(ProfileA);
            Assert.That(wf.State, Is.EqualTo(WorkflowState.Running), "resumed where it left off");
            _dispatcher.Attach(wf);

            // First tick back: the step became ready at +500 ms while parked, so it
            // fires immediately — the catch-up rule.
            _dispatcher.Tick(2000);
            Assert.That(_engine.Log, Is.EqualTo(new[] { "7", "<up>", "7", "<up>" }));
        }

        private static Workflow AssertOne(IReadOnlyList<Workflow> list)
        {
            Assert.That(list.Count, Is.EqualTo(1));
            return list[0];
        }

        // ==== C-2: normalization failures ==========================================

        [Test]
        public void NormalizeFailedProfile_IsAbsentFromCatalog_AndSilentlySkips()
        {
            Write(ProfileA, @"{
                ""schema_version"": 1,
                ""name"": ""A"",
                ""window"": { ""exe"": ""gamea"" },
                ""macros"": [ { ""name"": ""m"", ""trigger"": ""F1"", ""steps"": [ { ""keys"": ""7"" } ] } ]
            }");
            Write(ProfileB, @"{
                ""schema_version"": 1,
                ""name"": ""B"",
                ""window"": { ""exe"": ""gameb"" },
                ""input_engine"": ""nasal"",
                ""macros"": [ { ""name"": ""m"", ""trigger"": ""F2"", ""steps"": [ { ""keys"": ""8"" } ] } ]
            }");

            var outcome = _catalog.LoadAll(Sources());

            Assert.That(outcome.Succeeded, Is.True, "a build failure is a per-file error, not a load blocker");
            Assert.That(outcome.Errors.Exists(e => e.File == ProfileB && e.Message.Contains("input_engine")), Is.True);
            Assert.That(_catalog.ProfileFiles, Is.EquivalentTo(new[] { ProfileA }),
                "the normalize-failed profile is NOT registered in the catalog");
            Assert.That(_catalog.ResolveOwner(new ForegroundInfo(IntPtr.Zero, "gameb", null)), Is.Null,
                "a broken profile can never auto-resolve");
            _catalog.Select(ProfileA);
            Assert.That(_catalog.ActiveWorkflows.Count, Is.EqualTo(1), "the good profile still runs");
            Assert.That(_catalog.ActiveWorkflows[0].State, Is.EqualTo(WorkflowState.Armed));
        }
    }
}