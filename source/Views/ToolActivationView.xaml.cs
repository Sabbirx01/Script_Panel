using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AISuitePanel.Views;

public partial class ToolActivationView : UserControl
{
    private readonly Models.ToolInfo _tool;
    private bool _suppress;
    private bool _busy;

    public ToolActivationView(Models.ToolInfo tool)
    {
        _tool = tool;
        InitializeComponent();
        ToolTitle.Text = $"{tool.Name.ToUpperInvariant()} LICENCE";
        // Showing the shape of the key is more use than the internal product slug.
        ProductText.Text = $"This box needs its own key, like {ToolLicenseService.KeyFormatFor(tool.Id)}";
        StatusText.Text = "Licence required - tool locked";
        Loaded += (_, _) => KeyBox.Focus();
    }

    private string RawKey => new string(
        KeyBox.Text.Trim().Where(ch => char.IsLetterOrDigit(ch) || ch == '-').ToArray())
        .ToUpperInvariant();

    private void OnKeyTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppress) return;
        var raw = RawKey;
        _suppress = true;
        KeyBox.Text = raw;
        KeyBox.CaretIndex = KeyBox.Text.Length;
        _suppress = false;
        ActivateButton.IsEnabled = raw.Length >= 4 && !_busy;
    }

    private void OnKeyKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !ActivateButton.IsEnabled) return;
        e.Handled = true;
        OnActivate(ActivateButton, new RoutedEventArgs());
    }

    private void OnKeyFocused(object sender, KeyboardFocusChangedEventArgs e) => KeyBox.SelectAll();

    private async void OnActivate(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        ActivateButton.IsEnabled = false;
        StatusText.Text = "Verifying signed licence response...";

        var (ok, message) = await ToolLicenseService.ActivateAsync(_tool.Id, RawKey);
        if (!ok)
        {
            _busy = false;
            StatusText.Text = message;
            ActivateButton.IsEnabled = RawKey.Length >= 4;
            return;
        }

        StatusText.Text = $"{message} - tool unlocked";
        await Task.Delay(350);
        Nav.GoTool(_tool);
    }

    private void OnBack(object sender, RoutedEventArgs e) => Nav.GoDashboard();
}
