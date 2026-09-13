using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SlitherIn.Core.Config
{
    /// <summary>Everything gated on <c>schema_version</c> by <see cref="Loader"/>.</summary>
    public interface ISchemaVersioned
    {
        int SchemaVersion { get; }
    }

    // ==== Global settings (slitherin.settings.json) ========================

    /// <summary>Global settings shared by every profile. Optional file — omitted keys
    /// fall back to the defaults below.</summary>
    public sealed class Settings : ISchemaVersioned
    {
        [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; } = 1;
        [JsonPropertyName("input_engine")] public string InputEngine { get; set; } = "sendinput";
        [JsonPropertyName("abort")] public string Abort { get; set; }               // global default abort combo
        [JsonPropertyName("kill")] public string Kill { get; set; }                 // unconditional kill combo
        [JsonPropertyName("default_delay_ms")] public int? DefaultDelayMs { get; set; }
        [JsonPropertyName("window_check")] public bool WindowCheck { get; set; } = true;
        [JsonPropertyName("notifications")] public Notifications Notifications { get; set; } = new();
        [JsonPropertyName("log")] public string Log { get; set; } = "off";          // "off" | "on" | "debug"
        [JsonPropertyName("active_profile")] public string ActiveProfile { get; set; }
        [JsonPropertyName("profile_mode")] public string ProfileMode { get; set; } = "manual"; // "manual"|"auto"
        // NOTE: run_at_startup is NOT a settings option — it is a carried setting
        // held in the current user's HKCU Run key and toggled from the tray
        // (Startup.RunAtStartup). Deliberately absent from this model so the
        // settings file never implies it.
        /// <summary>EXTRA folders whose `slitherin*.json` profiles are loaded, in
        /// addition to the exe's own folder (always first). Relative entries resolve
        /// against the settings file's folder. Optional; empty = exe folder only.</summary>
        [JsonPropertyName("profile_dirs")] public List<string> ProfileDirs { get; set; } = new();
    }

    /// <summary>Tray balloon notifications.</summary>
    public sealed class Notifications
    {
        [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    }

    // ==== Profile (slitherin.json / slitherin.<N>.<label>.json) ============

    /// <summary>One playable profile: window target + wrappers + macros.</summary>
    public sealed class Profile : ISchemaVersioned
    {
        [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; } = 1;
        [JsonPropertyName("name")] public string Name { get; set; }
        /// <summary>Optional engine override; absent = follow settings default.</summary>
        [JsonPropertyName("input_engine")] public string InputEngine { get; set; }
        [JsonPropertyName("default_delay_ms")] public int? DefaultDelayMs { get; set; }
        /// <summary>Window gating target (exe and/or title). Absent = manual-only
        /// profile (never auto-selected).</summary>
        [JsonPropertyName("window")] public WindowSpec Window { get; set; }
        [JsonPropertyName("wrappers")] public Dictionary<string, Wrapper> Wrappers { get; set; }
        [JsonPropertyName("macros")] public List<Macro> Macros { get; set; } = new();
    }

    /// <summary>Target descriptor: process exe (optional) and/or title wildcard
    /// (optional). Match = exe AND title when both are given.</summary>
    public sealed class WindowSpec
    {
        [JsonPropertyName("exe")] public string Exe { get; set; }
        [JsonPropertyName("title")] public string Title { get; set; }
    }

    /// <summary>One macro: start / loop / schedule / steps.</summary>
    public sealed class Macro
    {
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("trigger")] public string Trigger { get; set; }
        [JsonPropertyName("fire_after_ms")] public int? FireAfterMs { get; set; }   // hold N ms, then it counts
        [JsonPropertyName("toggle")] public string Toggle { get; set; }             // arm/disarm key
        [JsonPropertyName("toggle_sound")] public ToggleSound ToggleSound { get; set; }
        [JsonPropertyName("loop")] public LoopSpec Loop { get; set; }
        [JsonPropertyName("schedule")] public Schedule Schedule { get; set; }
        /// <summary>Step objects are shape-typed ({keys}/{button}/{sound}/{wait_ms},
        /// optional wrapper/cooldown_ms). Classified by <see cref="Normalizer"/>;
        /// deliberately not polymorphic JSON so the vocabulary stays strict.</summary>
        [JsonPropertyName("steps")] public List<JsonElement> Steps { get; set; } = new();
        [JsonPropertyName("abort_keys")] public string AbortKeys { get; set; }     // stops only this macro
        [JsonPropertyName("ignore_abort")] public bool IgnoreAbort { get; set; }
    }

    /// <summary>Loop: a count + one flag. Count omitted = 1; -1 = forever.</summary>
    public sealed class LoopSpec
    {
        [JsonPropertyName("count")] public int? Count { get; set; }
        /// <summary>Omitted = derived (tap ↦ true, held ↦ false).</summary>
        [JsonPropertyName("continue_on_release")] public bool? ContinueOnRelease { get; set; }
    }

    /// <summary>Pacing policy. Omitted = fixed pacing via default_delay_ms.</summary>
    public sealed class Schedule
    {
        [JsonPropertyName("mode")] public string Mode { get; set; } = "fixed";     // "fixed" | "cooldown"
        [JsonPropertyName("fire_delay_ms")] public int? FireDelayMs { get; set; }  // cooldown mode pacing
    }

    /// <summary>Named reusable before/after step sets.</summary>
    public sealed class Wrapper
    {
        [JsonPropertyName("before")] public List<JsonElement> Before { get; set; }
        [JsonPropertyName("after")] public List<JsonElement> After { get; set; }
    }

    /// <summary>Arm/disarm cues. A PRESENT slot beeps the default tone; omitted =
    /// silent for that state.</summary>
    public sealed class ToggleSound
    {
        [JsonPropertyName("on")] public SoundSpec On { get; set; }
        [JsonPropertyName("off")] public SoundSpec Off { get; set; }
    }

    /// <summary>Sound spec (frequency/file overrides).</summary>
    public sealed class SoundSpec
    {
        [JsonPropertyName("frequency")] public int? Frequency { get; set; }
        [JsonPropertyName("duration_ms")] public int? DurationMs { get; set; }
        [JsonPropertyName("file")] public string File { get; set; }
    }
}