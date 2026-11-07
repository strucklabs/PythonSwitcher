# PythonSwitcher

Kleines Windows-Programm (WPF, .NET 10) zum Verwalten mehrerer Python- und Git-Installationen:
**Hinzufügen, Entfernen, Aktivieren**.

## Funktionsweise

- **Aktivieren** sortiert die PATH-Einträge um: Die Einträge der gewählten Installation
  (bei Python der Ordner und `Scripts`, bei Git `cmd`) wandern geschlossen vor den ersten
  Eintrag einer anderen Installation. Alle übrigen Einträge behalten ihre Reihenfolge.
  Der PATH bleibt dadurch ein ganz normaler PATH und lässt sich weiterhin von Hand bearbeiten.
- Windows liest erst den **System-PATH**, dann den Benutzer-PATH. Steht eine andere Version im
  System-PATH, fragt das Programm nach und sortiert nur mit Zustimmung (UAC-Abfrage) auch dort um.
- Änderungen gelten für **neu gestartete** Terminals und Programme.
- Vor jeder Änderung wird der bisherige Wert gesichert
  (`%LOCALAPPDATA%\PythonSwitcher\Sicherungen\<Zeitstempel>\Benutzer.txt` / `System.txt`).
  „Letzte PATH-Änderung rückgängig“ spielt die jüngste Sicherung zurück.
- **Hinzufügen**: automatische Suche (PATH, Registry, übliche Ordner) oder Ordner wählen.
- **Herunterladen**: Python über den offiziellen Installer von python.org (still, nur für den
  aktuellen Benutzer, ohne py-Launcher, Verknüpfungen und Dateizuordnungen), Git als PortableGit
  von GitHub. Jede Datei wird per Prüfsumme (SHA-256, bei älteren Python-Versionen nur MD5)
  **und** digitaler Signatur geprüft. Neue Versionen werden im PATH hinter der aktiven eingetragen.
  Für eine Python-Hauptversion, die schon per Installer eingerichtet ist (z. B. 3.12), ist der
  Download gesperrt, weil der Installer sie sonst ersetzen würde.
- **Entfernen** trägt nur aus der Liste aus. Selbst heruntergeladene Versionen können nach
  Rückfrage gelöscht werden; fremde Installationen werden nie gelöscht.

## Aufbau

| Ordner | Inhalt |
|---|---|
| `src/PythonSwitcher.Core` | Logik: PATH-Planung (`PathPlanner`), Registry, Sicherungen, Suche, Downloads |
| `src/PythonSwitcher` | WPF-Oberfläche; mit `--system-path-schreiben <Datei>` gestartet schreibt sie als Admin-Hilfsprozess nur den System-PATH |
| `tests/PythonSwitcher.Tests` | Unit-Tests; arbeiten nur auf nachgebauten PATH-Werten, nie auf dem echten PATH |

```
dotnet test
dotnet run --project src/PythonSwitcher
```
