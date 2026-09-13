using System.Collections.Generic;
using NUnit.Framework;
using SlitherIn.Core.Config;
using SlitherIn.Core.Engine;

namespace SlitherIn.Tests
{
    /// <summary>The ONE start→run→end state machine: tap/hold triggers,
    /// continue_on_release, loop count, abort tiers, parking. Pacing policies are
    /// Stage 4 — here a scripted fake stands in for the timing seam. Stage 3.</summary>
    [TestFixture]
    public class WorkflowTests
    {
        [Test]
        public void NoToggle_TapTrigger_StartsRunFiresStepAndCompletes()
        {
            var wf = new Workflow(Macro(toggle: null, fireAfterMs: null, count: 1));
            wf.Pacing = new ScriptedPacing(PacingDecision.Fire(0));

            var fired = new List<NormalizedStep>();
            bool finished = false;
            wf.StepReady += (w, s) => fired.Add(s);
            wf.RunFinished += w => finished = true;

            wf.TriggerDown(KeyName.Parse("F9"), 0);
            Assert.That(wf.State, Is.EqualTo(WorkflowState.Running));

            wf.Advance(0);
            Assert.That(fired, Has.Count.EqualTo(1));
            Assert.That(wf.State, Is.EqualTo(WorkflowState.Running));

            wf.Advance(0); // scripted fake returns Complete on the 2nd call
            Assert.That(finished, Is.True);
            Assert.That(wf.State, Is.EqualTo(WorkflowState.Armed)); // no toggle → stays armed
        }

        [Test]
        public void Toggle_ArmsAndDisarms_TriggerIgnoredWhileIdle()
        {
            var wf = new Workflow(Macro(toggle: KeyName.Parse("Pause"), fireAfterMs: null, count: 1));
            int armed = 0, disarmed = 0;
            wf.ToggleArmed += w => armed++;
            wf.ToggleDisarmed += w => disarmed++;

            Assert.That(wf.State, Is.EqualTo(WorkflowState.Idle));
            wf.TriggerDown(KeyName.Parse("F9"), 0); // unarmed → ignored
            Assert.That(wf.State, Is.EqualTo(WorkflowState.Idle));

            wf.TogglePressed();
            Assert.That(wf.State, Is.EqualTo(WorkflowState.Armed));
            Assert.That(armed, Is.EqualTo(1));

            wf.TogglePressed();
            Assert.That(wf.State, Is.EqualTo(WorkflowState.Idle));
            Assert.That(disarmed, Is.EqualTo(1));
        }

        [Test]
        public void HeldTrigger_FiresOnlyAfterThreshold()
        {
            var wf = new Workflow(Macro(toggle: null, fireAfterMs: 3000, count: 1));
            wf.TriggerDown(KeyName.Parse("F9"), 0);
            Assert.That(wf.State, Is.EqualTo(WorkflowState.AwaitingHold));

            wf.Advance(1000);
            Assert.That(wf.State, Is.EqualTo(WorkflowState.AwaitingHold));

            wf.Advance(3000);
            Assert.That(wf.State, Is.EqualTo(WorkflowState.Running));
        }

        [Test]
        public void HeldTrigger_ReleasedBeforeThreshold_CancelsHold()
        {
            var wf = new Workflow(Macro(toggle: null, fireAfterMs: 3000, count: 1));
            wf.TriggerDown(KeyName.Parse("F9"), 0);
            wf.TriggerUp(KeyName.Parse("F9"), 500);
            Assert.That(wf.State, Is.EqualTo(WorkflowState.Armed));
        }

        [Test]
        public void ReleaseWhileRunning_HeldTrigger_StopsRun()
        {
            var wf = new Workflow(Macro(toggle: null, fireAfterMs: 1000, count: -1));
            bool finished = false;
            wf.RunFinished += w => finished = true;

            wf.TriggerDown(KeyName.Parse("F9"), 0);
            wf.Advance(1000);
            Assert.That(wf.State, Is.EqualTo(WorkflowState.Running));

            wf.TriggerUp(KeyName.Parse("F9"), 1500);
            Assert.That(finished, Is.True);
            Assert.That(wf.State, Is.EqualTo(WorkflowState.Armed));
        }

        [Test]
        public void ReleaseWhileRunning_TapTrigger_KeeepsRunning()
        {
            var wf = new Workflow(Macro(toggle: null, fireAfterMs: null, count: -1));
            bool finished = false;
            wf.RunFinished += w => finished = true;

            wf.TriggerDown(KeyName.Parse("F9"), 0);
            wf.Advance(0);
            wf.TriggerUp(KeyName.Parse("F9"), 10);
            Assert.That(finished, Is.False);
            Assert.That(wf.State, Is.EqualTo(WorkflowState.Running));
        }

