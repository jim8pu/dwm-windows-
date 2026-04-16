Important stuff
1. When I would be implementing AutoStart on startup, we need to do some complex task scheduler stuff. We can't do normal stuff because there is a problem: our app is an administrator app. To skip that, to bypass that admin check of not making Windows completely blank, we have to do that advanced way. It's not that complicated but we have to code it. Basically it actually uses some sort of tag scheduler, although I am not sure, it is probably this. 

1. Open 
Windows\WindowEnumerator.cs
Locate the "Phase 6: Standard Window Style Checks" section near the bottom of the 
ShouldTile
 method.

2. Replace the Code
Find and DELETE this original code block:

csharp
// Must be maximizable — tiling IS maximizing into a region
if ((style & WS_MAXIMIZEBOX) == 0)
    return false;
And REPLACE it with this updated code block:

csharp
// Must be formally maximizable OR explicitly resizable (WS_THICKFRAME).
// Some modern Electron apps (like Z-Library) use custom title bars and omit WS_MAXIMIZEBOX, 
// but can still be resized and tiled perfectly.
if ((style & WS_MAXIMIZEBOX) == 0 && !isResizable)
    return false;
(No other files need to be modified. Once this change is applied, Z-Library will snap to the tiling grid perfectly!)

# Future Plans & Implementation Guide
This document contains technical blueprints for advanced features that can be implemented in the Tiling Window Manager at a later time. If any of these features are requested, the AI assistant can use the implementation plans below to build them seamlessly.

## 1. Undocumented COM Window Cloaking
**Goal:** Hide a window completely from the screen without changing its `ShowWindow` state, allowing its icon to stay visibly active on the Windows Taskbar and preventing rendering glitches when it is restored.

**Implementation Plan:**
1. **Declare COM Interfaces (`NativeMethods.cs`):**
   * Define `[ComImport]` interfaces for `IServiceProvider` and `IApplicationViewCollection` (with UUID `1841C6D7-4F9D-42C0-AF41-8747538F10E5`).
   * Define the `IApplicationView` interface containing the `SetCloak(uint cloakType, int flags)` method.
   * Define the `CLSID_ImmersiveShell` GUID.
2. **Implement Wrapper (`NativeMethods.cs`):**
   * Create a `CloakWindow(IntPtr hwnd, bool hide)` helper method.
   * `CoCreateInstance` the Immersive Shell, Query interface for `IApplicationViewCollection`.
   * Call `GetViewForHwnd(hwnd, out IApplicationView view)`.
   * Call `view.SetCloak(hide ? 1u : 0u, hide ? 2 : 0)`.
3. **Integration (`WindowManager.cs`):**
   * Replace `ShowWindow(SW_HIDE)` or off-screen teleports with the new `CloakWindow` method when handling virtual workspaces, minimized states, or filtering out untiled windows.

---

## 2. Multi-Monitor Support
**Goal:** Allow the layout engine to independently tile windows on multiple distinct monitors simultaneously, rather than collapsing all windows onto the primary display.

**Implementation Plan:**
1. **Data Structure Overhaul (`WindowManager.cs`):**
   * Change `_masterRatio` from a single `double` to a `Dictionary<IntPtr, double>` keyed by monitor handle (`hMonitor`).
   * Change `_stackRatios` from a `List<double>` to a `Dictionary<IntPtr, List<double>>`.
2. **Monitor Grouping in `Retile()`:**
   * Instead of a flat `_stableWindows` list processing algorithm, iterate over `_stableWindows` and group them by `MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST)`.
   * Create a loop that runs the layout math (Monocle or Master/Stack) independently for *each* monitor group, using that specific monitor's `monitorInfo.rcWork`.
3. **Seamless Cross-Monitor Dragging:**
   * Because `MonitorFromWindow` is evaluated statically on every `Retile()` tick, a window dragged across a bezel will automatically switch groups. The algorithm will naturally pull it from the old monitor's stack and inject it into the new monitor's stack in real-time.

---

## 3. External JSON Configuration
**Goal:** Allow users to easily add to or modify application rules (unmanage, force tile) without needing to recompile the C# project.

**Implementation Plan:**
1. **Model Update (`AppConfig.cs`):** Update the `AppRule` class to cleanly serialize using `System.Text.Json`.
2. **File Handling:** In `ConfigLoader`, check if `%APPDATA%\TilingWindowManager\config.json` exists. If not, generate a default one with the existing rules (Flow.Launcher, PowerToys, etc.). 
3. **Dynamic Loading:** Deserialize the JSON file at launch and populate the `Rules` list. Add a `FileSystemWatcher` to optionally hot-reload rules if the user saves the JSON file while the app is running.

---

