using System;
using System.Collections.Generic;
using System.Text.Json;

namespace SlitherIn.Core.Config
{
    /// <summary>Engine-side, pre-parsed profile: every chord already canonicalized,
    /// wrappers attached, pacing defaults computed. Nothing in here re-parses
    /// strings. The engine (Workflow/Pacing) consumes exactly this.</summary>
    public sealed class NormalizedProfile
    {
        public string Name { get; set; }
        public WindowSpec Window { get; set; }
        public bool HasWindowTarget { get; set; }
        /// <summary>Resolved engine: profile override ?? settings ?? "sendinput".</summary>
        public string InputEngine { get; set; }
        /// <summary>Resolved fixed-mode pacing default: profile ?? settings ?? 3000.</summary>
        public int DefaultDelayMs { get; set; }
        public List<NormalizedMacro> Macros { get; set; } = new();
    }

    public sealed class NormalizedMacro
    {
        public string Name { get; set; }
        public Chord Trigger { get; set; }
        public int? FireAfterMs { get; set; }
        public Chord? Toggle { get; set; }
        public ToggleSound ToggleSound { get; set; }
        public Chord? AbortKeys { get; set; }
        public bool IgnoreAbort { get; set; }
        public LoopSpec Loop { get; set; }
        public Schedule Schedule { get; set; }
        /// <summary>Resolved fixed-pacing default for THIS macro's profile
        /// (profile ?? settings ?? 3000). The Dispatcher paces via
        /// <c>DefaultDelayMs ?? its own default</c>, so one dispatcher serves every
        /// profile regardless of switch order.</summary>
        public int? DefaultDelayMs { get; set; }
        public List<NormalizedStep> Steps { get; set; } = new();
    }

    /// <summary>One step = ONE logical action. A config `{ "keys": [...] }` list
    /// is a chord SEQUENCE fired within the step (no pacing inside); pacing
    /// applies BETWEEN steps. Sound/wait steps carry no chords.</summary>
    public sealed class NormalizedStep
    {
        public IReadOnlyList<Chord> Chords { get; set; } = Array.Empty<Chord>();
        public bool IsSound { get; set; }
        public SoundSpec Sound { get; set; }
        /// <summary>Keep the (single) chord held this long before release.</summary>
        public int? HoldForMs { get; set; }
        /// <summary>Step-level wait override after this step (0 = force none).</summary>
        public int? WaitMs { get; set; }
        /// <summary>Cooldown-mode candidacy (ms until this step can fire again).</summary>
        public int? CooldownMs { get; set; }
    }

    /// <summary>Thrown when a config cannot be turned into an engine model (invalid
    /// chord, undefined wrapper, stacked wrapper, bad hold_for_ms). Loader/App catch
    /// it; Validator is the non-throwing reporter.</summary>
    public sealed class NormalizeException : Exception
    {
        public NormalizeException(string message) : base(message) { }
    }

    /// <summary>
    /// One-pass canonicalization of a Profile into a <see cref="NormalizedProfile"/>:
    /// parse every chord via <see cref="KeyName"/>, classify steps by shape
    /// (keys/button/sound/wait_ms), attach wrappers (before+after, no stacking),
    /// fold pacing defaults. Pure and unit-testable — no IO, no UI. Strict from
    /// here: anything that cannot be built throws; the non-throwing reporter is
    /// <see cref="Validator"/>.
    /// </summary>
    public static class Normalizer
    {
        public const string DefaultInputEngine = "sendinput";
        public const int DefaultDelayMs = 3000;
        public const int SupportedSchemaVersion = 1;

