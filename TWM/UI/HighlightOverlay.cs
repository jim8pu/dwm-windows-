using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using static TilingWindowManager.Native.NativeMethods;

namespace TilingWindowManager.UI;

public class HighlightOverlay : Window
{
    private readonly Border _border;
    private const int THICKNESS = 5;

    public HighlightOverlay()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        IsHitTestVisible = false;

        _border = new Border
        {
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 120, 215)),
            BorderThickness = new Thickness(THICKNESS),
            SnapsToDevicePixels = true,
        };
        Content = _border;
    }

    private double _dpiScaleX = 1.0;
    private double _dpiScaleY = 1.0;
    private IntPtr _hwnd;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget != null)
        {
            _dpiScaleX = source.CompositionTarget.TransformToDevice.M11;
            _dpiScaleY = source.CompositionTarget.TransformToDevice.M22;
        }

        _hwnd = new WindowInteropHelper(this).Handle;

        long exStyle = GetWindowLongPtrW(_hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtrW(_hwnd, GWL_EXSTYLE, (IntPtr)(exStyle | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW));
    }

    private int _lastX,
        _lastY,
        _lastW,
        _lastH;

    public void ShowBorder(int x, int y, int w, int h)
    {
        if (
            x == _lastX
            && y == _lastY
            && w == _lastW
            && h == _lastH
            && Visibility == Visibility.Visible
        )
            return;

        _lastX = x;
        _lastY = y;
        _lastW = w;
        _lastH = h;

        if (Visibility != Visibility.Visible)
        {
            long exStyle = GetWindowLongPtrW(_hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtrW(_hwnd, GWL_EXSTYLE, (IntPtr)(exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT));
            Show();
        }

        // Fix: Use SetWindowPos to enforce Z-Order (Top of non-topmost windows)
        // This uses PHYSICAL pixels.
        // HWND_TOP (0) places it at the top of the Z-order
        int physicalThicknessX = (int)(THICKNESS * _dpiScaleX);
        int physicalThicknessY = (int)(THICKNESS * _dpiScaleY);

        SetWindowPos(
            _hwnd,
            (IntPtr)0,
            x - physicalThicknessX,
            y - physicalThicknessY,
            w + (physicalThicknessX * 2),
            h + (physicalThicknessY * 2),
            SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_ASYNCWINDOWPOS
        );
    }

    public void HideBorder()
    {
        if (Visibility == Visibility.Visible)
            Hide();
    }
}
