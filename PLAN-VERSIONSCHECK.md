# Plan: Versionscheck

Stand: 07.11.2026 – **freigegeben und umgesetzt (V1–V6)**

## Ergebnis Probelauf (07.11.2026)

- Prüfung über die Oberfläche: 28 s; 6 Programme mit Update, 34 veraltete pip-Pakete.
- Python 3.13.0 → 3.13.17, 3.12.7 → 3.12.10, 3.10.11 aktuell mit Hinweis (3.10.22 nur als Quelltext),
  Hinweis auf 3.15.2; Git 2.53.0.windows.2 → 2.56.0.4.
- Notepad++, Java 8, PHP 8.4, .NET SDK, py-Launcher: identisch mit `winget list` von Hand.
- pip-Pakete aller drei Python-Versionen identisch mit `pip list --outdated` von Hand (31/1/2).
- PATH (Benutzer/System), Python-Registry, Einstellungen und 45.207 Dateien in den Python-/Git-Ordnern vorher/nachher gleich.

## Ziel

Auf Knopfdruck zeigt das Programm, was rund um Python installiert ist und wofür es eine neuere Version gibt.
**Es wird nur angezeigt, nichts verändert** (kein Update, keine PATH-Änderung, keine Installation).

## Festlegungen (geklärt)

- Umfang: Python- und Git-Versionen, pip-Pakete je Python-Version, Zusatzwerkzeuge,
  Editoren/IDEs sowie Skript- und Programmiersprachen, die mit Python zusammen genutzt werden.
- Nur anzeigen, mit Hinweis, wie man selbst aktualisiert.
- Prüfung nur auf Knopfdruck, kein Netzwerkzugriff ohne Zutun.

## Datenquellen

| Was | Installierte Version | Neueste Version |
|---|---|---|
| Python, Git | vorhandene Liste des Programms | python.org / GitHub (bereits eingebaut) |
| pip-Pakete | `python -m pip list --format=json` | `python -m pip list --outdated --format=json` |
| Werkzeuge, Editoren, Sprachen | `winget list` (ein Aufruf, ca. 1,5 s), ersatzweise Registry „Apps“ bzw. `<programm> --version` | `winget list` (Spalte „Verfügbar“); pip-basierte Werkzeuge über PyPI |

Ist winget nicht vorhanden, entfallen nur die Angaben „neueste Version“ aus winget; der Rest funktioniert weiter.

## Katalog (erweiterbar, nur Installiertes wird angezeigt)

- **Python-Werkzeuge:** py-Launcher, uv, pipx, Poetry, Conda/Miniforge, pyenv-win, ruff, pre-commit
- **Editoren/IDEs:** VS Code, Notepad++, PyCharm, Sublime Text, Visual Studio, Spyder, Thonny, Cursor
- **Sprachen/Laufzeiten:** Node.js, Java, .NET SDK, Rust, Go, R, Julia, Lua, Perl, PHP, Ruby, C++ Build Tools

Werkzeuge, die selbst Python-Versionen oder den PATH verwalten (uv, Conda, pyenv-win), bekommen einen Hinweis,
dass sie sich mit dem Umsortieren durch dieses Programm überschneiden können.

## Blöcke und Abnahmekriterien

| # | Block | Fertig, wenn … |
|---|---|---|
| V1 | Auswertung der winget-Ausgabe | Eine echte, gespeicherte `winget list`-Ausgabe dieses Rechners ergibt in Tests für Git, Notepad++, Python 3.12/3.13 die richtige installierte und verfügbare Version. Abgeschnittene Einträge („…“) werden als unsicher markiert, nie falsch zugeordnet. Deutsche und englische Spaltenköpfe funktionieren. |
| V2 | Katalog und Erkennung | Auf diesem Rechner werden u. a. Notepad++, Java 8, PHP 8.4 und das .NET SDK mit richtiger Version erkannt; nicht installierte Einträge erscheinen nicht. |
| V3 | Python/Git-Updates | 3.12.7 → 3.12.10 und 3.13.0 → 3.13.17 werden gemeldet; 3.10.11 gilt als aktuell mit dem Hinweis, dass es für 3.10 keine neuen Windows-Installer mehr gibt; Git 2.53.0.2 → 2.56.0.4. Neuere Hauptversionen (z. B. 3.15) erscheinen als eigener Hinweis. |
| V4 | pip-Pakete je Version | Für jede Python-Version: Anzahl Pakete und veraltete Pakete (alt → neu). Ergebnis stimmt mit `pip list --outdated` von Hand überein. Version ohne pip oder ohne Netz → verständlicher Hinweis statt Fehler. Zeitlimit pro Version. |
| V5 | Oberfläche | Neuer Reiter „Versionscheck“ mit Knopf „Nach Updates suchen“, Fortschritt und Abbrechen; gruppierte Liste (Kategorie, Name, installiert, neueste, Status, Hinweis zum Aktualisieren). Texte allgemein und einsteigerfreundlich. |
| V6 | Tests, Probelauf, Weitergabe | Unit-Tests für V1–V4 grün. Probelauf hier: Prüfung unter 2 Minuten; PATH, Registry und Programmdateien vorher/nachher unverändert (Vergleich). Ergebnisse stimmen mit `winget list` und `pip list --outdated` überein. Danach ZIP auf den Desktop, Commit nach Rückfrage. |

## Risiken

- **winget-Ausgabe ist Text, keine Datenstruktur:** Spalten werden über die Kopfzeile bestimmt, lange Namen werden
  gekürzt. Deshalb V1 mit echten Beispielausgaben und „lieber unsicher als falsch“.
- **pip-Abfrage braucht Netz und dauert** (je Version einige Sekunden): Fortschritt anzeigen, Abbrechen möglich.

## Nur notiert, nicht geplant

- Updates per Knopf ausführen
- Automatische Prüfung beim Start
- „Rückgängig“ nach dem Löschen einer Version stellt deren PATH-Einträge wieder her
