using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using SlitherIn.Core.Config;

namespace SlitherIn.Tests
{
    /// <summary>One-pass canonicalization. Stage 2 — real, green. Locked semantics:
    /// a config step = ONE logical action (keys list = chord SEQUENCE inside it);
    /// wrappers attach before+after (no stacking); pacing defaults folded.</summary>
    [TestFixture]
    public class NormalizerTests
    {
        private static readonly JsonSerializerOptions Json = new()
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        private static Profile P(string json) => JsonSerializer.Deserialize<Profile>(json, Json);

        [Test]
        public void Wrapper_AttachesBeforeAndAfter()
        {
            var profile = P(@"{
                ""name"": ""Test"",
                ""wrappers"": {
                    ""bar6"": { ""before"": [ { ""keys"": ""Ctrl+6"" } ], ""after"": [ { ""keys"": ""Ctrl+1"" } ] }
                },
                ""macros"": [
                    { ""name"": ""M"", ""trigger"": ""Ctrl+K"",
                      ""steps"": [ { ""keys"": ""7"", ""wrapper"": ""bar6"" } ] }
                ]
            }");

            var normalized = Normalizer.Normalize(profile, new Settings());
            var macro = normalized.Macros.Single();

            Assert.That(macro.Steps, Has.Count.EqualTo(3));
            Assert.That(macro.Steps[0].Chords.Single(), Is.EqualTo(KeyName.Parse("Ctrl+6")));
            Assert.That(macro.Steps[1].Chords.Single(), Is.EqualTo(KeyName.Parse("7")));
            Assert.That(macro.Steps[2].Chords.Single(), Is.EqualTo(KeyName.Parse("Ctrl+1")));
        }

        [Test]
        public void KeysList_IsOneLogicalAction_ChordSequence()
        {
            var profile = P(@"{
                ""name"": ""T"",
                ""macros"": [
                    { ""name"": ""M"", ""trigger"": ""F9"",
                      ""steps"": [ { ""keys"": [ ""Ctrl+6"", ""7"" ] } ] }
                ]
            }");

            var normalized = Normalizer.Normalize(profile, new Settings());
            var step = normalized.Macros.Single().Steps.Single();

            Assert.That(step.IsSound, Is.False);
            Assert.That(step.Chords, Has.Count.EqualTo(2));
            Assert.That(step.Chords[0], Is.EqualTo(KeyName.Parse("Ctrl+6")));
            Assert.That(step.Chords[1], Is.EqualTo(KeyName.Parse("7")));
        }

        [Test]
        public void StepWaitMs_OverridesDefaultDelay()
        {
            var profile = P(@"{
                ""name"": ""T"",
                ""macros"": [
                    { ""name"": ""M"", ""trigger"": ""F9"",
                      ""steps"": [ { ""keys"": ""7"", ""wait_ms"": 0 } ] }
                ]
            }");

            var normalized = Normalizer.Normalize(profile, new Settings());
            Assert.That(normalized.Macros.Single().Steps.Single().WaitMs, Is.EqualTo(0));
        }

        [Test]
        public void DefaultDelay_Resolves_ProfileOverSettingsOverBuiltIn()
        {
            var profile = P(@"{
                ""name"": ""T"", ""default_delay_ms"": 2500,
                ""macros"": [ { ""name"": ""M"", ""trigger"": ""F9"", ""steps"": [ { ""keys"": ""7"" } ] } ]
            }");
            Assert.That(Normalizer.Normalize(profile, new Settings()).DefaultDelayMs, Is.EqualTo(2500));

            var noOverride = P(@"{ ""name"": ""T"", ""macros"": [ { ""name"": ""M"", ""trigger"": ""F9"", ""steps"": [ { ""keys"": ""7"" } ] } ] }");
            Assert.That(Normalizer.Normalize(noOverride, new Settings { DefaultDelayMs = 2000 }).DefaultDelayMs, Is.EqualTo(2000));
            Assert.That(Normalizer.Normalize(noOverride, new Settings()).DefaultDelayMs, Is.EqualTo(3000));

            var nullSettingsProfile = P(@"{ ""name"": ""T"", ""macros"": [] }");
            Assert.That(Normalizer.Normalize(nullSettingsProfile, null).DefaultDelayMs, Is.EqualTo(3000));
        }

