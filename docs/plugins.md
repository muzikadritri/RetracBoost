# Spiele-Plugins für den Games-Tab

Ein Plugin fügt dem Games-Tab ein weiteres Spiel hinzu. Es ist eine ZIP-Datei mit einer `manifest.json` und optional einem Bild.
Im Plugin steht kein Code. Es beschreibt nur, welche der eingebauten Aktionen von Retrac Boost für das Spiel gelten sollen.

Installieren: Games-Tab → **Plugin hinzufügen (ZIP)**. Vor der Installation zeigt Retrac Boost jede Änderung, die das Plugin machen kann.
Die Installation selbst kopiert nur die Dateien nach `%LOCALAPPDATA%\RetracBoost\plugins\<id>\`. Am System ändert sich erst etwas, wenn der Nutzer einen Schalter einschaltet, und jeder Schalter lässt sich wieder ausschalten.

Ein fertiges Beispiel liegt in [`examples/plugins/rainbow-six-siege`](../examples/plugins/rainbow-six-siege/manifest.json), als ZIP in `examples/plugins/rainbow-six-siege.zip`.

## Aufbau der ZIP

```
mein-spiel.zip
├── manifest.json      Pflicht
├── cover.jpg          optional, PNG oder JPG, wird in manifest.json unter "image" genannt
└── README.md          optional, .txt und .md werden ignoriert
```

- Höchstens 16 Dateien, jede bis 8 MB, zusammen bis 16 MB, ZIP bis 20 MB.
- Keine Unterordner. Liegt alles in genau einem Ordner (ZIP vom Ordner statt vom Inhalt), wird dieser Ordner übersprungen.
- Andere Dateitypen (.exe, .dll, .ps1, .bat, ...) führen zur Ablehnung.

## manifest.json

```json
{
  "format": 1,
  "id": "mein-spiel",
  "name": "Mein Spiel",
  "version": "1.0.0",
  "author": "Dein Name",
  "publisher": "Studio",
  "anti_cheat": "Easy Anti-Cheat",
  "description": { "de": "Kurzer Text", "en": "Short text" },

  "steam_appid": 123456,
  "image_page": "https://www.mein-spiel.com/",
  "image": "cover.jpg",

  "exes": ["MeinSpiel.exe"],
  "paths": ["{steam}/Mein Spiel/Binaries/Win64/MeinSpiel.exe", "{epic}/Binaries/Win64/MeinSpiel.exe"],

  "optimizations": [
    { "type": "priority", "level": "high" },
    { "type": "io_priority" },
    { "type": "network_qos" },
    { "type": "gpu_high_performance" },
    { "type": "fullscreen_optimizations_off" },
    {
      "type": "config",
      "id": "vsync",
      "title": { "de": "VSync aus", "en": "VSync off" },
      "summary": "bUseVSync = False in GameUserSettings.ini",
      "file": "{localappdata}/MeinSpiel/Saved/Config/Windows/GameUserSettings.ini",
      "format": "ini",
      "set": [
        { "key": "bUseVSync", "value": "False" },
        { "section": "ScalabilityGroups", "key": "sg.ShadowQuality", "value": 0 }
      ]
    },
    { "type": "launch_options", "launcher": "steam", "value": "-dx11", "summary": "Startet mit DirectX 11" }
  ]
}
```

| Feld | Pflicht | Bedeutung |
|---|---|---|
| `format` | ja | immer `1` |
| `id` | ja | `a-z`, `0-9`, `-`, `_`, 2 bis 40 Zeichen. Darf nicht wie ein eingebautes Spiel heißen (`minecraft`, `cs2`, `gta5`, `fortnite`, `valorant`, `apex`, `rocketleague`, `pubg`). Gleiche id = Update des Plugins. |
| `name` | ja | Anzeigename, bis 60 Zeichen |
| `version`, `author`, `publisher`, `anti_cheat` | nein | werden in der Detailansicht angezeigt |
| `description` | nein | bis 300 Zeichen |
| `exes` | für Exe-Aktionen | Dateinamen der Spiel-Exe, bis 8 |
| `paths` | nein | wo die Exe liegen kann, siehe Platzhalter. Der Nutzer kann die Exe auch selbst wählen. |
| `optimizations` | ja | 1 bis 16 Einträge |

Texte (`name`, `description`, `title`, `summary`) dürfen ein einfacher String sein oder `{ "de": "...", "en": "..." }`. Angezeigt wird die Sprache von Windows, sonst die andere.

### Bild

Das Bild soll das offizielle Bild des Spiels sein. Drei Wege, geprüft in dieser Reihenfolge:

1. `image`: PNG/JPG im ZIP.
2. `steam_appid`: Store-Bild (`header.jpg`) vom Steam-CDN.
3. `image_page`: eine offizielle https-Seite des Spiels. Retrac Boost nimmt deren `og:image`, also das Bild, das der Hersteller selbst zum Teilen hinterlegt.

Geladene Bilder liegen 14 Tage im Cache (`%LOCALAPPDATA%\RetracBoost\images`). Ohne Bild zeigt die Karte die Anfangsbuchstaben.

### Platzhalter für Pfade

Jeder Pfad beginnt mit genau einem Platzhalter. Danach folgen Ordner, getrennt mit `/` oder `\`. `*` steht für einen beliebigen Ordnernamen (ganzer Abschnitt, z. B. für Konto-IDs). `..` ist nicht erlaubt.

| Platzhalter | Wird zu |
|---|---|
| `{steam}` | jeder Steam-Bibliotheksordner `...\steamapps\common` |
| `{steamroot}` | Steam-Installationsordner (z. B. für `userdata\*\<appid>\...`) |
| `{epic}` | jeder Installationsordner aus den Manifesten des Epic Games Launchers |
| `{riot}` | jeder Installationsordner aus den Metadaten des Riot Clients |
| `{drives}` | jedes lokale Laufwerk (`C:\`, `D:\`, ...) |
| `{programfiles}`, `{programfilesx86}` | Programme-Ordner |
| `{appdata}`, `{localappdata}` | `%APPDATA%`, `%LOCALAPPDATA%` |
| `{documents}` | Dokumente-Ordner (auch wenn OneDrive ihn verschoben hat) |
| `{savedgames}` | `%USERPROFILE%\Saved Games` |
| `{userprofile}` | `%USERPROFILE%` (nur für `paths`) |
| `{game}` | Ordner der gefundenen Spiel-Exe (nur für `config`) |

Ein Eintrag in `paths` muss auf eine Exe aus `exes` enden.

## Erlaubte Aktionen

Andere `type`-Werte lehnt Retrac Boost ab. Mit `id` (a-z, 0-9, `-`, `_`) bekommt eine Aktion einen festen Namen; dann bleiben ihre Sicherungen auch gültig, wenn sich die Reihenfolge in einem Update ändert.

### `priority`
CPU-Priorität über `Image File Execution Options\<exe>\PerfOptions\CpuPriorityClass`. Windows setzt sie selbst beim Start des Spiels.
`level`: `high` (Standard) oder `abovenormal`. Braucht Administratorrechte. Gilt für alle Einträge in `exes`.

### `io_priority`
Datenträger- und Speicherpriorität über `Image File Execution Options\<exe>\PerfOptions` (`IoPriority = 3`, `PagePriority = 5`). Das Spiel liest vor Hintergrundprogrammen von der Platte, seine Daten bleiben länger im RAM. Braucht Administratorrechte.

### `network_qos`
Windows-QoS-Regel, die alle Pakete der Exe mit DSCP 46 markiert. Wirkt, wenn der Router QoS oder WMM beachtet. Pro Exe eine Regel `RetracBoost-g-<id>-<n>`, beim Ausschalten wird sie wieder gelöscht. Braucht Administratorrechte.

### `gpu_high_performance`
Windows-Grafikeinstellung „Hohe Leistung“ (`HKCU\Software\Microsoft\DirectX\UserGpuPreferences`) für jede gefundene Exe. Wirkt bei Geräten mit zwei Grafikkarten.

### `fullscreen_optimizations_off`
Setzt das Kompatibilitäts-Flag `DISABLEDXMAXIMIZEDWINDOWEDMODE` für jede gefundene Exe.

### `config`
Ändert Werte in einer Konfigurationsdatei des Spiels.

- `file`: Pfadmuster, Platzhalter nur aus `{appdata}`, `{localappdata}`, `{documents}`, `{savedgames}`, `{steam}`, `{steamroot}`, `{epic}`, `{riot}`, `{game}`. Endung `.ini`, `.txt`, `.cfg`, `.xml` oder `.conf`. Passen mehrere Dateien (z. B. mehrere Steam-Konten), werden alle geändert, höchstens 16.
- `format`:
  - `ini`: `Schlüssel=Wert`, optional mit `section` (ohne Klammern). Ohne `section` zählt jede Sektion.
  - `kv`: Valve-Format `"Schlüssel" "Wert"` (Source-Spiele).
  - `colon`: `Schlüssel:Wert` (z. B. Minecraft `options.txt`). Stand der alte Wert in Anführungszeichen, bekommt der neue sie auch.
  - `xml`: Element mit `value`-Attribut, z. B. `<VSync value="0" />`.
- `set`: 1 bis 32 Einträge `{ "section"?, "key", "value" }`. `value` darf Text, Zahl oder true/false sein, bis 200 Zeichen, ohne Zeilenumbruch.

Regeln beim Anwenden:
- Geändert werden nur Schlüssel, die schon in der Datei stehen. Neue Zeilen legt Retrac Boost nie an. Steht keiner der Schlüssel drin, meldet der Schalter das.
- Läuft das Spiel (Prozessname aus `exes`), wird nichts geändert, weil das Spiel die Datei beim Beenden überschreiben würde.
- Der alte Wert jedes Schlüssels wird gesichert und beim Ausschalten zurückgeschrieben. Vor der ersten Änderung entsteht zusätzlich eine Kopie der ganzen Datei in `%LOCALAPPDATA%\RetracBoost\config-backup`.
- Kodierung (UTF-8, UTF-16, mit oder ohne BOM) und Zeilenenden der Datei bleiben erhalten.

### `launch_options`
Zeigt eine Startoption mit Kopieren-Knopf an. Retrac Boost trägt sie nicht selbst ein.
`launcher`: `steam`, `epic`, `ea`, `riot` oder `other` (bestimmt den Hinweis, wo man sie einträgt). `value`: die Option, bis 200 Zeichen.

## Was ein Plugin nicht kann

- Programme starten, Code oder Skripte ausführen, DLLs laden
- Dateien außerhalb der oben genannten Ordner ändern oder neue Dateien anlegen
- beliebige Registry-Werte setzen
- etwas im laufenden Spiel ändern (kein Speicherzugriff, kein Handle auf den Spielprozess, nichts was Anti-Cheat stören würde)

Optimierungen sollen echte, nachvollziehbare Einstellungen sein. Beschreib in `summary`, was der Wert tut, und versprich keine Zahlen.

## ZIP bauen

PowerShell, im Ordner des Plugins:

```powershell
Compress-Archive -Path manifest.json, cover.jpg -DestinationPath ..\mein-spiel.zip -Force
```

---

## English summary

A plugin is a ZIP with `manifest.json` and an optional PNG/JPG. It contains no code: it only lists which of Retrac Boost's built-in actions apply to a game (`priority`, `io_priority`, `network_qos`, `gpu_high_performance`, `fullscreen_optimizations_off`, `config`, `launch_options`). Paths must start with one of the placeholders above. Before installing, the app shows every change the plugin can make; nothing on the system changes until the user flips a switch, and every switch can be turned off again, restoring the saved values. Config edits only touch keys the game already wrote, never while the game runs.
