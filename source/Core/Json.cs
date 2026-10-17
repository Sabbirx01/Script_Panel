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

    /// <summary>
    /// Why the last <see cref="Save{T}"/> failed, and for which path. Empty when
    /// it succeeded. Callers that must tell the user (Global Settings → Save) read
    /// this instead of guessing: a silent failure used to look exactly like a
    /// successful save, so the operator pressed Save, saw the settings stay
    /// unapplied, and had no way to find out why.
    /// </summary>
    public static string LastError { get; private set; } = "";

    public static string LastErrorPath { get; private set; } = "";

    /// <summary>Writes atomically. Returns false and records <see cref="LastError"/> on failure.</summary>
    public static bool Save<T>(string path, T value)
    {
        LastError = "";
        LastErrorPath = "";

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                AppPaths.Ensure();

                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options), new UTF8Encoding(false));

                // A scanner or a second instance can hold the target open for a
                // moment; retrying beats reporting a failure that would vanish.
                File.Move(tmp, path, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                LastErrorPath = path;

                if (attempt < 3)
                {
                    try { Thread.Sleep(80 * attempt); } catch { }
                }
            }
        }

        return false;
    }
}
