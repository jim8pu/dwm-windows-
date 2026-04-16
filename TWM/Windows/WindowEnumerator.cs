using System.Text;
using TilingWindowManager.Configs;
using TilingWindowManager.Native;
using static TilingWindowManager.Native.NativeMethods;

namespace TilingWindowManager.Windows;

/// <summary>
/// Scans the operating system to find which windows are currently valid for tiling.
/// Uses a series of heuristics (Visibility, Cloaking, UWP rules, and configurable exclusions)
/// to filter out hidden background processes, tooltips, and unmanageable windows.
/// </summary>
public class WindowEnumerator
{
    private readonly ConfigLoader _configLoader;
    public event Action? WindowMetadataUpdated;

    public WindowEnumerator(ConfigLoader configLoader)
    {
        _configLoader = configLoader;
    }

    private class WindowMetadata
    {
        public string ClassName { get; set; } = "";
        public string ExeName { get; set; } = "";
        public string FullPath { get; set; } = "";
    }

    private readonly List<IntPtr> _windowsBuffer = new();
    private readonly List<IntPtr> _staleBuffer = new();
    private readonly Dictionary<IntPtr, WindowMetadata> _metadataCache = new();

    /// <summary>
    /// Executes a native EnumWindows pass over the entire OS.
    /// </summary>
    public List<IntPtr> GetTileableWindows()
    {
        _windowsBuffer.Clear();

        EnumWindows(
            (hwnd, _) =>
            {
                if (ShouldTile(hwnd))
                {
                    _windowsBuffer.Add(hwnd);
                }
                return true;
            },
            IntPtr.Zero
        );

        // Cleanup stale cache entries to prevent memory leaks when windows close
        _staleBuffer.Clear();
        foreach (var cachedHwnd in _metadataCache.Keys)
        {
            if (!IsWindow(cachedHwnd))
            {
                _staleBuffer.Add(cachedHwnd);
            }
        }
        foreach (var s in _staleBuffer)
        {
            _metadataCache.Remove(s);
        }

        return _windowsBuffer;
    }

    private bool ShouldTile(IntPtr hwnd)
    {
        // ============================================================
        // PHASE 1: Basic Visibility Checks
        // ============================================================
        // Fast fail for invalid or hung windows to prevent WM freezing
        if (!IsWindow(hwnd) || IsHungAppWindow(hwnd))
            return false;

        if (!IsWindowVisible(hwnd))
            return false;
        if (IsIconic(hwnd))
            return false;
        if (IsCloaked(hwnd))
            return false;

        long style = GetWindowLongPtrW(hwnd, GWL_STYLE).ToInt64();
        long exStyle = GetWindowLongPtrW(hwnd, GWL_EXSTYLE).ToInt64();

        if ((exStyle & WS_EX_TOOLWINDOW) != 0)
            return false;
        if ((exStyle & WS_EX_NOACTIVATE) != 0)
            return false;
        if (GetParent(hwnd) != IntPtr.Zero && GetAncestor(hwnd, GA_ROOT) != hwnd)
            return false;

        // ============================================================
        // PHASE 2: Get Window Information
        // ============================================================
        // Double check validity before querying strings (transient windows might have closed)
        if (!IsWindow(hwnd)) return false;

        string title = "";
        var length = GetWindowTextLengthW(hwnd);
        if (length > 0)
        {
            var sbTitle = new StringBuilder(length + 1);
            if (GetWindowText(hwnd, sbTitle, sbTitle.Capacity) > 0)
                title = sbTitle.ToString();
        }

        if (!_metadataCache.TryGetValue(hwnd, out var meta))
        {
            var sbClass = new StringBuilder(256);
            if (RealGetWindowClassW(hwnd, sbClass, (uint)sbClass.Capacity) == 0)
                return false; // Failed to get class name, window probably died
            
            string className = sbClass.ToString();
            meta = new WindowMetadata
            {
                ClassName = className,
                ExeName = "",
                FullPath = ""
            };
            
            _metadataCache[hwnd] = meta;

            if (GetWindowThreadProcessId(hwnd, out int pid) != 0)
            {
                // OFF-LOAD DISK I/O AND PROCESS HANDLES TO A BACKGROUND THREAD!
                // This prevents the Window Manager from freezing when a slow drive spins up 
                // or when querying Anti-Cheat protected kernel-level game executables.
                Task.Run(() =>
                {
                    IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                    if (hProcess != IntPtr.Zero)
                    {
                        try
                        {
                            var sbPath = new StringBuilder(1024);
                            int size = sbPath.Capacity;
                            if (QueryFullProcessImageName(hProcess, 0, sbPath, ref size))
                            {
                                string asyncPath = sbPath.ToString();
                                string asyncExe = System.IO.Path.GetFileName(asyncPath);
                                
                                meta.FullPath = asyncPath;
                                meta.ExeName = asyncExe;

                                // Provide a tiny delay to ensure the window's own UI framework has settled
                                // before we violently rip it out of the tiling layout if it ends up being unmanaged.
                                Thread.Sleep(50);

                                // If this new executable triggers an Unmanage/Force Rule, 
                                // we MUST notify the main layout engine to redraw natively.
                                var match = _configLoader.FindMatch("", meta.ClassName, meta.ExeName, meta.FullPath);
                                if (match != null)
                                {
                                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                                    {
                                        WindowMetadataUpdated?.Invoke();
                                    }, System.Windows.Threading.DispatcherPriority.Render);
                                }
                            }
                        }
                        finally
                        {
                            CloseHandle(hProcess);
                        }
                    }
                });
            }
        }

