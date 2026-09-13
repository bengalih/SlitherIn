using System;
using System.Collections.Generic;
using SlitherIn.Core.Config;
using SlitherIn.Hook;

namespace SlitherIn.Input
{
    /// <summary>
    /// Canonical chord vocabulary ⇄ Win32 translation. One table for BOTH
    /// directions so detection (hook: vkCode → canonical) and injection
    /// (canonical → SendInput vkCode/scancode) can never disagree — "what you
    /// detect is what you can inject" symmetry. Pure + unit-tested
    /// (<see cref="KeyTranslationTests"/>).
    ///
    /// Modifiers are not keys: Ctrl/Alt/Shift/Win live in the chord's modifier
    /// flags, never as the key token. Their VKs are exposed only so the engine can
    /// press/release them around a chord.
    /// </summary>
    public static class KeyMapping
    {
        // Modifier VKs (generic form — a low-level hook always reports these).
        public const byte VK_CONTROL = 0x11;
        public const byte VK_MENU = 0x12;    // Alt
        public const byte VK_SHIFT = 0x10;
        public const byte VK_LWIN = 0x5B;    // Win

        public const byte VK_LBUTTON = 0x01;
        public const byte VK_RBUTTON = 0x02;
        public const byte VK_MBUTTON = 0x04;
        public const byte VK_XBUTTON1 = 0x05;
        public const byte VK_XBUTTON2 = 0x06;

        private static readonly Dictionary<string, byte> ByToken;
        private static readonly Dictionary<byte, string> ByVk;

        static KeyMapping()
        {
            ByToken = new Dictionary<string, byte>(KeyName.NamedKeys.Length + 26 + 10 + 24, StringComparer.OrdinalIgnoreCase);
            ByVk = new Dictionary<byte, string>();

            letter('A', 0x41, 26);
            digits(0x30);
            FuncKeys(24);
            foreach (string token in KeyName.NamedKeys)
                if (!ByToken.ContainsKey(token)) Add(token, VkOf(token));
        }

        private static void letter(char start, byte vkBase, int count)
        {
            for (int i = 0; i < count; i++)
                Add(((char)(start + i)).ToString(), (byte)(vkBase + i));
        }

        private static void digits(byte vkBase)
        {
            for (int i = 0; i < 10; i++) Add(i.ToString(), (byte)(vkBase + i));
        }

        private static void FuncKeys(int count)
        {
            for (int n = 1; n <= count; n++) Add("F" + n, (byte)(0x70 + n - 1));
        }

        private static byte VkOf(string token) => token switch
        {
            "LButton" => VK_LBUTTON,
            "RButton" => VK_RBUTTON,
            "MButton" => VK_MBUTTON,
            "XButton1" => VK_XBUTTON1,
            "XButton2" => VK_XBUTTON2,
            "Numpad0" => 0x60, "Numpad1" => 0x61, "Numpad2" => 0x62, "Numpad3" => 0x63, "Numpad4" => 0x64,
            "Numpad5" => 0x65, "Numpad6" => 0x66, "Numpad7" => 0x67, "Numpad8" => 0x68, "Numpad9" => 0x69,
            "NumpadMultiply" => 0x6A, "NumpadAdd" => 0x6B,
            "NumpadSubtract" => 0x6D, "NumpadDivide" => 0x6F,
            "Space" => 0x20, "Enter" => 0x0D, "Tab" => 0x09, "Esc" => 0x1B, "Backspace" => 0x08,
            "CapsLock" => 0x14, "Pause" => 0x13, "PrintScreen" => 0x2C,
            "Home" => 0x24, "End" => 0x23, "PageUp" => 0x21, "PageDown" => 0x22,
            "Insert" => 0x2D, "Delete" => 0x2E,
            "Up" => 0x26, "Down" => 0x28, "Left" => 0x25, "Right" => 0x27,
            "ScrollLock" => 0x91, "NumLock" => 0x90,
            _ => 0,
        };

        private static void Add(string token, byte vk)
        {
            ByToken[token] = vk;
            ByVk[vk] = token;
        }

        /// <summary>True when the canonical token has a Win32 virtual key.</summary>
        public static bool TryGetVirtualKey(string canonicalKey, out byte vk)
        {
            vk = 0;
            if (string.IsNullOrEmpty(canonicalKey)) return false;
            return ByToken.TryGetValue(canonicalKey, out vk);
        }

        public static byte GetVirtualKey(string canonicalKey)
        {
            if (!TryGetVirtualKey(canonicalKey, out byte vk))
                throw new ArgumentException($"'{canonicalKey}' is not in the key vocabulary.");
            return vk;
        }

        /// <summary>Reverse: a virtual key's canonical token, if it is one of ours.</summary>
        public static bool TryGetCanonical(byte vk, out string canonical)
        {
            canonical = null;
            return ByVk.TryGetValue(vk, out canonical);
        }

        public static byte ScanCodeOf(byte vk) => (byte)Win32.MapVirtualKey(vk, Win32.MAPVK_VK_TO_VSC);

        /// <summary>Ctrl/Alt/Shift/Win are never chord KEYS — only hook-time state.</summary>
        public static bool IsModifier(byte vk)
            => vk == VK_CONTROL || vk == VK_MENU || vk == VK_SHIFT || vk == VK_LWIN;

        /// <summary>Mouse-button tokens are injected as MOUSEINPUT, never keyboard.</summary>
        public static bool IsMouseToken(string canonicalKey) => KeyName.IsMouseToken(canonicalKey);
    }
}