using System;
using System.Runtime.InteropServices;

namespace WinQRemapper
{
    internal partial class Program
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;

        private const int VK_Q = 0x51;
        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;
        private const int VK_MENU = 0x12; // Alt
        private const int VK_F4 = 0x73;

        private static IntPtr _hookID = IntPtr.Zero;

        static unsafe void Main()
        {
            // Install the Low-Level Keyboard Hook
            IntPtr moduleHandle = GetModuleHandleW(IntPtr.Zero);
            _hookID = SetWindowsHookExW(WH_KEYBOARD_LL, &HookCallback, moduleHandle, 0);

            // Minimal Message Loop to keep the thread alive with near-zero CPU usage
            while (GetMessageW(out MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }

            // Proper cleanup on exit
            UnhookWindowsHookEx(_hookID);
        }

        [UnmanagedCallersOnly]
        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN))
            {
                int vkCode = Marshal.ReadInt32(lParam);

                if (vkCode == VK_Q)
                {
                    bool lWinDown = (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0;
                    bool rWinDown = (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;

                    // If LWin or RWin is held and Q is pressed
                    if (lWinDown || rWinDown)
                    {
                        SimulateAltF4();
                        return (IntPtr)1; // Swallow the 'Q' instance
                    }
                }
            }
            // Chain to the next hook
            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        private static void SimulateAltF4()
        {
            const uint KEYEVENTF_KEYUP = 0x0002;
            keybd_event((byte)VK_MENU, 0, 0, 0); // Press Alt
            keybd_event((byte)VK_F4, 0, 0, 0); // Press F4
            keybd_event((byte)VK_F4, 0, KEYEVENTF_KEYUP, 0); // Release F4
            keybd_event((byte)VK_MENU, 0, KEYEVENTF_KEYUP, 0); // Release Alt
        }

        // --- P/Invokes (Optimized for Native AOT using LibraryImport) ---

        [LibraryImport("user32.dll")]
        private static unsafe partial IntPtr SetWindowsHookExW(
            int idHook,
            delegate* unmanaged<int, IntPtr, IntPtr, IntPtr> lpfn,
            IntPtr hMod,
            uint dwThreadId
        );

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool UnhookWindowsHookEx(IntPtr hhk);

        [LibraryImport("user32.dll")]
        private static partial IntPtr CallNextHookEx(
            IntPtr hhk,
            int nCode,
            IntPtr wParam,
            IntPtr lParam
        );

        [LibraryImport("kernel32.dll")]
        private static partial IntPtr GetModuleHandleW(IntPtr lpModuleName);

        [LibraryImport("user32.dll")]
        private static partial short GetAsyncKeyState(int vKey);

        [LibraryImport("user32.dll")]
        private static partial void keybd_event(
            byte bVk,
            byte bScan,
            uint dwFlags,
            nuint dwExtraInfo
        );

        [LibraryImport("user32.dll")]
        private static partial int GetMessageW(
            out MSG lpMsg,
            IntPtr hWnd,
            uint wMsgFilterMin,
            uint wMsgFilterMax
        );

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool TranslateMessage(ref MSG lpMsg);

        [LibraryImport("user32.dll")]
        private static partial IntPtr DispatchMessageW(ref MSG lpMsg);

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int x;
            public int y;
        }
    }
}
