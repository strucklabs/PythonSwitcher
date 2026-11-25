using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Win32;

namespace PythonSwitcher.Core.VersionCheck;

/// <summary>
/// Prüft, ob Python und die übrigen Programme sichere Verbindungen (HTTPS) prüfen können und ob ihre
/// Zertifikatssammlungen veraltet sind. Liest nur und baut höchstens Testverbindungen auf – nichts wird verändert.
/// </summary>
public sealed class CertScan
{
    public const string GroupTitle = "Certificates";

    /// <summary>Ab diesem Alter gilt eine Zertifikatssammlung als veraltet.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(365);

    public const string CurlCaUrl = "https://curl.se/ca/cacert.pem";

    // ---- austauschbare Zugriffe (Standard: nichts vorhanden, für Tests) ----
    public Func<string, IReadOnlyList<string>, int, CancellationToken, Task<RunResult?>> Run { get; init; } =
        (_, _, _, _) => Task.FromResult<RunResult?>(null);
    public Func<string, string?> FindCommand { get; init; } = _ => null;
    public Func<string, CancellationToken, Task<string?>> PyPiLatest { get; init; } = (_, _) => Task.FromResult<string?>(null);
    /// <summary>Änderungsdatum einer Datei oder eines Ordners; null, wenn nicht vorhanden.</summary>
    public Func<string, DateTime?> PathDate { get; init; } = _ => null;
    public Func<string, byte[]?> ReadBytes { get; init; } = _ => null;
    /// <summary>Umgebungsvariable, wie ein neu gestartetes Programm sie sieht.</summary>
    public Func<string, string?> EnvVar { get; init; } = _ => null;
    /// <summary>Stammzertifikate im Windows-Speicher: Anzahl und Namen der abgelaufenen.</summary>
    public Func<(int Total, IReadOnlyList<string> Expired)> WindowsRoots { get; init; } = () => (0, []);
    public Func<IReadOnlyList<string>> JavaHomes { get; init; } = () => [];
    public Func<string, string?> ExternallyManaged { get; init; } = _ => null;
    public Func<IReadOnlyList<HeldPackage>> Holds { get; init; } = () => [];
    public Func<DateTime> Now { get; init; } = () => DateTime.Now;

    /// <summary>Umgebungsvariablen, die Zertifikate umlenken, mit den Programmen, die sie beachten.</summary>
    public static readonly IReadOnlyList<(string Name, string Who)> Redirects =
    [
        ("SSL_CERT_FILE", "Python, PHP and other OpenSSL-based programs"),
        ("SSL_CERT_DIR", "Python, PHP and other OpenSSL-based programs"),
        ("REQUESTS_CA_BUNDLE", "the Python package requests"),
        ("CURL_CA_BUNDLE", "curl and the Python package requests"),
        ("PIP_CERT", "pip"),
        ("NODE_EXTRA_CA_CERTS", "Node.js (in addition to its built-in list)"),
        ("GIT_SSL_CAINFO", "Git"),
    ];

    /// <summary>Echte Umgebung.</summary>
    public static CertScan ForSystem(IPathStore paths, Func<string, string?> externallyManaged, Func<IReadOnlyList<HeldPackage>> holds)
    {
        var env = new SystemEnvironment(paths);
        return new CertScan
        {
            Run = PipScan.RunAsync,
            FindCommand = env.FindCommand,
            PyPiLatest = SystemEnvironment.PyPiLatestAsync,
            PathDate = p => File.Exists(p) ? File.GetLastWriteTime(p) : Directory.Exists(p) ? Directory.GetLastWriteTime(p) : null,
            ReadBytes = p => File.Exists(p) ? File.ReadAllBytes(p) : null,
            EnvVar = FreshEnvironmentVariable,
            WindowsRoots = ReadWindowsRoots,
            JavaHomes = FindJavaHomes,
            ExternallyManaged = externallyManaged,
            Holds = holds,
        };
    }

    // =====================================================================================
    // Gesamtprüfung und Einzelprüfung
    // =====================================================================================

    public async Task<List<CheckItem>> ScanAsync(IReadOnlyList<Installation> pythons, IReadOnlyList<Installation> gits, CancellationToken ct)
    {
        var tasks = new List<Task<List<CheckItem>>>();
        tasks.AddRange(pythons.Select(p => PythonAsync(p, ct)));
        tasks.AddRange(gits.Select(g => GitAsync(g, ct)));
        tasks.Add(Task.Run(() => JavaHomes().SelectMany(h => Java(h)).ToList(), ct));
        tasks.Add(NodeAsync(ct));
        tasks.Add(PhpAsync(ct));
        var rows = (await Task.WhenAll(tasks)).SelectMany(r => r).ToList();
        rows.AddRange(EnvironmentRows(emptyRow: false));
        rows.AddRange(WindowsStore());
        return rows;
    }

