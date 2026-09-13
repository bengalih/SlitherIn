using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SlitherIn.Core.Config;
using SlitherIn.Hook;

namespace SlitherIn.Input
{
    /// <summary>
    /// Default engine (always available). Diff-based <see cref="SetState"/>: between
    /// calls it computes the chords that went down/up and injects exactly those —
    /// modifiers press before their key and release after (fixed chord order, same
    /// as <see cref="Chord.ToString"/>); mouse-button tokens go out as MOUSEINPUT.
    /// A chord that is still held across two SetState calls is NOT re-injected.
    ///
    /// Input carries BOTH wVk and wScan (scan from MapVirtualKey) plus the
    /// extended-key flag for the extended set — the reference rule for games
    /// that read raw scancode input ("works in Notepad but not in the game").
    /// </summary>
    public sealed class SendInputEngine : IKeyEngine
    {
        public string Name => "sendinput";
        public bool Available => true;

        private readonly List<Chord> _held = new();

        public void Initialize()
        {
            // no-op — SendInput needs no device handshake.
        }

        public void SetState(IReadOnlyList<Chord> pressed)
        {
            if (pressed == null) throw new ArgumentNullException(nameof(pressed));

            foreach (Chord c in _held)
                if (!Contains(pressed, c)) UpChord(c);
            foreach (Chord c in pressed)
                if (!Contains(_held, c)) DownChord(c);

            _held.Clear();
            _held.AddRange(pressed);
        }

        public void ReleaseAll()
        {
            for (int i = _held.Count - 1; i >= 0; i--) UpChord(_held[i]);
            _held.Clear();
        }

        public void Dispose() => ReleaseAll();

        // ==== Injection ===========================================================

        private static void DownChord(Chord c)
        {
            foreach (ModifierFlags m in ModifierOrder(c.Modifiers)) SendKey(ModVk(m), down: true);
            if (KeyMapping.IsMouseToken(c.Key)) SendMouse(c.Key, down: true);
            else SendKey(KeyMapping.GetVirtualKey(c.Key), down: true);
        }

        private static void UpChord(Chord c)
        {
            if (KeyMapping.IsMouseToken(c.Key)) SendMouse(c.Key, down: false);
            else SendKey(KeyMapping.GetVirtualKey(c.Key), down: false);
            for (int i = ModifierOrder(c.Modifiers).Length - 1; i >= 0; i--)
                SendKey(ModVk(ModifierOrder(c.Modifiers)[i]), down: false);
        }

        /// <summary>Fixed chord modifier order — the same order ToString() joins.</summary>
        private static ModifierFlags[] ModifierOrder(ModifierFlags modifiers)
        {
            var list = new List<ModifierFlags>(4);
            if ((modifiers & ModifierFlags.Ctrl) != 0) list.Add(ModifierFlags.Ctrl);
            if ((modifiers & ModifierFlags.Alt) != 0) list.Add(ModifierFlags.Alt);
            if ((modifiers & ModifierFlags.Shift) != 0) list.Add(ModifierFlags.Shift);
            if ((modifiers & ModifierFlags.Win) != 0) list.Add(ModifierFlags.Win);
            return list.ToArray();
        }

        private static byte ModVk(ModifierFlags m) => m switch
        {
            ModifierFlags.Ctrl => KeyMapping.VK_CONTROL,
            ModifierFlags.Alt => KeyMapping.VK_MENU,
            ModifierFlags.Shift => KeyMapping.VK_SHIFT,
            ModifierFlags.Win => KeyMapping.VK_LWIN,
            _ => 0,
        };

        private static void SendKey(byte vk, bool down)
        {
            var input = new Win32.INPUT { type = Win32.INPUT_KEYBOARD };
            input.ki.wVk = vk;
            input.ki.wScan = KeyMapping.ScanCodeOf(vk);
            uint flags = down ? 0 : Win32.KEYEVENTF_KEYUP;
            if (IsExtended(vk)) flags |= Win32.KEYEVENTF_EXTENDEDKEY;
            input.ki.dwFlags = flags;
            Win32.SendInput(1, new[] { input }, Marshal.SizeOf(typeof(Win32.INPUT)));
        }

        /// <summary>Extended keys carry an 0xE0 scan prefix on a normal keyboard —
        /// they must be flagged so the injected event matches a real press
        /// (PgUp/PgDn/End/Home/arrows, Insert/Delete, right-side modifiers).</summary>
        private static bool IsExtended(byte vk) => vk switch
        {
            0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28
                or 0x2D or 0x2E or 0xA1 or 0xA3 or 0xA5 => true,
            _ => false,
        };

        private static void SendMouse(string token, bool down)
        {
            var input = new Win32.INPUT { type = Win32.INPUT_MOUSE };
            input.mi.dwFlags = MouseFlags(token, down);
            if (token == "XButton1") input.mi.mouseData = (uint)(1 << 16);
            if (token == "XButton2") input.mi.mouseData = (uint)(2 << 16);
            Win32.SendInput(1, new[] { input }, Marshal.SizeOf(typeof(Win32.INPUT)));
        }

        private static uint MouseFlags(string token, bool down) => token switch
        {
            "LButton" => down ? Win32.MOUSEEVENTF_LEFTDOWN : Win32.MOUSEEVENTF_LEFTUP,
            "RButton" => down ? Win32.MOUSEEVENTF_RIGHTDOWN : Win32.MOUSEEVENTF_RIGHTUP,
            "MButton" => down ? Win32.MOUSEEVENTF_MIDDLEDOWN : Win32.MOUSEEVENTF_MIDDLEUP,
            "XButton1" or "XButton2" => down ? Win32.MOUSEEVENTF_XDOWN : Win32.MOUSEEVENTF_XUP,
            _ => 0,
        };

        private static bool Contains(IReadOnlyList<Chord> list, Chord chord)
        {
            foreach (Chord c in list) if (c.Equals(chord)) return true;
            return false;
        }
    }
}