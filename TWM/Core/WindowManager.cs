using System.Runtime.InteropServices;
using System.Text;

using System.Windows;
using System.Windows.Threading;
using TilingWindowManager.Native;
using TilingWindowManager.UI;
using TilingWindowManager.Windows;
using static TilingWindowManager.Native.NativeMethods;

namespace TilingWindowManager.Core;

/// <summary>
/// The core layout engine.
/// Responsible for calculating Master/Stack positions and moving windows natively.
/// </summary>
public class WindowManager
{
    private readonly WindowEnumerator _enumerator;
    private readonly HighlightOverlay _overlay;
    private readonly EventHookManager _hookManager;
    private readonly List<IntPtr> _stableWindows = new(); // Stable list for layout
    private IntPtr _lastFocusedTiledHwnd = IntPtr.Zero;
    private IntPtr _lastForegroundHwnd = IntPtr.Zero;
    private const int GAP = 8;
    
    private uint _originalActiveWindowTracking = 0;
    private uint _originalActiveWndTrkTimeout = 0;
    private uint _originalActiveWndTrkZorder = 0;
    
    // Tracks the live OS state to prevent redundant system broadcasts
    private bool _isXMouseCurrentlyActive = false;
    
    // Dynamic Layout Ratios
    private double _masterRatio = 0.5;
    private List<double> _stackRatios = new();
    
    // Shadow Margin Cache (avoids repeated expensive DWM calls)
    private readonly Dictionary<IntPtr, (int left, int top, int right, int bottom)> _shadowCache = new();
    
    // Reuse objects to eliminate per-frame allocations
    private readonly StringBuilder _sbClassRetile = new(256);
    private readonly List<IntPtr> _newlyAdded = new();
    
    // Resize Tracking
    private IntPtr _dragHwnd = IntPtr.Zero;
    private RECT _dragStartRect;
    private bool _isResizeRetileQueued = false;
    
    // Reuse collections to eliminate Garbage Collection (GC) thrashing during 60FPS dragging
    private readonly List<(IntPtr hwnd, int x, int y, int w, int h)> _moves = new();

    public WindowManager(
        WindowEnumerator enumerator,
        HighlightOverlay overlay,
        EventHookManager hookManager
    )
    {
        _enumerator = enumerator;
        _overlay = overlay;
        _hookManager = hookManager;

        // Permanently disable the OS Foreground Lock Timeout for the session
        // This completely eliminates the orange blinking taskbar securely without ANY hacky fake Alt keystrokes or thread attachments!
        SystemParametersInfoW(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, IntPtr.Zero, SPIF_SENDCHANGE);

        _hookManager.WindowStateChanged += OnWindowStateChanged;
        _hookManager.WindowMoveSizeStart += OnWindowMoveSizeStart;
        _hookManager.WindowMoveSizeEnd += OnWindowMoveSizeEnd;
        _enumerator.WindowMetadataUpdated += Retile;

        // Native OS Kernel XMouse tracking (Zero C# overhead!)
        SystemParametersInfoW(SPI_GETACTIVEWINDOWTRACKING, 0, ref _originalActiveWindowTracking, 0);
        SystemParametersInfoW(SPI_GETACTIVEWNDTRKTIMEOUT, 0, ref _originalActiveWndTrkTimeout, 0);
        SystemParametersInfoW(SPI_GETACTIVEWNDTRKZORDER, 0, ref _originalActiveWndTrkZorder, 0);

        SetOSFocusFollowsMouse(_isFocusFollowsMouseEnabled);

        System.Windows.Application.Current.Exit += (s, e) => 
        {
            SystemParametersInfoW(SPI_SETACTIVEWINDOWTRACKING, 0, (IntPtr)_originalActiveWindowTracking, SPIF_SENDCHANGE);
            SystemParametersInfoW(SPI_SETACTIVEWNDTRKZORDER, 0, (IntPtr)_originalActiveWndTrkZorder, SPIF_SENDCHANGE);
            SystemParametersInfoW(SPI_SETACTIVEWNDTRKTIMEOUT, 0, (IntPtr)_originalActiveWndTrkTimeout, SPIF_SENDCHANGE);
        };
        
        _hookManager.WindowLocationChanged += OnWindowLocationChanged;
    }

