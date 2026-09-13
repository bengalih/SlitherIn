using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using SlitherIn.Core.Config;
using SlitherIn.Core.Target;

namespace SlitherIn.Hook
{
    /// <summary>
    /// WH_KEYBOARD_LL + WH_MOUSE_LL event source. Installed on the UI thread so
    /// events arrive on the message pump thread. Pass-through is ALWAYS (return
    /// CallNextHookEx unchanged) — matching the locked "trigger passthrough"
    /// decision. Emits canonical <see cref="Chord"/>s via <see cref="HookModel"/>
    /// plus a foreground snapshot so consumers (App routing, key-finder log) never
    /// touch raw VKs or hwnds.
    ///
    /// Injected events are SKIPPED (LLKHF_/LLMHF_INJECTED): our own SendInput
    /// output must never be treated as user input or re-logged.
    /// </summary>
    public sealed class LowLevelHooks : IDisposable
    {
        public event EventHandler<InputEvent> Input;

        /// <summary>
        /// Fired on every real keyboard event where the modifier bitmap CHANGES
        /// (a modifier pressed, or released). Carries the NEW full bitmap. Modifier-only
        /// keys never produce a canonical chord event — this delta feed is how a held
        /// chord-trigger run learns that a member left the down-set (C-6), so release
        /// stops the run no matter the order the chord is dismantled.
        /// </summary>
        public event EventHandler<ModifierFlags> ModifierState;

        private IntPtr _keyboardHook;
        private IntPtr _mouseHook;
        private Win32.HookProc _keyboardProc;
        private Win32.HookProc _mouseProc;
        private ModifierFlags _lastMods;

        /// <summary>Keyboard hook health (the key-finder's critical path).</summary>
        public bool KeyboardInstalled => _keyboardHook != IntPtr.Zero;
        public bool MouseInstalled => _mouseHook != IntPtr.Zero;

        /// <summary>Install both hooks on the current (UI) thread. Returns whether
        /// the KEYBOARD hook landed — mouse failure is logged, never fatal.</summary>
        public bool Install()
        {
            using Process current = Process.GetCurrentProcess();
            IntPtr hMod = Win32.GetModuleHandle(current.MainModule.ModuleName);

            _keyboardProc = KeyboardProc;
            _mouseProc = MouseProc;

            _keyboardHook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _keyboardProc, hMod, 0);
            _mouseHook = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, _mouseProc, hMod, 0);
            return KeyboardInstalled;
        }

        private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                uint msg = (uint)wParam;
                if (msg == Win32.WM_KEYDOWN || msg == Win32.WM_KEYUP
                    || msg == Win32.WM_SYSKEYDOWN || msg == Win32.WM_SYSKEYUP)
                {
                    var kb = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
                    if ((kb.flags & Win32.LLKHF_INJECTED) == 0)
                    {
                        bool isDown = msg == Win32.WM_KEYDOWN || msg == Win32.WM_SYSKEYDOWN;
                        ModifierFlags mods = HookModel.ModifierBitmap((byte)kb.vkCode, isDown,
                            Mod(0x11), Mod(0x12), Mod(0x10), Mod(0x5B));
                        if (mods != _lastMods)
                        {
                            _lastMods = mods;
                            ModifierState?.Invoke(this, mods);
                        }

                        Chord? chord = HookModel.KeyboardChord((byte)kb.vkCode,
                            Mod(0x11), Mod(0x12), Mod(0x10), Mod(0x5B));
                        if (chord.HasValue)
                            Emit(new InputEvent(HookModel.KeyboardKind(msg), chord.Value, SnapshotForeground()));
                    }
                }
            }
            return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                uint msg = (uint)wParam;
                if (IsMouseButtonMessage(msg))
                {
                    var m = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);
                    if ((m.flags & Win32.LLMHF_INJECTED) == 0)
                    {
                        Chord? chord = HookModel.MouseChord(msg, m.mouseData,
                            Mod(0x11), Mod(0x12), Mod(0x10), Mod(0x5B));
                        if (chord.HasValue)
                            Emit(new InputEvent(HookModel.MouseKind(msg), chord.Value, SnapshotForeground()));
                    }
                }
            }
            return Win32.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        private static bool Mod(int vk) => (Win32.GetAsyncKeyState(vk) & 0x8000) != 0;

        private static bool IsMouseButtonMessage(uint msg)
            => msg == Win32.WM_LBUTTONDOWN || msg == Win32.WM_RBUTTONDOWN || msg == Win32.WM_MBUTTONDOWN
               || msg == Win32.WM_XBUTTONDOWN || msg == Win32.WM_LBUTTONUP || msg == Win32.WM_RBUTTONUP
               || msg == Win32.WM_MBUTTONUP || msg == Win32.WM_XBUTTONUP;

        private void Emit(InputEvent e) => Input?.Invoke(this, e);

        /// <summary>Snapshot of the current foreground window (exe + title). Public
        /// so the composition root can poll it for AUTO profile resolution; the
        /// hooks also capture it at event time.</summary>
        public static ForegroundInfo SnapshotForeground()
        {
            IntPtr hwnd = Win32.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return ForegroundInfo.Empty;

            var title = new StringBuilder(512);
            int len = Win32.GetWindowText(hwnd, title, title.Capacity);
            string titleText = len > 0 ? title.ToString().Trim() : null;

            uint pid = 0;
            Win32.GetWindowThreadProcessId(hwnd, out pid);
            return new ForegroundInfo(hwnd, ProcessExe(pid), titleText);
        }

        private static string ProcessExe(uint pid)
        {
            if (pid == 0) return null;
            IntPtr h = Win32.OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                uint size = (uint)sb.Capacity;
                return Win32.QueryFullProcessImageName(h, 0, sb, ref size)
                    ? Path.GetFileName(sb.ToString())
                    : null;
            }
            finally
            {
                Win32.CloseHandle(h);
            }
        }

        public void Dispose()
        {
            if (_keyboardHook != IntPtr.Zero) Win32.UnhookWindowsHookEx(_keyboardHook);
            if (_mouseHook != IntPtr.Zero) Win32.UnhookWindowsHookEx(_mouseHook);
            _keyboardHook = _mouseHook = IntPtr.Zero;
        }
    }
}