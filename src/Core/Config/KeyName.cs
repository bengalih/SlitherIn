using System;
using System.Collections.Generic;

namespace SlitherIn.Core.Config
{
    /// <summary>Modifier part of a chord.</summary>
    [Flags]
    public enum ModifierFlags
    {
        None = 0,
        Ctrl = 1,
        Alt = 2,
        Shift = 4,
        Win = 8,
    }

    /// <summary>
    /// A canonical chord, e.g. "Ctrl+K", "Shift+RButton", "LButton", "Numpad1".
    /// Immutable. This type is the engine's currency for detection, injection and
    /// logging — the ONLY allowed spellings come from <see cref="KeyName"/>.
    /// </summary>
    public readonly struct Chord : IEquatable<Chord>
    {
        public readonly ModifierFlags Modifiers;
        /// <summary>Canonical token: letter, "0"-"9" (top row), "Numpad0-9",
        /// "F1-F24", named keys, or a mouse button token.</summary>
        public readonly string Key;

        public Chord(ModifierFlags modifiers, string key)
        {
            Modifiers = modifiers;
            Key = key;
        }

        public override string ToString()
        {
            string mods = Modifiers == ModifierFlags.None ? "" : Modifiers.ToString().Replace(", ", "+") + "+";
            return mods + Key;
        }

        public bool Equals(Chord other) => Modifiers == other.Modifiers && Key == other.Key;
        public override bool Equals(object obj) => obj is Chord c && Equals(c);
        public override int GetHashCode() => (Modifiers.GetHashCode() * 397) ^ (Key?.GetHashCode() ?? 0);
    }

    /// <summary>
    /// Single source of truth for the key vocabulary and its parser. Every trigger,
    /// step key, abort combo, and log line flows through here so that "what you log
    /// is what you can paste into config" (the key-finder property).
    ///
    /// Rules locked in design: bare digits 1-9/0 = TOP-ROW only; numpad is explicit
    /// ("Numpad1"); mouse buttons = LButton/RButton/MButton/XButton1/XButton2;
    /// wheel SCROLL is not a key (no rotation tokens). The full vocabulary is
    /// single letters A-Z, digits (top row), F1-F24, and <see cref="NamedKeys"/>.
    ///
    /// Tokens are case-insensitive but OUTPUT is canonical ("ctrl+k" → "Ctrl+K");
    /// modifiers may come in any order and are re-joined in fixed order
    /// (Ctrl, Alt, Shift, Win). Rejections: any token outside the vocabulary, a
    /// second key token, a duplicate modifier, a trailing/leading '+', or a chord
    /// with no key at all.
    /// </summary>
    public static class KeyName
    {
        /// <summary>The named token table the parser accepts exactly (modifiers
        /// combined with '+' for a full chord). Letters/F-keys/mouse buttons:
        /// letters and F-keys are parsed structurally; the five mouse-button tokens
        /// are listed here for completeness.</summary>
        public static readonly string[] NamedKeys =
        {
            // mouse buttons
            "LButton", "RButton", "MButton", "XButton1", "XButton2",
            // digits — bare digits are top-row only (see parser)
            "0","1","2","3","4","5","6","7","8","9",
            // numpad — explicit only
            "Numpad0","Numpad1","Numpad2","Numpad3","Numpad4",
            "Numpad5","Numpad6","Numpad7","Numpad8","Numpad9",
            "NumpadAdd","NumpadSubtract","NumpadMultiply","NumpadDivide",
            // named keys
            "Space","Enter","Tab","Esc","Backspace","CapsLock","Pause",
            "Home","End","PageUp","PageDown","Insert","Delete",
            "Up","Down","Left","Right",
            "PrintScreen","ScrollLock","NumLock",
        };

        /// <summary>Parses a chord spec to the canonical form. Throws
        /// <see cref="KeyParseException"/> on any non-canonical spelling.</summary>
        public static Chord Parse(string spec) => TryParse(spec, out Chord chord)
            ? chord
            : throw new KeyParseException($"Unknown key token in '{spec ?? "<null>"}'.");

        /// <summary>Canonicalizing parser. True only for in-vocabulary chords;
        /// output chord is always in canonical spelling/order.</summary>
        public static bool TryParse(string spec, out Chord chord)
        {
            chord = default;
            if (string.IsNullOrWhiteSpace(spec)) return false;

            string[] parts = spec.Split('+');
            ModifierFlags modifiers = ModifierFlags.None;
            string key = null;

            for (int i = 0; i < parts.Length; i++)
            {
                string token = parts[i].Trim();
                if (token.Length == 0) return false;

                switch (token.ToLowerInvariant())
                {
                    case "ctrl":
                        if ((modifiers & ModifierFlags.Ctrl) != 0) return false;
                        modifiers |= ModifierFlags.Ctrl;
                        break;
                    case "alt":
                        if ((modifiers & ModifierFlags.Alt) != 0) return false;
                        modifiers |= ModifierFlags.Alt;
                        break;
                    case "shift":
                        if ((modifiers & ModifierFlags.Shift) != 0) return false;
                        modifiers |= ModifierFlags.Shift;
                        break;
                    case "win":
                        if ((modifiers & ModifierFlags.Win) != 0) return false;
                        modifiers |= ModifierFlags.Win;
                        break;
                    default:
                        if (key != null) return false;   // two key tokens
                        key = CanonicalKey(token);
                        if (key == null) return false;
                        break;
                }
            }

            if (key == null) return false;               // modifiers alone
            chord = new Chord(modifiers, key);
            return true;
        }

        /// <summary>Single-letter keys (A-Z), top-row digits, F1-F24, or a
        /// NamedKeys entry. Returns null for anything else.</summary>
        private static string CanonicalKey(string token)
        {
            if (token.Length == 1)
            {
                char c = token[0];
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))
                    return char.ToUpperInvariant(c).ToString();
                if (c >= '0' && c <= '9') return token; // top-row digits only
                return null;
            }

            if (token.Length >= 2 && (token[0] == 'f' || token[0] == 'F'))
            {
                if (int.TryParse(token.Substring(1), out int n) && n >= 1 && n <= 24)
                    return "F" + n;
                return null;                            // F0, F25, or "foo"
            }

            foreach (string name in NamedKeys)
                if (name.Equals(token, StringComparison.OrdinalIgnoreCase)) return name;
            return null;
        }

        /// <summary>Mouse-button tokens can never ride a keyboard report. The VIIPER
        /// backend is a HID keyboard device — such chords are dropped (and a viiper
        /// profile that declares them is rejected at validation).</summary>
        public static bool IsMouseToken(string canonicalKey)
            => canonicalKey == "LButton" || canonicalKey == "RButton" || canonicalKey == "MButton"
               || canonicalKey == "XButton1" || canonicalKey == "XButton2";
    }

    public sealed class KeyParseException : Exception
    {
        public KeyParseException(string message) : base(message) { }
    }
}