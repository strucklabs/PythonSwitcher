# Plan: Zertifikatsprüfung im Versionscheck

Stand: 25.11.2026 – **freigegeben und umgesetzt** (offene Fragen von Marco an Claude übergeben, entschieden siehe unten)

## Ergebnis (25.11.2026)

- Prüfung über die Oberfläche: Zertifikatsgruppe kostet kaum Zeit (Gesamtcheck 21–22 s, Zertifikate allein 0,7 s, parallel).
- Gefunden: **PHP 8.4 `problem`** (curl ohne Zertifikatsdatei), Python 3.10 Hinweis OpenSSL 1.1.1t,
  uv-Python 3.12.9: pips Zertifikate von 2024 (Hinweis ohne Befehl, weil von uv verwaltet).
- Git (schannel), Node.js, Java (107 Zertifikate, 5 abgelaufen – von Oracle mitgeliefert, kein Handlungsbedarf),
  Windows-Speicher (77, davon 14 abgelaufene Altlasten): in Ordnung.
- 178 Tests grün (20 neue in CertScanTests).
- Offen: Abnahmekriterium 1 zweiter Teil (php.ini von Hand ändern, dann „Check this entry again“) – macht Marco selbst.

## Ziel

Der Versionscheck zeigt zusätzlich, ob Python und die schon geprüften Programme (Git, Sprachen/Laufzeiten)
**sichere Verbindungen (HTTPS) prüfen können** und ob ihre Zertifikatssammlung veraltet ist.
Wie bisher gilt: **nur anzeigen**, mit Hinweis bzw. Befehl, wie man es selbst behebt. Nichts wird verändert.

## Hintergrund: Woher die Programme ihre Zertifikate nehmen

Jedes Programm prüft Server-Zertifikate gegen eine Liste vertrauenswürdiger Stammzertifikate. Es gibt drei Arten:

1. **Windows-Zertifikatsspeicher** – pflegt Windows Update automatisch. Veraltet praktisch nie.
2. **Eigene Datei** des Programms – wird nur mit dem Programm (oder Paket) aktualisiert. Kann veralten.
3. **Gar keine** – das Programm kann HTTPS-Server dann überhaupt nicht prüfen.

## Bestandsaufnahme auf dem Entwickler-PC (25.11.2026)

| Programm | Quelle | Befund |
|---|---|---|
| Python 3.10 / 3.12 / 3.13 – Standardbibliothek (`ssl`, `urllib`) | Windows-Speicher | in Ordnung |
| Python – Paket **certifi** (für `requests` u. a.) | eigene Datei im Paket | 3.10: 2026.11.20 (aktuell); 3.12/3.13 nicht installiert |
| Python – **pip** (eingebautes certifi) | eigene Datei in pip | 2026.10.23 in allen drei, kommt mit pip-Update |
| Python – eingebautes **OpenSSL** | Teil der Python-Version | 3.10: OpenSSL 1.1.1t (seit 2023 ohne Sicherheitsupdates), 3.12: 3.0.16, 3.13: 3.5.9 |
| Git 2.56.1 | `http.sslBackend = schannel` → Windows-Speicher | in Ordnung (eigene Datei ca-bundle.crt vom 26.10.2026 liegt bei, wird nicht benutzt) |
| Node.js 26.13.1 | fest eingebaute Liste (118 Stammzertifikate) | Verbindung zu pypi.org ok; Liste kommt mit Node-Update |
| Java 8 (jre1.8.0_513) | eigene Datei `lib\security\cacerts` | noch nicht ausgewertet (siehe Z4) |
| **PHP 8.4** | `curl.cainfo`/`openssl.cafile` **leer** | **curl-Verbindungen schlagen fehl** („unable to get local issuer certificate“); `file_get_contents` funktioniert |
| .NET SDK | Windows-Speicher | in Ordnung, nichts zu prüfen |
| Windows-Speicher selbst | – | 16 abgelaufene Zertifikate, alles gewollte Altlasten von Microsoft (nicht löschen) |

Der PHP-Befund ist ein echter Fehler, den man sonst erst bemerkt, wenn z. B. ein PHP-Skript mit curl scheitert.

## Bausteine

### Z1 – Gruppe „Certificates“ im Versionscheck

Neue Gruppe in der Liste, eine Zeile je geprüfter Quelle (nur für Installiertes):
Name, Installed (Stand/Datum der Zertifikatssammlung), Latest (falls bestimmbar), Status, Hint.
Status: `up to date`, `update available`, `note`, `problem` (neu, rot – Verbindung kann nicht geprüft werden).
Kontextmenü wie bei den anderen Einträgen (erneut prüfen, Befehl kopieren, festhalten).

### Z2 – Python (je Python-Version)

- **certifi**: installierte Version (Datum) und neueste von PyPI; Update-Befehl wie bei pip-Paketen.
  Nicht installiert → keine Zeile.
- **pip-eigenes certifi**: Version anzeigen; Hinweis „updated together with pip“, kein eigener Befehl.
- **OpenSSL der Python-Version**: Version anzeigen; bei OpenSSL 1.1.x Hinweis „no longer receives security
  updates – only a newer Python version brings a newer OpenSSL“.
- **Verbindungstest**: `ssl`-Standardkontext und – falls certifi installiert – certifi gegen
  `https://pypi.org` (nur Verbindungsaufbau, keine Daten). Fehler → `problem` mit der Meldung.