    private bool _isFocusFollowsMouseEnabled = true;
    public bool IsFocusFollowsMouseEnabled
    {
        get => _isFocusFollowsMouseEnabled;
        set
        {
            if (_isFocusFollowsMouseEnabled != value)
            {
                _isFocusFollowsMouseEnabled = value;
                SetOSFocusFollowsMouse(value);
            }
        }
    }

    private void SetOSFocusFollowsMouse(bool enable)
    {
        if (_isXMouseCurrentlyActive == enable) return;
        _isXMouseCurrentlyActive = enable;

        if (enable)
        {
            SystemParametersInfoW(SPI_SETACTIVEWINDOWTRACKING, 0, (IntPtr)1, SPIF_SENDCHANGE);
            SystemParametersInfoW(SPI_SETACTIVEWNDTRKZORDER, 0, (IntPtr)0, SPIF_SENDCHANGE);
            SystemParametersInfoW(SPI_SETACTIVEWNDTRKTIMEOUT, 0, IntPtr.Zero, SPIF_SENDCHANGE);
        }
        else
        {
            SystemParametersInfoW(SPI_SETACTIVEWINDOWTRACKING, 0, IntPtr.Zero, SPIF_SENDCHANGE);
            SystemParametersInfoW(SPI_SETACTIVEWNDTRKZORDER, 0, (IntPtr)_originalActiveWndTrkZorder, SPIF_SENDCHANGE);
            SystemParametersInfoW(SPI_SETACTIVEWNDTRKTIMEOUT, 0, (IntPtr)_originalActiveWndTrkTimeout, SPIF_SENDCHANGE);
        }
    }

    public void Start()
    {
        _hookManager.Start();
        Retile(); // Initial layout pass
    }

    public void Stop()
    {
        _hookManager.Dispose();
        _overlay.HideBorder();
    }

    private void OnWindowStateChanged()
    {
        Retile();
    }

    private void Retile()
    {
        // 0. Fullscreen App Detection (Games, YouTube F11)
        // If the active window covers the entire physical monitor (including taskbar), suspend tiling.
        var foregroundApp = GetForegroundWindow();
        if (foregroundApp != IntPtr.Zero && foregroundApp != GetDesktopWindow())
        {
            _sbClassRetile.Clear();
            RealGetWindowClassW(foregroundApp, _sbClassRetile, (uint)_sbClassRetile.Capacity);
            var className = _sbClassRetile.ToString();

            // Ignore the desktop background itself
            if (className != "Progman" && className != "WorkerW")
            {
                var hMon = MonitorFromWindow(foregroundApp, MONITOR_DEFAULTTONEAREST);
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(hMon, ref mi))
                {
                    GetWindowRect(foregroundApp, out var fgRect);
                    bool isFullscreen = fgRect.Left <= mi.rcMonitor.Left &&
                                        fgRect.Top <= mi.rcMonitor.Top &&
                                        fgRect.Right >= mi.rcMonitor.Right &&
                                        fgRect.Bottom >= mi.rcMonitor.Bottom;

                    if (isFullscreen)
                    {
                        _overlay.HideBorder();
                        return; // Suspend WM operations while in fullscreen
                    }
                }
            }
        }

        // 1. Get current valid windows
        var currentWindows = _enumerator.GetTileableWindows();

        // 2. Sync Stable List
        // We maintain a stable list so windows don't randomly swap positions when one maximizes/restores.
        // Remove closed (and clean up shadow cache)
        _stableWindows.RemoveAll(h =>
        {
            if (!currentWindows.Contains(h))
            {
                _shadowCache.Remove(h);
                return true;
            }
            return false;
        });
        // Add new (append to end)
        _newlyAdded.Clear();
        foreach (var hwnd in currentWindows)
        {
            if (!_stableWindows.Contains(hwnd))
            {
                _stableWindows.Add(hwnd);
                _newlyAdded.Add(hwnd);
            }
        }

        // 3. Balance Ratios if Window Count Changed
        int stackCount = Math.Max(0, _stableWindows.Count - 1);
        if (_stackRatios.Count != stackCount)
        {
            _stackRatios.Clear();
            for (int i = 0; i < stackCount; i++)
            {
                _stackRatios.Add(1.0 / stackCount);
            }
        }

