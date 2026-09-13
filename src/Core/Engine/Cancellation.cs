using System;

namespace SlitherIn.Core.Engine
{
    /// <summary>
    /// Cooperative cancellation for one macro run. The single detection path
    /// (low-level hook) requests it; waits and sound playback check it. Nothing
    /// aborts threads — the cooperative dispatcher just stops advancing the macro.
    /// </summary>
    public sealed class Cancellation
    {
        private readonly bool _mutable;

        public bool IsRequested { get; private set; }

        public Cancellation(bool mutable = true) => _mutable = mutable;

        public void Request()
        {
            if (_mutable) IsRequested = true;
        }

        /// <summary>Reusable non-cancelling token.</summary>
        public static Cancellation Never { get; } = new Cancellation(mutable: false);

        public void ThrowIfCancelled()
        {
            if (IsRequested) throw new OperationCanceledException();
        }
    }
}