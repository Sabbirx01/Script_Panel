using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace AISuitePanel.Automation;

/// <summary>
/// The tools' Python source, packed into ONE AES-256 encrypted blob that is
/// embedded in this executable (<c>source\Resources\toolbox.enc</c>), plus the
/// tiny loader that mount it in memory.
///
/// <para>Why this exists: the shipped install used to carry the whole
/// <c>tools\</c> tree as plain <c>.py</c> files next to the panel, so anybody
/// who got the installer also got the source. Now the scripts never touch the
/// disk at all - the panel decrypts this blob in memory and hands it to the
/// bundled Python on stdin, which runs the requested entry script straight out
/// of the archive. There is no folder to copy.</para>
///
/// <para>Payload layout, written by <c>temp\build-toolbox.ps1</c>:</para>
/// <code>
///   "DXTB"    4 B   magic
///   format    1 B   = 1
///   IV       16 B   random per build
///   SHA-256  32 B   plaintext (zip) hash - integrity
///   cipher    ..    AES-256-CBC + PKCS7
/// </code>
/// The key is NOT stored: <c>ToolboxKey.g.cs</c> carries a seed, and the key is
/// derived from it plus this payload's SHA-256 (PBKDF2-SHA256, 150k rounds).
/// See that file's header for what that does and does not protect against.
/// </summary>
internal static class ToolboxRunner
{
    private const string ArchiveResource = "AISuitePanel.Resources.toolbox.enc";
    private const string BootstrapResource = "AISuitePanel.Resources.toolbox_bootstrap.py";

    private const int HeaderSize = 4 + 1 + 16 + 32;

    /// <summary>
    /// ONE line of glue, passed to <c>python -c</c>. It reads a 4-byte length
    /// and that many bytes from stdin (the loader above) and exec's them; the
    /// loader then reads the archive the same way. The real code travels on
    /// stdin, so neither the 32 KB command-line limit nor quoting can bite -
    /// and keeping it to a single line means no shell or launcher that re-parses
    /// the command line can tear it apart. The loader does its own exact-read
    /// loop, so the buffer read here is a straight "give me N bytes" (Python's
    /// BufferedReader blocks until it has them).
    /// </summary>
    internal const string Glue =
        "import sys as _s;_b=_s.stdin.buffer;" +
        "exec(compile(_b.read(int.from_bytes(_b.read(4),'big')),'<toolbox>','exec'))";

