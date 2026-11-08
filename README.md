# PythonSwitcher

Kleines Windows-Programm (WPF, .NET 10) zum Verwalten mehrerer Python- und Git-Installationen:
**Hinzufügen, Entfernen, Aktivieren**. Die Oberfläche ist englisch; diese Entwickler-Doku, Code-Kommentare
und Commit-Nachrichten sind deutsch.

## Funktionsweise

- **Aktivieren** sortiert die PATH-Einträge um: Die Einträge der gewählten Installation
  (bei Python der Ordner und `Scripts`, bei Git `cmd`) wandern geschlossen vor den ersten
  Eintrag einer anderen Installation. Alle übrigen Einträge behalten ihre Reihenfolge.
  Der PATH bleibt dadurch ein ganz normaler PATH und lässt sich weiterhin von Hand bearbeiten.
- Windows liest erst den **System-PATH**, dann den Benutzer-PATH. Steht eine andere Version im
  System-PATH, fragt das Programm nach und sortiert nur mit Zustimmung (UAC-Abfrage) auch dort um.
- Änderungen gelten für **neu gestartete** Terminals und Programme.
- Vor jeder Änderung wird der bisherige Wert gesichert
  (`%LOCALAPPDATA%\PythonSwitcher\Backups\<Zeitstempel>\User.txt` / `System.txt`).
  „Undo last PATH change“ spielt die jüngste Sicherung zurück; Einträge für inzwischen gelöschte
  Ordner können dabei weggelassen werden. „Backups to keep“ (Standard 50) begrenzt die Anzahl.
- Daten älterer Versionen (`einstellungen.json`, `Sicherungen\`, `Benutzer.txt`) werden beim Start
  automatisch auf die englischen Namen umgezogen bzw. weiter gelesen.
- **Hinzufügen**: automatische Suche (PATH, Registry, übliche Ordner, von uv verwaltete Pythons unter
  `%APPDATA%\uv\python` bzw. `UV_PYTHON_INSTALL_DIR`) oder Ordner wählen. uv-Pythons (Herkunft „uv“) werden nie gelöscht.
- Reiter Python zeigt, was der Befehl **`py`** startet (PY_PYTHON → `%LOCALAPPDATA%\py.ini` → `py.ini` neben der
  benutzten `py.exe` → neueste Version); `py` folgt nicht der PATH-Reihenfolge.
- **Entfernen** sucht vorher im Benutzerordner nach virtuellen Umgebungen (`pyvenv.cfg`, `home` = diese Version;
  max. 6 Ebenen, 15 s, ohne AppData/node_modules/.git) und warnt vor ihnen.
- **Herunterladen**: Python über den offiziellen Installer von python.org (still, nur für den
  aktuellen Benutzer, ohne py-Launcher, Verknüpfungen und Dateizuordnungen), Git als PortableGit
  von GitHub. Jede Datei wird per Prüfsumme (SHA-256, bei älteren Python-Versionen nur MD5)
  **und** digitaler Signatur geprüft. Neue Versionen werden im PATH hinter der aktiven eingetragen.
  Für eine Python-Hauptversion, die schon per Installer eingerichtet ist (z. B. 3.12), ist der
  Download gesperrt, weil der Installer sie sonst ersetzen würde.
- **Entfernen** trägt nur aus der Liste aus. Selbst heruntergeladene Versionen können nach
  Rückfrage gelöscht werden; fremde Installationen werden nie gelöscht.

- **Versionscheck** (eigener Reiter, nur auf Knopfdruck, verändert nichts): Python- und Git-Versionen gegen
  python.org/GitHub, pip-Pakete je Python-Version (`pip list --outdated`), Python-Werkzeuge, Editoren und
  Sprachen aus einem erweiterbaren Katalog (`VersionCheck/Catalog.cs`) über `winget list`, PATH und PyPI.
  Zu jedem Update ein Hinweis, wie man selbst aktualisiert. Python-/Git-Versionen werden bei jeder Prüfung frisch
  ausgelesen. Kontextmenü je Zeile: „Check this entry again“ (Einzelprüfung, z. B. nach einem Update) und
  „Copy update command“ (nur der Befehl; bei der pip-Übersicht alle veralteten Pakete). Plan und Abnahme: `PLAN-VERSIONSCHECK.md`.

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

## Weitergabe-Paket

```
dotnet publish src/PythonSwitcher -p:PublishProfile=Dist
```

Baut eine eigenständige Einzeldatei-EXE (win-x64, läuft ohne installiertes .NET) und legt
`PythonSwitcher.zip` (Ordner `PythonSwitcher\` mit EXE, `README.txt` und `LICENSE`) **auf dem Desktop** ab;
eine vorhandene ZIP wird überschrieben.

## Lizenz

PythonSwitcher steht unter der MIT-Lizenz, siehe [`LICENSE`](LICENSE).
