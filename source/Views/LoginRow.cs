using System.Windows.Media;
using AISuitePanel.Models;

namespace AISuitePanel.Views;

/// <summary>
/// One row in the Login tab — wraps an <see cref="AccountRecord"/> with
/// per-row UI bits (tick-box, verify-status colour, optional cookie-file
/// reference).  Shared by <see cref="AccioPanelView"/> and
/// <see cref="PythonToolPanelView"/> so the same XAML binding shape works on
/// every tool page.
/// </summary>
public class LoginRow : ObservableObject
{
    private bool _selected;
    private string _lastVerify = "";
    private Brush _lastVerifyColor = Brushes.Gray;

    public AccountRecord? Account { get; set; }
    public int Id { get; set; }
    public string IdLabel { get; set; } = "";
    public string EmailUsed { get; set; } = "";
    public string CookieLabel { get; set; } = "";
    public string CookieFileLabel { get; set; } = "";
    public string? CookieFilePath { get; set; }

    public bool Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    public string LastVerify
    {
        get => _lastVerify;
        set => Set(ref _lastVerify, value);
    }

    public Brush LastVerifyColor
    {
        get => _lastVerifyColor;
        set => Set(ref _lastVerifyColor, value);
    }
}