    /// <summary>
    /// Tool id -> (create script, login script) inside the archive. The first
    /// path component is the tool's folder in <c>tools\</c>, which is also the
    /// folder name the archive mounts under.
    /// </summary>
    private static readonly Dictionary<string, (string Create, string Login)> Entries =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["zai"] = ("zai/Zcreatev2.py", "zai/Zloginv2.py"),
            ["devin"] = ("devin/devin_create.py", "devin/devin_login.py"),
            ["tool4"] = ("minimax/minimax_create.py", "minimax/minimax_login.py"),
            ["accio"] = ("accio/create.py", "accio/login.py"),
        };

    private static readonly object Gate = new();
    private static bool _tried;
    private static byte[]? _archive;
    private static string? _bootstrap;
    private static HashSet<string>? _names;

    /// <summary>True when a usable toolbox is embedded in this build.</summary>
    internal static bool Available
    {
        get
        {
            Load();
            return _archive is not null && _bootstrap is not null;
        }
    }

    /// <summary>Every tool id this build should carry - used by the start-up check.</summary>
    internal static IEnumerable<string> ToolIds => Entries.Keys;

    /// <summary>True when the archive carries this tool at all.</summary>
    internal static bool Has(string? toolId) =>
        !string.IsNullOrWhiteSpace(toolId) && Available && Entries.ContainsKey(toolId!);

    internal static bool HasCreate(string? toolId) => HasEntry(CreateEntry(toolId));

    internal static bool HasLogin(string? toolId) => HasEntry(LoginEntry(toolId));

    /// <summary>True when the archive carries this exact entry, e.g. <c>zai/Zloginv2.py</c>.</summary>
    internal static bool HasEntry(string? entry) =>
        !string.IsNullOrWhiteSpace(entry) && Available &&
        _names is not null && _names.Contains(entry!);

    internal static string? CreateEntry(string? toolId) =>
        Has(toolId) && Entries.TryGetValue(toolId!, out var e) ? e.Create : null;

    internal static string? LoginEntry(string? toolId) =>
        Has(toolId) && Entries.TryGetValue(toolId!, out var e) ? e.Login : null;

    /// <summary>
    /// Which entry to run: the operator's own script when it is actually on
    /// disk (a dev checkout keeps working exactly as before), otherwise the
    /// copy inside the archive.
    /// </summary>
    internal static string? CreateEntryFor(string toolId, string? scriptOnDisk) =>
        OnDisk(scriptOnDisk) ? null : CreateEntry(toolId);

    /// <inheritdoc cref="CreateEntryFor"/>
    internal static string? LoginEntryFor(string toolId, string? scriptOnDisk) =>
        OnDisk(scriptOnDisk) ? null : LoginEntry(toolId);

    /// <summary>
    /// The archive entry for a file that sits NEXT TO the tool's own scripts in
    /// its folder - <c>login_advanced.py</c>, <c>claim_advanced.py</c> and any
    /// other companion the panel can invoke. Returns null when the archive does
    /// not carry that file, so the caller falls back to whatever the disk has.
    /// </summary>
    internal static string? SiblingEntry(string? toolId, string? fileNameOnDisk)
    {
        if (string.IsNullOrWhiteSpace(fileNameOnDisk) || !Has(toolId)) return null;

        var folder = Entries[toolId!].Create.Replace('\\', '/').Split('/')[0];
        var entry = folder + "/" + fileNameOnDisk!.Replace('\\', '/');
        return HasEntry(entry) ? entry : null;
    }

    private static bool OnDisk(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    /// <summary>
    /// Where a toolbox run should behave as if the script lived: a real folder,
    /// because the tools read and write small side files there ("_live_proxies.txt",
    /// bug reports) and the panel writes the run's <c>.env</c> into it too. It is
    /// the tool's accounts folder beside the exe unless the operator pointed the
    /// tool at a folder of their own.
    /// </summary>
    internal static string WorkingDirectory(string toolId, string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
            return configured!;

        AppPaths.EnsureToolAccountsDir(toolId);
        return AppPaths.ToolAccountsDir(toolId);
    }

    /// <summary>
    /// The virtual path the archive is mounted at. It deliberately does not
    /// exist on disk - it is only a name the tools use for <c>__file__</c>.
    /// </summary>
    internal static string MountRoot(string entry)
    {
        var folder = entry.Replace('\\', '/').Split('/')[0];
        return Path.Combine(AppPaths.Root, "toolbox", folder);
    }

    /// <summary>
    /// Command-line prefix for a run that boots the loader: <c>-u -c "&lt;glue&gt;"</c>.
    /// For callers that build the command line as a STRING (and so cannot use
    /// <c>ArgumentList</c>, which .NET refuses to mix with <c>Arguments</c>) plus
    /// <see cref="ApplyEnvironment"/>; their own arguments go after it.
    /// </summary>
    internal const string RawPrefix = "-u -c \"" + Glue + "\"";

    /// <summary>
    /// The two keys the loader reads. False when this build carries no toolbox
    /// at all (a dev build that never ran <c>temp\build-toolbox.ps1</c>).
    /// </summary>
    internal static bool ApplyEnvironment(ProcessStartInfo psi, string entry)
    {
        if (!Available) return false;

        psi.Environment["PANEL_TOOLBOX_ROOT"] = MountRoot(entry);
        psi.Environment["PANEL_TOOLBOX_ENTRY"] = entry;
        return true;
    }

    /// <summary>
    /// Arranges <paramref name="psi"/> to boot the loader instead of a script
    /// file: <c>python -c "&lt;glue&gt;"</c> plus the two keys the loader reads.
    /// </summary>
    internal static bool TryConfigure(ProcessStartInfo psi, string entry)
    {
        if (!ApplyEnvironment(psi, entry)) return false;

        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(Glue);
        return true;
    }

    /// <summary>
    /// Writes the loader, the archive and finally the tool's own stdin lines.
    /// Everything is written straight to the child's pipe - the blob is binary,
    /// so it must not go through a text writer.
    /// </summary>
    internal static async Task WriteStdinAsync(Stream stdin, IEnumerable<string> stdinLines)
    {
        var (archive, bootstrap) = Payload();
        if (archive is null || bootstrap is null) return;

        var bootBytes = Encoding.UTF8.GetBytes(bootstrap);

        await stdin.WriteAsync(LengthBytes(bootBytes.Length)).ConfigureAwait(false);
        await stdin.WriteAsync(bootBytes).ConfigureAwait(false);
        await stdin.WriteAsync(LengthBytes(archive.Length)).ConfigureAwait(false);
        await stdin.WriteAsync(archive).ConfigureAwait(false);

        foreach (var line in stdinLines)
        {
            await stdin.WriteAsync(Encoding.UTF8.GetBytes(line + "\n")).ConfigureAwait(false);
        }

        await stdin.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>The decrypted archive and the loader text, or (null, null).</summary>
    internal static (byte[]? Archive, string? Bootstrap) Payload()
    {
        Load();
        return (_archive, _bootstrap);
    }

    private static byte[] LengthBytes(int value) => new[]
    {
        (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value
    };

    // ------------------------------------------------------------------ load

    private static void Load()
    {
        if (_tried) return;
        lock (Gate)
        {
            if (_tried) return;
            _tried = true;

            try
            {
                var asm = typeof(ToolboxRunner).Assembly;

                using var enc = asm.GetManifestResourceStream(ArchiveResource);
                if (enc is null)
                {
                    // A dev build that never ran temp\build-toolbox.ps1. The
                    // tools\ folder is still on disk in that case, so the
                    // drivers simply keep using it.
                    Log.Warn("toolbox", $"no embedded toolbox ({ArchiveResource}) - " +
                                        "using the tools folder instead");
                    return;
                }

                using var buffer = new MemoryStream();
                enc.CopyTo(buffer);
                _archive = Decrypt(buffer.ToArray());
                _names = ArchiveNames(_archive);

                using var boot = asm.GetManifestResourceStream(BootstrapResource);
                if (boot is null)
                {
                    Log.Warn("toolbox", $"no embedded loader ({BootstrapResource})");
                    _archive = null;
                    return;
                }

                using var reader = new StreamReader(boot, Encoding.UTF8);
                _bootstrap = reader.ReadToEnd();
            }
            catch (Exception ex)
            {
                Log.Warn("toolbox", "could not load the embedded toolbox: " + ex.Message);
                _archive = null;
                _bootstrap = null;
            }
        }
    }

    /// <summary>
    /// Every file the archive carries, forward-slashed. Only used to answer
    /// "does the toolbox have X?" - the bytes are never extracted.
    /// </summary>
    private static HashSet<string> ArchiveNames(byte[] zip)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var stream = new MemoryStream(zip, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) continue;
            names.Add(entry.FullName.Replace('\\', '/'));
        }
        return names;
    }

    private static byte[] Decrypt(byte[] blob)
    {
        if (blob.Length < HeaderSize)
            throw new InvalidDataException("toolbox payload is truncated");

        if (blob[0] != (byte)'D' || blob[1] != (byte)'X' ||
            blob[2] != (byte)'T' || blob[3] != (byte)'B')
            throw new InvalidDataException("toolbox payload has a bad magic");

        // Format 2 derives the key from the payload's own hash. An older build's
        // blob (format 1) cannot be opened by this code and must not be guessed
        // at - say so, instead of decrypting garbage.
        if (blob[4] != 2)
            throw new InvalidDataException($"toolbox payload format {blob[4]} is not supported - rebuild the panel");

        var iv = new byte[16];
        Buffer.BlockCopy(blob, 5, iv, 0, 16);

        var wantSha = new byte[32];
        Buffer.BlockCopy(blob, 21, wantSha, 0, 32);

        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = ToolboxKey.Derive(wantSha);
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        var plain = decryptor.TransformFinalBlock(blob, HeaderSize, blob.Length - HeaderSize);

        var gotSha = SHA256.HashData(plain);
        if (!CryptographicOperations.FixedTimeEquals(gotSha, wantSha))
            throw new InvalidDataException("toolbox payload failed its integrity check");

        return plain;
    }
}
