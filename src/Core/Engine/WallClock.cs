using System.Diagnostics;

namespace SlitherIn.Core.Engine
{
    /// <summary>
    /// Abstract time source. THE rule: all cooldowns/countdowns run on wall-clock
    /// (Stopwatch) and are never paused by focus changes or parked profiles.
    /// Tests inject a fake clock and advance time manually.
    /// </summary>
    public abstract class WallClock
    {
        public abstract long NowMs { get; }

        public static WallClock Stopwatch { get; } = new StopwatchClock();

        private sealed class StopwatchClock : WallClock
        {
            private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
            public override long NowMs => _sw.ElapsedMilliseconds;
        }
    }
}