using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace SlitherIn.Ui
{
    /// <summary>
    /// NotifyIcon lifecycle: show/hide, custom tray icons (normal ⇄ alert used
    /// for errors / the VIIPER hard-fail), alert flash + tooltip, balloons, and
    /// left-click-to-open-menu. No menu logic (that's <see cref="TrayMenu"/>).
    /// </summary>
    public sealed class TrayHost : IDisposable
    {
        private const int FlashMs = 500;

        private readonly NotifyIcon _icon = new();
        private readonly System.Threading.Timer _flash = new(ToggleFlash, null, Timeout.Infinite, Timeout.Infinite);
        private Icon _normalIcon;
        private Icon _alertIcon;
        private string _normalText = "SlitherIn";
        private string _alertReason;
        private bool _flashOnAlert;
        private SynchronizationContext _ui;

        public ContextMenuStrip Menu { get; set; }

        /// <summary>Gates balloon popups — mirrors the Notifications setting.</summary>
        public bool NotificationsEnabled { get; set; }

        public void Show(string tooltipText)
        {
            _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            _normalIcon = LoadIcon("icon.ico", "SlitherIn.Ui.Icons.icon") ?? SystemIcons.Application;
            _alertIcon = LoadIcon("alert.ico", "SlitherIn.Ui.Icons.alert") ?? _normalIcon;
            _normalText = tooltipText;

            _icon.Icon = _normalIcon;
            _icon.Text = tooltipText;
            _icon.ContextMenuStrip = Menu;
            _icon.MouseClick += OnMouseClick;
            _icon.Visible = true;
        }

        /// <summary>Alert state for config errors and the VIIPER hard-fail:
        /// flashes normal ⇄ alert, retitles the tooltip with the reason, and
        /// leaves the alert glyph last shown. Cleared back to the normal state
        /// ("SlitherIn" tooltip, no flash).</summary>
        public void SetAlert(bool alert, string reason = null)
        {
            try
            {
                if (alert)
                {
                    _alertReason = Truncate(reason, 100);
                    _flashOnAlert = false;
                    OnUi(() =>
                    {
                        _icon.Icon = _normalIcon;
                        _icon.Text = Truncate("SlitherIn - " + _alertReason, 60);
                    });
                    _flash.Change(100, FlashMs);
                }
                else
                {
                    _flash.Change(Timeout.Infinite, Timeout.Infinite);
                    OnUi(() =>
                    {
                        _icon.Icon = _normalIcon;
                        _icon.Text = _normalText;
                    });
                }
            }
            catch
            {
                // Icon flash must never take the app down.
            }
        }

        public void Balloon(string title, string text, ToolTipIcon icon = ToolTipIcon.Info)
        {
            if (Menu == null || !NotificationsEnabled) return; // gate lives in settings
            try { OnUi(() => _icon.ShowBalloonTip(3000, title, text, icon)); }
            catch { }
        }

        public void Dispose()
        {
            _flash.Dispose();
            _icon.Visible = false;
            _icon.Dispose();
            _normalIcon?.Dispose();
            _alertIcon?.Dispose();
        }

        // ---- internals --------------------------------------------------------------

        private static void ToggleFlash(object state)
        {
            var host = (TrayHost)state;
            host._flashOnAlert = !host._flashOnAlert;
            Icon next = host._flashOnAlert ? host._alertIcon : host._normalIcon;
            host.OnUi(() => host._icon.Icon = next);
        }

        private void OnMouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (!string.IsNullOrEmpty(_alertReason))
                Balloon("SlitherIn - problem", _alertReason, ToolTipIcon.Error);
            ShowMenu();
        }

        // Replicates the tray icon's own right-click menu opening (private
        // ShowContextMenu), so a left click produces the exact same positioned
        // menu — carried over from the old app.
        private void ShowMenu()
        {
            try
            {
                MethodInfo mi = typeof(NotifyIcon).GetMethod(
                    "ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);
                mi?.Invoke(_icon, null);
            }
            catch { }
        }

        /// <summary>External icon override next to the exe, else the resource
        /// embedded in this assembly (baked-in fallback, like the old app).</summary>
        private static Icon LoadIcon(string fileName, string resourceName)
        {
            try
            {
                string path = Path.Combine(Startup.ExeDir(), fileName);
                if (File.Exists(path)) return new Icon(path);
            }
            catch { }
            try
            {
                using var stream = typeof(TrayHost).Assembly.GetManifestResourceStream(resourceName);
                if (stream != null) return new Icon(stream);
            }
            catch { }
            return null;
        }

        /// <summary>Marshals UI mutations onto the message-pump thread. SetAlert
        /// typically arrives on the UI thread already; the flash timer fires on a
        /// pool thread.</summary>
        private void OnUi(Action action)
        {
            if (SynchronizationContext.Current == _ui) { action(); return; }
            _ui.Post(_ => action(), null);
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
            return s.Substring(0, max);
        }
    }
}