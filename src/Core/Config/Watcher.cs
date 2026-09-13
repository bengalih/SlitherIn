using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace SlitherIn.Core.Config
{
    /// <summary>Raised after a debounce window when a monitored config file changes;
    /// carries which file so the app can decide (profile vs settings).</summary>
    public sealed class ReloadRequestedEventArgs : EventArgs
    {
        public string FilePath { get; private set; }
        public bool IsSettings { get; private set; }

        public ReloadRequestedEventArgs(string filePath, bool isSettings)
        {
            FilePath = filePath;
            IsSettings = isSettings;
        }
    }

    /// <summary>
    /// Filesystem watcher over one or more config folders with debounce (~300 ms).
    /// Purely reports "a watched file changed" — the <b>App</b> decides the
    /// semantics (file reload = abort + disarm all; broken config = keep old +
    /// alert).
    ///
    /// Roots come from <see cref="ConfigSources"/>. <see cref="SetRoots"/> re-points
    /// them after a reload (the settings file can move, and settings
    /// `profile_dirs` can change). Dirs that don't exist are skipped but retried on
    /// the next <see cref="SetRoots"/>.
    ///
    /// Debounce: a burst of events (editors write many times) coalesces into ONE
    /// ReloadRequested for the last path in the window. <see cref="SuppressFor"/>
    /// lets the App suppress its OWN writes (settings toggles) so saving a toggle
    /// never re-triggers a full reload.
    /// </summary>
    public sealed class Watcher : IDisposable
    {
        /// <summary>Debounce window: hold a pending change this long before raising.</summary>
        public const int DebounceMs = 300;

        private readonly object _lock = new();
        private readonly string _settingsFileName;
        private readonly List<FileSystemWatcher> _watchers = new();
        private System.Threading.Timer _debounce;
        private string _pendingPath;
        private bool _pendingIsSettings;
        private bool _started;
        private readonly List<(string Path, long UntilMs)> _suppressed = new();

        public event EventHandler<ReloadRequestedEventArgs> ReloadRequested;

        public Watcher(IEnumerable<string> roots, string settingsFileName)
        {
            _settingsFileName = string.IsNullOrWhiteSpace(settingsFileName)
                ? Loader.SettingsFileName
                : settingsFileName;
            SetRoots(roots);
        }

        /// <summary>Begin reporting changes. Called by the App AFTER it has
        /// subscribed, so no change event can be lost to a not-yet-connected
        /// consumer.</summary>
        public void Start()
        {
            lock (_lock)
            {
                _started = true;
                foreach (FileSystemWatcher w in _watchers) w.EnableRaisingEvents = true;
            }
        }

        /// <summary>Re-point the watched folders. New roots that already exist are
        /// watched immediately; missing ones are remembered (the list is stored via
        /// the watcher set) and created on a later call — e.g. right after a reload.
        /// </summary>
        public void SetRoots(IEnumerable<string> roots)
        {
            lock (_lock)
            {
                List<string> desired = Dedupe(roots ?? Array.Empty<string>());

                // Drop watchers whose root is no longer desired; keep the rest.
                for (int i = _watchers.Count - 1; i >= 0; i--)
                {
                    if (desired.Any(d => SamePath(d, _watchers[i].Path))) continue;
                    _watchers[i].Dispose();
                    _watchers.RemoveAt(i);
                }

                // Watch newly-desired, existing roots.
                foreach (string root in desired)
                {
                    if (_watchers.Any(w => SamePath(w.Path, root))) continue;
                    if (!Directory.Exists(root)) continue;

                    var watcher = new FileSystemWatcher(root, "*.json")
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                        IncludeSubdirectories = false,
                        EnableRaisingEvents = _started,
                    };
                    watcher.Changed += OnChanged;
                    watcher.Created += OnChanged;
                    watcher.Renamed += OnChanged;
                    watcher.Deleted += OnChanged;
                    _watchers.Add(watcher);
                }
            }
        }

        /// <summary>Ignore changes to <paramref name="path"/> for this many
        /// milliseconds (self-write guard: the App suppresses its own settings
        /// saves so a toggle doesn't cascade into a full reload loop).</summary>
        public void SuppressFor(string path, int milliseconds)
        {
            if (string.IsNullOrWhiteSpace(path) || milliseconds < 0) return;
            lock (_lock)
            {
                _suppressed.RemoveAll(s => SamePath(s.Path, path));
                _suppressed.Add((path, NowMs() + milliseconds));
            }
        }

        private void OnChanged(object sender, FileSystemEventArgs e)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(e.FullPath)) return;
                if (!IsInterestingFile(e.Name)) return; // editor/tmp artifacts, unrelated json

                lock (_lock)
                {
                    _pendingPath = e.FullPath;
                    _pendingIsSettings = IsSettingsFile(e.Name);
                }

                _debounce?.Dispose();
                _debounce = new System.Threading.Timer(_ => FirePending(), null, DebounceMs, Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
                // A root was re-pointed while this event was in flight — drop it.
            }
        }

        private void FirePending()
        {
            string path;
            bool isSettings;
            lock (_lock)
            {
                if (_pendingPath == null) return;
                path = _pendingPath;
                isSettings = _pendingIsSettings;
                _pendingPath = null;

                if (IsSuppressedNow(path)) return; // our own write — swallow silently
            }

            ReloadRequested?.Invoke(this, new ReloadRequestedEventArgs(path, isSettings));
        }

        private bool IsSuppressedNow(string path)
        {
            long now = NowMs();
            for (int i = _suppressed.Count - 1; i >= 0; i--)
            {
                if (_suppressed[i].UntilMs <= now)
                {
                    _suppressed.RemoveAt(i);
                    continue;
                }
                if (SamePath(_suppressed[i].Path, path)) return true;
            }
            return false;
        }

        private bool IsInterestingFile(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            if (string.Equals(name, _settingsFileName, StringComparison.OrdinalIgnoreCase)) return true; // custom settings file name
            if (name.StartsWith("~", StringComparison.Ordinal)) return false;          // editor temp
            if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return false; // our writes
            return name.StartsWith("slitherin", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsSettingsFile(string name)
            => name != null && name.Equals(_settingsFileName, StringComparison.OrdinalIgnoreCase);

        private static bool SamePath(string a, string b)
            => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static List<string> Dedupe(IEnumerable<string> paths)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string p in paths)
                if (!string.IsNullOrWhiteSpace(p) && seen.Add(p)) result.Add(p);
            return result;
        }

        private static long NowMs()
            => DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

        public void Dispose()
        {
            _debounce?.Dispose();
            _debounce = null;
            lock (_lock)
            {
                foreach (FileSystemWatcher w in _watchers) w.Dispose();
                _watchers.Clear();
            }
        }
    }
}