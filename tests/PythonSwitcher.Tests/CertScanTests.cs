using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PythonSwitcher.Core;
using PythonSwitcher.Core.VersionCheck;

namespace PythonSwitcher.Tests;

/// <summary>Zertifikatsprüfung: Auswertung der Programmausgaben mit festen Daten, ohne Netz.</summary>
public class CertScanTests
{
    private const string Py = @"C:\Py310";
    private const string GitRoot = @"C:\Program Files\Git";
    private static readonly DateTime Today = new(2026, 11, 25);
    private static readonly Installation Python = new() { Tool = ToolKind.Python, Root = Py, Version = "3.10.11" };
    private static readonly Installation Git = new() { Tool = ToolKind.Git, Root = GitRoot, Version = "2.56.1" };

    private sealed class Fake
    {
        public Dictionary<string, RunResult?> Runs = [];   // Schlüssel: Programmname (ohne Pfad) + erstes Argument
        public Dictionary<string, DateTime> Files = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Env = [];
        public Dictionary<string, string> Commands = [];
        public string? Latest = "2026.11.20";
        public (int, IReadOnlyList<string>) Roots = (0, []);
        public Dictionary<string, byte[]> Bytes = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Java = [];
        public List<HeldPackage> Holds = [];

        public CertScan Scan() => new()
        {
            Run = (exe, args, _, _) =>
            {
                var key = Path.GetFileNameWithoutExtension(exe) + " " + string.Join(" ", args.Take(args[0] == "config" ? 3 : 1));
                return Task.FromResult(Runs.GetValueOrDefault(key));
            },
            FindCommand = c => Commands.GetValueOrDefault(c),
            PyPiLatest = (_, _) => Task.FromResult(Latest),
            PathDate = p => Files.TryGetValue(p, out var d) ? d : null,
            ReadBytes = p => Bytes.GetValueOrDefault(p),
            EnvVar = n => Env.GetValueOrDefault(n),
            WindowsRoots = () => Roots,
            JavaHomes = () => Java,
            Holds = () => Holds,
            Now = () => Today,
        };
    }

    private static RunResult Ok(string output) => new(0, output, "");

    private static string PyJson(int major = 3, string openssl = "OpenSSL 3.0.16 11 Feb 2025", string? certifi = null,
        string pipCertifi = "2026.10.23", string test = "ok", string? testCertifi = "ok", string pipCert = "[]") =>
        $$"""
        {"openssl": "{{openssl}}", "openssl_major": {{major}}, "certifi": {{(certifi is null ? "null" : $"\"{certifi}\"")}},
         "certifi_where": "C:\\x\\cacert.pem", "pip_certifi": "{{pipCertifi}}", "pip_cert": {{pipCert}},
         "test": "{{test}}"{{(certifi is null ? "" : $", \"test_certifi\": \"{testCertifi}\"")}}}
        """.ReplaceLineEndings(" ");

    private static Task<List<CheckItem>> ScanPython(Fake f) => f.Scan().ScanAsync([Python], [], default);

    // ---------------------------------------------------------------- Python

    [Fact]
    public async Task Python_with_working_connections_is_up_to_date()
    {
        var f = new Fake { Runs = { ["python -c"] = Ok(PyJson()) } };
        var rows = await ScanPython(f);
        var https = rows.Single(r => r.Name == "Python 3.10.11 – HTTPS");
        Assert.Equal(CheckStatus.Aktuell, https.Status);
        Assert.Equal("OpenSSL 3.0.16", https.Installed);
        Assert.Equal(CertScan.GroupTitle, https.Group);
        Assert.Equal(CheckStatus.Aktuell, rows.Single(r => r.Name.StartsWith("pip's certificates")).Status);
        Assert.DoesNotContain(rows, r => r.Name.StartsWith("certifi"));
    }

    [Fact]
    public async Task Old_openssl_is_a_note()
    {
        var f = new Fake { Runs = { ["python -c"] = Ok(PyJson(major: 1, openssl: "OpenSSL 1.1.1t  7 Feb 2023")) } };
        var https = (await ScanPython(f)).Single(r => r.Name.EndsWith("HTTPS"));
        Assert.Equal(CheckStatus.Hinweis, https.Status);
        Assert.Equal("OpenSSL 1.1.1t", https.Installed);
        Assert.Contains("no longer receives security updates", https.Hint);
    }

