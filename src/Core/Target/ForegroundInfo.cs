using System;

namespace SlitherIn.Core.Target
{
    /// <summary>
    /// Immutable snapshot of the foreground window taken at event time, so target
    /// matching stays deterministic even if focus changes mid-decision.
    /// </summary>
    public readonly struct ForegroundInfo
    {
        public readonly IntPtr Hwnd;
        public readonly string ProcessExe;
        public readonly string Title;

        public ForegroundInfo(IntPtr hwnd, string processExe, string title)
        {
            Hwnd = hwnd;
            ProcessExe = processExe;
            Title = title;
        }

        public override string ToString() => $"{ProcessExe} | {Title}";

        public static readonly ForegroundInfo Empty = new(IntPtr.Zero, null, null);
    }
}