using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace SlitherIn.Core.Config
{
    public enum ValidationSeverity { Error, Warning }

    /// <summary>One schema/structural finding. Never thrown at the UI — collected
    /// and surfaced through the tray (alert icon + row + balloon) / log.</summary>
    public sealed class ValidationIssue
    {
        public ValidationSeverity Severity { get; set; }
        public string Field { get; set; }
        public string Message { get; set; }

        public override string ToString() => $"{Severity}: {Field}: {Message}";
    }

    public sealed class ValidationResult
    {
        public List<ValidationIssue> Issues { get; } = new();
        public bool IsValid => Issues.TrueForAll(i => i.Severity != ValidationSeverity.Error);
    }

    /// <summary>
    /// Schema and cross-profile validation. Runs at load over every profile (so a
    /// broken non-active profile is caught at launch). NEVER throws — it collects
    /// <see cref="ValidationIssue"/>s. Cross-profile ambiguous-target check is
    /// added by <see cref="ValidateAll"/> (milestone 4).
    /// </summary>
    public static class Validator
    {
        private static readonly HashSet<string> KnownStepProperties = new(StringComparer.Ordinal)
        {
            "keys", "button", "sound", "wait_ms", "cooldown_ms", "hold_for_ms", "wrapper",
        };
        private static readonly string[] EngineNames = { "sendinput", "viiper" };

        public static ValidationResult Validate(Settings settings)
        {
            var r = new ValidationResult();
            if (settings == null) return r;

            if (settings.SchemaVersion != Normalizer.SupportedSchemaVersion)
                Error(r, "schema_version", $"unsupported schema_version {settings.SchemaVersion} (supported: {Normalizer.SupportedSchemaVersion}).");
            if (!Normalizer.IsSupportedEngine(settings.InputEngine))
                Error(r, "input_engine", $"unknown input_engine '{settings.InputEngine}' (must be \"sendinput\" or \"viiper\").");
            if (settings.ProfileMode != "manual" && settings.ProfileMode != "auto")
                Error(r, "profile_mode", $"unknown profile_mode '{settings.ProfileMode}' (must be \"manual\" or \"auto\").");
            if (settings.Log != "off" && settings.Log != "on" && settings.Log != "debug")
                Error(r, "log", $"unknown log level '{settings.Log}' (must be off|on|debug).");
            for (int i = 0; settings.ProfileDirs != null && i < settings.ProfileDirs.Count; i++)
                if (string.IsNullOrWhiteSpace(settings.ProfileDirs[i]))
                    Warning(r, $"profile_dirs[{i}]", "profile dir entry is empty (ignored).");
            return r;
        }

        public static ValidationResult Validate(Profile profile)
        {
            var r = new ValidationResult();
            if (profile == null) return r;

            if (profile.SchemaVersion != Normalizer.SupportedSchemaVersion)
                Error(r, "schema_version", $"unsupported schema_version {profile.SchemaVersion} (supported: {Normalizer.SupportedSchemaVersion}).");
            if (string.IsNullOrWhiteSpace(profile.Name))
                Warning(r, "name", "profile has no name (tray menu shows the filename instead).");
            if (profile.Macros == null || profile.Macros.Count == 0)
                Warning(r, "macros", "profile has no macros.");

            for (int i = 0; profile.Macros != null && i < profile.Macros.Count; i++)
                ValidateMacro(profile, profile.Macros[i], i, r);

            return r;
        }

        /// <summary>Cross-profile checks (e.g. ambiguous window targets). Runs at startup
        /// over every loaded profile. Ambiguity is a WARNING, never a blocker:
        /// in AUTO mode two profiles whose window targets overlap cannot be chosen
        /// deterministically, and the user must see that in --validate / the tray.
        /// A profile with no window target can never be auto-selected, so it is
        /// exempt.</summary>
        public static ValidationResult ValidateAll(IEnumerable<Profile> profiles)
        {
            var r = new ValidationResult();
            var list = profiles == null ? new List<Profile>() : profiles.ToList();

            for (int i = 0; i < list.Count; i++)
            {
                WindowSpec a = list[i].Window;
                if (a == null || !HasPattern(a.Exe, a.Title)) continue;

                for (int j = i + 1; j < list.Count; j++)
                {
                    WindowSpec b = list[j].Window;
                    if (b == null || !HasPattern(b.Exe, b.Title)) continue;

                    if (!TargetsOverlap(a, b)) continue;

                    Warning(r, "window.target",
                        $"profiles '{NameOf(list[i], i)}' and '{NameOf(list[j], j)}' have overlapping window targets — AUTO mode cannot choose between them deterministically.");
                }
            }
            return r;
        }

        /// <summary>Do the two targets admit a shared foreground window? Two
        /// constraints separate them only when BOTH sides carry the same field with
        /// DIFFERENT patterns (a shared exe with different titles, or vice versa,
        /// still can coincide on a window that satisfies both).</summary>
        private static bool TargetsOverlap(WindowSpec a, WindowSpec b)
        {
            bool aExe = !string.IsNullOrWhiteSpace(a.Exe);
            bool bExe = !string.IsNullOrWhiteSpace(b.Exe);
            bool aTitle = !string.IsNullOrWhiteSpace(a.Title);
            bool bTitle = !string.IsNullOrWhiteSpace(b.Title);

            if (aExe && bExe && !string.Equals(a.Exe, b.Exe, StringComparison.OrdinalIgnoreCase)) return false;
            if (aTitle && bTitle && !string.Equals(a.Title, b.Title, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        private static bool HasPattern(string exe, string title)
            => !string.IsNullOrWhiteSpace(exe) || !string.IsNullOrWhiteSpace(title);

        private static string NameOf(Profile p, int index)
            => string.IsNullOrWhiteSpace(p.Name) ? "<unnamed profile " + (index + 1) + ">" : p.Name;

        private static void ValidateMacro(Profile profile, Macro m, int index, ValidationResult r)
        {
            string field = $"macros[{index}]";

            if (string.IsNullOrWhiteSpace(m.Name))
                Warning(r, field + ".name", "macro has no name.");

            if (string.IsNullOrWhiteSpace(m.Trigger))
            {
                Error(r, field + ".trigger", "macro has no trigger.");
            }
            else if (!KeyName.TryParse(m.Trigger, out _))
            {
                Error(r, field + ".trigger", $"cannot parse trigger '{m.Trigger}'.");
            }

            if (m.Toggle != null && !KeyName.TryParse(m.Toggle, out _))
                Error(r, field + ".toggle", $"cannot parse toggle '{m.Toggle}'.");
            if (m.AbortKeys != null && !KeyName.TryParse(m.AbortKeys, out _))
                Error(r, field + ".abort_keys", $"cannot parse abort_keys '{m.AbortKeys}'.");

            if (m.Steps == null || m.Steps.Count == 0)
                Error(r, field + ".steps", "macro has no steps.");

            if (m.Loop != null && m.Loop.Count.HasValue && m.Loop.Count.Value < -1)
                Error(r, field + ".loop.count", $"loop count must be -1 (forever) or >= 1, got {m.Loop.Count.Value}.");
            else if (m.Loop != null && m.Loop.Count == 0)
                Warning(r, field + ".loop.count", "loop count 0 fires nothing.");

            if (m.Schedule != null && m.Schedule.Mode != "fixed" && m.Schedule.Mode != "cooldown")
                Error(r, field + ".schedule.mode", $"unknown schedule.mode '{m.Schedule.Mode}' (must be fixed|cooldown).");

            for (int j = 0; m.Steps != null && j < m.Steps.Count; j++)
                ValidateStep(profile, m.Steps[j], field + ".steps[" + j + "]", r);
        }

        private static void ValidateStep(Profile profile, JsonElement el, string field, ValidationResult r)
        {
            if (el.ValueKind != JsonValueKind.Object)
            {
                Error(r, field, "step must be a JSON object.");
                return;
            }

            foreach (JsonProperty prop in el.EnumerateObject())
                if (!KnownStepProperties.Contains(prop.Name))
                    Warning(r, field + "." + prop.Name, $"unknown step property '{prop.Name}' (ignored).");

            bool hasKeys = el.TryGetProperty("keys", out JsonElement keys);
            bool hasButton = el.TryGetProperty("button", out JsonElement button);
            bool hasSound = el.TryGetProperty("sound", out JsonElement sound);

            if (hasKeys && hasButton)
                Error(r, field, "step cannot have both 'keys' and 'button'.");
            if (hasSound && (hasKeys || hasButton))
                Error(r, field, "'sound' is the only action a sound step may carry.");

            if (hasKeys)
                ValidateKeys(keys, field, r);
            else if (hasButton)
            {
                if (button.ValueKind != JsonValueKind.String || !KeyName.TryParse(button.GetString(), out _))
                    Error(r, field + ".button", "'button' must be a valid mouse-button chord.");
            }
            else if (hasSound)
            {
                if (sound.ValueKind != JsonValueKind.Object)
                    Error(r, field + ".sound", "'sound' must be an object.");
            }
            else if (!el.TryGetProperty("wait_ms", out _))
            {
                Error(r, field, "step must carry one of keys/button/sound/wait_ms.");
            }

            ValidateNonNegativeInt(el, "wait_ms", field, r);
            ValidateNonNegativeInt(el, "cooldown_ms", field, r);
            ValidateNonNegativeInt(el, "hold_for_ms", field, r);

            if (IsViiperProfile(profile))
                ValidateNoMouseOutput(el, field, r);

            int? holdMs = TryInt(el, "hold_for_ms");
            if (holdMs.HasValue)
            {
                int chordCount = 0;
                if (hasKeys)
                {
                    if (keys.ValueKind == JsonValueKind.String) chordCount = 1;
                    else if (keys.ValueKind == JsonValueKind.Array) chordCount = CountStrings(keys);
                }
                if (hasButton) chordCount = 1;
                if (chordCount != 1)
                    Warning(r, field + ".hold_for_ms", "hold_for_ms applies to single-chord steps (holds the last chord) — odd on this step.");
            }

            if (el.TryGetProperty("wrapper", out JsonElement wrapper))
            {
                if (wrapper.ValueKind != JsonValueKind.String)
                    Error(r, field + ".wrapper", "wrapper name must be a string.");
                else if (profile.Wrappers == null || !profile.Wrappers.ContainsKey(wrapper.GetString()))
                    Error(r, field + ".wrapper", $"references undefined wrapper '{wrapper.GetString()}'.");
                else
                    ValidateWrapperContents(profile, profile.Wrappers[wrapper.GetString()], field + ".wrapper", r);
            }
        }

        /// <summary>The VIIPER backend is a HID KEYBOARD device: mouse-button chords
        /// cannot be emitted (there is no mouse report). A profile that declares
        /// `viiper` and carries a 'button' step or a mouse-key chord is a hard error
        /// — the press would silently vanish at injection time. (Only an explicit
        /// per-profile override is checked here; a global viiper default + mouse
        /// steps is caught when the engine drops the chord — see ViiperEngine.)</summary>
        private static void ValidateNoMouseOutput(JsonElement el, string field, ValidationResult r)
        {
            if (el.TryGetProperty("button", out JsonElement button) && button.ValueKind == JsonValueKind.String)
            {
                Error(r, field + ".button", "'viiper' cannot emit mouse buttons.");
                return;
            }
            foreach (string chord in ChordStrings(el, "keys"))
                if (KeyName.TryParse(chord, out Chord c) && KeyName.IsMouseToken(c.Key))
                {
                    Error(r, field + ".keys", $"'viiper' cannot emit mouse button '{chord}'.");
                    return;
                }
        }

        private static IEnumerable<string> ChordStrings(JsonElement el, string prop)
        {
            if (!el.TryGetProperty(prop, out JsonElement keys)) yield break;
            if (keys.ValueKind == JsonValueKind.String) yield return keys.GetString();
            else if (keys.ValueKind == JsonValueKind.Array)
                foreach (JsonElement item in keys.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String) yield return item.GetString();
        }

        private static bool IsViiperProfile(Profile p)
            => string.Equals(p.InputEngine, "viiper", StringComparison.OrdinalIgnoreCase);

        private static void ValidateKeys(JsonElement keys, string field, ValidationResult r)
        {
            if (keys.ValueKind == JsonValueKind.String)
            {
                if (!KeyName.TryParse(keys.GetString(), out _))
                    Error(r, field + ".keys", $"cannot parse key '{keys.GetString()}'.");
                return;
            }
            if (keys.ValueKind == JsonValueKind.Array)
            {
                int n = 0;
                foreach (JsonElement item in keys.EnumerateArray())
                {
                    n++;
                    if (item.ValueKind != JsonValueKind.String)
                        Error(r, field + ".keys", "every entry of a keys list must be a chord string.");
                    else if (!KeyName.TryParse(item.GetString(), out _))
                        Error(r, field + ".keys", $"cannot parse key '{item.GetString()}'.");
                }
                if (n == 0)
                    Error(r, field + ".keys", "a keys list must not be empty.");
                return;
            }
            Error(r, field + ".keys", "'keys' must be a chord string or a list of chord strings.");
        }

        private static void ValidateWrapperContents(Profile profile, Wrapper w, string field, ValidationResult r)
        {
            foreach (JsonElement s in w.Before ?? new List<JsonElement>())
                if (s.ValueKind == JsonValueKind.Object && s.TryGetProperty("wrapper", out _))
                    Error(r, field, "wrapper stacking is not allowed.");
            foreach (JsonElement s in w.After ?? new List<JsonElement>())
                if (s.ValueKind == JsonValueKind.Object && s.TryGetProperty("wrapper", out _))
                    Error(r, field, "wrapper stacking is not allowed.");

            if (IsViiperProfile(profile))
            {
                foreach (JsonElement s in w.Before ?? new List<JsonElement>())
                    if (s.ValueKind == JsonValueKind.Object) ValidateNoMouseOutput(s, field + ".before", r);
                foreach (JsonElement s in w.After ?? new List<JsonElement>())
                    if (s.ValueKind == JsonValueKind.Object) ValidateNoMouseOutput(s, field + ".after", r);
            }
        }

        private static int? TryInt(JsonElement el, string prop)
        {
            return el.TryGetProperty(prop, out JsonElement n)
                && n.ValueKind == JsonValueKind.Number
                && n.TryGetInt32(out int value)
                ? value
                : (int?)null;
        }

        private static int CountStrings(JsonElement arr)
        {
            int n = 0;
            foreach (JsonElement item in arr.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String) n++;
            return n;
        }

        private static void ValidateNonNegativeInt(JsonElement el, string prop, string field, ValidationResult r)
        {
            if (!el.TryGetProperty(prop, out JsonElement n)) return;
            if (n.ValueKind != JsonValueKind.Number || !n.TryGetInt32(out int value))
                Error(r, field + "." + prop, $"'{prop}' must be an integer.");
            else if (value < 0)
                Error(r, field + "." + prop, $"'{prop}' must be >= 0.");
        }

        private static void Error(ValidationResult r, string field, string message)
            => r.Issues.Add(new ValidationIssue { Severity = ValidationSeverity.Error, Field = field, Message = message });

        private static void Warning(ValidationResult r, string field, string message)
            => r.Issues.Add(new ValidationIssue { Severity = ValidationSeverity.Warning, Field = field, Message = message });
    }
}