using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using AISuitePanel.Models;

namespace AISuitePanel;

/// <summary>
/// One <c>.invalid</c> temp mailbox is enough to test that the panel-pipeline
/// reaches the right code path. Real services go through
/// <see cref="TempMailService"/>.
/// </summary>
public sealed record TempMailMessage(
    string Id,
    string From,
    string Subject,
    string Body,
    DateTime Date);

/// <summary>
/// Service catalog entry: name + endpoint + whether we need an API key.
/// </summary>
public sealed record TempMailProvider(
    string Name,
    string Display,
    string EndpointBase,
    bool RequiresApiKey);

public static class TempMailProviderCatalog
{
    /// <summary>
    /// Every Method-1 temp-mail service, in the order the dropdown shows them.
    ///
    /// <para><b>Order is load-bearing.</b> <see cref="MailRouter.ServiceNames"/> is a
    /// parallel array and <c>PickProvider</c> indexes straight into this list, so
    /// Guerrilla MUST stay first: an install that saved "ServiceIndex = 1" means
    /// Guerrilla, and moving it would silently repoint every existing setting.
    /// Append new services at the END only.</para>
    ///
    /// <para>Specs came from the Meta_EXE provider registry (Client Niloy /
    /// Final Product / Meta_EXE, source/core/mail.py), which probed these
    /// endpoints live on 2026-10-13. Ported 2026-10-22.</para>
    /// </summary>
    public static readonly TempMailProvider[] All =
    {
        // ── original five - never reorder ───────────────────────────────
        // Guerrilla Mail is the default — its public AJAX endpoint returns a
        // real address + a session token in one call, with no API key and no
        // captcha.
        new("guerrilla",   "Guerrilla Mail (free)",            "https://api.guerrillamail.com",                 false),
        new("1secmail",    "1secmail.com (free)",             "https://www.1secmail.com/api/v1/",               false),
        new("mailtm",      "mail.tm (signup required)",       "https://api.mail.tm",                            true),
        new("tempmailio",  "Temp-Mail.io (API key)",         "https://api.internal.temp-mail.io/api/v3",       true),
        new("inboxes",     "Inboxes.com demo",                "https://inboxes.com/api/v2",                     false),

        // ── added 2026-10-22, append-only ───────────────────────────────
        new("tempmailplus", "tempmail.plus (free, no key)",   "https://tempmail.plus/api",                      false),
        new("maildrop",     "maildrop.cc (free, no key)",     "https://api.maildrop.cc/graphql",                false),
        new("inboxkitten",  "inboxkitten.com (free)",         "https://inboxkitten.com/api/v1",                 false),
        new("emailnator",   "emailnator (real @gmail.com)",   "https://www.emailnator.com/api",                 false),
        new("tempmailing",  "tempmail.ing (30-min box)",      "https://api.tempmail.ing/api",                   false),
        new("byomde",       "byom.de (free, no key)",         "https://api.byom.de",                            false),
        new("restmailnet",  "restmail.net (free, no key)",    "https://restmail.net",                           false),
        new("mailforspam",  "mailforspam.com (free)",         "https://api.mailforspam.com/api",                false),
        new("duckmail",     "duckmail.sbs (mail.tm clone)",   "https://api.duckmail.sbs",                       false),
        new("getnada",      "getnada.net (free)",             "https://getnada.net/api",                        false),
        new("catchmail",    "catchmail.io (catch-all)",      "https://api.catchmail.io/api/v1",                false),
        new("tempmailorg",  "temp-mail.org (free, no key)",   "https://web2.temp-mail.org",                     false),
    };
}

