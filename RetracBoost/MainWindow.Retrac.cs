using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace RetracBoost
{
    public partial class MainWindow
    {
        bool wasGameRunning, detecting;
        DateTime lastDetect = DateTime.MinValue, lastRamCheck = DateTime.MinValue;
        ServerInfo lastServer;

        void LocalizeRetrac()
        {
            ServerTitle.Text = L.T("Spielserver", "Game server");
            ServerDesc.Text = L.T("Liest aus dem laufenden Match die Adresse deines Spielservers aus und misst sie. Das passiert automatisch, sobald du in einem Match bist.",
                                  "Reads your game server's address from the running match and measures it. This happens automatically once you are in a match.");
            ServerFind.Content = L.T("Server suchen", "Find server");
            ServerUse.Content = L.T("Live-Ping verwenden", "Use for live ping");
            ServerAddr.Text = "–";
            ServerStats.Text = L.T("Starte Retrac und tritt einem Match bei.", "Start Retrac and join a match.");

            OgRegionTitle.Text = L.T("Regionen", "Regions");
            OgRegionDesc.Text = L.T("So wie in der Retrac-Serverliste, gemessen über je einen Referenzpunkt in der Weltgegend (kann im Spiel um einige ms abweichen). Klick setzt das Live-Ziel.",
                                    "Same regions as the Retrac server list, measured via one reference point per area (may differ by a few ms from the in-game value). Click to set the live target.");
            OgRegionRun.Content = L.T("Regionen messen", "Measure regions");

            RetracNote.Text = L.T("CPU-Priorität, Potato-Grafik und Engine-Änderung gelten ab dem nächsten Spielstart. Die Netzwerkkarte verbindet beim ersten Anwenden kurz neu – am besten vor dem Spiel. Jede Zeile lässt sich einzeln zurücknehmen.",
                                  "CPU priority, potato graphics and the engine change apply from the next game launch. The adapter reconnects briefly on first apply – best before gaming. Every row can be undone individually.");
            PotatoHeader.Text = L.T("Potato-Grafik Stufe wählen (wie EM Tweaks, kostenlos)", "Choose potato graphics level (like EM Tweaks, free)");

            SetChip(ChipLauncherDot, ChipLauncherText, false, L.T("Retrac-Launcher läuft", "Retrac launcher running"), L.T("Retrac-Launcher nicht gestartet", "Retrac launcher not running"));
            SetChip(ChipGameDot, ChipGameText, false, L.T("Spiel läuft", "Game running"), L.T("Spiel nicht gestartet", "Game not running"));
            ChipBuildText.Text = L.T("Build unbekannt", "Build unknown");
        }

        void SetChip(System.Windows.Shapes.Ellipse dot, System.Windows.Controls.TextBlock text, bool on, string onText, string offText)
        {
            dot.Fill = on ? Res("Accent") : Res("Muted");
            text.Text = on ? onText : offText;
        }

        // ------------------------------------------------------------------ Spielpfad / Build

        void DetectOgfnPath()
        {
            string path = null;
            try
            {
                foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(OgfnExe)))
                {
                    using (p) { try { path = path ?? p.MainModule.FileName; } catch { } }
                }
            }
            catch { }

            if (path == null)
            {
                var saved = Store.Get("ogfn.path");
                if (saved != null && File.Exists(saved)) path = saved;
            }
            if (path == null)
            {
                try
                {
                    foreach (var name in Reg.ValueNames(@"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers", true))
                        if (name.EndsWith(OgfnExe, StringComparison.OrdinalIgnoreCase) && File.Exists(name)) { path = name; break; }
                }
                catch { }
            }

            if (path != null)
            {
                fso.ExePath = path;
                Store.TrySet("ogfn.path", path);
                var m = Regex.Match(path, @"(\d+\.\d+)-CL-\d+");
                string season = m.Success && m.Groups[1].Value.StartsWith("14.") ? " · Chapter 2 Season 4" : "";
                ChipBuildText.Text = m.Success ? "Build " + m.Groups[1].Value + season : L.T("Build unbekannt", "Build unknown");
            }
        }

        // ------------------------------------------------------------------ Beobachtung (alle 2 s)

        void WatchTick()
        {
            bool launcher = ProcessRunning(RetracProcess);
            bool ogfnRunning = ProcessRunning(Path.GetFileNameWithoutExtension(OgfnExe));
            bool genericRunning = game.Relevant && !string.Equals(game.Exe, OgfnExe, StringComparison.OrdinalIgnoreCase)
                                  && ProcessRunning(Path.GetFileNameWithoutExtension(game.Exe));

            SetChip(ChipLauncherDot, ChipLauncherText, launcher, L.T("Retrac-Launcher läuft", "Retrac launcher running"), L.T("Retrac-Launcher nicht gestartet", "Retrac launcher not running"));
            SetChip(ChipGameDot, ChipGameText, ogfnRunning, L.T("Spiel läuft", "Game running"), L.T("Spiel nicht gestartet", "Game not running"));

            bool anyGame = ogfnRunning || genericRunning;
            if (anyGame && !wasGameRunning) OnGameStarted(ogfnRunning);
            if (!anyGame && wasGameRunning) OnGameStopped();
            wasGameRunning = anyGame;

            // RAM-Wächter des FPS-Boosters: alle ~30 s prüfen, Standby-Cache nur bei knappem freien RAM leeren
            if (anyGame && rowFps.IsOn && Admin.IsElevated && DateTime.UtcNow - lastRamCheck > TimeSpan.FromSeconds(30))
            {
                lastRamCheck = DateTime.UtcNow;
                var _ram = Task.Run(() => RamCleaner.PurgeIfLow());
            }

            // Server automatisch erkennen: solange kein Server bekannt ist alle 10 s, danach alle 45 s (Server wechselt je Match)
            if (ogfnRunning && !detecting && Admin.IsElevated)
            {
                double wait = lastServer == null ? 10 : 45;
                if ((DateTime.UtcNow - lastDetect).TotalSeconds > wait) { var _ = DetectServerAsync(false); }
            }
        }

        static readonly Dictionary<string, Process> processCache = new Dictionary<string, Process>(StringComparer.OrdinalIgnoreCase);

        static bool ProcessRunning(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            try
            {
                Process cached;
                if (processCache.TryGetValue(name, out cached) && cached != null)
                {
                    try
                    {
                        if (!cached.HasExited) return true;
                    }
                    catch { }
                    try { cached.Dispose(); } catch { }
                    processCache.Remove(name);
                }

                var ps = Process.GetProcessesByName(name);
                if (ps.Length > 0)
                {
                    processCache[name] = ps[0];
                    for (int i = 1; i < ps.Length; i++) { try { ps[i].Dispose(); } catch { } }
                    return true;
                }
                return false;
            }
            catch { return false; }
        }

        async void OnGameStarted(bool ogfnRunning)
        {
            if (ogfnRunning)
            {
                DetectOgfnPath();                       // Pfad aus dem laufenden Prozess
                var _ = RefreshRowAsync(rowFso);
                var __ = RefreshRowAsync(rowEngine);
                var ___ = RefreshRowAsync(rowPotato);
                var ____ = RefreshRowAsync(rowFps);
                var _____ = RefreshServerLockAsync();  // Regel auf die nun bekannte Spiel-Exe einschränken
            }
            if (Admin.IsElevated)
            {
                if (ogfnRunning && rowOgfn.IsOn) GameTweak.BoostLiveProcess(OgfnExe);
                else if (!ogfnRunning && rowGame.IsOn) GameTweak.BoostLiveProcess(game.Exe);
                if (rowFps.IsOn) { var _ram = Task.Run(() => RamCleaner.PurgeStandbyList()); }
            }
            if (autoBoostTweak.IsApplied() && Admin.IsElevated && !sequence)
            {
                sequence = true;
                UpdateMaster();
                try
                {
                    var targets = new List<Row> { rowNet, rowPower, rowFps };
                    targets.Add(ogfnRunning ? rowOgfn : rowGame);
                    foreach (var r in targets.Where(r => !r.IsOn && r.Tweak.Relevant)) await RunOne(r, true);
                }
                finally { sequence = false; UpdateMaster(); }
            }
        }

        void OnGameStopped()
        {
            lastServer = null;
            ServerStatusReset();
            var _ = RefreshServerLockAsync();       // gelernte Server erst jetzt sperren - nie mitten im Match
        }

        /// <summary>Schreibt die Server-Lock-Regeln neu, wenn neue Server gelernt wurden, sich der Spielpfad oder die Datenbasis geändert hat.</summary>
        async Task RefreshServerLockAsync()
        {
            if (!Admin.IsElevated || sequence || rowServerLock.Busy) return;
            bool stale = await Task.Run(() => { try { return serverLock.NeedsRefresh; } catch { return false; } });
            if (!stale) return;
            await RunOne(rowServerLock, true);
            UpdateRetracMaster();
        }

        async Task RefreshRowAsync(Row row)
        {
            await gate.WaitAsync();
            try
            {
                bool applied = await Task.Run(() => { try { return row.Tweak.Relevant && row.Tweak.IsApplied(); } catch { return false; } });
                row.SetOn(applied);
                row.Detail = row.Tweak.Detail;
                row.RefreshEnabled();
            }
            finally { gate.Release(); }
            UpdateRetracMaster();
        }

        // ------------------------------------------------------------------ Spielserver

        void ServerStatusReset()
        {
            ServerAddr.Text = "–";
            ServerName.Text = "";
            ServerStats.Text = L.T("Spiel beendet. Beim nächsten Match wird der Server neu erkannt.", "Game closed. The server is detected again in the next match.");
            ServerUse.IsEnabled = false;
        }

        async void ServerFind_Click(object sender, RoutedEventArgs e)
        {
            await DetectServerAsync(true);
        }

        async Task DetectServerAsync(bool manual)
        {
            if (detecting) return;
            detecting = true;
            lastDetect = DateTime.UtcNow;
            ServerFind.IsEnabled = false;
            try
            {
                if (!Admin.IsElevated)
                {
                    ServerStats.Text = L.T("Dafür sind Administratorrechte nötig.", "This needs administrator rights.");
                    return;
                }

                var pids = new List<int>();
                foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(OgfnExe))) { pids.Add(p.Id); p.Dispose(); }
                if (pids.Count == 0)
                {
                    ServerStats.Text = L.T("Das Spiel läuft nicht. Starte Retrac und tritt einem Match bei.", "The game is not running. Start Retrac and join a match.");
                    return;
                }

                var nic = Nics.Primary(Nics.Active(false));
                if (nic == null || nic.Address == null)
                {
                    ServerStats.Text = L.T("Keine aktive Netzwerkverbindung.", "No active network connection.");
                    return;
                }

                var ports = await Task.Run(() => ServerFinder.UdpPortsOf(pids));
                if (ports.Count == 0)
                {
                    ServerStats.Text = L.T("Das Spiel hat noch keine Verbindung aufgebaut.", "The game has not opened a connection yet.");
                    return;
                }

                ServerStats.Text = L.T("Lausche auf Spielverkehr …", "Listening for game traffic …");
                var peers = await ServerFinder.Capture(nic.Address, ports, 3500);
                var peer = ServerFinder.PickServer(peers);
                if (peer == null)
                {
                    ServerStats.Text = L.T("Noch kein Match: In der Lobby gibt es keinen Spielserver. Tritt einem Spiel bei – dann wird er automatisch erkannt.",
                                           "No match yet: there is no game server in the lobby. Join a game and it is detected automatically.");
                    return;
                }

                ServerStats.Text = L.T("Server gefunden, messe …", "Server found, measuring …");
                var info = await ServerFinder.Describe(peer, CancellationToken.None);
                lastServer = info;
                var verdict = ServerLockData.Judge(info);
                // Nachweislich fremde Server merkt sich der Server-Lock; gesperrt wird erst nach dem Match.
                if (verdict.Spot == ServerSpot.Elsewhere && verdict.Sure && serverLock.WeApplied) ServerLockTweak.Learn(info.Ip);
                ShowServer(info, verdict);
            }
            catch (Exception ex)
            {
                ServerStats.Text = L.T("Server-Erkennung nicht möglich: ", "Server detection failed: ") + Shorten(ex.Message);
            }
            finally
            {
                detecting = false;
                ServerFind.IsEnabled = true;
            }
        }

        void ShowServer(ServerInfo s, ServerVerdict verdict)
        {
            Func<double, string> ms = v => v.ToString(v < 10 ? "0.0" : "0", CultureInfo.CurrentCulture) + " ms";

            ServerAddr.Text = s.Ip + ":" + s.Port;
            var name = new List<string>();
            var where = new List<string>();
            if (!string.IsNullOrEmpty(s.Provider)) where.Add(s.Provider);
            if (!string.IsNullOrEmpty(s.Location)) where.Add(s.Location);
            else if (!string.IsNullOrEmpty(s.RegionGuess)) where.Add(s.RegionGuess);
            if (where.Count > 0) name.Add(string.Join(" · ", where));
            if (!string.IsNullOrEmpty(s.HostName)) name.Add(s.HostName);
            ServerName.Text = string.Join("  ·  ", name);

            var lines = new List<string>();
            if (s.PingMs.HasValue)
                lines.Add(L.T("Ping zum Server: ", "Ping to server: ") + ms(s.PingMs.Value));
            else if (s.ViaHopMs.HasValue)
                lines.Add(L.T("Der Server antwortet nicht auf Ping. Letzter erreichbarer Router (", "The server does not answer ping. Last reachable router (")
                          + s.ViaHopAddress + "): " + ms(s.ViaHopMs.Value));
            else
                lines.Add(L.T("Der Server und die Zwischenstationen antworten nicht auf Ping.", "The server and hops do not answer ping."));

            var timed = s.Route.Where(h => h.Avg.HasValue).ToList();
            if (timed.Count > 0)
            {
                Hop jump = null; double by = 0, prev = 0;
                foreach (var h in timed)
                {
                    double d = h.Avg.Value - prev;
                    if (d > by) { by = d; jump = h; }
                    prev = h.Avg.Value;
                }
                string route = L.T("Route: ", "Route: ") + s.Route.Count + L.T(" Stationen", " hops");
                if (jump != null && by >= 15) route += L.T(", größter Sprung bei Station ", ", biggest jump at hop ") + jump.Ttl + " (+" + by.ToString("0", CultureInfo.CurrentCulture) + " ms)";
                lines.Add(route);
            }

            if (verdict.Spot == ServerSpot.Frankfurt)
                lines.Add(L.T("Standort: Frankfurt ✓", "Location: Frankfurt ✓") + (verdict.Sure ? "" : L.T(" (geschätzt)", " (estimated)")) + " · " + verdict.Reason);
            else if (verdict.Spot == ServerSpot.Elsewhere)
            {
                string what = L.T("Standort: NICHT Frankfurt ✗ · ", "Location: NOT Frankfurt ✗ · ") + verdict.Reason;
                if (!verdict.Sure) what += L.T(" · nur per Ping geschätzt, wird nicht gesperrt", " · estimated from ping only, not blocked");
                else if (serverLock.WeApplied) what += L.T(" · Server-Lock sperrt ihn nach diesem Match", " · server lock blocks it after this match");
                else what += L.T(" · Server-Lock einschalten, um solche Server zu sperren", " · turn on the server lock to block servers like this");
                lines.Add(what);
            }
            else
                lines.Add(L.T("Standort: unbekannt · ", "Location: unknown · ") + verdict.Reason);

            ServerStats.Text = string.Join("\n", lines);
            ServerUse.IsEnabled = s.PingMs.HasValue || s.ViaHopAddress != null;
        }

        void ServerUse_Click(object sender, RoutedEventArgs e)
        {
            var s = lastServer;
            if (s == null) return;
            SetTarget(s.PingMs.HasValue ? s.Ip.ToString() : s.ViaHopAddress);
            TabBoost.IsChecked = true;
        }

        // ------------------------------------------------------------------ Regionen

        async void OgRegionRun_Click(object sender, RoutedEventArgs e)
        {
            OgRegionRun.IsEnabled = false;
            OgRegionList.ItemsSource = null;
            OgRegionStatus.Text = L.T("Messe …", "Measuring …");
            try
            {
                var available = OgfnRegions.All.Where(r => r.HasServers).ToList();
                var results = await RegionTest.Run(available.Select(r => new KeyValuePair<string, string>(r.Name, r.Host)), 6,
                    (d, n) => { var _ = Dispatcher.BeginInvoke(new Action(() => OgRegionStatus.Text = d + " / " + n)); },
                    CancellationToken.None);

                double max = Math.Max(1, results.Where(r => r.Ms.HasValue).Select(r => r.Ms.Value).DefaultIfEmpty(1).Max());
                var best = results.FirstOrDefault(r => r.Ms.HasValue);
                var list = new List<ListRow>();
                foreach (var r in results)
                {
                    list.Add(new ListRow
                    {
                        Left = r.Name,
                        Right = r.Ms.HasValue ? "≈ " + r.Ms.Value.ToString("0", CultureInfo.CurrentCulture) + " ms" : "–",
                        Fraction = r.Ms.HasValue ? r.Ms.Value / max : 0,
                        BarBrush = r == best ? Res("Accent") : Res("BarIdle"),
                        Host = r.Host
                    });
                }
                foreach (var r in OgfnRegions.All.Where(x => !x.HasServers))
                    list.Add(new ListRow { Left = r.Name, Right = L.T("kein Server", "no server"), Dim = true, BarBrush = Res("BarIdle") });

                OgRegionList.ItemsSource = list;
                OgRegionStatus.Text = best == null
                    ? L.T("Keine Antwort", "No reply")
                    : L.T("Beste Region: ", "Best region: ") + best.Name;
            }
            catch (Exception ex) { OgRegionStatus.Text = Shorten(ex.Message); }
            finally { OgRegionRun.IsEnabled = true; }
        }

        // ------------------------------------------------------------------ Retrac-Boost

        Row[] RetracCore() { return new[] { rowNet, rowAdapter, rowPower, rowFps, rowOgfn, rowServerLock, rowFso, rowDvr, rowMouse }; }

        void UpdateRetracMaster()
        {
            if (RetracBoostButton == null) return;
            var mine = RetracCore();
            bool busy = sequence || mine.Any(r => r.Busy);
            var relevant = mine.Where(r => r.Tweak.Relevant).ToList();
            bool active = relevant.All(r => r.IsOn);
            bool anyOurs = relevant.Any(r => r.IsOn && r.Tweak.WeApplied);

            RetracBoostButton.Tag = null;
            if (busy)
            {
                RetracBoostButton.IsEnabled = false;
                RetracBoostButton.Content = L.T("Einen Moment …", "One moment …");
                return;
            }
            if (!Admin.IsElevated) { RetracBoostButton.IsEnabled = false; }

            if (active && anyOurs)
            {
                RetracBoostButton.IsEnabled = Admin.IsElevated;
                RetracBoostButton.Tag = "on";
                RetracBoostButton.Content = L.T("Retrac-Boost aktiv", "Retrac boost active");
                RetracHint.Text = L.T("Klicken, um nur deine Änderungen zurückzusetzen", "Click to undo only the changes made here");
            }
            else if (active)
            {
                RetracBoostButton.IsEnabled = false;
                RetracBoostButton.Content = L.T("Alles bereits optimal", "Already optimal");
                RetracHint.Text = L.T("Dein System war schon so eingestellt – es gab nichts zu ändern.", "Your system was already set up like this – nothing to change.");
            }
            else
            {
                RetracBoostButton.IsEnabled = Admin.IsElevated;
                RetracBoostButton.Content = L.T("Retrac-Boost starten", "Start Retrac boost");
                RetracHint.Text = L.T("Setzt alle fehlenden Standard-Einstellungen (Potato-Grafik & Experimentelles einzeln schaltbar)",
                                      "Applies all missing default settings (potato graphics & experimental toggled individually)");
            }
        }

        async void RetracBoost_Click(object sender, RoutedEventArgs e)
        {
            if (sequence) return;
            var mine = RetracCore();
            var relevant = mine.Where(r => r.Tweak.Relevant).ToList();
            bool active = relevant.All(r => r.IsOn);

            sequence = true;
            UpdateMaster();
            try
            {
                if (active)
                {
                    foreach (var r in relevant.Where(r => r.IsOn && r.Tweak.WeApplied).ToList())
                        await RunOne(r, false);
                }
                else
                {
                    foreach (var r in relevant.Where(r => !r.IsOn).ToList())
                        await RunOne(r, true);
                }
            }
            finally
            {
                sequence = false;
                UpdateMaster();
            }
        }

        // ------------------------------------------------------------------ Potato-Grafik Stufen-Popup

        void OpenPotatoPicker(FrameworkElement anchor = null)
        {
            if (anchor != null) PotatoPopup.PlacementTarget = anchor;
            else PotatoPopup.PlacementTarget = RetracRowList;
            var items = PotatoTweak.AllPresets;
            PotatoList.ItemsSource = items;
            PotatoList.SelectedItem = items.FirstOrDefault(p => string.Equals(p.Key, potato.PresetKey, StringComparison.OrdinalIgnoreCase));
            PotatoPopup.IsOpen = true;
        }

        async void PotatoItem_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var fe = sender as FrameworkElement;
            var item = fe != null ? fe.DataContext as PotatoPreset : null;
            if (item == null) return;
            e.Handled = true;
            PotatoPopup.IsOpen = false;
            await SetPotatoPreset(item.Key);
        }

        async Task SetPotatoPreset(string key)
        {
            if (sequence || rowPotato.Busy) return;
            potato.PresetKey = key;
            Store.TrySet("potato.preset", key);
            rowPotato.ActionText = potato.CurrentPreset.ActionLabel;
            rowPotato.Detail = potato.Detail;

            if (Admin.IsElevated && potato.Relevant)
                await RunOne(rowPotato, true);
        }
    }
}
