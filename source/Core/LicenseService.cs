using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace AISuitePanel;

/// <summary>
/// Client-side RSA licence validator. Only the server public key is embedded here;
/// the signing private key and any signing secret remain server-side.
/// </summary>
public static class LicenseService
{
    /// <summary>
    /// The Licence endpoint copies of this panel ship with. Kept as the built-in
    /// default so a customer build always talks to the real server; an operator
    /// can point one machine somewhere else without a rebuild by setting
    /// <c>validateUrl</c> in <c>%APPDATA%\AISuitePanel\products.json</c>.
    ///
    /// Pointing the panel at another server does not weaken anything: every
    /// "valid" answer still has to carry an RSA signature made with the private
    /// key matching the public key embedded below, which only the licence server
    /// holds.
    /// </summary>
    public const string LiveLicenseUrl = "https://license.automationservice.online/api/validate.php";

    /// <summary>Effective endpoint: the products.json override, else the built-in one.</summary>
    public static string LicenseUrl => ProductCatalog.LicenseUrl;

    private const string PublicKeyPem = "-----BEGIN PUBLIC KEY-----\n" +
        "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEA2cg6K8JtbbQOpwQhsTZo\n" +
        "vem0MC6hEZJ1Dk6a00X0En1L9itVZZvfUqYdMP3CVcTgEEWPJHM5qNbd71DDOQKG\n" +
        "0mdlUzxHDGSzHGEH5egWzUwxkNcBtPI5z9DNBLEc6vlieDbarZobAFN8wDS0vo5C\n" +
        "yAptBiRZsxSrN7gP1e2akVjkVrCPKEM/Q5d6fHC/8tT1EuPIARoxxFrbYuDSxBP/\n" +
        "ivof15z7yO5OfgkcI6HrQ0LlYzjSzNVWqx0iXWoNsuiu6074X63l5U9ohMyb08um\n" +
        "Toyb8oPWJYjT06SqGsQd/gDfgapM8NrrF+luSG/fppicnc3lUqe/q2MEPmBpzdmf\n" +
        "cwIDAQAB\n-----END PUBLIC KEY-----";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static LicenseSnapshot? _current;

    public static event Action? Changed;

    /// <summary>
    /// Raises <see cref="Changed"/> on the UI thread.
    ///
    /// The validation pipeline awaits with <c>ConfigureAwait(false)</c> so it can
    /// run off the UI thread; the continuation that reaches here therefore often
    /// sits on a thread-pool thread. Subscribers touch WPF elements
    /// (<see cref="MainWindow"/> repaints the status pill), and that throws
    /// "The calling thread cannot access this object because a different thread
    /// owns it". Because the raise happened inside the try block of
    /// <see cref="ActivateAsync"/>, the exception was swallowed and reported as
    /// "License server unreachable: ..." - a successful activation looked like a
    /// failure while the licence file had already been written.
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

    public static bool IsActivated => _current is not null && !IsExpired(_current);
    public static string Key => _current?.Key ?? "";
    public static DateTime ActivatedAt => _current?.ActivatedAt ?? default;
    public static string? ExpiresAt => _current?.ExpiresAt;
    public static int? DaysRemaining => _current?.DaysRemaining;
    public static string MaskedKey => MaskKey(Key);

