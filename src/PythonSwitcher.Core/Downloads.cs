using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PythonSwitcher.Core;

/// <summary>Eine herunterladbare Version mit Prüfwerten.</summary>
public sealed record AvailableRelease(
    ToolKind Tool,
    string Version,
    Version SortKey,
    string Url,
    string FileName,
    long Size,
    string? Sha256,
    string? Md5);

public static class ReleaseCatalog
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PythonSwitcher", "1.0"));
        return c;
    }

    internal static HttpClient Client => Http;

    /// <summary>Alle stabilen Python-Versionen mit 64-Bit-Installer für Windows, neueste zuerst.</summary>
    public static async Task<List<AvailableRelease>> PythonAsync(CancellationToken ct)
    {
        var json = await Http.GetStringAsync("https://www.python.org/api/v2/downloads/release_file/?os=1", ct);
        return ParsePython(json);
    }

    internal static List<AvailableRelease> ParsePython(string json)
    {
        var result = new List<AvailableRelease>();
        using var doc = JsonDocument.Parse(json);
        foreach (var f in doc.RootElement.EnumerateArray())
        {
            var url = f.GetProperty("url").GetString() ?? "";
            // Nur endgültige Versionen, z. B. .../3.13.9/python-3.13.9-amd64.exe (keine a1/b2/rc1)
            var m = Regex.Match(url, @"/python-(\d+)\.(\d+)\.(\d+)-amd64\.exe$");
            if (!m.Success)
                continue;
            var version = new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
            string? Str(string name) =>
                f.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && p.GetString() is { Length: > 0 } s ? s : null;
            long size = f.TryGetProperty("filesize", out var fs) && fs.TryGetInt64(out var l) ? l : 0;
            var md5 = Str("md5_sum");
            var sha = Str("sha256_sum");
            if (md5 is null && sha is null)
                continue; // ohne Prüfwert nicht anbieten
            result.Add(new AvailableRelease(ToolKind.Python, version.ToString(3), version, url,
                Path.GetFileName(url), size, sha, md5));
        }
        return result
            .GroupBy(r => r.Version)
            .Select(g => g.First())
            .OrderByDescending(r => r.SortKey)
            .ToList();
    }

    /// <summary>Stabile Git-für-Windows-Versionen als PortableGit (64 Bit), neueste zuerst.</summary>
    public static async Task<List<AvailableRelease>> GitAsync(CancellationToken ct)
    {
        var json = await Http.GetStringAsync("https://api.github.com/repos/git-for-windows/git/releases?per_page=40", ct);
        return ParseGit(json);
    }

    internal static List<AvailableRelease> ParseGit(string json)
    {
        var result = new List<AvailableRelease>();
        using var doc = JsonDocument.Parse(json);
        foreach (var rel in doc.RootElement.EnumerateArray())
        {
            if (rel.TryGetProperty("prerelease", out var pre) && pre.GetBoolean())
                continue;
            if (rel.TryGetProperty("draft", out var draft) && draft.GetBoolean())
                continue;
            foreach (var asset in rel.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                var m = Regex.Match(name, @"^PortableGit-(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?-64-bit\.7z\.exe$");
                if (!m.Success)
                    continue;
                var digest = asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                if (digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal))
                    continue; // ohne Prüfwert nicht anbieten
                int rev = m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 0;
                var version = new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), rev);
                result.Add(new AvailableRelease(ToolKind.Git,
                    m.Groups[4].Success ? version.ToString(4) : version.ToString(3), version,
                    asset.GetProperty("browser_download_url").GetString() ?? "", name,
                    asset.GetProperty("size").GetInt64(), digest["sha256:".Length..], null));
            }
        }
        return result.OrderByDescending(r => r.SortKey).ToList();
    }
}

public static class Downloader
{
    /// <summary>Lädt die Datei herunter und prüft die Prüfsumme. Bei Abweichung wird die Datei gelöscht.</summary>
    public static async Task DownloadAsync(AvailableRelease release, string target, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var part = target + ".teil";
        using var hash = release.Sha256 is not null
            ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
            : IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var expected = (release.Sha256 ?? release.Md5)!.ToLowerInvariant();

        try
        {
            using (var response = await ReleaseCatalog.Client.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? release.Size;
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var output = File.Create(part);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    hash.AppendData(buffer, 0, read);
                    done += read;
                    if (total > 0)
                        progress?.Report((double)done / total);
                }
            }

            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (actual != expected)
                throw new InvalidDataException(
                    $"Die Prüfsumme der heruntergeladenen Datei stimmt nicht (erwartet {expected}, erhalten {actual}). Die Datei wurde verworfen.");
            File.Move(part, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(part))
                File.Delete(part);
        }
    }
}

/// <summary>Prüft die digitale Signatur (Authenticode) einer Datei und den Namen des Unterzeichners.</summary>
public static class SignatureCheck
{
    public const string PythonSigner = "Python Software Foundation";
    public const string GitSigner = "Johannes Schindelin";

    public static string ExpectedSigner(ToolKind tool) => tool == ToolKind.Python ? PythonSigner : GitSigner;

