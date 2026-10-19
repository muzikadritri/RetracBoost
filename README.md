# Retrac Boost

Windows-Tool, das Latenz und Jitter deiner Verbindung misst, lokal verbessert und für **OGFN/Retrac** den Spielserver live auswertet.

**Starten:** `RetracBoost.exe` doppelklicken, UAC bestätigen (Netzwerkeinstellungen brauchen Administratorrechte).
Läuft ohne Installation auf Windows 10/11.

## Tab „Boost"
Live-Ping (ICMP, bei Cloud-Zielen automatisch TCP:443), Graph, Jitter, Verlust und fünf Optimierungen mit Ein-Klick-Boost:
Netzwerk-Stack, Netzwerkkarte, Energiesparplan, Spiel (QoS + CPU-Priorität), DNS. „Vorher/Jetzt" zeigt die tatsächlich gemessene Veränderung.

- **Ping-Booster:** UDP-Fastpath, UDP Receive Offload (Windows 11) aus, keine verzögerten ACKs, keine Netzwerkdrosselung, Spiele-Scheduler auf Echtzeit.
- **Netzwerkkarte:** Interrupt-Moderation und Interrupt-Verzögerungen, Paket-Bündelung, Energiesparen (EEE, Green Ethernet, Power Gating, „Gerät darf ausgeschaltet werden"), Flusssteuerung und WLAN-Roaming aus, RSS an. Gesetzt wird nur, was der Treiber selbst anbietet.
- **Energiesparplan:** am Desktop „Ultimative Leistung", am Notebook „Höchstleistung". Der angelegte Plan wird beim Zurücksetzen wieder gelöscht.
- **FPS-Booster:** NVIDIA-Profil (max. Leistung, Low Latency, VSync aus), Windows-Scheduler/HAGS/MPO-Fix, Spiel immer auf der Hochleistungs-GPU, Flip-Modell auch im randlosen Fenster, Multithreaded Rendering, DX11, Shader-Cache, Logs aus. Der **RAM-Wächter** leert während des Spiels den Standby-Cache, sobald weniger als 1 GB RAM frei ist.

## Tab „Werkzeuge"
| Karte | Was sie tut |
|---|---|
| Beste Region | misst alle AWS-Standorte gleichzeitig, Klick setzt das Live-Ziel |
| Verbindungs-Diagnose | Traceroute mit Namen, Heimnetz/Provider/Ziel-Zeiten, größter Sprung |
| Ping unter Last | Bufferbloat-Test (Download/Upload über Cloudflare), Note A+ bis F |
| Paketgröße (MTU) | findet die Pfad-MTU und kann die Karte darauf einstellen |
| Netzwerk reparieren | Caches leeren, Winsock zurücksetzen |
| System & Komfort | TCP-Feintuning, Hintergrund pausieren, Timer 0,5 ms, Mit Windows starten, Ping-Overlay, Auto-Boost |

## Tab „Games"
Acht Spiele mit ihrem offiziellen Bild: Minecraft, Counter-Strike 2, GTA V, Fortnite, VALORANT, Apex Legends, Rocket League, PUBG.
Steam-Spiele zeigen das Store-Bild vom Steam-CDN, die anderen das `og:image` der offiziellen Seite (minecraft.net, fortnite.com, playvalorant.com). Bilder werden 14 Tage zwischengespeichert.

Pro Spiel gibt es einzelne Schalter. Alles wirkt über Windows und die Dateien des Spiels, das laufende Spiel wird nicht angefasst (wichtig wegen VAC, Easy Anti-Cheat, BattlEye, Vanguard):
- **CPU-Priorität hoch** über Image File Execution Options (Windows setzt sie beim Start)
- **Grafikkarte: hohe Leistung** (Windows-Grafikeinstellung pro Exe, wirkt bei zwei GPUs)
- **Vollbildoptimierung aus**
- **Werte in der Konfigurationsdatei des Spiels**, z. B. VSync aus (CS2 `cs2_video.txt`, Fortnite/PUBG `GameUserSettings.ini`, GTA V `settings.xml`, Minecraft `options.txt`). Geändert werden nur Schlüssel, die das Spiel selbst angelegt hat, nie während es läuft. Ausschalten schreibt die alten Werte zurück.
- **Startoptionen** zum Kopieren (z. B. Apex `+fps_max 0`)

Spiele werden in Steam-, Epic- und Riot-Bibliotheken gesucht; sonst lässt sich die Exe selbst wählen.
Eigene Spiele kommen als **Plugin (ZIP)** dazu, ohne Code, nur mit den eingebauten Aktionen. Format: [docs/plugins.md](docs/plugins.md), Beispiel: `examples/plugins/rainbow-six-siege.zip`.

## Tab „Retrac"
- **Spielserver:** findet die IP des Match-Servers aus dem laufenden Spiel (UDP-Ports des Spiels + Absender der eingehenden Pakete), zeigt Betreiber/Standort, Ping und Route. Wird bei jedem Match automatisch neu erkannt.
- **Server-Lock (nur Frankfurt):** sperrt per Windows-Firewall ausgehendes UDP des Spiels zu **allen** AWS-Standorten in Europa außer Frankfurt (London, Irland, Paris, Stockholm, Mailand, Spanien, Zürich, Sovereign Cloud; alle Dienste, IPv4 + IPv6). Die Regel gilt nur für die Spiel-Exe. Der **Match-Server-Wächter** prüft jeden erkannten Match-Server: Steht er nachweislich nicht in Frankfurt (anderer AWS-Standort oder OVH-Rechenzentrum), wird seine Adresse **nach dem Match** zusätzlich gesperrt, nie mitten im Spiel.
- **Regionen:** die Retrac-Serverliste (Europa, NA Ost/West, Ozeanien, Naher Osten, Asien, South Africa; Brasilien hat keine Server), gemessen über Referenzpunkte.
- **Profil:** CPU-Priorität (Windows setzt sie beim Start, die App öffnet nie ein Handle auf das Spiel), QoS, **Potato-Grafik (wie EM Tweaks, kostenlos)** mit 5 Stufen, `GameUserSettings.ini` wird danach schreibgeschützt, damit das Spiel die Werte nicht zurücksetzt (Leicht `+1,0`, Mittel `+1,75`, Stark `+2,5`, Extrem `+3,0` – setzt direkt per `nvapi64.dll` im NVIDIA-Treiberprofil `Replay Mode All`, `Driver Controlled LOD Bias Off`, `DX LOD Bias` & `High Performance` sowie die passenden `Engine.ini`- und `GameUserSettings.ini`-Werte), Vollbild-Optimierung, Spielmodus, Mausbeschleunigung, Engine-Option `r.OneFrameThreadLag` (experimentell).

## Grundsatz
Zurücksetzen fasst **nur an, was die App selbst geändert hat**. Davor werden die Originalwerte gesichert (`C:\ProgramData\RetracBoost\state.ini`). Was bei dir schon so eingestellt war, bleibt unangetastet.

## Grenzen
Das Tool ändert nur, was lokal möglich ist. Es leitet keinen Verkehr über andere Server um (das macht ExitLag mit seinem Relay-Netz) und kann deshalb keinen schlechten Weg zum Spieleserver verkürzen.

## Selbsttest
```
RetracBoost.exe --selftest --out ergebnis.txt                        # nur lesend
RetracBoost.exe --selftest --apply --skip-adapter --out ergebnis.txt # anwenden, prüfen, zurücksetzen (Admin)
RetracBoost.exe --selftest --server --out server.txt                 # Spielserver des laufenden Matches (Admin)
RetracBoost.exe --screens ordner                                     # rendert die drei Seiten als PNG
```

## Selbst bauen
`RetracBoost\build.ps1` (benötigt das .NET SDK; Ziel ist .NET Framework 4.8).

## Server-Lock-Daten aktualisieren
AWS ändert seine IP-Bereiche regelmäßig. Vor einem Release neu erzeugen:
```
python tools/update_serverlock.py            # lädt ip-ranges.json direkt von AWS
python tools/update_serverlock.py --input aws-ranges.json
```
Danach neu bauen. Bestehende Installationen schreiben ihre Firewall-Regeln beim nächsten Start automatisch neu.
