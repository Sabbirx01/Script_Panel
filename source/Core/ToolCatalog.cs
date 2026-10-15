using System.IO;
using System.Windows.Media;
using AISuitePanel.Models;

namespace AISuitePanel;

/// <summary>
/// The tool boxes shown on the main dashboard.
///
/// The list is registry driven: <c>%APPDATA%\AISuitePanel\tools.json</c> is
/// written on first run and read on every start, so a box can be added,
/// renamed, recoloured or pointed at another product without touching the
/// source and rebuilding. The four shipped tools are the built-in defaults and
/// are merged back in whenever the file is incomplete.
/// </summary>
public static class ToolCatalog
{
    private static readonly List<ToolInfo> Items = new();

    private static bool _loaded;

    static ToolCatalog() => Apply(ToolDefinition.Defaults());

    /// <summary>Every tool box, in dashboard order.</summary>
    public static IReadOnlyList<ToolInfo> Tools => Items;

    public static string ConfigPath => AppPaths.Tools;

    public static ToolInfo? Find(string id) =>
        Items.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The box that owns a licence product. Used to say WHERE a pasted key
    /// belongs ("This is a Z AI key") instead of just refusing it.
    /// </summary>
    public static ToolInfo? FindByProduct(string? product) =>
        string.IsNullOrWhiteSpace(product)
            ? null
            : Items.FirstOrDefault(t => t.AcceptedProducts.Any(p => ProductCatalog.SameProduct(p, product)));

    /// <summary>True when some tool box owns this licence product.</summary>
    public static bool ClaimsProduct(string? product) => FindByProduct(product) is not null;

    /// <summary>
    /// Reads <c>tools.json</c> (creating it on first run) and rebuilds the
    /// dashboard list. Called once from <c>App.OnStartup</c>.
    /// </summary>
    internal static void Load()
    {
        var defaults = ToolDefinition.Defaults();

        if (!File.Exists(ConfigPath))
        {
            Apply(defaults);
            Save(defaults);
            _loaded = true;
            SeedWorkspaces();
            return;
        }

        var loaded = Json.Load(ConfigPath, () => new ToolListFile());
        var merged = Merge(defaults, loaded.Tools);
        Apply(merged);
        Save(merged);
        _loaded = true;
        SeedWorkspaces();
    }

    /// <summary>Adds the tool list to the registry when it was empty.</summary>
    private static void SeedWorkspaces()
    {
        if (!_loaded) return;
        foreach (var tool in Items) AppPaths.EnsureWorkspace(tool.Id);
    }

    /// <summary>
    /// Pulls "Saved Cookies: N" for every box out of the vault, and refreshes
    /// the licence state shown next to it. Called once at startup and again
    /// whenever the vault or a licence changes.
    /// </summary>
    public static void RefreshCookieCounts()
    {
        foreach (var tool in Items)
        {
            tool.CookieCount = CookieVault.CountFor(tool.Id);
            tool.RefreshLicenseState();
        }
    }

    // ------------------------------------------------------------------ mapping

    private static void Apply(IReadOnlyList<ToolDefinition> definitions)
    {
        Items.Clear();
        foreach (var definition in definitions)
        {
            var tool = Make(definition);
            if (tool is not null) Items.Add(tool);
        }
    }

    private static ToolInfo? Make(ToolDefinition definition)
    {
        var id = Slug(definition.Id);
        if (id.Length == 0) return null;

        var start = Parse(definition.AccentFrom, DefaultAccentFrom);
        var end = ParseOr(definition.AccentTo, start);

        // product = the tool's own name unless the box says otherwise, so a box
        // added to tools.json is licensable with no further configuration.
        var product = ProductCatalog.Slug(definition.Product) is { Length: > 0 } slug ? slug : id;

        var accepted = new List<string> { product };
        foreach (var legacy in definition.LegacyProducts ?? new List<string>())
        {
            var value = ProductCatalog.Slug(legacy);
            if (value.Length == 0) continue;
            if (accepted.Any(p => ProductCatalog.SameProduct(p, value))) continue;
            accepted.Add(value);
        }

        return new ToolInfo
        {
            Id = id,
            Product = product,
            AcceptedProducts = accepted,
            Name = string.IsNullOrWhiteSpace(definition.Name) ? id.ToUpperInvariant() : definition.Name.Trim(),
            Type = string.IsNullOrWhiteSpace(definition.Type) ? "Automation Tool" : definition.Type.Trim(),
            Status = "Active",
            Tagline = definition.Tagline ?? "",
            Driver = string.IsNullOrWhiteSpace(definition.Driver) ? "python" : definition.Driver.Trim().ToLowerInvariant(),
            Url = definition.Url ?? "",
            IconData = string.IsNullOrWhiteSpace(definition.Icon) ? DefaultIcon : definition.Icon,
            Accent = Freeze(new SolidColorBrush(start)),
            AccentColor = start,
            AccentGradient = Freeze(new LinearGradientBrush(start, end,
                new System.Windows.Point(0, 0), new System.Windows.Point(1, 1)))
        };
    }

