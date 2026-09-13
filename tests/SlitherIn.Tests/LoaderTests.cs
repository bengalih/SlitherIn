using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using SlitherIn.Core.Config;

namespace SlitherIn.Tests
{
    /// <summary>Loader contract: never throws at the UI; a missing settings file is
    /// defaults, not an error; a broken/schema-mismatched/invalid profile FAILS
    /// only itself; the settings file is never counted as a profile.
    /// Stage 2 — real, green.</summary>
    [TestFixture]
    public class LoaderTests
    {
        private string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "slitherin-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }

        private string Write(string name, string contents)
        {
            string path = Path.Combine(_dir, name);
            File.WriteAllText(path, contents);
            return path;
        }

        private static string SettingsPath(string dir)
            => Path.Combine(dir, Loader.SettingsFileName);

        private string WriteAux(string auxDir, string name, string contents)
        {
            Directory.CreateDirectory(auxDir);
            string path = Path.Combine(auxDir, name);
            File.WriteAllText(path, contents);
            return path;
        }

        private string CopySample(string relative, string asName)
        {
            string src = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sample-json", relative);
            string dst = Path.Combine(_dir, asName);
            File.Copy(src, dst);
            return dst;
        }

        [Test]
        public void Settings_MissingFile_IsDefaultsNotError()
        {
            var outcome = Loader.LoadSettingsFile(SettingsPath(_dir));
            Assert.That(outcome.Succeeded, Is.True);
            Assert.That(outcome.Config.DefaultDelayMs, Is.Null);
            Assert.That(outcome.Config.Abort, Is.Null);
        }

        [Test]
        public void Settings_File_Loads()
        {
            CopySample("slitherin.settings.sample.json", Loader.SettingsFileName);
            var outcome = Loader.LoadSettingsFile(SettingsPath(_dir));
            Assert.That(outcome.Succeeded, Is.True);
            Assert.That(outcome.Config.Abort, Is.EqualTo("Ctrl+Alt+F12"));
            Assert.That(outcome.Config.DefaultDelayMs, Is.EqualTo(3000));
            Assert.That(outcome.Config.ProfileMode, Is.EqualTo("manual"));
        }

        [Test]
        public void Settings_UnknownEngine_IsRejected()
        {
            Write(Loader.SettingsFileName, @"{ ""input_engine"": ""nasal"" }");
            var outcome = Loader.LoadSettingsFile(SettingsPath(_dir));
            Assert.That(outcome.Succeeded, Is.False);
            Assert.That(outcome.Errors, Is.Not.Empty);
        }

        [Test]
        public void Settings_BadJson_IsRejected_NotThrown()
        {
            Write(Loader.SettingsFileName, "{ this is not json");
            var outcome = Loader.LoadSettingsFile(SettingsPath(_dir));
            Assert.That(outcome.Succeeded, Is.False);
            Assert.That(outcome.Errors.Single().Message, Does.Contain("JSON parse error"));
        }

        [Test]
        public void Profiles_Load_ExcludingSettingsFile()
        {
            CopySample("profiles/slitherin.json", "slitherin.json");
            CopySample("profiles/slitherin.char-b.json", "slitherin.char-b.json");
            Write(Loader.SettingsFileName, "{ }");

            var outcome = Loader.LoadProfiles(new[] { _dir });
            Assert.That(outcome.Succeeded, Is.True);
            Assert.That(outcome.Config, Has.Count.EqualTo(2));
            Assert.That(outcome.Config.Any(p => p.Name.Contains("CharacterA")), Is.True);
            Assert.That(outcome.Config.Any(p => p.Name.Contains("CharacterB")), Is.True);
        }

        [Test]
        public void Profiles_BadJson_FailsOnlyThatFile_OthersStillLoad()
        {
            Write("slitherin.bad.json", "{ this is not json");
            CopySample("profiles/slitherin.json", "slitherin.json");

            var outcome = Loader.LoadProfiles(new[] { _dir });
            Assert.That(outcome.Config, Has.Count.EqualTo(1));
            Assert.That(outcome.Errors, Has.Count.EqualTo(1));
            Assert.That(outcome.Errors.Single().File, Does.EndWith("slitherin.bad.json"));
        }

