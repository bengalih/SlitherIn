using SlitherIn.Core.Config;

namespace SlitherIn.Core.Engine
{
    /// <summary>What the pacing policy wants the dispatcher to do next.</summary>
    public enum PacingAction
    {
        /// <summary>Inject this step now.</summary>
        FireStep,
        /// <summary>Do nothing for this many ms, then ask again.</summary>
        WaitMs,
        /// <summary>This run is over (count reached / no more steps).</summary>
        RunComplete,
    }

    public sealed class PacingDecision
    {
        public PacingAction Action { get; init; }
        public int StepIndex { get; init; } = -1;
        public int WaitMs { get; init; }
        public static PacingDecision Wait(int ms) => new() { Action = PacingAction.WaitMs, WaitMs = ms };
        public static PacingDecision Fire(int index) => new() { Action = PacingAction.FireStep, StepIndex = index };
        public static PacingDecision Complete() => new() { Action = PacingAction.RunComplete };
    }

    /// <summary>
    /// Pacing policy seam. <see cref="Workflow"/> sequences steps but never decides
    /// timing; the policy does. Two implementations exist (fixed, cooldown); a new
    /// policy is a new class, never a new macro kind.
    /// </summary>
    public interface IPacing
    {
        PacingDecision Next(NormalizedMacro macro, PacingState state, long nowMs);
    }

    /// <summary>Mutable per-run pacing state handed to policies.</summary>
    public sealed class PacingState
    {
        /// <summary>Incremented by Workflow on every fresh run (re-trigger); stable
        /// across loop passes and park/unpark within that run. A policy uses it to
        /// reset per-run cursors/cooldowns: a parked-and-resumed run keeps them,
        /// a re-triggered run starts clean.</summary>
        public int RunId { get; set; }
        /// <summary>Index into macro.Steps of the last fired step (-1 = none).</summary>
        public int LastFiredIndex { get; set; } = -1;
        /// <summary>Wall-clock ms when the previous step fired.</summary>
        public long LastFireMs { get; set; } = -1;
        /// <summary>Remaining run count (decremented on complete pass).</summary>
        public int RunsRemaining { get; set; } = 1;
    }
}