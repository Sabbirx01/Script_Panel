using System.IO;
using System.Text;
using System.Text.Json;
using AISuitePanel.Models;

namespace AISuitePanel;

/// <summary>
/// The cookie vault behind "Saved Cookies: N" on the dashboard and everything
/// section 2/3 of a tool panel does.
///
/// Storage: <c>%APPDATA%\AISuitePanel\cookies\{toolId}.json</c>.
///
/// Note on secrecy: app passwords and cookie values are stored in clear so the
/// panel stays debuggable on your own machine. Before shipping this, wrap
/// <c>Json.Save</c>/<c>Json.Load</c> here and in <see cref="SettingsStore"/> with
/// DPAPI - see BLUEPRINT.md §7.
/// </summary>
public static class CookieVault
{
    private static readonly Dictionary<string, List<CookieRecord>> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly object Gate = new();

    /// <summary>Raised with the tool id after any change - the dashboard listens.</summary>
    public static event Action<string>? Changed;

    public static void LoadAll()
    {
        foreach (var tool in ToolCatalog.Tools) _ = Mutable(tool.Id);
    }

    public static IReadOnlyList<CookieRecord> For(string toolId) =>
        Merge(Snapshot(toolId), ToolLayer(toolId));

    /// <summary>
    /// "Saved Cookies: N" on the dashboard.
    ///
    /// <para>Counts the vault AND the cookies the tool keeps in its own folder,
    /// de-duplicated by address. Before this it counted only the vault, which
    /// nothing ever filled, so the dashboard read 0 while the tools' own files
    /// held 10, 13, 27 and 12.
    /// </summary>
    public static int CountFor(string toolId) => For(toolId).Count;

    public static CookieRecord? Get(string toolId, int accountId)
    {
        var merged = For(toolId);

        var direct = merged.FirstOrDefault(c => c.AccountId == accountId);
        if (direct is not null) return direct;

        // Nothing filed under that id, so find out which ADDRESS the id belongs
        // to and match on that. This is what lets a freshly created account log
        // in: its cookie is written by the tool into the tool's own folder, keyed
        // by email, and never reached this vault.
        var email = AccountStore.For(toolId).FirstOrDefault(a => a.Id == accountId)?.EmailUsed;
        if (string.IsNullOrWhiteSpace(email)) return null;

        return merged.FirstOrDefault(c => SameMailbox(c.Email, email));
    }

    public static void Save(string toolId, CookieRecord record)
    {
        lock (Gate)
        {
            var list = Mutable(toolId);
            var index = list.FindIndex(c => c.AccountId == record.AccountId);

            if (index >= 0) list[index] = record;
            else list.Add(record);

            Json.Save(AppPaths.CookiesFile(toolId), list);
        }

        Changed?.Invoke(toolId);
    }

    public static void Delete(string toolId, int accountId)
    {
        lock (Gate)
        {
            var list = Mutable(toolId);
            list.RemoveAll(c => c.AccountId == accountId);
            Json.Save(AppPaths.CookiesFile(toolId), list);
        }

        Changed?.Invoke(toolId);
    }

    /// <summary>
    /// Drops every vault entry for one address. Accio's delete calls this so a
    /// removed account cannot keep showing up under "Saved Cookies" through a
    /// stale vault record whose AccountId no longer matches any row.
    /// </summary>
    public static void DeleteForEmail(string toolId, string email)
    {
        if (string.IsNullOrWhiteSpace(email)) return;

        lock (Gate)
        {
            var list = Mutable(toolId);
            if (list.RemoveAll(c => SameMailbox(c.Email, email)) == 0) return;
            Json.Save(AppPaths.CookiesFile(toolId), list);
        }

        Changed?.Invoke(toolId);
    }

    public static void DeleteAll(string toolId)
    {
        lock (Gate)
        {
            Mutable(toolId).Clear();
            Json.Save(AppPaths.CookiesFile(toolId), new List<CookieRecord>());
        }

        Changed?.Invoke(toolId);
    }

    // -------------------------------------------------------------- exporting

    /// <summary>Pretty JSON - same shape as the vault file.</summary>
    public static string ExportJson(string toolId) =>
        JsonSerializer.Serialize(For(toolId), Json.Options);