    [Fact]
    public async Task Failed_verification_is_a_problem_and_no_connection_is_unknown()
    {
        var f = new Fake { Runs = { ["python -c"] = Ok(PyJson(test: "verify:unable to get local issuer certificate")) } };
        var https = (await ScanPython(f)).Single(r => r.Name.EndsWith("HTTPS"));
        Assert.Equal(CheckStatus.Problem, https.Status);
        Assert.Contains("unable to get local issuer certificate", https.Hint);

        f.Runs["python -c"] = Ok(PyJson(test: "net:gaierror: getaddrinfo failed"));
        https = (await ScanPython(f)).Single(r => r.Name.EndsWith("HTTPS"));
        Assert.Equal(CheckStatus.Unbekannt, https.Status);
        Assert.Contains("no connection", https.Hint);
    }

    [Fact]
    public async Task Outdated_certifi_offers_the_pip_command()
    {
        var f = new Fake { Runs = { ["python -c"] = Ok(PyJson(certifi: "2024.2.2")) } };
        var row = (await ScanPython(f)).Single(r => r.Name == "certifi (Python 3.10.11)");
        Assert.Equal(CheckStatus.UpdateVerfuegbar, row.Status);
        Assert.Equal("2026.11.20", row.Latest);
        Assert.Contains("install --upgrade certifi", row.UpdateCommand);
    }

    [Fact]
    public async Task Held_certifi_gets_no_command()
    {
        var f = new Fake
        {
            Runs = { ["python -c"] = Ok(PyJson(certifi: "2024.2.2")) },
            Holds = [new() { Kind = HeldPackage.KindPip, Root = Py, Package = "certifi", Version = "2024.2.2" }],
        };
        var row = (await ScanPython(f)).Single(r => r.Name.StartsWith("certifi"));
        Assert.Equal(CheckStatus.Hinweis, row.Status);
        Assert.Null(row.UpdateCommand);
    }

    [Fact]
    public async Task Old_pip_certificates_suggest_updating_pip()
    {
        var f = new Fake { Runs = { ["python -c"] = Ok(PyJson(pipCertifi: "2024.8.30")) } };
        var row = (await ScanPython(f)).Single(r => r.Name.StartsWith("pip's certificates"));
        Assert.Equal(CheckStatus.UpdateVerfuegbar, row.Status);
        Assert.Contains("install --upgrade pip", row.UpdateCommand);
    }

    [Fact]
    public async Task Old_pip_certificates_of_a_managed_python_are_a_note_without_command()
    {
        var f = new Fake { Runs = { ["python -c"] = Ok(PyJson(pipCertifi: "2024.08.30")) } };
        var scan = new CertScan
        {
            Run = f.Scan().Run, Now = () => Today, ExternallyManaged = _ => "Use uv to manage this Python.",
        };
        var row = (await scan.ScanAsync([Python], [], default)).Single(r => r.Name.StartsWith("pip's certificates"));
        Assert.Equal(CheckStatus.Hinweis, row.Status);
        Assert.Null(row.UpdateCommand);
        Assert.Contains("managed by another tool", row.Hint);
    }

    [Fact]
    public async Task Pip_cert_setting_to_a_missing_file_is_a_problem()
    {
        var f = new Fake { Runs = { ["python -c"] = Ok(PyJson(pipCert: """[["global.cert", "C:\\certs\\corp.pem"]]""")) } };
        var row = (await ScanPython(f)).Single(r => r.Name.StartsWith("pip setting global.cert"));
        Assert.Equal(CheckStatus.Problem, row.Status);
        Assert.EndsWith("-m pip config unset global.cert", row.UpdateCommand);
    }

    [Fact]
    public async Task Python_that_does_not_answer_is_unknown()
    {
        var rows = await ScanPython(new Fake());
        Assert.Equal(CheckStatus.Unbekannt, Assert.Single(rows).Status);
    }

    // ---------------------------------------------------------------- Git

    [Fact]
    public async Task Git_with_schannel_uses_the_windows_store()
    {
        var f = new Fake { Runs = { ["git config --get http.sslBackend"] = Ok("schannel\n"), ["git ls-remote"] = Ok("abc\tHEAD") } };
        var row = Assert.Single(await f.Scan().ScanAsync([], [Git], default));
        Assert.Equal(CheckStatus.Aktuell, row.Status);
        Assert.Equal("Windows certificate store", row.Installed);
    }

