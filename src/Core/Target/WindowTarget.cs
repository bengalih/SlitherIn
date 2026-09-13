using System;
using System.Text.RegularExpressions;
using SlitherIn.Core.Config;

namespace SlitherIn.Core.Target
{
    /// <summary>
    /// Matcher for a profile's window target (exe + title wildcards). Pure — no
    /// Win32. Semantics locked in design: match = exe AND title when both are
    /// given; exe-only is the single-instance default; a profile with no patterns
    /// can never match (never auto-selected).
    /// </summary>
    public sealed class WindowTarget
    {
        private readonly Regex _exe;
        private readonly Regex _title;

        public bool HasAny { get; }

        private WindowTarget(Regex exe, Regex title)
        {
            _exe = exe;
            _title = title;
            HasAny = exe != null || title != null;
        }

        /// <summary>Build from the config descriptor. Null/blank spec ↦ no target.</summary>
        public static WindowTarget FromSpec(WindowSpec spec)
        {
            if (spec == null) return new WindowTarget(null, null);
            Regex exe = string.IsNullOrWhiteSpace(spec.Exe) ? null : WildcardToRegex(spec.Exe);
            Regex title = string.IsNullOrWhiteSpace(spec.Title) ? null : WildcardToRegex(spec.Title);
            return new WindowTarget(exe, title);
        }

        public bool Matches(string exe, string title)
        {
            if (!HasAny) return false;
            if (_exe != null && (exe == null || !_exe.IsMatch(exe))) return false;
            if (_title != null && (title == null || !_title.IsMatch(title))) return false;
            return true;
        }

        /// <summary>Wildcard pattern ("dndclient64", "*-Sarlona") → anchored regex.</summary>
        private static Regex WildcardToRegex(string pattern)
        {
            string escaped = Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".");
            return new Regex("^" + escaped + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }
}