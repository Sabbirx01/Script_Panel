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
    public static readonly TempMailProvider[] All =
    {
        // Guerrilla Mail is the default — its public AJAX endpoint returns a
        // real address + a session token in one call, with no API key and no
        // captcha. 1secmail and mail.tm are reliable too but require key or
        // session bookkeeping the panel hasn't wired yet.
        new("guerrilla",   "Guerrilla Mail (free, no key)", "https://api.guerrillamail.com",   false),
        new("1secmail",    "1secmail.com (free, no key)",  "https://www.1secmail.com/api/v1/", false),
        new("mailtm",      "mail.tm (requires signup)",    "https://api.mail.tm",              true),
        new("tempmailio",  "Temp-Mail.io (requires key)",  "https://api.internal.temp-mail.io/v1/", true),
        new("inboxes",     "Inboxes.com demo",             "https://inboxes.com/api/v1/",     false),
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
            _ => new[] { "guerrillamailblock.com" }
        };
    }

    /// <summary>
    /// Generate a fresh random mailbox on the service.  Returns
    /// (local-part, domain) so the panel can show the address and the script
    /// can later read its inbox.
    /// </summary>
    public async Task<(string Local, string Domain)?> GenerateMailboxAsync()
    {
        if (_provider.Name == "guerrilla")
        {
            // Guerrilla's AJAX returns one mailbox per session; the panel keeps
            // the sid_token in SettingsStore.Current.TempMail.CustomApiKey so a
            // follow-up ListMessages call reuses the same mailbox.
            var url = _provider.EndpointBase + "/ajax.php?f=get_email_address";
            var json = await _http.GetStringAsync(url).ConfigureAwait(false);
            var raw = JsonSerializer.Deserialize<GuerrillaAddr>(json, JsonOpts);
            if (raw is null || string.IsNullOrWhiteSpace(raw.EmailAddr)) return null;
            return SplitAddress(raw.EmailAddr);
        }

        // 1secmail
        var u = _provider.EndpointBase + "?action=genRandomMailbox&count=1";
        var j = await _http.GetStringAsync(u).ConfigureAwait(false);

        // Response is a JSON array of strings: ["abc@1secmail.com"]
        var arr = JsonSerializer.Deserialize<string[]>(j, JsonOpts);
        if (arr is null || arr.Length == 0 || string.IsNullOrWhiteSpace(arr[0]))
            return null;
        return SplitAddress(arr[0]);
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
