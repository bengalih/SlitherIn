using System;
using System.Runtime.InteropServices;
using System.Text;

namespace SlitherIn.Hook
{
    /// <summary>
    /// Every P/Invoke in one file. The low-level hooks are the SINGLE detection
    /// path (triggers, toggles, abort, kill) — no RegisterHotKey, no poll
    /// watchdog, exactly as decided.
    /// </summary>
    public static class Win32
    {
        // ---- low-level hooks ----
        public const int WH_KEYBOARD_LL = 13;
        public const int WH_MOUSE_LL = 14;
        public const int HC_ACTION = 0;

        // ---- hook event params (WM_ codes beyond those listed are untapped; hook
        //      handlers filter on these) ----
        public const int WM_KEYDOWN = 0x0100;
        public const int WM_KEYUP = 0x0101;
        public const int WM_SYSKEYDOWN = 0x0104;
        public const int WM_SYSKEYUP = 0x0105;
        public const int WM_LBUTTONDOWN = 0x0201; public const int WM_LBUTTONUP = 0x0202;
        public const int WM_RBUTTONDOWN = 0x0204; public const int WM_RBUTTONUP = 0x0205;
        public const int WM_MBUTTONDOWN = 0x0207; public const int WM_MBUTTONUP = 0x0208;
        public const int WM_XBUTTONDOWN = 0x020B; public const int WM_XBUTTONUP = 0x020C;
        public const int WM_MOUSEMOVE = 0x0200;
        public const int WM_MOUSEWHEEL = 0x020A;

        // ---- LLKHF_/LLMHF_ event flags ----
        /// <summary>Alt is down for this keyboard event.</summary>
        public const uint LLKHF_ALTDOWN = 0x20;
        /// <summary>Keyboard event was injected by a process (never treat as user input).</summary>
        public const uint LLKHF_INJECTED = 0x10;
        /// <summary>Mouse event was injected by a process.</summary>
        public const uint LLMHF_INJECTED = 0x01;

        // ---- SendInput input system ----
        public const uint INPUT_MOUSE = 0;
        public const uint INPUT_KEYBOARD = 1;
        public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002; public const uint MOUSEEVENTF_LEFTUP = 0x0004;
        public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008; public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020; public const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        public const uint MOUSEEVENTF_XDOWN = 0x0080; public const uint MOUSEEVENTF_XUP = 0x0100;
        public const uint MAPVK_VK_TO_VSC = 0;

        public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT
        {
            public uint vkCode; public uint scanCode; public uint flags;
            public uint time; public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT
        {
            public int ptX; public int ptY; public uint mouseData;
            public uint flags; public uint time; public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk; public ushort wScan; public uint dwFlags;
            public uint time; public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx; public int dy; public uint mouseData; public uint dwFlags;
            public uint time; public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HARDWAREINPUT
        {
            public uint uMsg; public ushort wParamL; public ushort wParamH;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct INPUT
        {
            [FieldOffset(0)] public uint type;
            [FieldOffset(8)] public KEYBDINPUT ki;
            [FieldOffset(8)] public MOUSEINPUT mi;
            [FieldOffset(8)] public HARDWAREINPUT hi;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        /// <summary>High bit set = key is currently down (used ONLY to build the
        /// modifier state of a hook event — not a hold watchdog).</summary>
        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        /// <summary>VK → hardware scan code (uMapType 0 = VK_TO_VSC).</summary>
        [DllImport("user32.dll")]
        public static extern uint MapVirtualKey(uint uCode, uint uMapType);

        // ---- SendInput injection ----
        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] INPUT[] pInputs, int cbSize);

        // ---- module handle for SetWindowsHookEx on 32/64-bit (hooks run in-proc) ----
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);

        // ---- process identity for the foreground snapshot (exe name) ----
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern bool QueryFullProcessImageName(IntPtr hProcess, uint flags, StringBuilder exe, ref uint size);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr handle);
    }
}