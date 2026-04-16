using System.Windows;
using TilingWindowManager.Configs;
using TilingWindowManager.Core;
using TilingWindowManager.UI;
using TilingWindowManager.Windows;

namespace TilingWindowManager;

public partial class App : System.Windows.Application
{
    private ConfigLoader? _configLoader;
    private WindowEnumerator? _windowEnumerator;
    private HighlightOverlay? _overlay;
    private EventHookManager? _hookManager;
    private WindowManager? _windowManager;
    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    private System.Threading.Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new System.Threading.Mutex(true, "TilingWindowManager_SingleInstance_Mutex", out bool createdNew);
        if (!createdNew)
        {
            // Another instance is already running; terminate safely.
            System.Windows.Application.Current.Shutdown();
            return;
        }

        base.OnStartup(e);

        // 1. Load Configs (Default Rules)
        _configLoader = new ConfigLoader();

        // 2. Setup Overlay
        _overlay = new HighlightOverlay();
        // Don't show it yet, WindowManager will handle it

        // 3. Setup Window Manager & Event Hooks
        _hookManager = new EventHookManager();
        _windowEnumerator = new WindowEnumerator(_configLoader);
        _windowManager = new WindowManager(_windowEnumerator, _overlay, _hookManager);
        _windowManager.Start();

        // 4. System Tray (to exit)
        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true,
            Text = "Tiling Window Manager (Running)",
        };
        _notifyIcon.ContextMenuStrip = new System.Windows.Forms.ContextMenuStrip();

        var focusItem = new System.Windows.Forms.ToolStripMenuItem("Focus Follows Mouse")
        {
            CheckOnClick = true,
            Checked = true
        };
        focusItem.CheckedChanged += (s, args) =>
        {
            if (_windowManager != null)
            {
                _windowManager.IsFocusFollowsMouseEnabled = focusItem.Checked;
            }
        };

        _notifyIcon.ContextMenuStrip.Items.Add(focusItem);
        _notifyIcon.ContextMenuStrip.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _notifyIcon.ContextMenuStrip.Items.Add("Exit", null, (s, args) => Shutdown());
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _windowManager?.Stop();
        _notifyIcon?.Dispose();
        
        if (_instanceMutex != null)
        {
            _instanceMutex.ReleaseMutex();
            _instanceMutex.Dispose();
        }
        
        base.OnExit(e);
    }
}
