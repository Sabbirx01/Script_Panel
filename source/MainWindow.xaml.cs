using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AISuitePanel;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

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