/// <summary>
/// Live temp-mail service: talks to 1secmail.com's free, no-key API by
/// default.  Each tool panel can request a fresh mailbox, read its inbox,
/// and pull out the OTP that Z AI / Devin signups need.
///
/// 1secmail docs: https://www.1secmail.com/api/
///   GET ?action=domList                                → ["1secmail.com", ...]
///   GET ?action=genRandomMailbox&count=1               → ["abc@1secmail.com"]
///   GET ?action=getMessages&login=USER&domain=DOMAIN   → [{"id":...,"subject":...}]
///   GET ?action=readMessage&login=USER&domain=DOMAIN&id=ID → full body
///   GET ?action=deleteMailbox&login=USER&domain=DOMAIN  (optional cleanup)
/// </summary>
public sealed class TempMailService
{
    private readonly HttpClient _http;
    private readonly TempMailProvider _provider;
    private readonly string? _apiKey;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public TempMailService(TempMailProvider? provider = null, string? apiKey = null)
    {
        _provider = provider ?? TempMailProviderCatalog.All[0];
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();

        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        if (!string.IsNullOrEmpty(_apiKey))
            _http.DefaultRequestHeaders.Add("Authorization", "Bearer " + _apiKey);
    }

    public string ProviderName => _provider.Name;

    /// <summary>
    /// Fetch the up-to-date domain list from the service.  Falls back to the
    /// hard-coded known-good set on any error so the panel still issues real
    /// addresses when the service is briefly offline.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetDomainsAsync()
    {
        if (_provider.Name == "1secmail")
        {
            try
            {
                var url = _provider.EndpointBase + "?action=domList";
                var json = await _http.GetStringAsync(url).ConfigureAwait(false);
                var arr = JsonSerializer.Deserialize<string[]>(json, JsonOpts);
                if (arr != null && arr.Length > 0) return arr;
            }
            catch { /* fall through */ }
        }
        if (_provider.Name == "mailtm")
        {
            try
            {
                var url = _provider.EndpointBase + "/domains";
                var json = await _http.GetStringAsync(url).ConfigureAwait(false);
                var arr = JsonSerializer.Deserialize<MailTmDomainList>(json, JsonOpts);
                if (arr is not null && arr.HydraMember.Count > 0)
                    return arr.HydraMember.Select(d => d.Domain ?? "").Where(d => d.Length > 0).ToList();
            }
            catch { /* fall through */ }
        }

        // Hard-coded fall-back set per provider.
        return _provider.Name switch
        {
            "1secmail"   => new[] { "1secmail.com", "1secmail.net", "1secmail.org",
                                    "bgsaddam.com", "byesyes.com", "dedyn.io",
                                    "ezztt.com", "kfempm.com", "khtyler.com",
                                    "lettersbox.com", "loketa.com", "mailtothis.com",
                                    "poofy.org", "rockkes.com", "rover.info",
                                    "sharklasers.com", "spam4.me", "tafmail.com",
                                    "txtadvertise.com", "wuzup.net", "wuzupmail.net",
                                    "zoemail.org" },
            "mailtm"     => new[] { "uberip.com" },
            "guerrilla"  => new[] { "guerrillamailblock.com", "sharklasers.com",
                                    "guerrillamail.com", "guerrillamail.net",
                                    "guerrillamail.org", "guerrillamail.biz",
                                    "spam4.me" },
            "inboxes"    => new[] { "inboxes.com" },
            "tempmailio" => new[] { "tempmail.io", "tmpmail.io" },

            // Added 2026-10-22. Every one of these either serves a single fixed
            // domain (the client-side services) or publishes its own, so the
            // fallback is just what the service is known to hand out.
            "tempmailplus" => new[] { "mailto.plus" },
            "maildrop"     => new[] { "maildrop.cc" },
            "inboxkitten"  => new[] { "inboxkitten.com" },
            "emailnator"   => new[] { "gmail.com" },
            "tempmailing"  => new[] { "tempmail.ing" },
            "byomde"       => new[] { "byom.de" },
            "restmailnet"  => new[] { "restmail.net" },
            "mailforspam"  => new[] { "mailforspam.com" },
            "duckmail"     => new[] { "niceground.shop" },
            "getnada"      => new[] { "getnada.net", "getairmail.com", "getnada.com" },
            "catchmail"    => new[] { "catchmail.io" },
            "tempmailorg"  => new[] { "temp-mail.org" },

            _ => new[] { "guerrillamailblock.com" }
        };
    }

