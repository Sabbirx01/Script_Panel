using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace AISuitePanel.Views;

/// <summary>
/// Screen 1 - licence activation. One field holds the 16-character key; the
/// dashes are decoration and are stripped before validation.
/// </summary>
public partial class ActivationView : UserControl
{
    private enum StatusKind { Idle, Busy, Ok, Error }

    private const int MinimumKeyLength = 4;
    private const int MaxAttempts = 5;

    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);

    private bool _suppress;
    private bool _busy;
    private bool _done;
    private int _failures;

    private readonly DispatcherTimer _cooldownTimer;
    private DateTime _cooldownUntil;

    public ActivationView()
    {
        InitializeComponent();

        _cooldownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _cooldownTimer.Tick += OnCooldownTick;

        Loaded += (_, _) => KeyBox.Focus();

        if (LicenseService.IsActivated)
        {
            KeyBox.Text = Format(LicenseService.Key);
            _done = true;
            ActivateText.Text = "ACTIVATED";
            SetStatus("Status: License Activated", StatusKind.Ok);
            RecolorBadge(true);
        }
        else
        {
            SetStatus("Status: License Unverified", StatusKind.Idle);
        }

        UpdateButtonState();
    }

    // ------------------------------------------------------------------ input

    /// <summary>The exact server-format key, preserving separators.</summary>
    private string RawKey =>
        new string(KeyBox.Text.Trim().Where(ch => char.IsLetterOrDigit(ch) || ch == '-').ToArray())
            .ToUpperInvariant();

    private void OnKeyTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppress) return;

        var raw = RawKey;
        _suppress = true;
        KeyBox.Text = raw;
        KeyBox.CaretIndex = KeyBox.Text.Length;
        _suppress = false;
        UpdateButtonState();
    }

    private void OnKeyKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        e.Handled = true;
        if (ActivateButton.IsEnabled) OnActivate(ActivateButton, new RoutedEventArgs());
    }

    private void OnKeyFocused(object sender, KeyboardFocusChangedEventArgs e) => KeyBox.SelectAll();

    private void UpdateButtonState()
    {
        if (_busy || _done || _cooldownTimer.IsEnabled) return;

        var complete = RawKey.Length >= MinimumKeyLength;
        ActivateButton.IsEnabled = complete;

        SetStatus(complete
                ? "Status: Key entered - ready to activate"
                : "Status: License Unverified",
            complete ? StatusKind.Busy : StatusKind.Idle);
    }

    // ----------------------------------------------------------------- actions

    private async void OnActivate(object sender, RoutedEventArgs e)
    {
        if (_busy || _done || _cooldownTimer.IsEnabled) return;

        var key = RawKey;
        if (key.Length < MinimumKeyLength) return;

        _busy = true;
        ActivateButton.IsEnabled = false;
        ActivateText.Text = "VERIFYING...";
        SetStatus("Status: Verifying license key...", StatusKind.Busy);

        var (ok, message) = await LicenseService.ActivateAsync(key);

        if (!ok)
        {
            _busy = false;
            _failures++;
            ActivateText.Text = "🚀  ACTIVATE SOFTWARE";
            SetStatus("Status: " + message, StatusKind.Error);

            // The reason has to stay on screen. Calling UpdateButtonState() here
            // would immediately overwrite the error with the neutral
            // "ready to activate" line, which is why a refused key used to look
            // like nothing happened at all.
            if (_failures >= MaxAttempts) StartCooldown();
            else ActivateButton.IsEnabled = RawKey.Length >= MinimumKeyLength;

            return;
        }

        _busy = false;
        _done = true;
        ActivateText.Text = "ACTIVATED";
        SetStatus("Status: License Activated", StatusKind.Ok);
        RecolorBadge(true);

        await Task.Delay(650);
        Nav.GoDashboard();
    }

    // ---------------------------------------------------------------- cooldown

    /// <summary>After five bad keys the button locks for a short while.</summary>
    private void StartCooldown()
    {
        _cooldownUntil = DateTime.Now + Cooldown;
        ActivateButton.IsEnabled = false;
        _cooldownTimer.Start();
        TickCooldown();
    }

    private void OnCooldownTick(object? sender, EventArgs e) => TickCooldown();

    private void TickCooldown()
    {
        var left = _cooldownUntil - DateTime.Now;

        if (left <= TimeSpan.Zero)
        {
            _cooldownTimer.Stop();
            _failures = 0;
            ActivateText.Text = "🚀  ACTIVATE SOFTWARE";
            UpdateButtonState();
            return;
        }

        ActivateButton.IsEnabled = false;
        ActivateText.Text = $"TRY AGAIN IN {Math.Ceiling(left.TotalSeconds):0}s";
        SetStatus("Status: Too many attempts - please wait.", StatusKind.Error);
    }

    // ------------------------------------------------------------------ status

    private void SetStatus(string text, StatusKind kind)
    {
        StatusLabel.Text = text;

        var (dot, fg, bg, border) = kind switch
        {
            StatusKind.Busy => ("#FFFBBF24", "#FFFBBF24", "#22FBBF24", "#55FBBF24"),
            StatusKind.Ok => ("#FF34D399", "#FF9CF0D0", "#2234D399", "#6634D399"),
            StatusKind.Error => ("#FFF87171", "#FFFCA5A5", "#22F87171", "#55F87171"),
            _ => ("#FF5E6D8B", "#FF93A3C2", "#FF131C2F", "#FF1E2A44")
        };

        StatusDot.Fill = Brush(dot);
        StatusLabel.Foreground = Brush(fg);
        StatusPill.Background = Brush(bg);
        StatusPill.BorderBrush = Brush(border);
    }

    private void RecolorBadge(bool ok)
    {
        var accent = ok ? "#FF34D399" : "#FF22D3EE";
        var rgb = accent[3..];

        LockShackle.Stroke = Brush(accent);
        LockBody.Fill = Brush(accent);

        Badge.BorderBrush = Brush(ok ? "#6634D399" : "#6622D3EE");
        Badge.Effect = new DropShadowEffect
        {
            Color = Color(accent),
            BlurRadius = 34,
            ShadowDepth = 0,
            Opacity = 0.38
        };

        var radial = new RadialGradientBrush();
        radial.GradientStops.Add(new GradientStop(Color("#55" + rgb), 0));
        radial.GradientStops.Add(new GradientStop(Color("#00" + rgb), 1));
        BadgeGlow.Fill = radial;
    }

    // ----------------------------------------------------------------- helpers

    /// <summary>1234-5678-9012-3456</summary>
    private static string Format(string raw)
    {
        if (raw.Length == 0) return "";

        var groups = (raw.Length + 3) / 4;
        return string.Join("-", Enumerable.Range(0, groups)
            .Select(i => raw.Substring(i * 4, Math.Min(4, raw.Length - i * 4))));
    }

    private static Color Color(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;

    private static Brush Brush(string hex) => new SolidColorBrush(Color(hex));
}
