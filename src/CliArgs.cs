using System;
using System.Collections.Generic;

namespace SlitherIn
{
    /// <summary>
    /// Parsed command line. `--settings-file &lt;path&gt;` overrides the settings file
    /// location; repeatable `--profile-dir &lt;path&gt;` appends extra profile folders
    /// after the settings-derived set. `--key=value` is accepted as well. Unknown
    /// switches are ignored (the launcher stays forgiving). Parsing is pure string
    /// work so the tests cover it directly.
    /// </summary>
    internal sealed class CliArgs
    {
        public string SettingsFile { get; }
        public IReadOnlyList<string> ProfileDirs { get; }

        public CliArgs(string settingsFile, IEnumerable<string> profileDirs)
        {
            SettingsFile = settingsFile;
            ProfileDirs = profileDirs == null ? new List<string>() : new List<string>(profileDirs);
        }

        public static CliArgs Parse(string[] args)
        {
            string settingsFile = null;
            var profileDirs = new List<string>();
            string[] argv = args ?? Array.Empty<string>();

            for (int i = 0; i < argv.Length; i++)
            {
                string token = argv[i] ?? string.Empty;
                if (string.IsNullOrEmpty(token)) continue;

                string key, value;
                int eq = token.IndexOf('=');
                if (eq > 0)
                {
                    key = token.Substring(0, eq);
                    value = token.Substring(eq + 1);
                }
                else if (i + 1 < argv.Length && !argv[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    key = token;
                    value = argv[++i];
                }
                else
                {
                    continue; // flag with no value (or a bare token) — ignored
                }

                Consume(key, value, ref settingsFile, profileDirs);
            }

            return new CliArgs(settingsFile, profileDirs);
        }

        private static void Consume(string key, string value, ref string settingsFile, List<string> profileDirs)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            switch (key)
            {
                case "--settings-file":
                    settingsFile = value;
                    break;
                case "--profile-dir":
                    profileDirs.Add(value);
                    break;
            }
        }
    }
}