- **Umgebungsvariablen/Einstellungen**, die die Zertifikate umlenken (`SSL_CERT_FILE`, `REQUESTS_CA_BUNDLE`,
  `PIP_CERT`, pip-Einstellung `cert`): anzeigen, falls gesetzt; zeigen sie auf eine fehlende Datei → `problem`.

### Z3 – Git

- Welches Verfahren (`http.sslBackend`): `schannel` → „uses the Windows certificate store“ (`up to date`).
- `openssl` → benutzte Datei (`http.sslCAInfo` bzw. mitgelieferte ca-bundle.crt) mit Datum;
  älter als die mitgelieferte Datei der installierten Git-Version oder fehlt → Hinweis.
- Verbindungstest mit `git ls-remote https://github.com/git/git HEAD` (nur Lesen, kein Klonen).

### Z4 – Java

- `lib\security\cacerts` der installierten Java-Versionen direkt einlesen (Dateiformat JKS/PKCS12, Einträge
  mit vertrauenswürdigen Zertifikaten sind ohne Kennwort lesbar – **kein Kennwort wird eingegeben**).
- Anzahl der Zertifikate, wie viele abgelaufen sind, Datum des jüngsten Eintrags.
  Abgelaufene → Hinweis „updated together with Java“ (Update-Befehl wie bei Java).

### Z5 – Node.js

- Anzahl der eingebauten Stammzertifikate und Verbindungstest gegen pypi.org.
- Gesetzte Umlenkungen anzeigen (`NODE_EXTRA_CA_CERTS`, npm-Einstellung `cafile`), fehlende Datei → `problem`.
- Hinweis: Liste kommt mit Node-Update; ab Node 22.15/23.8 kann `NODE_USE_SYSTEM_CA=1` den Windows-Speicher nutzen.

### Z6 – PHP

- `curl.cainfo` und `openssl.cafile` aus der geladenen php.ini, Verbindungstest mit curl gegen pypi.org.
- Leer und Test schlägt fehl → `problem` mit Anleitung: CA-Datei (z. B. `cacert.pem` von curl.se) neben
  php.exe ablegen und in php.ini `curl.cainfo` und `openssl.cafile` darauf setzen. **Nur Anleitung,
  das Programm ändert die php.ini nicht.**

### Z7 – Übrige Sprachen und der Windows-Speicher

- .NET, Go, Rust/cargo, R: nutzen den Windows-Speicher → keine eigene Zeile (Erwähnung in der README).
- Ruby, Perl, Lua, Julia: nur falls ohne großen Aufwand prüfbar (Verbindungstest); sonst nicht im Umfang.
- Windows-Speicher: eine Zeile `note` mit Anzahl abgelaufener Stammzertifikate und der Erklärung,
  dass das gewollte Altlasten sind. **Kein Lösch-Befehl.**

## Abgrenzung

- Keine Änderungen an Dateien, php.ini, Git-Einstellungen, Umgebungsvariablen oder am Windows-Speicher.
- Keine Prüfung eigener Zertifikate (Code-Signatur, Client-Zertifikate in „Eigene Zertifikate“).
- Verbindungstests nur zu pypi.org bzw. github.com, nur beim Knopfdruck „Check for updates“.

## Abnahmekriterien

1. Auf dem Entwickler-PC erscheint PHP als `problem` mit verständlicher Anleitung; nach Setzen von
   `curl.cainfo` von Hand und „Check this entry again“ als `up to date`.
2. certifi in Python 3.10 erscheint mit Version und Status; in 3.12/3.13 keine certifi-Zeile.
3. Python 3.10 zeigt den OpenSSL-1.1-Hinweis, 3.12/3.13 nicht.
4. Git zeigt „uses the Windows certificate store“ und einen erfolgreichen Verbindungstest.
5. Java zeigt Anzahl und abgelaufene Einträge von cacerts – ohne Kennworteingabe.
6. Ohne Internet: Verbindungstests ergeben `unknown` mit Hinweis, nicht `problem`.
7. Der Versionscheck dauert durch die Tests höchstens ca. 10 s länger (Tests parallel, Zeitlimit je Test).
8. Vorher/nachher: PATH, Registry, php.ini, Git-Einstellungen und Einstellungen des Programms unverändert.
9. Unit-Tests für alle Auswertungen (Ausgaben fest hinterlegt), alle bisherigen Tests grün.

## Entscheidungen (25.11.2026)

- **Ruby, Perl, Lua, Julia: nicht im Umfang.** Keines davon ist auf dem Entwickler-PC installiert, eine Prüfung ließe
  sich also nicht an echten Installationen testen; Perl und Lua können HTTPS ohne Zusatzmodule ohnehin nicht.
  Bei Bedarf später nachrüsten (wie Z5/Z6: Verbindungstest über das Programm selbst).
- **Windows-Speicher: eine Zeile.** Die ursprüngliche Frage war gerade „Gibt es veraltete Zertifikate?“ –
  die Zeile beantwortet das direkt (Status `up to date`, Anzahl abgelaufener Altlasten mit Erklärung, kein Lösch-Befehl).
  Die README erklärt es zusätzlich.
- Umlenkungen per Umgebungsvariable (`SSL_CERT_FILE`, `SSL_CERT_DIR`, `REQUESTS_CA_BUNDLE`, `CURL_CA_BUNDLE`,
  `PIP_CERT`, `NODE_EXTRA_CA_CERTS`, `GIT_SSL_CAINFO`) erscheinen gesammelt, nur wenn gesetzt.
- Alter von Zertifikatssammlungen (certifi in pip, eigene CA-Dateien): älter als 12 Monate → Hinweis.
