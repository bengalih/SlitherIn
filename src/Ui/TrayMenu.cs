using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using SlitherIn.Core.Config;
using SlitherIn.Core.Target;

namespace SlitherIn.Ui
{
    /// <summary>
    /// Builds the context menu model on every refresh, per the locked tray-UX
    /// continuity list:
    ///   Profile row       — CLICK opens the active profile JSON.
    ///   "Profiles" row    — the switch submenu: "Auto (switch by window)" entry
    ///                       (checked when AUTO is on) + one entry per profile file
    ///                       (checked when active).
    ///   "Macros:" header  — blue, like the old app.
    ///   Macro rows        — "• name: trigger" display rows (blue name); tooltip
    ///                       carries the step count.
    ///   "Abort: &lt;combo&gt;" row — red "Abort", stays.
    ///   Window-check / Notifications / run-at-startup — toggles; state re-shown
    ///                       through the row label (tray-only v1 UX).
    ///   "Open Settings (&lt;settings file name&gt;)" row (the actual name — it can be
    ///                       overridden via --settings-file).
    ///   Green clickable version (releases page), Exit.
    ///   Manual "Reload configuration" row: intentionally ABSENT — the watcher
    ///                       (debounce + artifact filter + SuppressFor) is the only
    ///                       reload trigger (C-1).
    ///   ALERT: &lt;msg&gt; rows (warning glyph) on load errors / VIIPER hard-fail.
    ///   Idler menu: absent (concept dropped).
    /// A row's TOGGLE actions mutate settings via <see cref="MenuActions"/> and the
    /// App rebuilds the menu from the new state — this class only renders.
    /// </summary>
    public sealed class TrayMenu
    {
        public ContextMenuStrip Build(
            ProfileCatalog catalog,
            Settings settings,
            IReadOnlyList<string> alertLines,
            MenuActions actions,
            string settingsFileName = Loader.SettingsFileName)
        {
            var menu = new ContextMenuStrip { Renderer = new TwoToneRenderer() };

            if (alertLines != null && alertLines.Count > 0)
            {
                foreach (string line in alertLines)
                    menu.Items.Add(Row("ALERT: " + line, null, SystemIcons.Warning.ToBitmap(), enabled: false));
                menu.Items.Add(new ToolStripSeparator());
            }

            // -- Profile row: click opens the active profile JSON. -------------------
            menu.Items.Add(ProfileRow(catalog, settings, actions));

            // -- Switch submenu: Auto + per-file switch. -----------------------------
            var switchMenu = new ToolStripMenuItem("Profiles");
            var auto = new ToolStripMenuItem("Auto (switch by window)");
            auto.Checked = catalog.Mode == ProfileCatalog.Auto;
            auto.Click += (_, __) => actions.SetMode?.Invoke(ProfileCatalog.Auto);
            switchMenu.DropDownItems.Add(auto);
            switchMenu.DropDownItems.Add(new ToolStripSeparator());
            foreach (string file in catalog.ProfileFiles.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var item = new ToolStripMenuItem(file);
                item.Checked = string.Equals(file, catalog.ActiveFile, StringComparison.OrdinalIgnoreCase);
                item.Click += (_, __) => actions.SwitchProfile?.Invoke(file);
                switchMenu.DropDownItems.Add(item);
            }
            menu.Items.Add(switchMenu);

            // -- "Macros:" blue header + trigger-only display rows. -------------------
            menu.Items.Add(Row(catalog.ActiveWorkflows.Count == 0 ? "Macros: (none)" : "Macros:", null));
            foreach (var wf in catalog.ActiveWorkflows)
            {
                string trigger = wf.Definition.Trigger.ToString();
                string name = wf.Definition.Name;
                string label = string.IsNullOrWhiteSpace(name)
                    ? "• " + trigger
                    : $"• {name}: {trigger}";
                var row = Row(label, null, enabled: true);
                row.ToolTipText = string.IsNullOrWhiteSpace(name)
                    ? trigger
                    : $"{name}: {trigger} ({wf.Definition.Steps.Count} steps)";
                menu.Items.Add(row);
            }

            // -- Abort combo row (display-only, red "Abort"). --------------------------
            menu.Items.Add(Row("Abort: " + (settings.Abort ?? "(none)"), null));

            // -- Toggles; the label re-shows state through the row text. ----------------
            menu.Items.Add(ToggleRow("Window-check", settings.WindowCheck, actions.ToggleWindowCheck));
            menu.Items.Add(ToggleRow("Notifications", settings.Notifications?.Enabled ?? false, actions.ToggleNotifications));
            menu.Items.Add(ToggleRow("Run at startup", Startup.RunAtStartup, actions.ToggleRunAtStartup));

            menu.Items.Add(Row("Open Settings (" + settingsFileName + ")", actions.OpenSettingsJson));

            menu.Items.Add(Row("SlitherIn " + Program.BuildRevision, actions.OpenReleasesPage));
            menu.Items.Add(Row("Exit", actions.Exit));

            return menu;
        }

