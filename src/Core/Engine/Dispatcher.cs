using System;
using System.Collections.Generic;
using SlitherIn.Core.Config;
using SlitherIn.Core.Diagnostics;
using SlitherIn.Input;

namespace SlitherIn.Core.Engine
{
    /// <summary>
    /// The cooperative dispatcher: ONE tick advances every attached macro workflow
    /// in a small slice, asks its pacing what to do next, and serializes the actual
    /// injection through the active <see cref="IKeyEngine"/>. No per-macro threads;
    /// waits are never busy-looped here — Tick is called by the driver (timer /
    /// message loop), the pacing decides, and remaining gotos return immediately.
    ///
    /// Profile switching PARKS runtime: the catalog owns parked workflows, so this
    /// dispatcher only ever ticks the active profile's workflows (attach clears on
    /// switch). A workflow that is detached mid-run keeps its state; re-attaching
    /// resumes against the current wall-clock nowMs — cooldowns "kept counting"
    /// while away ("timers never pause").
    ///
    /// Injection: a step is ONE logical action. Its chords are pressed/released
    /// one at a time (down+up each); a `hold_for_ms` step stays down for the hold
    /// and is released by the loop when due. Sound and pure-wait steps are not
    /// injected (sounds are a Shell concern). Held-chord union is tracked so
    /// parallel holds from different workflows never drop each other.
    /// </summary>
    public sealed class Dispatcher : IDisposable
    {
        internal const int DefaultCooldownFireDelayMs = 700;
        internal const int DefaultFixedDelayMs = 3000;

        private IKeyEngine _engine;
        private readonly WallClock _clock;
        private readonly Log _log;
        private readonly int _defaultDelayMs;
        private readonly List<Workflow> _attached = new();
        private readonly List<Chord> _held = new();      // union of currently-held chords
        private readonly Dictionary<Workflow, PendingHold> _pendingHolds = new();

        public Dispatcher(IKeyEngine engine, WallClock clock, Log log, int defaultDelayMs)
        {
            Engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _clock = clock;
            _log = log;
            _defaultDelayMs = defaultDelayMs;
        }

        /// <summary>The engine all injection currently flows through. The composition
        /// root swaps it when the active profile resolves to a different engine
        /// (sendinput ⇄ viiper); every SetState/ReleaseAll goes through this seam.</summary>
        public IKeyEngine Engine
        {
            get => _engine;
            set => _engine = value ?? throw new ArgumentNullException(nameof(value));
        }

        /// <summary>Advance all attached (non-parked) workflows one cooperative slice.</summary>
        public void Tick(long nowMs)
        {
            ReleaseDueHolds(nowMs);

            foreach (Workflow wf in _attached.ToArray())
            {
                if (wf.State == WorkflowState.Parked) continue;
                wf.Advance(nowMs);
            }
        }

        /// <summary>Window gate for mid-run step injection. Evaluated ONCE per Tick by
        /// the composition (from the CURRENT foreground + the active profile's target)
        /// and read by every attached workflow before it consults its pacing — so a
        /// closed gate holds ready steps but never pauses cooldowns/countdowns.</summary>
        public Func<bool> InjectionAllowed { get; set; }

        /// <summary>Attach a workflow: sets its pacing, wires the injection gate to this
        /// dispatcher's, and starts delivering StepReady/RunFinished.</summary>
        public void Attach(Workflow workflow)
        {
            if (_attached.Contains(workflow)) return;
            workflow.Pacing ??= CreatePacing(workflow.Definition);
            workflow.InjectionAllowed = () => InjectionAllowed?.Invoke() ?? true;
            workflow.StepReady += OnStepReady;
            workflow.RunFinished += OnRunFinished;
            _attached.Add(workflow);
        }

        public void Detach(Workflow workflow)
        {
            _attached.Remove(workflow);
            workflow.StepReady -= OnStepReady;
            workflow.RunFinished -= OnRunFinished;
            ReleaseHold(workflow);              // held keys always release on switch
        }

