using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace AISuitePanel;

/// <summary>
/// Panel self-update, driven by a manifest the operator publishes to their own
/// server (Global Settings → UPDATES → Server URL).
///
/// <para><b>Why file level, not one big zip.</b> A customer install is ~813 MB,
/// of which <c>runtime\</c> is 609 MB and <c>.playwright\</c> 109 MB and neither
/// changes between bug fixes. The manifest therefore lists every updatable file
/// with its SHA-256, and only the files whose hash differs are downloaded — a
/// normal fix moves a few hundred KB of Python instead of a gigabyte.</para>
///
/// <para><b>Why the exe is handled separately.</b> Windows will not let a running
/// program overwrite its own image, but it WILL let it be <i>renamed</i>. So the
/// old exe is renamed aside, the new one is copied into place, and the panel
/// relaunches. Every replaced file is also backed up under the data folder, so a
/// bad release can be rolled back from the same screen.</para>
/// </summary>
internal static class UpdateService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(45) };

    /// <summary>Files never taken from a manifest, whatever it claims.</summary>
    private static readonly string[] NeverUpdate =
    {
        "portable.txt", "README.txt", "accounts", "data",
    };

    // ------------------------------------------------------------------ contract

    public sealed class ManifestFile
    {
        public string Path { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public long Size { get; set; }
        public string PackageUrl { get; set; } = "";
        public string HashUrl { get; set; } = "";
        /// <summary>"app" (default) lands in the install folder, "data" is merged
        /// into a file under %APPDATA%\AISuitePanel (used for tools.json so a NEW
        /// tool box appears after the restart without rebuilding the exe).</summary>
        public string Target { get; set; } = "app";
    }

    public sealed class Manifest
    {
        public string Version { get; set; } = "";
        public string MinPanelVersion { get; set; } = "";
        public string Published { get; set; } = "";
        public string Notes { get; set; } = "";
        public string PackageSha256 { get; set; } = "";
        public string PackageUrl { get; set; } = "";
        public string HashUrl { get; set; } = "";
        public string Signature { get; set; } = "";
        public List<ManifestFile> Files { get; set; } = new();
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("body")] public string Body { get; set; } = "";
        [JsonPropertyName("published_at")] public string PublishedAt { get; set; } = "";
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAsset> Assets { get; set; } = new();
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("browser_download_url")] public string DownloadUrl { get; set; } = "";
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("digest")] public string? Digest { get; set; }
    }

    /// <summary>What both feeds boil down to, so the rest of the check is shared.</summary>
    private sealed class FeedInfo
    {
        public string Version { get; set; } = "";
        public string Published { get; set; } = "";
        public string Notes { get; set; } = "";
        public string PackageUrl { get; set; } = "";
        public string PackageSha256 { get; set; } = "";
        public long Size { get; set; }
        public string HashUrl { get; set; } = "";
        public string Signature { get; set; } = "";
    }

    /// <summary>Shape of the small update.json published next to the download page.</summary>
    private sealed class StaticFeed
    {
        [JsonPropertyName("version")] public string Version { get; set; } = "";
        [JsonPropertyName("published")] public string Published { get; set; } = "";
        [JsonPropertyName("notes")] public string Notes { get; set; } = "";
        [JsonPropertyName("packageUrl")] public string PackageUrl { get; set; } = "";
        [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
        [JsonPropertyName("size")] public long Size { get; set; }
    }

    /// <summary>
    /// CDN-hosted feed written on every release. It carries the same facts the
    /// GitHub API would, but has no per-IP request cap.
    /// </summary>
    private const string StaticFeedUrl = "https://sabbirx01.github.io/Script_Panel/update.json";

    /// <summary>Reads the static feed; null means "not there, use the GitHub API".</summary>
    private static async Task<FeedInfo?> TryStaticFeedAsync(CancellationToken ct)
    {
        try
        {
            using var response = await Http.GetAsync(StaticFeedUrl, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var feed = System.Text.Json.JsonSerializer.Deserialize<StaticFeed>(
                await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false), Json.Options);

            if (feed is null || string.IsNullOrWhiteSpace(feed.Version) || string.IsNullOrWhiteSpace(feed.PackageUrl))
                return null;

            return new FeedInfo
            {
                Version = feed.Version.TrimStart('v', 'V'),
                Published = feed.Published,
                Notes = feed.Notes,
                PackageUrl = feed.PackageUrl,
                PackageSha256 = feed.Sha256,
                Size = feed.Size
            };
        }
        catch
        {
            return null;
        }
    }

    public sealed class CheckResult
    {
        public bool Ok { get; init; }
        public string Error { get; init; } = "";
        public string CurrentVersion { get; init; } = "";
        public string AvailableVersion { get; init; } = "";
        public string Notes { get; init; } = "";
        public IReadOnlyList<ManifestFile> Changed { get; init; } = Array.Empty<ManifestFile>();
        public long ChangedBytes { get; init; }
        public bool UpToDate => Ok && Changed.Count == 0;
    }

    public sealed class InstallResult
    {
        public bool Ok { get; init; }
        public string Error { get; init; } = "";
        public int FilesWritten { get; init; }
        public bool ExeReplaced { get; init; }
    }

    private static string InstalledVersionFile => Path.Combine(AppPaths.Root, "update-version.txt");

    /// <summary>
    /// What this install is on.  After an update this is the version the server
    /// published (written by <see cref="InstallAsync"/>); on a fresh install it
    /// falls back to the assembly version, because a build that has never pulled
    /// an update has nothing else to report.
    /// </summary>
    public static string CurrentVersion
    {
        get
        {
            try
            {
                if (File.Exists(InstalledVersionFile))
                {
                    var text = File.ReadAllText(InstalledVersionFile).Trim();
                    if (text.Length > 0) return text;
                }
            }
            catch
            {
                // fall through to the assembly version
            }
            return typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }
    }

    private static string RawBaseUrl => (SettingsStore.Current.Update.BaseUrl ?? "").Trim();

    public static bool Configured =>
        !RawBaseUrl.Equals("off", StringComparison.OrdinalIgnoreCase);

    private static bool IsGitHubFeed =>
        (RawBaseUrl.Length == 0 || RawBaseUrl.StartsWith("github://", StringComparison.OrdinalIgnoreCase)) &&
        !RawBaseUrl.Equals("off", StringComparison.OrdinalIgnoreCase);

    private static string GitHubRepo
    {
        get
        {
            var raw = RawBaseUrl;
            if (raw.Length == 0) raw = AISuitePanel.Models.UpdateSettings.DefaultBaseUrl;
            return raw.StartsWith("github://", StringComparison.OrdinalIgnoreCase)
                ? raw[9..].Trim().Trim('/').Trim()
                : "Sabbirx01/Script_Panel";
        }
    }

    private static string BaseUrl
    {
        get
        {
            var url = RawBaseUrl;
            if (url.Length == 0 || url.Equals("off", StringComparison.OrdinalIgnoreCase))
                url = AISuitePanel.Models.UpdateSettings.DefaultBaseUrl;
            return url.EndsWith("/") ? url : url + "/";
        }
    }

    private static string GitHubApiUrl => "https://api.github.com/repos/" + GitHubRepo + "/releases/latest";

    public static CheckResult? StartupResult { get; private set; }
    public static event Action<CheckResult>? StartupChecked;

    public static async Task CheckOnStartupAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            if (!LicenseService.IsActivated || !Configured) return;
            var result = await CheckAsync().ConfigureAwait(false);
            StartupResult = result;
            StartupChecked?.Invoke(result);
        }
        catch { }
    }

    private static string StagingDir => Path.Combine(AppPaths.Root, "update-staging");
    private static string BackupDir =>
        Path.Combine(AppPaths.Root, "update-backup", DateTime.Now.ToString("yyyyMMdd-HHmmss"));

    // ------------------------------------------------------------------- checking

    public static async Task<CheckResult> CheckAsync(CancellationToken ct = default)
    {
        if (!Configured)
            return new CheckResult { Ok = false, Error = "Updates are disabled." };
        if (!LicenseService.IsActivated)
            return new CheckResult { Ok = false, Error = "Activate the panel licence before checking for updates." };

        var current = CurrentVersion;
        try
        {
            Manifest manifest;
            if (IsGitHubFeed)
            {
                // The static feed on GitHub Pages comes first. api.github.com is
                // limited to 60 unauthenticated requests per hour per IP, so a few
                // clicks of CHECK NOW - or several customers behind one office
                // NAT - exhausted it and every check answered
                // "403 (rate limit exceeded)". Pages is a CDN and has no such cap.
                var feed = await TryStaticFeedAsync(ct).ConfigureAwait(false);

                if (feed is null)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, GitHubApiUrl);
                    request.Headers.UserAgent.ParseAdd("DeveloperXBulkAccPanel/1.1");
                    request.Headers.Accept.ParseAdd("application/vnd.github+json");
                    using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);

                    if ((int)response.StatusCode == 429 || (int)response.StatusCode == 403)
                        return new CheckResult
                        {
                            Ok = false, CurrentVersion = current,
                            Error = "GitHub is rate-limiting update checks from this connection. "
                                  + "Try again in a few minutes, or download the latest Setup from the download page."
                        };

                    response.EnsureSuccessStatusCode();
                    var release = System.Text.Json.JsonSerializer.Deserialize<GitHubRelease>(
                        await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false), Json.Options)
                        ?? new GitHubRelease();
                    if (release.Draft || release.Prerelease || string.IsNullOrWhiteSpace(release.TagName))
                        return new CheckResult { Ok = false, CurrentVersion = current, Error = "No stable GitHub release is available." };

                    var package = release.Assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
                    var signature = release.Assets.FirstOrDefault(a => a.Name.EndsWith(".sig", StringComparison.OrdinalIgnoreCase));
                    var hashFile = release.Assets.FirstOrDefault(a => a.Name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase));
                    if (package is null)
                        return new CheckResult { Ok = false, CurrentVersion = current, Error = "The GitHub release has no update package." };

                    feed = new FeedInfo
                    {
                        Version = release.TagName.TrimStart('v', 'V'),
                        Published = release.PublishedAt,
                        Notes = string.IsNullOrWhiteSpace(release.Body) ? release.Name : release.Body,
                        PackageUrl = package.DownloadUrl,
                        PackageSha256 = (package.Digest ?? "").Replace("sha256:", "", StringComparison.OrdinalIgnoreCase),
                        Size = package.Size,
                        HashUrl = hashFile?.DownloadUrl ?? "",
                        Signature = signature?.DownloadUrl ?? ""
                    };
                }

                manifest = new Manifest
                {
                    Version = feed.Version,
                    Published = feed.Published,
                    Notes = feed.Notes,
                    PackageSha256 = feed.PackageSha256,
                    PackageUrl = feed.PackageUrl,
                    HashUrl = feed.HashUrl,
                    Signature = feed.Signature
                };

                var installed = ParseVersion(current);
                var available = ParseVersion(manifest.Version);
                if (available <= installed)
                    return new CheckResult { Ok = true, CurrentVersion = current, AvailableVersion = manifest.Version, Notes = manifest.Notes };

                var packageFile = new ManifestFile
                {
                    Path = "DeveloperXBulkAccPanel.exe",
                    Size = feed.Size,
                    Sha256 = manifest.PackageSha256,
                    PackageUrl = manifest.PackageUrl,
                    HashUrl = manifest.HashUrl,
                    Target = "package"
                };
                return new CheckResult
                {
                    Ok = true, CurrentVersion = current, AvailableVersion = manifest.Version,
                    Notes = manifest.Notes, Changed = new[] { packageFile }, ChangedBytes = feed.Size
                };
            }

            var text = await Http.GetStringAsync(BaseUrl + "manifest.json", ct).ConfigureAwait(false);
            manifest = System.Text.Json.JsonSerializer.Deserialize<Manifest>(text, Json.Options) ?? new Manifest();
            var changed = new List<ManifestFile>();
            foreach (var file in manifest.Files)
            {
                if (string.IsNullOrWhiteSpace(file.Path) || IsForbidden(file.Path)) continue;
                if (file.Target.Equals("data", StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(LocalPathFor(file.Path)) || !HashMatches(LocalPathFor(file.Path), file.Sha256))
                    changed.Add(file);
            }
            return new CheckResult { Ok = true, CurrentVersion = current, AvailableVersion = manifest.Version,
                Notes = manifest.Notes, Changed = changed, ChangedBytes = changed.Sum(f => f.Size) };
        }
        catch (Exception ex)
        {
            return new CheckResult { Ok = false, CurrentVersion = current, Error = ex.Message };
        }
    }

    private static Version ParseVersion(string value)
    {
        var clean = new string((value ?? "").Trim().TrimStart('v', 'V').TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        return Version.TryParse(clean, out var parsed) ? parsed : new Version(0, 0, 0);
    }

    /// <summary>Where a manifest entry lands: the install folder, or AppData for a seed.</summary>
    private static string LocalPathFor(string relative)
    {
        var clean = relative.Replace('/', Path.DirectorySeparatorChar).TrimStart('\\');
        // The exe sits beside the data root in the dev layout but inside the
        // payload root in a portable install; AppFolderRoot is correct for both
        // for tools\..., so only the exe needs AppRoot.
        var root = clean.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? AppPaths.AppRoot
            : AppPaths.AppFolderRoot;
        return Path.Combine(root, clean);
    }

    private static bool IsForbidden(string relative)
    {
        var first = relative.Replace('\\', '/').Split('/')[0];
        return NeverUpdate.Any(n => first.Equals(n, StringComparison.OrdinalIgnoreCase))
               || relative.Contains("..");
    }

    private static bool HashMatches(string path, string expected)
    {
        if (string.IsNullOrWhiteSpace(expected)) return false;
        try
        {
            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            return hash.Equals(expected.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ installing

    /// <summary>
    /// Downloads every changed file, verifies it, then swaps it in. Nothing is
    /// touched until all downloads have verified, so a dropped connection can
    /// never leave a half-updated install behind.
    /// </summary>
    public static async Task<InstallResult> InstallAsync(
        IReadOnlyList<ManifestFile> files,
        string version = "",
        IProgress<(int Done, int Total, string Name)>? progress = null,
        CancellationToken ct = default)
    {
        if (files.Count == 0) return new InstallResult { Ok = true };
        if (!LicenseService.IsActivated)
            return new InstallResult { Ok = false, Error = "Activate the panel licence before installing updates." };

        if (files.Any(f => f.Target.Equals("package", StringComparison.OrdinalIgnoreCase)))
            return await InstallGitHubPackageAsync(files.First(f => f.Target.Equals("package", StringComparison.OrdinalIgnoreCase)), version, progress, ct);

        // ---- 1. download + verify into staging ------------------------------
        try
        {
            if (Directory.Exists(StagingDir)) Directory.Delete(StagingDir, recursive: true);
            Directory.CreateDirectory(StagingDir);
        }
        catch (Exception ex)
        {
            return new InstallResult { Ok = false, Error = "Cannot prepare the staging folder: " + ex.Message };
        }

        var staged = new List<(ManifestFile File, string Path)>();
        var done = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file.Path);
            progress?.Report((done, files.Count, name));
            try
            {
                var target = Path.Combine(StagingDir, name + "." + done);
                var url = BaseUrl + "files/" + file.Path.Replace('\\', '/');
                var bytes = await Http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
                await File.WriteAllBytesAsync(target, bytes, ct).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(file.Sha256) &&
                    !HashMatches(target, file.Sha256))
                {
                    return new InstallResult
                    {
                        Ok = false,
                        Error = $"{file.Path} arrived damaged (hash mismatch) — nothing was changed.",
                    };
                }
                staged.Add((file, target));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new InstallResult
                {
                    Ok = false,
                    Error = $"Could not download {file.Path}: {ex.Message}",
                };
            }
            done++;
        }
        progress?.Report((done, files.Count, "applying"));

        // ---- 2. make sure nothing we must replace is in use ------------------
        var appFiles = staged.Where(s => !IsDataSeed(s.File)).ToList();
        foreach (var (file, _) in appFiles)
        {
            var destination = LocalPathFor(file.Path);
            if (destination.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            if (IsLocked(destination))
                return new InstallResult
                {
                    Ok = false,
                    Error = $"{file.Path} is in use — close the running tool and try again. "
                            + "Nothing was changed.",
                };
        }

        // ---- 3. back up, then swap ------------------------------------------
        Directory.CreateDirectory(BackupDir);
        // Backup names are flattened, so the original path is recorded rather
        // than guessed back out of the file name at restore time.
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var written = 0;
        var exeReplaced = false;

        foreach (var (file, source) in appFiles)
        {
            var destination = LocalPathFor(file.Path);
            try
            {
                var dir = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                if (File.Exists(destination))
                {
                    var flat = file.Path.Replace('\\', '_').Replace('/', '_');
                    File.Copy(destination, Path.Combine(BackupDir, flat), overwrite: true);
                    map[flat] = file.Path;
                }

                if (destination.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    // A running image cannot be overwritten but CAN be renamed.
                    var aside = destination + ".old";
                    if (File.Exists(aside)) File.Delete(aside);
                    File.Move(destination, aside);
                    File.Copy(source, destination, overwrite: true);
                    exeReplaced = true;
                }
                else
                {
                    File.Copy(source, destination, overwrite: true);
                }
                written++;
            }
            catch (Exception ex)
            {
                return new InstallResult
                {
                    Ok = false,
                    Error = $"Could not install {file.Path}: {ex.Message}. "
                            + $"Previous copies are safe in {BackupDir}.",
                    FilesWritten = written,
                };
            }
        }

        Json.Save(Path.Combine(BackupDir, "backup-map.json"), new BackupMap { Files = map });

        // Remember what the server called this release, so "installed version"
        // is the number the operator published rather than a stale assembly
        // version from whenever the exe happened to be compiled.
        if (version.Trim().Length > 0)
        {
            try { File.WriteAllText(InstalledVersionFile, version.Trim(), Encoding.UTF8); }
            catch { /* display only - never fail an install over it */ }
        }

        // ---- 4. merge any data seeds (tools.json) ---------------------------
        foreach (var (file, source) in staged.Where(s => IsDataSeed(s.File)))
            MergeToolSeed(source);

        return new InstallResult { Ok = true, FilesWritten = written, ExeReplaced = exeReplaced };
    }

    /// <summary>backup file name → the manifest path it came from.</summary>
    private sealed class BackupMap
    {
        public Dictionary<string, string> Files { get; set; } = new();
    }

    /// <summary>
    /// Copies the files of the most recent update back over the install, so a
    /// release that turns out to be broken can be undone from the UI instead of
    /// by hand.  Returns how many files were restored.
    /// </summary>
    public static int RestoreLastBackup(out string error)
    {
        error = "";
        var dir = LastBackup();
        if (dir is null)
        {
            error = "There is no backup yet.";
            return 0;
        }

        var map = Json.Load(Path.Combine(dir, "backup-map.json"), () => new BackupMap());
        var restored = 0;
        try
        {
            foreach (var (flat, relative) in map.Files)
            {
                var backup = Path.Combine(dir, flat);
                if (!File.Exists(backup)) continue;

                var destination = LocalPathFor(relative);
                var parent = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                File.Copy(backup, destination, overwrite: true);
                restored++;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        if (restored == 0 && error.Length == 0) error = "The backup contains no usable files.";
        return restored;
    }

    private static async Task<InstallResult> InstallGitHubPackageAsync(
        ManifestFile package, string version,
        IProgress<(int Done, int Total, string Name)>? progress,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(package.PackageUrl))
            return new InstallResult { Ok = false, Error = "GitHub release package URL is missing." };

        var tempZip = Path.Combine(AppPaths.Root, "update-staging", "release.zip");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(tempZip)!);
            progress?.Report((0, 1, "GitHub release package"));
            using var response = await Http.GetAsync(package.PackageUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var output = File.Create(tempZip))
                await input.CopyToAsync(output, ct).ConfigureAwait(false);

            var expected = package.Sha256;
            if (string.IsNullOrWhiteSpace(expected) && !string.IsNullOrWhiteSpace(package.HashUrl))
            {
                var hashText = await Http.GetStringAsync(package.HashUrl, ct).ConfigureAwait(false);
                expected = hashText.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(x => x.Length == 64 && x.All(Uri.IsHexDigit)) ?? "";
            }
            if (!HashMatches(tempZip, expected))
                return new InstallResult { Ok = false, Error = "GitHub package hash mismatch — nothing was changed." };

            var extract = Path.Combine(AppPaths.Root, "update-staging", "release");
            if (Directory.Exists(extract)) Directory.Delete(extract, true);
            ZipFile.ExtractToDirectory(tempZip, extract);
            var newExe = Directory.GetFiles(extract, "DeveloperXBulkAccPanel.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (newExe is null) return new InstallResult { Ok = false, Error = "GitHub package has no DeveloperXBulkAccPanel.exe." };

            var destination = Path.Combine(AppPaths.AppRoot, "DeveloperXBulkAccPanel.exe");
            var backup = Path.Combine(BackupDir, "DeveloperXBulkAccPanel.exe");
            Directory.CreateDirectory(BackupDir);
            if (File.Exists(destination)) File.Copy(destination, backup, true);
            var aside = destination + ".old";
            if (File.Exists(aside)) File.Delete(aside);
            if (File.Exists(destination)) File.Move(destination, aside);
            File.Copy(newExe, destination, true);

            var shippedPlaywright = Directory.GetDirectories(extract, ".playwright", SearchOption.AllDirectories).FirstOrDefault();
            if (shippedPlaywright is not null)
            {
                var localPlaywright = Path.Combine(AppPaths.AppRoot, ".playwright");
                if (Directory.Exists(localPlaywright)) Directory.Delete(localPlaywright, true);
                CopyDirectory(shippedPlaywright, localPlaywright);
            }
            try { File.WriteAllText(InstalledVersionFile, version.Trim(), Encoding.UTF8); } catch { }
            progress?.Report((1, 1, "complete"));
            return new InstallResult { Ok = true, FilesWritten = 1, ExeReplaced = true };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new InstallResult { Ok = false, Error = "Could not install GitHub update: " + ex.Message }; }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        foreach (var dir in Directory.GetDirectories(source)) CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }

    private static bool IsDataSeed(ManifestFile file) =>
        file.Target.Equals("data", StringComparison.OrdinalIgnoreCase);

    private static bool IsLocked(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var _ = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Merges a shipped <c>tools.seed.json</c> into the operator's own
    /// <c>tools.json</c>: entries are added or refreshed by id, and nothing the
    /// operator added themselves is ever removed. That is what makes a brand-new
    /// tool box appear after the restart without rebuilding the exe.
    /// </summary>
    private static void MergeToolSeed(string seedPath)
    {
        try
        {
            var seed = JsonNode.Parse(File.ReadAllText(seedPath, Encoding.UTF8))?.AsObject();
            var incoming = seed?["tools"]?.AsArray();
            if (incoming is null) return;

            var targetPath = AppPaths.Tools;
            var root = File.Exists(targetPath)
                ? JsonNode.Parse(File.ReadAllText(targetPath, Encoding.UTF8))?.AsObject() ?? new JsonObject()
                : new JsonObject();
            var existing = root["tools"]?.AsArray() ?? new JsonArray();

            foreach (var candidate in incoming)
            {
                if (candidate is not JsonObject tool) continue;
                var id = (tool["id"]?.GetValue<string>() ?? "").Trim();
                if (id.Length == 0) continue;

                var match = existing.OfType<JsonObject>().FirstOrDefault(
                    t => string.Equals((t["id"]?.GetValue<string>() ?? "").Trim(), id,
                                       StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    existing.Add(JsonNode.Parse(tool.ToJsonString()));
                }
                else
                {
                    foreach (var field in tool)
                        match[field.Key] = JsonNode.Parse(field.Value?.ToJsonString() ?? "null");
                }
            }

            root["tools"] = existing;
            Json.Save(targetPath, root);
        }
        catch
        {
            // A bad seed must never take the panel down; the update just does not
            // add a new box this time.
        }
    }

    // ------------------------------------------------------------------ restart + undo

    /// <summary>Launches the freshly installed exe; the caller then shuts down.</summary>
    public static bool Restart()
    {
        try
        {
            var exe = Path.Combine(AppPaths.AppRoot,
                                   Path.GetFileName(Environment.ProcessPath ?? "DeveloperXBulkAccPanel.exe"));
            if (!File.Exists(exe)) return false;
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = AppPaths.AppRoot });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The backups written by the most recent successful install.</summary>
    public static string? LastBackup()
    {
        try
        {
            var root = Path.Combine(AppPaths.Root, "update-backup");
            if (!Directory.Exists(root)) return null;
            return Directory.GetDirectories(root).OrderBy(d => d).LastOrDefault();
        }
        catch
        {
            return null;
        }
    }
}