        [Test]
        public void C5_TapTrigger_ExplicitContinueOnReleaseFalse_StillRunsFullCount()
        {
            // C-5: a tap trigger ALWAYS runs its full count — key-up is the end of
            // the tap, never a stop. The explicit false flag is ignored for a tap.
            var wf = new Workflow(Macro(toggle: null, fireAfterMs: null, count: 3, continueOnRelease: false));
            wf.Pacing = new ScriptedPacing();
            int finished = 0;
            wf.RunFinished += w => finished++;

            wf.TriggerDown(KeyName.Parse("F9"), 0);
            wf.TriggerUp(KeyName.Parse("F9"), 10);
            Assert.That(finished, Is.EqualTo(0), "release must NOT stop a tap-trigger run");
            Assert.That(wf.State, Is.EqualTo(WorkflowState.Running));

            for (int i = 0; i < 3; i++) wf.Advance(0);   // all three passes
            Assert.That(finished, Is.EqualTo(1), "the tap still completes its full count after release");
            Assert.That(wf.State, Is.EqualTo(WorkflowState.Armed));
        }

        [Test]
        public void C5_HeldTrigger_CountFive_HoldThrough_CompletesAllFive()
        {
            var wf = new Workflow(Macro(toggle: null, fireAfterMs: 1000, count: 5));
            wf.Pacing = new ScriptedPacing();
            int finished = 0;
            wf.RunFinished += w => finished++;

            wf.TriggerDown(KeyName.Parse("F9"), 0);
            wf.Advance(1000);                                   // threshold → Running
            for (int i = 0; i < 5; i++) wf.Advance(1000);       // five Complete passes
            Assert.That(finished, Is.EqualTo(1));
            Assert.That(wf.State, Is.EqualTo(WorkflowState.Armed), "held count=5 honored when the trigger stays down");
        }

        [Test]
        public void C5_HeldTrigger_CountFive_ReleaseWithContinueTrue_CompletesAllFive()
        {
            var wf = new Workflow(Macro(toggle: null, fireAfterMs: 1000, count: 5, continueOnRelease: true));
            wf.Pacing = new ScriptedPacing();
            int finished = 0;
            wf.RunFinished += w => finished++;

            wf.TriggerDown(KeyName.Parse("F9"), 0);
            wf.Advance(1000);
            wf.TriggerUp(KeyName.Parse("F9"), 1500);
            Assert.That(wf.State, Is.EqualTo(WorkflowState.Running), "continue_on_release:true keeps the run after release");

            for (int i = 0; i < 5; i++) wf.Advance(1500);
            Assert.That(finished, Is.EqualTo(1), "held count=5 completes even after release when the flag says so");
            Assert.That(wf.State, Is.EqualTo(WorkflowState.Armed));
        }

        [Test]
        public void C5_HeldTrigger_CountFive_ReleaseWithContinueFalse_StopsMidRun()
        {
            var wf = new Workflow(Macro(toggle: null, fireAfterMs: 1000, count: 5)); // omitted = false for held
            bool finished = false;
            wf.RunFinished += w => finished = true;

            wf.TriggerDown(KeyName.Parse("F9"), 0);
            wf.Advance(1000);
            Assert.That(wf.State, Is.EqualTo(WorkflowState.Running));

            wf.TriggerUp(KeyName.Parse("F9"), 1500);
            Assert.That(finished, Is.True, "held count=5, release + false stops the run mid-way");
            Assert.That(wf.State, Is.EqualTo(WorkflowState.Armed));
        }

        [Test]
        public void Count_TwoPasses_ThenCompletes()
        {
            var wf = new Workflow(Macro(toggle: null, fireAfterMs: null, count: 2));
            wf.Pacing = new ScriptedPacing(
                PacingDecision.Fire(0), PacingDecision.Fire(0), PacingDecision.Complete(),
                PacingDecision.Fire(0), PacingDecision.Complete());

            int fired = 0, finished = 0;
            wf.StepReady += (w, s) => fired++;
            wf.RunFinished += w => finished++;

            wf.TriggerDown(KeyName.Parse("F9"), 0);
            for (int i = 0; i < 5; i++) wf.Advance(0);

            Assert.That(fired, Is.EqualTo(3));       // pass 1: 2 steps, pass 2: 1 step
            Assert.That(finished, Is.EqualTo(1));
            Assert.That(wf.State, Is.EqualTo(WorkflowState.Armed));
        }

