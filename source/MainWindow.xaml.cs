using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;

namespace AISuitePanel;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Long settings pages used to jump from top to bottom in one flick:
        // many mice and precision touchpads send several notches per physical
        // detent, and each notch scrolled a full WPF step. One notch per 70 ms,
        // about a tenth of the viewport each, keeps it predictable.
        PreviewMouseWheel += OnWindowPreviewMouseWheel;

        Nav.Renderer = Render;
        LicenseService.Changed += UpdateStatusPill;

        UpdateStatusPill();

        // Restore straight into the dashboard when a valid license is already stored.
        if (LicenseService.IsActivated)
            Nav.GoDashboard();
        else
            Nav.GoActivation();
    }

    // ------------------------------------------------------------------ views

    private UserControl? _current;

    private void Render(UserControl view)
    {
        _current = view;

        view.Opacity = 0;
        var slide = new TranslateTransform(0, 14);
        view.RenderTransform = slide;

        Host.Content = view;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        view.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        slide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
    }

    // ---------------------------------------------------------------- scrolling

    private static readonly Dictionary<ScrollViewer, DateTime> _wheelAt = new();

    private void OnWindowPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var sv = FindScrollViewer(e.OriginalSource as DependencyObject);
        if (sv is null) return;

        var now = DateTime.UtcNow;
        if (_wheelAt.TryGetValue(sv, out var last) && (now - last).TotalMilliseconds < 70)
        {
            // Extra notches from a single physical detent: drop them.
            e.Handled = true;
            return;
        }
        _wheelAt[sv] = now;

        if (sv.CanContentScroll)
        {
            if (e.Delta > 0) sv.LineUp(); else sv.LineDown();
            e.Handled = true;
            return;
        }

        var step = Math.Clamp(sv.ViewportHeight * 0.10, 32, 110);
        var target = sv.VerticalOffset - (e.Delta > 0 ? step : -step);
        target = Math.Clamp(target, 0, Math.Max(0, sv.ScrollableHeight));

        // At either end, leave the event alone so the outer page scrolls.
        if (Math.Abs(target - sv.VerticalOffset) < 0.5) return;

        sv.ScrollToVerticalOffset(target);
        e.Handled = true;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject? node)
    {
        while (node is not null && node is not ScrollViewer)
        {
            node = node is Visual or Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return node as ScrollViewer;
    }

    // ------------------------------------------------------------ status pill

    private void UpdateStatusPill()
    {
        if (LicenseService.IsActivated)
        {
            StatusPill.Background = new SolidColorBrush(Color.FromArgb(0x22, 0x34, 0xD3, 0x99));
            StatusPill.BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x34, 0xD3, 0x99));
            StatusDot.Fill = (Brush)FindResource("B.Ok");
            StatusText.Text = "STATUS: ACTIVATED";
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xF0, 0xD0));
        }
        else
        {
            StatusPill.Background = new SolidColorBrush(Color.FromArgb(0x22, 0xF8, 0x71, 0x71));
            StatusPill.BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xF8, 0x71, 0x71));
            StatusDot.Fill = (Brush)FindResource("B.Err");
            StatusText.Text = "STATUS: LOCKED";
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xFC, 0xA5, 0xA5));
        }
    }

    // ------------------------------------------------------------ window chrome

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnWindowStateChanged(object sender, EventArgs e)
    {
        // Without a native frame, a maximized window would cover the taskbar.
        MaxHeight = WindowState == WindowState.Maximized
            ? SystemParameters.WorkArea.Height + 8
            : double.PositiveInfinity;
    }
}
