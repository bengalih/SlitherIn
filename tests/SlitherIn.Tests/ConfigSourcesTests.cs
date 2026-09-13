using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using SlitherIn.Core.Config;

namespace SlitherIn.Tests
{
    /// <summary>ConfigSources contract (C-7): the settings file resolves from
    /// --settings-file (else exe\slitherin.settings.json); profile dirs are the exe
    /// folder ALWAYS first, then settings `profile_dirs` (relative entries resolve
    /// against the settings file's folder), then --profile-dir; duplicates collapse
    /// case-insensitively; watch roots = settings folder + profile dirs.
    /// Pure path math — no files needed except explicit existence probes.</summary>
    [TestFixture]
    public class ConfigSourcesTests
    {
        private string _exe;
        private string _settingsDir;

        [SetUp]
        public void SetUp()
        {
            string baseDir = Path.Combine(Path.GetTempPath(), "slitherin-sources-" + Guid.NewGuid().ToString("N"));
            _exe = Path.Combine(baseDir, "exe");
            _settingsDir = Path.Combine(baseDir, "settings");
            Directory.CreateDirectory(_exe);
            Directory.CreateDirectory(_settingsDir);
        }

        [TearDown]
        public void TearDown()
        {
            string baseDir = Path.GetDirectoryName(_exe);
            try { if (baseDir != null && Directory.Exists(baseDir)) Directory.Delete(baseDir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }

        [Test]
        public void Default_ResolvesToExeDir()
        {
            var s = ConfigSources.Resolve(_exe, null, null, null);

            Assert.That(s.SettingsPath, Is.EqualTo(Path.Combine(_exe, Loader.SettingsFileName)));
            Assert.That(s.SettingsFileName, Is.EqualTo(Loader.SettingsFileName));
            Assert.That(s.ProfileDirs, Is.EqualTo(new[] { _exe }));
            Assert.That(s.WatchRoots, Is.EqualTo(new[] { _exe }));
        }

        [Test]
        public void CliSettingsFile_OverridesSettingsPath()
        {
            string custom = Path.Combine(_settingsDir, "my-settings.json");
            var s = ConfigSources.Resolve(_exe, custom, null, null);

            Assert.That(s.SettingsPath, Is.EqualTo(custom));
            Assert.That(s.SettingsFileName, Is.EqualTo("my-settings.json"));
            Assert.That(s.WatchRoots, Is.EqualTo(new[] { _settingsDir, _exe }),
                "the settings folder joins the watch roots");
        }

        [Test]
        public void ProfileDirs_FromSettings_ResolveRelativeToSettingsDir()
        {
            string customSettings = Path.Combine(_settingsDir, "s.json");
            var settings = new Settings { ProfileDirs = { "profiles", _exe } }; // exe dedupes against the default
            var s = ConfigSources.Resolve(_exe, customSettings, null, settings);

            Assert.That(s.ProfileDirs, Is.EqualTo(new[] { _exe, Path.Combine(_settingsDir, "profiles") }),
                "exe dir stays first; relative profile_dirs resolve against the settings folder");
            Assert.That(s.WatchRoots, Is.EqualTo(new[] { _settingsDir, _exe, Path.Combine(_settingsDir, "profiles") }));
        }

        [Test]
        public void CliProfileDirs_AppendAfterSettingsDirs()
        {
            string aux = Path.Combine(Path.GetTempPath(), "slitherin-cli-aux-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(aux);
            try
            {
                var settings = new Settings { ProfileDirs = { "extra" } };
                var s = ConfigSources.Resolve(_exe, Path.Combine(_settingsDir, "s.json"),
                    new[] { aux }, settings);

                Assert.That(s.ProfileDirs, Is.EqualTo(new[] { _exe, Path.Combine(_settingsDir, "extra"), aux }),
                    "order locked: exe dir -> settings profile_dirs -> --profile-dir");
            }
            finally
            {
                try { if (Directory.Exists(aux)) Directory.Delete(aux, recursive: true); } catch { }
            }
        }

        [Test]
        public void DuplicateDirs_Collapse_CaseInsensitiveFirstWins()
        {
            var s = ConfigSources.Resolve(_exe, Path.Combine(_exe, Loader.SettingsFileName),
                new[] { _exe.ToUpperInvariant() }, new Settings { ProfileDirs = { _exe } });

            Assert.That(s.ProfileDirs, Is.EqualTo(new[] { _exe }), "every duplicate collapses onto the exe dir");
            Assert.That(s.WatchRoots, Is.EqualTo(new[] { _exe }));
        }

        [Test]
        public void Rebuild_PicksUpNewProfileDirs()
        {
            var s = ConfigSources.Resolve(_exe, null, null, new Settings());
            Assert.That(s.ProfileDirs, Is.EqualTo(new[] { _exe }));

            var rebuilt = s.Rebuild(new Settings { ProfileDirs = { "later" } });
            Assert.That(rebuilt.ProfileDirs, Is.EqualTo(new[] { _exe, Path.Combine(_exe, "later") }));
        }

        [Test]
        public void ResolveProfilePath_FindsFirstExisting_Files()
        {
            Directory.CreateDirectory(_settingsDir);
            string aux = Path.Combine(Path.GetTempPath(), "slitherin-resolve-aux-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(aux);
            try
            {
                File.WriteAllText(Path.Combine(_exe, "slitherin.json"), "{}");
                File.WriteAllText(Path.Combine(aux, "slitherin.json"), "{}");   // shadowed
                File.WriteAllText(Path.Combine(aux, "slitherin.aux.json"), "{}");

                var s = ConfigSources.Resolve(_exe, null, new[] { aux }, null);

                Assert.That(s.ResolveProfilePath("slitherin.json"), Is.EqualTo(Path.Combine(_exe, "slitherin.json")),
                    "the exe dir's copy wins");
                Assert.That(s.ResolveProfilePath("slitherin.aux.json"), Is.EqualTo(Path.Combine(aux, "slitherin.aux.json")));
                Assert.That(s.ResolveProfilePath("missing.json"), Is.Null);
            }
            finally
            {
                try { if (Directory.Exists(aux)) Directory.Delete(aux, recursive: true); } catch { }
            }
        }
    }
}