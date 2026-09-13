using NUnit.Framework;
using SlitherIn;

namespace SlitherIn.Tests
{
    /// <summary>Command-line contract (C-7): `--settings-file <path>` /
    /// `--profile-dir <path>` (repeatable), `--key=value` accepted, unknown
    /// switches and missing values ignored.</summary>
    [TestFixture]
    public class CliArgsTests
    {
        [Test]
        public void EmptyArgs_GiveDefaults()
        {
            var a = CliArgs.Parse(null);
            Assert.That(a.SettingsFile, Is.Null);
            Assert.That(a.ProfileDirs, Is.Empty);
        }

        [Test]
        public void SettingsFile_SpaceForm()
        {
            var a = CliArgs.Parse(new[] { "--settings-file", @"D:\cfg\custom.json" });
            Assert.That(a.SettingsFile, Is.EqualTo(@"D:\cfg\custom.json"));
        }

        [Test]
        public void SettingsFile_EqualsForm()
        {
            var a = CliArgs.Parse(new[] { "--settings-file=other.json" });
            Assert.That(a.SettingsFile, Is.EqualTo("other.json"));
        }

        [Test]
        public void ProfileDirs_Repeatable()
        {
            var a = CliArgs.Parse(new[] { "--profile-dir", @"D:\a", "--profile-dir", @"D:\b" });
            Assert.That(a.ProfileDirs, Is.EqualTo(new[] { @"D:\a", @"D:\b" }));
        }

        [Test]
        public void MixedForms_AllHonored()
        {
            var a = CliArgs.Parse(new[]
            {
                "--settings-file", "settings.json",
                "--profile-dir=A",
                "--profile-dir", "B",
            });
            Assert.That(a.SettingsFile, Is.EqualTo("settings.json"));
            Assert.That(a.ProfileDirs, Is.EqualTo(new[] { "A", "B" }));
        }

        [Test]
        public void UnknownSwitch_IsIgnored()
        {
            var a = CliArgs.Parse(new[] { "--bogus", "x", "--profile-dir", "A" });
            Assert.That(a.SettingsFile, Is.Null);
            Assert.That(a.ProfileDirs, Is.EqualTo(new[] { "A" }));
        }

        [Test]
        public void MissingValue_IsIgnored()
        {
            var a = CliArgs.Parse(new[] { "--settings-file", "--profile-dir", "A" });
            Assert.That(a.SettingsFile, Is.Null, "a flag with only another flag following has no value");
            Assert.That(a.ProfileDirs, Is.EqualTo(new[] { "A" }));
        }

        [Test]
        public void EmptyValue_IsIgnored()
        {
            var a = CliArgs.Parse(new[] { "--settings-file=" });
            Assert.That(a.SettingsFile, Is.Null);
        }
    }
}