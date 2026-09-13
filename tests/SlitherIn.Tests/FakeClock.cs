using SlitherIn.Core.Engine;

namespace SlitherIn.Tests
{
    /// <summary>Manually-advanceable clock so engine tests don't depend on real
    /// sleep: the wall-clock rule (timers never pause) is tested by advancing time
    /// while a macro is parked/unfocused.</summary>
    internal sealed class FakeClock : WallClock
    {
        public long Time { get; set; }
        public override long NowMs => Time;
    }
}