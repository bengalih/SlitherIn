using System;
using SlitherIn.Core.Config;

namespace SlitherIn.Core.Engine
{
    /// <summary>States of the ONE start→run→end container (per macro).</summary>
    public enum WorkflowState
    {
        /// <summary>Not toggled-on and not running.</summary>
        Idle,
        /// <summary>Toggle key has armed it (beep).</summary>
        Armed,
        /// <summary>Trigger held past `fire_after_ms`; about to start a run.</summary>
        AwaitingHold,
        /// <summary>A run is advancing through steps/pacing.</summary>
        Running,
        /// <summary>Parked by a profile switch — state retained, not ticked.</summary>
        Parked,
    }

    /// <summary>
    /// The ONE workflow container shared by every macro kind (sequence = cycle with
    /// count 1). Driven exclusively by the Dispatcher's cooperative loop. It owns
    /// the per-macro lifecycle — arm/disarm toggle, trigger hold threshold
    /// (fire_after_ms), release-behavior (continue_on_release), loop count, and
    /// per-macro cancellation — and hands step timing to the active
    /// <see cref="Pacing"/>. It NEVER sends input: the Dispatcher subscribes to
    /// <see cref="StepReady"/> and does the injection.
    ///
    /// Trigger semantics:
    ///   - no toggle configured  → built Armed, a trigger press starts a run.
    ///   - toggle configured     → built Idle; <see cref="TogglePressed"/> arms and
    ///     disarms ("off" always stops the run).
    ///   - held trigger (fire_after_ms) → press → AwaitingHold → run at the
    ///     threshold; releasing before the threshold cancels the hold.
    ///   - release while Running: continue_on_release false → run stops; true → not.
    ///   - re-trigger while Running is ignored (design: restart/resume is an open
    ///     decision, default ignore).
    /// </summary>
    public sealed class Workflow
    {
        public NormalizedMacro Definition { get; }

        private WorkflowState _state;
        public WorkflowState State => _state;

        private Cancellation _cancel = new Cancellation();
        public Cancellation Cancel => _cancel;

        /// <summary>Snapshot of "last trigger chord that started this run", used for
        /// toggle/repeated-trigger bookkeeping.</summary>
        public Chord LastTrigger { get; private set; }

        /// <summary>Pacing policy for the current run (set by the Dispatcher).
        /// Timing is its only responsibility.</summary>
        public IPacing Pacing { get; set; }

        /// <summary>Per-run pacing state handed to the policy.</summary>
        public PacingState PacingState { get; private set; }

        /// <summary>Mid-run injection gate, wired by the Dispatcher. When it returns
        /// false a step that would fire is HELD (the pacing is not consulted, so its
        /// cursor never advances) and fires on the next tick where it returns true —
        /// the skip-but-kept-ready catch-up. Null = always allowed.</summary>
        public Func<bool> InjectionAllowed { get; set; }

        /// <summary>A step is ready to inject. The Dispatcher injects keys/sound here.</summary>
        public event Action<Workflow, NormalizedStep> StepReady;

        /// <summary>A run ended (completed or cancelled) — Dispatcher releases held keys.</summary>
        public event Action<Workflow> RunFinished;

        /// <summary>Armed via toggle — App plays the "on" cue.</summary>
        public event Action<Workflow> ToggleArmed;

        /// <summary>Disarmed via toggle — App plays the "off" cue and stops everything.</summary>
        public event Action<Workflow> ToggleDisarmed;

        private bool _alwaysArmed => Definition.Toggle == null;
        private bool _heldTrigger => Definition.FireAfterMs.HasValue;
        private long _holdStartMs;
        private int _passesRemaining;
        private bool _infinite;
        private int _runId;
        private WorkflowState _parkedFromState;

        public Workflow(NormalizedMacro definition)
        {
            Definition = definition ?? throw new ArgumentNullException(nameof(definition));
            _state = _alwaysArmed ? WorkflowState.Armed : WorkflowState.Idle;
        }

        // ==== Toggle ============================================================

        public void TogglePressed()
        {
            if (Definition.Toggle == null) return;
            if (_state == WorkflowState.Idle) Arm();
            else Disarm();
        }

        public void Arm()
        {
            if (_state == WorkflowState.Armed) return;
            _state = WorkflowState.Armed;
            ToggleArmed?.Invoke(this);
        }

        public void Disarm()
        {
            if (_state == WorkflowState.Idle) return;
            EndRun();
            _state = WorkflowState.Idle;
            ToggleDisarmed?.Invoke(this);
        }

        // ==== Trigger ============================================================

