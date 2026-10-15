using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AISuitePanel;

/// <summary>
/// Minimal read-only IMAP client behind the "Test" buttons in Global Settings.
///
/// It answers one question the operator cannot otherwise answer without reading
/// the tool logs: <em>does this address + app password actually let the panel
/// read the mail?</em>  A Gmail app password is displayed as four groups of four
/// letters - <c>abcd efgh ijkl mnop</c> - while IMAP wants the 16 bare
/// characters, so a rejected login used to look exactly like "no code has
/// arrived yet", and every custom-domain run simply timed out.
///
/// Only LOGIN, SELECT, STATUS and a single header FETCH are ever sent, and every
/// body access uses BODY.PEEK, so nothing is downloaded destructively and no
/// message is ever flagged as read.
/// </summary>
public static class MailboxProbe
{
    private const int TimeoutMs = 20000;

    /// <summary>The outcome of one probe, ready to show in the UI.</summary>
    public sealed record Result(
        bool Ok,
        string Summary,
        string Detail,
        int Total,
        int Unseen,
        string Newest);

    /// <summary>Gmail uses imap.gmail.com; anything else is assumed self-hosted.</summary>
    public static string ImapHostFor(string address)
    {
        var at = (address ?? "").LastIndexOf('@');
        var domain = at >= 0 ? address![(at + 1)..].Trim().ToLowerInvariant() : "";
        if (domain is "gmail.com" or "googlemail.com") return "imap.gmail.com";
        return domain.Length == 0 ? "imap.gmail.com" : "imap." + domain;
    }

    public static Task<Result> TestAsync(string address, string password,
                                         CancellationToken ct = default)
        => Task.Run(() => Test(address, password, ct), ct);

    public static Result Test(string address, string password, CancellationToken ct = default)
    {
        var user = (address ?? "").Trim();
        // 16 bare characters - the same normalisation the tools get.
        var pass = SettingsStore.NormalizeAppPassword(password);

        if (user.Length == 0 || !user.Contains('@'))
            return new Result(false, "Enter the forward-target address first.", "", 0, 0, "");
        if (pass.Length == 0)
            return new Result(false, "Enter the target app password first.", "", 0, 0, "");
        if (pass.Length != 16)
            return new Result(false,
                $"That app password is {pass.Length} characters after removing spaces - "
                + "Google's are 16. Re-copy it from the Google Account page.",
                "", 0, 0, "");

        var host = ImapHostFor(user);
        var trace = new StringBuilder();

        try
        {
            ct.ThrowIfCancellationRequested();

            using var client = new TcpClient();
            var connect = client.BeginConnect(host, 993, null, null);
            if (!connect.AsyncWaitHandle.WaitOne(TimeoutMs, false))
                return Fail($"Could not reach {host}:993 - the connection timed out.", trace);
            client.EndConnect(connect);

            using var raw = client.GetStream();
            using var ssl = new SslStream(raw, false, (_, _, _, _) => true);
            ssl.ReadTimeout = TimeoutMs;
            ssl.WriteTimeout = TimeoutMs;
            ssl.AuthenticateAsClient(host);

            using var imap = new Session(ssl);
            trace.AppendLine("S: " + imap.Greeting);

            // ---- LOGIN ----------------------------------------------------
            var login = imap.Command("A1", $"LOGIN {Quote(user)} {Quote(pass)}");
            trace.AppendLine($"C: A1 LOGIN {Quote(user)} ********");
            trace.AppendLine("S: " + login.Status);
            if (!login.Ok)
            {
                var refused = login.Status.Contains("AUTHENTICATIONFAILED",
                                                    StringComparison.OrdinalIgnoreCase)
                              || login.Status.Contains("Invalid credentials",
                                                       StringComparison.OrdinalIgnoreCase);
                return Fail(refused
                    ? $"{user} refused the password. Make sure it is a 16-character app "
                      + "password (not the account password) and that IMAP is enabled."
                    : "Login failed - " + login.Status,
                    trace);
            }

            // ---- SELECT INBOX ---------------------------------------------
            var select = imap.Command("A2", "SELECT \"INBOX\"");
            trace.AppendLine("C: A2 SELECT \"INBOX\"");
            trace.AppendLine("S: " + select.Status);
            if (!select.Ok)
                return Fail("Logged in, but INBOX could not be opened - " + select.Status, trace);

            var total = imap.CountFrom("* ", " EXISTS");
            var unseen = 0;
            var status = imap.Command("A3", "STATUS \"INBOX\" (MESSAGES UNSEEN)");
            trace.AppendLine("C: A3 STATUS \"INBOX\" (MESSAGES UNSEEN)");
            trace.AppendLine("S: " + status.Status);
            if (status.Ok)
            {
                var untagged = imap.Untagged;
                var m = Regex.Match(untagged, @"MESSAGES\s+(\d+)", RegexOptions.IgnoreCase);
                if (m.Success) total = int.Parse(m.Groups[1].Value);
                m = Regex.Match(untagged, @"UNSEEN\s+(\d+)", RegexOptions.IgnoreCase);
                if (m.Success) unseen = int.Parse(m.Groups[1].Value);
            }

            // ---- newest message (headers only) ----------------------------
            var newest = "";
            if (total > 0)
            {
                var header = new StringBuilder();
                var fetch = imap.Command($"A4",
                    $"FETCH {total} (BODY.PEEK[HEADER.FIELDS (DATE FROM SUBJECT)])", header);
                trace.AppendLine($"C: A4 FETCH {total} (BODY.PEEK[HEADER.FIELDS (DATE FROM SUBJECT)])");
                if (fetch.Ok && header.Length > 0)
                    newest = Summarise(header.ToString());
            }

            imap.Command("A5", "LOGOUT");

            var summary = $"Login OK - {user} opened INBOX ({total} message"
                          + (total == 1 ? "" : "s") + $", {unseen} unread).";
            return new Result(true, summary, trace.ToString(), total, unseen, newest);
        }
        catch (OperationCanceledException)
        {
            return Fail("Cancelled.", trace);
        }
        catch (Exception ex)
        {
            return Fail($"{ex.GetType().Name}: {ex.Message}", trace);
        }
    }

