using System;
using System.IO;
using System.Threading;
using Microsoft.Win32;

namespace SlitherIn
{
    /// <summary>
    /// Platform-level startup concerns, all UI-logic-free. The exe's folder is the
    /// anchor for the log + icon files (config locations come from
    /// `ConfigSources`, built from settings + `--settings-file`/`--profile-dir`);
    /// run-at-startup reads/writes the current user's Run key ("carried setting" —
    /// not part of settings JSON).
    /// </summary>
    internal static class Startup
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "SlitherIn";
        private const string SecondLaunchEventName = "SlitherIn.SecondInstance";

        private static Mutex _singleInstanceMutex;
        private static EventWaitHandle _secondLaunch;

        /// <summary>
        /// Ensures only one slitherin runs per user session. Returns false (after
        /// optionally notifying the existing instance) when another is alive.
        /// </summary>
        public static bool AcquireSingleInstance()
        {
            _singleInstanceMutex = new Mutex(true, "SlitherIn.SingleInstance", out bool createdNew);
            return createdNew;
        }

        public static void ReleaseSingleInstance()
        {
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Close();
            _singleInstanceMutex = null;
        }

        /// <summary>
        /// The named event the running instance polls to learn that a second copy
        /// of slitherin was launched. Created once, owned by the primary instance.
        /// </summary>
        public static EventWaitHandle CreateSecondLaunchSignal()
        {
            if (_secondLaunch == null)
                _secondLaunch = new EventWaitHandle(false, EventResetMode.AutoReset, SecondLaunchEventName, out _);
            return _secondLaunch;
        }

        /// <summary>
        /// Signals the already-running instance to surface a "second copy launched"
        /// balloon (that instance polls the handle and pops the notification).
        /// Called by the duplicate process right before it exits.
        /// </summary>
        public static void NotifyExistingInstance()
        {
            try
            {
                using var h = new EventWaitHandle(false, EventResetMode.AutoReset, SecondLaunchEventName, out _);
                h.Set();
            }
            catch
            {
                // No running instance to notify (or an untouchable session): ignore.
            }
        }

        /// <summary>
        /// The executable's folder — the anchor for the log file and the external
        /// icon overrides. The old %SLITHERIN_DIR% env override is RETIRED: config
        /// locations are now resolved by <c>ConfigSources</c> (settings file +
        /// `profile_dirs` + `--settings-file`/`--profile-dir`).
        /// </summary>
        public static string ExeDir()
            => AppDomain.CurrentDomain.BaseDirectory;

        /// <summary>
        /// Run-at-startup state, persisted in the current user's Run key (under
        /// HKCU\Software\Microsoft\Windows\CurrentVersion\Run) as the value
        /// "SlitherIn". A carried setting — the tray toggle reads/writes it directly.
        /// </summary>
        public static bool RunAtStartup
        {
            get
            {
                using RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return key?.GetValue(RunValueName) != null;
            }
            set
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
                if (value)
                    key.SetValue(RunValueName, "\"" + Path.Combine(ExeDir(), "slitherin.exe") + "\"");
                else
                    key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
    }
}