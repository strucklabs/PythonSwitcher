namespace PythonSwitcher.Core;

/// <summary>Hilfsfunktionen für einzelne PATH-Einträge und PATH-Zeichenketten.</summary>
public static class PathUtil
{
    /// <summary>Setzt Umgebungsvariablen ein (%USERPROFILE% usw.) und entfernt Leerraum und Anführungszeichen.</summary>
    public static string Expand(string entry)
    {
        var e = entry.Trim().Trim('"').Trim();
        return Environment.ExpandEnvironmentVariables(e);
    }

    /// <summary>Vergleichsform: ausgeschrieben, ohne abschließenden Backslash, klein geschrieben.</summary>
    public static string Normalize(string entry)
    {
        var e = Expand(entry);
        try
        {
            if (Path.IsPathFullyQualified(e))
                e = Path.GetFullPath(e);
        }
        catch (Exception)
        {
            // ungültiger Pfad: so vergleichen, wie er dasteht
        }
        e = e.TrimEnd('\\', '/');
        return e.ToLowerInvariant();
    }

    public static bool SameDirectory(string a, string b) => Normalize(a) == Normalize(b);

    /// <summary>True, wenn der Eintrag der Ordner selbst ist oder darunter liegt (mit Grenze am Backslash).</summary>
    public static bool IsUnder(string entry, string root)
    {
        var e = Normalize(entry);
        var r = Normalize(root);
        if (r.Length == 0)
            return false;
        return e == r || e.StartsWith(r + "\\", StringComparison.Ordinal);
    }

    /// <summary>Zerlegt einen PATH-Wert in Einträge; leere Einträge fallen weg, die Schreibweise bleibt erhalten.</summary>
    public static List<string> Split(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return [];
        return value.Split(';')
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }

    public static string Join(IEnumerable<string> entries) => string.Join(";", entries);
}
