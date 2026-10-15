using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AISuitePanel;

/// <summary>
/// Shared JSON read/write. Writes are atomic (temp file + move) so a crash half
/// way through never leaves a truncated accounts or cookies file behind.
/// </summary>
internal static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static T Load<T>(string path, Func<T> fallback)
    {
        try
        {
            if (!File.Exists(path)) return fallback();

            var text = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text)) return fallback();

            return JsonSerializer.Deserialize<T>(text, Options) ?? fallback();
        }
        catch
        {
            // A damaged file is set aside (kept as .bak) instead of taking the
            // panel down with it - starting empty beats not starting at all.
            try { File.Move(path, path + ".bak", overwrite: true); } catch { /* ignore */ }
            return fallback();
        }
    }

    public static void Save<T>(string path, T value)
    {
        try
        {
            AppPaths.Ensure();

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options), new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // Persistence is best-effort; the session keeps working regardless.
        }
    }
}