    public async Task<List<CheckItem>> RecheckAsync(CheckItem item, IReadOnlyList<Installation> installations, Func<ToolDefinition, string, string?> probe,
        CancellationToken ct)
    {
        var t = item.Target!;
        Installation? Fresh(ToolKind kind)
        {
            var inst = installations.FirstOrDefault(i => i.Tool == kind && PathUtil.SameDirectory(i.Root, t.Root ?? ""));
            if (inst is null)
                return null;
            var version = probe(ToolDefinition.For(kind), inst.Root);
            return version is null ? null : new Installation { Tool = kind, Root = inst.Root, Version = version, Source = inst.Source };
        }

        var rows = t.CatalogName switch
        {
            "python" => Fresh(ToolKind.Python) is { } py ? await PythonAsync(py, ct) : [],
            "git" => Fresh(ToolKind.Git) is { } git ? await GitAsync(git, ct) : [],
            "java" => Java(t.Root!),
            "node" => await NodeAsync(ct),
            "php" => await PhpAsync(ct),
            "env" => EnvironmentRows(emptyRow: true),
            "windows" => WindowsStore(),
            _ => [],
        };
        if (rows.Count > 0)
            return rows;
        return [item with
        {
            Installed = null, Latest = null, Status = CheckStatus.Hinweis, UpdateCommand = null, Note = null,
            Hint = "No longer found. It may have been uninstalled.",
        }];
    }

    // =====================================================================================
    // Python
    // =====================================================================================

    internal const string PythonScript = """
        import json, ssl, socket
        r = {'openssl': ssl.OPENSSL_VERSION, 'openssl_major': ssl.OPENSSL_VERSION_INFO[0]}
        try:
            import certifi
            r['certifi'] = certifi.__version__
            r['certifi_where'] = certifi.where()
        except Exception:
            r['certifi'] = None
        try:
            from pip._vendor import certifi as pc
            r['pip_certifi'] = pc.__version__
        except Exception:
            r['pip_certifi'] = None
        try:
            from pip._internal.configuration import Configuration
            c = Configuration(isolated=False)
            c.load()
            r['pip_cert'] = [[k, v] for k, v in c.items() if k.split('.')[-1] == 'cert']
        except Exception:
            r['pip_cert'] = []
        def test(ctx):
            try:
                with socket.create_connection(('pypi.org', 443), timeout=8) as s:
                    with ctx.wrap_socket(s, server_hostname='pypi.org'):
                        return 'ok'
            except ssl.SSLCertVerificationError as e:
                return 'verify:' + (e.verify_message or str(e))
            except ssl.SSLError as e:
                return 'verify:' + str(e)
            except Exception as e:
                return 'net:' + type(e).__name__ + ': ' + str(e)
        r['test'] = test(ssl.create_default_context())
        if r['certifi']:
            try:
                r['test_certifi'] = test(ssl.create_default_context(cafile=r['certifi_where']))
            except Exception as e:
                r['test_certifi'] = 'verify:' + str(e)
        print(json.dumps(r))
        """;

    internal sealed record PyInfo(string? Openssl, int OpensslMajor, string? Certifi, string? CertifiWhere, string? PipCertifi,
        List<(string Key, string Value)> PipCert, string? Test, string? TestCertifi);

