using System;
using SlitherIn.Core.Config;

namespace SlitherIn.Input
{
    /// <summary>
    /// Canonical chord token ⇄ USB-HID keyboard usage (and modifier byte) for the
    /// VIIPER backend. A HID keyboard report is [mod][count][usages...], where each
    /// usage is the key's position on the boot-keyboard usage table (not a VK —
    /// VIIPER streams the device's raw report, so Win32 mapping is irrelevant here).
    ///
    /// Mouse-button tokens have NO keyboard usage (they come from the mouse report,
    /// which a keyboard device cannot emit) → <see cref="UsageOf"/> returns 0 for
    /// them and the engine drops the chord. Numpad keys use the keypad column of
    /// the HID table, distinct from top-row digits, mirroring KeyMapping's split.
    /// </summary>
    internal static class HidKeyMap
    {
        /// <summary>HID usage of a canonical key token; 0 (the "no key" code) when
        /// the token is unmappable on a keyboard device (mouse buttons).</summary>
        public static byte UsageOf(string canonicalKey)
        {
            if (string.IsNullOrEmpty(canonicalKey)) return 0;

            if (canonicalKey.Length == 1)
            {
                char c = canonicalKey[0];
                if (c >= 'A' && c <= 'Z') return (byte)(0x04 + (c - 'A'));
                if (c >= '0' && c <= '9')
                    return c == '0' ? (byte)0x27 : (byte)(0x1E + (c - '1'));
                return 0;
            }

            if (canonicalKey[0] == 'F' && canonicalKey.Length > 1
                && int.TryParse(canonicalKey.Substring(1), out int f))
            {
                if (f >= 1 && f <= 12) return (byte)(0x3A + (f - 1));
                if (f >= 13 && f <= 24) return (byte)(0x68 + (f - 13));
                return 0;
            }

            return canonicalKey switch
            {
                "Space" => 0x2C, "Enter" => 0x28, "Tab" => 0x2B, "Esc" => 0x29,
                "Backspace" => 0x2A, "CapsLock" => 0x39, "Pause" => 0x48,
                "Home" => 0x4A, "End" => 0x4D, "PageUp" => 0x4B, "PageDown" => 0x4E,
                "Insert" => 0x49, "Delete" => 0x4C,
                "Up" => 0x52, "Down" => 0x51, "Left" => 0x50, "Right" => 0x4F,
                "PrintScreen" => 0x46, "ScrollLock" => 0x47, "NumLock" => 0x53,
                "Numpad1" => 0x59, "Numpad2" => 0x5A, "Numpad3" => 0x5B, "Numpad4" => 0x5C,
                "Numpad5" => 0x5D, "Numpad6" => 0x5E, "Numpad7" => 0x5F, "Numpad8" => 0x60,
                "Numpad9" => 0x61, "Numpad0" => 0x62,
                "NumpadDivide" => 0x54, "NumpadMultiply" => 0x55,
                "NumpadSubtract" => 0x56, "NumpadAdd" => 0x57,
                _ => 0,
            };
        }

        /// <summary>Modifier flags → HID modifier bitmap.
        /// NOTE: not the same bit order as <see cref="ModifierFlags"/> — HID puts
        /// Ctrl=0x01, Shift=0x02, Alt=0x04, Gui(Win)=0x08 (the old VIIPER protocol's
        /// MOD_LCONTROL/MOD_LSHIFT/MOD_LALT/MOD_LWIN). Mapped explicitly so Alt↔Shift
        /// never swap.</summary>
        public static byte Modifiers(ModifierFlags flags)
        {
            byte b = 0;
            if ((flags & ModifierFlags.Ctrl) != 0) b |= 0x01;
            if ((flags & ModifierFlags.Shift) != 0) b |= 0x02;
            if ((flags & ModifierFlags.Alt) != 0) b |= 0x04;
            if ((flags & ModifierFlags.Win) != 0) b |= 0x08;
            return b;
        }
    }
}