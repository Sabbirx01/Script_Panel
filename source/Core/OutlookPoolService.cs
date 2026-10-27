using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AISuitePanel.Models;

namespace AISuitePanel;

/// <summary>One row of the Outlook token pool.</summary>
public sealed record OutlookAccount(string Email, string Password, string Refresh, string Client);

/// <summary>
/// The Outlook token pool - the mail side of Method 5.
///
/// <para>Rows look exactly like the Audible panel's
/// (<c>email|password|refresh_token|client_id</c>), and the OTP is read from the
/// real mailbox: the refresh token is exchanged for an access token, and the
/// tool reads the code over IMAP / Graph. Nothing here types a guessed code -
/// the panel only proves a token is alive; the tool's script waits for the mail
/// that actually arrives.</para>
/// </summary>
public static class OutlookPoolService
{
    public const string DefaultFileName = "accounts_outlook_panel.txt";

    private const string TokenUrl = "https://login.microsoftonline.com/{0}/oauth2/v2.0/token";
    private static readonly string[] Tenants = { "consumers", "common" };

    public const string ImapScope = "https://outlook.office.com/IMAP.AccessAsUser.All offline_access";
    public const string GraphScope = "https://graph.microsoft.com/Mail.Read offline_access";

    private static readonly Regex ClientRe = new(
        "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
        RegexOptions.Compiled);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };

    /// <summary>Where the pool lives. A configured path wins over the default.</summary>
    public static string PoolPath
    {
        get
        {
            var configured = (SettingsStore.Current.OutlookPool?.FilePath ?? "").Trim();
            if (configured.Length > 0) return AppPaths.Resolve(configured);

            // AccountsRoot (the payload folder next to the tools), NOT AccountsDir
            // (the roaming data folder): the pool has to sit where the Python
            // side looks for it - tools/_shared/outlook_pool.py resolves the
            // same folder from app-root.txt.
            return Path.Combine(AppPaths.AccountsRoot, DefaultFileName);
        }
    }

    // ------------------------------------------------------------------ parsing

    /// <summary>One <c>email|password|refresh|client_id</c> row, or null.</summary>
    public static OutlookAccount? ParseLine(string? line)
    {
        var raw = (line ?? "").Trim();
        if (raw.Length == 0 || raw.StartsWith("#")) return null;

        var parts = raw.Replace(',', '|').Split('|').Select(p => p.Trim()).ToList();
        if (parts.Count < 4)
            parts = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count < 4) return null;

        var email = parts[0];
        var password = parts[1];
        var refresh = parts[2];
        var client = parts[3];

        if (email.Length == 0 || !email.Contains('@')) return null;
        if (!ClientRe.IsMatch(client)) return null;
        if (refresh.Length < 20) return null;
        return new OutlookAccount(email, password, refresh, client);
    }

    /// <summary>A pasted block (newlines or spaces) -> accounts + rejected chunks.</summary>
    public static (List<OutlookAccount> Accounts, List<string> Bad) ParseText(string? text)
    {
        var accounts = new List<OutlookAccount>();
        var bad = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var chunk in (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var acct = ParseLine(chunk);
            if (acct is null)
            {
                if (chunk.Trim().Length > 0) bad.Add(chunk);
                continue;
            }
            if (!seen.Add(acct.Email)) continue;
            accounts.Add(acct);
        }
        return (accounts, bad);
    }

    /// <summary>Every mailbox in the pool, de-duplicated by address.</summary>
    public static List<OutlookAccount> Load()
    {
        try
        {
            var path = PoolPath;
            if (!File.Exists(path)) return new List<OutlookAccount>();
            var (accounts, _bad) = ParseText(File.ReadAllText(path, Encoding.UTF8));
            return accounts;
        }
        catch (Exception ex)
        {
            Log.Info("mail", $"  cannot read the Outlook pool: {ex.Message}");
            return new List<OutlookAccount>();
        }
    }

    /// <summary>Rewrite the pool file, one canonical line per unique address.</summary>
    public static int Save(IEnumerable<OutlookAccount> accounts)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = new List<string>();
        foreach (var acct in accounts)
        {
            if (!seen.Add(acct.Email)) continue;
            lines.Add($"{acct.Email}|{acct.Password}|{acct.Refresh}|{acct.Client}");
        }

        var path = PoolPath;
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(folder)) Directory.CreateDirectory(folder);

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, string.Concat(lines.Select(l => l + "\n")), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
        return lines.Count;
    }

    /// <summary>Merge a pasted block into the pool. Duplicates are skipped.</summary>
    public static (int Added, int Duplicates, int Bad, int Total) Import(string? text)
    {
        var (incoming, bad) = ParseText(text);
        var existing = Load();
        var have = new HashSet<string>(existing.Select(a => a.Email), StringComparer.OrdinalIgnoreCase);

        var fresh = incoming.Where(a => !have.Contains(a.Email)).ToList();
        var merged = existing.Concat(fresh).ToList();
        var total = Save(merged);

        Log.Info("mail", $"  pool import: +{fresh.Count} · {incoming.Count - fresh.Count} duplicate(s) · " +
                         $"{bad.Count} malformed · {total} total");
        return (fresh.Count, incoming.Count - fresh.Count, bad.Count, total);
    }

    public static int RemoveDead(IEnumerable<string> deadAddresses)
    {
        var dead = new HashSet<string>(deadAddresses, StringComparer.OrdinalIgnoreCase);
        var keep = Load().Where(a => !dead.Contains(a.Email)).ToList();
        return Save(keep);
    }

    // -------------------------------------------------------------------- token

    /// <summary>
    /// Exchange a refresh token for an access token. Returns "" when Microsoft
    /// refuses it (the caller then treats the row as dead).
    /// </summary>
    public static async Task<string> AccessTokenAsync(OutlookAccount acct, string scope,
                                                      CancellationToken ct = default)
    {
        foreach (var tenant in Tenants)
        {
            try
            {
                var body = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = acct.Client,
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = acct.Refresh,
                    ["scope"] = scope
                });
                using var resp = await Http.PostAsync(string.Format(TokenUrl, tenant), body, ct)
                                           .ConfigureAwait(false);
                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) continue;

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("access_token", out var tok))
                {
                    var value = tok.GetString() ?? "";
                    if (value.Length > 0) return value;
                }
            }
            catch (Exception ex)
            {
                Log.Info("mail", $"  token error ({tenant}): {ex.GetType().Name} {ex.Message}");
            }
        }
        return "";
    }

    /// <summary>Is this row alive? A token exchange is enough - no IMAP needed.</summary>
    public static async Task<(bool Ok, string Reason)> CheckAsync(OutlookAccount acct,
                                                                 CancellationToken ct = default)
    {
        var token = await AccessTokenAsync(acct, ImapScope, ct).ConfigureAwait(false);
        if (token.Length > 0) return (true, "imap scope ok");

        token = await AccessTokenAsync(acct, GraphScope, ct).ConfigureAwait(false);
        if (token.Length > 0) return (true, "graph scope ok");

        return (false, "token refused");
    }

    // ------------------------------------------------------------------ rotation

    /// <summary>
    /// Next address for a run: round-robin over the pool, skipping anything the
    /// account store already holds so two workers never share one mailbox.
    /// </summary>
    public static OutlookAccount? Next(string toolId)
    {
        var pool = Load();
        if (pool.Count == 0) return null;

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var rec in AccountStore.For(toolId))
                if (!string.IsNullOrWhiteSpace(rec.EmailUsed)) used.Add(rec.EmailUsed);
        }
        catch
        {
            // A read failure must not stop the run - fall back to pure rotation.
        }

        var cursor = SettingsStore.Current.OutlookPool.Cursor;
        for (var i = 0; i < pool.Count; i++)
        {
            var idx = (cursor + i) % pool.Count;
            if (used.Contains(pool[idx].Email)) continue;

            SettingsStore.Current.OutlookPool.Cursor = (idx + 1) % pool.Count;
            try { SettingsStore.Save(SettingsStore.Current); } catch { /* cursor is only a hint */ }
            return pool[idx];
        }

        SettingsStore.Current.OutlookPool.Cursor = (cursor + 1) % pool.Count;
        return pool[cursor % pool.Count];
    }

    /// <summary>"49 mailbox(es) · outlook.fr: 49" for the settings card.</summary>
    public static string Summary()
    {
        var pool = Load();
        if (pool.Count == 0) return "no mailbox in the pool";
        var byDomain = pool.GroupBy(a => a.Email.Split('@').Last().ToLowerInvariant())
                           .OrderBy(g => g.Key)
                           .Select(g => $"{g.Key}: {g.Count()}");
        return $"{pool.Count} mailbox(es) · {string.Join(" · ", byDomain)}";
    }
}
