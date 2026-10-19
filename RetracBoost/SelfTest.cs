using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RetracBoost
{
    /// <summary>
    /// Headless-Prüfung der echten Funktionen (kein Fenster). Schreibt ein Protokoll in eine Datei.
    ///   --selftest [--out datei]                nur lesend: Adapter, Ping, DNS, Werkzeuge, Zustand der Tweaks
    ///   --selftest --apply [--skip-adapter]     zusätzlich: jeden Tweak anwenden, prüfen, zurücksetzen, prüfen (Admin)
    ///   --load                                  zusätzlich: Ping-unter-Last-Test (belastet die Leitung kurz)
    /// </summary>
    static class SelfTest
    {
        static StringBuilder log = new StringBuilder();
        static int failures;

        static void Line(string s) { log.AppendLine(s); }
        static void Check(bool ok, string what) { Line((ok ? "  PASS  " : "  FAIL  ") + what); if (!ok) failures++; }

        public static int Run(string[] args)
        {
            string outPath = Path.Combine(Path.GetTempPath(), "retracboost-selftest.txt");
            int oi = Array.IndexOf(args, "--out");
            if (oi >= 0 && oi + 1 < args.Length) outPath = args[oi + 1];
            bool apply = args.Contains("--apply");
            bool skipAdapter = args.Contains("--skip-adapter");
            bool load = args.Contains("--load");
            int oo = Array.IndexOf(args, "--only");
            var only = oo >= 0 && oo + 1 < args.Length ? new HashSet<string>(args[oo + 1].Split(','), StringComparer.OrdinalIgnoreCase) : null;
            string tempDir = Path.Combine(Path.GetTempPath(), "retracboost-selftest");

            if (args.Contains("--server")) return RunServerProbe(outPath);
            if (args.Contains("--repair")) return RunRepair(outPath);

            try
            {
                Directory.CreateDirectory(tempDir);
                Line("Retrac Boost selftest  " + DateTime.Now.ToString("s"));
                Line("OS " + Environment.OSVersion.VersionString + " | elevated=" + Admin.IsElevated + " | apply=" + apply);

                // ---------------------------------------------------------------- Adapter
                Line("");
                Line("[Adapter]");
                var nics = Nics.Active();
                foreach (var n in nics)
                    Line("  " + n.Name + " | " + n.Description + " | physical=" + n.Physical + " | ip=" + n.Address + " | dns=" + string.Join(",", n.Dns.Select(d => d.ToString())));
                Check(nics.Count > 0, "mindestens ein aktiver Adapter mit Gateway");
                var primary = Nics.Primary(nics);
                Line("  primär (Route ins Internet): " + (primary == null ? "-" : primary.Name));
                Check(primary != null && primary.Physical && primary.Address != null, "primärer Adapter ist eine echte Netzwerkkarte mit IPv4");

                var dns = new DnsTweak();
                DnsPlan plan = null;
                if (only == null || only.Contains("dns"))
                {
                    if (only == null)
                    {
                        // ---------------------------------------------------------------- Ping
                        Line("");
                        Line("[Ping]");
                        foreach (var host in new[] { "1.1.1.1", "ec2.eu-central-1.amazonaws.com" })
                        {
                            var samples = new List<PingSample>();
                            ProbeMode mode = ProbeMode.Auto;
                            var done = new ManualResetEvent(false);
                            var p = new Pinger();
                            p.StateChanged += (h, m, ip) => mode = m;
                            p.Sampled += s => { lock (samples) { samples.Add(s); if (samples.Count >= 4) done.Set(); } };
                            p.Start(host);
                            bool got = done.WaitOne(15000);
                            p.Stop();
                            var st = Stats.From(samples);
                            Line("  " + host + " -> mode=" + mode + " replies=" + st.Replies + "/" + st.Count + " avg=" + st.Avg.ToString("0.0") + " ms");
                            Check(got && st.Replies >= 2 && mode != ProbeMode.Auto, "Ping " + host);
                        }
                    }

                    // ---------------------------------------------------------------- DNS
                    Line("");
                    Line("[DNS-Messung]");
                    plan = dns.Benchmark();
                    foreach (var r in plan.All)
                        Line("  " + r.Name.PadRight(28) + (r.Score.HasValue ? r.Score.Value.ToString("0.0") + " ms" : "keine Antwort"));
                    Line("  Entscheidung: " + (plan.Switch ? "wechseln zu " + plan.BestProvider.Name : "aktuellen DNS behalten"));
                    Check(plan.All.Count(r => r.Score.HasValue) >= 2, "mindestens zwei Resolver beantworten Abfragen");
                }

                if (only == null)
                {
                    // ---------------------------------------------------------------- Werkzeuge (nur Messungen)
                    Line("");
                    Line("[Regionstest]");
                    var regs = RegionTest.Run(OgfnRegions.All.Where(r => r.HasServers).Select(r => new KeyValuePair<string, string>(r.Name, r.Host)),
                                              3, null, CancellationToken.None).GetAwaiter().GetResult();
                    foreach (var r in regs) Line("  " + r.Name.PadRight(22) + (r.Ms.HasValue ? r.Ms.Value.ToString("0") + " ms" : "-"));
                    Check(regs.Count(r => r.Ms.HasValue) >= 5, "mindestens fünf Regionen messbar");
                    var europe = regs.FirstOrDefault(r => r.Name == OgfnRegions.All[0].Name);
                    Check(europe != null && europe.Ms.HasValue && europe.Ms.Value < 120, "Europa liegt unter 120 ms (Plausibilität)");

                    Line("");
                    Line("[Traceroute -> 1.1.1.1]");
                    var hops = Traceroute.Run(IPAddress.Parse("1.1.1.1"), 20, 2, CancellationToken.None).GetAwaiter().GetResult();
                    foreach (var h in hops)
                        Line("  " + h.Ttl.ToString().PadLeft(2) + "  " + (h.Avg.HasValue ? h.Avg.Value.ToString("0.0").PadLeft(6) + " ms" : "     *   ") + "  " + h.Label + (h.IsPrivate ? "  [privat]" : ""));
                    Check(hops.Count >= 2 && hops.Last().Reached, "Route erreicht das Ziel");
                    Check(hops.Count > 0 && hops[0].IsPrivate, "erster Hop ist der Router im Heimnetz");

                    Line("");
                    Line("[MTU]");
                    var mtuPath = MtuTool.FindPathMtu(IPAddress.Parse("1.1.1.1"), CancellationToken.None).GetAwaiter().GetResult();
                    Line("  Pfad-MTU: " + (mtuPath.HasValue ? mtuPath.Value.ToString() : "-"));
                    Check(mtuPath.HasValue && mtuPath.Value >= 576 && mtuPath.Value <= 1500, "Pfad-MTU liegt im gültigen Bereich");
                }

                if (load)
                {
                    Line("");
                    Line("[Ping unter Last]");
                    var lr = LoadTest.Run(p => Line("  Phase: " + p), 5, CancellationToken.None).GetAwaiter().GetResult();
                    Line("  Leerlauf " + lr.IdleMs.ToString("0.0") + " ms | Download " + lr.DownMbps.ToString("0") + " Mbit/s @ " + lr.DownLoadedMs.ToString("0.0") +
                         " ms | Upload " + lr.UpMbps.ToString("0") + " Mbit/s @ " + lr.UpLoadedMs.ToString("0.0") + " ms | Note " + lr.Grade);
                    Check(lr.DownMbps > 1 && lr.UpMbps > 1, "Download und Upload liefern Daten");
                }

                // ---------------------------------------------------------------- Server-Finder (nur mit Admin: Raw-Socket)
                if (apply && only == null)
                {
                    Line("");
                    Line("[Server-Finder mit künstlichem UDP-Verkehr]");
                    try
                    {
                        using (var udp = new UdpClient(new IPEndPoint(primary.Address, 0)))
                        {
                            int localPort = ((IPEndPoint)udp.Client.LocalEndPoint).Port;
                            var capture = ServerFinder.Capture(primary.Address, new[] { localPort }, 2500);
                            Thread.Sleep(400);
                            // DNS-Abfrage nach example.com an 1.1.1.1:53 - die Antwort kommt als UDP an unseren lokalen Port
                            byte[] q = { 0x12, 0x34, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0, 7, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e', 3, (byte)'c', (byte)'o', (byte)'m', 0, 0, 1, 0, 1 };
                            for (int i = 0; i < 4; i++) { udp.Send(q, q.Length, new IPEndPoint(IPAddress.Parse("1.1.1.1"), 53)); Thread.Sleep(150); }
                            var peers = capture.GetAwaiter().GetResult();
                            foreach (var pr in peers) Line("  gesehen: " + pr.Ip + ":" + pr.Port + " -> lokal " + pr.LocalPort + " (" + pr.Packets + " Pakete)");
                            Check(peers.Any(pr => pr.Ip.ToString() == "1.1.1.1" && pr.Port == 53 && pr.LocalPort == localPort), "Absender 1.1.1.1:53 wird erkannt");

                            var ports = ServerFinder.UdpPortsOf(new[] { System.Diagnostics.Process.GetCurrentProcess().Id });
                            Check(ports.Contains(localPort), "UDP-Port wird dem eigenen Prozess zugeordnet");
                        }
                    }
                    catch (Exception e) { Check(false, "Server-Finder: " + e.Message); }
                }

                // ---------------------------------------------------------------- Zustand + Zyklen
                var engineCopy = Path.Combine(tempDir, "Engine.ini");
                string realEngine = new EngineLatencyTweak().IniPath;
                if (File.Exists(realEngine)) File.Copy(realEngine, engineCopy, true);
                else File.WriteAllText(engineCopy, "[SystemSettings]\r\nr.Test=1\r\n");
                File.SetAttributes(engineCopy, FileAttributes.Normal);

                var potatoEngineCopy = Path.Combine(tempDir, "PotatoEngine.ini");
                var potatoGusCopy = Path.Combine(tempDir, "PotatoGameUserSettings.ini");
                var realPotato = new PotatoTweak();
                if (File.Exists(realPotato.EngineIniPath)) File.Copy(realPotato.EngineIniPath, potatoEngineCopy, true);
                else File.WriteAllText(potatoEngineCopy, "[SystemSettings]\r\nr.Test=1\r\n");
                if (File.Exists(realPotato.GameUserIniPath)) File.Copy(realPotato.GameUserIniPath, potatoGusCopy, true);
                else File.WriteAllText(potatoGusCopy, "[/Script/FortniteGame.FortGameUserSettings]\r\nbMotionBlur=True\r\n\r\n[ScalabilityGroups]\r\nsg.TextureQuality=3\r\nsg.ShadingQuality=3\r\n");
                File.SetAttributes(potatoEngineCopy, FileAttributes.Normal);
                File.SetAttributes(potatoGusCopy, FileAttributes.Normal);
                var potatoOrigEngineBytes = File.ReadAllBytes(potatoEngineCopy);
                var potatoOrigGusBytes = File.ReadAllBytes(potatoGusCopy);

                var fpsEngineCopy = Path.Combine(tempDir, "FpsEngine.ini");
                var fpsGusCopy = Path.Combine(tempDir, "FpsGameUserSettings.ini");
                var realFps = new FpsBoostTweak();
                if (File.Exists(realFps.EngineIniPath)) File.Copy(realFps.EngineIniPath, fpsEngineCopy, true);
                else File.WriteAllText(fpsEngineCopy, "[SystemSettings]\r\nr.Test=1\r\n");
                if (File.Exists(realFps.GameUserIniPath)) File.Copy(realFps.GameUserIniPath, fpsGusCopy, true);
                else File.WriteAllText(fpsGusCopy, "[/Script/FortniteGame.FortGameUserSettings]\r\nbMotionBlur=True\r\n");
                File.SetAttributes(fpsEngineCopy, FileAttributes.Normal);
                File.SetAttributes(fpsGusCopy, FileAttributes.Normal);
                var fpsOrigEngineBytes = File.ReadAllBytes(fpsEngineCopy);
                var fpsOrigGusBytes = File.ReadAllBytes(fpsGusCopy);

                var mtu = new MtuTweak();
                if (only == null || only.Contains("mtu")) mtu.Measure();

                var tweaks = new List<Tweak>
                {
                    new NetStackTweak(), new AdapterTweak(), new PowerTweak(),
                    new FpsBoostTweak { EngineIniPath = fpsEngineCopy, GameUserIniPath = fpsGusCopy, IncludeReg = Admin.IsElevated },
                    dns,
                    new GameTweak("selftest-game", "RetracBoost-SelfTest", "selftest-game") { Exe = "RetracBoostSelfTest.exe" },
                    new TcpTuneTweak(), new BackgroundTweak(), new TimerTweak(), new AutoStartTweak(), mtu,
                    new GameTweak("ogfn", "RetracBoost-OGFN-Test", "ogfn") { Exe = "RetracBoostSelfTest2.exe" },
                    new ServerLockTweak(),
                    new PotatoTweak { EngineIniPath = potatoEngineCopy, GameUserIniPath = potatoGusCopy },
                    new FsoTweak { ExePath = @"C:\RetracBoostSelfTest\FortniteClient-Win64-Shipping.exe" },
                    new RegValueTweak("dvr", "dvr", "dvr", () => new List<RegItem>
                    {
                        RegItem.Dw(@"System\GameConfigStore", "GameDVR_Enabled", 0, 1, null, true),
                        RegItem.Dw(@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, 1, null, true),
                        RegItem.Dw(@"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, null, null, true)
                    }),
                    new MouseAccelTweak(),
                    new EngineLatencyTweak { IniPath = engineCopy }
                };

                Line("");
                Line("[Zustand]");
                foreach (var t in tweaks)
                {
                    bool applied;
                    try { applied = t.Relevant && t.IsApplied(); }
                    catch (Exception e) { applied = false; Line("  " + t.Id + " IsApplied-Fehler: " + e.Message); failures++; }
                    Line("  " + t.Title.PadRight(34) + " applied=" + applied + "  | " + t.Detail);
                }

                if (apply)
                {
                    if (!args.Contains("--noelevate")) Check(Admin.IsElevated, "Administratorrechte vorhanden");
                    foreach (var t in tweaks)
                    {
                        if (only != null && !only.Contains(t.Id)) continue;
                        Line("");
                        Line("[Zyklus: " + t.Title + "]");
                        if (t is AdapterTweak && skipAdapter) { Line("  übersprungen (--skip-adapter)"); continue; }
                        if (t is DnsTweak && !plan.Switch)
                        {
                            plan.BestProvider = DnsTweak.Providers[0];     // erzwingt Cloudflare, damit der Schreibpfad geprüft wird
                            plan.Best = new DnsResult { Name = "Cloudflare", Server = "1.1.1.1", Score = 1 };
                            plan.Switch = true;
                            Line("  (Wechsel nur für den Test erzwungen)");
                        }
                        if (t is MtuTweak)
                        {
                            var m = (MtuTweak)t;
                            if (!m.CanLower) { m.ForceForTest(m.CurrentMtu - 8); Line("  (Pfad-MTU nur für den Test auf " + m.PathMtu + " gesetzt)"); }
                        }
                        var keysBefore = new HashSet<string>(Store.Keys(""));
                        string schemeBefore = t is PowerTweak ? t.Snapshot().Replace("scheme=", "").Trim() : null;
                        try { Cycle(t); }
                        catch (Exception e) { Check(false, "Ausnahme: " + e.Message); }
                        // Der Zyklus stellt den Systemzustand wieder her - übrig gebliebene Sicherungen sind nur Testspuren.
                        foreach (var k in Store.Keys("").Where(k => !keysBefore.Contains(k)).ToList()) Store.Set(k, null);
                        if (schemeBefore != null)
                        {
                            var rr = Sh.PowerCfg("/setactive " + schemeBefore);
                            Line("  Energieplan zurück auf " + schemeBefore + ": " + (rr.Ok ? "ok" : rr.Text));
                        }
                    }

                    // Engine.ini, FPS-Booster & Potato-INIs: die Dateien müssen nach dem Zyklus bytegleich zum Original sein
                    if (only == null || only.Contains("engine"))
                    {
                        Line("");
                        Line("[Engine.ini bytegleich]");
                        var a = File.ReadAllBytes(engineCopy);
                        var b = File.Exists(realEngine) ? File.ReadAllBytes(realEngine) : a;
                        Check(a.SequenceEqual(b), "Kopie nach Anwenden + Zurücksetzen identisch mit dem Original (" + a.Length + " Bytes)");
                    }
                    if (only == null || only.Contains("fpsboost"))
                    {
                        Line("");
                        Line("[FPS-Booster INIs bytegleich]");
                        var fe = File.ReadAllBytes(fpsEngineCopy);
                        var fg = File.ReadAllBytes(fpsGusCopy);
                        Check(fe.SequenceEqual(fpsOrigEngineBytes), "FPS-Booster Engine.ini nach Anwenden + Zurücksetzen bytegleich (" + fe.Length + " Bytes)");
                        Check(fg.SequenceEqual(fpsOrigGusBytes), "FPS-Booster GameUserSettings.ini nach Anwenden + Zurücksetzen bytegleich (" + fg.Length + " Bytes)");
                    }
                    if (only == null || only.Contains("potato"))
                    {
                        Line("");
                        Line("[Potato-Grafik INIs bytegleich]");
                        var pe = File.ReadAllBytes(potatoEngineCopy);
                        var pg = File.ReadAllBytes(potatoGusCopy);
                        Check(pe.SequenceEqual(potatoOrigEngineBytes), "Potato Engine.ini nach Anwenden + Zurücksetzen bytegleich (" + pe.Length + " Bytes)");
                        Check(pg.SequenceEqual(potatoOrigGusBytes), "Potato GameUserSettings.ini nach Anwenden + Zurücksetzen bytegleich (" + pg.Length + " Bytes)");
                    }
                }
            }
            catch (Exception e)
            {
                Line("UNBEHANDELTER FEHLER: " + e);
                failures++;
            }

            Line("");
            Line(failures == 0 ? "ERGEBNIS: alles bestanden" : "ERGEBNIS: " + failures + " Fehler");
            File.WriteAllText(outPath, log.ToString(), Encoding.UTF8);
            return failures == 0 ? 0 : 1;
        }

        /// <summary>--selftest --repair: beseitigt Spuren früherer Testläufe und stellt das gespeicherte Spiel-Profil wieder her (Admin).</summary>
        static int RunRepair(string outPath)
        {
            try
            {
                Line("Retrac Boost repair  " + DateTime.Now.ToString("s") + " | elevated=" + Admin.IsElevated);
                const string ifeo = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\";
                foreach (var exe in new[] { "RetracBoostSelfTest.exe", "RetracBoostSelfTest2.exe" })
                {
                    Reg.DeleteTree(ifeo + exe);
                    Line("  IFEO-Testschlüssel entfernt: " + exe);
                }

                var r = Sh.PowerShell("Get-NetQosPolicy -PolicyStore localhost -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'RetracBoost-*Test*' -or $_.Name -like 'RetracBoost-SelfTest*' } | Remove-NetQosPolicy -PolicyStore localhost -Confirm:$false;'OK'");
                Line("  Test-QoS-Regeln entfernt: " + (r.Ok ? "ok" : r.Text));

                // Sicherungen, die nur den Windows-Standard festhalten, würden einen Zustand als "von uns gesetzt" ausgeben,
                // den der Nutzer schon vorher hatte.
                var net = new NetStackTweak();
                if (net.IsApplied()) foreach (var k in Store.Keys("bk|net|")) Store.Set(k, null);
                if (new MouseAccelTweak().IsApplied()) Store.Set("bk|mouse", null);
                var dvr = new RegValueTweak("dvr", "dvr", "dvr", () => new List<RegItem>
                {
                    RegItem.Dw(@"System\GameConfigStore", "GameDVR_Enabled", 0, 1, null, true),
                    RegItem.Dw(@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, 1, null, true),
                    RegItem.Dw(@"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, null, null, true)
                });
                if (dvr.IsApplied()) foreach (var k in Store.Keys("bk|dvr|")) Store.Set(k, null);
                Line("  veraltete Sicherungen bereinigt");

                // Von einem früheren Testlauf versehentlich gelöschte, vorher vorhandene Einstellungen zurückschreiben:
                // Übermittlungsoptimierung "nur HTTP" und das Verzögert-Starten-Flag von BITS.
                if (!(Reg.Get(@"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode") is int))
                {
                    Reg.Set(@"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 0, Microsoft.Win32.RegistryValueKind.DWord);
                    Line("  Richtlinie DODownloadMode=0 wiederhergestellt");
                }
                var bitsDelayed = Reg.Get(@"SYSTEM\CurrentControlSet\Services\BITS", "DelayedAutostart");
                if (!(bitsDelayed is int) || (int)bitsDelayed != 1)
                {
                    try { Reg.Set(@"SYSTEM\CurrentControlSet\Services\BITS", "DelayedAutostart", 1, Microsoft.Win32.RegistryValueKind.DWord); Line("  BITS DelayedAutostart=1 wiederhergestellt"); }
                    catch (Exception e) { Line("  BITS-Flag nicht schreibbar: " + e.Message); }
                }

                string exeName = Store.Get("game");
                if (!string.IsNullOrEmpty(exeName))
                {
                    var g = new GameTweak { Exe = exeName };
                    bool ok = g.IsApplied();
                    Line("  gespeichertes Spiel: " + exeName + " | aktiv=" + ok);
                    if (!ok) { g.Apply(); Check(g.IsApplied(), "Spiel-Profil für " + exeName + " wiederhergestellt"); }
                }
            }
            catch (Exception e) { Line("FEHLER: " + e); failures++; }
            Line(failures == 0 ? "ERGEBNIS: ok" : "ERGEBNIS: " + failures + " Fehler");
            File.WriteAllText(outPath, log.ToString(), Encoding.UTF8);
            return failures == 0 ? 0 : 1;
        }

        /// <summary>--selftest --server: erkennt den Spielserver des laufenden Matches (rein lesend, braucht Admin).</summary>
        static int RunServerProbe(string outPath)
        {
            try
            {
                Line("Retrac Boost server probe  " + DateTime.Now.ToString("s") + " | elevated=" + Admin.IsElevated);
                var pids = new List<int>();
                foreach (var p in System.Diagnostics.Process.GetProcessesByName("FortniteClient-Win64-Shipping")) { pids.Add(p.Id); p.Dispose(); }
                Line("Spielprozesse: " + (pids.Count == 0 ? "keine" : string.Join(", ", pids)));
                Check(pids.Count > 0, "Spiel läuft");
                if (pids.Count > 0)
                {
                    var nic = Nics.Primary(Nics.Active(false));
                    Line("Adapter: " + nic.Name + " " + nic.Address);
                    var ports = ServerFinder.UdpPortsOf(pids);
                    Line("UDP-Ports des Spiels: " + (ports.Count == 0 ? "keine" : string.Join(", ", ports)));
                    Check(ports.Count > 0, "UDP-Ports gefunden");
                    if (ports.Count > 0)
                    {
                        var peers = ServerFinder.Capture(nic.Address, ports, 4000).GetAwaiter().GetResult();
                        Line("Absender (Top 10):");
                        foreach (var pr in peers.Take(10))
                            Line("  " + pr.Ip + ":" + pr.Port + "  ->  lokal " + pr.LocalPort + "  (" + pr.Packets + " Pakete)" + (Probe.IsPrivate(pr.Ip) ? "  [privat]" : ""));
                        var pick = ServerFinder.PickServer(peers);
                        Check(pick != null, "Spielserver erkannt");
                        if (pick != null)
                        {
                            var info = ServerFinder.Describe(pick, CancellationToken.None).GetAwaiter().GetResult();
                            Line("SERVER: " + info.Ip + ":" + info.Port + " (" + info.Packets + " Pakete)");
                            Line("  Hostname:   " + (info.HostName ?? "-"));
                            Line("  Region:     " + (info.RegionGuess ?? "-"));
                            Line("  Betreiber:  " + (info.Provider ?? "-") + " | Standort: " + (info.Location ?? "-"));
                            Line("  Ping:       " + (info.PingMs.HasValue ? info.PingMs.Value.ToString("0.0") + " ms" : "keine Antwort"));
                            if (info.ViaHopMs.HasValue) Line("  Letzter Hop: " + info.ViaHopAddress + " " + info.ViaHopMs.Value.ToString("0.0") + " ms");
                            Line("  Route:");
                            foreach (var h in info.Route)
                                Line("    " + h.Ttl.ToString().PadLeft(2) + "  " + (h.Avg.HasValue ? h.Avg.Value.ToString("0.0").PadLeft(6) + " ms" : "     *   ") + "  " + h.Label);
                        }
                    }
                }
            }
            catch (Exception e) { Line("FEHLER: " + e); failures++; }
            Line(failures == 0 ? "ERGEBNIS: ok" : "ERGEBNIS: " + failures + " Fehler");
            File.WriteAllText(outPath, log.ToString(), Encoding.UTF8);
            return failures == 0 ? 0 : 1;
        }

        static void Cycle(Tweak t)
        {
            string before = t.Snapshot();
            bool was = t.IsApplied();
            Line("  vorher:   applied=" + was);
            Line("  snapshot: " + before.Replace("\n", " ; "));

            if (!was)
            {
                t.Apply();
                Check(t.IsApplied(), "nach Apply() gilt der Tweak als aktiv");
                Line("  aktiv:    " + t.Snapshot().Replace("\n", " ; "));
                t.Revert();
                Check(!t.IsApplied() || t is DnsTweak && !((DnsTweak)t).HasBackup, "nach Revert() ist er nicht mehr aktiv");
                Check(t.Snapshot() == before, "Zustand nach Revert() ist identisch mit dem Ausgangszustand");
            }
            else
            {
                t.Revert();
                Check(!t.IsApplied(), "bereits aktiver Tweak lässt sich ausschalten");
                t.Apply();
                Check(t.IsApplied(), "und wieder einschalten");
                // Beim Energieplan kann Apply() nur "Höchstleistung" wählen, nicht die eigene Kopie des Nutzers - dort zählt der Zustand.
                if (!(t is PowerTweak)) Check(t.Snapshot() == before, "Endzustand identisch mit dem Ausgangszustand");
            }
        }
    }
}
