using System;
using SlitherIn.Core.Config;
using SlitherIn.Core.Target;

namespace SlitherIn.Ui
{
    /// <summary>
    /// Contained handlers invoked by tray clicks. Their implementations live in the
    /// composition root (App) — TrayMenu only builds UI; it never touches
    /// catalog/settings directly.
    /// </summary>
    public sealed class MenuActions
    {
        /// <summary>Manual profile selection (file name).</summary>
        public Action<string> SwitchProfile;

        /// <summary>"manual" | "auto" — the profile-selection MODE.</summary>
        public Action<string> SetMode;

        public Func<bool> ToggleWindowCheck;      // returns new state
        public Func<bool> ToggleNotifications;    // returns new state
        public Func<bool> ToggleRunAtStartup;     // returns new state
        public Action OpenActiveProfileJson;
        public Action OpenSettingsJson;
        public Action OpenReleasesPage;
        public Action Exit;
    }
}