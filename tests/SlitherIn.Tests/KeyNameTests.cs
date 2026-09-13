using NUnit.Framework;
using SlitherIn.Core.Config;

namespace SlitherIn.Tests
{
    /// <summary>Canonical chord parser (Stage 1 — real, green). Locked semantics:
    ///   letters A-Z, digits 0-9 (TOP-ROW), F1-F24, NamedKeys table;
    ///   mouse buttons LButton/RButton/MButton/XButton1/XButton2;
    ///   modifiers Ctrl/Alt/Shift/Win joined with '+';
    ///   case-insensitive input, CANONICAL output, fixed modifier order.</summary>
    [TestFixture]
    public class KeyNameTests
    {
        [Test]
        public void Vocabulary_Parses_EveryNamedKey()
        {
            foreach (string name in KeyName.NamedKeys)
                Assert.That(KeyName.Parse(name).Key, Is.EqualTo(name), $"'{name}' must parse to itself");
        }

        [TestCase("K", ModifierFlags.None, "K")]
        [TestCase("k", ModifierFlags.None, "K")]                 // canonicalized
        [TestCase("1", ModifierFlags.None, "1")]                 // top-row, NOT Numpad1
        [TestCase("Numpad1", ModifierFlags.None, "Numpad1")]
        [TestCase("numpad9", ModifierFlags.None, "Numpad9")]     // canonicalized
        [TestCase("LButton", ModifierFlags.None, "LButton")]
        [TestCase("XButton1", ModifierFlags.None, "XButton1")]
        [TestCase("F24", ModifierFlags.None, "F24")]
        [TestCase("f5", ModifierFlags.None, "F5")]               // canonicalized
        [TestCase("Space", ModifierFlags.None, "Space")]
        [TestCase("Ctrl+K", ModifierFlags.Ctrl, "K")]
        [TestCase("ctrl+k", ModifierFlags.Ctrl, "K")]            // canonicalized
        [TestCase("Alt+Shift+F5", ModifierFlags.Alt | ModifierFlags.Shift, "F5")]
        [TestCase("shift+ctrl+k", ModifierFlags.Ctrl | ModifierFlags.Shift, "K")] // reordered
        [TestCase("Ctrl+Numpad1", ModifierFlags.Ctrl, "Numpad1")]
        public void Parses_CanonicalChord(string spec, ModifierFlags mods, string key)
        {
            Chord c = KeyName.Parse(spec);
            Assert.That(c.Key, Is.EqualTo(key));
            Assert.That(c.Modifiers, Is.EqualTo(mods));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("Control+K")]       // non-canonical modifier
        [TestCase("Ctl+K")]
        [TestCase("Ctrl+K+J")]        // two key tokens
        [TestCase("K+1")]
        [TestCase("Ctrl+Ctrl+K")]     // duplicate modifier
        [TestCase("Ctrl+")]           // trailing '+'
        [TestCase("+K")]
        [TestCase("Ctrl++K")]         // empty token
        [TestCase("F0")]
        [TestCase("F25")]
        [TestCase("foo")]             // F-lookalike garbage
        [TestCase("'")]
        [TestCase("Mouse1")]          // non-canonical button
        [TestCase("Shift")]           // modifier alone, no key
        [TestCase("LButton+LButton")]
        public void Rejects_NonCanonical(string spec)
        {
            Assert.That(KeyName.TryParse(spec, out _), Is.False, $"'{spec}' must be rejected");
        }

        [Test]
        public void Parse_Throws_OnInvalidSpec()
        {
            Assert.Throws<KeyParseException>(() => KeyName.Parse("Control+K"));
        }

        [Test]
        public void ToString_IsCanonical_PlusJoined_FixedOrder()
        {
            Assert.That(KeyName.Parse("Ctrl+K").ToString(), Is.EqualTo("Ctrl+K"));
            Assert.That(KeyName.Parse("shift+ctrl+k").ToString(), Is.EqualTo("Ctrl+Shift+K"));
            Assert.That(KeyName.Parse("Alt+Ctrl+K").ToString(), Is.EqualTo("Ctrl+Alt+K"));
            Assert.That(KeyName.Parse("Win+Shift+Ctrl+Alt+Enter").ToString(), Is.EqualTo("Ctrl+Alt+Shift+Win+Enter"));
            Assert.That(KeyName.Parse("Enter").ToString(), Is.EqualTo("Enter"));
        }
    }
}