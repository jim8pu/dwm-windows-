// MacStatusBar - A macOS-style status bar for Windows
// Build with: build.bat

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Threading;
using System.Xml;

public class MacStatusBar : Application
{
    [STAThread]
    public static void Main()
    {
        SetProcessDPIAware();
        MacStatusBar app = new MacStatusBar();
        app.Run(new StatusBarWindow());
    }

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();
}

public class StatusBarWindow : Window
{
    private TextBlock clockText;
    private Grid batteryContainer;
    private Grid wifiCanvas;
    private StackPanel dockPanel;
    private DispatcherTimer timer;
    private const int BAR_HEIGHT = 36;
    private int floatingGap = 8; // Symmetric gap around bar and apps
    private bool isRounded = true;
    private AppSettings settings = new AppSettings();
    private Border mainBorder; // Keep reference directly
    private ControlCenterWindow _controlCenter;

    // Unified Layout specific fields
    private TextBlock timeText;
    private TextBlock dateText;
    private int currentLayoutMode = -1;
    private bool _lastIsFullWidth = false;

    // Wi-Fi Fields
    private UIElement _wifiIcon;      // Connected state (filled fan Path in Viewbox)
    private UIElement _wifiOffIcon;   // Disconnected state (dimmed)


    // Screen metrics
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    // Window positioning
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int X,
        int Y,
        int cx,
        int cy,
        uint uFlags
    );

    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_HIDEWINDOW = 0x0080;

    // Fullscreen detection
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetClassName(
        IntPtr hWnd,
        System.Text.StringBuilder lpClassName,
        int nMaxCount
    );

    // Window enumeration for dock
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetWindowText(
        IntPtr hWnd,
        System.Text.StringBuilder lpString,
        int nMaxCount
    );

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    private const uint GW_OWNER = 4;

    // Find child windows (for UWP CoreWindow detection)
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindowEx(
        IntPtr hwndParent,
        IntPtr hwndChildAfter,
        string lpszClass,
        string lpszWindow
    );

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLongPtr(IntPtr hWnd, int nIndex);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_APPWINDOW = 0x00040000;

    // Icon extraction
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_GETICON = 0x007F;
    private static readonly IntPtr ICON_SMALL = IntPtr.Zero;
    private static readonly IntPtr ICON_BIG = new IntPtr(1);
    private static readonly IntPtr ICON_SMALL2 = new IntPtr(2);

    // Keyboard simulation for Action Center shortcut
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const byte VK_LWIN = 0x5B;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    [DllImport("user32.dll", EntryPoint = "GetClassLong")]
    private static extern uint GetClassLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtr")]
    private static extern IntPtr GetClassLong64(IntPtr hWnd, int nIndex);

    private const int GCL_HICONSM = -34;
    private const int GCL_HICON = -14;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    // Process path extraction for UWP icon fallback
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(
        uint dwDesiredAccess,
        bool bInheritHandle,
        uint dwProcessId
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool QueryFullProcessImageName(
        IntPtr hProcess,
        uint dwFlags,
        System.Text.StringBuilder lpExeName,
        ref uint lpdwSize
    );

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    // UWP Package APIs for icon extraction
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetPackageFullName(
        IntPtr hProcess,
        ref uint packageFullNameLength,
        System.Text.StringBuilder packageFullName
    );

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetPackagePathByFullName(
        string packageFullName,
        ref uint pathLength,
        System.Text.StringBuilder path
    );

    private const int ERROR_INSUFFICIENT_BUFFER = 122;
    private const int APPMODEL_ERROR_NO_PACKAGE = 15700;

    // Activate window on click
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    private const int SW_RESTORE = 9;

    private const int GWL_STYLE = -16;
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_THICKFRAME = 0x00040000;

    // AppBar API
    [DllImport("shell32.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern uint SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("user32.dll")]
    private static extern int RegisterWindowMessage(string msg);

    private const uint ABM_NEW = 0x00;
    private const uint ABM_REMOVE = 0x01;
    private const uint ABM_QUERYPOS = 0x02;
    private const uint ABM_SETPOS = 0x03;
    private const uint ABM_POSCHANGED = 0x01;
    private const uint ABE_TOP = 1;

    // --- Acrylic Blur (Backdrop Filter) ---
    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(
        IntPtr hwnd,
        ref WindowCompositionAttributeData data
    );

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute; // WCA_ACCENT_POLICY = 19
        public IntPtr Data;
        public int SizeOfData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor; // AABBGGRR format
        public int AnimationId;
    }

    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;
    private const int ACCENT_ENABLE_BLURBEHIND = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public int lParam;
    }

    // Work area API
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(
        uint uiAction,
        uint uiParam,
        ref RECT pvParam,
        uint fWinIni
    );

    private const uint SPI_GETWORKAREA = 0x0030;
    private const uint SPI_SETWORKAREA = 0x002F;
    private const uint SPIF_SENDCHANGE = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int left,
            top,
            right,
            bottom;
    }

    private IntPtr hwnd;
    private int screenWidth;
    private int screenHeight;
    private bool appBarRegistered = false;
    private RECT originalWorkArea;
    private bool workAreaModified = false;
    private int callbackMsgId;
    private bool isHiddenForFullscreen = false;

    // Dock caching for stability and performance
    private List<IntPtr> cachedWindowOrder = new List<IntPtr>(); // Unified order for all windows
    private Dictionary<IntPtr, ImageSource> iconCache = new Dictionary<IntPtr, ImageSource>();

    // Virtual Desktop State
    private int currentWorkspace = 0; // 0-indexed, matches WorkspaceNames list
    private Dictionary<IntPtr, int> windowWorkspaceMap = new Dictionary<IntPtr, int>();
    private StackPanel workspacePanelRef; // Keep reference for visual updates
    private IntPtr lastForegroundWindow = IntPtr.Zero; // Track foreground window changes

    // Notification state
    private Ellipse _notifDot; // Blue dot indicator for unread notifications
    private bool _hasUnreadNotifications = false;
    private DispatcherTimer _notifTimer;
    private bool _isCheckingNotifs = false;

    public StatusBarWindow()
    {
        screenWidth = GetSystemMetrics(SM_CXSCREEN);
        screenHeight = GetSystemMetrics(SM_CYSCREEN);

        Title = "MacStatusBar";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        AllowsTransparency = true;
        Background = Brushes.Transparent;

        Width = screenWidth - (floatingGap * 2); // Gap on left and right
        Height = BAR_HEIGHT;
        Left = floatingGap;
        Top = floatingGap;

        // Main border is now transparent — each segment gets its own glass panel
        mainBorder = new Border
        {
            Background = Brushes.Transparent,
            ClipToBounds = false,
            Name = "MainBorder",
        };
        // Removed RegisterName as we keep direct reference

        // Context Menu
        // Create Control Center immediately
        _controlCenter = new ControlCenterWindow();

        // Right-click context menu
        ContextMenu ctxMenu = new ContextMenu();
        MenuItem settingsItem = new MenuItem { Header = "Settings" };
        settingsItem.Click += (s, e) => OpenSettings();
        ctxMenu.Items.Add(settingsItem);
        ctxMenu.Items.Add(new Separator());
        MenuItem exitItem = new MenuItem { Header = "Exit" };
        exitItem.Click += (s, e) =>
        {
            Application.Current.Shutdown();
        };
        ctxMenu.Items.Add(exitItem);
        this.ContextMenu = ctxMenu;

        BuildLayout(); // Initial Layout Build

        // Timer (checks clock, battery, wifi, and fullscreen status)
        timer = new DispatcherTimer();
        timer.Interval = TimeSpan.FromMilliseconds(100); // Fast updates for instant response
        timer.Tick += Timer_Tick;
        timer.Start();

        Loaded += StatusBarWindow_Loaded;
        Closing += StatusBarWindow_Closing;
    }

    private Border CreateGlassPanel()
    {
        // Convert double Opacity (0.0 - 1.0) to Byte (0 - 255)
        byte alpha = (byte)(settings.GlassOpacity * 255);

        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(alpha, 15, 15, 15)),
            CornerRadius = new CornerRadius(8),
            BorderBrush = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                Direction = 270,
                ShadowDepth = 4,
                BlurRadius = 12,
                Opacity = 0.4,
            },
            VerticalAlignment = VerticalAlignment.Center,
            Height = BAR_HEIGHT,
        };
    }

    private Border CreateSeparator(double leftMargin, double rightMargin)
    {
        return new Border
        {
            Width = 1,
            Height = 12,
            Background = new SolidColorBrush(Color.FromArgb(25, 255, 255, 255)),
            Margin = new Thickness(leftMargin, 0, rightMargin, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private void BuildLayout()
    {
        // Check if we need to rebuild due to layout mode OR full width change
        // We need to track the last full width state to know if it changed
        if (currentLayoutMode == settings.LayoutMode && _lastIsFullWidth == settings.IsFullWidth)
            return;

        currentLayoutMode = settings.LayoutMode;
        _lastIsFullWidth = settings.IsFullWidth;

        clockText = null;
        timeText = null;
        dateText = null;

        // --- Re-Init Common Components ---
        if (dockPanel == null)
        {
            dockPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        else
        {
            var parent = VisualTreeHelper.GetParent(dockPanel) as Panel;
            if (parent != null)
                parent.Children.Remove(dockPanel);
        }

        if (wifiCanvas == null)
        {
            wifiCanvas = new Grid
            {
                Width = 20,
                Height = 20,
                Margin = new Thickness(0, 0, 6, 0),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
            };
            
            wifiCanvas.MouseLeftButtonUp += (s, e) => OpenNetworkSettings();
        }
        else
        {
            var parent = VisualTreeHelper.GetParent(wifiCanvas) as Panel;
            if (parent != null)
                parent.Children.Remove(wifiCanvas);
        }

        if (batteryContainer == null)
        {
            batteryContainer = new Grid
            {
                Width = 36,
                Height = 20,
                Margin = new Thickness(0, 0, 6, 0),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
            };
            batteryContainer.MouseLeftButtonUp += (s, e) => OpenPowerSettings();
            UpdateBatteryUI();
        }
        else
        {
            var parent = VisualTreeHelper.GetParent(batteryContainer) as Panel;
            if (parent != null)
                parent.Children.Remove(batteryContainer);
        }

        // Initialize Wi-Fi icon if needed
        if (wifiCanvas.Children.Count == 0)
        {
            // Google Material Design Wi-Fi icon (24×24 viewbox)
            // Battle-tested path with proper proportions and spacing
            string wifiData =
                // Top arc band
                "M1,9l2,2c4.97-4.97,13.03-4.97,18,0l2-2C16.93,2.93,7.08,2.93,1,9z " +
                // Middle arc band
                "M5,13l2,2c2.76-2.76,7.24-2.76,10,0l2-2C15.14,9.14,8.87,9.14,5,13z " +
                // Bottom dot
                "M9,17l3,3l3-3c-1.65-1.66-4.34-1.66-6,0z";

            Path wifiPath = new Path
            {
                Fill = Brushes.White,
                Data = Geometry.Parse(wifiData),
            };
            Canvas wifiHolder = new Canvas { Width = 24, Height = 24 };
            wifiHolder.Children.Add(wifiPath);

            _wifiIcon = new System.Windows.Controls.Viewbox
            {
                Width = 20,
                Height = 20,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = wifiHolder,
            };
            wifiCanvas.Children.Add(_wifiIcon);

            // Disconnected: same icon but dimmed
            Path wifiPathOff = new Path
            {
                Fill = Brushes.White,
                Data = Geometry.Parse(wifiData),
            };
            Canvas offHolder = new Canvas { Width = 24, Height = 24 };
            offHolder.Children.Add(wifiPathOff);

            _wifiOffIcon = new System.Windows.Controls.Viewbox
            {
                Width = 20,
                Height = 20,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = offHolder,
                Opacity = 0,
            };
            wifiCanvas.Children.Add(_wifiOffIcon);

            NetworkChange.NetworkAvailabilityChanged += (s, ev) =>
                Dispatcher.Invoke(() => UpdateWifiUI(false));
            NetworkChange.NetworkAddressChanged += (s, ev) =>
                Dispatcher.Invoke(() => UpdateWifiUI(false));
            UpdateWifiUI(true);
        }

        // ========== LAYOUT GENERATION ==========

        // Initialize common text elements
        timeText = new TextBlock
        {
            Text = DateTime.Now.ToString("HH:mm"),
            Foreground = new SolidColorBrush(Color.FromArgb(204, 255, 255, 255)),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            FontWeight = FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        dateText = new TextBlock
        {
            Text = DateTime.Now.ToString("ddd d").ToUpper(),
            Foreground = new SolidColorBrush(Color.FromArgb(128, 255, 255, 255)),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        };

        Grid outerGrid = new Grid();

        if (settings.IsFullWidth)
        {
            // --- FULL WIDTH LAYOUT ---
            // Single glass panel spanning the whole bar
            Border fullBorder = CreateGlassPanel();
            fullBorder.HorizontalAlignment = HorizontalAlignment.Stretch; // Fill width
            fullBorder.Padding = new Thickness(12, 0, 12, 0); // Unified padding

            // Grid inside the border to hold Left, Center, Right
            Grid contentGrid = new Grid();
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Left
            contentGrid.ColumnDefinitions.Add(
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            ); // Center (Spacer)
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Right

            // Left Content
            StackPanel leftContent = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            leftContent.Children.Add(timeText);
            leftContent.Children.Add(CreateSeparator(10, 10));
            leftContent.Children.Add(dateText);

            Grid.SetColumn(leftContent, 0);
            contentGrid.Children.Add(leftContent);

            // Center Content (Workspaces + Dock) — Centered in the middle of the screen
            // But since we are using a Grid with Star in middle, we can put it in the middle column
            // and align it center.
            StackPanel centerContent = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            StackPanel wsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            RefreshWorkspaces(wsPanel);
            centerContent.Children.Add(wsPanel);

            // Smart Divider
            centerContent.Children.Add(
                new Border
                {
                    Width = 1,
                    Height = 16,
                    Background = new SolidColorBrush(Color.FromArgb(25, 255, 255, 255)),
                    Margin = new Thickness(8, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                }
            );

            dockPanel.Margin = new Thickness(0);
            centerContent.Children.Add(dockPanel);

            Grid.SetColumn(centerContent, 1);
            contentGrid.Children.Add(centerContent);

            // Right Content
            StackPanel rightContent = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            rightContent.Children.Add(wifiCanvas);
            rightContent.Children.Add(batteryContainer);

            // Notification Bell
            Grid bellBtn = new Grid
            {
                Width = 20,
                Height = 20,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                Tag = "NotificationBell",
            };
            bellBtn.MouseLeftButtonUp += (s, e) => OpenActionCenter();
            bellBtn.Children.Add(CreateBellIcon());

            _notifDot = new Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = new SolidColorBrush(Color.FromRgb(59, 130, 246)), // Blue
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 0, 0),
                Visibility = _hasUnreadNotifications ? Visibility.Visible : Visibility.Collapsed,
            };
            bellBtn.Children.Add(_notifDot);
            rightContent.Children.Add(bellBtn);

            Grid.SetColumn(rightContent, 2);
            contentGrid.Children.Add(rightContent);

            fullBorder.Child = contentGrid;
            outerGrid.Children.Add(fullBorder);

            // Store reference/Reset others
            _fullWidthSegment = fullBorder;
            _leftSegment = null;
            _centerSegment = null;
            _rightSegment = null;
        }
        else
        {
            // ========== 3-SEGMENT LAYOUT (Original) ==========
            // Top-level: a Grid that holds 3 separate glass panels

            // --- LEFT SEGMENT: Clock / Date / Tiling ---
            Border leftBorder = CreateGlassPanel();
            leftBorder.HorizontalAlignment = HorizontalAlignment.Left;
            leftBorder.Padding = new Thickness(12, 0, 12, 0);

            StackPanel leftContent = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };

            leftContent.Children.Add(timeText);

            // Separator
            leftContent.Children.Add(CreateSeparator(10, 10));

            leftContent.Children.Add(dateText);

            leftBorder.Child = leftContent;
            outerGrid.Children.Add(leftBorder);

            // --- CENTER SEGMENT: Workspaces + Dock ---
            Border centerBorder = CreateGlassPanel();
            centerBorder.HorizontalAlignment = HorizontalAlignment.Center;
            centerBorder.Padding = new Thickness(8, 0, 8, 0);

            StackPanel centerContent = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };

            StackPanel wsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            RefreshWorkspaces(wsPanel);
            centerContent.Children.Add(wsPanel);

            // Smart Divider (between workspaces and dock)
            Border dockDivider = new Border
            {
                Width = 1,
                Height = 16,
                Background = new SolidColorBrush(Color.FromArgb(25, 255, 255, 255)),
                Margin = new Thickness(8, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            centerContent.Children.Add(dockDivider);

            dockPanel.Margin = new Thickness(0);
            centerContent.Children.Add(dockPanel);

            centerBorder.Child = centerContent;
            outerGrid.Children.Add(centerBorder);

            // --- RIGHT SEGMENT: Wifi + Battery ---
            Border rightBorder = CreateGlassPanel();
            rightBorder.HorizontalAlignment = HorizontalAlignment.Right;
            rightBorder.Padding = new Thickness(12, 0, 10, 0);

            StackPanel rightContent = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };

            rightContent.Children.Add(wifiCanvas);
            rightContent.Children.Add(batteryContainer);

            // Notification Bell
            Grid bellBtn = new Grid
            {
                Width = 20,
                Height = 20,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                Tag = "NotificationBell",
            };
            bellBtn.MouseLeftButtonUp += (s, e) => OpenActionCenter();
            bellBtn.Children.Add(CreateBellIcon());

            // Notification dot
            _notifDot = new Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = new SolidColorBrush(Color.FromRgb(59, 130, 246)), // Blue
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 0, 0),
                Visibility = _hasUnreadNotifications ? Visibility.Visible : Visibility.Collapsed,
            };
            bellBtn.Children.Add(_notifDot);
            rightContent.Children.Add(bellBtn);

            rightBorder.Child = rightContent;
            outerGrid.Children.Add(rightBorder);

            // Store segment references for blur region calculation
            _leftSegment = leftBorder;
            _centerSegment = centerBorder;
            _rightSegment = rightBorder;
            _fullWidthSegment = null;
        }

        mainBorder.Child = outerGrid;
        Content = mainBorder;

        // Schedule blur to apply after layout has rendered (needs ActualWidth/Height)
        Dispatcher.BeginInvoke(
            new Action(() => EnableBlurOnSegments()),
            System.Windows.Threading.DispatcherPriority.Loaded
        );
    }

    private Path CreateTilingIcon()
    {
        Path path = new Path
        {
            Stroke = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
            StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };

        GeometryGroup group = new GeometryGroup();
        group.Children.Add(new RectangleGeometry(new Rect(4, 4, 5, 5), 1, 1));
        group.Children.Add(new RectangleGeometry(new Rect(11, 4, 5, 5), 1, 1));
        group.Children.Add(new RectangleGeometry(new Rect(11, 11, 5, 5), 1, 1));
        group.Children.Add(new RectangleGeometry(new Rect(4, 11, 5, 5), 1, 1));

        path.Data = group;
        return path;
    }

    private UIElement CreateBellIcon()
    {
        // Clean iOS-style notification bell (16×16 viewbox native)
        Path bellPath = new Path
        {
            Fill = Brushes.White,
            Data = Geometry.Parse(
                "M8 16a2 2 0 0 0 2-2H6a2 2 0 0 0 2 2zM3 6a5 5 0 0 1 10 0c0 1.098.5 6 2 7H1c1.5-1 2-5.902 2-7z"
            ),
        };

        Canvas pathHolder = new Canvas { Width = 16, Height = 16 };
        pathHolder.Children.Add(bellPath);

        System.Windows.Controls.Viewbox vb = new System.Windows.Controls.Viewbox
        {
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = pathHolder,
        };

        return vb;
    }

    // Segment border references for blur region calculation
    private Border _leftSegment,
        _centerSegment,
        _rightSegment,
        _fullWidthSegment;

    [DllImport("dwmapi.dll")]
    private static extern int DwmEnableBlurBehindWindow(IntPtr hwnd, ref DWM_BLURBEHIND blurBehind);

    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_BLURBEHIND
    {
        public uint dwFlags;
        public bool fEnable;
        public IntPtr hRgnBlur;
        public bool fTransitionOnMaximized;
    }

    private const uint DWM_BB_ENABLE = 0x01;
    private const uint DWM_BB_BLURREGION = 0x02;

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cx, int cy);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(
        IntPtr hrgnDst,
        IntPtr hrgnSrc1,
        IntPtr hrgnSrc2,
        int fnCombineMode
    );

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private const int RGN_OR = 2;

    private void EnableBlurOnSegments()
    {
        if (hwnd == IntPtr.Zero)
            return;

        // First enable acrylic on the window
        var accent = new AccentPolicy
        {
            AccentState = ACCENT_ENABLE_ACRYLICBLURBEHIND,
            AccentFlags = 0,
            GradientColor = 0x00000000, // Fully transparent - panels provide their own color
            AnimationId = 0,
        };

        int accentSize = Marshal.SizeOf(accent);
        IntPtr accentPtr = Marshal.AllocHGlobal(accentSize);
        Marshal.StructureToPtr(accent, accentPtr, false);

        var data = new WindowCompositionAttributeData
        {
            Attribute = WCA_ACCENT_POLICY,
            SizeOfData = accentSize,
            Data = accentPtr,
        };

        SetWindowCompositionAttribute(hwnd, ref data);
        Marshal.FreeHGlobal(accentPtr);

        // Now set a blur region covering only the 3 segments
        try
        {
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            IntPtr combinedRgn = IntPtr.Zero;

            if (settings.IsFullWidth && _fullWidthSegment != null)
            {
                // Full width blur region
                try
                {
                    Point pos = _fullWidthSegment.TranslatePoint(new Point(0, 0), this);
                    int x1 = (int)(pos.X * dpi);
                    int y1 = (int)(pos.Y * dpi);
                    int x2 = (int)((pos.X + _fullWidthSegment.ActualWidth) * dpi);
                    int y2 = (int)((pos.Y + _fullWidthSegment.ActualHeight) * dpi);
                    int radius = (int)(8 * dpi);

                    combinedRgn = CreateRoundRectRgn(x1, y1, x2, y2, radius, radius);
                }
                catch { }
            }
            else
            {
                // 3-Segment blur region
                Border[] segments = { _leftSegment, _centerSegment, _rightSegment };

                foreach (var seg in segments)
                {
                    if (seg == null)
                        continue;
                    try
                    {
                        Point pos = seg.TranslatePoint(new Point(0, 0), this);
                        int x1 = (int)(pos.X * dpi);
                        int y1 = (int)(pos.Y * dpi);
                        int x2 = (int)((pos.X + seg.ActualWidth) * dpi);
                        int y2 = (int)((pos.Y + seg.ActualHeight) * dpi);
                        int radius = (int)(8 * dpi); // Match CornerRadius(8)

                        IntPtr segRgn = CreateRoundRectRgn(x1, y1, x2, y2, radius, radius);

                        if (combinedRgn == IntPtr.Zero)
                        {
                            combinedRgn = segRgn;
                        }
                        else
                        {
                            CombineRgn(combinedRgn, combinedRgn, segRgn, RGN_OR);
                            DeleteObject(segRgn);
                        }
                    }
                    catch { }
                }
            }

            if (combinedRgn != IntPtr.Zero)
            {
                var blur = new DWM_BLURBEHIND
                {
                    dwFlags = DWM_BB_ENABLE | DWM_BB_BLURREGION,
                    fEnable = true,
                    hRgnBlur = combinedRgn,
                    fTransitionOnMaximized = false,
                };
                DwmEnableBlurBehindWindow(hwnd, ref blur);
                // Don't delete combinedRgn - DWM takes ownership
            }
        }
        catch { }
    }

    private void DisableBlur()
    {
        if (hwnd == IntPtr.Zero)
            return;

        var accent = new AccentPolicy
        {
            AccentState = 0, // ACCENT_DISABLED
            AccentFlags = 0,
            GradientColor = 0,
            AnimationId = 0,
        };

        int accentSize = Marshal.SizeOf(accent);
        IntPtr accentPtr = Marshal.AllocHGlobal(accentSize);
        Marshal.StructureToPtr(accent, accentPtr, false);

        var data = new WindowCompositionAttributeData
        {
            Attribute = WCA_ACCENT_POLICY,
            SizeOfData = accentSize,
            Data = accentPtr,
        };

        SetWindowCompositionAttribute(hwnd, ref data);
        Marshal.FreeHGlobal(accentPtr);

        // Also disable DWM blur
        var blur = new DWM_BLURBEHIND
        {
            dwFlags = DWM_BB_ENABLE,
            fEnable = false,
            hRgnBlur = IntPtr.Zero,
            fTransitionOnMaximized = false,
        };
        DwmEnableBlurBehindWindow(hwnd, ref blur);
    }

    private void StatusBarWindow_Loaded(object sender, RoutedEventArgs e)
    {
        hwnd = new WindowInteropHelper(this).Handle;

        // ensure Control Center has Owner so it floats above this bar
        if (_controlCenter != null)
        {
            _controlCenter.Owner = this;
        }
        callbackMsgId = RegisterWindowMessage("MacStatusBarAppBarMsg");

        RegisterAppBar();
        LoadSettings();
        ApplySettings();

        Left = floatingGap;
        Top = floatingGap;
        int barWidth = screenWidth - (floatingGap * 2);
        SetWindowPos(
            hwnd,
            HWND_TOPMOST,
            floatingGap,
            floatingGap,
            barWidth,
            BAR_HEIGHT,
            SWP_NOACTIVATE | SWP_SHOWWINDOW
        );

        // Start background polling for unread notifications
        _notifTimer = new DispatcherTimer();
        _notifTimer.Interval = TimeSpan.FromSeconds(2);
        _notifTimer.Tick += NotifTimer_Tick;
        _notifTimer.Start();
    }

    private async void NotifTimer_Tick(object sender, EventArgs e)
    {
        if (_isCheckingNotifs) return;
        _isCheckingNotifs = true;

        bool hasUnread = await Task.Run(() => CheckForUnreadNotifications());

        if (_hasUnreadNotifications != hasUnread)
        {
            _hasUnreadNotifications = hasUnread;
            if (_notifDot != null)
                _notifDot.Visibility = _hasUnreadNotifications ? Visibility.Visible : Visibility.Collapsed;
        }

        _isCheckingNotifs = false;
    }

    private bool CheckForUnreadNotifications()
    {
        try
        {
            IntPtr taskbarHwnd = FindWindow("Shell_TrayWnd", null);
            if (taskbarHwnd != IntPtr.Zero)
            {
                AutomationElement taskbar = AutomationElement.FromHandle(taskbarHwnd);
                if (taskbar != null)
                {
                    var allButtons = taskbar.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);
                    foreach (AutomationElement btn in allButtons)
                    {
                        try
                        {
                            string name = btn.Current.Name;
                            // Look for the action center button by name content
                            if (!string.IsNullOrEmpty(name) && 
                                (name.StartsWith("Action Center") || name.StartsWith("Notification Center") || name.Contains("new notification")))
                            {
                                if (name.Contains("No new notifications") || name.Contains("no new notifications"))
                                    return false;
                                
                                if (name.Contains("new notification"))
                                    return true;
                            }
                        }
                        catch { }
                    }
                }
            }
        }
        catch { }
        return false;
    }

    private void RefreshWorkspaces(StackPanel panel)
    {
        workspacePanelRef = panel;
        panel.Children.Clear();

        // Ensure we have 5 workspaces for the design
        if (settings.WorkspaceNames.Count < 5)
        {
            for (int i = settings.WorkspaceNames.Count; i < 5; i++)
                settings.WorkspaceNames.Add((i + 1).ToString());
        }

        for (int i = 0; i < 5; i++) // Fixed 5 for design fidelity
        {
            int index = i;
            bool isActive = (index == currentWorkspace);

            // Container for hit target
            Grid btnContainer = new Grid
            {
                Width = 20,
                Height = 20,
                Margin = new Thickness(0, 0, 2, 0),
                Background = Brushes.Transparent, // Hit test
                Cursor = Cursors.Hand,
            };

            // Active Background (Faint shimmer) - NOW CONDITIONAL
            if (isActive && settings.ShowActiveHighlights)
            {
                Border activeBg = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(25, 255, 255, 255)),
                    CornerRadius = new CornerRadius(4),
                };
                btnContainer.Children.Add(activeBg);
            }

            // Text Number
            TextBlock text = new TextBlock
            {
                Text = (i + 1).ToString(),
                Foreground = isActive
                    ? Brushes.White
                    : new SolidColorBrush(Color.FromArgb(102, 255, 255, 255)), // 0.4 opacity
                FontFamily = new FontFamily("Consolas"), // Monospace look
                FontSize = 14, // Match the 14px size of dock app icons seamlessly
                FontWeight = isActive ? FontWeights.Bold : FontWeights.Normal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            btnContainer.Children.Add(text);

            // Active Dot REMOVED per user request
            // The text color change remains as the indicator

            // Click Handler
            btnContainer.MouseLeftButtonUp += (s, e) => SwitchToWorkspace(index);

            panel.Children.Add(btnContainer);
        }
    }

    private void SwitchToWorkspace(int targetWorkspace)
    {
        if (targetWorkspace == currentWorkspace)
            return;

        // 1. Assign all currently visible windows to the OLD workspace
        AssignVisibleWindowsToWorkspace(currentWorkspace);

        // 2. Hide windows belonging to old workspace
        HideWindowsForWorkspace(currentWorkspace);

        // 3. Update current workspace
        currentWorkspace = targetWorkspace;

        // 4. Show windows belonging to new workspace
        ShowWindowsForWorkspace(currentWorkspace);

        // 5. Update UI (re-bold the correct indicator)
        if (workspacePanelRef != null)
            RefreshWorkspaces(workspacePanelRef);

        // 6. Refresh dock to show only current workspace windows
        UpdateDock();
    }

    private void AssignVisibleWindowsToWorkspace(int workspaceIndex)
    {
        // Enumerate all visible taskbar windows and assign them to this workspace
        EnumWindows(
            (hWnd, lParam) =>
            {
                if (IsTaskbarWindow(hWnd) && hWnd != hwnd)
                {
                    // Only assign if not already tracked
                    if (!windowWorkspaceMap.ContainsKey(hWnd))
                    {
                        windowWorkspaceMap[hWnd] = workspaceIndex;
                    }
                }
                return true;
            },
            IntPtr.Zero
        );
    }

    private void HideWindowsForWorkspace(int workspaceIndex)
    {
        // Get list of windows to hide (avoid modifying dict while iterating)
        List<IntPtr> toHide = new List<IntPtr>();
        foreach (var kvp in windowWorkspaceMap)
        {
            if (kvp.Value == workspaceIndex && kvp.Key != hwnd)
            {
                toHide.Add(kvp.Key);
            }
        }

        foreach (IntPtr hWnd in toHide)
        {
            ShowWindow(hWnd, 0); // SW_HIDE = 0
        }
    }

    private void ShowWindowsForWorkspace(int workspaceIndex)
    {
        // Get list of windows to show
        List<IntPtr> toShow = new List<IntPtr>();
        foreach (var kvp in windowWorkspaceMap)
        {
            if (kvp.Value == workspaceIndex && kvp.Key != hwnd)
            {
                toShow.Add(kvp.Key);
            }
        }

        foreach (IntPtr hWnd in toShow)
        {
            ShowWindow(hWnd, 5); // SW_SHOW = 5
        }
    }

    private void StatusBarWindow_Closing(object sender, CancelEventArgs e)
    {
        UnregisterAppBar();

        // Stop notification listener
        if (_notifTimer != null)
        {
            _notifTimer.Stop();
        }
    }

    private bool IsFullscreenAppActive()
    {
        IntPtr foregroundWindow = GetForegroundWindow();

        // If it's our own window, not fullscreen
        if (foregroundWindow == hwnd || foregroundWindow == IntPtr.Zero)
            return false;

        // Get window class name to exclude desktop/shell windows
        System.Text.StringBuilder className = new System.Text.StringBuilder(256);
        GetClassName(foregroundWindow, className, 256);
        string windowClass = className.ToString();

        // Exclude desktop and shell windows - these should NEVER trigger fullscreen hide
        if (
            windowClass == "Progman"
            || // Desktop program manager
            windowClass == "WorkerW"
            || // Desktop worker window
            windowClass == "Shell_TrayWnd"
            || // Taskbar
            windowClass == "Shell_SecondaryTrayWnd"
            || // Secondary monitor taskbar
            windowClass == "DV2ControlHost"
            || // Start menu
            windowClass == "Windows.UI.Core.CoreWindow"
        ) // Start menu / Action center
            return false;

        // Get window rectangle
        RECT windowRect;
        if (!GetWindowRect(foregroundWindow, out windowRect))
            return false;

        // Check if window covers entire screen
        int windowWidth = windowRect.right - windowRect.left;
        int windowHeight = windowRect.bottom - windowRect.top;

        // Check if it's a fullscreen window (covers entire screen)
        bool coversScreen =
            windowRect.left <= 0
            && windowRect.top <= 0
            && windowWidth >= screenWidth
            && windowHeight >= screenHeight;

        if (!coversScreen)
            return false;

        // Additional check: true fullscreen apps usually have no caption/border
        int style = GetWindowLong(foregroundWindow, GWL_STYLE);
        bool hasNoCaption = (style & WS_CAPTION) == 0;
        bool hasNoBorder = (style & WS_THICKFRAME) == 0;

        // It's fullscreen if it covers screen AND has no decorations
        return hasNoCaption || hasNoBorder;
    }

    private void Timer_Tick(object sender, EventArgs e)
    {
        // Update clock/battery/wifi every tick
        if (clockText != null)
            clockText.Text = DateTime.Now.ToString("ddd MMM d  h:mm tt");

        if (timeText != null)
            timeText.Text = DateTime.Now.ToString("h:mm tt");

        if (dateText != null)
            dateText.Text = DateTime.Now.ToString("ddd MMM d");

        UpdateBatteryUI();
        UpdateWifiUI(false);

        // Track foreground window changes - move windows from other workspaces to current
        IntPtr foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero && foreground != hwnd && foreground != lastForegroundWindow)
        {
            lastForegroundWindow = foreground;
            MoveWindowToCurrentWorkspaceIfNeeded(foreground);
        }

        UpdateDock();

        // Check fullscreen status
        bool fullscreenActive = IsFullscreenAppActive();

        if (fullscreenActive && !isHiddenForFullscreen)
        {
            // Hide the bar
            Hide();
            isHiddenForFullscreen = true;
        }
        else if (!fullscreenActive && isHiddenForFullscreen)
        {
            // Show the bar again
            Show();
            if (appBarRegistered)
                AppBarSetPos();

            isHiddenForFullscreen = false;
        }
    }

    private void MoveWindowToCurrentWorkspaceIfNeeded(IntPtr hWnd)
    {
        // Only process taskbar windows (not our own window or shell windows)
        if (!IsTaskbarWindow(hWnd))
            return;

        // Check if this window is tracked in a different workspace
        if (windowWorkspaceMap.ContainsKey(hWnd))
        {
            int windowWorkspace = windowWorkspaceMap[hWnd];
            if (windowWorkspace != currentWorkspace)
            {
                // Move window to current workspace
                windowWorkspaceMap[hWnd] = currentWorkspace;

                // Ensure window is visible (it may have been hidden when switching from its old workspace)
                ShowWindow(hWnd, 5); // SW_SHOW = 5
            }
        }
        else
        {
            // New window - assign to current workspace
            windowWorkspaceMap[hWnd] = currentWorkspace;
        }
    }

    private void RegisterAppBar()
    {
        APPBARDATA abd = new APPBARDATA();
        abd.cbSize = (uint)Marshal.SizeOf(abd);
        abd.hWnd = hwnd;
        abd.uCallbackMessage = (uint)callbackMsgId;

        uint ret = SHAppBarMessage(ABM_NEW, ref abd);
        appBarRegistered = (ret != 0);

        if (appBarRegistered)
        {
            AppBarSetPos();
        }
    }

    private void UnregisterAppBar()
    {
        if (appBarRegistered)
        {
            APPBARDATA abd = new APPBARDATA();
            abd.cbSize = (uint)Marshal.SizeOf(abd);
            abd.hWnd = hwnd;
            SHAppBarMessage(ABM_REMOVE, ref abd);
            appBarRegistered = false;
        }
    }

    private void AppBarSetPos()
    {
        APPBARDATA abd = new APPBARDATA();
        abd.cbSize = (uint)Marshal.SizeOf(abd);
        abd.hWnd = hwnd;
        abd.uEdge = ABE_TOP;

        // 1. QUERYPOS: Propose our desired rectangle
        // We want the bar height PLUS the floating gaps to be reserved
        int totalReservedHeight = BAR_HEIGHT + (floatingGap * 2);

        abd.rc.left = 0;
        abd.rc.right = screenWidth;
        abd.rc.top = 0;
        abd.rc.bottom = totalReservedHeight;

        SHAppBarMessage(ABM_QUERYPOS, ref abd);

        // 2. Adjust if Windows says we can't have that space (e.g. conflicting with another bar)
        // For TOP edge, we prioritize the height we want
        switch (abd.uEdge)
        {
            case ABE_TOP:
                abd.rc.bottom = abd.rc.top + totalReservedHeight;
                break;
        }

        // 3. SETPOS: Tell Windows effective space to reserve
        SHAppBarMessage(ABM_SETPOS, ref abd);

        // 4. Position our actual window floating INSIDE that reserved space
        // The reserved space starts at abd.rc.top and ends at abd.rc.bottom.
        // We want to be visually "floating" so we add the gap.

        int actualX = abd.rc.left + floatingGap;
        int actualY = abd.rc.top + floatingGap;
        int actualWidth = (abd.rc.right - abd.rc.left) - (floatingGap * 2);

        SetWindowPos(
            hwnd,
            HWND_TOPMOST,
            actualX,
            actualY,
            actualWidth,
            BAR_HEIGHT,
            SWP_NOACTIVATE | SWP_SHOWWINDOW
        );
    }

    // Hook into the window message loop to handle AppBar notifications
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource source = PresentationSource.FromVisual(this) as HwndSource;
        source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == callbackMsgId)
        {
            if (wParam.ToInt32() == ABM_POSCHANGED)
            {
                AppBarSetPos();
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    private void UpdateBatteryUI()
    {
        int percent = GetBatteryPercentage();
        bool isCharging = GetIsCharging();
        batteryContainer.Children.Clear();

        batteryContainer.Width = 36;
        batteryContainer.Height = 20;

        Canvas canvas = new Canvas { Width = 25, Height = 12 };

        if (!isCharging)
        {
            // Solid full battery with gap and nib
            Path body = new Path
            {
                Fill = Brushes.White,
                Data = Geometry.Parse("M 3,1 H 18 A 2,2 0 0 1 20,3 V 9 A 2,2 0 0 1 18,11 H 3 A 2,2 0 0 1 1,9 V 3 A 2,2 0 0 1 3,1 Z")
            };
            Path nib = new Path
            {
                Stroke = Brushes.White,
                StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Data = Geometry.Parse("M 23,5 V 7")
            };
            canvas.Children.Add(body);
            canvas.Children.Add(nib);

            TextBlock text = new TextBlock
            {
                Text = percent.ToString(),
                FontSize = 8.5,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.Black,
                Width = 21,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Canvas.SetLeft(text, 0);
            Canvas.SetTop(text, 0.5);
            canvas.Children.Add(text);
        }
        else
        {
            // Charging battery outline with bolt
            Path leftOutline = new Path
            {
                Stroke = Brushes.White,
                StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round,
                Data = Geometry.Parse("M 6,1 H 3 A 2,2 0 0 0 1,3 V 9 A 2,2 0 0 0 3,11 H 5")
            };
            Path rightOutline = new Path
            {
                Stroke = Brushes.White,
                StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round,
                Data = Geometry.Parse("M 15,1 H 18 A 2,2 0 0 1 20,3 V 9 A 2,2 0 0 1 18,11 H 14")
            };
            Path bolt = new Path
            {
                Stroke = Brushes.White,
                StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Data = Geometry.Parse("M 11,0 L 8,6 H 12 L 9,12")
            };
            Path nib = new Path
            {
                Stroke = Brushes.White,
                StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Data = Geometry.Parse("M 23,5 V 7")
            };
            canvas.Children.Add(leftOutline);
            canvas.Children.Add(rightOutline);
            canvas.Children.Add(bolt);
            canvas.Children.Add(nib);

            TextBlock text = new TextBlock
            {
                Text = percent.ToString(),
                FontSize = 8,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                Width = 21,
                TextAlignment = TextAlignment.Center,
                Opacity = 0.9,
            };
            Canvas.SetLeft(text, -5);
            Canvas.SetTop(text, 1);
            canvas.Children.Add(text);
        }

        System.Windows.Controls.Viewbox vb = new System.Windows.Controls.Viewbox
        {
            Width = 28,
            Height = 14,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = canvas,
        };

        batteryContainer.Children.Add(vb);
    }

    private int GetBatteryPercentage()
    {
        try
        {
            System.Management.ManagementClass wmi = new System.Management.ManagementClass(
                "Win32_Battery"
            );
            System.Management.ManagementObjectCollection allBatteries = wmi.GetInstances();
            foreach (System.Management.ManagementObject battery in allBatteries)
            {
                object charge = battery["EstimatedChargeRemaining"];
                if (charge != null)
                {
                    return Convert.ToInt32(charge);
                }
            }
        }
        catch { }
        return 100;
    }

    private bool GetIsCharging()
    {
        try
        {
            System.Management.ManagementClass wmi = new System.Management.ManagementClass(
                "Win32_Battery"
            );
            System.Management.ManagementObjectCollection allBatteries = wmi.GetInstances();
            foreach (System.Management.ManagementObject battery in allBatteries)
            {
                // BatteryStatus: 2 = AC Power / Charging
                object status = battery["BatteryStatus"];
                if (status != null)
                {
                    int s = Convert.ToInt32(status);
                    return s == 2; // 2 = charging on AC power
                }
            }
        }
        catch { }
        return false;
    }

    private void UpdateWifiUI(bool forceUpdate)
    {
        bool isConnected = NetworkInterface.GetIsNetworkAvailable();

        if (!isConnected)
        {
            _wifiIcon.Opacity = 0;
            _wifiOffIcon.Opacity = 1.0;
            return;
        }

        _wifiIcon.Opacity = 1.0;
        _wifiOffIcon.Opacity = 0;
    }

    private string GetWifiStatus()
    {
        // Deprecated
        return "";
    }

    private void OpenNetworkSettings()
    {
        ToggleControlCenter();
    }

    private void OpenPowerSettings()
    {
        ToggleControlCenter();
    }

    private void ToggleControlCenter()
    {
        if (_controlCenter == null)
        {
            MessageBox.Show("Control Center is NULL");
            return;
        }

        if (_controlCenter.IsVisible)
        {
            _controlCenter.Hide();
        }
        else
        {
            // If the Control Center was just hidden (< 500ms ago) by losing focus
            // from THIS click, don't reopen it — treat it as a toggle-close.
            if ((DateTime.UtcNow - _controlCenter.LastHideTime).TotalMilliseconds < 500)
            {
                return;
            }

            // Position Top-Right
            // Align right edge: ScreenWidth - floatingGap
            // Target Left = (ScreenWidth - floatingGap) - Width

            Point cursor = GetMousePosition();
            MonitorInfo monitor = GetMonitorFromPoint(cursor);

            // Get DPI scale for this monitor
            Point dpiScale = GetDpiScale(monitor.hMonitor);

            // Convert everything to Logical Pixels for WPF Window Positioning
            // WPF Window.Left/Top/Width/Height are in Logical Pixels (1/96th inc)

            // 1. Calculate Target Properties in Physical Pixels first
            // Since the MacStatusBar is sized via SetWindowPos (using physical pixels),
            // the boundaries are physically offset by overlapping physical dimensions
            double physicalRight = monitor.MonitorArea.right - floatingGap;
            double physicalTop = monitor.MonitorArea.top + (floatingGap * 2) + BAR_HEIGHT;

            // 2. Convert Physical target coordinates to Logical properties for WPF
            double targetLeft = (physicalRight / dpiScale.X) - _controlCenter.Width;
            double targetTop = physicalTop / dpiScale.Y;

            _controlCenter.Left = targetLeft;
            _controlCenter.Top = targetTop;

            _controlCenter.Show();
            _controlCenter.Activate();
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    private Point GetMousePosition()
    {
        POINT p;
        if (GetCursorPos(out p))
            return new Point(p.X, p.Y);
        return new Point(0, 0);
    }

    private struct MonitorInfo
    {
        public IntPtr hMonitor;
        public RECT WorkArea;
        public RECT MonitorArea;
    }

    private MonitorInfo GetMonitorFromPoint(Point p)
    {
        IntPtr hMonitor = MonitorFromPoint(
            new POINT { X = (int)p.X, Y = (int)p.Y },
            MONITOR_DEFAULTTONEAREST
        );

        MONITORINFO mi = new MONITORINFO();
        mi.cbSize = Marshal.SizeOf(mi);
        GetMonitorInfo(hMonitor, ref mi);

        return new MonitorInfo { hMonitor = hMonitor, WorkArea = mi.rcWork, MonitorArea = mi.rcMonitor };
    }

    private Point GetDpiScale(IntPtr hMonitor)
    {
        try
        {
            uint dpiX,
                dpiY;
            GetDpiForMonitor(hMonitor, 0, out dpiX, out dpiY); // 0 = MDT_EFFECTIVE_DPI
            return new Point(dpiX / 96.0, dpiY / 96.0);
        }
        catch
        {
            // Fallback for Windows 7/8 or error
            using (System.Drawing.Graphics g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero))
            {
                return new Point(g.DpiX / 96.0, g.DpiY / 96.0);
            }
        }
    }

    // Monitor APIs
    private const int MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, int dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(
        IntPtr hmonitor,
        int dpiType,
        out uint dpiX,
        out uint dpiY
    );

    private bool _acOpenedByBell = false;

    private void OpenActionCenter()
    {
        if (_acOpenedByBell)
        {
            _acOpenedByBell = false;
            // AC closes from click-outside. Don't re-send Win+A.
        }
        else
        {
            _acOpenedByBell = true;
            try
            {
                // Win+A on Windows 10, Win+N on Windows 11
                byte targetKey = (byte)(Environment.OSVersion.Version.Build >= 22000 ? 0x4E : 0x41);
                keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);       // Win down
                keybd_event(targetKey, 0, 0, UIntPtr.Zero);      // Key down
                keybd_event(targetKey, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); // Key up
                keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);  // Win up
            }
            catch { }
        }

        // Mark notifications as read — hide the dot
        _hasUnreadNotifications = false;
        if (_notifDot != null)
            _notifDot.Visibility = Visibility.Collapsed;
    }

    private void UpdateDock()
    {
        IntPtr foregroundWindow = GetForegroundWindow();
        List<IntPtr> currentWindows = new List<IntPtr>();

        // Enumerate all visible taskbar windows
        EnumWindows(
            (hWnd, lParam) =>
            {
                if (IsTaskbarWindow(hWnd) && hWnd != hwnd)
                {
                    // Only include windows belonging to current workspace (or untracked new windows)
                    bool belongsToCurrentWorkspace =
                        !windowWorkspaceMap.ContainsKey(hWnd)
                        || windowWorkspaceMap[hWnd] == currentWorkspace;

                    if (belongsToCurrentWorkspace)
                    {
                        currentWindows.Add(hWnd);

                        // Auto-assign untracked windows to current workspace
                        if (!windowWorkspaceMap.ContainsKey(hWnd))
                        {
                            windowWorkspaceMap[hWnd] = currentWorkspace;
                        }
                    }
                }
                return true;
            },
            IntPtr.Zero
        );

        // Remove closed windows from cache (stable ordering)
        cachedWindowOrder.RemoveAll(h => !currentWindows.Contains(h));

        // Add new windows to the end (preserves order for existing windows)
        foreach (IntPtr hWnd in currentWindows)
        {
            if (!cachedWindowOrder.Contains(hWnd))
            {
                cachedWindowOrder.Add(hWnd);
            }
        }

        // Clean up icon cache for closed windows
        List<IntPtr> staleIcons = new List<IntPtr>();
        foreach (IntPtr cachedHwnd in iconCache.Keys)
        {
            if (!currentWindows.Contains(cachedHwnd))
            {
                staleIcons.Add(cachedHwnd);
            }
        }
        foreach (IntPtr stale in staleIcons)
        {
            iconCache.Remove(stale);
        }

        // Rebuild dock UI
        dockPanel.Children.Clear();

        // Display icons in cached order (stable positions)
        foreach (IntPtr windowHandle in cachedWindowOrder)
        {
            bool isActive = (windowHandle == foregroundWindow);
            AddDockIcon(windowHandle, isActive);
        }
    }

    private void AddDockIcon(IntPtr windowHandle, bool isActive)
    {
        // Get cached icon or load new one
        ImageSource icon = GetOrCacheIcon(windowHandle);
        if (icon == null)
            return;

        Image iconImage = new Image
        {
            Source = icon,
            Width = 14,
            Height = 14,
            Cursor = Cursors.Hand,
            Opacity = isActive ? 1.0 : 0.45, // Inactive icons much dimmer
            Tag = windowHandle,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // White translucent background for active icons (Conditional)
        Border iconContainer = new Border
        {
            Width = 20,
            Height = 20,
            Background =
                (isActive && settings.ShowActiveHighlights)
                    ? new SolidColorBrush(Color.FromArgb(50, 255, 255, 255))
                    : Brushes.Transparent,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(0),
            Margin = new Thickness(2, 0, 2, 0),
            Child = iconImage,
            Cursor = Cursors.Hand,
        };

        iconContainer.MouseLeftButtonUp += (s, e) =>
        {
            if (IsIconic(windowHandle))
            {
                ShowWindow(windowHandle, SW_RESTORE);
            }
            SetForegroundWindow(windowHandle);
            UpdateDock();
        };

        dockPanel.Children.Add(iconContainer);
    }

    private ImageSource GetOrCacheIcon(IntPtr hWnd)
    {
        // Return cached icon if available
        if (iconCache.ContainsKey(hWnd))
            return iconCache[hWnd];

        // Load icon and cache it
        ImageSource icon = GetWindowIcon(hWnd);
        if (icon != null)
        {
            iconCache[hWnd] = icon;
        }
        return icon;
    }

    private bool IsTaskbarWindow(IntPtr hWnd)
    {
        // Must be visible
        if (!IsWindowVisible(hWnd))
            return false;

        // Must have a title
        int titleLength = GetWindowTextLength(hWnd);
        if (titleLength == 0)
            return false;

        // Get extended style
        int exStyle = GetWindowLongPtr(hWnd, GWL_EXSTYLE);

        // Skip tool windows (unless they also have WS_EX_APPWINDOW)
        if ((exStyle & WS_EX_TOOLWINDOW) != 0 && (exStyle & WS_EX_APPWINDOW) == 0)
            return false;

        // Skip windows with an owner (child windows)
        if (GetWindow(hWnd, GW_OWNER) != IntPtr.Zero)
            return false;

        // Get class name
        System.Text.StringBuilder className = new System.Text.StringBuilder(256);
        GetClassName(hWnd, className, 256);
        string windowClass = className.ToString();

        // Skip shell windows
        if (
            windowClass == "Progman"
            || windowClass == "WorkerW"
            || windowClass == "Shell_TrayWnd"
            || windowClass == "Shell_SecondaryTrayWnd"
            || windowClass == "Windows.UI.Core.CoreWindow"
        )
            return false;

        // Special handling for UWP apps (ApplicationFrameWindow)
        if (windowClass == "ApplicationFrameWindow")
        {
            // Only include if it has meaningful size (not a hidden stub)
            RECT rect;
            if (GetWindowRect(hWnd, out rect))
            {
                int width = rect.right - rect.left;
                int height = rect.bottom - rect.top;
                // Must have meaningful size (not minimized stubbed frame)
                if (width > 1 && height > 1)
                    return true;
            }
            return false;
        }

        return true;
    }

    private ImageSource GetWindowIcon(IntPtr hWnd)
    {
        try
        {
            // Check if this is a UWP app first
            System.Text.StringBuilder className = new System.Text.StringBuilder(256);
            GetClassName(hWnd, className, 256);

            uint processId = 0;
            if (className.ToString() == "ApplicationFrameWindow")
            {
                // Find the child CoreWindow to get the actual UWP app's process
                IntPtr coreWindow = FindWindowEx(
                    hWnd,
                    IntPtr.Zero,
                    "Windows.UI.Core.CoreWindow",
                    null
                );
                if (coreWindow != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(coreWindow, out processId);
                }
                else
                {
                    // CoreWindow hasn't loaded yet — the UWP app is still initializing.
                    // Return null to prevent caching a blank ApplicationFrameHost icon.
                    // The timer will naturally retry in 100ms once the app finishes loading.
                    return null;
                }

                // Try UWP-specific icon extraction first
                ImageSource uwpIcon = GetUwpAppIcon(processId);
                if (uwpIcon != null)
                    return uwpIcon;
            }
            else
            {
                // For non-UWP apps, get process ID now
                GetWindowThreadProcessId(hWnd, out processId);
            }

            // Standard Win32 icon extraction
            IntPtr hIcon = SendMessage(hWnd, WM_GETICON, ICON_SMALL2, IntPtr.Zero);
            if (hIcon == IntPtr.Zero)
                hIcon = SendMessage(hWnd, WM_GETICON, ICON_SMALL, IntPtr.Zero);
            if (hIcon == IntPtr.Zero)
                hIcon = SendMessage(hWnd, WM_GETICON, ICON_BIG, IntPtr.Zero);

            // Try class icon
            if (hIcon == IntPtr.Zero)
            {
                if (IntPtr.Size == 8)
                    hIcon = GetClassLong64(hWnd, GCL_HICONSM);
                else
                    hIcon = new IntPtr(GetClassLong32(hWnd, GCL_HICONSM));
            }
            if (hIcon == IntPtr.Zero)
            {
                if (IntPtr.Size == 8)
                    hIcon = GetClassLong64(hWnd, GCL_HICON);
                else
                    hIcon = new IntPtr(GetClassLong32(hWnd, GCL_HICON));
            }

            if (hIcon != IntPtr.Zero)
            {
                return Imaging.CreateBitmapSourceFromHIcon(
                    hIcon,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions()
                );
            }

            // Fallback: Extract icon from process executable

            string exePath = GetProcessPath(processId);
            if (!string.IsNullOrEmpty(exePath) && System.IO.File.Exists(exePath))
            {
                try
                {
                    System.Drawing.Icon exeIcon = System.Drawing.Icon.ExtractAssociatedIcon(
                        exePath
                    );
                    if (exeIcon != null)
                    {
                        return Imaging.CreateBitmapSourceFromHIcon(
                            exeIcon.Handle,
                            Int32Rect.Empty,
                            BitmapSizeOptions.FromEmptyOptions()
                        );
                    }
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    private string GetProcessPath(uint processId)
    {
        IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (hProcess == IntPtr.Zero)
            return null;

        try
        {
            System.Text.StringBuilder path = new System.Text.StringBuilder(1024);
            uint size = 1024;
            if (QueryFullProcessImageName(hProcess, 0, path, ref size))
            {
                return path.ToString();
            }
        }
        finally
        {
            CloseHandle(hProcess);
        }
        return null;
    }

    private ImageSource GetUwpAppIcon(uint processId)
    {
        IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (hProcess == IntPtr.Zero)
            return null;

        try
        {
            // Step 1: Get package full name
            uint length = 0;
            int result = GetPackageFullName(hProcess, ref length, null);
            if (result != ERROR_INSUFFICIENT_BUFFER)
                return null;

            System.Text.StringBuilder packageName = new System.Text.StringBuilder((int)length);
            result = GetPackageFullName(hProcess, ref length, packageName);
            if (result != 0)
                return null;

            // Step 2: Get package path
            uint pathLength = 0;
            result = GetPackagePathByFullName(packageName.ToString(), ref pathLength, null);
            if (result != ERROR_INSUFFICIENT_BUFFER)
                return null;

            System.Text.StringBuilder packagePath = new System.Text.StringBuilder((int)pathLength);
            result = GetPackagePathByFullName(packageName.ToString(), ref pathLength, packagePath);
            if (result != 0)
                return null;

            string installPath = packagePath.ToString();

            // Step 3: Parse AppxManifest.xml to find logo path
            string manifestPath = System.IO.Path.Combine(installPath, "AppxManifest.xml");
            if (!System.IO.File.Exists(manifestPath))
                return null;

            string logoRelativePath = null;
            try
            {
                XmlDocument doc = new XmlDocument();
                doc.Load(manifestPath);

                // Try multiple namespace patterns (Windows 8.1, 10, 10 UAP3)
                string[][] namespacePatterns = new string[][]
                {
                    new string[]
                    {
                        "uap",
                        "http://schemas.microsoft.com/appx/manifest/uap/windows10",
                    },
                    new string[] { "uap", "http://schemas.microsoft.com/appx/2013/manifest" },
                    new string[]
                    {
                        "uap3",
                        "http://schemas.microsoft.com/appx/manifest/uap/windows10/3",
                    },
                    new string[]
                    {
                        "m",
                        "http://schemas.microsoft.com/appx/manifest/foundation/windows10",
                    },
                    new string[] { "m", "http://schemas.microsoft.com/appx/2010/manifest" },
                };

                foreach (string[] ns in namespacePatterns)
                {
                    if (!string.IsNullOrEmpty(logoRelativePath))
                        break;

                    XmlNamespaceManager nsmgr = new XmlNamespaceManager(doc.NameTable);
                    nsmgr.AddNamespace(ns[0], ns[1]);

                    XmlNode visualNode = doc.SelectSingleNode(
                        "//" + ns[0] + ":VisualElements",
                        nsmgr
                    );
                    if (visualNode != null)
                    {
                        XmlAttribute logoAttr = visualNode.Attributes["Square44x44Logo"];
                        if (logoAttr != null)
                            logoRelativePath = logoAttr.Value;
                    }
                }

                // Namespace-agnostic fallback: find any VisualElements node
                if (string.IsNullOrEmpty(logoRelativePath))
                {
                    XmlNodeList allNodes = doc.GetElementsByTagName("VisualElements");
                    foreach (XmlNode node in allNodes)
                    {
                        XmlAttribute logoAttr = node.Attributes["Square44x44Logo"];
                        if (logoAttr != null)
                        {
                            logoRelativePath = logoAttr.Value;
                            break;
                        }
                    }
                }

                // Fallback to Properties/Logo (store logo)
                if (string.IsNullOrEmpty(logoRelativePath))
                {
                    XmlNodeList logoNodes = doc.GetElementsByTagName("Logo");
                    if (logoNodes.Count > 0)
                        logoRelativePath = logoNodes[0].InnerText;
                }
            }
            catch
            {
                return null;
            }

            if (string.IsNullOrEmpty(logoRelativePath))
                return null;

            // Step 4: Find the actual icon file (handle scale variants)
            string logoDir = System.IO.Path.GetDirectoryName(
                System.IO.Path.Combine(installPath, logoRelativePath)
            );
            string logoBaseName = System.IO.Path.GetFileNameWithoutExtension(logoRelativePath);
            string logoExt = System.IO.Path.GetExtension(logoRelativePath);

            // Try exact path first
            string exactPath = System.IO.Path.Combine(installPath, logoRelativePath);
            if (System.IO.File.Exists(exactPath))
            {
                return LoadPngIcon(exactPath);
            }

            // Try scale variants (prefer higher scales for better quality)
            string[] scales =
            {
                "scale-200",
                "scale-150",
                "scale-125",
                "scale-100",
                "targetsize-48",
                "targetsize-44",
                "targetsize-32",
                "targetsize-24",
            };
            string fullLogoDir = System.IO.Path.Combine(
                installPath,
                System.IO.Path.GetDirectoryName(logoRelativePath) ?? ""
            );

            if (System.IO.Directory.Exists(fullLogoDir))
            {
                foreach (string scale in scales)
                {
                    // Pattern: Logo.scale-100.png or Logo.targetsize-48.png
                    string scaledName = logoBaseName + "." + scale + logoExt;
                    string scaledPath = System.IO.Path.Combine(fullLogoDir, scaledName);
                    if (System.IO.File.Exists(scaledPath))
                    {
                        return LoadPngIcon(scaledPath);
                    }
                }

                // Try to find any matching file with the base name
                try
                {
                    string[] matchingFiles = System.IO.Directory.GetFiles(
                        fullLogoDir,
                        logoBaseName + "*" + logoExt
                    );
                    if (matchingFiles.Length > 0)
                    {
                        // Prefer files with higher scale numbers
                        System.Array.Sort(matchingFiles);
                        System.Array.Reverse(matchingFiles);
                        return LoadPngIcon(matchingFiles[0]);
                    }
                }
                catch { }
            }

            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }

    private ImageSource LoadPngIcon(string path)
    {
        try
        {
            BitmapImage bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze(); // Make it thread-safe
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    public static class WlanApi
    {
        [DllImport("wlanapi.dll")]
        public static extern uint WlanOpenHandle(
            uint dwClientVersion,
            IntPtr pReserved,
            out uint pdwNegotiatedVersion,
            out IntPtr phClientHandle
        );

        [DllImport("wlanapi.dll")]
        public static extern uint WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

        [DllImport("wlanapi.dll")]
        public static extern uint WlanEnumInterfaces(
            IntPtr hClientHandle,
            IntPtr pReserved,
            out IntPtr ppInterfaceList
        );

        [DllImport("wlanapi.dll")]
        public static extern void WlanFreeMemory(IntPtr pMemory);

        [DllImport("wlanapi.dll")]
        public static extern uint WlanQueryInterface(
            IntPtr hClientHandle,
            ref Guid pInterfaceGuid,
            int OpCode,
            IntPtr pReserved,
            out uint pdwDataSize,
            out IntPtr ppData,
            out int pWlanOpcodeValueType
        );

        [StructLayout(LayoutKind.Sequential)]
        public struct WLAN_INTERFACE_INFO_LIST
        {
            public uint dwNumberOfItems;
            public uint dwIndex;
            public WLAN_INTERFACE_INFO InterfaceInfo; // Array of 1 for simplicity
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WLAN_INTERFACE_INFO
        {
            public Guid InterfaceGuid;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string strInterfaceDescription;
            public int isState;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WLAN_CONNECTION_ATTRIBUTES
        {
            public int isState;
            public int wlanConnectionMode;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string strProfileName;
            public WLAN_ASSOCIATION_ATTRIBUTES wlanAssociationAttributes;
            public WLAN_SECURITY_ATTRIBUTES wlanSecurityAttributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WLAN_ASSOCIATION_ATTRIBUTES
        {
            public DOT11_SSID dot11Ssid;
            public int dot11BssType;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
            public byte[] dot11Bssid;
            public int dot11PhyType;
            public uint uDot11PhyIndex;
            public uint wlanSignalQuality;
            public uint ulRxRate;
            public uint ulTxRate;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WLAN_SECURITY_ATTRIBUTES
        {
            public bool bSecurityEnabled;
            public bool bOneXEnabled;
            public int dot11AuthAlgorithm;
            public int dot11CipherAlgorithm;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DOT11_SSID
        {
            public uint uSSIDLength;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] ucSSID;
        }

        // Simplification: Just get signal quality of first interface
        public static int GetSignalQuality()
        {
            IntPtr hClient = IntPtr.Zero;
            IntPtr pIntfList = IntPtr.Zero;
            IntPtr pData = IntPtr.Zero;
            try
            {
                uint vers;
                if (WlanOpenHandle(2, IntPtr.Zero, out vers, out hClient) != 0)
                    return 0;

                if (WlanEnumInterfaces(hClient, IntPtr.Zero, out pIntfList) != 0)
                    return 0;

                // Read list
                WLAN_INTERFACE_INFO_LIST list = (WLAN_INTERFACE_INFO_LIST)
                    Marshal.PtrToStructure(pIntfList, typeof(WLAN_INTERFACE_INFO_LIST));

                if (list.dwNumberOfItems == 0)
                    return 0;

                // Get Guid of first interface SAFE way
                IntPtr current = new IntPtr(pIntfList.ToInt64() + 8); // Skip count & index
                WLAN_INTERFACE_INFO info = (WLAN_INTERFACE_INFO)
                    Marshal.PtrToStructure(current, typeof(WLAN_INTERFACE_INFO));

                Guid guid = info.InterfaceGuid;

                // 6 = wlan_intf_opcode_media_specific_attributes
                uint dataSize;
                int opcodeValueType;
                if (
                    WlanQueryInterface(
                        hClient,
                        ref guid,
                        6,
                        IntPtr.Zero,
                        out dataSize,
                        out pData,
                        out opcodeValueType
                    ) != 0
                )
                    return 0;

                // Verify size to prevent AV
                // Use a safe minimum size check (at least up to signal quality)
                if (dataSize < 500) // Expect ~600+ usually, but definitely not extremely small
                    return 0;

                // Safe marshalling
                try
                {
                    WLAN_CONNECTION_ATTRIBUTES attr = (WLAN_CONNECTION_ATTRIBUTES)
                        Marshal.PtrToStructure(pData, typeof(WLAN_CONNECTION_ATTRIBUTES));
                    uint quality = attr.wlanAssociationAttributes.wlanSignalQuality;
                    if (quality > 100)
                        return 0;
                    return (int)quality;
                }
                catch
                {
                    return 0;
                }
            }
            catch
            {
                return 0;
            }
            finally
            {
                if (pData != IntPtr.Zero)
                    WlanFreeMemory(pData);
                if (pIntfList != IntPtr.Zero)
                    WlanFreeMemory(pIntfList);
                if (hClient != IntPtr.Zero)
                    WlanCloseHandle(hClient, IntPtr.Zero);
            }
        }
    }

    private void OpenSettings()
    {
        SettingsWindow settingsWindow = new SettingsWindow(settings);
        settingsWindow.SettingsChanged += (s, e) =>
        {
            settings = e.NewSettings;
            SaveSettings();
            ApplySettings();
        };
        settingsWindow.ShowDialog();
    }

    private void LoadSettings()
    {
        try
        {
            if (System.IO.File.Exists("MacStatusBar.ini"))
            {
                string[] lines = System.IO.File.ReadAllLines("MacStatusBar.ini");
                foreach (string line in lines)
                {
                    if (line.StartsWith("IsFloating="))
                        bool.TryParse(line.Substring(11), out settings.IsFloating);
                    else if (line.StartsWith("IsRounded="))
                        bool.TryParse(line.Substring(10), out settings.IsRounded);
                    else if (line.StartsWith("GapSize="))
                        int.TryParse(line.Substring(8), out settings.GapSize);
                    else if (line.StartsWith("UseAmoledBlack="))
                        bool.TryParse(line.Substring(15), out settings.UseAmoledBlack);
                    else if (line.StartsWith("LayoutMode="))
                        int.TryParse(line.Substring(11), out settings.LayoutMode);
                    else if (line.StartsWith("EnableTransparency="))
                        bool.TryParse(line.Substring(19), out settings.EnableTransparency);
                    else if (line.StartsWith("WorkspaceNames="))
                        settings.WorkspaceNames = new List<string>(
                            line.Substring(15)
                                .Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                        );
                    else if (line.StartsWith("IsFullWidth="))
                        bool.TryParse(line.Substring(12), out settings.IsFullWidth);
                    else if (line.StartsWith("GlassOpacity="))
                        double.TryParse(line.Substring(13), out settings.GlassOpacity);
                    else if (line.StartsWith("ShowActiveHighlights="))
                        bool.TryParse(line.Substring(21), out settings.ShowActiveHighlights);
                }
            }
        }
        catch { }
    }

    private void SaveSettings()
    {
        try
        {
            using (System.IO.StreamWriter writer = new System.IO.StreamWriter("MacStatusBar.ini"))
            {
                writer.WriteLine("IsFloating=" + settings.IsFloating);
                writer.WriteLine("IsRounded=" + settings.IsRounded);
                writer.WriteLine("GapSize=" + settings.GapSize);
                writer.WriteLine("UseAmoledBlack=" + settings.UseAmoledBlack);
                writer.WriteLine("LayoutMode=" + settings.LayoutMode);
                writer.WriteLine("EnableTransparency=" + settings.EnableTransparency);
                writer.WriteLine("IsFullWidth=" + settings.IsFullWidth);
                writer.WriteLine("GlassOpacity=" + settings.GlassOpacity);
                writer.WriteLine("ShowActiveHighlights=" + settings.ShowActiveHighlights);
                writer.WriteLine("WorkspaceNames=" + string.Join(",", settings.WorkspaceNames));
            }
        }
        catch { }
    }

    private void ApplySettings()
    {
        floatingGap = settings.IsFloating ? settings.GapSize : 0;
        isRounded = settings.IsRounded;

        // Update visuals — mainBorder is transparent; segments have their own glass styling
        if (mainBorder != null)
        {
            mainBorder.Background = Brushes.Transparent;
            mainBorder.CornerRadius = new CornerRadius(0);
        }

        // FORCE REDRAW of Glass Panels with new Opacity
        // Since CreateGlassPanel reads settings.GlassOpacity, we need to re-apply the background brush
        byte alpha = (byte)(settings.GlassOpacity * 255);
        SolidColorBrush newGlassBrush = new SolidColorBrush(Color.FromArgb(alpha, 15, 15, 15));

        if (_leftSegment != null)
            _leftSegment.Background = newGlassBrush;
        if (_centerSegment != null)
            _centerSegment.Background = newGlassBrush;
        if (_rightSegment != null)
            _rightSegment.Background = newGlassBrush;
        if (_fullWidthSegment != null)
            _fullWidthSegment.Background = newGlassBrush;

        // Apply transparency/blur setting
        if (settings.EnableTransparency)
        {
            EnableBlurOnSegments();
        }
        else
        {
            DisableBlur();
        }

        // Update position and work area
        int barWidth = (originalWorkArea.right - originalWorkArea.left) - (floatingGap * 2);

        // Window Position:
        // X = Original Left + Gap
        // Y = Original Top + Gap
        int xPos = originalWorkArea.left + floatingGap;
        int yPos = originalWorkArea.top + floatingGap;

        SetWindowPos(
            hwnd,
            HWND_TOPMOST,
            xPos,
            yPos,
            barWidth,
            BAR_HEIGHT,
            SWP_NOACTIVATE | SWP_SHOWWINDOW
        );

        // Update work area (AppBar)
        if (appBarRegistered)
            AppBarSetPos();

        // Check if layout needs rebuild
        // Also check if Full Width setting changed (comparing against current logic would require storing prev state,
        // but simplest is to just rebuild if the structure doesn't match current settings)
        // For now, we'll force rebuild if IsFullWidth changed.
        // But since we don't track "prevIsFullWidth" easily here without extra fields,
        // let's just rely on BuildLayout handling it or force it.
        // Better: layout mode check + explicit full width check logic
        // For simplicity, we'll just call BuildLayout(). If it matches, it might return early,
        // NOT SO FAST: BuildLayout only checks settings.LayoutMode.
        // We need to force it if IsFullWidth changed.
        // Let's modify BuildLayout to check IsFullWidth too.

        BuildLayout();
        UpdateDock();

        // Sync Control Center settings
        if (_controlCenter != null)
        {
            _controlCenter.SetRoundedCorners(settings.IsRounded);
        }
    }
}

// Duplicate AppSettings removed

public class InputBox : Window
{
    private TextBox textBox;
    public string ResponseText { get; private set; }

    public InputBox(string title, string defaultText)
    {
        Title = title;
        Width = 300;
        Height = 150;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.ToolWindow;
        Background = new SolidColorBrush(Color.FromRgb(40, 40, 40));

        StackPanel panel = new StackPanel { Margin = new Thickness(10) };

        textBox = new TextBox
        {
            Text = defaultText,
            Margin = new Thickness(0, 0, 0, 10),
            Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
            Foreground = Brushes.White,
            Padding = new Thickness(5),
        };
        panel.Children.Add(textBox);

        StackPanel buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        Button okButton = new Button
        {
            Content = "OK",
            Width = 70,
            Margin = new Thickness(0, 0, 10, 0),
            IsDefault = true,
        };
        okButton.Click += (s, e) =>
        {
            ResponseText = textBox.Text;
            DialogResult = true;
        };

        Button cancelButton = new Button
        {
            Content = "Cancel",
            Width = 70,
            IsCancel = true,
        };

        buttonPanel.Children.Add(okButton);
        buttonPanel.Children.Add(cancelButton);
        panel.Children.Add(buttonPanel);

        Content = panel;

        textBox.Focus();
        textBox.SelectAll();
    }
}

public class SettingsWindow : Window
{
    public event EventHandler<SettingsChangedEventArgs> SettingsChanged;
    private AppSettings currentSettings;
    private CheckBox floatingCheckBox;
    private CheckBox roundedCheckBox;
    private CheckBox amoledCheckBox;
    private CheckBox transparencyCheckBox;
    private CheckBox fullWidthCheckBox;
    private CheckBox highlightsCheckBox;
    private ComboBox layoutComboBox;
    private Slider gapSlider;
    private TextBlock gapValueText;

    public SettingsWindow(AppSettings settings)
    {
        currentSettings = settings;
        Title = "Settings";
        Width = 350;
        Height = 550; // Increased height to ensure all options fit
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(40, 40, 40));
        Foreground = Brushes.White;

        StackPanel mainPanel = new StackPanel { Margin = new Thickness(20) };

        TextBlock title = new TextBlock
        {
            Text = "Unite Bar Settings",
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 15),
        };
        mainPanel.Children.Add(title);

        fullWidthCheckBox = new CheckBox
        {
            Content = "Full Width Bar (No Gaps)",
            IsChecked = currentSettings.IsFullWidth,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 10),
        };
        fullWidthCheckBox.Checked += OnSettingsChanged;
        fullWidthCheckBox.Unchecked += OnSettingsChanged;
        mainPanel.Children.Add(fullWidthCheckBox);

        floatingCheckBox = new CheckBox
        {
            Content = "Floating Mode",
            IsChecked = currentSettings.IsFloating,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 10),
        };
        floatingCheckBox.Checked += OnSettingsChanged;
        floatingCheckBox.Unchecked += OnSettingsChanged;
        mainPanel.Children.Add(floatingCheckBox);

        roundedCheckBox = new CheckBox
        {
            Content = "Rounded Corners",
            IsChecked = currentSettings.IsRounded,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 20),
        };
        roundedCheckBox.Checked += OnSettingsChanged;
        roundedCheckBox.Unchecked += OnSettingsChanged;
        mainPanel.Children.Add(roundedCheckBox);

        amoledCheckBox = new CheckBox
        {
            Content = "AMOLED Black Theme",
            IsChecked = currentSettings.UseAmoledBlack,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 20),
        };
        amoledCheckBox.Checked += OnSettingsChanged;
        amoledCheckBox.Unchecked += OnSettingsChanged;
        mainPanel.Children.Add(amoledCheckBox);

        transparencyCheckBox = new CheckBox
        {
            Content = "Enable Transparency / Blur",
            IsChecked = currentSettings.EnableTransparency,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 20),
        };
        transparencyCheckBox.Checked += OnSettingsChanged;
        transparencyCheckBox.Unchecked += OnSettingsChanged;
        mainPanel.Children.Add(transparencyCheckBox);

        highlightsCheckBox = new CheckBox
        {
            Content = "Show Active Highlights (Workspaces/Dock)",
            IsChecked = currentSettings.ShowActiveHighlights,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 20),
        };
        highlightsCheckBox.Checked += OnSettingsChanged;
        highlightsCheckBox.Unchecked += OnSettingsChanged;
        mainPanel.Children.Add(highlightsCheckBox);

        // Layout Mode
        TextBlock layoutLabel = new TextBlock
        {
            Text = "Layout Style:",
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 5),
        };
        mainPanel.Children.Add(layoutLabel);

        layoutComboBox = new ComboBox
        {
            Width = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 20),
        };
        layoutComboBox.Items.Add("Standard (Clock Right)");
        layoutComboBox.Items.Add("Unified Center");
        layoutComboBox.SelectedIndex = currentSettings.LayoutMode;
        layoutComboBox.SelectionChanged += OnSettingsChanged;
        mainPanel.Children.Add(layoutComboBox);

        // Gap Slider
        TextBlock gapLabel = new TextBlock
        {
            Text = "Gap Size:",
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 5),
        };
        mainPanel.Children.Add(gapLabel);

        StackPanel sliderPanel = new StackPanel { Orientation = Orientation.Horizontal };

        gapSlider = new Slider
        {
            Minimum = 0,
            Maximum = 30,
            Value = currentSettings.GapSize,
            Width = 200,
            IsSnapToTickEnabled = true,
            TickFrequency = 1,
            Margin = new Thickness(0, 0, 10, 0),
        };
        gapSlider.ValueChanged += OnSettingsChanged;
        sliderPanel.Children.Add(gapSlider);

        gapValueText = new TextBlock
        {
            Text = currentSettings.GapSize.ToString(),
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 30,
        };
        sliderPanel.Children.Add(gapValueText);
        mainPanel.Children.Add(sliderPanel);

        // --- Transparency Slider ---
        TextBlock opacityLabel = new TextBlock
        {
            Text = "Background Opacity:",
            Foreground = Brushes.White,
            Margin = new Thickness(0, 20, 0, 5),
        };
        mainPanel.Children.Add(opacityLabel);

        StackPanel opacityPanel = new StackPanel { Orientation = Orientation.Horizontal };

        Slider opacitySlider = new Slider
        {
            Minimum = 0.0,
            Maximum = 1.0,
            Value = currentSettings.GlassOpacity,
            Width = 160,
            IsSnapToTickEnabled = false,
            TickFrequency = 0.01,
            Margin = new Thickness(0, 0, 10, 0),
        };

        TextBlock opacityValueText = new TextBlock
        {
            Text = (currentSettings.GlassOpacity * 100).ToString("F0") + "%",
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 40,
        };

        opacitySlider.ValueChanged += (s, e) =>
        {
            currentSettings.GlassOpacity = opacitySlider.Value;
            opacityValueText.Text = (currentSettings.GlassOpacity * 100).ToString("F0") + "%";
            SettingsChanged?.Invoke(this, new SettingsChangedEventArgs(currentSettings));
        };

        opacityPanel.Children.Add(opacitySlider);
        opacityPanel.Children.Add(opacityValueText);

        // Reset Button
        Button resetButton = new Button
        {
            Content = "Reset",
            Width = 50,
            Height = 24,
            Margin = new Thickness(10, 0, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
        };
        resetButton.Click += (s, e) =>
        {
            opacitySlider.Value = 0.85; // Default
        };
        opacityPanel.Children.Add(resetButton);

        mainPanel.Children.Add(opacityPanel);

        // Close Button
        Button closeButton = new Button
        {
            Content = "Close",
            Width = 80,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 30, 0, 0),
        };
        closeButton.Click += (s, e) => Close();
        mainPanel.Children.Add(closeButton);

        // ScrollViewer for safety if window is small
        ScrollViewer scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = mainPanel,
        };

        Content = scrollViewer;
    }

    private void OnSettingsChanged(object sender, RoutedEventArgs e)
    {
        currentSettings.IsFloating = floatingCheckBox.IsChecked ?? true;
        currentSettings.IsRounded = roundedCheckBox.IsChecked ?? true;
        currentSettings.UseAmoledBlack = amoledCheckBox.IsChecked ?? true;
        currentSettings.EnableTransparency = transparencyCheckBox.IsChecked ?? true;
        currentSettings.IsFullWidth = fullWidthCheckBox.IsChecked ?? false;
        currentSettings.ShowActiveHighlights = highlightsCheckBox.IsChecked ?? true;
        currentSettings.LayoutMode = layoutComboBox.SelectedIndex;
        currentSettings.GapSize = (int)gapSlider.Value;

        if (gapValueText != null)
            gapValueText.Text = currentSettings.GapSize.ToString();

        EventHandler<SettingsChangedEventArgs> handler = SettingsChanged;
        if (handler != null)
        {
            handler(this, new SettingsChangedEventArgs(currentSettings));
        }
    }
}

public class SettingsChangedEventArgs : EventArgs
{
    public AppSettings NewSettings { get; private set; }

    public SettingsChangedEventArgs(AppSettings settings)
    {
        NewSettings = settings;
    }
}

public class AppSettings
{
    public bool IsFloating = true;
    public bool IsRounded = true;
    public bool UseAmoledBlack = true;
    public bool EnableTransparency = true;
    public bool IsFullWidth = false;
    public int GapSize = 8;
    public double GlassOpacity = 0.85; // Default Alpha ~217/255
    public bool ShowActiveHighlights = true;

    // 0 = Standard, 1 = Unified Center
    public int LayoutMode = 1;
    public List<string> WorkspaceNames;

    public AppSettings()
    {
        WorkspaceNames = new List<string>();
        WorkspaceNames.Add("1");
        WorkspaceNames.Add("2");
        WorkspaceNames.Add("3");
        WorkspaceNames.Add("4");
        WorkspaceNames.Add("5");
    }
}

public class ControlCenterWindow : Window
{
    private Grid _rootGrid;
    private Border _contentBorder;
    private Border _wifiBorder;
    private Border _brightnessBorder;
    private Border _volumeBorder;
    private StackPanel _wifiListPanel;

    public DateTime LastHideTime { get; set; } = DateTime.MinValue;

    private List<Border> _tileBorders = new List<Border>();
    private double _brightnessValue;
    private double _volumeValue;
    private Border _brightnessFillBorder;
    private Border _volumeFillBorder;
    private bool _isDraggingSlider = false;
    private IAudioEndpointVolume _audioEndpointVolume;
    private AudioVolumeCallback _audioCallback;
    private System.Management.ManagementEventWatcher _brightnessWatcher;

    public ControlCenterWindow()
    {
        _brightnessValue = GetCurrentBrightness();
        _volumeValue = GetCurrentVolume();

        _audioEndpointVolume = GetAudioEndpointVolume();
        if (_audioEndpointVolume != null)
        {
            _audioCallback = new AudioVolumeCallback(vol =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    if (!_isDraggingSlider)
                    {
                        _volumeValue = vol;
                        if (_volumeFillBorder != null)
                        {
                            double trackHeight = 216;
                            _volumeFillBorder.Height = _volumeValue * trackHeight;
                            _volumeFillBorder.CornerRadius = _volumeValue >= 0.98 ? new CornerRadius(12) : new CornerRadius(0, 0, 12, 12);
                        }
                    }
                });
            });
            _audioEndpointVolume.RegisterControlChangeNotify(_audioCallback);
        }

        try
        {
            var query = new System.Management.WqlEventQuery("SELECT * FROM WmiMonitorBrightnessEvent");
            _brightnessWatcher = new System.Management.ManagementEventWatcher(new System.Management.ManagementScope(@"\\.\root\wmi"), query);
            _brightnessWatcher.EventArrived += (s, e) =>
            {
                if (e.NewEvent.Properties["Brightness"] != null)
                {
                    byte b = (byte)e.NewEvent.Properties["Brightness"].Value;
                    Dispatcher.InvokeAsync(() =>
                    {
                        if (!_isDraggingSlider)
                        {
                            _brightnessValue = b / 100.0;
                            if (_brightnessFillBorder != null)
                            {
                                double trackHeight = 216;
                                _brightnessFillBorder.Height = _brightnessValue * trackHeight;
                                _brightnessFillBorder.CornerRadius = _brightnessValue >= 0.98 ? new CornerRadius(12) : new CornerRadius(0, 0, 12, 12);
                            }
                        }
                    });
                }
            };
            _brightnessWatcher.Start();
        } catch { }

        this.IsVisibleChanged += (s, e) => 
        {
            if (this.Visibility == Visibility.Visible)
            {
                SyncSliders();
            }
        };

        Title = "Control Center";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        Width = 292;
        Height = 292;
        Background = Brushes.Transparent;
        AllowsTransparency = true;

        // Main layout - compact with less margins
        Grid mainGrid = new Grid();
        mainGrid.RowDefinitions.Add(
            new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }
        );
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        mainGrid.Margin = new Thickness(10);

        // TOP ROW: Left tiles | Right vertical sliders
        Grid topRow = new Grid();
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        topRow.ColumnDefinitions.Add(
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
        );

        // LEFT: 4 vertical tiles with icons
        StackPanel leftPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Stretch };
        leftPanel.Children.Add(CreateVerticalTile("\uD83D\uDCF6", "Internet", "Connected", true)); // 📶
        leftPanel.Children.Add(CreateVerticalTile("\u0042", "Bluetooth", "On", false)); // B symbol
        leftPanel.Children.Add(CreateVerticalTile("\uD83D\uDCF1", "Hotspot", "Off", false)); // 📱
        leftPanel.Children.Add(CreateVerticalTile("\uE774", "VPN", "Not Connected", false, true)); // 🌐
        Grid.SetColumn(leftPanel, 0);
        topRow.Children.Add(leftPanel);

        // RIGHT: 2 tall vertical sliders
        StackPanel sliderPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(8, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        sliderPanel.Children.Add(CreateVerticalSlider("\u2600", true)); // ☀ Brightness
        sliderPanel.Children.Add(CreateVerticalSlider("\uD83D\uDD0A", false, true)); // 🔊 Volume
        Grid.SetColumn(sliderPanel, 1);
        topRow.Children.Add(sliderPanel);

        Grid.SetRow(topRow, 0);
        mainGrid.Children.Add(topRow);

        // BOTTOM ROW: 5 equal tiles perfectly aligned with top modules mapped individually
        StackPanel bottomRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        bottomRow.Children.Add(CreateSmallTile("\uE709", "Airplane Mode", false, true));
        bottomRow.Children.Add(CreateSmallTile("\uEBAA", "Battery Saver", false));
        bottomRow.Children.Add(CreateSmallTile("\uE708", "Night Light", false));
        bottomRow.Children.Add(CreateSmallTile("\uE776", "Accessibility", false));
        bottomRow.Children.Add(CreateSmallTile("\uEBC6", "Cast", false));

        Grid.SetRow(bottomRow, 1);
        mainGrid.Children.Add(bottomRow);

        _contentBorder = new Border
        {
            Child = mainGrid,
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromRgb(28, 28, 28)),
        };

        _rootGrid = new Grid();
        _rootGrid.Children.Add(_contentBorder);

        _wifiBorder = CreateWifiPanel();
        _wifiBorder.Visibility = Visibility.Collapsed;
        _rootGrid.Children.Add(_wifiBorder);

        _brightnessBorder = CreateSliderDetailPanel(true);
        _brightnessBorder.Visibility = Visibility.Collapsed;
        _rootGrid.Children.Add(_brightnessBorder);

        _volumeBorder = CreateSliderDetailPanel(false);
        _volumeBorder.Visibility = Visibility.Collapsed;
        _rootGrid.Children.Add(_volumeBorder);

        Content = _rootGrid;
        Deactivated += (s, e) =>
        {
            LastHideTime = DateTime.UtcNow;
            Hide();
            // Reset to main view on hide
            _wifiBorder.Visibility = Visibility.Collapsed;
            _brightnessBorder.Visibility = Visibility.Collapsed;
            _volumeBorder.Visibility = Visibility.Collapsed;
            _contentBorder.Visibility = Visibility.Visible;
        };
    }

    public void SetRoundedCorners(bool isRounded)
    {
        if (_contentBorder != null)
            _contentBorder.CornerRadius = new CornerRadius(isRounded ? 12 : 0);
        foreach (var tile in _tileBorders)
            tile.CornerRadius = new CornerRadius(isRounded ? 8 : 0);
    }

    // --- Acrylic Fallback for Win10 ---
    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(
        IntPtr hwnd,
        ref WindowCompositionAttribData data
    );

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttribData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicyData
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    private void ControlCenterWindow_Loaded(object sender, RoutedEventArgs e)
    {
        IntPtr ccHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (ccHwnd == IntPtr.Zero)
            return;

        var accent = new AccentPolicyData
        {
            AccentState = 4, // ACCENT_ENABLE_ACRYLICBLURBEHIND
            AccentFlags = 0,
            GradientColor = 0x00000000,
            AnimationId = 0,
        };
        int size = Marshal.SizeOf(accent);
        IntPtr ptr = Marshal.AllocHGlobal(size);
        Marshal.StructureToPtr(accent, ptr, false);
        var data = new WindowCompositionAttribData
        {
            Attribute = 19, // WCA_ACCENT_POLICY
            SizeOfData = size,
            Data = ptr,
        };
        SetWindowCompositionAttribute(ccHwnd, ref data);
        Marshal.FreeHGlobal(ptr);
    }

    private Border CreateVerticalTile(string icon, string title, string status, bool state, bool isLast = false)
    {
        Border card = new Border
        {
            Height = 48,
            Margin = new Thickness(0, 0, 0, isLast ? 0 : 8),
            Background = new SolidColorBrush(
                state ? Color.FromRgb(255, 255, 255) : Color.FromRgb(55, 55, 55)
            ),
            CornerRadius = new CornerRadius(8),
            Cursor = string.IsNullOrEmpty(title) ? Cursors.Arrow : Cursors.Hand,
        };
        _tileBorders.Add(card);

        if (string.IsNullOrEmpty(title))
            return card;

        Grid content = new Grid();
        content.ColumnDefinitions.Add(
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
        );
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });

        StackPanel textPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };

        // Icon
        if (!string.IsNullOrEmpty(icon))
        {
            textPanel.Children.Add(
                new TextBlock
                {
                    Text = icon,
                    FontSize = 14,
                    Foreground = state ? Brushes.Black : Brushes.White,
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                }
            );
        }

        // Title + Status stacked
        StackPanel labelStack = new StackPanel();
        labelStack.Children.Add(
            new TextBlock
            {
                Text = title,
                FontWeight = FontWeights.SemiBold,
                Foreground = state ? Brushes.Black : Brushes.White,
                FontSize = 12,
            }
        );
        if (!string.IsNullOrEmpty(status))
            labelStack.Children.Add(
                new TextBlock
                {
                    Text = status,
                    FontSize = 10,
                    Opacity = 0.6,
                    Foreground = state ? Brushes.Black : Brushes.White,
                }
            );
        textPanel.Children.Add(labelStack);

        Grid.SetColumn(textPanel, 0);
        content.Children.Add(textPanel);

        // Arrow icon
        TextBlock arrow = new TextBlock
        {
            Text = "\uE76C", // Segoe MDL2 Chevron Right
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            Foreground = state ? Brushes.Black : Brushes.White,
            Opacity = 0.6,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        // Arrow hit area (Fluent Hover Feedback with glow)
        Border arrowHitbox = new Border
        {
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            CornerRadius = new CornerRadius(0, 8, 8, 0),
            Child = arrow,
        };
        arrowHitbox.MouseEnter += (s, e) =>
        {
            arrowHitbox.Background = state ? new SolidColorBrush(Color.FromArgb(20, 0, 0, 0)) : new SolidColorBrush(Color.FromArgb(20, 255, 255, 255));
            arrow.Foreground = state ? Brushes.Black : Brushes.White;
            arrow.Opacity = 1.0;
            arrowHitbox.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Color.FromRgb(96, 165, 250),
                BlurRadius = 12,
                ShadowDepth = 0,
                Opacity = 0.6,
            };
        };
        arrowHitbox.MouseLeave += (s, e) =>
        {
            arrowHitbox.Background = Brushes.Transparent;
            arrow.Foreground = state ? Brushes.Black : Brushes.White;
            arrow.Opacity = 0.6;
            arrowHitbox.Effect = null;
        };
        Grid.SetColumn(arrowHitbox, 1);
        content.Children.Add(arrowHitbox);

        // Put the visual divider back!
        Border separator = new Border
        {
            Width = 1,
            Background = state ? new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)) : new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), // Extremely subtle opacity
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 12, 0, 12) // Native centered padding line
        };
        Grid.SetColumn(separator, 1);
        content.Children.Add(separator);

        card.Child = content;

        // Split click logic
        arrowHitbox.MouseLeftButtonUp += (s, e) =>
        {
            if (title == "Internet")
            {
                // Switch to custom Wi-Fi panel
                _contentBorder.Visibility = Visibility.Collapsed;
                _wifiBorder.Visibility = Visibility.Visible;
                LoadWifiNetworks();
                e.Handled = true;
            }
        };

        // Text panel handles the state toggle
        textPanel.Background = Brushes.Transparent;
        textPanel.MouseLeftButtonUp += (s, e) =>
        {
            state = !state;
            card.Background = new SolidColorBrush(
                state ? Color.FromRgb(255, 255, 255) : Color.FromRgb(55, 55, 55)
            );
            separator.Background = state ? new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)) : new SolidColorBrush(Color.FromArgb(80, 255, 255, 255));

            // Update text colors
            foreach (var child in content.Children)
            {
                if (child is StackPanel sp)
                    foreach (var c in sp.Children)
                        if (c is TextBlock tb)
                            tb.Foreground = state ? Brushes.Black : Brushes.White;
                if (child is Border b && b.Child is TextBlock t)
                    t.Foreground = state ? Brushes.Black : Brushes.White;
            }
            e.Handled = true;
        };

        return card;
    }

    private StackPanel CreateVerticalSlider(string icon, bool isBrightness, bool isLast = false)
    {
        double sliderValue = isBrightness ? _brightnessValue : _volumeValue;
        double trackHeight = 216; // Full height, no top button

        StackPanel columnPanel = new StackPanel { Margin = new Thickness(0, 0, isLast ? 0 : 8, 0) };

        // Slider track (full height, 48px wide) — matches tile height for symmetry
        Border sliderBorder = new Border
        {
            Width = 48,
            Height = trackHeight,
            Background = new SolidColorBrush(Color.FromRgb(40, 40, 40)),
            CornerRadius = new CornerRadius(12),
            ClipToBounds = true,
        };
        _tileBorders.Add(sliderBorder);

        Grid sliderGrid = new Grid();

        Border fillBorder = new Border
        {
            Height = sliderValue * trackHeight,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = Brushes.White,
            CornerRadius = sliderValue >= 0.98 ? new CornerRadius(12) : new CornerRadius(0, 0, 12, 12)
        };
        if (isBrightness) _brightnessFillBorder = fillBorder;
        else _volumeFillBorder = fillBorder;
        sliderGrid.Children.Add(fillBorder);

        // Sun/speaker icon at bottom of slider
        TextBlock iconText = new TextBlock
        {
            Text = isBrightness ? "\uE706" : "\uE767",
            FontSize = 16,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            Foreground = new SolidColorBrush(Color.FromRgb(65, 65, 65)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 14),
        };
        sliderGrid.Children.Add(iconText);

        // Expand arrow perfectly aligned with Wi-Fi tile arrow (48px height)
        TextBlock arrowIcon = new TextBlock
        {
            Text = "\uE76C", // Segoe MDL2 ChevronRight
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(102, 102, 102)), // Gray default
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
        };

        // Wrap arrow in a 48px hit-target border identically matching neighboring tiles
        Border arrowHit = new Border
        {
            Width = 48,
            Height = 48,
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = Cursors.Hand,
            Child = arrowIcon,
        };

        arrowHit.MouseEnter += (s, e) =>
        {
            arrowIcon.Foreground = Brushes.White;
            arrowHit.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Color.FromRgb(96, 165, 250), // Soft blue glow
                BlurRadius = 12,
                ShadowDepth = 0,
                Opacity = 0.6,
            };
        };
        arrowHit.MouseLeave += (s, e) =>
        {
            arrowIcon.Foreground = new SolidColorBrush(Color.FromRgb(102, 102, 102));
            arrowHit.Effect = null;
        };

        // Prevent arrow clicks from starting slider drag
        arrowHit.MouseLeftButtonDown += (s, e) =>
        {
            e.Handled = true; // Stop event from bubbling to sliderBorder drag handler
        };

        // Click — opens detail panel for this slider
        arrowHit.MouseLeftButtonUp += (s, e) =>
        {
            _contentBorder.Visibility = Visibility.Collapsed;
            if (isBrightness)
                _brightnessBorder.Visibility = Visibility.Visible;
            else
                _volumeBorder.Visibility = Visibility.Visible;
            e.Handled = true;
        };

        sliderGrid.Children.Add(arrowHit);

        sliderBorder.Child = sliderGrid;
        columnPanel.Children.Add(sliderBorder);

        // Drag logic
        Action<Point> updateSlider = (pos) =>
        {
            double newValue = 1.0 - (pos.Y / sliderBorder.ActualHeight);
            newValue = Math.Max(0, Math.Min(1, newValue));
            fillBorder.Height = newValue * trackHeight;
            fillBorder.CornerRadius = newValue >= 0.98 ? new CornerRadius(12) : new CornerRadius(0, 0, 12, 12);
            if (isBrightness)
            {
                _brightnessValue = newValue;
                SetMonitorBrightness(newValue);
            }
            else
            {
                _volumeValue = newValue;
                SetSystemVolume(newValue);
            }
        };

        sliderBorder.MouseLeftButtonDown += (s, e) =>
        {
            _isDraggingSlider = true;
            sliderBorder.CaptureMouse();
            updateSlider(e.GetPosition(sliderBorder));
        };
        sliderBorder.MouseLeftButtonUp += (s, e) =>
        {
            _isDraggingSlider = false;
            sliderBorder.ReleaseMouseCapture();
        };
        sliderBorder.MouseMove += (s, e) =>
        {
            if (_isDraggingSlider)
            {
                updateSlider(e.GetPosition(sliderBorder));
            }
        };

        return columnPanel;
    }

    private Border CreateWideTile(string icon, string title, bool state)
    {
        Border card = new Border
        {
            Width = 152,
            Height = 44,
            Margin = new Thickness(0, 0, 7, 0),
            Background = new SolidColorBrush(state ? Color.FromRgb(255, 255, 255) : Color.FromRgb(55, 55, 55)),
            CornerRadius = new CornerRadius(10),
            Cursor = Cursors.Hand
        };
        _tileBorders.Add(card);

        StackPanel sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        TextBlock iconText = new TextBlock { Text = icon, FontSize = 16, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Foreground = state ? Brushes.Black : Brushes.White, Margin = new Thickness(0, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(iconText);
        card.Child = sp;

        card.MouseLeftButtonUp += (s, e) => {
            state = !state;
            card.Background = new SolidColorBrush(state ? Color.FromRgb(255, 255, 255) : Color.FromRgb(55, 55, 55));
            iconText.Foreground = state ? Brushes.Black : Brushes.White;
        };
        return card;
    }

    private Border CreateSmallTile(string icon, string tooltip, bool state, bool isFirst = false)
    {
        Border card = new Border
        {
            Width = 48,
            Height = 48,
            Margin = new Thickness(isFirst ? 0 : 8, 0, 0, 0),
            Background = new SolidColorBrush(
                state ? Color.FromRgb(255, 255, 255) : Color.FromRgb(55, 55, 55)
            ),
            CornerRadius = new CornerRadius(8),
            Cursor = Cursors.Hand,
            ToolTip = tooltip,
        };
        _tileBorders.Add(card);

        TextBlock iconText = new TextBlock
        {
            Text = icon,
            FontSize = 18,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            Foreground = state ? Brushes.Black : Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        card.Child = iconText;

        card.MouseLeftButtonUp += (s, e) =>
        {
            state = !state;
            card.Background = new SolidColorBrush(
                state ? Color.FromRgb(255, 255, 255) : Color.FromRgb(55, 55, 55)
            );
            iconText.Foreground = state ? Brushes.Black : Brushes.White;
        };

        return card;
    }

    private Border CreateSliderDetailPanel(bool isBrightness)
    {
        Grid detailGrid = new Grid { Margin = new Thickness(12) };
        detailGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        detailGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Header with back arrow
        Grid headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        TextBlock backBtn = new TextBlock
        {
            Text = "\uE72B", // Segoe MDL2 ChevronLeft
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            Foreground = Brushes.White,
            FontSize = 14,
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        backBtn.MouseLeftButtonUp += (s, e) =>
        {
            if (isBrightness)
                _brightnessBorder.Visibility = Visibility.Collapsed;
            else
                _volumeBorder.Visibility = Visibility.Collapsed;
            _contentBorder.Visibility = Visibility.Visible;
        };
        Grid.SetColumn(backBtn, 0);
        headerGrid.Children.Add(backBtn);

        TextBlock title = new TextBlock
        {
            Text = isBrightness ? "Display" : "Sound",
            Foreground = Brushes.White,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(title, 1);
        headerGrid.Children.Add(title);

        Grid.SetRow(headerGrid, 0);
        detailGrid.Children.Add(headerGrid);

        // Content area
        StackPanel contentPanel = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        // Large icon
        TextBlock largeIcon = new TextBlock
        {
            Text = isBrightness ? "\uE706" : "\uE767",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 36,
            Foreground = new SolidColorBrush(Color.FromRgb(96, 165, 250)), // Blue accent
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 16),
        };
        contentPanel.Children.Add(largeIcon);

        // Description text
        TextBlock descText = new TextBlock
        {
            Text = isBrightness
                ? "Display brightness and color settings will be available here in a future update."
                : "Application sound mixer and audio output controls will be available here in a future update.",
            Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 160)),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 240,
        };
        contentPanel.Children.Add(descText);

        Grid.SetRow(contentPanel, 1);
        detailGrid.Children.Add(contentPanel);

        return new Border
        {
            Child = detailGrid,
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromRgb(28, 28, 28)),
        };
    }

    private Border CreateWifiPanel()
    {
        Grid wifiGrid = new Grid { Margin = new Thickness(12) };
        wifiGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        wifiGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Header
        Grid headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        TextBlock backBtn = new TextBlock
        {
            Text = "<",
            Foreground = Brushes.White,
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        backBtn.MouseLeftButtonUp += (s, e) =>
        {
            _wifiBorder.Visibility = Visibility.Collapsed;
            _contentBorder.Visibility = Visibility.Visible;
        };
        Grid.SetColumn(backBtn, 0);
        headerGrid.Children.Add(backBtn);

        TextBlock title = new TextBlock
        {
            Text = "Wi-Fi Networks",
            Foreground = Brushes.White,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(title, 1);
        headerGrid.Children.Add(title);

        Grid.SetRow(headerGrid, 0);
        wifiGrid.Children.Add(headerGrid);

        // Network list container
        _wifiListPanel = new StackPanel();
        ScrollViewer scroll = new ScrollViewer
        {
            Content = _wifiListPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            CanContentScroll = true,
            Margin = new Thickness(0, 0, -8, 0), // Adjust for scrollbar
        };
        Grid.SetRow(scroll, 1);
        wifiGrid.Children.Add(scroll);

        return new Border
        {
            Child = wifiGrid,
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromRgb(28, 28, 28)),
        };
    }

    public void SyncSliders()
    {
        double trackHeight = 216; // Based on CreateVerticalSlider trackHeight
        if (_brightnessFillBorder != null)
        {
            _brightnessFillBorder.Height = _brightnessValue * trackHeight;
            _brightnessFillBorder.CornerRadius = _brightnessValue >= 0.98 ? new CornerRadius(12) : new CornerRadius(0, 0, 12, 12);
        }
        if (_volumeFillBorder != null)
        {
            _volumeFillBorder.Height = _volumeValue * trackHeight;
            _volumeFillBorder.CornerRadius = _volumeValue >= 0.98 ? new CornerRadius(12) : new CornerRadius(0, 0, 12, 12);
        }
    }

    #region Audio and Brightness Native Control
    [StructLayout(LayoutKind.Sequential)]
    private struct AUDIO_VOLUME_NOTIFICATION_DATA
    {
        public Guid guidEventContext;
        public int bMuted;
        public float fMasterVolume;
        public int nChannels;
        public float afChannelVolumes;
    }

    [ComImport]
    [Guid("657804FA-D6AD-4496-8A60-352752AF4F89")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolumeCallback
    {
        void OnNotify(IntPtr pNotify);
    }

    private class AudioVolumeCallback : IAudioEndpointVolumeCallback
    {
        private readonly Action<float> _onVolumeChanged;
        public AudioVolumeCallback(Action<float> onVolumeChanged)
        {
            _onVolumeChanged = onVolumeChanged;
        }

        public void OnNotify(IntPtr pNotify)
        {
            var data = Marshal.PtrToStructure<AUDIO_VOLUME_NOTIFICATION_DATA>(pNotify);
            _onVolumeChanged(data.fMasterVolume);
        }
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IAudioEndpointVolumeCallback pNotify);
        int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback pNotify);
        int GetChannelCount(out int pnChannelCount);
        int SetMasterVolumeLevel(float fLevelDB, Guid pguidEventContext);
        int SetMasterVolumeLevelScalar(float fLevel, Guid pguidEventContext);
        int GetMasterVolumeLevel(out float pfLevelDB);
        int GetMasterVolumeLevelScalar(out float pfLevel);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid id, int clsCtx, IntPtr activationParams, out IAudioEndpointVolume aev);
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr ppDevices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    private IAudioEndpointVolume GetAudioEndpointVolume()
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            enumerator.GetDefaultAudioEndpoint(0, 1, out IMMDevice device); // 0 = eRender, 1 = eMultimedia
            if (device != null)
            {
                Guid iid = typeof(IAudioEndpointVolume).GUID;
                device.Activate(ref iid, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out IAudioEndpointVolume aev);
                return aev;
            }
        }
        catch { }
        return null;
    }

    private void SetSystemVolume(double value)
    {
        try
        {
            var aev = GetAudioEndpointVolume();
            if (aev != null)
            {
                float level = (float)Math.Max(0, Math.Min(1, value));
                aev.SetMasterVolumeLevelScalar(level, Guid.Empty);
            }
        }
        catch { }
    }

    private double GetCurrentVolume()
    {
        try
        {
            var aev = GetAudioEndpointVolume();
            if (aev != null)
            {
                aev.GetMasterVolumeLevelScalar(out float level);
                return level;
            }
        }
        catch { }
        return 0.5;
    }

    private void SetMonitorBrightness(double value)
    {
        try
        {
            byte brightness = (byte)(value * 100);
            using (var mclass = new System.Management.ManagementClass("WmiMonitorBrightnessMethods"))
            {
                mclass.Scope = new System.Management.ManagementScope(@"\\.\root\wmi");
                var instances = mclass.GetInstances();
                foreach (System.Management.ManagementObject instance in instances)
                {
                    var args = instance.GetMethodParameters("WmiSetBrightness");
                    args["Brightness"] = brightness;
                    args["Timeout"] = 1;
                    instance.InvokeMethod("WmiSetBrightness", args, null);
                }
            }
        }
        catch { }
    }

    private double GetCurrentBrightness()
    {
        try
        {
            using (var mclass = new System.Management.ManagementClass("WmiMonitorBrightness"))
            {
                mclass.Scope = new System.Management.ManagementScope(@"\\.\root\wmi");
                var instances = mclass.GetInstances();
                foreach (System.Management.ManagementObject instance in instances)
                {
                    return (byte)instance.GetPropertyValue("CurrentBrightness") / 100.0;
                }
            }
        }
        catch { }
        return 0.5;
    }
    #endregion

    private async void LoadWifiNetworks()
    {
        _wifiListPanel.Children.Clear();
        
        TextBlock scanningText = new TextBlock
        {
            Text = "Scanning networks...",
            Foreground = Brushes.DarkGray,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 20, 0, 0)
        };
        _wifiListPanel.Children.Add(scanningText);

        try
        {
            var networks = await Task.Run(() =>
            {
                var list = new List<string>();
                var p = new System.Diagnostics.Process();
                p.StartInfo.FileName = "netsh";
                p.StartInfo.Arguments = "wlan show networks mode=bssid";
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.CreateNoWindow = true;
                p.Start();

                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();

                string[] lines = output.Split('\n');
                foreach (string line in lines)
                {
                    if (line.Contains("SSID") && !line.Contains("BSSID"))
                    {
                        string[] parts = line.Split(':');
                        if (parts.Length > 1)
                        {
                            string ssid = parts[1].Trim();
                            if (!string.IsNullOrEmpty(ssid) && !list.Contains(ssid))
                                list.Add(ssid);
                        }
                    }
                }
                return list;
            });

            _wifiListPanel.Children.Clear();

            if (networks.Count == 0)
            {
                _wifiListPanel.Children.Add(new TextBlock
                {
                    Text = "No networks found.",
                    Foreground = Brushes.Gray,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 20, 0, 0)
                });
                return;
            }

            foreach (var ssid in networks)
            {
                Border netBorder = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(45, 45, 45)),
                    CornerRadius = new CornerRadius(6),
                    Margin = new Thickness(0, 0, 8, 6),
                    Padding = new Thickness(12, 10, 12, 10),
                    Cursor = Cursors.Hand
                };

                TextBlock netText = new TextBlock
                {
                    Text = ssid,
                    Foreground = Brushes.White,
                    FontSize = 14
                };

                netBorder.Child = netText;

                // Simple visual state toggle for hover/click feel
                netBorder.MouseEnter += (s, e) => netBorder.Background = new SolidColorBrush(Color.FromRgb(65, 65, 65));
                netBorder.MouseLeave += (s, e) => netBorder.Background = new SolidColorBrush(Color.FromRgb(45, 45, 45));

                _wifiListPanel.Children.Add(netBorder);
            }
        }
        catch
        {
            _wifiListPanel.Children.Clear();
            _wifiListPanel.Children.Add(new TextBlock
            {
                Text = "Failed to scan networks.",
                Foreground = Brushes.IndianRed,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 20, 0, 0)
            });
        }
    }
}