    /// <summary>
    /// Built-in entries, then anything extra from the file, then the file's
    /// overrides. A tool the operator deleted stays deleted only if the file
    /// lists it - the defaults come back otherwise, which is the safer default
    /// for a licensed tool box.
    /// </summary>
    private static List<ToolDefinition> Merge(
        IReadOnlyList<ToolDefinition> defaults, List<ToolDefinition>? loaded)
    {
        var merged = new List<ToolDefinition>();
        var overrides = new Dictionary<string, ToolDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (var definition in loaded ?? new List<ToolDefinition>())
        {
            var id = Slug(definition.Id);
            if (id.Length == 0) continue;
            overrides[id] = definition;
        }

        foreach (var definition in defaults)
        {
            var id = Slug(definition.Id);
            merged.Add(overrides.TryGetValue(id, out var replacement)
                ? MergeOne(definition, replacement)
                : definition);
            overrides.Remove(id);
        }

        foreach (var extra in overrides.Values) merged.Add(extra);

        return merged.Count == 0 ? new List<ToolDefinition>(defaults) : merged;
    }

    /// <summary>
    /// One entry from the file wins field by field, not wholesale: a value the
    /// operator did not touch keeps coming from the built-in default. Without
    /// this a tools.json written before a field existed would silently blank it -
    /// which is how the Accio box could have ended up on the wrong driver, and
    /// how the MiniMax box would have lost its "minimax" product name.
    /// </summary>
    private static ToolDefinition MergeOne(ToolDefinition fallback, ToolDefinition file) => new()
    {
        Id = Pick(file.Id, fallback.Id),
        Name = Pick(file.Name, fallback.Name),
        Type = Pick(file.Type, fallback.Type),
        Tagline = Pick(file.Tagline, fallback.Tagline),
        AccentFrom = Pick(file.AccentFrom, fallback.AccentFrom),
        AccentTo = Pick(file.AccentTo, fallback.AccentTo),
        Icon = Pick(file.Icon, fallback.Icon),
        Driver = Pick(file.Driver, fallback.Driver),
        Url = Pick(file.Url, fallback.Url),
        Product = Pick(file.Product, fallback.Product),
        LegacyProducts = file.LegacyProducts is { Count: > 0 }
            ? file.LegacyProducts
            : fallback.LegacyProducts
    };

    private static string Pick(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static void Save(IReadOnlyList<ToolDefinition> definitions) =>
        Json.Save(ConfigPath, new ToolListFile { Tools = definitions.ToList() });

    // ------------------------------------------------------------------ helpers

    private const string DefaultAccentFrom = "#FF22D3EE";
    private const string DefaultIcon = "M13.6,2 L5.2,13.4 H11.2 L9.6,22 L18.4,10.2 H12.4 Z";

    private static string Slug(string? value) =>
        new string((value ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static Color Parse(string? hex, string fallback) =>
        ParseOr(hex, (Color)ColorConverter.ConvertFromString(fallback)!);

    private static Color ParseOr(string? hex, Color fallback)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(hex)) return fallback;
            return (Color)ColorConverter.ConvertFromString(hex.Trim())!;
        }
        catch
        {
            return fallback;
        }
    }

    private static Brush Freeze(SolidColorBrush brush) { brush.Freeze(); return brush; }

    private static Brush Freeze(LinearGradientBrush brush) { brush.Freeze(); return brush; }
}

/// <summary>Shape of one entry in <c>%APPDATA%\AISuitePanel\tools.json</c>.</summary>
public sealed class ToolDefinition
{
    /// <summary>Short stable id used for files and folders (e.g. "zai").</summary>
    public string Id { get; set; } = "";

    /// <summary>Box title on the dashboard.</summary>
    public string Name { get; set; } = "";

    public string Type { get; set; } = "Automation Tool";

    public string Tagline { get; set; } = "";

    /// <summary>Gradient start, <c>#AARRGGBB</c> or <c>#RRGGBB</c>.</summary>
    public string AccentFrom { get; set; } = "#FF22D3EE";

    /// <summary>Gradient end.</summary>
    public string AccentTo { get; set; } = "#FF0EA5E9";

    /// <summary>Icon path geometry drawn in a 24x24 box.</summary>
    public string Icon { get; set; } = "";

    /// <summary>Which automation driver to wire up: "python", "accio" or "simulated".</summary>
    public string Driver { get; set; } = "python";

    /// <summary>Official site opened from the tool panel. Empty hides the button.</summary>
    public string Url { get; set; } = "";

    /// <summary>
    /// Licence product for this box - keys look like &lt;PRODUCT&gt;-XXXXX-XXXXX-XXXXXXXX.
    /// Leave empty to use the box's own id, which is what a new box wants.
    /// </summary>
    public string Product { get; set; } = "";