        [Test]
        public void InputEngine_Resolves_ProfileOverride_ThenDefaults()
        {
            var viiper = P(@"{
                ""name"": ""T"", ""input_engine"": ""viiper"",
                ""macros"": [ { ""name"": ""M"", ""trigger"": ""F9"", ""steps"": [ { ""keys"": ""7"" } ] } ]
            }");
            Assert.That(Normalizer.Normalize(viiper, new Settings()).InputEngine, Is.EqualTo("viiper"));

            var noOverride = P(@"{ ""name"": ""T"", ""macros"": [ { ""name"": ""M"", ""trigger"": ""F9"", ""steps"": [ { ""keys"": ""7"" } ] } ] }");
            Assert.That(Normalizer.Normalize(noOverride, new Settings { InputEngine = "viiper" }).InputEngine, Is.EqualTo("viiper"));
            Assert.That(Normalizer.Normalize(noOverride, new Settings()).InputEngine, Is.EqualTo("sendinput"));
        }

        [Test]
        public void InvalidChord_ThrowsKeyParseException()
        {
            var profile = P(@"{
                ""name"": ""T"",
                ""macros"": [ { ""name"": ""M"", ""trigger"": ""Control+K"", ""steps"": [ { ""keys"": ""7"" } ] } ]
            }");
            Assert.Throws<KeyParseException>(() => Normalizer.Normalize(profile, new Settings()));
        }

        [Test]
        public void UndefinedWrapper_ThrowsNormalizeException()
        {
            var profile = P(@"{
                ""name"": ""T"",
                ""macros"": [ { ""name"": ""M"", ""trigger"": ""F9"", ""steps"": [ { ""keys"": ""7"", ""wrapper"": ""nope"" } ] } ]
            }");
            Assert.Throws<NormalizeException>(() => Normalizer.Normalize(profile, new Settings()));
        }

        [Test]
        public void HoldForMsOnMultiChord_Throws()
        {
            var profile = P(@"{
                ""name"": ""T"",
                ""macros"": [ { ""name"": ""M"", ""trigger"": ""F9"",
                                ""steps"": [ { ""keys"": [ ""Ctrl+6"", ""7"" ], ""hold_for_ms"": 500 } ] } ]
            }");
            Assert.Throws<NormalizeException>(() => Normalizer.Normalize(profile, new Settings()));
        }

        [Test]
        public void SoundStep_EmptyObject_IsDefaultBeep()
        {
            var profile = P(@"{
                ""name"": ""T"",
                ""macros"": [ { ""name"": ""M"", ""trigger"": ""F9"",
                                ""steps"": [ { ""sound"": {} } ] } ]
            }");
            var step = Normalizer.Normalize(profile, new Settings()).Macros.Single().Steps.Single();
            Assert.That(step.IsSound, Is.True);
            Assert.That(step.Sound, Is.Not.Null);
            Assert.That(step.Sound.Frequency, Is.Null);
            Assert.That(step.Chords, Is.Empty);
        }

        [Test]
        public void TopRowDigit_And_Numpad_Stay_Distinct()
        {
            var profile = P(@"{
                ""name"": ""T"",
                ""macros"": [ { ""name"": ""M"", ""trigger"": ""F9"",
                                ""steps"": [ { ""keys"": ""1"" }, { ""keys"": ""Numpad1"" } ] } ]
            }");
            var steps = Normalizer.Normalize(profile, new Settings()).Macros.Single().Steps;
            Assert.That(steps[0].Chords.Single().Key, Is.EqualTo("1"));
            Assert.That(steps[1].Chords.Single().Key, Is.EqualTo("Numpad1"));
        }
    }
}