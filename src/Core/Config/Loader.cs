using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SlitherIn.Core.Config
{
    /// <summary>A load attempt: either the config or the details of why it failed
    /// (blocking validation errors included). Never thrown — the tray decides how
    /// to surface errors without crashing.</summary>
    public sealed class LoadOutcome<T>
    {
        public T Config { get; set; }
        public List<LoadError> Errors { get; } = new();
        public bool Succeeded => Config != null;
    }

    public sealed class LoadError
    {
        public string File { get; set; }
        public string Message { get; set; }

        public override string ToString() => Message;
    }

    /// <summary>A successfully-loaded profile plus the file it came from, so callers
    /// like the catalog can key per-file state (window targets, runtimes).</summary>
    public sealed class LoadedProfile
    {
        public string File { get; set; }
        public Profile Profile { get; set; }
    }

    /// <summary>
    /// Reads config files into <see cref="Models"/> using System.Text.Json with
    /// JSON comments skipped natively (no StripJsonComments pre-pass). Gates on
    /// `schema_version`, then runs <see cref="Validator"/>: a Settings/Profile
    /// with any Error-severity issue is NOT returned (Config = null) and the
    /// reasons land in <c>Errors</c>. Never throws into the UI.
    /// </summary>
    public static class Loader
    {
        public const string SettingsFileName = "slitherin.settings.json";
        public const string ProfileSearchPattern = "slitherin*.json";

        static readonly JsonSerializerOptions JsonOptions = new()
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            PropertyNameCaseInsensitive = false,
        };

        /// <summary>Load the global settings from a specific file. A MISSING file
        /// is not an error — it yields untouched defaults.</summary>
        public static LoadOutcome<Settings> LoadSettingsFile(string settingsPath)
        {
            var outcome = new LoadOutcome<Settings>();
            if (string.IsNullOrWhiteSpace(settingsPath) || !File.Exists(settingsPath))
            {
                outcome.Config = new Settings();
                return outcome;
            }

            Settings settings = Deserialize<Settings>(settingsPath, outcome);
            if (!outcome.Succeeded) return outcome;

            if (settings.SchemaVersion != Normalizer.SupportedSchemaVersion)
            {
                Fail(settingsPath, $"unsupported schema_version {settings.SchemaVersion} (supported: {Normalizer.SupportedSchemaVersion}).", outcome);
                return outcome;
            }

            ValidationResult v = Validator.Validate(settings);
            if (HasErrors(v))
            {
                Fail(settingsPath, Join(v), outcome);
                return outcome;
            }

            outcome.Config = settings;
            return outcome;
        }

        /// <summary>Loads EVERY profile across the profile dirs (for the catalog +
        /// startup validation). Returns the valid profiles + per-file load errors.
        /// The settings file itself is never treated as a profile; a dir that does
        /// not exist is skipped and reported as a non-blocking error. When the same
        /// file name exists in several dirs, the FIRST dir wins.</summary>
        public static LoadOutcome<List<Profile>> LoadProfiles(
            IEnumerable<string> dirs, string settingsFileName = SettingsFileName)
        {
            var detailed = LoadProfilesDetailed(dirs, settingsFileName);
            var outcome = new LoadOutcome<List<Profile>> { Config = new List<Profile>() };
            foreach (LoadedProfile entry in detailed.Config) outcome.Config.Add(entry.Profile);
            foreach (LoadError e in detailed.Errors) outcome.Errors.Add(e);
            return outcome;
        }

        /// <summary>Like <see cref="LoadProfiles"/> but keeps the per-file mapping
        /// (file name ⇒ profile), which the catalog needs for window targets and
        /// per-profile runtimes.</summary>
        public static LoadOutcome<List<LoadedProfile>> LoadProfilesDetailed(
            IEnumerable<string> dirs, string settingsFileName = SettingsFileName)
        {
            var outcome = new LoadOutcome<List<LoadedProfile>> { Config = new List<LoadedProfile>() };
            if (dirs == null) return outcome;

            string exclude = string.IsNullOrWhiteSpace(settingsFileName) ? SettingsFileName : settingsFileName;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string dir in dirs)
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                if (!Directory.Exists(dir))
                {
                    outcome.Errors.Add(new LoadError { File = dir, Message = "profile dir does not exist; skipped." });
                    continue;
                }

                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(dir, ProfileSearchPattern)
                        .Where(f => !Path.GetFileName(f).Equals(exclude, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(f => f, StringComparer.Ordinal);
                }
                catch (Exception ex)
                {
                    outcome.Errors.Add(new LoadError { File = dir, Message = "cannot enumerate config dir: " + ex.Message });
                    continue;
                }

                foreach (string file in files)
                {
                    string name = Path.GetFileName(file);
                    if (!seen.Add(name)) continue; // first dir wins

                    var one = ReadProfile(file);
                    if (one.Config != null)
                        outcome.Config.Add(new LoadedProfile { File = name, Profile = one.Config });
                    foreach (LoadError e in one.Errors) outcome.Errors.Add(e);
                }
            }
            return outcome;
        }

        private static LoadOutcome<Profile> ReadProfile(string filePath)
        {
            var outcome = new LoadOutcome<Profile>();
            Profile profile = Deserialize<Profile>(filePath, outcome);
            if (!outcome.Succeeded) return outcome;

            if (profile.SchemaVersion != Normalizer.SupportedSchemaVersion)
            {
                Fail(filePath, $"unsupported schema_version {profile.SchemaVersion} (supported: {Normalizer.SupportedSchemaVersion}).", outcome);
                return outcome;
            }

            ValidationResult v = Validator.Validate(profile);
            if (HasErrors(v))
            {
                Fail(filePath, Join(v), outcome);
                return outcome;
            }

            outcome.Config = profile;
            return outcome;
        }

        /// <summary>Write the settings file back (tray toggles, active_profile/profile_mode
        /// persistence). Returns the written path.</summary>
        public static string SaveSettings(string settingsPath, Settings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (string.IsNullOrWhiteSpace(settingsPath))
                throw new ArgumentNullException(nameof(settingsPath));
            File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings, JsonOptions));
            return settingsPath;
        }

        private static T Deserialize<T>(string filePath, LoadOutcome<T> outcome) where T : class
        {
            try
            {
                string json = File.ReadAllText(filePath);
                T result = JsonSerializer.Deserialize<T>(json, JsonOptions);
                if (result == null)
                {
                    Fail(filePath, "document contains no data", outcome);
                    return null;
                }
                outcome.Config = result;
                return result;
            }
            catch (JsonException ex)
            {
                Fail(filePath, "JSON parse error: " + ex.Message, outcome);
                return null;
            }
            catch (Exception ex)
            {
                Fail(filePath, "cannot read file: " + ex.Message, outcome);
                return null;
            }
        }

        private static bool HasErrors(ValidationResult v)
            => v.Issues.Exists(i => i.Severity == ValidationSeverity.Error);

        private static string Join(ValidationResult v)
            => string.Join("; ", v.Issues);

        private static void Fail<T>(string file, string message, LoadOutcome<T> outcome)
            => AddError(outcome, file, message);

        private static void AddError<T>(LoadOutcome<T> outcome, string file, string message)
        {
            outcome.Config = default;
            outcome.Errors.Add(new LoadError { File = file, Message = message });
        }
    }
}