namespace AISuitePanel.Models;

public enum LogKind
{
    Info,
    Success,
    Warn,
    Error
}

/// <summary>One line in the tool activity log.</summary>
public sealed class LogEntry
{
    public string Time { get; init; } = "";
    public LogKind Kind { get; init; } = LogKind.Info;
    public string Message { get; init; } = "";
    public string ToolId { get; init; } = "";

    public string KindLabel => Kind.ToString().ToUpperInvariant();

    public string KindColor => Kind switch
    {
        LogKind.Success => "#FF34D399",
        LogKind.Warn => "#FFFBBF24",
        LogKind.Error => "#FFF87171",
        _ => "#FF93A3C2"
    };
}
