using System.IO;
using System.Text.Json;

namespace AISuitePanel;

/// <summary>
/// Stores one independent server-validated licence per tool. Product slugs are
/// client configuration only; the server panel must contain matching products.
///
/// The per-tool product codes used to be a hard-coded switch in this file, so a
/// product added on the licence server needed a rebuild before a tool key would
/// activate. They now come from <see cref="ProductCatalog"/> - the editable
/// <c>%APPDATA%\AISuitePanel\products.json</c> - and the product actually used
/// for a successful activation is remembered there automatically.
/// </summary>
public static class ToolLicenseService
{
    private static readonly Dictionary<string, ToolLicenseState> States =
        new(StringComparer.OrdinalIgnoreCase);

    public static event Action? Changed;

    public static void NotifyChanged() => Changed?.Invoke();

    /// <summary>
    /// Product this box claims - its own entry in <c>tools.json</c>, or its id.
    /// "zai" needs no configuration at all.
    /// </summary>
    public static string ProductFor(string toolId) =>
        ToolCatalog.Find(toolId)?.Product is { Length: > 0 } product
            ? product
            : ProductCatalog.Slug(toolId);

    /// <summary>
    /// Products this box accepts: its own product first, then any older name the
    /// same tool was sold under (e.g. zai accepts both "zai" and "dolazai"), so a
    /// key issued before the rename keeps working.
    /// </summary>
    public static IReadOnlyList<string> AcceptedFor(string toolId) =>
        ToolCatalog.Find(toolId)?.AcceptedProducts is { Count: > 0 } accepted
            ? accepted
            : new[] { ProductCatalog.Slug(toolId) };

    /// <summary>What a key for this box looks like: "ZAI-XXXXX-XXXXX-XXXXXXXX".</summary>
    public static string KeyFormatFor(string toolId) =>
        ProductFor(toolId).ToUpperInvariant() + "-XXXXX-XXXXX-XXXXXXXX";

    /// <summary>Days left on a tool's licence, or null when it has none.</summary>
    public static int? DaysFor(string toolId) =>
        States.TryGetValue(toolId, out var state) ? state.DaysRemaining : null;

    public static bool IsLicensed(string toolId) =>
        States.TryGetValue(toolId, out var state) &&
        !string.IsNullOrWhiteSpace(state.Key) &&
        !IsExpired(state);

    public static string KeyFor(string toolId) =>
        States.TryGetValue(toolId, out var state) ? state.Key : "";

    public static string StatusFor(string toolId)
    {
        if (!States.TryGetValue(toolId, out var state) || string.IsNullOrWhiteSpace(state.Key))
            return "Licence required";
        if (IsExpired(state)) return "Licence expired";
        return state.DaysRemaining is null ? "Licensed" : $"Licensed · {state.DaysRemaining} day(s)";
    }

    public static string MaskedKeyFor(string toolId) =>
        LicenseService.MaskKey(KeyFor(toolId));

    internal static void Load()
    {
        States.Clear();
        try
        {
            if (!File.Exists(AppPaths.ToolLicenses)) return;
            var loaded = JsonSerializer.Deserialize<Dictionary<string, ToolLicenseState>>(
                File.ReadAllText(AppPaths.ToolLicenses), JsonOptions);
            if (loaded is null) return;
            foreach (var item in loaded)
                States[item.Key] = item.Value;
        }
        catch
        {
            States.Clear();
        }
    }

    internal static async Task RefreshStoredAsync()
    {
        foreach (var item in States.ToArray())
        {
            try
            {
                // One product, taken from the key's own prefix, so this is one
                // call per stored licence instead of a fan-out.
                var product = ProductCatalog.ProductFromKey(item.Value.Key) ?? item.Value.Product;

                if (!AcceptedFor(item.Key).Any(p => ProductCatalog.SameProduct(p, product)))
                {
                    States.Remove(item.Key);
                    continue;
                }

                var result = await LicenseService.ValidateOnlineAsync(item.Value.Key, product, activate: false)
                    .ConfigureAwait(false);

                if (result.Ok && result.Snapshot is not null)
                    States[item.Key] = FromSnapshot(result.Snapshot);
                else
                    States.Remove(item.Key);
            }
            catch
            {
                States.Remove(item.Key);
            }
        }

        Save();
        RaiseChanged();
    }

