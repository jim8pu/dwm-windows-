using System;
using System.Runtime.InteropServices;
using System.Text;

namespace TaskbarKillerNative;

// =============================================================================
// A zero-dependency, zero-polling, zero-GUI taskbar killer.
//
// How it works:
//   1. Finds Shell_TrayWnd / Shell_SecondaryTrayWnd windows
//   2. Sets AppBar state to AutoHide (reclaims screen edge space)
//   3. Hides them with ShowWindow(SW_HIDE)
//   4. Installs a WinEvent hook — if Windows ever re-shows them, hides again
//   5. Runs a bare Win32 message loop (required for WinEvent callbacks)
//   6. On Ctrl+C / console close → restores taskbar and exits cleanly
// =============================================================================

internal static class Program
{
    // ── Win32 Constants ─────────────────────────────────────────────
    const int SW_HIDE = 0;
    const int SW_SHOWNORMAL = 1;
    const uint ABM_SETSTATE = 0x0000000A;
    const int ABS_AUTOHIDE = 0x0000001;
    const int ABS_ALWAYSONTOP = 0x0000002;
    const uint EVENT_OBJECT_CREATE = 0x8000;
    const uint EVENT_OBJECT_SHOW = 0x8002;
    const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    // ── Win32 Structs ───────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    struct RECT
    {
        public int Left,
            Top,
            Right,
            Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    // ── Win32 Delegates ─────────────────────────────────────────────
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    delegate void WinEventDelegate(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime
    );

    // ── Win32 Imports ───────────────────────────────────────────────
    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags
    );

    [DllImport("user32.dll")]
    static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    static extern bool GetMessageW(
        out MSG lpMsg,
        IntPtr hWnd,
        uint wMsgFilterMin,
        uint wMsgFilterMax
    );

    [DllImport("user32.dll")]
    static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll")]
    static extern void PostQuitMessage(int nExitCode);

    [DllImport("shell32.dll")]
    static extern uint SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("kernel32.dll")]
    static extern bool SetConsoleCtrlHandler(ConsoleCtrlHandler handler, bool add);

    delegate bool ConsoleCtrlHandler(uint ctrlType);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetConsoleWindow();

    // ── State ───────────────────────────────────────────────────────
    static IntPtr _hook;
    static WinEventDelegate? _hookDelegate; // prevent GC
    static ConsoleCtrlHandler? _ctrlHandler; // prevent GC
    static readonly string[] _taskbarClasses = { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };

    // ── Entry Point ─────────────────────────────────────────────────
    static void Main()
    {
        // Hide the console window itself — we're a background process
        IntPtr console = GetConsoleWindow();
        if (console != IntPtr.Zero)
            ShowWindow(console, SW_HIDE);

        // Install Ctrl+C / close handler for clean shutdown
        _ctrlHandler = OnCtrlEvent;
        SetConsoleCtrlHandler(_ctrlHandler, true);

        // Step 1: Hide all taskbars
        HideAllTaskbars();

        // Step 2: Install event hook to keep them hidden
        _hookDelegate = OnWinEvent;
        _hook = SetWinEventHook(
            EVENT_OBJECT_CREATE,
            EVENT_OBJECT_SHOW,
            IntPtr.Zero,
            _hookDelegate,
            0,
            0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS
        );

        // Step 3: Run Win32 message loop (required for WinEvent callbacks)
        RunMessageLoop();

        // Step 4: Cleanup
        Restore();
    }

    // ── Message Loop ────────────────────────────────────────────────
    static void RunMessageLoop()
    {
        while (GetMessageW(out MSG msg, IntPtr.Zero, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    // ── Ctrl+C / Console Close Handler ──────────────────────────────
    static bool OnCtrlEvent(uint ctrlType)
    {
        Restore();
        PostQuitMessage(0);
        return true;
    }

    // ── Core Logic ──────────────────────────────────────────────────
    static void HideAllTaskbars()
    {
        EnumWindows(
            (hwnd, _) =>
            {
                if (IsTaskbarWindow(hwnd))
                {
                    SetAppBarState(hwnd, ABS_AUTOHIDE);
                    ShowWindow(hwnd, SW_HIDE);
                }
                return true;
            },
            IntPtr.Zero
        );
    }

    static void Restore()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }

        EnumWindows(
            (hwnd, _) =>
            {
                if (IsTaskbarWindow(hwnd))
                {
                    SetAppBarState(hwnd, ABS_ALWAYSONTOP);
                    ShowWindow(hwnd, SW_SHOWNORMAL);
                }
                return true;
            },
            IntPtr.Zero
        );
    }

    static void OnWinEvent(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime
    )
    {
        if (IsTaskbarWindow(hwnd))
        {
            SetAppBarState(hwnd, ABS_AUTOHIDE);
            ShowWindow(hwnd, SW_HIDE);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────
    static bool IsTaskbarWindow(IntPtr hwnd)
    {
        var sb = new StringBuilder(64);
        GetClassName(hwnd, sb, sb.Capacity);
        var cls = sb.ToString();
        for (int i = 0; i < _taskbarClasses.Length; i++)
        {
            if (cls == _taskbarClasses[i])
                return true;
        }
        return false;
    }

    static void SetAppBarState(IntPtr hwnd, int state)
    {
        var abd = new APPBARDATA
        {
            cbSize = Marshal.SizeOf<APPBARDATA>(),
            hWnd = hwnd,
            lParam = (IntPtr)state,
        };
        SHAppBarMessage(ABM_SETSTATE, ref abd);
    }
}