    private static Result Fail(string why, StringBuilder trace)
        => new(false, why, trace.ToString(), 0, 0, "");

    /// <summary>"Fri, 08 Oct 2026 08:33:45 +0000 · Devin &lt;no-reply@cognition.ai&gt; · Your Devin Login Code"</summary>
    private static string Summarise(string headerBlock)
    {
        string Value(string name)
        {
            var m = Regex.Match(headerBlock, @"^" + name + @":\s*(.+)$",
                                RegexOptions.Multiline | RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }
        var parts = new List<string>();
        foreach (var name in new[] { "Date", "From", "Subject" })
        {
            var v = Value(name);
            if (v.Length > 0) parts.Add(v.Length > 90 ? v[..90] + "..." : v);
        }
        return string.Join("  ·  ", parts);
    }

    private static string Quote(string value)
        => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>One TLS-wrapped IMAP conversation. Not thread-safe; use one per probe.</summary>
    private sealed class Session : IDisposable
    {
        private readonly Stream _stream;
        private readonly byte[] _byte = new byte[1];
        private readonly Dictionary<string, string> _untagged = new(StringComparer.Ordinal);

        public Session(Stream stream)
        {
            _stream = stream;
            Greeting = ReadLine();
        }

        public string Greeting { get; }

        /// <summary>Everything the server volunteered during the last command.</summary>
        public string Untagged => string.Join("\n", _untagged.Values);

        public (bool Ok, string Status) Command(string tag, string body,
                                                StringBuilder? literalSink = null)
        {
            Write(tag + " " + body + "\r\n");
            while (true)
            {
                var line = ReadLine();
                if (line.StartsWith(tag + " ", StringComparison.Ordinal))
                {
                    // "A1 OK ..." / "A1 NO ..." / "A1 BAD ..."
                    var rest = line[(tag.Length + 1)..].TrimStart();
                    var ok = rest.StartsWith("OK", StringComparison.OrdinalIgnoreCase);
                    return (ok, line);
                }
                var exists = Regex.Match(line, @"^\*\s+(\d+)\s+EXISTS\s*$", RegexOptions.IgnoreCase);
                if (exists.Success) _untagged["EXISTS"] = exists.Groups[1].Value;

                var len = LiteralLength(line);
                if (len > 0)
                {
                    var payload = ReadExactly(len);
                    literalSink?.Append(payload);
                }
            }
        }

        /// <summary>Reads a value announced by the last command, e.g. "* 12 EXISTS".</summary>
        public int CountFrom(string prefix, string suffix)
        {
            foreach (var line in _untagged.Values)
            {
                var t = line.TrimEnd();
                if (t.StartsWith(prefix, StringComparison.Ordinal)
                    && t.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    var digits = Regex.Match(t, @"\d+");
                    if (digits.Success) return int.Parse(digits.Value);
                }
            }
            if (suffix.Trim().Equals("EXISTS", StringComparison.OrdinalIgnoreCase)
                && _untagged.TryGetValue("EXISTS", out var v))
                return int.Parse(v);
            return 0;
        }

        private void Write(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            _stream.Write(bytes, 0, bytes.Length);
            _stream.Flush();
        }

        private string ReadLine()
        {
            var sb = new StringBuilder();
            while (true)
            {
                int n = _stream.Read(_byte, 0, 1);
                if (n <= 0) return sb.ToString();
                if (_byte[0] == (byte)'\n')
                {
                    if (sb.Length > 0 && sb[^1] == '\r') sb.Length--;
                    return sb.ToString();
                }
                sb.Append((char)_byte[0]);
            }
        }

        private string ReadExactly(int count)
        {
            var buffer = new byte[count];
            var got = 0;
            while (got < count)
            {
                var n = _stream.Read(buffer, got, count - got);
                if (n <= 0) break;
                got += n;
            }
            return Encoding.UTF8.GetString(buffer, 0, got);
        }

        /// <summary>The byte count in a trailing "{123}"; 0 when the line is plain.</summary>
        private static int LiteralLength(string line)
        {
            var m = Regex.Match(line, @"\{(\d+)\}\s*$");
            return m.Success ? int.Parse(m.Groups[1].Value) : 0;
        }

        public void Dispose()
        {
            try { _stream.Dispose(); } catch { /* nothing useful to do */ }
        }
    }
}
