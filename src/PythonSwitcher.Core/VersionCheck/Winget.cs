using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace PythonSwitcher.Core.VersionCheck;

/// <summary>Eine Zeile aus "winget list".</summary>
public sealed record WingetPackage(
    string Name,
    string Id,
    string? Version,
    string? Available,
    string? Source,
    bool Uncertain);

/// <summary>
/// Liest die Texttabelle von "winget list". Die Spalten werden über die Kopfzeile bestimmt.
/// Grundsatz: lieber als unsicher markieren als falsch zuordnen.
/// </summary>
public static class Winget
{
    private static readonly HashSet<string> AvailableHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "Verfügbar", "Available", "Disponible", "Disponibile", "Beschikbaar" };
    private static readonly HashSet<string> SourceHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "Quelle", "Source", "Origen", "Origine", "Bron" };

    public static List<WingetPackage> Parse(string output)
    {
        var lines = output.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Contains('\r') ? l[(l.LastIndexOf('\r') + 1)..] : l) // Fortschrittsanzeige entfernen
            .ToList();

        int header = -1;
        for (int i = 0; i + 1 < lines.Count; i++)
        {
            if (Regex.IsMatch(lines[i + 1].Trim(), "^-{10,}$") && lines[i].Trim().Length > 0)
            {
                header = i;
                break;
            }
        }
        if (header < 0)
            return [];

        var cols = Regex.Matches(lines[header], @"\S+").Select(m => (m.Index, m.Value)).ToList();
        if (cols.Count < 3)
            return [];

        int availableCol = -1, sourceCol = -1;
        for (int c = 3; c < cols.Count; c++)
        {
            if (AvailableHeaders.Contains(cols[c].Value)) availableCol = c;
            else if (SourceHeaders.Contains(cols[c].Value)) sourceCol = c;
        }
        if (availableCol < 0 && sourceCol < 0 && cols.Count == 5)
        {
            // Unbekannte Sprache: übliche Reihenfolge annehmen
            availableCol = 3;
            sourceCol = 4;
        }
        else if (availableCol < 0 && sourceCol < 0 && cols.Count == 4)
        {
            sourceCol = 3; // ohne Updates gibt es keine Spalte "Verfügbar"
        }

        var result = new List<WingetPackage>();
        for (int i = header + 2; i < lines.Count; i++)
        {
            var line = lines[i].TrimEnd();
            if (line.Length == 0)
                break; // Ende der Tabelle (danach folgen ggf. Hinweistexte)

            string Field(int c)
            {
                int start = cols[c].Index;
                if (start >= line.Length)
                    return "";
                int end = c + 1 < cols.Count ? Math.Min(cols[c + 1].Index, line.Length) : line.Length;
                return line[start..end].Trim();
            }

            bool uncertain = line.Contains('…');
            foreach (var (index, _) in cols.Skip(1))
            {
                if (index < line.Length && index > 0 && line[index - 1] != ' ')
                    uncertain = true; // Spalten verschoben (z. B. durch breite Zeichen)
            }

            var id = Field(1);
            if (id.Length == 0)
                continue;
            static string? Clean(string v) => v.Length == 0 || v.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
                || v.Equals("Unbekannt", StringComparison.OrdinalIgnoreCase) ? null : v;

            result.Add(new WingetPackage(
                Field(0),
                id,
                Clean(Field(2)),
                availableCol >= 0 ? Clean(Field(availableCol)) : null,
                sourceCol >= 0 ? Clean(Field(sourceCol)) : null,
                uncertain));
        }
        return result;
    }

    /// <summary>Ruft "winget list" auf. Liefert null, wenn winget nicht vorhanden ist oder nicht antwortet.</summary>
    public static async Task<List<WingetPackage>?> ListAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo("winget", "list --disable-interactivity --accept-source-agreements")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        try
        {
            using var p = Process.Start(psi);
            if (p is null)
                return null;
            var outTask = p.StandardOutput.ReadToEndAsync(ct);
            _ = p.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            try
            {
                await p.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception) { }
                ct.ThrowIfCancellationRequested();
                return null;
            }
            var list = Parse(await outTask);
            return list.Count > 0 ? list : null;
        }
        catch (Win32Exception)
        {
            return null; // winget nicht installiert
        }
    }
}
