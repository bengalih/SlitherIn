using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SlitherIn.Core.Config
{
    /// <summary>
    /// Where config lives, resolved: the settings file path + the ordered list of
    /// profile directories + the folders the file watcher must monitor. Pure path
    /// math only (no I/O side effects), so it is directly unit-testable.
    ///
    /// Resolution rules (the successor of the retired %SLITHERIN_DIR% override):
    ///   1. The settings file is <c>--settings-file &lt;path&gt;</c> if given, else
    ///      <c>ExeDir\slitherin.settings.json</c>.
    ///   2. Profile dirs, in load/search order: the executable's folder ALWAYS,
    ///      then settings <c>profile_dirs</c> (relative entries resolve against the
    ///      settings file's folder), then <c>--profile-dir</c> overrides.
    ///   3. Watch roots: the settings folder + every profile dir.
    /// Duplicate directories collapse (first occurrence wins, OrdinalIgnoreCase).
    /// Nonexistent dirs are kept in the lists — the watcher skips them and the
    /// loader surfaces them as an alert line, so a typo in profile_dirs is visible.
    /// </summary>
    public sealed class ConfigSources
    {
        public string ExeDir { get; }
        public string CliSettingsFile { get; }
        public IReadOnlyList<string> CliProfileDirs { get; }

        /// <summary>Absolute path of the settings file this instance loads/saves.</summary>
        public string SettingsPath { get; }

        /// <summary>File name portion of <see cref="SettingsPath"/> (what profiles
        /// enumeration must exclude, and what the watcher matches).</summary>
        public string SettingsFileName { get; }

        /// <summary>Ordered, de-duplicated profile directories (exe dir first).</summary>
        public IReadOnlyList<string> ProfileDirs { get; }

        /// <summary>Ordered, de-duplicated folders the watcher monitors: the
        /// settings folder + every profile dir.</summary>
        public IReadOnlyList<string> WatchRoots { get; }

        private ConfigSources(string exeDir, string cliSettingsFile, IReadOnlyList<string> cliProfileDirs,
            string settingsPath, IReadOnlyList<string> profileDirs, IReadOnlyList<string> watchRoots)
        {
            ExeDir = exeDir;
            CliSettingsFile = cliSettingsFile;
            CliProfileDirs = cliProfileDirs;
            SettingsPath = settingsPath;
            SettingsFileName = Path.GetFileName(settingsPath);
            ProfileDirs = profileDirs;
            WatchRoots = watchRoots;
        }

        /// <summary>Resolve config locations from the app recipe + settings. The
        /// settings are optional at first resolution (boot guesses their location
        /// before they load); <see cref="Rebuild"/> re-resolves once they did.</summary>
        public static ConfigSources Resolve(string exeDir, string cliSettingsFile,
            IEnumerable<string> cliProfileDirs, Settings settings)
        {
            string exe = string.IsNullOrWhiteSpace(exeDir)
                ? AppDomain.CurrentDomain.BaseDirectory
                : exeDir;

            string settingsPath = !string.IsNullOrWhiteSpace(cliSettingsFile)
                ? Path.GetFullPath(cliSettingsFile)
                : Path.Combine(exe, Loader.SettingsFileName);
            string settingsDir = Path.GetDirectoryName(settingsPath);
            if (string.IsNullOrWhiteSpace(settingsDir)) settingsDir = exe;

            var rawDirs = new List<string> { exe };
            foreach (string d in settings?.ProfileDirs ?? new List<string>())
            {
                if (string.IsNullOrWhiteSpace(d)) continue; // also flagged by the validator
                rawDirs.Add(Path.IsPathRooted(d) ? d : Path.Combine(settingsDir, d));
            }
            foreach (string d in cliProfileDirs ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(d)) continue;
                rawDirs.Add(Path.GetFullPath(d));
            }

            List<string> profileDirs = Dedupe(rawDirs.Select(Path.GetFullPath));

            var rootCandidates = new List<string> { settingsDir };
            rootCandidates.AddRange(profileDirs);
            List<string> watchRoots = Dedupe(rootCandidates.Select(Path.GetFullPath));

            return new ConfigSources(exe, cliSettingsFile,
                cliProfileDirs == null ? new List<string>() : cliProfileDirs.ToList(),
                settingsPath, profileDirs, watchRoots);
        }

        /// <summary>Re-resolve after the settings object changes — their
        /// <c>profile_dirs</c> may add or remove profile folders. Called on boot (once
        /// real settings loaded) and on every reload.</summary>
        public ConfigSources Rebuild(Settings settings)
            => Resolve(ExeDir, CliSettingsFile, CliProfileDirs, settings);

        /// <summary>Find a profile file across the profile dirs (first dir that has
        /// it wins — the same rule as loading). Null when absent everywhere.</summary>
        public string ResolveProfilePath(string file)
        {
            if (string.IsNullOrWhiteSpace(file)) return null;
            foreach (string dir in ProfileDirs)
            {
                string candidate = Path.Combine(dir, file);
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        private static List<string> Dedupe(IEnumerable<string> paths)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string p in paths)
                if (seen.Add(p)) result.Add(p);
            return result;
        }
    }
}