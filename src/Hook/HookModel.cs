using SlitherIn.Core.Config;
using SlitherIn.Input;

namespace SlitherIn.Hook
{
    /// <summary>
    /// PURE event-model: turns raw hook data (vkCode / mouse wParam + data) plus
    /// the modifier state into a canonical <see cref="Chord"/> and event kind.
    /// No P/Invoke, no window access — fully unit-tested
    /// (<see cref="HookEventModelTests"/>). <see cref="LowLevelHooks"/> reads the
    /// kernel structs, snapshots the foreground, and wraps the result in an
    /// <see cref="InputEvent"/>.
    ///
    /// Modifier-only key presses and keys outside the vocabulary produce NO event:
    /// a lone Ctrl is not a chord, and unknown keys must not break routing.
    /// </summary>
    public static class HookModel
    {
        /// <summary>Canonical chord for a keyboard event, or null if the vk is a
        /// modifier or outside the vocabulary.</summary>
        public static Chord? KeyboardChord(byte vkCode, bool ctrl, bool alt, bool shift, bool win)
        {
            if (KeyMapping.IsModifier(vkCode)) return null;
            if (!KeyMapping.TryGetCanonical(vkCode, out string key)) return null;
            return new Chord(Mods(ctrl, alt, shift, win), key);
        }

        /// <summary>
        /// Full modifier bitmap AFTER the raw keyboard event, given the sampled
        /// states for what's on the wire. When the event's own vk IS a modifier,
        /// its authoritative down/up folds in (the transition in flight may not be
        /// visible in GetAsyncKeyState yet); otherwise the sample stands. This is
        /// the chord-release feed (C-6): modifier-only keys never produce a chord
        /// event, but their state DELTAS are how a run releasing its modifiers gets
        /// stopped regardless of release order.
        /// </summary>
        public static ModifierFlags ModifierBitmap(byte vkCode, bool isDown, bool ctrl, bool alt, bool shift, bool win)
        {
            ModifierFlags m = Mods(ctrl, alt, shift, win);
            if (KeyMapping.IsModifier(vkCode))
            {
                ModifierFlags self = ModifierFlagOf(vkCode);
                m = isDown ? m | self : m & ~self;
            }
            return m;
        }

        /// <summary>The single flag for a modifier vk; <see cref="ModifierFlags.None"/>
        /// for anything that is not a modifier.</summary>
        public static ModifierFlags ModifierFlagOf(byte vkCode) => vkCode switch
        {
            KeyMapping.VK_CONTROL => ModifierFlags.Ctrl,
            KeyMapping.VK_MENU => ModifierFlags.Alt,
            KeyMapping.VK_SHIFT => ModifierFlags.Shift,
            KeyMapping.VK_LWIN => ModifierFlags.Win,
            _ => ModifierFlags.None,
        };

        /// <summary>Canonical chord for a mouse-button event, or null if the message
        /// is not a button down/up we model. XButton number comes from the high word
        /// of <paramref name="mouseData"/>.</summary>
        public static Chord? MouseChord(uint wParam, uint mouseData, bool ctrl, bool alt, bool shift, bool win)
        {
            string key = wParam switch
            {
                Win32.WM_LBUTTONDOWN or Win32.WM_LBUTTONUP => "LButton",
                Win32.WM_RBUTTONDOWN or Win32.WM_RBUTTONUP => "RButton",
                Win32.WM_MBUTTONDOWN or Win32.WM_MBUTTONUP => "MButton",
                Win32.WM_XBUTTONDOWN or Win32.WM_XBUTTONUP => (mouseData >> 16) == 2 ? "XButton2" : "XButton1",
                _ => null,
            };
            if (key == null) return null;
            return new Chord(Mods(ctrl, alt, shift, win), key);
        }

        public static InputEventKind KeyboardKind(uint wParam) => wParam switch
        {
            Win32.WM_KEYDOWN => InputEventKind.KeyDown,
            Win32.WM_KEYUP => InputEventKind.KeyUp,
            Win32.WM_SYSKEYDOWN => InputEventKind.SysKeyDown,
            Win32.WM_SYSKEYUP => InputEventKind.SysKeyUp,
            _ => throw new System.ArgumentOutOfRangeException(nameof(wParam), "not a keyboard message"),
        };

        public static InputEventKind MouseKind(uint wParam) => wParam switch
        {
            Win32.WM_LBUTTONDOWN or Win32.WM_RBUTTONDOWN or Win32.WM_MBUTTONDOWN or Win32.WM_XBUTTONDOWN
                => InputEventKind.MouseDown,
            Win32.WM_LBUTTONUP or Win32.WM_RBUTTONUP or Win32.WM_MBUTTONUP or Win32.WM_XBUTTONUP
                => InputEventKind.MouseUp,
            _ => throw new System.ArgumentOutOfRangeException(nameof(wParam), "not a mouse-button message"),
        };

        private static ModifierFlags Mods(bool ctrl, bool alt, bool shift, bool win)
        {
            ModifierFlags m = ModifierFlags.None;
            if (ctrl) m |= ModifierFlags.Ctrl;
            if (alt) m |= ModifierFlags.Alt;
            if (shift) m |= ModifierFlags.Shift;
            if (win) m |= ModifierFlags.Win;
            return m;
        }
    }
}