    /// <summary>Stable Windows MachineGuid-based identifier used by the server binding.</summary>
    public static string MachineId
    {
        get
        {
            using var baseKey = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            var machineGuid = key?.GetValue("MachineGuid")?.ToString();
            if (string.IsNullOrWhiteSpace(machineGuid))
                throw new InvalidOperationException("Windows MachineGuid is unavailable.");

            var raw = machineGuid + "|" + Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE");
            return "PC-" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..32];
        }
    }

    public static string Normalize(string raw) =>
        (raw ?? "").Trim().ToUpperInvariant();

    /// <summary>Loads only the local snapshot; startup must call RefreshStoredAsync before unlocking.</summary>
    internal static void Load()
    {
        _current = null;
        try
        {
            if (File.Exists(AppPaths.License))
            {
                var record = JsonSerializer.Deserialize<LicenseSnapshot>(
                    File.ReadAllText(AppPaths.License), JsonOptions);
                if (record is not null &&
                    string.Equals(record.MachineId, MachineId, StringComparison.Ordinal))
                {
                    _current = record;
                    return;
                }
            }

            // Backward-compatible migration: keep the old file untouched and
            // revalidate its key online before accepting it as the new main key.
            if (!File.Exists(AppPaths.LegacyLicense)) return;
            var legacyLines = File.ReadAllLines(AppPaths.LegacyLicense);
            if (legacyLines.Length == 0 || string.IsNullOrWhiteSpace(legacyLines[0])) return;
            _current = new LicenseSnapshot
            {
                Key = Normalize(legacyLines[0]),
                Product = ProductCatalog.ProductFromKey(legacyLines[0])
                          ?? ProductCatalog.PanelProducts.FirstOrDefault() ?? "panel",
                MachineId = MachineId,
                ActivatedAt = DateTime.Now
            };
        }
        catch
        {
            _current = null;
        }
    }

    /// <summary>Strict online gate: a stored key is trusted only after a fresh signed response.</summary>
    internal static async Task RefreshStoredAsync()
    {
        var snapshot = _current;
        if (snapshot is null)
        {
            _current = null;
            return;
        }

        try
        {
            // The product is the key's own prefix, so there is nothing to guess
            // and only one call to make. A stored key whose product may no longer
            // open the panel is dropped.
            var product = ProductCatalog.ProductFromKey(snapshot.Key) ?? snapshot.Product;
            if (!ProductCatalog.IsPanelProduct(product))
            {
                _current = null;
                return;
            }

            var result = await ValidateOnlineAsync(snapshot.Key, product, activate: false)
                .ConfigureAwait(false);

            _current = result.Ok &&
                       result.Snapshot is not null &&
                       ProductCatalog.IsPanelProduct(result.Snapshot.Product)
                ? result.Snapshot
                : null;
        }
        catch
        {
            _current = null;
        }
    }

    public static async Task<(bool Ok, string Message)> ActivateAsync(string rawKey)
    {
        var key = Normalize(rawKey);
        if (key.Length < 4 || key.Length > 128)
            return (false, "Enter the complete licence key.");

        var product = ProductCatalog.ProductFromKey(key);
        if (product is null)
            return (false, "That does not look like a licence key. It should start with the "
                           + "product name, like " + ProductCatalog.PanelKeyFormat + ".");

        // Decided locally and BEFORE any call, so pasting a tool's key here can
        // never bind that key to this PC while being refused.
        if (!ProductCatalog.IsPanelProduct(product))
            return (false, WrongScreenMessage(product));

        try
        {
            var result = await ValidateOnlineAsync(key, product, activate: true).ConfigureAwait(false);
            if (!result.Ok || result.Snapshot is null)
                return (false, result.Message);

            // Belt and braces: the server's answer is authoritative about which
            // product the key really is.
            if (!ProductCatalog.IsPanelProduct(result.Snapshot.Product))
                return (false, WrongScreenMessage(result.Snapshot.Product));

            _current = result.Snapshot;
            Save(AppPaths.License, _current);
            RaiseChanged();
            return (true, result.Message);
        }
        catch (Exception ex)
        {
            return (false, "License server unreachable: " + ex.Message);
        }
    }

    /// <summary>
    /// "This is a Z AI key - open the Z AI box and enter it there." Naming the
    /// right box is far more useful than a bare refusal.
    /// </summary>
    internal static string WrongScreenMessage(string product)
    {
        var tool = ToolCatalog.FindByProduct(product);
        return tool is not null
            ? $"This is a {tool.Name} key. Open the {tool.Name} box and enter it there."
            : $"This key is for {product.ToUpperInvariant()}, so it cannot unlock the main panel.";
    }

    internal static async Task<OnlineValidation> ValidateOnlineAsync(
        string rawKey, string product, bool activate)
    {
        var key = Normalize(rawKey);
        var device = MachineId;
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        var request = new ValidationRequest
        {
            LicenseKey = key,
            DeviceId = device,
            Nonce = nonce,
            Product = product.ToLowerInvariant(),
            Activate = activate,
            Device = new DeviceMetadata
            {
                Hostname = Environment.MachineName,
                Os = $"Windows {Environment.OSVersion.Version}",
                Arch = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "AMD64",
                User = Environment.UserName,
                AppVersion = typeof(App).Assembly.GetName().Version?.ToString() ?? "1.0.0"
            }
        };

        using var content = new StringContent(
            JsonSerializer.Serialize(request, JsonOptions), Encoding.UTF8, "application/json");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, LicenseUrl)
        {
            Content = content
        };
        httpRequest.Headers.Accept.ParseAdd("application/json");
        httpRequest.Headers.UserAgent.ParseAdd("AISuitePanel/1.0");

        using var response = await Http.SendAsync(httpRequest).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        ServerResponse? server;
        try
        {
            server = JsonSerializer.Deserialize<ServerResponse>(body, JsonOptions);
        }
        catch
        {
            return new OnlineValidation(false, "Invalid licence server response.", null);
        }

        if (server is null)
            return new OnlineValidation(false, "Invalid licence server response.", null);
        if (server.Valid is not true)
            return new OnlineValidation(false, server.Message ?? $"License server HTTP {(int)response.StatusCode}.", null);
        if (!string.Equals(server.Nonce, nonce, StringComparison.Ordinal))
            return new OnlineValidation(false, "Nonce mismatch - response rejected.", null);
        if (!VerifySignature(server, key, device, nonce))
            return new OnlineValidation(false, "RSA signature verification failed.", null);
        if (!string.IsNullOrWhiteSpace(server.Product) &&
            !string.Equals(server.Product, product, StringComparison.OrdinalIgnoreCase))
            return new OnlineValidation(false, "License product mismatch.", null);

        var snapshot = new LicenseSnapshot
        {
            Key = key,
            Product = product.ToLowerInvariant(),
            MachineId = device,
            ActivatedAt = DateTime.Now,
            ServerTime = server.ServerTime ?? "",
            ExpiresAt = server.ExpiresAt,
            LicenseType = server.LicenseType ?? "",
            DaysRemaining = server.DaysRemaining,
            Message = server.Message ?? "OK"
        };

        if (IsExpired(snapshot))
            return new OnlineValidation(false, "This licence has expired.", null);

        return new OnlineValidation(true, snapshot.Message, snapshot);
    }

    public static void Reset()
    {
        _current = null;
        try { if (File.Exists(AppPaths.License)) File.Delete(AppPaths.License); }
        catch { }
        RaiseChanged();
    }

    internal static void Save(string path, LicenseSnapshot snapshot)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(snapshot, JsonOptions));
        }
        catch
        {
            // The online session remains valid even if persistence is unavailable.
        }
    }

    private static bool VerifySignature(ServerResponse response, string key, string device, string nonce)
    {
        if (string.IsNullOrWhiteSpace(response.ServerTime) || string.IsNullOrWhiteSpace(response.SigV2))
            return false;

        var expires = string.IsNullOrEmpty(response.ExpiresAt) ? "lifetime" : response.ExpiresAt;
        var canonical = $"valid=1|key={key}|device={device}|expires={expires}|nonce={nonce}|time={response.ServerTime}";

        try
        {
            using var publicKey = RSA.Create();
            publicKey.ImportFromPem(PublicKeyPem);
            return publicKey.VerifyData(
                Encoding.UTF8.GetBytes(canonical),
                Convert.FromHexString(response.SigV2),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsExpired(LicenseSnapshot snapshot)
    {
        if (string.Equals(snapshot.ExpiresAt, "lifetime", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(snapshot.ExpiresAt)) return false;

        return DateTimeOffset.TryParse(
            snapshot.ExpiresAt, out var expiry) && expiry <= DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// "ACCIO-QNXAA-CZX8W-XKA59DDC" becomes "ACCIO-••••9DDC".
    ///
    /// The prefix is kept whole on purpose: it is the licence's name, and the
    /// dashboard shows it so it is obvious which key opened which box. Chopping
    /// the first four characters turned ACCIO into ACCI and MINIMAX into MINI,
    /// which defeats the point of naming a licence after its tool.
    /// </summary>
    internal static string MaskKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "";

        var text = key.Trim();
        var dash = text.IndexOf('-');
        var head = dash > 0 ? text[..dash] : "";
        var tail = text.Length >= 4 ? text[^4..] : text;

        return head.Length > 0 ? head + "-••••" + tail : "••••" + tail;
    }

    internal sealed class OnlineValidation
    {
        public OnlineValidation(bool ok, string message, LicenseSnapshot? snapshot)
        {
            Ok = ok;
            Message = message;
            Snapshot = snapshot;
        }

        public bool Ok { get; }
        public string Message { get; }
        public LicenseSnapshot? Snapshot { get; }
    }

    internal sealed class ValidationRequest
    {
        [JsonPropertyName("license_key")] public string LicenseKey { get; set; } = "";
        [JsonPropertyName("device_id")] public string DeviceId { get; set; } = "";
        [JsonPropertyName("nonce")] public string Nonce { get; set; } = "";
        [JsonPropertyName("product")] public string Product { get; set; } = "";
        [JsonPropertyName("activate")] public bool Activate { get; set; }
        [JsonPropertyName("device")] public DeviceMetadata Device { get; set; } = new();
    }

    internal sealed class DeviceMetadata
    {
        [JsonPropertyName("hostname")] public string Hostname { get; set; } = "";
        [JsonPropertyName("os")] public string Os { get; set; } = "";
        [JsonPropertyName("arch")] public string Arch { get; set; } = "";
        [JsonPropertyName("user")] public string User { get; set; } = "";
        [JsonPropertyName("app_version")] public string AppVersion { get; set; } = "";
    }

    internal sealed class ServerResponse
    {
        [JsonPropertyName("valid")] public bool? Valid { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("nonce")] public string? Nonce { get; set; }
        [JsonPropertyName("server_time")] public string? ServerTime { get; set; }
        [JsonPropertyName("expires_at")] public string? ExpiresAt { get; set; }
        [JsonPropertyName("license_type")] public string? LicenseType { get; set; }
        [JsonPropertyName("days_remaining")] public int? DaysRemaining { get; set; }
        [JsonPropertyName("sig_v2")] public string? SigV2 { get; set; }
        [JsonPropertyName("product")] public string? Product { get; set; }
    }
}

public sealed class LicenseSnapshot
{
    public string Key { get; set; } = "";
    public string Product { get; set; } = "";
    public string MachineId { get; set; } = "";
    public DateTime ActivatedAt { get; set; }
    public string ServerTime { get; set; } = "";
    public string? ExpiresAt { get; set; }
    public string LicenseType { get; set; } = "";
    public int? DaysRemaining { get; set; }
    public string Message { get; set; } = "";
}