    /// <summary>
    /// Raises <see cref="Changed"/> on the UI thread. The validation pipeline runs
    /// with <c>ConfigureAwait(false)</c>, so the continuation can land on a pool
    /// thread and a subscriber that touches WPF elements would throw - the same
    /// trap <see cref="LicenseService"/> had.
    /// </summary>
    private static void RaiseChanged()
    {
        var handler = Changed;
        if (handler is null) return;

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            handler();
            return;
        }

        dispatcher.Invoke(handler);
    }

    public static async Task<(bool Ok, string Message)> ActivateAsync(string toolId, string rawKey)
    {
        var key = LicenseService.Normalize(rawKey);
        if (string.IsNullOrWhiteSpace(key)) return (false, "Enter a licence key.");

        var product = ProductCatalog.ProductFromKey(key);
        if (product is null)
            return (false, "That does not look like a licence key. It should start with the "
                           + "product name, like " + KeyFormatFor(toolId) + ".");

        // Checked here, BEFORE any call, so a key belonging to another box can
        // never be bound to this PC on the way to being refused.
        if (!AcceptedFor(toolId).Any(p => ProductCatalog.SameProduct(p, product)))
            return (false, WrongBoxMessage(toolId, product));

        try
        {
            var result = await LicenseService.ValidateOnlineAsync(key, product, activate: true)
                .ConfigureAwait(false);
            if (!result.Ok || result.Snapshot is null)
                return (false, result.Message);

            // The server's answer is authoritative about which product the key is.
            if (!AcceptedFor(toolId).Any(p => ProductCatalog.SameProduct(p, result.Snapshot.Product)))
                return (false, WrongBoxMessage(toolId, result.Snapshot.Product));

            States[toolId] = FromSnapshot(result.Snapshot);
            Save();
            RaiseChanged();
            return (true, result.Message);
        }
        catch (Exception ex)
        {
            return (false, "License server unreachable: " + ex.Message);
        }
    }

    /// <summary>
    /// "This is a Z AI key - open the Z AI box." Naming where the key does belong
    /// is far more useful than a bare refusal.
    /// </summary>
    private static string WrongBoxMessage(string toolId, string product)
    {
        var owner = ToolCatalog.FindByProduct(product);
        if (owner is not null && !string.Equals(owner.Id, toolId, StringComparison.OrdinalIgnoreCase))
            return $"This is a {owner.Name} key. Open the {owner.Name} box and enter it there.";

        if (ProductCatalog.IsPanelProduct(product))
            return "This is a main panel key. Enter it on the first screen, not inside a tool.";

        return $"This key is for {product.ToUpperInvariant()}, not for "
               + ToolLicenseService.ProductFor(toolId).ToUpperInvariant() + ".";
    }

    public static void Reset(string toolId)
    {
        States.Remove(toolId);
        Save();
        RaiseChanged();
    }

    private static ToolLicenseState FromSnapshot(LicenseSnapshot snapshot) => new()
    {
        Key = snapshot.Key,
        Product = snapshot.Product,
        MachineId = snapshot.MachineId,
        ActivatedAt = snapshot.ActivatedAt,
        ServerTime = snapshot.ServerTime,
        ExpiresAt = snapshot.ExpiresAt,
        LicenseType = snapshot.LicenseType,
        DaysRemaining = snapshot.DaysRemaining
    };

    private static bool IsExpired(ToolLicenseState state)
    {
        if (string.Equals(state.ExpiresAt, "lifetime", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(state.ExpiresAt)) return false;

        return DateTimeOffset.TryParse(state.ExpiresAt, out var expiry) &&
               expiry <= DateTimeOffset.UtcNow;
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(AppPaths.ToolLicenses, JsonSerializer.Serialize(States, JsonOptions));
        }
        catch
        {
            // The current online session remains usable even if local persistence fails.
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private sealed class ToolLicenseState
    {
        public string Key { get; set; } = "";
        public string Product { get; set; } = "";
        public string MachineId { get; set; } = "";
        public DateTime ActivatedAt { get; set; }
        public string ServerTime { get; set; } = "";
        public string? ExpiresAt { get; set; }
        public string LicenseType { get; set; } = "";
        public int? DaysRemaining { get; set; }
    }
}
