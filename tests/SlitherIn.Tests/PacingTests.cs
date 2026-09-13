using System.Collections.Generic;
using NUnit.Framework;
using SlitherIn.Core.Config;
using SlitherIn.Core.Engine;

namespace SlitherIn.Tests
{
    /// <summary>Fixed + cooldown pacing sequences with fake time. Stage 4.</summary>
    [TestFixture]
    public class PacingTests
    {
        [Test]
        public void FixedPacing_FiresInOrder_WithDefaults()
        {
            var macro = Macro(Steps("7", "8", "9"));
            var pacing = new FixedPacing(500);
            var st = NewState(1);

            AssertDecision(pacing.Next(macro, st, 0), FireAt: 0);      // fire-on-press
            ApplyFire(st, 0, 0);

            AssertDecision(pacing.Next(macro, st, 100), Wait: 400);    // due 0 + 500
            AssertDecision(pacing.Next(macro, st, 500), FireAt: 1);
            ApplyFire(st, 1, 500);

            AssertDecision(pacing.Next(macro, st, 800), Wait: 200);    // due 1000
            AssertDecision(pacing.Next(macro, st, 1000), FireAt: 2);
            ApplyFire(st, 2, 1000);

            AssertDecision(pacing.Next(macro, st, 1400), Wait: 100);   // due 1500
            AssertDecision(pacing.Next(macro, st, 1500), Complete: true);

            // Workflow.CompletePass hands the policy a fresh PacingState (same RunId).
            st = NewState(1);
            AssertDecision(pacing.Next(macro, st, 1500), FireAt: 0);   // next pass starts on press
        }

        [Test]
        public void FixedPacing_StepWaitOverrideAndZero_LocalWins()
        {
            var macro = Macro(
                Step("7", waitMs: 0),        // forces none → next fires immediately
                Step("8", waitMs: 3000),     // overrides the 500 default
                Step("9"));                  // default 500 applies
            var pacing = new FixedPacing(500);
            var st = NewState(1);

            AssertDecision(pacing.Next(macro, st, 0), FireAt: 0);
            ApplyFire(st, 0, 0);

            AssertDecision(pacing.Next(macro, st, 10), FireAt: 1);     // 0 forced no delay
            ApplyFire(st, 1, 10);

            AssertDecision(pacing.Next(macro, st, 1000), Wait: 2010);  // due 3010
            AssertDecision(pacing.Next(macro, st, 3010), FireAt: 2);
            ApplyFire(st, 2, 3010);

            AssertDecision(pacing.Next(macro, st, 3511), Complete: true); // 3510 elapsed
        }

        [Test]
        public void CooldownPacing_SkipsUntilExpiry_SpacesFirings()
        {
            var macro = Macro(                     // fire_delay 100
                Step("7", cooldownMs: 300),        // ready again at +300
                Step("8"));                        // always ready
            var pacing = new CooldownPacing(100);
            var st = NewState(1);

            AssertDecision(pacing.Next(macro, st, 0), FireAt: 0);      // step 7 fires now
            ApplyFire(st, 0, 0);

            AssertDecision(pacing.Next(macro, st, 40), Wait: 60);      // step 8 ready, spaced to +100
            AssertDecision(pacing.Next(macro, st, 100), FireAt: 1);
            ApplyFire(st, 1, 100);

            AssertDecision(pacing.Next(macro, st, 120), Complete: true); // scan exhausted

            AssertDecision(pacing.Next(macro, st, 210), FireAt: 1);    // 8 always ready; 100+100 elapsed
            ApplyFire(st, 1, 210);

            // Scan exhausted right after the fire → the pass completes; the rescan
            // then fires step 7 at 310, where its 300 cooldown meets the fire delay.
            AssertDecision(pacing.Next(macro, st, 300), Complete: true);
            AssertDecision(pacing.Next(macro, st, 310), FireAt: 0);
            ApplyFire(st, 0, 310);
        }