    /// <summary>
    /// The token / session id the service handed back with the mailbox, when it
    /// has one (Guerrilla's <c>sid_token</c>, mail.tm's JWT, tempmail.plus's
    /// <c>epin</c>, getnada / temp-mail.org's bearer …).
    ///
    /// <para>The panel persists this into <c>TempMail.CustomApiKey</c>, which the
    /// driver already hands to Python as <c>TEMP_MAIL_API_KEY</c> - the same slot
    /// Guerrilla's <c>sid_token</c> has always used. Without it a service that
    /// issues a token cannot be read back, so the OTP poll would sit on an empty
    /// inbox. Null for the client-side services, which need no token.</para>
    /// </summary>
    public string? LastToken { get; private set; }

    /// <summary>
    /// Generate a fresh random mailbox on the service.  Returns
    /// (local-part, domain) so the panel can show the address and the script
    /// can later read its inbox.
    ///
    /// <para>Every service mints its mailbox in a different way, and they are NOT
    /// interchangeable: six of them have no create endpoint at all (the address is
    /// made up client-side and the service accepts anything), the mail.tm family
    /// needs a three-call account + token dance, and the rest are a single JSON
    /// POST whose answer field name differs per service. This used to fall
    /// through to the 1secmail query for everything except Guerrilla, so mail.tm,
    /// Temp-Mail.io and Inboxes were all POSTing <c>?action=genRandomMailbox</c> to
    /// their own hosts and silently taking the <c>.invalid</c> offline fallback.</para>
    /// </summary>
    public async Task<(string Local, string Domain)?> GenerateMailboxAsync()
    {
        LastToken = null;

        switch (_provider.Name)
        {
            // ── Guerrilla: address and session token in one call ──────────
            case "guerrilla":
            {
                var url = _provider.EndpointBase + "/ajax.php?f=get_email_address";
                var json = await _http.GetStringAsync(url).ConfigureAwait(false);
                var raw = JsonSerializer.Deserialize<GuerrillaAddr>(json, JsonOpts);
                if (raw is null || string.IsNullOrWhiteSpace(raw.EmailAddr)) return null;
                LastToken = raw.SidToken;
                return SplitAddress(raw.EmailAddr);
            }

            // ── 1secmail: a JSON array holding one address ────────────────
            case "1secmail":
            {
                var j = await _http
                    .GetStringAsync(_provider.EndpointBase + "?action=genRandomMailbox&count=1")
                    .ConfigureAwait(false);
                var arr = JsonSerializer.Deserialize<string[]>(j, JsonOpts);
                if (arr is null || arr.Length == 0 || string.IsNullOrWhiteSpace(arr[0]))
                    return null;
                return SplitAddress(arr[0]);
            }

            // ── mail.tm family: duckmail.sbs is the same API on another host
            case "mailtm":
            case "duckmail":
                return await CreateMailTmFamilyAsync().ConfigureAwait(false);

            // ── no create endpoint: the service accepts any address ──────
            case "tempmailplus":
            case "maildrop":
            case "inboxkitten":
            case "byomde":
            case "restmailnet":
            case "mailforspam":
            case "catchmail":
                return ClientSideMailbox();

            // ── single JSON POST, per-service answer field ────────────────
            case "emailnator":
            {
                var raw = await PostRawAsync($"{_provider.EndpointBase}/generate-email",
                                             "{\"ids\":[3]}").ConfigureAwait(false);
                var addr = raw is null ? null : Pick(raw, "email");
                return addr is null ? null : SplitAddress(addr);
            }

            // tempmail.ing answers {"email":"a@b"} or {"email":{"address":"a@b"}}
            case "tempmailing":
            {
                var raw = await PostRawAsync($"{_provider.EndpointBase}/generate",
                                             "{\"duration\":30}").ConfigureAwait(false);
                if (raw is null) return null;
                var addr = Pick(raw, "email") ?? PickNested(raw, "email", "address");
                return addr is null ? null : SplitAddress(addr);
            }

            case "inboxes":
            {
                var raw = await PostRawAsync($"{_provider.EndpointBase}/inbox", "{}").ConfigureAwait(false);
                var addr = raw is null ? null : Pick(raw, "inbox", "email");
                return addr is null ? null : SplitAddress(addr);
            }

            // The address goes in the path RAW - the API 400s "Email not found"
            // when the @ is percent-encoded.
            case "tempmailio":
            {
                var raw = await PostRawAsync($"{_provider.EndpointBase}/email/new", null).ConfigureAwait(false);
                if (raw is null) return null;
                LastToken = Pick(raw, "token");
                var addr = Pick(raw, "email");
                return addr is null ? null : SplitAddress(addr);
            }

            // No body at all: sending "{}" is rejected outright.
            case "tempmailorg":
            {
                var raw = await PostRawAsync($"{_provider.EndpointBase}/mailbox", null,
                        ("Origin", "https://temp-mail.org"),
                        ("Referer", "https://temp-mail.org/")).ConfigureAwait(false);
                if (raw is null) return null;
                var addr = Pick(raw, "mailbox", "email");
                if (addr is null) return null;
                LastToken = Pick(raw, "token");
                return SplitAddress(addr);
            }

            case "getnada":
            {
                var domain = await GetNadaDomainAsync().ConfigureAwait(false);
                var address = "mp" + Rand(9) + "@" + domain;
                var body = JsonSerializer.Serialize(new { email = address });
                var raw = await PostRawAsync($"{_provider.EndpointBase}/inbox/open", body).ConfigureAwait(false);
                if (raw is null) return null;
                LastToken = Pick(raw, "token", "accessToken") ?? PickNested(raw, "data", "token");
                return string.IsNullOrWhiteSpace(LastToken) ? null : SplitAddress(address);
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// An address the service never had to be asked for: it accepts whatever
    /// arrives at its domain, so the panel mints one locally. The local part
    /// doubles as the mailbox name every later call identifies the box by.
    /// </summary>
    private (string Local, string Domain)? ClientSideMailbox()
    {
        var (domain, localLength) = _provider.Name switch
        {
            "tempmailplus" => ("mailto.plus", 10),
            "maildrop"     => ("maildrop.cc", 11),
            "inboxkitten"  => ("inboxkitten.com", 12),
            "byomde"       => ("byom.de", 10),
            "restmailnet"  => ("restmail.net", 10),
            "mailforspam"  => ("mailforspam.com", 10),
            "catchmail"    => ("catchmail.io", 10),
            _              => (null!, 0)
        };
        if (domain is null) return null;

        var local = "mp" + Rand(localLength);
        // tempmail.plus is the odd one out: it hands out no token, but every
        // later call must repeat a 4-digit PIN the CLIENT chose up front.
        LastToken = _provider.Name == "tempmailplus"
            ? Random.Shared.Next(1000, 10000).ToString()
            : local;
        return (local, domain);
    }

    /// <summary>
    /// The mail.tm three-call dance, shared with duckmail.sbs: read a domain,
    /// create the account, then exchange it for a bearer token.
    /// </summary>
    private async Task<(string Local, string Domain)?> CreateMailTmFamilyAsync()
    {
        var host = _provider.EndpointBase;

        // Accept BOTH shapes: the service negotiates by Accept header, so a bare
        // request answers {"hydra:member":[…]} and an explicit JSON Accept answers
        // a plain array. Reading only hydra:member crashed on the array form.
        var dj = await GetRawAsync($"{host}/domains?page=1").ConfigureAwait(false);
        string? domain = dj is null
            ? null
            : PickDomain(dj, "hydra:member");
        domain ??= _provider.Name == "duckmail" ? "niceground.shop" : "uberip.com";

        var address = "mp" + Rand(9) + "@" + domain;
        var password = "Mp!" + Rand(10) + "7x";

        var created = await PostRawAsync($"{host}/accounts",
            JsonSerializer.Serialize(new { address, password })).ConfigureAwait(false);
        if (created is null || Pick(created, "id") is null) return null;

        var tk = await PostRawAsync($"{host}/token",
            JsonSerializer.Serialize(new { address, password })).ConfigureAwait(false);
        LastToken = tk is null ? null : Pick(tk, "token");
        if (string.IsNullOrWhiteSpace(LastToken)) return null;

        return SplitAddress(address);
    }

    /// <summary>getnada hands out its own domain pool; fall back to its own.</summary>
    private async Task<string> GetNadaDomainAsync()
    {
        try
        {
            var dj = await _http.GetStringAsync($"{_provider.EndpointBase}/public/domains").ConfigureAwait(false);
            using var dd = JsonDocument.Parse(dj);
            if (dd.RootElement.TryGetProperty("domains", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in arr.EnumerateArray())
                {
                    var v = d.GetString();
                    if (!string.IsNullOrWhiteSpace(v)) return v;
                }
            }
        }
        catch { /* fall through */ }
        return "getnada.net";
    }

    /// <summary>GET, returning the raw body or null on any failure.</summary>
    private async Task<string?> GetRawAsync(string url)
    {
        try
        {
            using var resp = await _http.GetAsync(url).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        catch { return null; }
    }

    /// <summary>
    /// POST and return the raw body, or null on any failure. A null
    /// <paramref name="body"/> sends NO content at all, which some services
    /// require (temp-mail.org rejects a "{}" body outright) and which no
    /// <c>StringContent</c> default would do.
    /// </summary>
    private async Task<string?> PostRawAsync(string url, string? body,
                                             params (string Name, string Value)[] headers)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            if (body is not null)
                req.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            foreach (var (name, value) in headers)
                req.Headers.TryAddWithoutValidation(name, value);
            using var resp = await _http.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        catch { return null; }
    }

    /// <summary>First non-empty string among <paramref name="keys"/> in a JSON body.</summary>
    private static string? Pick(string json, params string[] keys)
    {
        try
        {
            using var d = JsonDocument.Parse(json);
            return FirstString(d.RootElement, keys);
        }
        catch { return null; }
    }

    /// <summary>A string at <c>outer.inner</c>, for services that nest the value.</summary>
    private static string? PickNested(string json, string outer, string inner)
    {
        try
        {
            using var d = JsonDocument.Parse(json);
            if (d.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!d.RootElement.TryGetProperty(outer, out var mid)) return null;
            return mid.ValueKind == JsonValueKind.Object &&
                   mid.TryGetProperty(inner, out var v) &&
                   v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// First usable "domain" from a list that is either the array itself or an
    /// object wrapping it (mail.tm answers both shapes depending on Accept).
    /// </summary>
    private static string? PickDomain(string json, string wrapper)
    {
        try
        {
            using var d = JsonDocument.Parse(json);
            var target = d.RootElement.ValueKind == JsonValueKind.Array
                ? d.RootElement
                : (d.RootElement.ValueKind == JsonValueKind.Object &&
                   d.RootElement.TryGetProperty(wrapper, out var w) ? w : default);
            if (target.ValueKind != JsonValueKind.Array) return null;
            foreach (var e in target.EnumerateArray())
                if (e.ValueKind == JsonValueKind.Object &&
                    e.TryGetProperty("domain", out var dom) &&
                    dom.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(dom.GetString()))
                    return dom.GetString();
            return null;
        }
        catch { return null; }
    }

    /// <summary>First non-empty string among <paramref name="names"/>.</summary>
    private static string? FirstString(JsonElement el, params string[] names)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        foreach (var n in names)
        {
            if (!el.TryGetProperty(n, out var v)) continue;
            if (v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
            else if (v.ValueKind == JsonValueKind.Object &&
                     v.TryGetProperty("address", out var a) &&
                     a.ValueKind == JsonValueKind.String &&
                     !string.IsNullOrWhiteSpace(a.GetString()))
                return a.GetString();
        }
        return null;
    }

    /// <summary>Lower-case letters and digits, the shape every provider accepts.</summary>
    private static string Rand(int n)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
        return string.Create(n, alphabet, static (span, set) =>
        {
            for (var i = 0; i < span.Length; i++)
                span[i] = set[Random.Shared.Next(set.Length)];
        });
    }

    /// <summary>
    /// Fetch the inbox for a mailbox.  Returns a list of message headers;
    /// call <see cref="ReadMessageAsync"/> for full body / OTP.
    /// </summary>
    public async Task<IReadOnlyList<TempMailMessage>> ListMessagesAsync(string local, string domain)
    {
        if (_provider.Name == "guerrilla")
        {
            // Guerrilla doesn't separate "local/domain" — the whole address is
            // <code>sid_token</code> is the session key.  Caller passes the
            // local part + domain; we synthesise the URL.
            var sid = string.IsNullOrEmpty(_apiKey) ? "" : _apiKey;
            var url = $"{_provider.EndpointBase}/ajax.php?f=get_email_list&offset=0&sid_token={Uri.EscapeDataString(sid)}";
            var json = await _http.GetStringAsync(url).ConfigureAwait(false);
            var raw = JsonSerializer.Deserialize<List<GuerrillaMsg>>(json, JsonOpts) ?? new();
            var out_ = new List<TempMailMessage>(raw.Count);
            foreach (var m in raw)
            {
                if (string.IsNullOrWhiteSpace(m.MailId)) continue;
                out_.Add(new TempMailMessage(
                    Id:      m.MailId,
                    From:    m.MailFrom ?? "",
                    Subject: m.MailSubject ?? "",
                    Body:    "",
                    Date:    DateTimeOffset.FromUnixTimeSeconds(m.MailTimestamp).UtcDateTime
                ));
            }
            return out_;
        }

        // 1secmail
        var u = $"{_provider.EndpointBase}?action=getMessages&login={Uri.EscapeDataString(local)}&domain={Uri.EscapeDataString(domain)}";
        var j = await _http.GetStringAsync(u).ConfigureAwait(false);

        var raw1 = JsonSerializer.Deserialize<List<RawHeader>>(j, JsonOpts) ?? new List<RawHeader>();
        var out1 = new List<TempMailMessage>(raw1.Count);
        foreach (var h in raw1)
        {
            if (string.IsNullOrWhiteSpace(h.Id)) continue;
            out1.Add(new TempMailMessage(
                Id:      h.Id,
                From:    h.From ?? "",
                Subject: h.Subject ?? "",
                Body:    "",
                Date:    DateTime.TryParse(h.Date, out var d) ? d : DateTime.Now
            ));
        }
        return out1;
    }

    /// <summary>
    /// Read the full body of one message.  Prefer text body (no HTML tags)
    /// since the script's IMAP-style regex looks for "verify" / "code" in
    /// plain text and a stray <code>&lt;a href="..."&gt;</code> would
    /// confuse the matcher.
    /// </summary>
    public async Task<TempMailMessage?> ReadMessageAsync(string local, string domain, string id)
    {
        if (_provider.Name == "guerrilla")
        {
            var sid = string.IsNullOrEmpty(_apiKey) ? "" : _apiKey;
            var url = $"{_provider.EndpointBase}/ajax.php?f=fetch_message&email_id={Uri.EscapeDataString(id)}&sid_token={Uri.EscapeDataString(sid)}";
            var json = await _http.GetStringAsync(url).ConfigureAwait(false);
            var raw = JsonSerializer.Deserialize<GuerrillaBody>(json, JsonOpts);
            if (raw is null) return null;
            var mailBody = !string.IsNullOrWhiteSpace(raw.MailBody) ? StripHtml(raw.MailBody) : "";
            return new TempMailMessage(
                Id:      raw.MailId ?? id,
                From:    raw.MailFrom ?? "",
                Subject: raw.MailSubject ?? "",
                Body:    mailBody,
                Date:    DateTimeOffset.FromUnixTimeSeconds(raw.MailTimestamp).UtcDateTime
            );
        }

        // 1secmail
        var u = $"{_provider.EndpointBase}?action=readMessage&login={Uri.EscapeDataString(local)}&domain={Uri.EscapeDataString(domain)}&id={Uri.EscapeDataString(id)}";
        var j = await _http.GetStringAsync(u).ConfigureAwait(false);

        var raw1 = JsonSerializer.Deserialize<RawBody>(j, JsonOpts);
        if (raw1 is null) return null;
        var body = !string.IsNullOrWhiteSpace(raw1.TextBody) ? raw1.TextBody :
                   !string.IsNullOrWhiteSpace(raw1.Body)     ? StripHtml(raw1.Body) : "";
        return new TempMailMessage(
            Id:      raw1.Id ?? id,
            From:    raw1.From ?? "",
            Subject: raw1.Subject ?? "",
            Body:    body,
            Date:    DateTime.TryParse(raw1.Date, out var d) ? d : DateTime.Now
        );
    }

    private static (string Local, string Domain)? SplitAddress(string address)
    {
        var at = address.IndexOf('@');
        if (at <= 0) return null;
        return (address[..at], address[(at + 1)..]);
    }

    // ───────────────────────────────────────────── helpers

    private static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var sb = new System.Text.StringBuilder(html.Length);
        var inside = false;
        foreach (var c in html)
        {
            if (c == '<') { inside = true; sb.Append(' '); continue; }
            if (c == '>') { inside = false; sb.Append(' '); continue; }
            if (!inside) sb.Append(c);
        }
        return System.Text.RegularExpressions.Regex.Replace(
            sb.ToString(), @"\s+", " ").Trim();
    }

    private sealed class RawHeader
    {
        [JsonPropertyName("id")]      public string? Id      { get; set; }
        [JsonPropertyName("from")]    public string? From    { get; set; }
        [JsonPropertyName("subject")] public string? Subject { get; set; }
        [JsonPropertyName("date")]    public string? Date    { get; set; }
    }

    private sealed class RawBody
    {
        [JsonPropertyName("id")]        public string? Id        { get; set; }
        [JsonPropertyName("from")]      public string? From      { get; set; }
        [JsonPropertyName("subject")]   public string? Subject   { get; set; }
        [JsonPropertyName("date")]      public string? Date      { get; set; }
        [JsonPropertyName("body")]      public string? Body      { get; set; }
        [JsonPropertyName("textBody")]  public string? TextBody  { get; set; }
        [JsonPropertyName("htmlBody")]  public string? HtmlBody  { get; set; }
    }

    // ───────────────────────────────────────────── Guerrilla Mail DTOs

    private sealed class GuerrillaAddr
    {
        [JsonPropertyName("email_addr")]    public string? EmailAddr    { get; set; }
        [JsonPropertyName("sid_token")]     public string? SidToken     { get; set; }
        [JsonPropertyName("alias")]         public string? Alias        { get; set; }
        [JsonPropertyName("email_timestamp")] public long   EmailTs       { get; set; }
    }

    private sealed class GuerrillaMsg
    {
        [JsonPropertyName("mail_id")]      public string? MailId      { get; set; }
        [JsonPropertyName("mail_from")]    public string? MailFrom    { get; set; }
        [JsonPropertyName("mail_subject")] public string? MailSubject { get; set; }
        [JsonPropertyName("mail_timestamp")] public long   MailTimestamp { get; set; }
        [JsonPropertyName("mail_read")]    public int     MailRead    { get; set; }
        [JsonPropertyName("mail_body")]     public string? MailBody    { get; set; }
    }

    private sealed class GuerrillaBody
    {
        [JsonPropertyName("mail_id")]      public string? MailId      { get; set; }
        [JsonPropertyName("mail_from")]    public string? MailFrom    { get; set; }
        [JsonPropertyName("mail_subject")] public string? MailSubject { get; set; }
        [JsonPropertyName("mail_timestamp")] public long   MailTimestamp { get; set; }
        [JsonPropertyName("mail_body")]     public string? MailBody    { get; set; }
    }

    // ───────────────────────────────────────────── mail.tm DTOs

    private sealed class MailTmDomain
    {
        [JsonPropertyName("id")]     public string? Id     { get; set; }
        [JsonPropertyName("domain")] public string? Domain { get; set; }
    }

    private sealed class MailTmDomainList
    {
        [JsonPropertyName("hydra:member")] public List<MailTmDomain> HydraMember { get; set; } = new();
    }
}