    /// <summary>One cookie per line, tab separated, easy to eyeball or grep.</summary>
    public static string ExportTxt(string toolId)
    {
        var sb = new StringBuilder();
        sb.AppendLine("email\tname\tvalue\tdomain\tpath\texpires");

        foreach (var record in For(toolId))
        {
            foreach (var cookie in record.Cookies)
            {
                sb.Append(record.Email).Append('\t')
                  .Append(cookie.Name).Append('\t')
                  .Append(cookie.Value).Append('\t')
                  .Append(cookie.Domain).Append('\t')
                  .Append(cookie.Path).Append('\t')
                  .Append(cookie.Expires)
                  .AppendLine();
            }
        }

        return sb.ToString();
    }

    public static string SuggestedExportName(string toolId, string extension) =>
        $"{toolId}-cookies-{DateTime.Now:yyyyMMdd-HHmm}.{extension}";

    // --------------------------------------------------- the tools' own folders

    private static readonly Dictionary<string, (DateTime At, List<CookieRecord> List)> ToolCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly object ToolGate = new();

    /// <summary>How long a read of the tools' folders is reused. Short, so an
    /// account created a moment ago shows up almost at once.</summary>
    private static readonly TimeSpan ToolCacheFor = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Drops the cached read of one tool's own cookie folder. AccountStore calls
    /// this after a refresh (i.e. right after a create run), so an account whose
    /// cookie file was written seconds ago is found on the very next lookup
    /// instead of after the cache expires.
    /// </summary>
    public static void InvalidateToolLayer(string toolId)
    {
        lock (ToolGate) ToolCache.Remove(toolId);
    }

    /// <summary>
    /// The cookies each tool keeps in its OWN folder, as a read-only layer.
    ///
    /// <para>The tools write <c>accounts\&lt;tool&gt;\cookies.txt</c>, one line per
    /// account in the form <c>email | [cookie json]</c>; accio instead keeps one
    /// json file per account under <c>accounts\accio\accounts\cookies\</c>.
    ///
    /// <para>This vault is a DIFFERENT file
    /// (<c>%APPDATA%\AISuitePanel\cookies\&lt;toolId&gt;.json</c>) and nothing ever
    /// copied between the two - so the dashboard said "Saved Cookies: 0" and
    /// Login had nothing to send, even though the cookies were sitting in the
    /// tool's own folder the whole time. Reading them here fixes both without
    /// moving, copying or rewriting a single file: every tool keeps its own
    /// folder, and one tool's cookies never reach another's.
    /// </summary>
    private static List<CookieRecord> ToolLayer(string toolId)
    {
        lock (ToolGate)
        {
            if (ToolCache.TryGetValue(toolId, out var cached) &&
                DateTime.UtcNow - cached.At < ToolCacheFor)
            {
                return cached.List;
            }

            var list = ReadToolCookieFiles(toolId);
            ToolCache[toolId] = (DateTime.UtcNow, list);
            return list;
        }
    }

    private static List<CookieRecord> ReadToolCookieFiles(string toolId)
    {
        var list = new List<CookieRecord>();
        try
        {
            var folder = AppPaths.ToolAccountsDir(toolId);

            // z.ai / Devin / MiniMax: one line per account.
            var file = Path.Combine(folder, "cookies.txt");
            if (File.Exists(file))
            {
                foreach (var line in File.ReadAllLines(file, Encoding.UTF8))
                {
                    var bar = line.IndexOf('|');
                    if (bar <= 0) continue;
                    var record = BuildRecord(toolId, line[..bar].Trim(), line[(bar + 1)..].Trim());
                    if (record is not null) list.Add(record);
                }
            }

            // accio: one file per account, named in accounts.json.
            var cookieDir = Path.Combine(folder, "accounts", "cookies");
            if (Directory.Exists(cookieDir))
            {
                foreach (var path in Directory.GetFiles(cookieDir, "*.json"))
                {
                    string email;
                    try
                    {
                        email = EmailForCookieFile(folder, Path.GetFileName(path));
                    }
                    catch (Exception)                          // noqa: BLE001
                    {
                        continue;
                    }
                    var record = BuildRecord(toolId, email, File.ReadAllText(path, Encoding.UTF8));
                    if (record is not null) list.Add(record);
                }
            }
        }
        catch (Exception)                                      // noqa: BLE001
        {
            // A missing or unreadable folder just means no extra cookies.
        }

        return list;
    }