        // Restore maximized windows ONCE when first tracked (not every retile frame)
        // This prevents the infinite loop: Retile → ShowWindow → EVENT_OBJECT_SHOW → Retile
        foreach (var hwnd in _newlyAdded)
        {
            if (IsZoomed(hwnd))
                ShowWindowAsync(hwnd, SW_RESTORE);
        }

        // Check foreground window for overlay
        var foreground = GetForegroundWindow();

        // Focus Restoration Logic for Popups
        // If the previous foreground was an untiled window (popup) that just closed, 
        // the OS might give focus to something else. Restore it to the last active tiled window.
        if (_lastForegroundHwnd != IntPtr.Zero && 
            _lastForegroundHwnd != foreground && 
            !_stableWindows.Contains(_lastForegroundHwnd) && 
            !IsWindow(_lastForegroundHwnd))
        {
            if (_lastFocusedTiledHwnd != IntPtr.Zero && _stableWindows.Contains(_lastFocusedTiledHwnd))
            {
                // Unconditionally grant the target process (or ANY process) permission to set the foreground window
                if (GetWindowThreadProcessId(_lastFocusedTiledHwnd, out int targetPid) != 0)
                {
                    AllowSetForegroundWindow((uint)targetPid);
                }
                else
                {
                    AllowSetForegroundWindow(ASFW_ANY);
                }

                // SendInput trick: send a dummy mouse event to acquire the foreground lock token
                var input = new INPUT { type = INPUT_MOUSE };
                SendInput(1, new[] { input }, INPUT.Size);
                SetForegroundWindow(_lastFocusedTiledHwnd);
                foreground = GetForegroundWindow(); // Refresh foreground
            }
        }
        
        _lastForegroundHwnd = foreground;

        // If foreground is a managed tiled window, remember it
        if (_stableWindows.Contains(foreground))
            _lastFocusedTiledHwnd = foreground;

        // --- NEW POPUP FOCUS FIX LOGIC ---
        if (_isFocusFollowsMouseEnabled)
        {
            bool isDesktopOrNull = (foreground == IntPtr.Zero);
            if (!isDesktopOrNull)
            {
                _sbClassRetile.Clear();
                RealGetWindowClassW(foreground, _sbClassRetile, (uint)_sbClassRetile.Capacity);
                var className = _sbClassRetile.ToString();
                isDesktopOrNull = (className == "Progman" || className == "WorkerW");
            }

            if (_stableWindows.Contains(foreground) || isDesktopOrNull)
            {
                SetOSFocusFollowsMouse(true);
            }
            else
            {
                SetOSFocusFollowsMouse(false); // Temporarily disable OS tracking for untiled popups
            }
        }
        // ---------------------------------

        // Use the effective focus target: if foreground is not tiled (popup), fall back to last known
        var effectiveFocus = _stableWindows.Contains(foreground) ? foreground : _lastFocusedTiledHwnd;
        bool overlayUpdated = false;

        // 3. Tile
        if (_stableWindows.Count == 0)
        {
            _overlay.HideBorder();
            return;
        }

        // Get Monitor Info (Physical Pixels)
        var hMonitor = MonitorFromWindow(_stableWindows[0], MONITOR_DEFAULTTONEAREST);
        var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(hMonitor, ref monitorInfo);

        var workArea = monitorInfo.rcWork; // Physical Pixels

        bool anyChange = false;
        _moves.Clear();

