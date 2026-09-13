using SlitherIn.Core.Config;

namespace SlitherIn.Core.Engine
{
    /// <summary>
    /// Default pacing: steps fire in list order. After step i fires, the next step
    /// waits the step's own `wait_ms` (0 forces none), else the resolved default
    /// delay. The FIRST step of a run/pass fires on press (fire-on-press — no
    /// initial delay). When the list is exhausted the policy returns RunComplete
    /// and the <see cref="Workflow"/> re-runs per the loop count, or finishes.
    ///
    /// Timing is absolute wall-clock via the `nowMs` argument: a parked run that
    /// isn't ticked resumes correctly later ("timers never pause"), because the
    /// delay gate compares against the previous fire's wall-clock time rather than
    /// a running tally.
    /// </summary>
    public sealed class FixedPacing : IPacing
    {
        private readonly int _defaultDelayMs;
        private int _run = -1;
        private int _nextIndex;

        public FixedPacing(int defaultDelayMs) => _defaultDelayMs = defaultDelayMs;

        public PacingDecision Next(NormalizedMacro macro, PacingState state, long nowMs)
        {
            if (state.RunId != _run)
            {
                _run = state.RunId;   // fresh run → start the list over (e.g. re-trigger)
                _nextIndex = 0;
            }

            if (_nextIndex >= macro.Steps.Count)
            {
                // Pass boundary ALSO honors the final step's delay ("between every
                // step" = loop wrapper): the run completes only once that has elapsed.
                if (_nextIndex > 0 && state.LastFireMs >= 0)
                {
                    int delay = macro.Steps[macro.Steps.Count - 1].WaitMs ?? _defaultDelayMs;
                    long due = state.LastFireMs + delay;
                    if (nowMs < due) return PacingDecision.Wait((int)(due - nowMs));
                }
                _nextIndex = 0;       // next call starts the next pass on press
                return PacingDecision.Complete();
            }

            // Delay gate belongs to the step that just fired (its wait_ms, else the
            // default). First step of a pass: fire on press, no gate.
            if (_nextIndex > 0 && state.LastFireMs >= 0)
            {
                int delay = macro.Steps[_nextIndex - 1].WaitMs ?? _defaultDelayMs;
                long due = state.LastFireMs + delay;
                if (nowMs < due) return PacingDecision.Wait((int)(due - nowMs));
            }

            int index = _nextIndex++;
            return PacingDecision.Fire(index);
        }
    }
}