using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using SlitherIn.Core.Config;

namespace SlitherIn.Tests
{
    /// <summary>Schema/structural rules. NEVER throws — collects issues.
    /// Stage 2 checks are real; the ambiguous-target cross check lands Stage 5.</summary>
    [TestFixture]
    public class ValidationTests
    {
        private static readonly JsonSerializerOptions Json = new()
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        private static Profile P(string json) => JsonSerializer.Deserialize<Profile>(json, Json);
        private static bool HasError(ValidationResult r, string contains)
            => r.Issues.Any(i =>
                i.Severity == ValidationSeverity.Error && i.Message.Contains(contains));

        [Test]
        public void EmptyProfile_IsValid_WithWarningsOnly()
        {
            var r = Validator.Validate(P(@"{ ""name"": ""Empty"", ""macros"": [] }"));
            Assert.That(r.IsValid, Is.True);
            Assert.That(r.Issues, Is.Not.Empty);
            Assert.That(r.Issues.All(i => i.Severity == ValidationSeverity.Warning), Is.True);
        }

        [Test]
        public void Macro_WithoutTrigger_IsAnError()
        {
            var r = Validator.Validate(P(@"{
                ""name"": ""T"",
                ""macros"": [ { ""name"": ""M"", ""steps"": [ { ""keys"": ""7"" } ] } ]
            }"));
            Assert.That(r.IsValid, Is.False);
            Assert.That(HasError(r, "no trigger"), Is.True);
        }

        [Test]
        public void InvalidTriggerToken_IsAnError()
        {
            var r = Validator.Validate(P(@"{
                ""name"": ""T"",
                ""macros"": [ { ""name"": ""M"", ""trigger"": ""Control+K"", ""steps"": [ { ""keys"": ""7"" } ] } ]
            }"));
            Assert.That(HasError(r, "trigger"), Is.True);
        }

        [Test]
        public void UndefinedWrapper_IsAnError()
        {
            var r = Validator.Validate(P(@"{
                ""name"": ""T"",
                ""macros"": [ { ""name"": ""M"", ""trigger"": ""F9"",
                                ""steps"": [ { ""keys"": ""7"", ""wrapper"": ""nope"" } ] } ]
            }"));
            Assert.That(HasError(r, "undefined wrapper"), Is.True);
        }

        [Test]
        public void UnknownStepProperty_IsWarning_NotError()
        {
            var r = Validator.Validate(P(@"{
                ""name"": ""T"",
                ""macros"": [ { ""name"": ""M"", ""trigger"": ""F9"",
                                ""steps"": [ { ""keys"": ""7"", ""hkeys"": ""typo"" } ] } ]
            }"));
            Assert.That(r.IsValid, Is.True, "a stray property must not block loading");
            Assert.That(r.Issues.Any(i =>
                i.Severity == ValidationSeverity.Warning && i.Message.Contains("hkeys")), Is.True);
        }

        [Test]
        public void UnknownScheduleMode_IsAnError()
        {
            var r = Validator.Validate(P(@"{
                ""name"": ""T"",
                ""macros"": [ { ""name"": ""M"", ""trigger"": ""F9"", ""schedule"": { ""mode"": ""turbo"" },
                                ""steps"": [ { ""keys"": ""7"" } ] } ]
            }"));
            Assert.That(HasError(r, "schedule.mode"), Is.True);
        }

        [Test]
        public void LoopCount_OutOfRange_IsAnError_Zero_Warns()
        {
            var r = Validator.Validate(P(@"{
                ""name"": ""T"",
                ""macros"": [ { ""name"": ""M"", ""trigger"": ""F9"", ""loop"": { ""count"": -2 },
                                ""steps"": [ { ""keys"": ""7"" } ] } ]
            }"));
            Assert.That(HasError(r, "loop count"), Is.True);

            var zero = Validator.Validate(P(@"{
                ""name"": ""T"",
                ""macros"": [ { ""name"": ""M"", ""trigger"": ""F9"", ""loop"": { ""count"": 0 },
                                ""steps"": [ { ""keys"": ""7"" } ] } ]
            }"));
            Assert.That(zero.IsValid, Is.True);
            Assert.That(zero.Issues.Any(i => i.Message.Contains("0")), Is.True);
        }

        [Test]
        public void HoldForMsOnMultiChord_Warns_ButValid()
        {
            var r = Validator.Validate(P(@"{
                ""name"": ""T"",
                ""macros"": [ { ""name"": ""M"", ""trigger"": ""F9"",
                                ""steps"": [ { ""keys"": [ ""Ctrl+6"", ""7"" ], ""hold_for_ms"": 500 } ] } ]
            }"));
            Assert.That(r.IsValid, Is.True);
            Assert.That(r.Issues.Any(i =>
                i.Severity == ValidationSeverity.Warning && i.Message.Contains("hold_for_ms")), Is.True);
        }

        [Test]
        public void Sound_NonObject_IsAnError()
        {
            var r = Validator.Validate(P(@"{
                ""name"": ""T"",
                ""macros"": [ { ""name"": ""M"", ""trigger"": ""F9"",
                                ""steps"": [ { ""sound"": ""beep.wav"" } ] } ]
            }"));
            Assert.That(HasError(r, "sound"), Is.True);
        }

        [Test]
        public void Settings_UnknownEngine_IsAnError()
        {
            var r = Validator.Validate(new Settings { InputEngine = "nasal" });
            Assert.That(r.IsValid, Is.False);
            Assert.That(HasError(r, "input_engine"), Is.True);
        }

        [Test]
        public void ViiperProfile_WithMouseButtonStep_IsAnError()
        {
            var r = Validator.Validate(P(@"{
                ""name"": ""T"", ""input_engine"": ""viiper"",
                ""macros"": [ { ""name"": ""M"", ""trigger"": ""F9"",
                                ""steps"": [ { ""button"": ""XButton1"" } ] } ]
            }"));
            Assert.That(r.IsValid, Is.False, "a viiper keyboard device cannot emit mouse buttons");
            Assert.That(HasError(r, "mouse buttons"), Is.True);
        }

        [Test]
        public void ViiperProfile_WithMouseKeyChord_IsAnError()
        {
            var r = Validator.Validate(P(@"{
                ""name"": ""T"", ""input_engine"": ""viiper"",
                ""macros"": [ { ""name"": ""M"", ""trigger"": ""F9"",
                                ""steps"": [ { ""keys"": ""Shift+RButton"" } ] } ]
            }"));
            Assert.That(r.IsValid, Is.False);
            Assert.That(HasError(r, "mouse button"), Is.True);
        }

        [Test]
        public void ViiperProfile_WithMouseInsideWrapper_IsAnError()
        {
            var r = Validator.Validate(P(@"{
                ""name"": ""T"", ""input_engine"": ""viiper"",
                ""wrappers"": { ""bar6"": { ""before"": [ { ""keys"": ""Ctrl+6"" } ],
                                             ""after"": [ { ""keys"": ""LButton"" } ] } },
                ""macros"": [ { ""name"": ""M"", ""trigger"": ""F9"",
                                ""steps"": [ { ""keys"": ""7"", ""wrapper"": ""bar6"" } ] } ]
            }"));
            Assert.That(r.IsValid, Is.False, "wrapper contents are output too");
            Assert.That(HasError(r, "mouse button"), Is.True);
        }

        [Test]
        public void ViiperProfile_WithKeyboardStepsOnly_IsValid()
        {
            var r = Validator.Validate(P(@"{
                ""name"": ""T"", ""input_engine"": ""viiper"",
                ""macros"": [ { ""name"": ""M"", ""trigger"": ""F9"",
                                ""steps"": [ { ""keys"": [ ""Ctrl+6"", ""7"" ] } ] } ]
            }"));
            Assert.That(r.IsValid, Is.True);
        }

        [Test]
        public void SendinputProfile_MouseStep_IsStillAllowed()
        {
            var r = Validator.Validate(P(@"{
                ""name"": ""T"", ""input_engine"": ""sendinput"",
                ""macros"": [ { ""name"": ""M"", ""trigger"": ""F9"",
                                ""steps"": [ { ""keys"": ""Shift+RButton"" } ] } ]
            }"));
            Assert.That(r.IsValid, Is.True);
        }

        [Test]
        public void AmbiguousWindowTargets_AreAWarning()
        {
            // Identical exe-only targets across two profiles: AUTO mode could not
            // pick between them → warning (never a load blocker).
            var r = Validator.ValidateAll(new[]
            {
                P(@"{ ""name"": ""A"", ""window"": { ""exe"": ""dndclient64"" }, ""macros"": [] }"),
                P(@"{ ""name"": ""B"", ""window"": { ""exe"": ""dndclient64"" }, ""macros"": [] }"),
            });
            Assert.That(r.IsValid, Is.True, "ambiguity is a warning, not a blocker");
            Assert.That(r.Issues.Any(i =>
                i.Severity == ValidationSeverity.Warning && i.Message.Contains("overlapping")), Is.True);

            // An exe-only profile overlaps any profile that could share its exe
            // (same-exe window with any title matches both).
            var partial = Validator.ValidateAll(new[]
            {
                P(@"{ ""name"": ""A"", ""window"": { ""exe"": ""dndclient64"" }, ""macros"": [] }"),
                P(@"{ ""name"": ""B"", ""window"": { ""exe"": ""dndclient64"", ""title"": ""*-Sarlona"" }, ""macros"": [] }"),
            });
            Assert.That(partial.Issues.Any(i => i.Message.Contains("overlapping")), Is.True);

            // Differing titles on the same exe are NOT ambiguous — the title rule
            // locks the match down per window.
            var distinct = Validator.ValidateAll(new[]
            {
                P(@"{ ""name"": ""A"", ""window"": { ""exe"": ""dndclient64"", ""title"": ""*-Sarlona"" }, ""macros"": [] }"),
                P(@"{ ""name"": ""B"", ""window"": { ""exe"": ""dndclient64"", ""title"": ""*-Khyber"" }, ""macros"": [] }"),
            });
            Assert.That(distinct.Issues, Is.Empty);

            // Different exes are never ambiguous.
            var separated = Validator.ValidateAll(new[]
            {
                P(@"{ ""name"": ""A"", ""window"": { ""exe"": ""dndclient64"" }, ""macros"": [] }"),
                P(@"{ ""name"": ""B"", ""window"": { ""exe"": ""game.exe"" }, ""macros"": [] }"),
            });
            Assert.That(separated.Issues, Is.Empty);

            // Manual-only profiles (no window target) are exempt.
            var noTargets = Validator.ValidateAll(new[]
            {
                P(@"{ ""name"": ""A"", ""macros"": [] }"),
                P(@"{ ""name"": ""B"", ""macros"": [] }"),
            });
            Assert.That(noTargets.Issues, Is.Empty);
        }
    }
}