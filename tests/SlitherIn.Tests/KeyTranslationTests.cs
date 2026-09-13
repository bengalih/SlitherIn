using System.Collections.Generic;
using NUnit.Framework;
using SlitherIn.Core.Config;
using SlitherIn.Input;

namespace SlitherIn.Tests
{
    /// <summary>Canonical vocabulary → Win32 translation: every token has a virtual
    /// key, round-trips canonically, numpad ≠ top-row, mouse buttons map, unknowns
    /// are rejected. Stage 6.</summary>
    [TestFixture]
    public class KeyTranslationTests
    {
        [Test]
        public void EveryCanonicalToken_Translates_AndRoundTrips()
        {
            foreach (string token in AllTokens())
            {
                Assert.That(KeyMapping.TryGetVirtualKey(token, out byte vk), Is.True, "no VK for " + token);
                Assert.That(vk, Is.Not.EqualTo(0), "VK for " + token + " must not be 0");
                Assert.That(KeyMapping.TryGetCanonical(vk, out string back), Is.True, "no reverse for " + token);
                Assert.That(back, Is.EqualTo(token), "round-trip must be canonical-stable");
            }
        }

        [Test]
        public void TopRowDigit_And_Numpad_AreDistinct()
        {
            Assert.That(KeyMapping.GetVirtualKey("1"), Is.EqualTo(0x31), "top-row 1 = VK 0x31");
            Assert.That(KeyMapping.GetVirtualKey("Numpad1"), Is.EqualTo(0x61), "numpad 1 = VK_NUMPAD1 0x61");
            Assert.That(KeyMapping.TryGetCanonical(0x31, out string top), Is.True);
            Assert.That(top, Is.EqualTo("1"));
            Assert.That(KeyMapping.TryGetCanonical(0x61, out string pad), Is.True);
            Assert.That(pad, Is.EqualTo("Numpad1"));

            byte topScan = KeyMapping.ScanCodeOf(0x31);
            byte padScan = KeyMapping.ScanCodeOf(0x61);
            Assert.That(topScan, Is.Not.EqualTo(0));
            Assert.That(padScan, Is.Not.EqualTo(0));
            Assert.That(topScan, Is.Not.EqualTo(padScan), "scan codes must differ: top-row & numpad are separate keys");
        }

        [Test]
        public void Letters_And_FunctionKeys_Map()
        {
            Assert.That(KeyMapping.GetVirtualKey("A"), Is.EqualTo(0x41));
            Assert.That(KeyMapping.GetVirtualKey("Z"), Is.EqualTo(0x5A));
            Assert.That(KeyMapping.GetVirtualKey("F1"), Is.EqualTo(0x70));
            Assert.That(KeyMapping.GetVirtualKey("F9"), Is.EqualTo(0x78));
            Assert.That(KeyMapping.GetVirtualKey("F12"), Is.EqualTo(0x7B));
        }

        [Test]
        public void ExtendedKeys_HaveScancodes_ForWScanFilledInjection()
        {
            // C-3: SendKey now fills wScan for every key; the extended set must
            // resolve to a real scancode so games reading raw scancode input work.
            byte[] extendedVks = { 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, 0xA1, 0xA3, 0xA5 };
            foreach (byte vk in extendedVks)
                Assert.That(KeyMapping.ScanCodeOf(vk), Is.Not.EqualTo(0), $"extended VK 0x{vk:X2} must have a scancode");
        }

        [Test]
        public void MouseButtons_MapToMouseVirtualKeys()
        {
            Assert.That(KeyMapping.GetVirtualKey("LButton"), Is.EqualTo(0x01));
            Assert.That(KeyMapping.GetVirtualKey("RButton"), Is.EqualTo(0x02));
            Assert.That(KeyMapping.GetVirtualKey("MButton"), Is.EqualTo(0x04));
            Assert.That(KeyMapping.GetVirtualKey("XButton1"), Is.EqualTo(0x05));
            Assert.That(KeyMapping.GetVirtualKey("XButton2"), Is.EqualTo(0x06));
            Assert.That(KeyMapping.IsMouseToken("XButton2"), Is.True);
            Assert.That(KeyMapping.IsMouseToken("K"), Is.False);
        }

        [Test]
        public void ModifierKeys_AreDetectedByVk_NotChords()
        {
            Assert.That(KeyMapping.IsModifier(0x11), Is.True, "Ctrl");
            Assert.That(KeyMapping.IsModifier(0x12), Is.True, "Alt");
            Assert.That(KeyMapping.IsModifier(0x10), Is.True, "Shift");
            Assert.That(KeyMapping.IsModifier(0x5B), Is.True, "Win");
            Assert.That(KeyMapping.IsModifier(0x41), Is.False, "A is a key, not a modifier");
            Assert.That(KeyMapping.TryGetCanonical(0x11, out _), Is.False, "a modifier alone is not a chord token");
        }

        [Test]
        public void UnknownTokens_AreRejected()
        {
            Assert.That(KeyMapping.TryGetVirtualKey("Nope", out _), Is.False);
            Assert.That(KeyMapping.TryGetVirtualKey("F25", out _), Is.False);
            Assert.That(KeyMapping.TryGetVirtualKey("Key7", out _), Is.False);
            Assert.That(KeyMapping.TryGetVirtualKey("", out _), Is.False);
            Assert.That(KeyMapping.TryGetCanonical(0x88 /* F24 ends at 0x87 */, out _), Is.False);
        }

        private static IEnumerable<string> AllTokens()
        {
            foreach (string token in KeyName.NamedKeys) yield return token;
            for (char c = 'A'; c <= 'Z'; c++) yield return c.ToString();
            for (int n = 1; n <= 24; n++) yield return "F" + n;
        }
    }
}