        [Test]
        public void Abort_SignalsCancelAndStopsRun()
        {
            var wf = new Workflow(Macro(toggle: null, fireAfterMs: null, count: -1));
            bool finished = false;
            wf.RunFinished += w => finished = true;

            wf.TriggerDown(KeyName.Parse("F9"), 0);
            Assert.That(wf.State, Is.EqualTo(WorkflowState.Running));

            wf.Abort();
            Assert.That(wf.Cancel.IsRequested, Is.True);
            Assert.That(finished, Is.True);
            Assert.That(wf.State, Is.EqualTo(WorkflowState.Armed));
        }

        [Test]
        public void ExternalCancel_IsPickedUpOnNextAdvance()
        {
            var wf = new Workflow(Macro(toggle: null, fireAfterMs: null, count: -1));
            bool finished = false;
            wf.RunFinished += w => finished = true;

            wf.TriggerDown(KeyName.Parse("F9"), 0);
            Assert.That(wf.State, Is.EqualTo(WorkflowState.Running));
            Assert.That(finished, Is.False); // nothing has asked to stop yet

            wf.Cancel.Request(); // kill / global abort path
            wf.Advance(0);
            Assert.That(finished, Is.True);
            Assert.That(wf.State, Is.EqualTo(WorkflowState.Armed));
        }

        [Test]
        public void DisarmWhileRunning_StopsRunAndReturnsToIdle()
        {
            var wf = new Workflow(Macro(toggle: KeyName.Parse("Pause"), fireAfterMs: null, count: -1));
            bool finished = false;
            wf.RunFinished += w => finished = true;

            wf.TogglePressed();          // arm
            wf.TriggerDown(KeyName.Parse("F9"), 0);
            Assert.That(wf.State, Is.EqualTo(WorkflowState.Running));

            wf.TogglePressed();          // disarm — "off always stops"
            Assert.That(finished, Is.True);
            Assert.That(wf.State, Is.EqualTo(WorkflowState.Idle));
        }

        [Test]
        public void Park_RetainsStateAndStopsAdvancing()
        {
            var wf = new Workflow(Macro(toggle: null, fireAfterMs: null, count: -1));
            wf.Pacing = new ScriptedPacing(PacingDecision.Fire(0));
            int fired = 0;
            wf.StepReady += (w, s) => fired++;

            wf.TriggerDown(KeyName.Parse("F9"), 0);
            wf.Park();
            Assert.That(wf.State, Is.EqualTo(WorkflowState.Parked));

            wf.Advance(0); // parked → not ticked
            Assert.That(fired, Is.EqualTo(0));
        }

        [Test]
        public void Park_Unpark_ResumesPreviousState()
        {
            var wf = new Workflow(Macro(toggle: null, fireAfterMs: null, count: -1));
            wf.TriggerDown(KeyName.Parse("F9"), 0);
            Assert.That(wf.State, Is.EqualTo(WorkflowState.Running));

            wf.Park();
            Assert.That(wf.State, Is.EqualTo(WorkflowState.Parked));

            wf.Unpark();
            Assert.That(wf.State, Is.EqualTo(WorkflowState.Running));
        }

        // ==== Helpers =============================================================

        private static NormalizedMacro Macro(Chord? toggle, int? fireAfterMs, int? count,
            bool? continueOnRelease = null)
        {
            return new NormalizedMacro
            {
                Name = "macro",
                Trigger = KeyName.Parse("F9"),
                FireAfterMs = fireAfterMs,
                Toggle = toggle,
                Loop = new LoopSpec { Count = count, ContinueOnRelease = continueOnRelease },
                Steps = new List<NormalizedStep>
                {
                    new NormalizedStep { Chords = new[] { KeyName.Parse("7") } },
                },
            };
        }

        /// <summary>Replaces the (Stage 4) pacing policies for transition tests:
        /// returns the scripted decisions in order, then Complete.</summary>
        private sealed class ScriptedPacing : IPacing
        {
            private readonly PacingDecision[] _plan;
            private int _index;

            public ScriptedPacing(params PacingDecision[] plan) => _plan = plan;

            public PacingDecision Next(NormalizedMacro macro, PacingState state, long nowMs)
                => _index < _plan.Length ? _plan[_index++] : PacingDecision.Complete();
        }
    }
}