        // ============================================================
        // PHASE 3: Configuration Rules (Highest Priority)
        // ============================================================
        var match = _configLoader.FindMatch(title, meta.ClassName, meta.ExeName, meta.FullPath);
        if (match != null)
        {
            if (match.Flag == AppExtraFlag.WmUnmanage)
                return false;
            if (match.Flag == AppExtraFlag.WmForce)
                return true;
        }

        // ============================================================
        // PHASE 4: Universal Popup/Dialog Detection
        // ============================================================
        // #32770 is the hardcoded Win32 class name for standard Dialog boxes.
        // "OperationStatusWindow" is the modern Windows File Copy/Move progress dialog.
        // It fakes WS_THICKFRAME for DWM drop-shadows but isn't actually resizable.
        if (meta.ClassName == "#32770" || meta.ClassName == "OperationStatusWindow")
            return false;

        IntPtr owner = GetWindow(hwnd, GW_OWNER);
        if (owner != IntPtr.Zero && owner != GetDesktopWindow())
            return false;

        IntPtr rootOwner = GetAncestor(hwnd, GA_ROOTOWNER);
        if (rootOwner != IntPtr.Zero && rootOwner != hwnd && rootOwner != GetDesktopWindow())
            return false;

        bool hasDlgFrame = (style & WS_DLGFRAME) != 0 && (style & WS_CAPTION) == 0;
        if (hasDlgFrame || (exStyle & WS_EX_DLGMODALFRAME) != 0)
            return false;

        // ============================================================
        // PHASE 5: UWP Special Handling
        // ============================================================
        // Modern Windows 10/11 UWP apps (Settings, Calculator, etc.) are hosted inside 
        // a generic 'ApplicationFrameWindow' container. We must explicitly allow this.
        if (meta.ClassName == "ApplicationFrameWindow")
            return true;

        // ============================================================
        // PHASE 6: Standard Window Style Checks
        // ============================================================
        bool isResizable = (style & WS_THICKFRAME) != 0;
        bool isAppWindow = (exStyle & WS_EX_APPWINDOW) != 0;
        bool isPopup = (style & WS_POPUP) != 0;

        // Must be formally maximizable OR explicitly resizable (WS_THICKFRAME).
        // Some modern Electron apps (like Z-Library) use custom title bars and omit WS_MAXIMIZEBOX, 
        // but can still be resized and tiled perfectly.
        if ((style & WS_MAXIMIZEBOX) == 0 && !isResizable)
            return false;

        // Popups without minimize are dialogs/menus
        if (isPopup && (style & WS_MINIMIZEBOX) == 0)
            return false;

        // Non-resizable topmost = overlay
        if ((exStyle & WS_EX_TOPMOST) != 0 && !isResizable)
            return false;

        // Non-resizable without AppWindow flag = not a main app
        if (!isResizable && !isAppWindow)
            return false;

        return true;
    }

    private bool IsCloaked(IntPtr hwnd)
    {
        int cloaked;
        DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out cloaked, sizeof(int));
        return cloaked != 0;
    }
}