        public void TriggerDown(Chord chord, long nowMs)
        {
            LastTrigger = chord;
            if (_state != WorkflowState.Armed) return; // re-trigger while running → ignore
            if (_heldTrigger)
            {
                _holdStartMs = nowMs;
                _state = WorkflowState.AwaitingHold;
            }
            else
            {
                StartRun();
            }
        }

        public void TriggerUp(Chord chord, long nowMs)
        {
            if (_state == WorkflowState.AwaitingHold)
            {
                // Released before the fire_after_ms threshold → cancel the hold.
                _state = WorkflowState.Armed;
            }
            else if (_state == WorkflowState.Running && _heldTrigger &&
                !LoopRules.ContinueOnRelease(Definition.Loop))
            {
                // C-5: release-behavior governs HELD triggers only. A tap/press
                // trigger always runs its FULL count to completion — key-up is the
                // end of the tap, never a stop (abort/kill/toggle-off only).
                EndRun();
                _state = WorkflowState.Armed;
            }
        }

        // ==== Abort (per-macro abort_keys; kill/global just call Cancel.Request) ===

        public void Abort()
        {
            _cancel.Request();
            if (_state == WorkflowState.Running || _state == WorkflowState.AwaitingHold)
            {
                EndRun();
                _state = WorkflowState.Armed;
            }
        }

        // ==== Driving — one cooperative slice ====================================

        public void Advance(long nowMs)
        {
            switch (_state)
            {
                case WorkflowState.Parked:
                    return;

                case WorkflowState.AwaitingHold:
                    if (nowMs >= _holdStartMs + Definition.FireAfterMs.Value)
                        StartRun();
                    return;

                case WorkflowState.Running:
                    if (_cancel.IsRequested)
                    {
                        EndRun();
                        _state = WorkflowState.Armed;
                        return;
                    }
                    if (Pacing == null) return;
                    if (InjectionAllowed != null && !InjectionAllowed())
                        return; // gate closed: hold the ready step, don't advance pacing; wall-clock continues
                    switch (Pacing.Next(Definition, PacingState, nowMs))
                    {
                        case PacingDecision d when d.Action == PacingAction.FireStep:
                            PacingState.LastFiredIndex = d.StepIndex;
                            PacingState.LastFireMs = nowMs;
                            if (d.StepIndex >= 0 && d.StepIndex < Definition.Steps.Count)
                                StepReady?.Invoke(this, Definition.Steps[d.StepIndex]);
                            break;
                        case PacingDecision d when d.Action == PacingAction.WaitMs:
                            break; // dispatcher re-ticks after the wait (or its slice budget)
                        case PacingDecision d when d.Action == PacingAction.RunComplete:
                            CompletePass();
                            break;
                    }
                    return;
            }
        }

        // ==== Parking (profile switch; state retained, wall-clock keeps counting) ===

        public void Park()
        {
            _parkedFromState = _state;
            _state = WorkflowState.Parked;
        }

        /// <summary>Resume the pre-park state. Cooldowns/countdowns use absolute
        /// wall-clock, so time that passed while parked is simply already elapsed —
        /// a step that became ready while away fires on the first tick back.</summary>
        public void Unpark()
        {
            if (_state != WorkflowState.Parked) return;
            _state = _parkedFromState;
        }

        // ==== Internals ============================================================

        private void StartRun()
        {
            _cancel = new Cancellation();
            int count = LoopRules.CountOrDefault(Definition.Loop);
            _infinite = LoopRules.IsInfinite(Definition.Loop);
            _passesRemaining = _infinite ? 0 : count;
            _runId++;
            PacingState = new PacingState
            {
                RunId = _runId,
                RunsRemaining = _infinite ? -1 : count,
                LastFiredIndex = -1,
            };

            if (!_infinite && _passesRemaining < 1)
            {
                _state = WorkflowState.Armed; // count 0 → nothing to run
                return;
            }

            _state = WorkflowState.Running;
        }

        private void CompletePass()
        {
            if (_infinite)
            {
                PacingState = new PacingState { RunId = _runId, RunsRemaining = -1, LastFiredIndex = -1 };
                return;
            }

            _passesRemaining--;
            if (_passesRemaining > 0)
            {
                PacingState = new PacingState { RunId = _runId, RunsRemaining = _passesRemaining, LastFiredIndex = -1 };
                return;
            }

            EndRun();
            _state = WorkflowState.Armed;
        }

        private void EndRun()
        {
            if (_state == WorkflowState.Running || _state == WorkflowState.AwaitingHold)
                RunFinished?.Invoke(this);
        }
    }
}