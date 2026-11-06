using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace AISuitePanel;

/// <summary>
/// Repairs an install that was created from the update ZIP instead of the
/// one-file Setup.
///
/// <para><b>Why this exists.</b> The download page used to hand out the update
/// package, which carries the panel exe and .playwright and nothing else. A
/// panel installed from it opens normally and then fails every single run with
/// "Create script not found", because <c>tools\</c> and <c>runtime\</c> were
/// never there. Telling the customer to go and find Setup.exe assumes they
/// will read a log, believe it, and finish the job - so the panel now fixes
/// itself: it downloads the published Setup.exe, verifies its SHA-256, and
/// hands over to it.</para>
///
/// <para>The self-updater cannot do this job. It replaces one file; a complete
/// install is ~600 MB of tools and a Python runtime, and the release publishes
/// the encrypted setup precisely because that is the only artefact that carries
/// them.</para>
/// </summary>
internal static class SelfRepair
{
    /// <summary>Title + detail for the dashboard banner, marshalled by the view.</summary>
    public static event Action<string, string>? Status;

    private static bool _running;

    /// <summary>
    /// True when this copy cannot possibly run a tool: the archive it came from
    /// had no <c>tools\</c> folder, or no Python runtime to run them with.
    /// </summary>
    public static bool NeedsRepair =>
        !Directory.Exists(Path.Combine(AppPaths.AppRoot, "tools")) ||
        !File.Exists(Path.Combine(AppPaths.AppRoot, "runtime", "python", "python.exe"));

    private static string MarkerFile => Path.Combine(AppPaths.Root, "repair-attempt.json");

    private sealed class Marker
    {
        public string At { get; set; } = "";
        public string Version { get; set; } = "";
        public string Result { get; set; } = "";
    }

    /// <summary>
    /// A repair that already failed today must not run again on every start -
    /// a 600 MB download in a loop is worse than the bug. The customer still
    /// has the download page and the message in the run log.
    /// </summary>
    private static bool AttemptedRecently()
    {
        try
        {
            if (!File.Exists(MarkerFile)) return false;
            var marker = JsonSerializer.Deserialize<Marker>(File.ReadAllText(MarkerFile));
            if (marker is null || !DateTime.TryParse(marker.At, out var at)) return false;
            return DateTime.Now - at < TimeSpan.FromHours(12);
        }
        catch
        {
            return false;
        }
    }

    private static void WriteMarker(string version, string result)
    {
        try
        {
            Json.Save(MarkerFile, new Marker
            {
                At = DateTime.Now.ToString("o"),
                Version = version,
                Result = result
            });
        }
        catch
        {
            // never fail a repair over bookkeeping
        }
    }

    /// <summary>
    /// Runs once, quietly, after the window is up. Returns when there is
    /// nothing to do, when the download failed, or after the installer has been
    /// started (the caller then shuts the panel down so the files can be
    /// replaced).
    /// </summary>
    public static async Task RunAsync(CancellationToken ct = default)
    {
        if (_running) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(6), ct).ConfigureAwait(false);
            if (!LicenseService.IsActivated || !NeedsRepair) return;

            // Wait for the licence the same way the auto-updater does: a slow
            // online revalidation at boot used to make this give up instantly.
            for (var i = 0; i < 15 && !LicenseService.IsActivated; i++)
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            if (!LicenseService.IsActivated) return;

            if (AttemptedRecently())
            {
                Log.Warn("", "This copy is missing its tool files and a repair was already tried today.");
                Status?.Invoke("This copy is incomplete",
                    "It has no tools\\ folder. Download DeveloperXPanel-Setup.exe from the download page to fix it.");
                return;
            }

            _running = true;

            var setupUrl = await UpdateService.LatestSetupUrlAsync(ct).ConfigureAwait(false);
            if (setupUrl is null)
            {
                Status?.Invoke("This copy is incomplete",
                    "It has no tools\\ folder, and the installer could not be located. Download it from the download page.");
                return;
            }

            var version = Path.GetFileName(new Uri(setupUrl).AbsolutePath); // file name only

            Status?.Invoke("Repairing this install",
                "This copy has no tool files. Downloading the full installer once - the panel will restart by itself.");

            var target = Path.Combine(Path.GetTempPath(), "DeveloperXPanel-Setup.exe");
            var ok = await DownloadAsync(setupUrl, target, ct).ConfigureAwait(false);
            if (!ok)
            {
                WriteMarker(version, "download failed");
                Status?.Invoke("Repair did not finish",
                    "The installer could not be downloaded. Try again later, or get it from the download page.");
                return;
            }

            if (!await VerifyAsync(target, setupUrl + ".sha256", ct).ConfigureAwait(false))
            {
                try { File.Delete(target); } catch { }
                WriteMarker(version, "hash mismatch");
                Status?.Invoke("Repair did not finish",
                    "The downloaded installer failed its hash check and was deleted. Try again, or get it from the download page.");
                return;
            }

            Log.Success("", "Full installer downloaded and verified - starting it to repair this install.");
            WriteMarker(version, "installing");

            var start = MessageBox.Show(
                "This copy of the panel is missing its tool files, which is why every run fails.\n\n" +
                "The full installer has been downloaded and verified. The panel will close, install, and reopen.\n\n" +
                "Install now?",
                Branding.AppName, MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (start != MessageBoxResult.Yes)
            {
                WriteMarker(version, "declined");
                Status?.Invoke("This copy is incomplete",
                    "Run " + target + " when you are ready - it installs the tool files this copy is missing.");
                return;
            }

            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            Application.Current?.Shutdown();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn("", "Self-repair failed: " + ex.Message);
        }
        finally
        {
            _running = false;
        }
    }

    private static async Task<bool> DownloadAsync(string url, string target, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            using var response = await http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? 0;
            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var output = File.Create(target);

            var buffer = new byte[81920];
            long done = 0;
            int read;
            var lastReport = 0;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;

                var percent = total > 0 ? (int)(done * 100 / total) : 0;
                if (percent >= lastReport + 5)
                {
                    lastReport = percent;
                    Status?.Invoke("Repairing this install",
                        $"Downloading the full installer — {percent}% of {total / 1048576} MB");
                }
            }
            return done > 0;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn("", "Installer download failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// The release publishes the digest as "sha256:&lt;hex&gt;"; the prefix is
    /// stripped before the 64-character token is looked for.
    /// </summary>
    private static async Task<bool> VerifyAsync(string file, string hashUrl, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            var text = await http.GetStringAsync(hashUrl, ct).ConfigureAwait(false);
            var expected = text
                .Replace("sha256:", "", StringComparison.OrdinalIgnoreCase)
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(x => x.Length == 64 && x.All(Uri.IsHexDigit));
            if (string.IsNullOrWhiteSpace(expected)) return false;

            await using var stream = File.OpenRead(file);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
            return actual.Equals(expected.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn("", "Installer verification failed: " + ex.Message);
            return false;
        }
    }
}