    internal static PyInfo? ParsePython(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json.Trim().Split('\n').Last());
            var r = doc.RootElement;
            string? S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var pipCert = new List<(string, string)>();
            if (r.TryGetProperty("pip_cert", out var pc) && pc.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in pc.EnumerateArray())
                    pipCert.Add((e[0].GetString() ?? "", e[1].GetString() ?? ""));
            }
            return new PyInfo(S("openssl"), r.TryGetProperty("openssl_major", out var m) && m.TryGetInt32(out var mi) ? mi : 0,
                S("certifi"), S("certifi_where"), S("pip_certifi"), pipCert, S("test"), S("test_certifi"));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<List<CheckItem>> PythonAsync(Installation py, CancellationToken ct)
    {
        var python = ToolDefinition.Python.MainExecutablePath(py.Root);
        var target = new CheckTarget(TargetKind.Certificate, ToolKind.Python, py.Root, CatalogName: "python");
        var label = $"Python {py.Version}";
        var run = await Run(python, ["-c", PythonScript], 40_000, ct);
        var info = run is { ExitCode: 0 } ? ParsePython(run.Out) : null;
        if (info is null)
        {
            return [Row(label + " – HTTPS", null, null, CheckStatus.Unbekannt,
                "Could not be checked: Python did not respond or reported an error.", py.Root, target)];
        }

        var rows = new List<CheckItem>();

        // Verbindungen über die Standardbibliothek (Windows-Zertifikatsspeicher)
        var (status, hint) = Test(info.Test, "pypi.org",
            "Secure connections work. Python uses the Windows certificate store, which Windows Update keeps up to date.",
            "Python cannot verify secure connections");
        string? note = null;
        if (info.OpensslMajor is > 0 and < 3)
        {
            var text = $"This Python version uses {ShortOpenssl(info.Openssl)}, which no longer receives security updates. " +
                       "Only a newer Python version brings a newer OpenSSL.";
            if (status == CheckStatus.Aktuell)
            {
                status = CheckStatus.Hinweis;
                hint = text + " Secure connections currently work.";
            }
            else
            {
                note = text;
            }
        }
        rows.Add(Row(label + " – HTTPS", ShortOpenssl(info.Openssl), null, status, hint, py.Root, target) with { Note = note });

        // certifi (für requests und viele andere Pakete)
        if (info.Certifi is not null)
            rows.Add(await CertifiRow(py, python, info, target, ct));

        // certifi in pip
        if (info.PipCertifi is not null)
        {
            var age = CertifiAge(info.PipCertifi);
            var pipCmd = PipScan.UpgradeCommand(python, ["pip"]);
            var managed = ExternallyManaged(py.Root);
            bool old = age > MaxAge;
            const string about = "pip brings its own certificate collection, updated together with pip.";
            rows.Add(!old
                ? Row($"pip's certificates ({label})", info.PipCertifi, null, CheckStatus.Aktuell, about, py.Root, target)
                : managed is not null
                    ? Row($"pip's certificates ({label})", info.PipCertifi, null, CheckStatus.Hinweis,
                        $"{about} It is {Months(age!.Value)} months old, but this Python installation is managed by another tool, " +
                        $"which also updates pip: {managed}", py.Root, target)
                    : Row($"pip's certificates ({label})", info.PipCertifi, null, CheckStatus.UpdateVerfuegbar,
                        $"{about} It is {Months(age!.Value)} months old. Update pip: {pipCmd}", py.Root, target, pipCmd));
        }

        // pip-Einstellung "cert"
        foreach (var (key, value) in info.PipCert.DistinctBy(c => c.Value, StringComparer.OrdinalIgnoreCase))
            rows.Add(FileSettingRow($"pip setting {key} ({label})", value, "pip", $"{python} -m pip config unset {key}", py.Root, target));

        return rows;
    }

    private async Task<CheckItem> CertifiRow(Installation py, string python, PyInfo info, CheckTarget target, CancellationToken ct)
    {
        var name = $"certifi (Python {py.Version})";
        const string what = "Certificate collection used by requests and many other packages.";
        if (info.TestCertifi is { } t && t.StartsWith("verify:", StringComparison.Ordinal))
        {
            return Row(name, info.Certifi, null, CheckStatus.Problem,
                $"{what} Secure connections with it fail: {t[7..]}. Updating certifi may help: {PipScan.UpgradeCommand(python, ["certifi"])}",
                py.Root, target, PipScan.UpgradeCommand(python, ["certifi"]));
        }
        var latest = await PyPiLatest("certifi", ct);
        var hold = Holds().FirstOrDefault(h => h.Matches(HeldPackage.KindPip, py.Root, "certifi"));
        var managed = ExternallyManaged(py.Root);
        var cmp = VersionCompare.Compare(info.Certifi, latest);
        if (cmp < 0)
        {
            if (hold is not null)
                return Row(name, info.Certifi, latest, CheckStatus.Hinweis, $"{what} {hold.Describe()}", py.Root, target);
            if (managed is not null)
                return Row(name, info.Certifi, latest, CheckStatus.Hinweis, $"{what} Newer version available, but this Python installation is managed by another tool: {managed}", py.Root, target);
            var cmd = PipScan.UpgradeCommand(python, ["certifi"]);
            return Row(name, info.Certifi, latest, CheckStatus.UpdateVerfuegbar, $"{what} Update: {cmd}", py.Root, target, cmd);
        }
        if (cmp is null && CertifiAge(info.Certifi) is { } age && age > MaxAge)
        {
            return Row(name, info.Certifi, null, CheckStatus.Hinweis,
                $"{what} It is {Months(age)} months old; the latest version could not be determined.", py.Root, target);
        }
        return Row(name, info.Certifi, latest, cmp is null ? CheckStatus.Unbekannt : CheckStatus.Aktuell,
            cmp is null ? what + " The latest version could not be determined (no connection?)." : what, py.Root, target);
    }

    /// <summary>certifi-Versionen sind Daten (2026.11.20) – Alter daraus.</summary>
    internal TimeSpan? CertifiAge(string? version)
    {
        var parts = version?.Split('.');
        if (parts is not { Length: >= 3 } || !int.TryParse(parts[0], out var y) || !int.TryParse(parts[1], out var m)
            || !int.TryParse(parts[2], out var d) || y < 2000 || m is < 1 or > 12 || d is < 1 or > 31)
            return null;
        try
        {
            return Now() - new DateTime(y, m, d);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    // =====================================================================================
    // Git
    // =====================================================================================

    internal static readonly string[] GitBundleCandidates =
    [
        @"mingw64\etc\ssl\certs\ca-bundle.crt", @"ucrt64\etc\ssl\certs\ca-bundle.crt",
        @"clangarm64\etc\ssl\certs\ca-bundle.crt", @"usr\ssl\certs\ca-bundle.crt",
    ];

    private async Task<List<CheckItem>> GitAsync(Installation git, CancellationToken ct)
    {
        var exe = ToolDefinition.Git.MainExecutablePath(git.Root);
        var target = new CheckTarget(TargetKind.Certificate, ToolKind.Git, git.Root, CatalogName: "git");
        var name = $"Git {git.Version}";
        var backendTask = Run(exe, ["config", "--get", "http.sslBackend"], 10_000, ct);
        var caInfoTask = Run(exe, ["config", "--get", "http.sslCAInfo"], 10_000, ct);
        var testTask = Run(exe, ["ls-remote", "https://github.com/git/git", "HEAD"], 25_000, ct);
        await Task.WhenAll(backendTask, caInfoTask, testTask);
        var backend = (await backendTask)?.Out.Trim();
        var caInfo = (await caInfoTask)?.Out.Trim();
        var test = await testTask;

        string installed, about;
        CheckStatus status = CheckStatus.Aktuell;
        string? command = null;
        if (string.Equals(backend, "schannel", StringComparison.OrdinalIgnoreCase))
        {
            installed = "Windows certificate store";
            about = "Git uses the Windows certificate store (setting http.sslBackend = schannel), which Windows Update keeps up to date.";
        }
        else
        {
            var custom = !string.IsNullOrEmpty(caInfo) ? caInfo : EnvVar("GIT_SSL_CAINFO");
            var file = !string.IsNullOrEmpty(custom)
                ? custom
                : GitBundleCandidates.Select(c => Path.Combine(git.Root, c)).FirstOrDefault(c => PathDate(c) is not null);
            var date = file is null ? null : PathDate(file);
            if (file is null || date is null)
            {
                return [Row(name, null, null, CheckStatus.Problem,
                    !string.IsNullOrEmpty(custom)
                        ? $"Git is set to use the certificate file {custom}, which does not exist – Git cannot verify secure connections. " +
                          "Correct or remove the setting http.sslCAInfo (or GIT_SSL_CAINFO)."
                        : "Git uses its own certificate file, but it was not found. Reinstalling or updating Git restores it.",
                    git.Root, target)];
            }
            installed = $"own file ({date:yyyy-MM-dd})";
            var age = Now() - date.Value;
            if (!string.IsNullOrEmpty(custom))
            {
                about = $"Git uses the certificate file {file} (setting http.sslCAInfo or GIT_SSL_CAINFO).";
                if (age > MaxAge)
                {
                    status = CheckStatus.Hinweis;
                    about += $" The file is {Months(age)} months old – replace it with a current one.";
                }
            }
            else
            {
                about = $"Git uses its own certificate file {file}, updated together with Git.";
                if (age > MaxAge)
                {
                    status = CheckStatus.Hinweis;
                    about += $" It is {Months(age)} months old – updating Git brings a current one.";
                }
            }
            about += " Alternatively, Git can use the Windows certificate store: git config --global http.sslBackend schannel";
        }

        if (test is null || test.ExitCode != 0)
        {
            var err = test?.Err.Trim() ?? "";
            if (err.Contains("certificate", StringComparison.OrdinalIgnoreCase) || err.Contains("SSL", StringComparison.Ordinal))
            {
                status = CheckStatus.Problem;
                about = $"Secure connections fail: {FirstLine(err)}. " + about;
            }
            else if (status == CheckStatus.Aktuell)
            {
                status = CheckStatus.Unbekannt;
                about += " The connection test could not reach github.com (no connection?).";
            }
        }
        return [Row(name, installed, null, status, about, git.Root, target, command)];
    }

    // =====================================================================================
    // Java
    // =====================================================================================

    private List<CheckItem> Java(string home)
    {
        var file = Path.Combine(home, "lib", "security", "cacerts");
        var target = new CheckTarget(TargetKind.Certificate, Root: home, CatalogName: "java");
        var name = $"Java ({Path.GetFileName(home.TrimEnd('\\'))})";
        var bytes = ReadBytes(file);
        if (bytes is null)
            return [];
        var date = PathDate(file);
        var certs = KeyStore.ReadCertificates(bytes);
        if (certs is null)
        {
            return [Row(name, date is null ? null : $"file from {date:yyyy-MM-dd}", null, CheckStatus.Unbekannt,
                $"Java's own certificate list ({file}) could not be read. It is updated together with Java.", home, target)];
        }
        var now = Now();
        var expired = certs.Where(c => c.NotAfter < now).ToList();
        var installed = $"{certs.Count} certificates";
        var age = date is null ? (TimeSpan?)null : now - date.Value;
        bool old = age > MaxAge;
        var text = "Java uses its own certificate list, updated together with Java." +
                   (old ? $" The list is {Months(age!.Value)} months old – installing the latest Java update brings a current one." : "");
        if (expired.Count == 0)
        {
            return [Row(name, installed, null, old ? CheckStatus.Hinweis : CheckStatus.Aktuell,
                text + (old ? "" : " None of the certificates have expired."), home, target)];
        }
        var names = string.Join(", ", expired.Take(3).Select(c => c.Name)) + (expired.Count > 3 ? ", …" : "");
        return [Row(name, installed, null, old ? CheckStatus.Hinweis : CheckStatus.Aktuell,
            text + $" {expired.Count} of the certificates have expired ({names}); Java simply no longer uses them, nothing needs to be done.",
            string.Join("\n", expired.Select(c => $"{c.Name} – expired {c.NotAfter:yyyy-MM-dd}")), target)];
    }

    // =====================================================================================
    // Node.js
    // =====================================================================================

    internal const string NodeScript = """
        const tls = require('tls');
        const out = { v: process.version, roots: tls.rootCertificates.length };
        const done = r => { out.test = r; console.log(JSON.stringify(out)); process.exit(0); };
        setTimeout(() => done('net:timeout'), 9000);
        const s = tls.connect(443, 'pypi.org', { servername: 'pypi.org' }, () => { s.destroy(); done('ok'); });
        s.on('error', e => done((/CERT|SELF_SIGNED|ISSUER|UNABLE_TO_VERIFY/.test(e.code || '') ? 'verify:' : 'net:') + (e.code || '') + ' ' + e.message));
        """;

    private async Task<List<CheckItem>> NodeAsync(CancellationToken ct)
    {
        var node = FindCommand("node");
        if (node is null)
            return [];
        var target = new CheckTarget(TargetKind.Certificate, CatalogName: "node");
        var npm = FindCommand("npm");
        var runTask = Run(node, ["-e", NodeScript], 20_000, ct);
        var npmTask = npm is null ? Task.FromResult<RunResult?>(null) : Run(npm, ["config", "get", "cafile"], 20_000, ct);
        await Task.WhenAll(runTask, npmTask);
        var run = await runTask;
        var rows = new List<CheckItem>();
        string? version = null, test = null;
        int roots = 0;
        try
        {
            using var doc = JsonDocument.Parse(run?.Out.Trim().Split('\n').Last() ?? "");
            version = doc.RootElement.GetProperty("v").GetString();
            roots = doc.RootElement.GetProperty("roots").GetInt32();
            test = doc.RootElement.GetProperty("test").GetString();
        }
        catch (Exception)
        {
        }
        if (version is null)
        {
            rows.Add(Row("Node.js", null, null, CheckStatus.Unbekannt, "Could not be checked: Node.js did not respond.", node, target));
        }
        else
        {
            var (status, hint) = Test(test, "pypi.org",
                "Secure connections work. Node.js uses its own built-in certificate list, updated together with Node.js.",
                "Node.js cannot verify secure connections");
            if (status == CheckStatus.Problem)
            {
                hint += " If a proxy or antivirus program inspects HTTPS, add its certificate with NODE_EXTRA_CA_CERTS, " +
                        "or let Node.js 22.15/23.8 and later use the Windows certificate store: NODE_USE_SYSTEM_CA=1";
            }
            rows.Add(Row($"Node.js {version.TrimStart('v')}", $"{roots} built-in root certificates", null, status, hint, node, target));
        }

        var cafile = (await npmTask)?.Out.Trim();
        if (!string.IsNullOrEmpty(cafile) && cafile is not ("null" or "undefined"))
            rows.Add(FileSettingRow("npm setting cafile", cafile, "npm", "npm config delete cafile", node, target));
        return rows;
    }

    // =====================================================================================
    // PHP
    // =====================================================================================

    internal const string PhpScript = """
        $r = ['v' => PHP_VERSION, 'ini' => php_ini_loaded_file(), 'curl_cainfo' => ini_get('curl.cainfo'),
              'openssl_cafile' => ini_get('openssl.cafile'), 'curl' => extension_loaded('curl')];
        if ($r['curl']) {
            $c = curl_init('https://pypi.org/');
            curl_setopt_array($c, [CURLOPT_NOBODY => true, CURLOPT_RETURNTRANSFER => true, CURLOPT_CONNECTTIMEOUT => 8, CURLOPT_TIMEOUT => 10]);
            curl_exec($c);
            $r['errno'] = curl_errno($c);
            $r['error'] = curl_error($c);
        }
        echo json_encode($r);
        """;

    /// <summary>curl-Fehler, die auf fehlende oder falsche Zertifikate hindeuten (60, 77, 35, 58, 83).</summary>
    private static readonly int[] CurlCertErrors = [35, 58, 60, 77, 83];

    private async Task<List<CheckItem>> PhpAsync(CancellationToken ct)
    {
        var php = FindCommand("php");
        if (php is null)
            return [];
        var target = new CheckTarget(TargetKind.Certificate, CatalogName: "php");
        var dir = Path.GetDirectoryName(php)!;
        var run = await Run(php, ["-r", PhpScript], 25_000, ct);
        string? version = null, ini = null, cainfo = null, cafile = null, error = null;
        bool curl = false;
        int errno = 0;
        try
        {
            using var doc = JsonDocument.Parse(run?.Out.Trim() ?? "");
            var r = doc.RootElement;
            string? S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            version = S("v");
            ini = S("ini");
            cainfo = S("curl_cainfo");
            cafile = S("openssl_cafile");
            curl = r.TryGetProperty("curl", out var cu) && cu.ValueKind == JsonValueKind.True;
            if (r.TryGetProperty("errno", out var en))
                en.TryGetInt32(out errno);
            error = S("error");
        }
        catch (Exception)
        {
        }
        if (version is null)
            return [Row("PHP", null, null, CheckStatus.Unbekannt, "Could not be checked: PHP did not respond.", php, target)];

        var name = $"PHP {version}";
        var download = $"Invoke-WebRequest {CurlCaUrl} -OutFile \"{Path.Combine(dir, "cacert.pem")}\"";
        var set = string.IsNullOrEmpty(cainfo) ? cafile : cainfo;
        var pem = Path.Combine(dir, "cacert.pem");
        var setText = ini is null
            ? $"No php.ini is loaded yet: copy php.ini-production to php.ini in {dir} and set in it"
            : $"Set in {ini}";
        var howTo = $"1. Download a certificate file (the command can be copied with \"Copy update command\"): {download}  " +
                    $"2. {setText}: curl.cainfo = \"{pem}\" and openssl.cafile = \"{pem}\".";

        if (!string.IsNullOrEmpty(set))
        {
            var date = PathDate(set);
            if (date is null)
            {
                return [Row(name, null, null, CheckStatus.Problem,
                    $"PHP is set to use the certificate file {set}, which does not exist – secure connections with curl fail. " +
                    $"Download a current file: {download}, and correct curl.cainfo/openssl.cafile in {ini ?? "php.ini"}.",
                    php, target, download)];
            }
            var installed = $"own file ({date:yyyy-MM-dd})";
            if (curl && CurlCertErrors.Contains(errno))
            {
                return [Row(name, installed, null, CheckStatus.Problem,
                    $"Secure connections with curl fail ({error}). The certificate file {set} may be outdated or unsuitable. Download a current one: {download}",
                    php, target, download)];
            }
            var age = Now() - date.Value;
            if (age > MaxAge)
            {
                return [Row(name, installed, null, CheckStatus.UpdateVerfuegbar,
                    $"PHP uses the certificate file {set}, which is {Months(age)} months old. Download a current one: {download}",
                    php, target, download)];
            }
            return [Row(name, installed, null, curl && errno != 0 ? CheckStatus.Unbekannt : CheckStatus.Aktuell,
                $"PHP uses the certificate file {set} (settings curl.cainfo/openssl.cafile)." +
                (curl && errno != 0 ? " The connection test could not reach pypi.org (no connection?)." : " Secure connections work."),
                php, target)];
        }

        if (!curl)
        {
            return [Row(name, "no certificate file", null, CheckStatus.Hinweis,
                "PHP has no certificate file set (curl.cainfo/openssl.cafile). The curl extension is not active, so this could not be tested. " +
                "If curl or OpenSSL functions are used later, they will not be able to verify secure connections. " + howTo,
                php, target, download)];
        }
        if (CurlCertErrors.Contains(errno))
        {
            return [Row(name, "no certificate file", null, CheckStatus.Problem,
                $"PHP has no certificate file, so curl cannot verify secure connections ({error}). Fix: {howTo}",
                php, target, download)];
        }
        if (errno != 0)
        {
            return [Row(name, "no certificate file", null, CheckStatus.Unbekannt,
                $"PHP has no certificate file set (curl.cainfo/openssl.cafile); the connection test could not reach pypi.org (no connection?). " +
                "If secure connections with curl fail later: " + howTo, php, target, download)];
        }
        return [Row(name, "Windows certificate store", null, CheckStatus.Aktuell,
            "Secure connections with curl work without a certificate file of its own.", php, target)];
    }

    // =====================================================================================
    // Umgebungsvariablen und Windows-Speicher
    // =====================================================================================

    private List<CheckItem> EnvironmentRows(bool emptyRow)
    {
        var target = new CheckTarget(TargetKind.Certificate, CatalogName: "env");
        var rows = new List<CheckItem>();
        foreach (var (name, who) in Redirects)
        {
            var value = EnvVar(name);
            if (!string.IsNullOrWhiteSpace(value))
                rows.Add(FileSettingRow($"Environment variable {name}", value, who, $"Remove or correct the environment variable {name}", null, target, plainFix: true));
        }
        if (rows.Count == 0 && emptyRow)
        {
            rows.Add(Row("Environment variables", "none set", null, CheckStatus.Aktuell,
                "No environment variable redirects certificates.", null, target));
        }
        return rows;
    }

    private List<CheckItem> WindowsStore()
    {
        var (total, expired) = WindowsRoots();
        if (total == 0)
            return [];
        var target = new CheckTarget(TargetKind.Certificate, CatalogName: "windows");
        var hint = "Kept up to date automatically by Windows Update; used by Python's standard library, Git (schannel), .NET, Go, Rust and browsers.";
        if (expired.Count > 0)
        {
            hint += $" {expired.Count} of them have expired: old certificates from Microsoft and other vendors that Windows keeps on purpose " +
                    "(for older signed programs and drivers). They are not used for secure connections and should not be deleted.";
        }
        return [Row("Windows certificate store", $"{total} root certificates", null, CheckStatus.Aktuell, hint,
            expired.Count == 0 ? null : "Expired: " + string.Join(", ", expired), target)];
    }

    // =====================================================================================
    // Hilfen
    // =====================================================================================

    private static CheckItem Row(string name, string? installed, string? latest, CheckStatus status, string hint,
        string? detail, CheckTarget target, string? command = null) =>
        new(CheckCategory.Zertifikate, GroupTitle, name, installed, latest, status, hint, null, detail, command, target);

    /// <summary>Eine Einstellung, die auf eine Zertifikatsdatei (oder einen Ordner) zeigt.</summary>
    private CheckItem FileSettingRow(string name, string path, string who, string fix, string? detail, CheckTarget target, bool plainFix = false)
    {
        var date = PathDate(path);
        if (date is null)
        {
            return Row(name, path, null, CheckStatus.Problem,
                $"Points to a certificate file that does not exist – {who} cannot verify secure connections. " +
                (plainFix ? fix + "." : $"Remove the setting: {fix}"), detail, target, plainFix ? null : fix);
        }
        var age = Now() - date.Value;
        return Row(name, path, null, CheckStatus.Hinweis,
            $"Redirects {who} to this certificate file (dated {date:yyyy-MM-dd}) instead of the built-in one." +
            (age > MaxAge ? $" It is {Months(age)} months old – replace it with a current one if connections fail." : ""),
            detail, target);
    }

    /// <summary>Wertet einen Verbindungstest aus ("ok", "verify:…", "net:…").</summary>
    private static (CheckStatus, string) Test(string? result, string host, string okText, string failText) => result switch
    {
        "ok" => (CheckStatus.Aktuell, okText),
        { } r when r.StartsWith("verify:", StringComparison.Ordinal) =>
            (CheckStatus.Problem, $"{failText}: the certificate of {host} could not be verified ({r[7..].Trim()}). " +
                                  "Common causes: a proxy or antivirus program that inspects HTTPS, or an environment variable that points to an unsuitable certificate file."),
        _ => (CheckStatus.Unbekannt, $"The connection test could not reach {host} (no connection?)."),
    };

    /// <summary>"OpenSSL 1.1.1t  7 Feb 2023" → "OpenSSL 1.1.1t".</summary>
    internal static string? ShortOpenssl(string? s) =>
        s is null ? null : string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2));

    private static int Months(TimeSpan age) => (int)(age.TotalDays / 30.4);

    private static string FirstLine(string s) => s.Trim().Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";

    /// <summary>Umgebungsvariable wie für ein neu gestartetes Programm: Benutzer vor System (Registry, nicht der Prozess).</summary>
    public static string? FreshEnvironmentVariable(string name)
    {
        try
        {
            using var user = Registry.CurrentUser.OpenSubKey("Environment");
            if (user?.GetValue(name) is string u && u.Length > 0)
                return Environment.ExpandEnvironmentVariables(u);
            using var machine = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment");
            if (machine?.GetValue(name) is string m && m.Length > 0)
                return Environment.ExpandEnvironmentVariables(m);
        }
        catch (Exception)
        {
        }
        return null;
    }

    public static (int, IReadOnlyList<string>) ReadWindowsRoots()
    {
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            var now = DateTime.Now;
            var all = store.Certificates.Cast<X509Certificate2>().ToList();
            var expired = all.Where(c => c.NotAfter < now)
                .Select(c => c.GetNameInfo(X509NameType.SimpleName, false) is { Length: > 0 } n ? n : c.Subject)
                .Distinct().ToList();
            foreach (var c in all)
                c.Dispose();
            return (all.Count, expired);
        }
        catch (Exception)
        {
            return (0, []);
        }
    }

    /// <summary>Java-Installationen mit eigener Zertifikatsliste (lib\security\cacerts).</summary>
    public static IReadOnlyList<string> FindJavaHomes()
    {
        var bases = new List<string>();
        foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }
                     .Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var vendor in new[] { "Java", "Eclipse Adoptium", "Microsoft", "Zulu", "Amazon Corretto", "BellSoft", "Semeru" })
                bases.Add(Path.Combine(pf, vendor));
        }
        var homes = new List<string>();
        foreach (var b in bases.Where(Directory.Exists))
        {
            try
            {
                homes.AddRange(Directory.GetDirectories(b));
            }
            catch (Exception)
            {
            }
        }
        if (FreshEnvironmentVariable("JAVA_HOME") is { } jh)
            homes.Add(jh);
        return homes
            .Where(h => File.Exists(Path.Combine(h, "lib", "security", "cacerts")))
            .Select(h => Path.GetFullPath(h).TrimEnd('\\'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

/// <summary>Liest die vertrauenswürdigen Zertifikate aus Javas cacerts (JKS oder PKCS12) – ohne Kennwort.</summary>
public static class KeyStore
{
    public sealed record Cert(string Name, DateTime NotAfter);

    /// <summary>null, wenn die Datei nicht gelesen werden kann.</summary>
    public static List<Cert>? ReadCertificates(byte[] data)
    {
        try
        {
            if (data.Length >= 4 && data[0] == 0xFE && data[1] == 0xED && data[2] == 0xFE && data[3] == 0xED)
                return ReadJks(data);
            var coll = X509CertificateLoader.LoadPkcs12Collection(data, null, X509KeyStorageFlags.EphemeralKeySet,
                new Pkcs12LoaderLimits { IgnoreEncryptedAuthSafes = true, IgnorePrivateKeys = true });
            var list = coll.Select(ToCert).ToList();
            foreach (var c in coll)
                c.Dispose();
            return list.Count == 0 ? null : list;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Cert ToCert(X509Certificate2 c) =>
        new(c.GetNameInfo(X509NameType.SimpleName, false) is { Length: > 0 } n ? n : c.Subject, c.NotAfter);

    /// <summary>
    /// JKS: Magic FEEDFEED, Version, Anzahl; je Eintrag Typ (1 = Schlüssel, 2 = Zertifikat), Alias, Zeitstempel,
    /// dann Zertifikat(e). Die abschließende Prüfsumme (braucht das Kennwort) wird nicht geprüft.
    /// </summary>
    private static List<Cert> ReadJks(byte[] data)
    {
        int pos = 4;
        int Int() { int v = (data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3]; pos += 4; return v; }
        string Utf() { int len = (data[pos] << 8) | data[pos + 1]; pos += 2; var s = System.Text.Encoding.UTF8.GetString(data, pos, len); pos += len; return s; }
        byte[] Bytes() { int len = Int(); var b = data.AsSpan(pos, len).ToArray(); pos += len; return b; }

        int version = Int();
        int count = Int();
        var result = new List<Cert>();
        for (int i = 0; i < count; i++)
        {
            int tag = Int();
            Utf(); // Alias
            pos += 8; // Zeitstempel
            if (tag == 1)
            {
                Bytes(); // geschützter Schlüssel
                int chain = Int();
                for (int k = 0; k < chain; k++)
                {
                    if (version == 2)
                        Utf();
                    Bytes();
                }
            }
            else if (tag == 2)
            {
                if (version == 2)
                    Utf(); // Zertifikatstyp
                using var cert = X509CertificateLoader.LoadCertificate(Bytes());
                result.Add(ToCert(cert));
            }
            else
            {
                throw new InvalidDataException("Unknown keystore entry.");
            }
        }
        return result;
    }
}
