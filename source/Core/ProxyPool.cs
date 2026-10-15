using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AISuitePanel;

/// <summary>
/// The panel's proxy list: one plain-text file every tool can read, plus the two
/// jobs the operator needs from Global Settings - pull a fresh batch from GitHub,
/// and test the list so dead entries can be dropped.
///
/// The file is the contract with the tools (see <c>tools/_shared/proxy_pool.py</c>
/// and the drivers, which export <c>PANEL_PROXY_FILE</c>): lines of
/// <c>[scheme://][user:pass@]host:port</c>, blank lines and <c>#</c> comments
/// ignored, so it stays hand-editable.
/// </summary>
public static class ProxyPool
{
    /// <summary>Where the list lives - shared by every tool, inside the data root.</summary>
    public static string File => Path.Combine(AppPaths.Root, "proxies.txt");

    /// <summary>
    /// Public, plain-text lists.  Only HTTP(S) proxies are pulled: the tester
    /// (and .NET's client stack) can speak to an HTTP proxy, while SOCKS would
    /// report every entry dead and quietly empty the list.
    /// </summary>
    private static readonly string[] Sources =
    {
        "https://raw.githubusercontent.com/monosans/proxy-list/main/proxies/http.txt",
        "https://raw.githubusercontent.com/TheSpeedX/PROXY-List/master/http.txt",
    };

    /// <summary>How many entries a GitHub pull keeps (the raw lists are thousands long).</summary>
    public const int FetchLimit = 400;

    private const int TestTimeoutSeconds = 6;
    private const int TestParallel = 50;

    private static readonly Regex Shape = new(
        @"^(?:(?<user>[^:@\s]+):(?<pass>[^@\s]+)@)?(?<host>(?:\d{1,3}\.){3}\d{1,3}):(?<port>\d{1,5})$",
        RegexOptions.Compiled);

    // ------------------------------------------------------------------ parsing

    /// <summary>
    /// One line to <c>scheme://[user:pass@]host:port</c>, or "" when the line is
    /// not a proxy at all.  Everything downstream (file, tester, Playwright)
    /// works from this single shape.
    /// </summary>
    public static string Normalize(string? line)
    {
        var text = (line ?? "").Trim();
        if (text.Length == 0 || text.StartsWith('#')) return "";

        var scheme = "http";
        var at = text.IndexOf("://", StringComparison.Ordinal);
        if (at > 0)
        {
            var given = text[..at].ToLowerInvariant();
            if (given is not ("http" or "https" or "socks4" or "socks5")) return "";
            scheme = given;
            text = text[(at + 3)..];
        }

        var m = Shape.Match(text);
        if (!m.Success) return "";
        if (!int.TryParse(m.Groups["port"].Value, out var port) || port < 1 || port > 65535)
            return "";

        // Reject impossible octets: "999.1.1.1:80" reads like a proxy but can
        // never connect, and it would burn a slot in every test run.
        foreach (var octet in m.Groups["host"].Value.Split('.'))
            if (!byte.TryParse(octet, out _)) return "";

        var user = m.Groups["user"].Value;
        var pass = m.Groups["pass"].Value;
        var host = m.Groups["host"].Value;
        var cred = user.Length > 0 ? $"{user}:{pass}@" : "";
        return $"{scheme}://{cred}{host}:{port}";
    }

    /// <summary>The list as stored, cleaned and de-duplicated (order preserved).</summary>
    public static List<string> Load()
    {
        try
        {
            if (!System.IO.File.Exists(File)) return new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>();
            foreach (var raw in System.IO.File.ReadAllLines(File))
            {
                var one = Normalize(raw);
                if (one.Length > 0 && seen.Add(one)) list.Add(one);
            }
            return list;
        }
        catch
        {
            return new List<string>();
        }
    }

    public static void Save(IEnumerable<string> proxies)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var clean = new List<string>();
        foreach (var raw in proxies)
        {
            var one = Normalize(raw);
            if (one.Length > 0 && seen.Add(one)) clean.Add(one);
        }

        var dir = Path.GetDirectoryName(File);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        System.IO.File.WriteAllText(File, string.Join(Environment.NewLine, clean) + Environment.NewLine);
    }

    // -------------------------------------------------------------- github pull

    /// <summary>
    /// Downloads the public lists and returns the merged, de-duplicated result.
    /// A source that fails is simply skipped - one dead URL must not lose the
    /// other source's batch.
    /// </summary>
    public static async Task<List<string>> FetchFromGitHubAsync(CancellationToken ct = default)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<string>();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AISuitePanel/1.0");

        foreach (var url in Sources)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var body = await http.GetStringAsync(url, ct).ConfigureAwait(false);
                foreach (var raw in body.Split('\n'))
                {
                    var one = Normalize(raw);
                    if (one.Length > 0 && seen.Add(one)) merged.Add(one);
                }
            }
            catch
            {
                // offline / rate-limited / renamed file: keep whatever we already have
            }
        }

        return merged.Take(FetchLimit).ToList();
    }

    // ------------------------------------------------------------------- testing

    /// <summary>True when the proxy completes a request to a tiny, always-on endpoint.</summary>
    public static async Task<bool> IsAliveAsync(string proxy, CancellationToken ct = default)
    {
        var canonical = Normalize(proxy);
        if (canonical.Length == 0) return false;

        try
        {
            var wp = new WebProxy(canonical);
            using var handler = new HttpClientHandler
            {
                Proxy = wp,
                UseProxy = true,
                AllowAutoRedirect = false,
                UseCookies = false,
            };
            using var http = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(TestTimeoutSeconds),
            };
            using var resp = await http
                .GetAsync("http://www.gstatic.com/generate_204", ct)
                .ConfigureAwait(false);
            return (int)resp.StatusCode is 200 or 204;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Tests every entry in parallel and returns only the ones that answered, in
    /// the original order.  Any failure - refused, timed out, wrong credentials -
    /// counts as dead, because the tools would hit the same wall.
    /// </summary>
    public static async Task<List<string>> TestAsync(
        IReadOnlyList<string> proxies,
        IProgress<(int Done, int Total, int Alive)>? progress = null,
        CancellationToken ct = default)
    {
        var alive = new List<string>();
        if (proxies.Count == 0) return alive;

        var gate = new SemaphoreSlim(TestParallel);
        var done = 0;
        var ok = 0;
        var results = new bool[proxies.Count];

        var work = proxies.Select(async (p, i) =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                results[i] = await IsAliveAsync(p, ct).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
                var d = Interlocked.Increment(ref done);
                if (results[i]) Interlocked.Increment(ref ok);
                progress?.Report((d, proxies.Count, ok));
            }
        });

        await Task.WhenAll(work).ConfigureAwait(false);

        for (var i = 0; i < proxies.Count; i++)
            if (results[i]) alive.Add(proxies[i]);

        return alive;
    }
}
