using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using AISuitePanel.Models;

namespace AISuitePanel;

/// <summary>
/// Activity log. Keeps a bounded in-memory tail for the tool panels plus an
/// append-only file per day. App-password shaped tokens are masked before they
/// reach either the UI or the file - secrets never get logged in clear.
/// </summary>
public static class Log
{
    private static readonly List<LogEntry> All = new();
    private static readonly object Gate = new();

    /// <summary>Raised on the calling thread - subscribers must marshal to the UI.</summary>
    public static event Action<LogEntry>? Appended;

    public static void Write(LogKind kind, string toolId, string message)
    {
        var entry = new LogEntry
        {
            Time = DateTime.Now.ToString("HH:mm:ss"),
            Kind = kind,
            Message = Redact(message),
            ToolId = toolId
        };

        lock (Gate)
        {
            All.Add(entry);
            if (All.Count > 500) All.RemoveRange(0, All.Count - 500);
        }

        WriteFile(entry);

        try { Appended?.Invoke(entry); } catch { /* a broken listener must not stop a run */ }
    }

    public static void Info(string toolId, string message) => Write(LogKind.Info, toolId, message);
    public static void Success(string toolId, string message) => Write(LogKind.Success, toolId, message);
    public static void Warn(string toolId, string message) => Write(LogKind.Warn, toolId, message);
    public static void Error(string toolId, string message) => Write(LogKind.Error, toolId, message);

    /// <summary>The in-memory tail, optionally filtered to one tool (for COPY LOG).</summary>
    public static string Text(string? toolId = null)
    {
        lock (Gate)
        {
            var sb = new StringBuilder();
            foreach (var e in All)
            {
                if (toolId is not null && !string.Equals(e.ToolId, toolId, StringComparison.OrdinalIgnoreCase))
                    continue;

                sb.Append(e.Time).Append(" [").Append(e.KindLabel).Append("] ").AppendLine(e.Message);
            }
            return sb.ToString();
        }
    }

    /// <summary>Masks things that must never be written down in clear.</summary>
    public static string Redact(string message)
    {
        if (string.IsNullOrEmpty(message)) return "";

        // Google app passwords: abcd-efgh-ijkl-mnop
        var masked = Regex.Replace(message, @"\b\w{4}-\w{4}-\w{4}-\w{4}\b", "••••-••••-••••-••••");

        // Anything that looks like an explicit secret assignment.
        masked = Regex.Replace(masked, @"(?i)\b(password|apppass|apikey|api key)\s*[:=]\s*\S+", "$1: ••••");

        return masked;
    }

    private static void WriteFile(LogEntry entry)
    {
        try
        {
            AppPaths.Ensure();
            var logPath = string.IsNullOrWhiteSpace(entry.ToolId)
                ? AppPaths.TodayLog
                : Path.Combine(AppPaths.WorkspaceLogsDir(entry.ToolId),
                    DateTime.Now.ToString("yyyy-MM-dd") + ".log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(
                logPath,
                $"{entry.Time} [{entry.KindLabel}] {entry.Message}{Environment.NewLine}",
                new UTF8Encoding(false));
        }
        catch
        {
            // logging must never be the reason a run fails
        }
    }
}
