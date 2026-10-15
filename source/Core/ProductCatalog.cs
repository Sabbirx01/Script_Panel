using System.IO;

namespace AISuitePanel;

/// <summary>
/// The licence rule for the whole panel, in one sentence:
///
///     a key's product IS the prefix printed on the key.
///
///     ZAI-XXXXX-XXXXX-XXXXXXXX     -> product "zai"
///     PANEL-XXXXX-XXXXX-XXXXXXXX   -> product "panel"
///     DEVIN-XXXXX-XXXXX-XXXXXXXX   -> product "devin"
///
/// The server checks the key exists, is bound to this PC, is unexpired and that
/// the product we claim matches the row it holds. It never needs a product list
/// from us. So the client does not have to guess anything either - which leaves
/// this class with exactly one job: deciding which products a *valid* key may
/// use to open the main panel (screen 1). Everything tool-specific belongs to
/// the tool and lives in <c>tools.json</c>.
///
/// Why it used to be complicated: the client tried a list of candidate products
/// for every key (key prefix -> known products -> main product), remembered
/// which one worked and wrote that back to disk. Guessing wrong often enough
/// produced ~30 HTTP calls on a single startup, and the server's product list
/// had to stay in sync with the client's. None of that is needed when the prefix
/// already IS the product.
///
/// Old keys keep working: the prefix on an issued key never changes, and the
/// server still holds the row it was created with, so "DOLAZAI-..." is simply
/// product "dolazai" and validates exactly as it always did. A tool box accepts
/// those older names through its own <c>legacyProducts</c> list.
/// </summary>
public static class ProductCatalog
{
    /// <summary>
    /// Products that may open the main panel when <c>products.json</c> says
    /// nothing. "panel" is the new name; "dola" is the original one, kept so the
    /// key an operator already activated does not stop working.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultPanelProducts = new[] { "panel", "dola" };

    private static ProductConfig _config = ProductConfig.CreateDefault();

    /// <summary>Raised after <see cref="Load"/> rewrites the registry.</summary>
    public static event Action? Changed;

    public static string ConfigPath => AppPaths.Products;

    /// <summary>
    /// Licence endpoint this machine talks to. Empty means the panel's built-in
    /// endpoint (<see cref="LicenseService.LiveLicenseUrl"/>), which is what every
    /// customer copy keeps; an operator who configures <c>validateUrl</c> in
    /// products.json can run against a local or staging licence server instead,
    /// without rebuilding the panel.
    /// </summary>
    public static string LicenseUrl =>
        string.IsNullOrWhiteSpace(_config.ValidateUrl)
            ? LicenseService.LiveLicenseUrl
            : _config.ValidateUrl!.Trim();

    // ------------------------------------------------------------- panel access

    /// <summary>Products a valid key may use to unlock the whole panel.</summary>
    public static IReadOnlyList<string> PanelProducts => _config.PanelProducts;

    /// <summary>
    /// True when a key carrying this product may open the main panel.
    ///
    /// A product a tool box claims is never a panel product, whatever
    /// <c>panelProducts</c> says - one licence per box is the whole point, and a
    /// Z AI key must not unlock the panel even if someone lists "zai" there by
    /// mistake (or an older products.json implied it).
    /// </summary>
    public static bool IsPanelProduct(string? product) =>
        !ToolCatalog.ClaimsProduct(product) &&
        _config.PanelProducts.Any(p => SameProduct(p, product));

    /// <summary>The product users see on a panel key, e.g. "PANEL-XXXXX-…".</summary>
    public static string PanelKeyFormat =>
        (_config.PanelProducts.Count > 0 ? _config.PanelProducts[0] : "panel").ToUpperInvariant()
        + "-XXXXX-XXXXX-XXXXXXXX";

    // ------------------------------------------------------------- key handling

    /// <summary>
    /// The product a key belongs to: the text before its first dash, lower-cased.
    /// Returns null when the key carries no usable prefix, so a pasted password
    /// or a stray word is refused before any network call.
    /// </summary>
    public static string? ProductFromKey(string? rawKey)
    {
        var key = LicenseService.Normalize(rawKey ?? "");
        if (key.Length == 0) return null;

        var dash = key.IndexOf('-');
        var head = (dash < 0 ? key : key[..dash]).Trim();
        if (head.Length == 0) return null;

        // A prefix that was renamed after keys were sold is mapped here.
        if (_config.KeyOverrides.TryGetValue(head, out var mapped) && !string.IsNullOrWhiteSpace(mapped))
            return Slug(mapped);

        var slug = Slug(head);

        // A key with no letters at all is not a product prefix.
        return slug.Length == 0 || !slug.Any(char.IsLetter) ? null : slug;
    }