    [Fact]
    public async Task Git_with_its_own_bundle_shows_the_file_date_and_problems()
    {
        var bundle = Path.Combine(GitRoot, @"mingw64\etc\ssl\certs\ca-bundle.crt");
        var f = new Fake
        {
            Runs = { ["git ls-remote"] = new RunResult(128, "", "fatal: unable to access 'https://github.com/git/git/': SSL certificate problem: unable to get local issuer certificate") },
            Files = { [bundle] = new DateTime(2026, 10, 26) },
        };
        var row = Assert.Single(await f.Scan().ScanAsync([], [Git], default));
        Assert.Equal("own file (2026-10-26)", row.Installed);
        Assert.Equal(CheckStatus.Problem, row.Status);
        Assert.Contains("SSL certificate problem", row.Hint);

        f.Runs["git config --get http.sslCAInfo"] = Ok(@"C:\gone\ca.pem");
        row = Assert.Single(await f.Scan().ScanAsync([], [Git], default));
        Assert.Equal(CheckStatus.Problem, row.Status);
        Assert.Contains("does not exist", row.Hint);
    }

    // ---------------------------------------------------------------- PHP und Node.js

    [Fact]
    public async Task Php_without_certificate_file_is_a_problem_with_download_command()
    {
        var f = new Fake
        {
            Commands = { ["php"] = @"C:\php\php.exe" },
            Runs = { ["php -r"] = Ok("""{"v":"8.4.28","ini":"C:\\php\\php.ini","curl_cainfo":"","openssl_cafile":"","curl":true,"errno":60,"error":"unable to get local issuer certificate"}""") },
        };
        var row = Assert.Single(await f.Scan().ScanAsync([], [], default));
        Assert.Equal("PHP 8.4.28", row.Name);
        Assert.Equal(CheckStatus.Problem, row.Status);
        Assert.Equal(@"Invoke-WebRequest https://curl.se/ca/cacert.pem -OutFile ""C:\php\cacert.pem""", row.UpdateCommand);
        Assert.Contains(@"curl.cainfo = ""C:\php\cacert.pem""", row.Hint);
        Assert.Contains(@"C:\php\php.ini", row.Hint);
    }

    [Fact]
    public async Task Php_with_current_certificate_file_is_up_to_date_and_old_one_is_offered_again()
    {
        var f = new Fake
        {
            Commands = { ["php"] = @"C:\php\php.exe" },
            Runs = { ["php -r"] = Ok("""{"v":"8.4.28","ini":"C:\\php\\php.ini","curl_cainfo":"C:\\php\\cacert.pem","openssl_cafile":"","curl":true,"errno":0,"error":""}""") },
            Files = { [@"C:\php\cacert.pem"] = new DateTime(2026, 9, 1) },
        };
        Assert.Equal(CheckStatus.Aktuell, Assert.Single(await f.Scan().ScanAsync([], [], default)).Status);

        f.Files[@"C:\php\cacert.pem"] = new DateTime(2024, 1, 1);
        var row = Assert.Single(await f.Scan().ScanAsync([], [], default));
        Assert.Equal(CheckStatus.UpdateVerfuegbar, row.Status);
        Assert.NotNull(row.UpdateCommand);
    }

    [Fact]
    public async Task Php_without_connection_is_unknown()
    {
        var f = new Fake
        {
            Commands = { ["php"] = @"C:\php\php.exe" },
            Runs = { ["php -r"] = Ok("""{"v":"8.4.28","ini":false,"curl_cainfo":"","openssl_cafile":"","curl":true,"errno":6,"error":"Could not resolve host"}""") },
        };
        var row = Assert.Single(await f.Scan().ScanAsync([], [], default));
        Assert.Equal(CheckStatus.Unbekannt, row.Status);
    }

    [Fact]
    public async Task Node_verification_failure_is_a_problem()
    {
        var f = new Fake
        {
            Commands = { ["node"] = @"C:\node\node.exe" },
            Runs = { ["node -e"] = Ok("""{"v":"v26.13.1","roots":118,"test":"verify:UNABLE_TO_GET_ISSUER_CERT_LOCALLY unable to get local issuer certificate"}""") },
        };
        var row = Assert.Single(await f.Scan().ScanAsync([], [], default));
        Assert.Equal("Node.js 26.13.1", row.Name);
        Assert.Equal("118 built-in root certificates", row.Installed);
        Assert.Equal(CheckStatus.Problem, row.Status);
        Assert.Contains("NODE_USE_SYSTEM_CA", row.Hint);
    }