        if (_stableWindows.Count == 1)
        {
            // Monocle Mode
            var hwnd = _stableWindows[0];

            int x = workArea.Left + GAP;
            int y = workArea.Top + GAP;
            int w = workArea.Width - GAP * 2;
            int h = workArea.Height - GAP * 2;

            // Sync Overlay if this is the foreground window
            if (hwnd == effectiveFocus)
            {
                _overlay.ShowBorder(x, y, w, h);
                overlayUpdated = true;
            }

            if (hwnd != _dragHwnd)
            {
                // Check if update is needed BEFORE shadow adjustment (compare intended content rect vs actual frame bounds)
                if (NeedsUpdate(hwnd, x, y, w, h))
                    anyChange = true;

                AdjustForShadow(hwnd, ref x, ref y, ref w, ref h);
                _moves.Add((hwnd, x, y, w, h));
            }
        }
        else
        {
            // Master + Stack
            int width = (int)(workArea.Width * _masterRatio);
            int height = workArea.Height;

            // Master (First Window)
            var master = _stableWindows[0];

            int mx = workArea.Left + GAP;
            int my = workArea.Top + GAP;
            int mw = width - GAP * 2;
            int mh = height - GAP * 2;

            if (master == effectiveFocus)
            {
                _overlay.ShowBorder(mx, my, mw, mh);
                overlayUpdated = true;
            }

            if (master != _dragHwnd)
            {
                if (NeedsUpdate(master, mx, my, mw, mh))
                    anyChange = true;

                AdjustForShadow(master, ref mx, ref my, ref mw, ref mh);
                _moves.Add((master, mx, my, mw, mh));
            }

            // Stack (Rest)
            int numStackWindows = _stableWindows.Count - 1;
            
            double currentYOffset = 0;

            for (int i = 1; i < _stableWindows.Count; i++)
            {
                var hwnd = _stableWindows[i];
                int ratioIndex = i - 1;
                double ratio = _stackRatios[ratioIndex];

                int stackHeight = (int)(height * ratio);
                
                // Adjust last window to fill remaining vertical space entirely to prevent 1px gaps due to rounding
                if (i == _stableWindows.Count - 1)
                {
                    stackHeight = height - (int)currentYOffset;
                }

                int sx = workArea.Left + width + GAP;
                int sy = (int)workArea.Top + (int)currentYOffset + GAP;
                int remainingWidth = workArea.Width - width;
                int sw = remainingWidth - GAP * 2;
                int sh = stackHeight - GAP * 2;
                
                currentYOffset += stackHeight;

                if (hwnd == effectiveFocus)
                {
                    _overlay.ShowBorder(sx, sy, sw, sh);
                    overlayUpdated = true;
                }

                if (hwnd != _dragHwnd)
                {
                    if (NeedsUpdate(hwnd, sx, sy, sw, sh))
                        anyChange = true;

                    AdjustForShadow(hwnd, ref sx, ref sy, ref sw, ref sh);
                    _moves.Add((hwnd, sx, sy, sw, sh));
                }
            }
        }

        if (anyChange)
        {
            // Notify all target windows that a resize operation is starting.
            // This lets WPF/Electron apps suspend internal layout recalculation
            // until the batch is done, matching what Komorebi does.
            foreach (var m in _moves)
                SendMessageW(m.hwnd, WM_ENTERSIZEMOVE, IntPtr.Zero, IntPtr.Zero);

            // =========================================================================
            // APPLIER PASS 1: The Polite Request (DeferWindowPos)
            // =========================================================================
            // DeferWindowPos allows Windows to batch all window movements into a single 
            // atomic screen refresh, preventing the "cascading redraw" tearing effect.
            var hdwp = BeginDeferWindowPos(_moves.Count);
            if (hdwp != IntPtr.Zero)
            {
                foreach (var m in _moves)
                {
                    hdwp = DeferWindowPos(
                        hdwp,
                        m.hwnd,
                        IntPtr.Zero,
                        m.x,
                        m.y,
                        m.w,
                        m.h,
                        SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_ASYNCWINDOWPOS
                    );
                    if (hdwp == IntPtr.Zero)
                        break;
                }
                if (hdwp != IntPtr.Zero)
                    EndDeferWindowPos(hdwp);
            }

            // =========================================================================
            // APPLIER PASS 2: The Ruthless Override (Electron/Fixed-Size Apps)
            // =========================================================================
            // Some apps (Electron, Notion, Discord) have strict internal Minimum Sizes and
            // will aggressively reject WM_WINDOWPOSCHANGING events to stay large.
            // By using SWP_NOSENDCHANGING, we bypass the application's internal veto and 
            // force the OS to resize them anyway. ("Perfect Tiling")
            foreach (var m in _moves)
            {
                GetWindowRect(m.hwnd, out var afterRect);
                if (Math.Abs(afterRect.Left - m.x) > 2 || Math.Abs(afterRect.Top - m.y) > 2
                    || Math.Abs(afterRect.Width - m.w) > 2 || Math.Abs(afterRect.Height - m.h) > 2)
                {
                    SetWindowPos(
                        m.hwnd,
                        IntPtr.Zero,
                        m.x,
                        m.y,
                        m.w,
                        m.h,
                        SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_NOSENDCHANGING | SWP_ASYNCWINDOWPOS
                    );
                }
            }

            // Notify all target windows that the resize operation is complete.
            // Apps can now recalculate their layouts in one pass.
            foreach (var m in _moves)
                SendMessageW(m.hwnd, WM_EXITSIZEMOVE, IntPtr.Zero, IntPtr.Zero);
        }

