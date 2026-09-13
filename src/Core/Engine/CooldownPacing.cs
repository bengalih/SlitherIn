using SlitherIn.Core.Config;

namespace SlitherIn.Core.Engine
{
    /// <summary>
    /// Cooldown scheduler (the old cycle behavior). A run is a repeating SCAN:
    /// each scan walks the steps in list order and fires EVERY step whose
    /// `cooldown_ms` has elapsed since its last firing (a step without cooldown_ms
    /// is ALWAYS ready), spacing consecutive firings by `fire_delay_ms`, then
    /// rescans. A scan that fires nothing completes immediately — the Workflow
    /// counts that as one finished pass (finite count ends the run after N scans;
    /// infinite rescans forever, so always-ready steps settle into a fire_delay
    /// cadence).
    ///
    /// Cooldowns and the spacing gate are absolute wall-clock: a block of parked /
    /// not-ticked time passes for free, and a step that became ready while away
    /// fires on the first opportunity after unparking (the dispatcher's catch-up
    /// rule). All per-run state resets only on a RunId change (a fresh run, e.g.
    /// a re-trigger) — not on pass boundaries.
    /// </summary>
    public sealed class CooldownPacing : IPacing
    {
        private readonly int _fireDelayMs;
        private int _run = -1;
        private long[] _readyAt;
        private int _scanIndex;
        private long _lastFireMs = -1;

        public CooldownPacing(int fireDelayMs) => _fireDelayMs = fireDelayMs;

        public PacingDecision Next(NormalizedMacro macro, PacingState state, long nowMs)
        {
            if (state.RunId != _run)
            {
                _run = state.RunId;
                _readyAt = new long[macro.Steps.Count];  // all 0 → every step ready
                _scanIndex = 0;
                _lastFireMs = -1;
            }

            for (int i = _scanIndex; i < macro.Steps.Count; i++)
            {
                if (nowMs < _readyAt[i]) continue;       // still cooling → look further in the scan

                if (_lastFireMs >= 0)
                {
                    long due = _lastFireMs + _fireDelayMs;
                    if (nowMs < due) return PacingDecision.Wait((int)(due - nowMs));
                }

                _readyAt[i] = nowMs + (macro.Steps[i].CooldownMs ?? 0);
                _lastFireMs = nowMs;
                _scanIndex = i + 1;
                return PacingDecision.Fire(i);
            }

            _scanIndex = 0;                              // scan exhausted → pass boundary, rescan next call
            return PacingDecision.Complete();
        }
    }
}