        /// <summary>Detach every workflow (profile switch / reload re-selection).
        /// Runs are NOT signalled here — parking or replacement decides their fate.</summary>
        public void DetachAll()
        {
            foreach (Workflow wf in _attached.ToArray()) Detach(wf);
        }

        /// <summary>Drop every held chord and any pending-hold schedule, telling the
        /// engine to release whatever it holds. Does NOT signal workflows (the
        /// global-abort flow calls <see cref="AbortAll"/> for that).</summary>
        public void ReleaseAll()
        {
            _pendingHolds.Clear();
            _held.Clear();
            _engine?.ReleaseAll();
        }

        /// <summary>Abort everything on reload/switch/kill: signal every workflow and
        /// release every pressed key. Workflows stay attached (App disposes or
        /// detaches as its reload policy dictates).</summary>
        public void AbortAll()
        {
            foreach (Workflow wf in _attached.ToArray()) wf.Abort();
            ReleaseAll();
        }

        public void Dispose()
        {
            AbortAll();
            foreach (Workflow wf in _attached.ToArray()) Detach(wf);
        }

        // ==== Injection ===========================================================

        private void OnStepReady(Workflow wf, NormalizedStep step)
        {
            if (step.IsSound)
            {
                _log?.Debug($"sound step '{wf.Definition.Name}' (Shell plays this at wiring time).");
                return;
            }
            if (step.Chords == null || step.Chords.Count == 0) return; // pure wait step

            if (step.HoldForMs is int holdMs)
            {
                foreach (Chord c in step.Chords) Down(c);
                _pendingHolds[wf] = new PendingHold(new List<Chord>(step.Chords), _clock.NowMs + holdMs);
            }
            else
            {
                foreach (Chord c in step.Chords)
                {
                    Down(c);
                    Up(c);
                }
            }
        }

        private void OnRunFinished(Workflow wf)
        {
            // A normally-finished run lets its hold_for_ms release happen on its own
            // due time; only a cancelled (abort/kill) run must release immediately.
            if (wf.Cancel.IsRequested) ReleaseHold(wf);
        }

        private void ReleaseDueHolds(long nowMs)
        {
            foreach (var kv in _pendingHolds.ToArray())
                if (nowMs >= kv.Value.ReleaseAtMs) ReleaseHold(kv.Key);
        }

        private void ReleaseHold(Workflow wf)
        {
            if (!_pendingHolds.TryGetValue(wf, out PendingHold hold)) return;
            _pendingHolds.Remove(wf);
            foreach (Chord c in hold.Chords) Up(c);
        }

        private void Down(Chord c)
        {
            if (!_held.Contains(c)) _held.Add(c);
            Engine.SetState(_held);
        }

        private void Up(Chord c)
        {
            _held.Remove(c);
            Engine.SetState(_held);
        }

        // ==== Pacing selection ====================================================

        /// <summary>schedule.mode: "cooldown" → CooldownPacing(fire_delay_ms ?? 700);
        /// anything else (incl. omitted) → FixedPacing(macro.DefaultDelayMs ?? the
        /// dispatcher default). The macro's resolved default comes from the Normalizer
        /// (profile ?? settings ?? 3000), so one dispatcher serves every profile.</summary>
        public IPacing CreatePacing(NormalizedMacro macro)
        {
            if (macro.Schedule?.Mode == "cooldown")
                return new CooldownPacing(macro.Schedule.FireDelayMs ?? DefaultCooldownFireDelayMs);
            return new FixedPacing(macro.DefaultDelayMs ?? _defaultDelayMs);
        }

        private sealed class PendingHold
        {
            public List<Chord> Chords;
            public long ReleaseAtMs;
            public PendingHold(List<Chord> chords, long releaseAtMs)
            {
                Chords = chords;
                ReleaseAtMs = releaseAtMs;
            }
        }
    }
}