        [Test]
        public void CooldownPacing_AlwaysReady_FiresAtFireDelayCadence()
        {
            var macro = Macro(Step("7"));
            var pacing = new CooldownPacing(100);
            var st = NewState(1);

            // A single always-ready step settles into the fire_delay cadence: fire
            // at 0, the scan completes, the rescan fires again at +100, etc. (The
            // spacing gate is checked on rescan, so firings stay 100 ms apart.)
            AssertDecision(pacing.Next(macro, st, 0), FireAt: 0);
            ApplyFire(st, 0, 0);

            AssertDecision(pacing.Next(macro, st, 40), Complete: true); // scan exhausted
            AssertDecision(pacing.Next(macro, st, 100), FireAt: 0);     // rescan, due reached
            ApplyFire(st, 0, 100);

            AssertDecision(pacing.Next(macro, st, 101), Complete: true);
            AssertDecision(pacing.Next(macro, st, 200), FireAt: 0);     // exactly +100 again
            ApplyFire(st, 0, 200);

            // Not a millisecond early: it yields to the gate until due.
            AssertDecision(pacing.Next(macro, st, 201), Complete: true);
            AssertDecision(pacing.Next(macro, st, 202), Wait: 98);
            AssertDecision(pacing.Next(macro, st, 300), FireAt: 0);
        }

        [Test]
        public void CooldownPacing_ParkedGap_ExpiryIsAbsoluteWallClock()
        {
            var macro = Macro(Step("7", cooldownMs: 500));
            var pacing = new CooldownPacing(100);
            var st = NewState(1);

            AssertDecision(pacing.Next(macro, st, 0), FireAt: 0);      // ready again at 500
            ApplyFire(st, 0, 0);

            AssertDecision(pacing.Next(macro, st, 100), Complete: true);

            // "Parked": the pacing is simply not asked; 900 ms pass. The cooldown
            // was absolute (`readyAt = 500`), so it is long elapsed on return.
            AssertDecision(pacing.Next(macro, st, 1000), FireAt: 0);   // catch-up, fires immediately
        }

        [Test]
        public void CooldownPacing_ReTrigger_NewRunResetsCooldowns()
        {
            var macro = Macro(Step("7", cooldownMs: 1000));
            var pacing = new CooldownPacing(100);
            var st = NewState(1);

            AssertDecision(pacing.Next(macro, st, 0), FireAt: 0);
            ApplyFire(st, 0, 0);
            AssertDecision(pacing.Next(macro, st, 800), Complete: true); // 7 still cooling

            // Re-trigger = new run (Workflow bumps RunId and resets PacingState).
            st = NewState(2);
            AssertDecision(pacing.Next(macro, st, 900), FireAt: 0);     // cooldowns reset → fires
        }

        // ==== Helpers =============================================================

        private static PacingState NewState(int runId)
            => new() { RunId = runId, LastFiredIndex = -1, LastFireMs = -1 };

        private static void ApplyFire(PacingState st, int index, long nowMs)
        {
            st.LastFiredIndex = index;
            st.LastFireMs = nowMs;
        }

        private static void AssertDecision(PacingDecision d, int? FireAt = null, int? Wait = null,
            bool Complete = false)
        {
            if (FireAt.HasValue)
            {
                Assert.That(d.Action, Is.EqualTo(PacingAction.FireStep), "expected Fire");
                Assert.That(d.StepIndex, Is.EqualTo(FireAt.Value));
            }
            else if (Wait.HasValue)
            {
                Assert.That(d.Action, Is.EqualTo(PacingAction.WaitMs), "expected Wait");
                Assert.That(d.WaitMs, Is.EqualTo(Wait.Value));
            }
            else if (Complete)
            {
                Assert.That(d.Action, Is.EqualTo(PacingAction.RunComplete), "expected Complete");
            }
            else Assert.Fail("no expectation given");
        }

        private static NormalizedMacro Macro(params NormalizedStep[] steps)
            => new() { Name = "m", Steps = new List<NormalizedStep>(steps) };

        private static NormalizedStep[] Steps(params string[] keys) => System.Array.ConvertAll(keys,
            k => new NormalizedStep { Chords = new[] { KeyName.Parse(k) } });

        private static NormalizedStep Step(string key, int? waitMs = null, int? cooldownMs = null)
            => new()
            {
                Chords = new[] { KeyName.Parse(key) },
                WaitMs = waitMs,
                CooldownMs = cooldownMs,
            };
    }
}