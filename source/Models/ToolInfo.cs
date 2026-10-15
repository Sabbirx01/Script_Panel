using System.Windows.Media;

namespace AISuitePanel.Models;

/// <summary>Describes one tool box shown on the main dashboard.</summary>
public sealed class ToolInfo : ObservableObject
{
    private int _cookieCount;

    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";
    public string Status { get; init; } = "Active";
    public string Tagline { get; init; } = "";

    /// <summary>SVG-style path data (24x24 box) used to draw the tool glyph.</summary>
    public string IconData { get; init; } = "";

    /// <summary>
    /// Which automation driver this box is wired to ("python", "accio" or
    /// "simulated"). Read from <c>tools.json</c> so a new box needs no code.
    /// </summary>
    public string Driver { get; init; } = "python";

    /// <summary>Official site opened from the tool panel. Empty hides the button.</summary>
    public string Url { get; init; } = "";

    /// <summary>
    /// Licence product this box claims - its own name, e.g. "zai" for the Z AI
    /// box, so keys look like ZAI-XXXXX-XXXXX-XXXXXXXX. Read from
    /// <c>tools.json</c>; a box added there needs no other licence setup.
    /// </summary>
    public string Product { get; init; } = "";

    /// <summary>
    /// Products this box also accepts. Older names the same tool was sold under
    /// live here, so a key issued before a rename keeps working - the Z AI box
    /// accepts both "zai" and "dolazai". Built by <see cref="ToolCatalog"/> from
    /// the tool's <c>product</c> and <c>legacyProducts</c>.
    /// </summary>
    public IReadOnlyList<string> AcceptedProducts { get; init; } = Array.Empty<string>();

    private Geometry? _icon;

    /// <summary>Parsed glyph geometry (cached).</summary>
    public Geometry Icon => _icon ??= Geometry.Parse(IconData);

    /// <summary>Solid accent - used for borders, pills and hover states.</summary>
    public Brush Accent { get; init; } = Brushes.Cyan;

    /// <summary>Same accent as a <see cref="Color"/>, used for glow effects.</summary>
    public Color AccentColor { get; init; } = Colors.Cyan;

    /// <summary>Gradient used behind the glyph and on the OPEN button.</summary>
    public Brush AccentGradient { get; init; } = Brushes.Cyan;

    /// <summary>
    /// Cookies currently held for this tool. Read from <c>CookieVault</c> - never
    /// hard-coded - and refreshed whenever the vault changes.
    /// </summary>
    public int CookieCount
    {
        get => _cookieCount;
        set
        {
            if (!Set(ref _cookieCount, value)) return;
            Raise(nameof(CookieCountLabel));
            Raise(nameof(HasCookies));
        }
    }

    public string CookieCountLabel => CookieCount.ToString();

    public bool HasCookies => CookieCount > 0;

    public string PanelTitle => Name.ToUpperInvariant() + " PANEL";

    /// <summary>
    /// Official site for this tool. Comes from <c>tools.json</c>; the built-in
    /// list below is only used when the entry does not set one.
    /// </summary>
    public string OfficialUrl => !string.IsNullOrWhiteSpace(Url)
        ? Url
        : Id.ToLowerInvariant() switch
        {
            "zai" => "https://chat.z.ai/auth",
            "devin" => "https://devin.ai/",
            "accio" => "https://www.accio.com/login",
            "tool4" => "https://www.minimax.io/",
            _ => ""
        };

    public string WorkspacePath => AppPaths.Workspace(Id);
    public bool IsLicensed => ToolLicenseService.IsLicensed(Id);
    public string LicenseStatus => ToolLicenseService.StatusFor(Id);
    public string OpenLabel => IsLicensed ? "OPEN TOOL" : "ENTER LICENCE";

    /// <summary>
    /// What the licence chip on the card shows. Licensed: the box's own key,
    /// masked, so it is obvious which key opened it. Not licensed: the shape a
    /// key for this box has ("ZAI-XXXXX-XXXXX-XXXXXXXX"), which is more useful
    /// than the bare words "licence required".
    /// </summary>
    public string LicenceHeadline =>
        IsLicensed ? ToolLicenseService.MaskedKeyFor(Id) : "Licence required";

    /// <summary>Second half of the chip: time left, or the format to look for.</summary>
    public string LicenceDetail => IsLicensed
        ? ToolLicenseService.DaysFor(Id) is int days ? $"{days} day(s) left" : "licensed"
        : ToolLicenseService.KeyFormatFor(Id);

    public void RefreshLicenseState()
    {
        Raise(nameof(IsLicensed));
        Raise(nameof(LicenseStatus));
        Raise(nameof(OpenLabel));
        Raise(nameof(LicenceHeadline));
        Raise(nameof(LicenceDetail));
    }
}