    /// <summary>
    /// Older product names this box still accepts, so keys sold before a rename
    /// keep working. Example: the Z AI box lists "dolazai" here.
    /// </summary>
    public List<string> LegacyProducts { get; set; } = new();

    public static List<ToolDefinition> Defaults() => new()
    {
        new ToolDefinition
        {
            Id = "zai",
            Name = "Z AI",
            Type = "Automation Tool",
            Tagline = "Task automation and workflow engine",
            AccentFrom = "#FF22D3EE",
            AccentTo = "#FF0EA5E9",
            Driver = "python",
            Url = "https://chat.z.ai/auth",
            Product = "zai",
            LegacyProducts = new List<string> { "dolazai" },
            Icon = "M13.6,2 L5.2,13.4 H11.2 L9.6,22 L18.4,10.2 H12.4 Z"
        },
        new ToolDefinition
        {
            Id = "accio",
            Name = "Accio AI",
            Type = "Automation Tool",
            Tagline = "Assistant driven bulk operations",
            AccentFrom = "#FF8B5CF6",
            AccentTo = "#FF6D28D9",
            Driver = "accio",
            Url = "https://www.accio.com/login",
            Product = "accio",
            LegacyProducts = new List<string> { "dolaaccio" },
            Icon = "M11,1.6 L13.1,8.6 L20.1,10.7 L13.1,12.8 L11,19.8 L8.9,12.8 L1.9,10.7 L8.9,8.6 Z " +
                   "M19.4,14.2 L20.3,17.2 L23.3,18.1 L20.3,19 L19.4,22 L18.5,19 L15.5,18.1 L18.5,17.2 Z"
        },
        new ToolDefinition
        {
            Id = "devin",
            Name = "Devin AI",
            Type = "Automation Tool",
            Tagline = "Session driven engineering agent",
            AccentFrom = "#FF34D399",
            AccentTo = "#FF059669",
            Driver = "python",
            Url = "https://devin.ai/",
            // "devinai" is a second product for this same tool: keys issued
            // there carry the DEVINAI prefix and the server reports them as
            // belonging to the DEVINAI tool, so without listing it here the
            // Devin box refuses an otherwise perfectly good key with
            // "This key is for DEVINAI, not for DEVIN".
            Product = "devin",
            LegacyProducts = new List<string> { "doladevin", "devinai" },
            Icon = "F0 M6,4 H18 A2,2 0 0 1 20,6 V18 A2,2 0 0 1 18,20 H6 A2,2 0 0 1 4,18 V6 A2,2 0 0 1 6,4 Z " +
                   "M9,9 H15 V15 H9 Z " +
                   "M8,1.6 H10 V4 H8 Z M14,1.6 H16 V4 H14 Z " +
                   "M8,20 H10 V22.4 H8 Z M14,20 H16 V22.4 H14 Z " +
                   "M1.6,8 H4 V10 H1.6 Z M1.6,14 H4 V16 H1.6 Z " +
                   "M20,8 H22.4 V10 H20 Z M20,14 H22.4 V16 H20 Z"
        },
        new ToolDefinition
        {
            Id = "tool4",
            Name = "MiniMax AI",
            Type = "Automation Tool",
            Tagline = "MiniMax accounts, cookies and daily claim",
            AccentFrom = "#FFFBBF24",
            AccentTo = "#FFD97706",
            Driver = "python",
            Url = "https://www.minimax.io/",
            // The box id stays "tool4" so its existing workspace and cookie files
            // keep their folder; only the licence name became the tool's own.
            Product = "minimax",
            LegacyProducts = new List<string> { "dolatool4" },
            Icon = "M4.6,3 H9.4 A1.6,1.6 0 0 1 11,4.6 V9.4 A1.6,1.6 0 0 1 9.4,11 H4.6 A1.6,1.6 0 0 1 3,9.4 V4.6 A1.6,1.6 0 0 1 4.6,3 Z " +
                   "M14.6,3 H19.4 A1.6,1.6 0 0 1 21,4.6 V9.4 A1.6,1.6 0 0 1 19.4,11 H14.6 A1.6,1.6 0 0 1 13,9.4 V4.6 A1.6,1.6 0 0 1 14.6,3 Z " +
                   "M4.6,13 H9.4 A1.6,1.6 0 0 1 11,14.6 V19.4 A1.6,1.6 0 0 1 9.4,21 H4.6 A1.6,1.6 0 0 1 3,19.4 V14.6 A1.6,1.6 0 0 1 4.6,13 Z " +
                   "M14.6,13 H19.4 A1.6,1.6 0 0 1 21,14.6 V19.4 A1.6,1.6 0 0 1 19.4,21 H14.6 A1.6,1.6 0 0 1 13,19.4 V14.6 A1.6,1.6 0 0 1 14.6,13 Z"
        }
    };
}

/// <summary>Root object of <c>tools.json</c>.</summary>
public sealed class ToolListFile
{
    public List<ToolDefinition> Tools { get; set; } = new();
}
