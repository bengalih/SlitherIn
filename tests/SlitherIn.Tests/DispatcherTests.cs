using System.Collections.Generic;
using NUnit.Framework;
using SlitherIn.Core.Config;
using SlitherIn.Core.Diagnostics;
using SlitherIn.Core.Engine;

namespace SlitherIn.Tests
{
    /// <summary>Cooperative dispatcher + real Workflow + real pacing, driven by the
    /// fake clock and a recording engine: two macros run simultaneously through one
    /// engine; the wall-clock rule under parking; detach; abort; hold_for_ms.
    /// Stage 4.</summary>
    [TestFixture]
    public class DispatcherTests
    {
        private FakeClock _clock;
        private RecordingEngine _engine;
        private Dispatcher _dispatcher;

        [SetUp]
        public void SetUp()
        {
            _clock = new FakeClock();
            _engine = new RecordingEngine();
            _dispatcher = new Dispatcher(_engine, _clock, new Log(), defaultDelayMs: 1000);
        }

        [TearDown]
        public void TearDown() => _dispatcher.Dispose();

        [Test]
        public void TwoMacros_RunSimultaneouslyThroughOneEngine()
        {
            var a = StartWorkflow(MacroFrom($"Ctrl+1", -1, Step("7"), Step("9")));
            var b = StartWorkflow(MacroFrom($"Ctrl+2", -1, Step("8"), Step("0")));
            _dispatcher.Attach(a);
            _dispatcher.Attach(b);

            _clock.Time = 0;
            a.TriggerDown(KeyName.Parse("Ctrl+1"), _clock.Time);
            b.TriggerDown(KeyName.Parse("Ctrl+2"), _clock.Time);
            _dispatcher.Tick(0);

            // Pass 1, in attach order: each macro's first step fires on press.
            Assert.That(_engine.Log, Is.EqualTo(new[] { "7", "<up>", "8", "<up>" }));

            // One default-delay later, both second steps go out.
            _clock.Time = 1000;
            _dispatcher.Tick(1000);
            Assert.That(_engine.Log, Is.EqualTo(
                new[] { "7", "<up>", "8", "<up>", "9", "<up>", "0", "<up>" }));

            // Runs are infinite and both still alive after their passes.
            Assert.That(a.State, Is.EqualTo(WorkflowState.Running));
            Assert.That(b.State, Is.EqualTo(WorkflowState.Running));
        }

        [Test]
        public void ParkedWorkflow_NotTicked_CooldownKeepsCountingWallClock()
        {
            var wf = StartWorkflow(MacroFrom("F9", -1, Step("7", cooldownMs: 500)));
            wf.Definition.Schedule = new Schedule { Mode = "cooldown", FireDelayMs = 100 };
            _dispatcher.Attach(wf);

            _clock.Time = 0;
            wf.TriggerDown(KeyName.Parse("F9"), 0);
            _dispatcher.Tick(0);
            Assert.That(_engine.Log, Is.EqualTo(new[] { "7", "<up>" }));

            // Still cooling: nothing fires, the scan just completes.
            _clock.Time = 100;
            _dispatcher.Tick(100);
            Assert.That(_engine.Log, Is.EqualTo(new[] { "7", "<up>" }));

            // Park, and let 1900 more wall-clock ms pass WITHOUT any ticks.
            wf.Park();
            _clock.Time = 2000;
            Assert.That(wf.State, Is.EqualTo(WorkflowState.Parked));

            // Unpark: the step became ready at +500 ms; it fires on the first tick
            // back — the catch-up rule. Timers were never paused.
            wf.Unpark();
            Assert.That(wf.State, Is.EqualTo(WorkflowState.Running));
            _dispatcher.Tick(2000);
            Assert.That(_engine.Log, Is.EqualTo(new[] { "7", "<up>", "7", "<up>" }));
        }

        [Test]
        public void Detach_StopsAdvancing()
        {
            var wf = StartWorkflow(MacroFrom("F9", 1, Step("7")));
            _dispatcher.Attach(wf);

            _clock.Time = 0;
            wf.TriggerDown(KeyName.Parse("F9"), 0);
            _dispatcher.Tick(0);
            Assert.That(_engine.Log, Is.EqualTo(new[] { "7", "<up>" }));

            _dispatcher.Detach(wf);
            _clock.Time = 5000;
            _dispatcher.Tick(5000);
            Assert.That(_engine.Log, Is.EqualTo(new[] { "7", "<up>" })); // untouched
        }

        [Test]
        public void AbortAll_ReleasesKeys_EndsRuns()
        {
            var wf = StartWorkflow(MacroFrom("F9", -1, Step("7")));
            _dispatcher.Attach(wf);

            _clock.Time = 0;
            wf.TriggerDown(KeyName.Parse("F9"), 0);
            _dispatcher.Tick(0);

            _dispatcher.AbortAll();
            Assert.That(wf.Cancel.IsRequested, Is.True);
            Assert.That(wf.State, Is.EqualTo(WorkflowState.Armed));
            Assert.That(_engine.Log, Does.Contain("release-all"));
        }

        [Test]
        public void HoldForMs_StaysDownUntilDue_ThenReleases()
        {
            var wf = StartWorkflow(MacroFrom("F9", 1, Step("7", holdMs: 200)));
            _dispatcher.Attach(wf);

            _clock.Time = 0;
            wf.TriggerDown(KeyName.Parse("F9"), 0);
            _dispatcher.Tick(0);
            Assert.That(_engine.Log, Is.EqualTo(new[] { "7" }));   // down, held

            // Mid-hold timeout: the run completes (count 1) but a normally-finished
            // run must NOT drop the key early — the hold has its own due time.
            _clock.Time = 100;
            _dispatcher.Tick(100);
            Assert.That(_engine.Log, Is.EqualTo(new[] { "7" }));

            // Due time reached → released by the loop.
            _clock.Time = 200;
            _dispatcher.Tick(200);
            Assert.That(_engine.Log, Is.EqualTo(new[] { "7", "<up>" }));
        }

        [Test]
        public void DetachWhileHolding_ReleasesKeyImmediately()
        {
            var wf = StartWorkflow(MacroFrom("F9", -1, Step("7", holdMs: 1000)));
            _dispatcher.Attach(wf);

            _clock.Time = 0;
            wf.TriggerDown(KeyName.Parse("F9"), 0);
            _dispatcher.Tick(0);
            Assert.That(_engine.Log, Is.EqualTo(new[] { "7" }));

            _dispatcher.Detach(wf);
            Assert.That(_engine.Log, Is.EqualTo(new[] { "7", "<up>" }));
        }

        // ==== Helpers =============================================================

        private Workflow StartWorkflow(NormalizedMacro macro)
            => new Workflow(macro);

        private static NormalizedMacro MacroFrom(string trigger, int count = -1,
            params NormalizedStep[] steps)
            => new()
            {
                Name = "m",
                Trigger = KeyName.Parse(trigger),
                Loop = new LoopSpec { Count = count },
                Steps = new List<NormalizedStep>(steps),
            };

        private static NormalizedStep Step(string key, int? cooldownMs = null, int? holdMs = null)
            => new()
            {
                Chords = new[] { KeyName.Parse(key) },
                CooldownMs = cooldownMs,
                HoldForMs = holdMs,
            };
    }
}