# AI Hub für PowerToys Run

PowerToys-Run-Plugin für Gemini über Antigravity CLI, Claude Code und Codex CLI. Version 0.5.2 zeigt Status und Antworten direkt unter der PowerToys-Eingabe. Es öffnet kein separates Chatfenster.

Die vorhandenen Konto-Anmeldungen der lokalen CLIs werden verwendet. Das Plugin benötigt keine eigenen API-Schlüssel und wechselt nicht automatisch auf API-Abrechnung.

## Getesteter Stand vom 17. Oktober 2026

- PowerToys: `0.101.2362.0`
- .NET SDK: `10.0.402`
- Build: `net10.0-windows10.0.22621.0`, x64
- Node.js: `24.14.1`
- Antigravity CLI: `1.4.0`
- Claude Code: `2.1.302`
- Codex CLI: `0.150.1`
- Installation: `%LOCALAPPDATA%\Microsoft\PowerToys\PowerToys Run\Plugins\AIHub`
- PowerToys: AI Hub `0.5.2.0`, aktiviert, erfolgreich geladen

Erfolgreich geprüft:

- Build gegen die echte lokale `Wox.Plugin.dll`: 0 Warnungen, 0 Fehler.
- Vier Bridge-Unit-Tests bestanden.
- 1.000 PowerToys-Abfragen starteten vor Enter keinen Modellprozess.
- Echter Inline-Paralleltest: drei erfolgreiche Antworten und eine gemeinsame Rückfragezeile.
- Gemini/Antigravity, Claude-Abo und Codex/ChatGPT wurden real aufgerufen.
- Unicode, Textkontext für Rückfragen und Beenden ganzer Prozessbäume sind implementiert.

Die PowerToys-Oberfläche konnte in der Automationssitzung nicht visuell angeklickt werden. PowerToys hat die installierte DLL jedoch geladen; der komplette Inline-Ablauf wurde mit der echten Plugin-API-Oberfläche und echten Modellaufrufen ausgeführt.

## Bedienung

Alt+Leertaste drücken und eingeben:

| Eingabe | Anbieter |
|---|---|
| `@gemini Erkläre mir Normalisierung` | Gemini über Antigravity |
| `@claude Formuliere diese Nachricht um` | Claude |
| `@codex Erkläre diesen Code` | Codex |
| `@ all Erkläre mir Normalisierung` | Alle drei parallel |

`@all Erkläre mir Normalisierung` funktioniert ebenfalls.

Während des Tippens erscheint nur **Senden**. Erst Enter startet die Anfrage. PowerToys Run bleibt offen und aktualisiert die Ergebnisse:

- Einzelmodus: eine Antwortzeile.
- Parallelmodus: je eine Zeile für Gemini, Claude und Codex.
- Während der Anfrage: zusätzliche Zeile **Abbrechen**.
- Nach Abschluss: zusätzliche Zeile **Rückfrage stellen**. Sie übernimmt den bisherigen Kontext.
- Enter auf einer Antwort kopiert den vollständigen Text. Darüberfahren zeigt den Volltext als Tooltip.

PowerToys Run kann Ergebnisse nur als Liste darstellen. Eine echte Drei-Spalten-Ansicht und lange Fließtexte im Hauptbereich sind dort nicht möglich. Deshalb werden lange Antworten in der Zeile gekürzt; der vollständige Text bleibt im Tooltip und in der Zwischenablage verfügbar. Token-Streaming wird nicht angezeigt.

## Anmeldung

Gemini CLI unterstützt seit dem 18. Juni 2026 keine privaten Google-AI-Pro-/Ultra-Konten mehr. AI Hub verwendet deshalb Antigravity CLI:

```powershell
agy
agy -p "Antworte nur mit OK" --output-format json --mode plan --sandbox
```

Claude und Codex prüfen:

```powershell
claude auth status
codex login status
```

Erwartet: Claude `authMethod: "claude.ai"`; Codex `Logged in using ChatGPT`.

Die Bridge entfernt vorhandene API-Schlüssel, alternative Basis-URLs und Cloud-Provider-Variablen ausschließlich aus den Kindprozessen. Globale Umgebungsvariablen werden nicht verändert. Antigravity läuft im Plan-Modus mit Sandbox, Claude ohne Tools/MCP/Chrome/Sitzungsablage, Codex in einer Read-only-Sandbox ohne Sitzungsablage.

## Installieren

PowerToys vollständig beenden, dann:

```powershell
.\Build.ps1 -Install
```

Das Skript installiert den mitgelieferten, auf diesem Rechner getesteten v0.5-Build. Danach PowerToys starten. AI Hub muss unter **PowerToys Run → Plugins** aktiviert sein. AI Hub verwendet `@`. Um die Aktivierungskollision zu vermeiden, ändert das Installationsskript das direkte Aktivierungswort des eingebauten PowerToys-Plugins von `@` zu `pt`, sofern es noch `@` verwendet.

Aus dem aktuellen Quellcode bauen:

```powershell
.\Build.ps1
```

Ausgabe: `build\AIHub`. Neu kompilierten Build installieren:

```powershell
.\Build.ps1 -Install -CompileFromSource
```

## Tests

```powershell
node --test bridge.test.mjs
& 'C:\Program Files\dotnet\dotnet.exe' run --project .\tests\QuerySmokeTest\QuerySmokeTest.csproj -c Release
```

Echter Inline-Test aller drei Anbieter:

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' run --project .\tests\QuerySmokeTest\QuerySmokeTest.csproj -c Release -- --live
```

## ZIP-Inhalt

Das Archiv enthält Quellcode, Tests und `AIHub\dist\AIHub` mit dem installierbaren v0.5-Build.

## Offizielle Referenzen

- [PowerToys-Run-Plugins](https://github.com/microsoft/PowerToys/blob/main/doc/thirdPartyRunPlugins.md)
- [PowerToys-Plugin-Checkliste](https://github.com/microsoft/PowerToys/blob/main/doc/devdocs/modules/launcher/new-plugin-checklist.md)
- [Ende der Gemini-CLI-Nutzung für private Konten](https://developers.google.com/gemini-code-assist/docs/deprecations/code-assist-individuals?hl=en)
- [Migration zu Antigravity CLI](https://antigravity.google/docs/cli/gcli-migration)
- [Antigravity-Headless-Modus](https://antigravity.google/docs/cli/headless)
- [Claude-Code-Authentifizierung](https://code.claude.com/docs/en/authentication)
- [Codex-Authentifizierung](https://developers.openai.com/codex/auth/)