    // ---------------------------------------------------------------- Umgebung, Windows, Java

    [Fact]
    public async Task Environment_variables_are_shown_only_when_set()
    {
        var f = new Fake { Env = { ["SSL_CERT_FILE"] = @"C:\gone.pem", ["REQUESTS_CA_BUNDLE"] = @"C:\corp.pem" }, Files = { [@"C:\corp.pem"] = Today } };
        var rows = await f.Scan().ScanAsync([], [], default);
        Assert.Equal(2, rows.Count);
        Assert.Equal(CheckStatus.Problem, rows.Single(r => r.Name.EndsWith("SSL_CERT_FILE")).Status);
        Assert.Equal(CheckStatus.Hinweis, rows.Single(r => r.Name.EndsWith("REQUESTS_CA_BUNDLE")).Status);
        Assert.Empty(await new Fake().Scan().ScanAsync([], [], default));
    }

    [Fact]
    public async Task Windows_store_explains_expired_certificates_without_delete_command()
    {
        var f = new Fake { Roots = (77, ["DST Root CA X3", "Baltimore CyberTrust Root"]) };
        var row = Assert.Single(await f.Scan().ScanAsync([], [], default));
        Assert.Equal(CheckStatus.Aktuell, row.Status);
        Assert.Contains("2 of them have expired", row.Hint);
        Assert.Contains("should not be deleted", row.Hint);
        Assert.Null(row.UpdateCommand);
    }

    [Fact]
    public async Task Java_keystore_is_read_without_password()
    {
        const string home = @"C:\Java\jre8";
        var file = Path.Combine(home, "lib", "security", "cacerts");
        var f = new Fake
        {
            Java = [home],
            Bytes = { [file] = Jks(("valid", Cert("Valid Root", Today.AddYears(5))), ("old", Cert("Old Root", Today.AddYears(-2)))) },
            Files = { [file] = Today.AddMonths(-2) },
        };
        var row = Assert.Single(await f.Scan().ScanAsync([], [], default));
        Assert.Equal("2 certificates", row.Installed);
        Assert.Equal(CheckStatus.Aktuell, row.Status);
        Assert.Contains("1 of the certificates have expired (Old Root)", row.Hint);

        f.Files[file] = Today.AddYears(-2);
        Assert.Equal(CheckStatus.Hinweis, Assert.Single(await f.Scan().ScanAsync([], [], default)).Status);
    }

    [Fact]
    public void Unreadable_keystore_gives_null()
    {
        Assert.Null(KeyStore.ReadCertificates([1, 2, 3, 4, 5]));
    }

    [Fact]
    public async Task Recheck_replaces_the_rows_of_one_source()
    {
        var f = new Fake { Commands = { ["node"] = @"C:\node\node.exe" }, Runs = { ["node -e"] = Ok("""{"v":"v26.13.1","roots":118,"test":"ok"}""") } };
        var scan = f.Scan();
        var row = Assert.Single(await scan.ScanAsync([], [], default));
        Assert.Equal("cert|node|", row.Target!.ReplaceKey);

        f.Commands.Clear();
        var again = Assert.Single(await scan.RecheckAsync(row, [], (_, _) => null, default));
        Assert.Contains("No longer found", again.Hint);
    }

    // ---------------------------------------------------------------- Hilfen

    private static byte[] Cert(string cn, DateTime notAfter)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest($"CN={cn}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var c = req.CreateSelfSigned(notAfter.AddYears(-10), notAfter);
        return c.Export(X509ContentType.Cert);
    }

    /// <summary>Baut eine JKS-Datei (Version 2) mit vertrauenswürdigen Zertifikaten.</summary>
    private static byte[] Jks(params (string Alias, byte[] Der)[] entries)
    {
        using var ms = new MemoryStream();
        void Int(int v) => ms.Write([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
        void Utf(string s) { var b = System.Text.Encoding.UTF8.GetBytes(s); ms.Write([(byte)(b.Length >> 8), (byte)b.Length]); ms.Write(b); }
        ms.Write([0xFE, 0xED, 0xFE, 0xED]);
        Int(2);
        Int(entries.Length);
        foreach (var (alias, der) in entries)
        {
            Int(2);
            Utf(alias);
            ms.Write(new byte[8]);
            Utf("X.509");
            Int(der.Length);
            ms.Write(der);
        }
        ms.Write(new byte[20]); // Prüfsumme (wird nicht geprüft)
        return ms.ToArray();
    }
}
