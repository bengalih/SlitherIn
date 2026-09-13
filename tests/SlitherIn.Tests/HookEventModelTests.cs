using NUnit.Framework;
using SlitherIn.Core.Config;
using SlitherIn.Core.Target;
using SlitherIn.Hook;

namespace SlitherIn.Tests
{
    /// <summary>Hook model: raw vkCode/mouse message + modifier state → canonical
    /// chord + event kind; unknown/modifier-only keys produce NO event; the
    /// foreground snapshot rides along on the InputEvent. Stage 6.</summary>
    [TestFixture]
    public class HookEventModelTests
    {
        [Test]
        public void KeyDown_CtrlK_YieldsCanonicalChord()
        {
            Chord? chord = HookModel.KeyboardChord(0x4B, ctrl: true, alt: false, shift: false, win: false);
            Assert.That(chord, Is.Not.Null);
            Assert.That(chord.Value.ToString(), Is.EqualTo("Ctrl+K"));
        }

        [Test]
        public void Numpad_And_TopRow_YieldDistinctChords()
        {
            Assert.That(HookModel.KeyboardChord(0x31, false, false, false, false).Value.ToString(), Is.EqualTo("1"));
            Assert.That(HookModel.KeyboardChord(0x61, false, false, false, false).Value.ToString(), Is.EqualTo("Numpad1"));
        }

        [Test]
        public void SysKey_AltF4_YieldsAltModifierChord()
        {
            // Hook reports the non-Alt vk during Alt+F4; Alt rides in the chord.
            Chord? chord = HookModel.KeyboardChord(0x73 /* F4 */, false, alt: true, false, false);
            Assert.That(chord.Value.ToString(), Is.EqualTo("Alt+F4"));
        }

        [Test]
        public void ModifierOnlyOrUnknownVk_YieldsNoEvent()
        {
            Assert.That(HookModel.KeyboardChord(0x11 /* Ctrl */, false, false, false, false), Is.Null,
                "a lone modifier is not a chord");
            Assert.That(HookModel.KeyboardChord(0xFF, false, false, false, false), Is.Null,
                "unknown key must not break routing");
            Assert.That(HookModel.KeyboardChord(0x4B, true, false, false, false), Is.Not.Null);
        }

        [Test]
        public void MouseButtonMessages_YieldButtonChords()
        {
            Assert.That(HookModel.MouseChord(Win32.WM_LBUTTONDOWN, 0, false, false, false, false).Value.ToString(), Is.EqualTo("LButton"));
            Assert.That(HookModel.MouseChord(Win32.WM_RBUTTONUP, 0, false, false, false, false).Value.ToString(), Is.EqualTo("RButton"));

            // XButton number rides in the high word of mouseData.
            Assert.That(HookModel.MouseChord(Win32.WM_XBUTTONDOWN, 0x00010000, false, false, false, false).Value.ToString(), Is.EqualTo("XButton1"));
            Assert.That(HookModel.MouseChord(Win32.WM_XBUTTONDOWN, 0x00020000, false, false, false, false).Value.ToString(), Is.EqualTo("XButton2"));
            Assert.That(HookModel.MouseChord(Win32.WM_XBUTTONDOWN, 0x00020000, false, false, shift: true, false).Value.ToString(), Is.EqualTo("Shift+XButton2"));
        }

        [Test]
        public void Modifiers_OfEveryKind_Combine()
        {
            var chord = HookModel.KeyboardChord(0x4B, ctrl: true, alt: true, shift: true, win: true);
            Assert.That(chord.Value.ToString(), Is.EqualTo("Ctrl+Alt+Shift+Win+K"));
        }

        [Test]
        public void EventKinds_AreMappedFromMessages()
        {
            Assert.That(HookModel.KeyboardKind(Win32.WM_KEYDOWN), Is.EqualTo(InputEventKind.KeyDown));
            Assert.That(HookModel.KeyboardKind(Win32.WM_KEYUP), Is.EqualTo(InputEventKind.KeyUp));
            Assert.That(HookModel.KeyboardKind(Win32.WM_SYSKEYDOWN), Is.EqualTo(InputEventKind.SysKeyDown));
            Assert.That(HookModel.KeyboardKind(Win32.WM_SYSKEYUP), Is.EqualTo(InputEventKind.SysKeyUp));
            Assert.That(HookModel.MouseKind(Win32.WM_LBUTTONDOWN), Is.EqualTo(InputEventKind.MouseDown));
            Assert.That(HookModel.MouseKind(Win32.WM_RBUTTONUP), Is.EqualTo(InputEventKind.MouseUp));
        }

        [Test]
        public void InputEvent_CarriesForegroundSnapshot()
        {
            var foreground = new ForegroundInfo(new System.IntPtr(1), "dndclient64", "Bob-Sarlona");
            var e = new InputEvent(InputEventKind.KeyDown, KeyName.Parse("F9"), foreground);
            Assert.That(e.IsKeyDown, Is.True);
            Assert.That(e.Chord.ToString(), Is.EqualTo("F9"));
            Assert.That(e.Foreground.ProcessExe, Is.EqualTo("dndclient64"));
            Assert.That(e.Foreground.Title, Is.EqualTo("Bob-Sarlona"));
        }

        // ==== C-6: modifier-bitmap feed ============================================

        [Test]
        public void ModifierBitmap_OwnKeyDown_FoldsTheModifierIn()
        {
            // Ctrl-down while no other modifier is sampled → Ctrl alone.
            ModifierFlags m = HookModel.ModifierBitmap(0x11 /* Ctrl */, isDown: true,
                ctrl: false, alt: false, shift: false, win: false);
            Assert.That(m, Is.EqualTo(ModifierFlags.Ctrl));
        }

        [Test]
        public void ModifierBitmap_OwnKeyUp_FoldsTheModifierOut()
        {
            // Ctrl-up: the in-flight transition may still sample Ctrl as down, but
            // the event's own up MUST win → the bitmap drops Ctrl.
            ModifierFlags m = HookModel.ModifierBitmap(0x11 /* Ctrl */, isDown: false,
                ctrl: true, alt: true, shift: false, win: false);
            Assert.That(m, Is.EqualTo(ModifierFlags.Alt));
        }

        [Test]
        public void ModifierBitmap_NonModifierKey_TrustsTheSamples()
        {
            // A letter key neither adds nor removes; the sampled bits stand.
            ModifierFlags m = HookModel.ModifierBitmap(0x4B /* K */, isDown: true,
                ctrl: true, alt: false, shift: false, win: true);
            Assert.That(m, Is.EqualTo(ModifierFlags.Ctrl | ModifierFlags.Win));
        }

        [Test]
        public void ModifierFlagOf_MapsOnlyModifierVks()
        {
            Assert.That(HookModel.ModifierFlagOf(0x11), Is.EqualTo(ModifierFlags.Ctrl));
            Assert.That(HookModel.ModifierFlagOf(0x12), Is.EqualTo(ModifierFlags.Alt));
            Assert.That(HookModel.ModifierFlagOf(0x10), Is.EqualTo(ModifierFlags.Shift));
            Assert.That(HookModel.ModifierFlagOf(0x5B), Is.EqualTo(ModifierFlags.Win));
            Assert.That(HookModel.ModifierFlagOf(0x4B), Is.EqualTo(ModifierFlags.None));
        }
    }
}