## 4. Global Crash Handlers (Silent Death Prevention)
**Goal:** Prevent the Window Manager from crashing silently without any error output if an unexpected native OS error or driver hitch occurs in the background.

**Implementation Plan:**
1. **Hook Application DOM (`App.xaml.cs`):**
   * Subscribe to `DispatcherUnhandledException` on the main UI thread.
   * Subscribe to `AppDomain.CurrentDomain.UnhandledException` for all background threads.
   * Subscribe to `TaskScheduler.UnobservedTaskException` for asynchronous tasks.
2. **Graceful Shutdown:** Inside the handlers, unhook all WinEvents (`EventHookManager.Dispose()`), release the Single Instance Mutex cleanly, and display a WPF `MessageBox` with the exact stack trace before finally calling `Environment.Exit()`.

---

## 5. WPF Multi-Monitor DPI Scale "Bleeding"
**Goal:** Fix a visual glitch where the blue focus bar (`HighlightOverlay`) bleeds outside its native hardware bounds when dragged across monitors with different display scaling percentages (e.g., 100% vs 150%).

**Implementation Plan:**
1. **Track Active DPI (`HighlightOverlay.cs`):**
   * Override the `OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)` native WPF method.
2. **Recalculate Bounds dynamically:**
   * Inside `OnDpiChanged`, instantly update the `_dpiScaleX` and `_dpiScaleY` internal variables using the `newDpi` parameters.
   * The `SetWindowPos` math will now dynamically resize the outer invisible hardware shell to perfectly match the natively scaled thicker inner WPF border when it teleports to a high-DPI monitor.




🛑🛑🛑🛑 could be important not sure 

Tiling Window Manager Fixes: The Modal Popup Bug Breakdown
This document provides a comprehensive explanation of why the Tiling Window Manager completely shut down and froze when certain system popups (like "Empty Recycle Bin" or File Pickers) were opened, and exactly how to apply the verified fixes.

🛑 The Core Problem
When a user triggered a standard System Dialog (like "File In Use"), the tiling manager paused mouse tracking correctly and kept the blue focus borders on the screen. However, when triggering a full Modal Dialog (like "Choose File" or "Delete Multiple Items"), the active application's blue border vanished instantly, mouse focus tracking stopped entirely, and the whole Window Manager effectively "shut down" until the popup was closed.

🔍 The Root Cause Analysis
1. The WS_DISABLED Check (The "Disappearing Borders" Bug)
When Windows launches a full modal dialog like a File Picker, it makes a secret system call to EnableWindow(parent_hwnd, FALSE). This forcibly applies the WS_DISABLED window style to the parent application (like VS Code or Edge) so the user cannot click anything in the background while deciding on the popup. Your 
WindowEnumerator.cs
 file contained a strict rule: if ((style & WS_DISABLED) != 0) return false;. Because the parent application was disabled, your code instantly rejected it. The Window Manager immediately deleted the application from its internal _stableWindows list. Seeing that there were "0 active apps" on the screen, the Window Manager hid the blue borders, aborted the layout engine, and suspended mouse tracking entirely. When the popup was closed, the app was re-enabled (WS_DISABLED removed) and the Window Manager resumed as if nothing happened.

2. The GetWindowTextLengthW Synchronous Deadlock
Inside 
WindowEnumerator.cs
, the code used GetWindowTextLengthW to check the title of every window. Unbeknownst to most, this API secretly sends a synchronous WM_GETTEXTLENGTH message to the target window. Because the application's thread was completely blocked by a COM or Modal operation, it never replied. This caused your Window Manager's main loop to freeze entirely while trying to just read the title of the frozen app.

3. The SPIF_SENDCHANGE OS-Wide Broadcast Deadlock
When the Window Manager detected an untiled popup, it correctly tried to temporarily turn off "Focus Follows Mouse" using SystemParametersInfoW. However, it used the SPIF_SENDCHANGE flag. This flag forces the Operating System to explicitly broadcast a WM_SETTINGCHANGE message to every top-level window on the computer and wait for a reply. Because the File Picker was frozen waiting for user interaction, it didn't reply to the system broadcast. Consequently, the OS blocked the Window Manager indefinitely until the user closed the popup.

4. False Fullscreen Detects (The Invisible Dimming Overlay)
Many modern Windows 10/11 popups spawn a massive, invisible (or dimly lit) full-screen background window to capture errant clicks. Your 
WindowManager.cs
 saw this invisible background window spanning 1920x1080 and falsely assumed the user had launched a Fullscreen Game, triggering an immediate suspension of the tiling engine (_overlay.HideBorder(); return;).

✅ Step-by-Step Fix Instructions for any AI or Developer
To perfectly fix these issues in the Tilling Window Manger codebase, apply the following 5 changes exactly as shown below:

FIX 1: Prevent Disappearing Apps (WindowEnumerator.cs)
Open 
Windows\WindowEnumerator.cs
 and find the 
ShouldTile
 method. Delete the WS_DISABLED check completely. Remove this code:

csharp
if ((style & WS_DISABLED) != 0)
    return false;
Why: Modal dialogs disable their parent windows heavily. Rejecting them kicks them out of the layout violently.

FIX 2: Prevent the Title Reading Deadlock (WindowEnumerator.cs)
Open 
Windows\WindowEnumerator.cs
 and locate the GetWindowTextLengthW usage inside 
ShouldTile
. Replace this code:

csharp
string title = "";
var length = GetWindowTextLengthW(hwnd);
if (length > 0)
{
    var sbTitle = new StringBuilder(length + 1);
    if (GetWindowText(hwnd, sbTitle, sbTitle.Capacity) > 0)
        title = sbTitle.ToString();
}
With this safe fixed-buffer code:

csharp
string title = "";
var sbTitle = new StringBuilder(512); // Hardcoded buffer avoids the synchronous GetWindowTextLengthW call
if (GetWindowText(hwnd, sbTitle, sbTitle.Capacity) > 0)
{
    title = sbTitle.ToString();
}
FIX 3: Prevent Tiling Manager False Game Suspend (WindowManager.cs)
Open 
Core\WindowManager.cs
 and find where isFullscreen is evaluated. Replace this code:

csharp
if (isFullscreen)
{
    _overlay.HideBorder();
    return; // Suspend WM operations while in fullscreen
}
With these explicit Dialog tests:

csharp
long style = GetWindowLongPtrW(foregroundApp, GWL_STYLE).ToInt64();
long exStyle = GetWindowLongPtrW(foregroundApp, GWL_EXSTYLE).ToInt64();
// Ignore popup dimming overlays and dialogs masquerading as fullscreen apps
bool isDialogOrTool = (style & WS_DLGFRAME) != 0 || 
                      (exStyle & WS_EX_DLGMODALFRAME) != 0 || 
                      (exStyle & WS_EX_TOOLWINDOW) != 0 ||
                      className == "#32770";
if (isFullscreen && !isDialogOrTool)
{
    _overlay.HideBorder();
    return;
}
FIX 4: Prevent OS-Wide Broadcast Deadlocks (WindowManager.cs)
Open 
Core\WindowManager.cs
 and find the 
SetOSFocusFollowsMouse
 method. Replace all 6 usages of SPIF_SENDCHANGE with 0 inside this specific method:

csharp
if (enable)
{
    SystemParametersInfoW(SPI_SETACTIVEWINDOWTRACKING, 0, (IntPtr)1, 0); 
    SystemParametersInfoW(SPI_SETACTIVEWNDTRKZORDER, 0, (IntPtr)0, 0);
    SystemParametersInfoW(SPI_SETACTIVEWNDTRKTIMEOUT, 0, IntPtr.Zero, 0);
}
else
{
    SystemParametersInfoW(SPI_SETACTIVEWINDOWTRACKING, 0, IntPtr.Zero, 0);
    SystemParametersInfoW(SPI_SETACTIVEWNDTRKZORDER, 0, (IntPtr)_originalActiveWndTrkZorder, 0);
    SystemParametersInfoW(SPI_SETACTIVEWNDTRKTIMEOUT, 0, (IntPtr)_originalActiveWndTrkTimeout, 0);
}
Why: Passing 0 updates the OS setting silently in kernel memory without forcing all unresponsive apps to reply to a global settings broadcast.

FIX 5: Prevent Resizing Freeze (WindowManager.cs & NativeMethods.cs)
Open 
Native\NativeMethods.cs
 and add this definition:

csharp
[DllImport("user32.dll", SetLastError = true)]
public static extern IntPtr SendMessageTimeoutW(
    IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
public const uint SMTO_ABORTIFHUNG = 0x0002;
Then, open 
Core\WindowManager.cs
, find the WM_ENTERSIZEMOVE and WM_EXITSIZEMOVE SendMessageW calls inside 
Retile()
 and replace them:

csharp
foreach (var m in _moves)
{
    SendMessageTimeoutW(m.hwnd, WM_ENTERSIZEMOVE, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 50, out _);
}
// ... later in the code ...
foreach (var m in _moves)
{
    SendMessageTimeoutW(m.hwnd, WM_EXITSIZEMOVE, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 50, out _);
}
Why: If a window is currently displaying a file picker, forcing it to resize synchronously with SendMessageW will hang the WM. The Timeout gracefully skips it if it doesn't reply in 50ms.