    /// <summary>Wirft eine Ausnahme, wenn die Signatur ungültig ist oder von jemand anderem stammt.</summary>
    public static void Verify(string file, string expectedSigner)
    {
        int rc = WinVerify(file);
        if (rc != 0)
            throw new InvalidDataException($"Die digitale Signatur der Datei ist ungültig (Code 0x{rc:X8}). Die Datei wird nicht ausgeführt.");

#pragma warning disable SYSLIB0057
        using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(file));
#pragma warning restore SYSLIB0057
        var cn = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        if (!string.Equals(cn, expectedSigner, StringComparison.Ordinal))
            throw new InvalidDataException($"Die Datei ist von „{cn}“ signiert, erwartet war „{expectedSigner}“. Die Datei wird nicht ausgeführt.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

#pragma warning disable SYSLIB1054
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WinTrustData data);
#pragma warning restore SYSLIB1054

    private static int WinVerify(string file)
    {
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
        var path = Marshal.StringToCoTaskMemUni(file);
        var fileInfo = new WinTrustFileInfo { cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(), pcwszFilePath = path };
        var pFile = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);
            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = 2,          // WTD_UI_NONE
                fdwRevocationChecks = 0, // WTD_REVOKE_NONE
                dwUnionChoice = 1,       // WTD_CHOICE_FILE
                pFile = pFile,
                dwStateAction = 1,       // WTD_STATEACTION_VERIFY
            };
            int rc = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            data.dwStateAction = 2;      // WTD_STATEACTION_CLOSE
            WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            return rc;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pFile);
            Marshal.FreeCoTaskMem(path);
        }
    }
}

public static class Installers
{
    /// <summary>
    /// Wenn für dieselbe Hauptversion (z. B. 3.12) schon eine Python-Installation für diesen Benutzer
    /// per Installer eingerichtet ist, würde der Installer diese aktualisieren statt eine zweite anzulegen.
    /// Liefert dann den Ordner der vorhandenen Installation.
    /// </summary>
    public static string? ExistingPythonForMinor(Version version)
    {
        var tag = $"{version.Major}.{version.Minor}";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Python\PythonCore\{tag}\InstallPath");
            return key?.GetValue(null) as string ?? (key is null ? null : "(Ordner unbekannt)");
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Der Python-Installer lässt beim Deinstallieren Teile von HKCU\Software\Python\PythonCore\&lt;X.Y&gt; zurück
    /// (Help, Idle, InstalledFeatures, PythonPath – im Probelauf festgestellt). Entfernt den Zweig nur, wenn er
    /// verwaist ist, also keinen InstallPath mehr hat. Liefert true, wenn etwas entfernt wurde.
    /// </summary>
    public static bool RemoveOrphanedPythonRegistry(Version version)
    {
        var tag = $"{version.Major}.{version.Minor}";
        using var core = Registry.CurrentUser.OpenSubKey(@"Software\Python\PythonCore", writable: true);
        if (core is null)
            return false;
        using (var key = core.OpenSubKey(tag))
        {
            if (key is null)
                return false;
            using var installPath = key.OpenSubKey("InstallPath");
            if (installPath is not null)
                return false; // noch eine echte Installation registriert
        }
        core.DeleteSubKeyTree(tag, throwOnMissingSubKey: false);
        return true;
    }

    /// <summary>
    /// Protokolldatei für den Python-Installer. Sie muss im TEMP-Ordner liegen: Mit einem Protokoll unter
    /// %LOCALAPPDATA%\PythonSwitcher bricht der Windows-Installer-Dienst mit Fehler 1622 ab (im Probelauf festgestellt).
    /// </summary>
    public static string LogFile(string name) => Path.Combine(Path.GetTempPath(), $"PythonSwitcher-{name}-{DateTime.Now:yyyyMMdd-HHmmss}.log");

    /// <summary>Installiert Python still für den aktuellen Benutzer, ohne PATH, Launcher, Verknüpfungen und Dateizuordnungen.</summary>
    public static Task<int> InstallPythonAsync(string installer, string targetDir, string logFile, CancellationToken ct) =>
        RunAsync(installer,
        [
            "/quiet",
            "InstallAllUsers=0",
            "TargetDir=" + targetDir,
            "PrependPath=0",
            "AppendPath=0",
            "Include_launcher=0",
            "InstallLauncherAllUsers=0",
            "AssociateFiles=0",
            "Shortcuts=0",
            "Include_test=0",
            "/log", logFile,
        ], ct);

    public static Task<int> UninstallPythonAsync(string installer, string logFile, CancellationToken ct) =>
        RunAsync(installer, ["/quiet", "/uninstall", "/log", logFile], ct);

    /// <summary>Entpackt PortableGit (selbstentpackendes 7-Zip-Archiv) still in den Zielordner.</summary>
    public static Task<int> ExtractPortableGitAsync(string archive, string targetDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(archive)
        {
            Arguments = $"-y -o\"{targetDir}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        return RunAsync(psi, ct);
    }

    /// <summary>Exit-Codes des Python-Installers, die Erfolg bedeuten.</summary>
    public static bool IsSuccess(int exitCode) => exitCode is 0 or 3010;

    public static string DescribePythonExitCode(int exitCode) => exitCode switch
    {
        0 => "Erfolgreich.",
        3010 => "Erfolgreich, ein Neustart wird empfohlen.",
        1602 => "Der Vorgang wurde abgebrochen.",
        1618 => "Eine andere Installation läuft gerade. Bitte später erneut versuchen.",
        1638 => "Diese Python-Version ist bereits installiert.",
        _ => $"Fehlercode {exitCode}. Details stehen in der Protokolldatei.",
    };

    private static Task<int> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        return RunAsync(psi, ct);
    }

    private static async Task<int> RunAsync(ProcessStartInfo psi, CancellationToken ct)
    {
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Das Programm konnte nicht gestartet werden.");
        await p.WaitForExitAsync(ct);
        return p.ExitCode;
    }
}
