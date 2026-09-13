using SlitherIn.Core.Config;

namespace SlitherIn.Core.Engine
{
    /// <summary>
    /// Pure loop semantics — the only place "count / continue_on_release" is
    /// derived. The defaults were locked in design:
    ///   count omitted = 1; -1 = forever. count is ALWAYS honored at every
    ///   value (1, 5, −1) for BOTH trigger kinds (C-5).
    ///   continue_on_release governs HELD triggers (fire_after_ms) ONLY: omitted
    ///   = derived false (release stops the run); true = keep the full count
    ///   running after release. Tap/combo triggers NEVER consult the flag — a
    ///   tap always runs its full count to completion (key-up is the end of the
    ///   tap, not a stop).
    /// </summary>
    public static class LoopRules
    {
        public static int CountOrDefault(LoopSpec spec) => spec?.Count ?? 1;

        public static bool IsInfinite(LoopSpec spec) => CountOrDefault(spec) == -1;

        /// <summary>Held-trigger release rule: the explicit flag, else false
        /// (release stops). Tap triggers never call this.</summary>
        public static bool ContinueOnRelease(LoopSpec spec)
            => spec?.ContinueOnRelease ?? false;
    }
}