        [Test]
        public void Profiles_SchemaVersionMismatch_IsRejected()
        {
            Write("slitherin.v2.json", @"{
                ""schema_version"": 2,
                ""name"": ""Future"",
                ""macros"": [ { ""name"": ""M"", ""trigger"": ""F9"", ""steps"": [ { ""keys"": ""7"" } ] } ]
            }");
            CopySample("profiles/slitherin.json", "slitherin.json");

            var outcome = Loader.LoadProfiles(new[] { _dir });
            Assert.That(outcome.Config, Has.Count.EqualTo(1));
            Assert.That(outcome.Errors.Single().Message, Does.Contain("schema_version"));
        }

        [Test]
        public void Profiles_ValidationError_RejectsOnlyThatFile()
        {
            Write("slitherin.broken.json", @"{
                ""name"": ""Broken"",
                ""macros"": [ { ""name"": ""M"", ""steps"": [ { ""keys"": ""7"" } ] } ]
            }");
            CopySample("profiles/slitherin.char-b.json", "slitherin.char-b.json");

            var outcome = Loader.LoadProfiles(new[] { _dir });
            Assert.That(outcome.Config, Has.Count.EqualTo(1));
            Assert.That(outcome.Errors.Single().Message, Does.Contain("no trigger"));
        }

        // ==== C-7: multiple profile dirs ===========================================

        private const string AuxJson = @"{
            ""schema_version"": 1,
            ""name"": ""Aux"",
            ""macros"": [ { ""name"": ""M"", ""trigger"": ""F9"", ""steps"": [ { ""keys"": ""7"" } ] } ]
        }";

        [Test]
        public void Profiles_MultipleDirs_LoadAll_AndFirstDirWinsOnSameName()
        {
            string aux = Path.Combine(Path.GetTempPath(), "slitherin-aux-" + Guid.NewGuid().ToString("N"));
            try
            {
                Write("slitherin.json", @"{
                    ""schema_version"": 1,
                    ""name"": ""Primary"",
                    ""macros"": [ { ""name"": ""M"", ""trigger"": ""F1"", ""steps"": [ { ""keys"": ""1"" } ] } ]
                }");
                // Same file name in the second dir — MUST be shadowed by the first.
                WriteAux(aux, "slitherin.json", AuxJson);
                WriteAux(aux, "slitherin.extra.json", AuxJson);

                var outcome = Loader.LoadProfiles(new[] { _dir, aux });

                Assert.That(outcome.Config, Has.Count.EqualTo(2), "same-name file dedupes (first dir wins)");
                Assert.That(outcome.Config.Any(p => p.Name == "Primary"), Is.True, "primary dir's file wins over the aux copy");
                Assert.That(outcome.Config.Any(p => p.Name == "Aux"), Is.True, "unique aux file still loads");
                Assert.That(outcome.Errors, Is.Empty);
            }
            finally
            {
                try { if (Directory.Exists(aux)) Directory.Delete(aux, recursive: true); } catch { }
            }
        }

        [Test]
        public void Profiles_MissingDir_LoadsOthers_AndReportsTheMissingDir()
        {
            string missing = Path.Combine(_dir, "does-not-exist");
            CopySample("profiles/slitherin.json", "slitherin.json");

            var outcome = Loader.LoadProfiles(new[] { _dir, missing });

            Assert.That(outcome.Config, Has.Count.EqualTo(1), "existing dir keeps loading");
            Assert.That(outcome.Errors.Single().File, Is.EqualTo(missing), "the missing dir is surfaced, not swallowed");
        }

        [Test]
        public void Profiles_CustomSettingsName_IsExcludedFromProfiles()
        {
            string customSettings = "my-settings.json";
            Write(customSettings, "{ }");
            CopySample("profiles/slitherin.json", "slitherin.json");

            var outcome = Loader.LoadProfiles(new[] { _dir }, customSettings);

            Assert.That(outcome.Config, Has.Count.EqualTo(1), "the custom-named settings file is excluded");
            Assert.That(outcome.Config.Single().Name, Does.Contain("CharacterA"));
        }
    }
}