        private static ToolStripMenuItem ProfileRow(ProfileCatalog catalog, Settings settings, MenuActions actions)
        {
            string label = ActiveLabel(catalog) ?? "(no profile)";
            var row = new ToolStripMenuItem("Profile: " + label);
            row.Image = SystemIcons.Information.ToBitmap();
            row.Click += (_, __) => actions.OpenActiveProfileJson?.Invoke();
            return row;
        }

        private static string ActiveLabel(ProfileCatalog catalog)
        {
            if (catalog.ActiveProfile == null) return null;
            return string.IsNullOrWhiteSpace(catalog.ActiveProfile.Name)
                ? catalog.ActiveFile
                : catalog.ActiveProfile.Name;
        }

        private static ToolStripMenuItem ToggleRow(string label, bool state, Func<bool> toggle)
        {
            var row = new ToolStripMenuItem($"{label}: {(state ? "On" : "Off")}");
            row.Click += (_, __) => toggle?.Invoke();
            return row;
        }

        private static ToolStripMenuItem Row(string label, Action action, Image image = null, bool enabled = true)
        {
            var item = new ToolStripMenuItem(label) { Enabled = enabled, Image = image };
            if (action != null) item.Click += (_, __) => action();
            return item;
        }

        // Colors only part of a menu label, exactly like the old app: the macro
        // name (blue) in "• Name: combo" rows, the "Macros:" header (blue), the
        // word "Abort" (red), and the green version row. Disabled rows draw fully
        // gray; plain rows keep the default color.
        private sealed class TwoToneRenderer : ToolStripProfessionalRenderer
        {
            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                string t = e.Text;
                if (string.IsNullOrEmpty(t))
                {
                    base.OnRenderItemText(e);
                    return;
                }

                if (t.StartsWith("Macros:", StringComparison.Ordinal))
                {
                    DrawSegments(e, t, new[] { t.Length }, new[] { Color.Blue });
                    return;
                }
                if (t.StartsWith("• ", StringComparison.Ordinal))
                {
                    int colon = t.IndexOf(':');
                    if (colon > 0)
                    {
                        // Disabled rows draw fully gray; enabled rows color only
                        // the macro name blue (the trigger keeps the default color).
                        if (e.Item.Enabled)
                            DrawSegments(e, t, new[] { colon }, new[] { Color.Blue });
                        else
                            DrawSegments(e, t, new[] { t.Length }, new[] { SystemColors.GrayText });
                        return;
                    }
                }
                else if (t.StartsWith("Abort", StringComparison.Ordinal))
                {
                    int n = t.Length;
                    int colon = t.IndexOf(':');
                    if (colon >= 0) n = colon;
                    DrawSegments(e, t, new[] { n }, new[] { Color.Red });
                    return;
                }
                else if (t.StartsWith("SlitherIn", StringComparison.Ordinal))
                {
                    DrawSegments(e, t, new[] { t.Length }, new[] { Color.Green });
                    return;
                }
                base.OnRenderItemText(e);
            }

            private static void DrawSegments(ToolStripItemTextRenderEventArgs e, string text, int[] ends, Color[] colors)
            {
                const TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.NoPrefix |
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
                Font f = e.TextFont;
                int x = e.TextRectangle.Left;
                int start = 0;
                for (int i = 0; i < ends.Length; i++)
                {
                    string seg = text.Substring(start, ends[i] - start);
                    Size sz = TextRenderer.MeasureText(e.Graphics, seg, f,
                        new Size(int.MaxValue, int.MaxValue), flags);
                    TextRenderer.DrawText(e.Graphics, seg, f,
                        new Rectangle(x, e.TextRectangle.Y, sz.Width, e.TextRectangle.Height),
                        colors[i], flags);
                    x += sz.Width;
                    start = ends[i];
                }
                if (start < text.Length)
                {
                    string seg = text.Substring(start);
                    TextRenderer.DrawText(e.Graphics, seg, f,
                        new Rectangle(x, e.TextRectangle.Y, Math.Max(0, e.TextRectangle.Right - x), e.TextRectangle.Height),
                        e.TextColor, flags);
                }
            }
        }
    }
}