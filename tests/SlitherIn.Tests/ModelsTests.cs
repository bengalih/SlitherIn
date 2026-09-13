using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using SlitherIn.Core.Config;

namespace SlitherIn.Tests
{
    /// <summary>Models contract: the sample-json files must deserialize into the
    /// config POCOs EXACTLY. Any vocabulary drift in a sample is caught here.
    /// Stage 1 — real, green.</summary>
    [TestFixture]
    public class ModelsTests
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            PropertyNameCaseInsensitive = false,
        };

        private static string ReadSample(params string[] relative)
        {
            string path = Path.Combine(
                new[] { AppDomain.CurrentDomain.BaseDirectory, "sample-json" }
                    .Concat(relative).ToArray());
            return File.ReadAllText(path);
        }

        [Test]
        public void Settings_Sample_Deserializes()
        {
            var s = JsonSerializer.Deserialize<Settings>(ReadSample("slitherin.settings.sample.json"), JsonOptions);
            Assert.That(s.SchemaVersion, Is.EqualTo(1));
            Assert.That(s.Abort, Is.EqualTo("Ctrl+Alt+F12"));
            Assert.That(s.Kill, Is.EqualTo("Ctrl+Alt+Esc"));
            Assert.That(s.DefaultDelayMs, Is.EqualTo(3000));
            Assert.That(s.InputEngine, Is.EqualTo("sendinput"));
            Assert.That(s.WindowCheck, Is.True);
            Assert.That(s.Log, Is.EqualTo("off"));
            Assert.That(s.ActiveProfile, Is.EqualTo("slitherin.json"));
            Assert.That(s.ProfileMode, Is.EqualTo("manual"));
            Assert.That(s.Notifications.Enabled, Is.False);
            Assert.That(s.ProfileDirs, Is.Empty, "sample keeps the default single-folder profile search");
        }

        [Test]
        public void CombatProfile_Sample_Deserializes()
        {
            var p = JsonSerializer.Deserialize<Profile>(ReadSample("profiles", "slitherin.json"), JsonOptions);
            Assert.That(p.SchemaVersion, Is.EqualTo(1));
            Assert.That(p.Name, Is.EqualTo("Main (CharacterA - Sarlona)"));
            Assert.That(p.Window.Exe, Is.EqualTo("dndclient64.exe"));
            Assert.That(p.Window.Title, Is.EqualTo("CharacterA - Sarlona"));
            Assert.That(p.Wrappers, Has.Count.EqualTo(1));
            Assert.That(p.Wrappers["bar6"].Before, Has.Count.EqualTo(1));
            Assert.That(p.Wrappers["bar6"].After, Has.Count.EqualTo(1));

            var m = p.Macros.Single();
            Assert.That(m.Name, Is.EqualTo("Combat Cycle"));
            Assert.That(m.Trigger, Is.EqualTo("LButton"));
            Assert.That(m.FireAfterMs, Is.EqualTo(1000));
            Assert.That(m.Toggle, Is.EqualTo("Pause"));
            Assert.That(m.ToggleSound.On.Frequency, Is.EqualTo(1500));
            Assert.That(m.Loop.Count, Is.EqualTo(-1));
            Assert.That(m.Schedule.Mode, Is.EqualTo("cooldown"));
            Assert.That(m.Schedule.FireDelayMs, Is.EqualTo(700));
            Assert.That(m.Steps, Has.Count.EqualTo(5));
        }

        [Test]
        public void CharBProfile_Sample_Deserializes()
        {
            var p = JsonSerializer.Deserialize<Profile>(ReadSample("profiles", "slitherin.char-b.json"), JsonOptions);
            Assert.That(p.Window.Title, Is.EqualTo("CharacterB - Sarlona"));
            Assert.That(p.DefaultDelayMs, Is.EqualTo(2500));
            Assert.That(p.Macros, Has.Count.EqualTo(2));
            Assert.That(p.Macros[0].Loop, Is.Null);      // no loop block → count 1
            Assert.That(p.Macros[1].Trigger, Is.EqualTo("RButton"));
        }

        [Test]
        public void Steps_Stay_ShapeTypedRawJson()
        {
            // Locked schema decision: config steps are raw, shape-typed {keys}/
            // {sound}/{wait_ms} JSON; typed step classes exist only in the
            // Normalizer output (Stage 2). "0" here is the top-row key 0.
            var p = JsonSerializer.Deserialize<Profile>(ReadSample("profiles", "slitherin.json"), JsonOptions);
            var first = p.Macros[0].Steps[0];
            Assert.That(first.ValueKind, Is.EqualTo(JsonValueKind.Object));
            Assert.That(first.TryGetProperty("keys", out JsonElement keys), Is.True);
            Assert.That(keys.GetString(), Is.EqualTo("0"));
        }
    }
}