        if (!overlayUpdated)
        {
            // Clean up stale reference if window was closed
            if (!_stableWindows.Contains(_lastFocusedTiledHwnd))
                _lastFocusedTiledHwnd = IntPtr.Zero;
            _overlay.HideBorder();
        }
    }

    /// <summary>
    /// Windows 10/11 adds an invisible 7px "Drop Shadow" padding to `GetWindowRect()`.
    /// If we don't offset this, windows have an ugly 7px gap between them.
    /// DWMWA_EXTENDED_FRAME_BOUNDS gets the *actual visible* box of the window.
    /// </summary>
    private void AdjustForShadow(IntPtr hwnd, ref int x, ref int y, ref int w, ref int h)
    {
        // Use cached shadow margins if available (avoids expensive DWM cross-process call)
        if (!_shadowCache.TryGetValue(hwnd, out var margins))
        {
            GetWindowRect(hwnd, out var rect);
            if (
                DwmGetWindowAttribute(
                    hwnd,
                    DWMWA_EXTENDED_FRAME_BOUNDS,
                    out RECT frame,
                    Marshal.SizeOf<RECT>()
                ) == 0
            )
            {
                margins = (
                    frame.Left - rect.Left,
                    frame.Top - rect.Top,
                    rect.Right - frame.Right,
                    rect.Bottom - frame.Bottom
                );
            }
            else
            {
                margins = (0, 0, 0, 0);
            }
            _shadowCache[hwnd] = margins;
        }

        // Expand bounds outward into the shadow area to achieve visually flush tiling
        x -= margins.left;
        y -= margins.top;
        w += margins.left + margins.right;
        h += margins.top + margins.bottom;
    }

    private bool NeedsUpdate(IntPtr hwnd, int x, int y, int w, int h)
    {
        // Use the DWM Extended Frame Bounds (the actual visible window content, without shadows)
        // This is what we compare against the intended tile position (pre-shadow-adjustment)
        RECT rect;
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out rect, Marshal.SizeOf<RECT>()) != 0)
        {
            // Fallback to GetWindowRect if DWM call fails
            GetWindowRect(hwnd, out rect);
        }
        // Allow small tolerance (e.g. 2px) due to potential rounding
        return Math.Abs(rect.Left - x) > 2
            || Math.Abs(rect.Top - y) > 2
            || Math.Abs(rect.Width - w) > 2
            || Math.Abs(rect.Height - h) > 2;
    }

    private void OnWindowMoveSizeStart(IntPtr hwnd)
    {
        if (!_stableWindows.Contains(hwnd)) return;
        
        _dragHwnd = hwnd;
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT rect, Marshal.SizeOf<RECT>()) != 0)
        {
            GetWindowRect(hwnd, out rect);
        }
        _dragStartRect = rect;
    }

    private void OnWindowLocationChanged(IntPtr hwnd)
    {
        // Only process live resizing if we are actively dragging THIS window
        if (_dragHwnd != hwnd) return;
        if (!_stableWindows.Contains(hwnd)) return;
        
        ProcessResize(hwnd);
    }

    private void OnWindowMoveSizeEnd(IntPtr hwnd)
    {
        if (_dragHwnd != hwnd) return;
        
        if (_stableWindows.Contains(hwnd))
        {
            ProcessResize(hwnd);
        }
        
        _dragHwnd = IntPtr.Zero; // Reset after processing final resize
    }

    private void ProcessResize(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT endRect, Marshal.SizeOf<RECT>()) != 0)
        {
            GetWindowRect(hwnd, out endRect);
        }

        // Determine if Width (Horizontal Split) Changed
        if (_stableWindows.Count > 1)
        {
            // Get Monitor Info (Physical Pixels) to calculate ratio
            var hMonitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(hMonitor, ref monitorInfo);
            double totalWidth = monitorInfo.rcWork.Width;
            
            double ratioChange = 0;

            // Did they expand or shrink the specific shared middle border?
            if (_stableWindows[0] == hwnd)
            {
                // Master window: Did the RIGHT edge move?
                int diffRight = endRect.Right - _dragStartRect.Right;
                if (diffRight != 0 && endRect.Left == _dragStartRect.Left) // Ensure left edge is stable
                {
                    ratioChange = diffRight / totalWidth;
                    _masterRatio += ratioChange;
                }
            }
            else // Dragged a stack window
            {
                // Stack window: Did the LEFT edge move?
                int diffLeft = endRect.Left - _dragStartRect.Left;
                if (diffLeft != 0 && endRect.Right == _dragStartRect.Right) // Ensure right edge is stable
                {
                    // Dragging left edge to the right (diffLeft > 0) means Stack shrinks, Master GROWS. So ratioChange is positive for Master.
                    ratioChange = diffLeft / totalWidth;
                    _masterRatio += ratioChange;
                }
            }
            
            // Bounds check
            if (_masterRatio < 0.15) _masterRatio = 0.15;
            if (_masterRatio > 0.85) _masterRatio = 0.85;
        }

        // Determine if Height Changed (Vertical Split within Stack)
        int initialHeight = _dragStartRect.Height;
        int newHeight = endRect.Height;
        
        if (initialHeight != newHeight && initialHeight > 0 && _stableWindows.Count > 2)
        {
            // Only vertical resizes for stack windows matter. Master takes full height always.
            if (_stableWindows[0] != hwnd)
            {
                int stackIndex = _stableWindows.IndexOf(hwnd) - 1; // 0-based index in the stack
                
                int diff = newHeight - initialHeight;
                var hMonitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                GetMonitorInfo(hMonitor, ref monitorInfo);
                double totalHeight = monitorInfo.rcWork.Height;
                
                double ratioChange = diff / totalHeight;
                
                // Determine if they dragged the TOP edge (Top changed) or BOTTOM edge (Bottom changed)
                bool draggedTopEdge = endRect.Top != _dragStartRect.Top;
                bool draggedBottomEdge = endRect.Bottom != _dragStartRect.Bottom;

                // Adjust the ratios of the manipulated window and its neighbor
                if (draggedBottomEdge && stackIndex < _stackRatios.Count - 1)
                {
                    // Dragged bottom down: This window gets bigger, the one below it gets smaller
                    // diff > 0 -> ratioChange > 0
                    // Clamp ratioChange so we don't shrink the neighbor below 0.1
                    double maxGrow = _stackRatios[stackIndex + 1] - 0.1;
                    if (ratioChange > maxGrow) ratioChange = maxGrow;
                    
                    double maxShrink = _stackRatios[stackIndex] - 0.1;
                    if (-ratioChange > maxShrink) ratioChange = -maxShrink;
                    
                    if (ratioChange != 0)
                    {
                        _stackRatios[stackIndex] += ratioChange;
                        _stackRatios[stackIndex + 1] -= ratioChange;
                    }
                }
                else if (draggedTopEdge && stackIndex > 0)
                {
                    // Dragged top edge up: This window gets bigger, the one above it gets smaller
                    // Width increases -> diff > 0 -> ratioChange > 0
                    double maxGrow = _stackRatios[stackIndex - 1] - 0.1;
                    if (ratioChange > maxGrow) ratioChange = maxGrow;
                    
                    double maxShrink = _stackRatios[stackIndex] - 0.1;
                    if (-ratioChange > maxShrink) ratioChange = -maxShrink;

                    if (ratioChange != 0)
                    {
                        _stackRatios[stackIndex] += ratioChange;
                        _stackRatios[stackIndex - 1] -= ratioChange;
                    }
                }
            }
        }
        
        // Force a retile to snap to the new exact ratios in real-time.
        // DEBOUNCED: Prevents flooding the WPF event loop with thousands of tasks during violent mouse drags
        if (!_isResizeRetileQueued)
        {
            _isResizeRetileQueued = true;
            System.Windows.Application.Current.Dispatcher.InvokeAsync(() => 
            {
                _isResizeRetileQueued = false;
                Retile();
            }, System.Windows.Threading.DispatcherPriority.Render);
        }
        
        // Update the start rect continuously so the next location change is just a small delta
        _dragStartRect = endRect;
    }
}