    /// <summary>
    /// Server product codes are alphanumeric - the licence panel strips
    /// punctuation - so every comparison happens on the slug form.
    /// </summary>
    public static string Slug(string? value) =>
        new string((value ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    public static bool SameProduct(string? left, string? right) =>
        string.Equals(Slug(left), Slug(right), StringComparison.Ordinal);

    // ------------------------------------------------------------ persistence

    internal static void Load()
    {
        var defaults = ProductConfig.CreateDefault();

        if (!File.Exists(ConfigPath))
        {
            // First run: leave an editable copy next to the other data files.
            _config = defaults;
            Save();
            Changed?.Invoke();
            return;
        }

        var loaded = Json.Load(ConfigPath, () => new ProductConfig());
        _config = Merge(defaults, loaded);
        Save();
        Changed?.Invoke();
    }

    private static void Save() => Json.Save(ConfigPath, _config);

    /// <summary>
    /// Fills the gaps in a hand-edited file from the built-in defaults so a
    /// half-written products.json never leaves the panel unable to activate.
    /// A file from the older shape (mainProduct / knownProducts / toolProducts)
    /// is read as "those products may open the panel", minus the tool ones, so an
    /// operator upgrading never has to touch the file by hand.
    /// </summary>
    private static ProductConfig Merge(ProductConfig defaults, ProductConfig loaded)
    {
        var merged = new ProductConfig
        {
            ValidateUrl = string.IsNullOrWhiteSpace(loaded.ValidateUrl)
                ? defaults.ValidateUrl
                : loaded.ValidateUrl!.Trim(),
            PanelProducts = new List<string>(),
            KeyOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };

        if (loaded.PanelProducts is { Count: > 0 })
        {
            foreach (var product in loaded.PanelProducts) Add(merged.PanelProducts, product);
        }
        else
        {
            // Upgrading from the old shape: keep whatever could open the panel
            // before, and make sure the new default is there too.
            foreach (var product in loaded.LegacyPanelProducts()) Add(merged.PanelProducts, product);
            foreach (var product in defaults.PanelProducts) Add(merged.PanelProducts, product);
        }

        // An empty list would lock the operator out of the panel they own.
        if (merged.PanelProducts.Count == 0)
            foreach (var product in defaults.PanelProducts) Add(merged.PanelProducts, product);

        foreach (var pair in loaded.KeyOverrides ?? new Dictionary<string, string>())
        {
            var prefix = NormalizePrefix(pair.Key);
            var product = Slug(pair.Value);
            if (prefix.Length == 0 || product.Length == 0) continue;
            merged.KeyOverrides[prefix] = product;
        }

        return merged;
    }

    private static void Add(ICollection<string> target, string? product)
    {
        var slug = Slug(product);
        if (slug.Length == 0) return;
        if (target.Any(p => SameProduct(p, slug))) return;
        target.Add(slug);
    }

    private static string NormalizePrefix(string? value) =>
        new string((value ?? "").Trim().ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
}

/// <summary>Shape of <c>%APPDATA%\AISuitePanel\products.json</c>.</summary>
public sealed class ProductConfig
{
    /// <summary>
    /// Products that may unlock the whole panel. Default: panel, dola.
    /// </summary>
    public List<string> PanelProducts { get; set; } = new();

    /// <summary>
    /// Rare escape hatch: key prefix -> product, for a prefix that was renamed on
    /// the licence server AFTER keys had been sold with it. Empty in normal use,
    /// because a product's prefix is meant to be fixed when it is created.
    /// </summary>
    public Dictionary<string, string> KeyOverrides { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Licence endpoint override. Empty / missing = the panel's built-in live
    /// endpoint, so a shipped copy is unaffected by this field.
    /// </summary>
    public string? ValidateUrl { get; set; }

    // ------------------------------------------------------- legacy file shape

    /// <summary>Old field, still read so an existing products.json keeps working.</summary>
    public string? MainProduct { get; set; }

    /// <summary>Old field, still read so an existing products.json keeps working.</summary>
    public List<string>? KnownProducts { get; set; }

    /// <summary>Old field, still read so an existing products.json keeps working.</summary>
    public Dictionary<string, string>? ToolProducts { get; set; }

    /// <summary>
    /// The products an old-shaped file implied for the panel: its main product,
    /// plus the other known products that were not tool products.
    /// </summary>
    internal IEnumerable<string> LegacyPanelProducts()
    {
        var tools = (ToolProducts?.Values ?? Enumerable.Empty<string>())
            .Select(ProductCatalog.Slug)
            .Where(v => v.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(MainProduct) &&
            !tools.Contains(ProductCatalog.Slug(MainProduct)))
        {
            yield return MainProduct!;
        }

        foreach (var product in KnownProducts ?? new List<string>())
        {
            var slug = ProductCatalog.Slug(product);
            if (slug.Length == 0 || tools.Contains(slug)) continue;

            // "accio" used to sit in this list as an ordinary product, but it is
            // now the Accio AI box's own licence, and a tool key must never open
            // the panel. Dropping it here keeps the migrated file honest.
            if (ToolCatalog.ClaimsProduct(slug)) continue;

            yield return slug;
        }
    }

    public static ProductConfig CreateDefault() => new()
    {
        PanelProducts = new List<string>(ProductCatalog.DefaultPanelProducts),
        KeyOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    };
}