        public static NormalizedProfile Normalize(Profile profile, Settings settings)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));

            string inputEngine = profile.InputEngine ?? settings?.InputEngine ?? DefaultInputEngine;
            EnsureSupportedEngine(inputEngine);

            int resolvedDefault = profile.DefaultDelayMs ?? settings?.DefaultDelayMs ?? DefaultDelayMs;

            var result = new NormalizedProfile
            {
                Name = profile.Name,
                Window = profile.Window,
                HasWindowTarget = profile.Window != null &&
                    (!string.IsNullOrWhiteSpace(profile.Window.Exe) ||
                     !string.IsNullOrWhiteSpace(profile.Window.Title)),
                InputEngine = inputEngine,
                DefaultDelayMs = resolvedDefault,
            };

            var wrappers = profile.Wrappers;
            foreach (var macro in profile.Macros ?? new List<Macro>())
                result.Macros.Add(NormalizeMacro(macro, wrappers, resolvedDefault));

            return result;
        }

        public static bool IsSupportedEngine(string value)
            => value == DefaultInputEngine || value == "viiper";

        private static void EnsureSupportedEngine(string value)
        {
            if (!IsSupportedEngine(value))
                throw new NormalizeException($"Unknown input_engine '{value}' (must be \"sendinput\" or \"viiper\").");
        }

        private static NormalizedMacro NormalizeMacro(Macro macro, Dictionary<string, Wrapper> wrappers, int resolvedDefaultDelay)
        {
            if (string.IsNullOrWhiteSpace(macro.Trigger))
                throw new NormalizeException($"Macro '{macro.Name}' has no trigger.");

            var result = new NormalizedMacro
            {
                Name = macro.Name,
                Trigger = KeyName.Parse(macro.Trigger),
                FireAfterMs = macro.FireAfterMs,
                Toggle = ParseOptionalChord(macro.Toggle, $"Macro '{macro.Name}' toggle"),
                ToggleSound = macro.ToggleSound,
                AbortKeys = ParseOptionalChord(macro.AbortKeys, $"Macro '{macro.Name}' abort_keys"),
                IgnoreAbort = macro.IgnoreAbort,
                Loop = macro.Loop,
                Schedule = macro.Schedule ?? new Schedule(),
                DefaultDelayMs = resolvedDefaultDelay,
            };

            foreach (var stepEl in macro.Steps ?? new List<JsonElement>())
                AppendStep(stepEl, wrappers, result.Steps, allowWrapper: true, macroName: macro.Name);

            return result;
        }

        private static Chord? ParseOptionalChord(string spec, string context)
        {
            if (string.IsNullOrWhiteSpace(spec)) return null;
            return KeyName.Parse(spec);
        }

        private static void AppendStep(JsonElement el, Dictionary<string, Wrapper> wrappers,
            List<NormalizedStep> output, bool allowWrapper, string macroName)
        {
            if (el.ValueKind != JsonValueKind.Object)
                throw new NormalizeException($"Macro '{macroName}': step must be a JSON object.");

            string wrapperName = null;
            if (el.TryGetProperty("wrapper", out JsonElement w))
            {
                if (w.ValueKind != JsonValueKind.String) throw Wrapped(macroName, "wrapper name must be a string.");
                wrapperName = w.GetString();
            }

            if (wrapperName == null)
            {
                output.Add(BuildStep(el, macroName));
                return;
            }

            if (!allowWrapper)
                throw Wrapped(macroName, "wrapper stacking is not allowed.");

            if (wrappers == null || !wrappers.TryGetValue(wrapperName, out Wrapper wrapper))
                throw Wrapped(macroName, $"references undefined wrapper '{wrapperName}'.");

            foreach (var before in wrapper.Before ?? new List<JsonElement>())
                AppendStep(before, null, output, allowWrapper: false, macroName: macroName);
            output.Add(BuildStep(el, macroName));
            foreach (var after in wrapper.After ?? new List<JsonElement>())
                AppendStep(after, null, output, allowWrapper: false, macroName: macroName);

            static NormalizeException Wrapped(string macro, string msg) => new($"Macro '{macro}': {msg}.");
        }

        private static NormalizedStep BuildStep(JsonElement el, string macroName)
        {
            bool hasKeys = el.TryGetProperty("keys", out JsonElement keysEl);
            bool hasButton = el.TryGetProperty("button", out JsonElement btnEl);
            bool hasSound = el.TryGetProperty("sound", out JsonElement sndEl);

            if (hasKeys && hasButton)
                throw Wrapped(macroName, "step cannot have both 'keys' and 'button'.");
            if (hasSound && (hasKeys || hasButton))
                throw Wrapped(macroName, "'sound' is the only action a sound step may carry.");

            var step = new NormalizedStep();

            if (hasKeys)
            {
                step.Chords = ParseChordSequence(keysEl, macroName);
            }
            else if (hasButton)
            {
                if (btnEl.ValueKind != JsonValueKind.String)
                    throw Wrapped(macroName, "'button' must be a chord string (e.g. \"XButton1\").");
                step.Chords = new[] { KeyName.Parse(btnEl.GetString()) };
            }
            else if (hasSound)
            {
                step.IsSound = true;
                step.Sound = ParseSound(sndEl, macroName);
            }
            else if (!el.TryGetProperty("wait_ms", out _))
            {
                // Also not a pure wait step → unrecognized shape.
                throw Wrapped(macroName, "step must carry one of keys/button/sound/wait_ms.");
            }

            step.WaitMs = ReadOptionalInt(el, "wait_ms", macroName);
            step.CooldownMs = ReadOptionalInt(el, "cooldown_ms", macroName);
            step.HoldForMs = ReadOptionalInt(el, "hold_for_ms", macroName);

            if (step.HoldForMs.HasValue && step.Chords.Count != 1)
                throw Wrapped(macroName, "hold_for_ms requires a step with exactly one chord.");

            return step;

            static NormalizeException Wrapped(string macro, string msg) => new($"Macro '{macro}': {msg}.");
        }

        private static IReadOnlyList<Chord> ParseChordSequence(JsonElement keysEl, string macroName)
        {
            if (keysEl.ValueKind == JsonValueKind.String)
                return new[] { KeyName.Parse(keysEl.GetString()) };

            if (keysEl.ValueKind == JsonValueKind.Array)
            {
                var chords = new List<Chord>();
                foreach (JsonElement item in keysEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                        throw Wrapped(macroName, "every entry of a keys list must be a chord string.");
                    chords.Add(KeyName.Parse(item.GetString()));
                }
                if (chords.Count == 0)
                    throw Wrapped(macroName, "a keys list must not be empty.");
                return chords;
            }

            throw Wrapped(macroName, "'keys' must be a chord string or a list of chord strings.");

            static NormalizeException Wrapped(string macro, string msg) => new($"Macro '{macro}': {msg}.");
        }

        private static SoundSpec ParseSound(JsonElement sndEl, string macroName)
        {
            if (sndEl.ValueKind != JsonValueKind.Object)
                throw Wrapped(macroName, "'sound' must be an object ({} or frequency/duration_ms/file).");

            var spec = new SoundSpec
            {
                Frequency = ReadOptionalInt(sndEl, "frequency", macroName),
                DurationMs = ReadOptionalInt(sndEl, "duration_ms", macroName),
            };
            if (sndEl.TryGetProperty("file", out JsonElement file))
            {
                if (file.ValueKind != JsonValueKind.String)
                    throw Wrapped(macroName, "'sound.file' must be a string.");
                spec.File = file.GetString();
            }
            return spec;

            static NormalizeException Wrapped(string macro, string msg) => new($"Macro '{macro}': {msg}.");
        }

        private static int? ReadOptionalInt(JsonElement el, string prop, string macroName)
        {
            if (!el.TryGetProperty(prop, out JsonElement n)) return null;
            if (n.ValueKind != JsonValueKind.Number || !n.TryGetInt32(out int value))
                throw Wrapped(macroName, $"'{prop}' must be an integer.");
            if (value < 0)
                throw Wrapped(macroName, $"'{prop}' must be >= 0.");
            return value;

            static NormalizeException Wrapped(string macro, string msg) => new($"Macro '{macro}': {msg}.");
        }
    }
}