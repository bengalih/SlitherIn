using System;
using System.IO;
using SlitherIn.Core.Config;
using SlitherIn.Core.Target;

namespace SlitherIn.Core.Diagnostics
{
    public enum LogLevel
    {
        Off, On, Debug,
    }

    /// <summary>
    /// Three-level logger. Levels locked in design: `off` (no log file created at
    /// all, the default) / `on` (REV + errors/warnings) / `debug` (adds every hook
    /// event in canonical config names + the focused window, step firing, reloads).
    ///
    /// The canonical-event lines ARE the key-finder: a user presses an unknown key
    /// (e.g. a side button) and reads the exact string to paste into config.
    /// </summary>
    public sealed class Log : IDisposable
    {
        private readonly object _lock = new();
        private StreamWriter _writer;

        public LogLevel Level { get; private set; } = LogLevel.Off;
        public string FilePath { get; private set; }

        public static LogLevel Parse(string value) => value?.Trim().ToLowerInvariant() switch
        {
            "on" => LogLevel.On,
            "debug" => LogLevel.Debug,
            _ => LogLevel.Off,
        };

        /// <summary>Open (or truncate) the log at the runtime dir; called on boot. The
        /// REV banner IS the "build-ran proof" line the manual test looks for.</summary>
        public void Start(LogLevel level, string rev, string dir)
        {
            Level = level;
            if (_writer != null) { _writer.Dispose(); _writer = null; }
            if (level == LogLevel.Off) return;

            string path = Path.Combine(dir, "slitherin.log");
            _writer = new StreamWriter(path, append: false) { AutoFlush = true };
            FilePath = path;
            Write("info", $"SlitherIn {rev} starting (log level: {level})");
        }

        public void Info(string message) => Write("info", message);
        public void Warn(string message) => Write("warn", message);
        public void Error(string message) => Write("error", message);
        public void Debug(string message) => Write("debug", message);

        /// <summary>Canonical key event line: the pasted-into-config string + the
        /// window it landed in. This is the key-finder feature.</summary>
        public void KeyEvent(Chord chord, ForegroundInfo foreground)
        {
            if (Level != LogLevel.Debug) return;
            Debug($"[input] {chord} -> {foreground}");
        }

        private void Write(string level, string message)
        {
            if (Level == LogLevel.Off || _writer == null) return;
            lock (_lock)
            {
                _writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}");
            }
        }

        public void Dispose()
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}