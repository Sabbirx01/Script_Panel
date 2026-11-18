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
        var sv = ScrollChain.Find(e.OriginalSource as DependencyObject);
        if (sv is null) return;

        var now = DateTime.UtcNow;
        if (_wheelAt.TryGetValue(sv, out var last) && (now - last).TotalMilliseconds < 70)
        {
            // Extra notches from a single physical detent: drop them.
            e.Handled = true;
            return;
        }
        _wheelAt[sv] = now;

        // The wheel used to die in whichever nested ScrollViewer sat under the
        // pointer - the live log, the mail list, the account list - because that
        // box marks the event handled even with nothing left to scroll. See
        // ScrollChain: it now walks outwards to the first box that can move.
        if (ScrollChain.Handle(sv, e.Delta)) e.Handled = true;
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