    private static CookieRecord? BuildRecord(string toolId, string email, string json)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(json)) return null;

        List<BrowserCookie>? jar;
        try
        {
            // Playwright writes camelCase names; Json.Options is
            // case-insensitive, so this binds straight onto BrowserCookie.
            jar = JsonSerializer.Deserialize<List<BrowserCookie>>(json, Json.Options);
        }
        catch (Exception)                                      // noqa: BLE001
        {
            return null;
        }

        if (jar is null || jar.Count == 0) return null;

        return new CookieRecord
        {
            Email = email,
            AccountId = AccountStore.For(toolId)
                .FirstOrDefault(a => SameMailbox(a.EmailUsed, email))?.Id ?? -1,
            SavedAt = DateTime.Now,
            Cookies = jar,
        };
    }

    /// <summary>
    /// accio's accounts.json maps each account's address to its cookie file.
    ///
    /// <para>The index sits one level BELOW the folder this is handed:
    /// <c>accounts\accio\accounts\accounts.json</c> with the cookie files in the
    /// sibling <c>cookies\</c> folder. Looking for <c>accounts.json</c> directly
    /// in the folder above found nothing, so every cookie file came back with an
    /// empty address, <see cref="BuildRecord"/> dropped it, the tool layer was
    /// empty and Login reported "No cookies found" for accounts whose cookies
    /// were sitting on disk. A flat index is only the pre-migration layout.</para>
    /// </summary>
    private static string EmailForCookieFile(string accountsFolder, string fileName)
    {
        foreach (var index in new[]
                 {
                     Path.Combine(accountsFolder, "accounts", "accounts.json"),
                     Path.Combine(accountsFolder, "accounts.json"),
                 })
        {
            if (!File.Exists(index)) continue;

            var email = EmailInIndex(index, fileName);
            if (email.Length > 0) return email;
        }

        return string.Empty;
    }

    /// <summary>The address one cookie file belongs to, according to one index file.</summary>
    private static string EmailInIndex(string index, string fileName)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(index, Encoding.UTF8));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return string.Empty;

            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                JsonElement value;
                if (!entry.TryGetProperty("cookies_file", out value) &&
                    !entry.TryGetProperty("cookiesFile", out value))
                {
                    continue;
                }

                if (!string.Equals(Path.GetFileName(value.GetString() ?? string.Empty), fileName,
                                   StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return entry.TryGetProperty("email", out var email) ? email.GetString() ?? "" : "";
            }
        }
        catch (Exception)                                      // noqa: BLE001
        {
            // An unreadable index simply yields no address for this file.
        }

        return string.Empty;
    }

    private static List<CookieRecord> Merge(List<CookieRecord> vault, List<CookieRecord> toolLayer)
    {
        if (toolLayer.Count == 0) return vault;

        var merged = new List<CookieRecord>(vault);
        foreach (var record in toolLayer)
        {
            if (merged.Any(c => SameMailbox(c.Email, record.Email))) continue;
            merged.Add(record);
        }

        return merged;
    }

    private static List<CookieRecord> Snapshot(string toolId)
    {
        lock (Gate) return Mutable(toolId).ToList();
    }

    /// <summary>
    /// True when two addresses are the SAME mailbox. Gmail ignores dots in the
    /// local part and everything from a "+", so the address a tool saved and the
    /// address a row shows can differ while naming one account.
    /// </summary>
    private static bool SameMailbox(string a, string b)
    {
        var na = NormaliseMailbox(a);
        return na.Length > 0 && string.Equals(na, NormaliseMailbox(b), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormaliseMailbox(string address)
    {
        var addr = (address ?? string.Empty).Trim().Trim('"');
        var at = addr.LastIndexOf('@');
        if (at <= 0) return addr.ToLowerInvariant();

        var local = addr[..at];
        var domain = addr[(at + 1)..].ToLowerInvariant();

        var plus = local.IndexOf('+');
        if (plus >= 0) local = local[..plus];

        if (domain is "gmail.com" or "googlemail.com")
        {
            local = local.Replace(".", string.Empty);
            domain = "gmail.com";
        }

        return local.ToLowerInvariant() + "@" + domain;
    }

    // ---------------------------------------------------------------- private

    private static List<CookieRecord> Mutable(string toolId)
    {
        if (Cache.TryGetValue(toolId, out var list)) return list;

        var workspace = AppPaths.CookiesFile(toolId);
        var legacy = AppPaths.LegacyCookiesFile(toolId);
        list = File.Exists(workspace)
            ? Json.Load(workspace, () => new List<CookieRecord>())
            : File.Exists(legacy)
                ? Json.Load(legacy, () => new List<CookieRecord>())
                : new List<CookieRecord>();
        Cache[toolId] = list;
        return list;
    }
}
