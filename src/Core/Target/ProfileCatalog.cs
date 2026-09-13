using System;
using System.Collections.Generic;
using System.Linq;
using SlitherIn.Core.Config;
using SlitherIn.Core.Engine;

namespace SlitherIn.Core.Target
{
    /// <summary>
    /// All-profiles model. Loads + validates EVERY profile at startup (broken any =
    /// tray alert, never a crash), keeps their window targets for AUTO resolution,
    /// and owns each profile's RUNTIME — one <see cref="Workflow"/> per macro — so
    /// switching profiles PARKS the outgoing set (state retained, wall-clock keeps
    /// counting) and RESUMES it on return. Only the active profile's workflows are
    /// surfaced to the dispatcher; the catalog itself never talks to the engine.
    /// </summary>
    public sealed class ProfileCatalog : IDisposable
    {
        public const string Manual = "manual";
        public const string Auto = "auto";

        private readonly Dictionary<string, Profile> _byFile = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, WindowTarget> _targets = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<Workflow>> _runtimes = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>profile_mode from settings; the caller toggles it. Only used to
        /// decide whether AutoChanged-driven switches are active.</summary>
        public string Mode { get; set; } = Manual;

        public string ActiveFile { get; private set; }
        public Profile ActiveProfile { get; private set; }

        /// <summary>Workflows of the currently-selected profile — the driver
        /// (App/tests) passes these to the Dispatcher. Empty until a Select.</summary>
        public IReadOnlyList<Workflow> ActiveWorkflows { get; private set; } = Array.Empty<Workflow>();

        /// <summary>Result of the startup cross-profile pass (ambiguous-window
        /// warnings). Surfaced via the tray/--validate alongside per-file errors.</summary>
        public ValidationResult CrossProfile { get; private set; } = new();

        public event EventHandler ActiveChanged;

        public IReadOnlyCollection<string> ProfileFiles => _byFile.Keys;

        public bool HasProfile(string file)
            => !string.IsNullOrWhiteSpace(file) && _byFile.ContainsKey(file);

        /// <summary>Startup: load + validate every profile across the configured sources
        /// (<see cref="ConfigSources"/>), build each profile's workflow runtime, and
        /// run the cross-profile check. Returns the load outcome (per-file errors
        /// included) so the caller can alert without crashing. A profile that fails
        /// to BUILD (normalization) is not registered at all — callers see it as
        /// absent and keep the previous runtime (C-2).</summary>
        public LoadOutcome<List<LoadedProfile>> LoadAll(ConfigSources sources, Settings settings = null)
        {
            _byFile.Clear();
            _targets.Clear();
            _runtimes.Clear();
            ActiveFile = null;
            ActiveProfile = null;
            ActiveWorkflows = Array.Empty<Workflow>();

            Settings effective = settings
                ?? Loader.LoadSettingsFile(sources.SettingsPath).Config
                ?? new Settings();
            LoadOutcome<List<LoadedProfile>> outcome = Loader.LoadProfilesDetailed(sources.ProfileDirs, sources.SettingsFileName);

            foreach (LoadedProfile entry in outcome.Config)
            {
                // Register a profile only AFTER it builds: a normalization failure
                // (undefined wrapper, unknown input_engine, bad hold_for_ms) is the
                // same failure class as a validation failure — the file surfaces as
                // a per-file error, stays OUT of the catalog, and the composition's
                // keep-old rule applies to it like any other broken active file.
                try
                {
                    NormalizedProfile normalized = Normalizer.Normalize(entry.Profile, effective);
                    _byFile[entry.File] = entry.Profile;
                    _targets[entry.File] = WindowTarget.FromSpec(entry.Profile.Window);
                    _runtimes[entry.File] = normalized.Macros.Select(m => new Workflow(m)).ToList();
                }
                catch (NormalizeException ex)
                {
                    outcome.Errors.Add(new LoadError { File = entry.File, Message = ex.Message });
                }
            }

            CrossProfile = Validator.ValidateAll(outcome.Config.Select(x => x.Profile));
            return outcome;
        }

        /// <summary>Select a profile: parks the outgoing profile's runtime (state
        /// retained), activates the incoming one (resuming its pre-park state), and
        /// raises <see cref="ActiveChanged"/>. Unknown files activate nothing.</summary>
        public void Select(string file)
        {
            if (string.IsNullOrWhiteSpace(file)) return;
            if (string.Equals(file, ActiveFile, StringComparison.OrdinalIgnoreCase)) return;

            foreach (Workflow wf in ActiveWorkflows) wf.Park();

            ActiveFile = file;
            ActiveProfile = _byFile.TryGetValue(file, out Profile p) ? p : null;
            ActiveWorkflows = _runtimes.TryGetValue(file, out List<Workflow> list)
                ? list
                : (IReadOnlyList<Workflow>)Array.Empty<Workflow>();
            foreach (Workflow wf in ActiveWorkflows) wf.Unpark();
            ActiveChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Reload keep-old rule: an active profile whose file failed the new
        /// load RETURNS here as its PREVIOUS profile+runtime (re-keyed under that
        /// file), so the app keeps running what it had while the error is surfaced.
        /// Call <see cref="Select"/> right after to resume it. Intentionally does not
        /// touch <c>Active*</c> — the caller decides the active selection.</summary>
        public void KeepActive(string file, Profile profile, IReadOnlyList<Workflow> workflows)
        {
            _byFile[file] = profile;
            _targets[file] = WindowTarget.FromSpec(profile.Window);
            _runtimes[file] = workflows == null ? new List<Workflow>() : workflows.ToList();
        }

        /// <summary>AUTO mode: which profile owns this foreground window? Null = no
        /// match (stay on the current profile). Profiles without a window target can
        /// never be returned.</summary>
        public string ResolveOwner(ForegroundInfo foreground)
        {
            foreach (var pair in _targets)
            {
                if (pair.Value != null && pair.Value.HasAny && pair.Value.Matches(foreground.ProcessExe, foreground.Title))
                    return pair.Key;
            }
            return null;
        }

        public void Dispose()
        {
            ActiveWorkflows = Array.Empty<Workflow>();
            _byFile.Clear();
            _targets.Clear();
            _runtimes.Clear();
        }
    }
}