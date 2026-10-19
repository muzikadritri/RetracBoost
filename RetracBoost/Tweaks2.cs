using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace RetracBoost
{
    // ------------------------------------------------------------------------------------------
    // Generischer Tweak aus Registry-Einträgen (HKLM oder HKCU)
    // ------------------------------------------------------------------------------------------
    sealed class RegValueTweak : Tweak
    {
        readonly string id, title, summary;
        readonly Func<List<RegItem>> items;

        public RegValueTweak(string id, string title, string summary, Func<List<RegItem>> items)
        {
            this.id = id; this.title = title; this.summary = summary; this.items = items;
        }

        public override string Id { get { return id; } }
        public override string Title { get { return title; } }
        public override string Summary { get { return summary; } }
        public override bool IsApplied() { return RegStore.AllSet(items()); }
        public override void Apply() { RegStore.Apply(id, items()); }
        public override void Revert() { RegStore.Revert(id, items()); }
        public override string Snapshot() { return Snap(items()); }
    }

    // ------------------------------------------------------------------------------------------
    // Einfacher App-Schalter (z. B. Overlay), gespeichert in den Einstellungen
    // ------------------------------------------------------------------------------------------
    sealed class SettingTweak : Tweak
    {
        readonly string id, title, summary, key;
        readonly Action<bool> onChange;

        public SettingTweak(string id, string title, string summary, Action<bool> onChange)
        {
            this.id = id; this.title = title; this.summary = summary; this.onChange = onChange;
            key = "setting." + id;
        }

        public override string Id { get { return id; } }
        public override string Title { get { return title; } }
        public override string Summary { get { return summary; } }
        public override bool WeApplied { get { return false; } }
        public override string BusyText { get { return ""; } }
        public override bool IsApplied() { return Store.Get(key) == "1"; }
        public override void Apply() { Store.Set(key, "1"); if (onChange != null) onChange(true); }
        public override void Revert() { Store.Set(key, "0"); if (onChange != null) onChange(false); }
        public override string Snapshot() { return key + "=" + Store.Get(key); }
    }

    // ------------------------------------------------------------------------------------------
    // TCP-Feintuning: Empfangsfenster-Autotuning + Receive Segment Coalescing
    // ------------------------------------------------------------------------------------------
    sealed class TcpTuneTweak : Tweak
    {
        const string AutoKey = "bk|tcp|auto", RscKey = "bk|tcp|rsc";

        public override string Id { get { return "tcp"; } }
        public override string Title { get { return L.T("TCP-Feintuning", "TCP tuning"); } }
        public override string Summary
        {
            get { return L.T("Empfangsfenster-Autotuning auf Normal, Paket-Zusammenfassung (RSC) aus", "Receive-window autotuning normal, receive segment coalescing off"); }
        }

        static bool Read(out string auto, out string rsc)
        {
            auto = rsc = null;
            var r = Sh.PowerShell("$t=Get-NetTCPSetting -SettingName InternetCustom;$o=Get-NetOffloadGlobalSetting;'RB|'+$t.AutoTuningLevelLocal+'|'+$o.ReceiveSegmentCoalescing");
            var m = Regex.Match(r.Out, @"RB\|(\w+)\|(\w+)");
            if (!r.Ok || !m.Success) return false;
            auto = m.Groups[1].Value;
            rsc = m.Groups[2].Value;
            return true;
        }

        public override bool IsApplied()
        {
            string a, r;
            return Read(out a, out r) && a == "Normal" && r == "Disabled";
        }

        public override bool WeApplied { get { return Store.Get(AutoKey) != null || Store.Get(RscKey) != null; } }

        static void Run(string args)
        {
            var r = Sh.Netsh(args);
            if (!r.Ok) throw new InvalidOperationException(r.Text);
        }

        public override void Apply()
        {
            string a, r;
            if (!Read(out a, out r)) throw new InvalidOperationException(L.T("TCP-Einstellungen nicht lesbar.", "Cannot read TCP settings."));
            if (a != "Normal")
            {
                if (Store.Get(AutoKey) == null) Store.Set(AutoKey, a);
                Run("int tcp set global autotuninglevel=normal");
            }
            if (r != "Disabled")
            {
                if (Store.Get(RscKey) == null) Store.Set(RscKey, r);
                Run("int tcp set global rsc=disabled");
            }
        }

        public override void Revert()
        {
            string a = Store.Get(AutoKey), r = Store.Get(RscKey);
            if (a != null) { Run("int tcp set global autotuninglevel=" + a.ToLowerInvariant()); Store.Set(AutoKey, null); }
            if (r != null) { Run("int tcp set global rsc=" + r.ToLowerInvariant()); Store.Set(RscKey, null); }
            if (a == null && r == null)
            {
                // nichts gesichert (war schon vorher so eingestellt): auf Windows-Standard
                Run("int tcp set global autotuninglevel=normal");
                Run("int tcp set global rsc=enabled");
            }
        }

        public override string Snapshot()
        {
            string a, r;
            return Read(out a, out r) ? "autotuning=" + a + " rsc=" + r : "unreadable";
        }
    }

    // ------------------------------------------------------------------------------------------
    // Hintergrund: Windows Update, BITS und Übermittlungsoptimierung pausieren
    // ------------------------------------------------------------------------------------------
    sealed class BackgroundTweak : Tweak
    {
        static readonly string[] Services = { "wuauserv", "BITS", "DoSvc", "UsoSvc" };
        const string SvcPath = @"SYSTEM\CurrentControlSet\Services\";
        const string SkipKey = "bg.skip";
        const string SvcKeyPrefix = "bk|bgsvc|";
        const string DoPath = @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization";

        public override string Id { get { return "bg"; } }
        public override string Title { get { return L.T("Hintergrund pausieren", "Pause background traffic"); } }
        public override string Summary
        {
            get { return L.T("Windows Update, BITS und Übermittlungsoptimierung (P2P-Upload) aus – bis du es wieder ausschaltest", "Windows Update, BITS and Delivery Optimization (P2P upload) off until you switch it back"); }
        }
        public override bool WeApplied { get { return Store.Keys(SvcKeyPrefix).Count > 0 || RegStore.HasBackup(Id); } }

        // Übermittlungsoptimierung per Richtlinie auf "nur HTTP" (kein Upload an andere PCs)
        static List<RegItem> DoItems() { return new List<RegItem> { RegItem.Dw(DoPath, "DODownloadMode", 0, null) }; }

        static int? StartType(string svc)
        {
            var v = Reg.Get(SvcPath + svc, "Start");
            return v is int ? (int?)(int)v : null;
        }

        static bool Delayed(string svc)
        {
            var v = Reg.Get(SvcPath + svc, "DelayedAutostart");
            return v is int && (int)v == 1;
        }

        static bool Running(string svc)
        {
            try { using (var c = new ServiceController(svc)) return c.Status == ServiceControllerStatus.Running || c.Status == ServiceControllerStatus.StartPending; }
            catch { return false; }
        }

        static HashSet<string> Skipped()
        {
            var s = Store.Get(SkipKey) ?? "";
            return new HashSet<string>(s.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
        }

        public override bool IsApplied()
        {
            var skip = Skipped();
            var present = Services.Where(s => StartType(s).HasValue && !skip.Contains(s)).ToList();
            bool servicesOff = present.Count > 0 && present.All(s => StartType(s) == 4);
            return servicesOff && RegStore.AllSet(DoItems());
        }

        static ShResult Sc(string args)
        {
            return Sh.Run(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe"), args, 30000, Encoding.Default);
        }

        public override void Apply()
        {
            var skipped = new List<string>();
            int changed = 0;
            foreach (var svc in Services)
            {
                int? start = StartType(svc);
                if (!start.HasValue || start == 4) continue;

                string key = SvcKeyPrefix + svc;
                if (Store.Get(key) == null)
                    Store.Set(key, start.Value + "|" + (Delayed(svc) ? 1 : 0) + "|" + (Running(svc) ? 1 : 0));

                var r = Sc("config " + svc + " start= disabled");
                if (!r.Ok || StartType(svc) != 4)
                {
                    Store.Set(key, null);               // nicht geändert (geschützter Dienst) -> keine Sicherung nötig
                    skipped.Add(svc);
                    continue;
                }
                changed++;
                try
                {
                    using (var c = new ServiceController(svc))
                        if (c.Status == ServiceControllerStatus.Running)
                        {
                            c.Stop();
                            c.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                        }
                }
                catch { /* Dienst lässt sich nicht stoppen - er ist dennoch deaktiviert und startet nicht neu */ }
            }
            Store.Set(SkipKey, skipped.Count == 0 ? null : string.Join(",", skipped));
            RegStore.Apply(Id, DoItems());
            if (changed == 0 && skipped.Count == Services.Count(s => StartType(s).HasValue))
                throw new InvalidOperationException(L.T("Windows erlaubt die Änderung der Dienste nicht.", "Windows does not allow changing these services."));
        }

        public override void Revert()
        {
            bool servicesHadBackup = Store.Keys(SvcKeyPrefix).Count > 0;
            foreach (var key in Store.Keys(SvcKeyPrefix))
            {
                string svc = key.Substring(SvcKeyPrefix.Length);
                var parts = (Store.Get(key) ?? "3|0|0").Split('|');
                int start = int.Parse(parts[0], CultureInfo.InvariantCulture);
                bool delayed = parts.Length > 1 && parts[1] == "1";
                bool wasRunning = parts.Length > 2 && parts[2] == "1";

                string mode = start == 2 ? (delayed ? "delayed-auto" : "auto") : start == 3 ? "demand" : start == 4 ? "disabled" : "demand";
                var r = Sc("config " + svc + " start= " + mode);
                if (!r.Ok) throw new InvalidOperationException(svc + ": " + r.Text);

                // "sc config demand" löscht ein vorhandenes DelayedAutostart-Flag - für identischen Zustand zurückschreiben.
                if (delayed && mode != "delayed-auto")
                {
                    try { Reg.Set(SvcPath + svc, "DelayedAutostart", 1, RegistryValueKind.DWord); } catch { }
                }
                if (wasRunning)
                {
                    try { using (var c = new ServiceController(svc)) c.Start(); } catch { }
                }
                Store.Set(key, null);
            }
            Store.Set(SkipKey, null);

            // Die Richtlinie nur zurücksetzen, wenn wir sie gesetzt haben. Gab es von uns gar nichts zu ändern,
            // gilt das Ausschalten ausdrücklich dem schon vorher aktiven Zustand.
            var mine = DoItems().Where(i => Store.Get(i.BackupKey(Id)) != null).ToList();
            if (mine.Count > 0)
            {
                RegStore.Revert(Id, mine);
                Reg.DeleteKeyIfEmpty(DoPath);
            }
            else if (!servicesHadBackup)
            {
                RegStore.Revert(Id, DoItems());
                Reg.DeleteKeyIfEmpty(DoPath);
            }
        }

        public override string Snapshot()
        {
            return string.Join(" ", Services.Select(s => s + "=" + (StartType(s).HasValue ? StartType(s).ToString() : "-") + (Delayed(s) ? "d" : "")))
                   + " | " + Snap(DoItems());
        }
    }


    // ------------------------------------------------------------------------------------------
    // Systemuhr-Auflösung 0,5 ms (Windows 10 ab 2004 / Windows 11)
    // ------------------------------------------------------------------------------------------
    static class TimerHold
    {
        [DllImport("ntdll.dll")]
        static extern int NtSetTimerResolution(uint desired, bool set, out uint current);

        static bool held;

        public static void Enable()
        {
            if (held) return;
            uint cur;
            if (NtSetTimerResolution(5000, true, out cur) == 0) held = true;   // 5000 x 100 ns = 0,5 ms
        }

        public static void Disable()
        {
            if (!held) return;
            uint cur;
            NtSetTimerResolution(5000, false, out cur);
            held = false;
        }
    }

    sealed class TimerTweak : Tweak
    {
        const string KernelPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel";

        public override string Id { get { return "timer"; } }
        public override string Title { get { return L.T("Timer-Auflösung 0,5 ms", "Timer resolution 0.5 ms"); } }
        public override string Summary
        {
            get { return L.T("Systemuhr bleibt fein getaktet, solange Retrac Boost läuft · einmaliger Neustart nötig", "Keeps the system clock fine-grained while Retrac Boost runs · one restart needed"); }
        }

        static List<RegItem> Items()
        {
            return new List<RegItem> { RegItem.Dw(KernelPath, "GlobalTimerResolutionRequests", 1, null) };
        }

        public override bool IsApplied() { return RegStore.AllSet(Items()); }

        public override void Apply()
        {
            RegStore.Apply(Id, Items());
            TimerHold.Enable();
        }

        public override void Revert()
        {
            RegStore.Revert(Id, Items());
            TimerHold.Disable();
        }

        public override string Snapshot() { return Snap(Items()); }
    }

    // ------------------------------------------------------------------------------------------
    // Mit Windows starten (geplante Aufgabe mit höchsten Rechten, ohne UAC-Abfrage beim Login)
    // ------------------------------------------------------------------------------------------
    sealed class AutoStartTweak : Tweak
    {
        const string TaskName = "RetracBoost";

        public override string Id { get { return "autostart"; } }
        public override string Title { get { return L.T("Mit Windows starten", "Start with Windows"); } }
        public override string Summary { get { return L.T("Startet minimiert beim Anmelden, ohne UAC-Abfrage", "Starts minimised at sign-in, no UAC prompt"); } }
        public override bool WeApplied { get { return false; } }

        public override bool IsApplied()
        {
            var r = Sh.PowerShell("if(Get-ScheduledTask -TaskName " + Sh.Q(TaskName) + " -ErrorAction SilentlyContinue){'YES'}else{'NO'}");
            return r.Ok && r.Out.Contains("YES");
        }

        public override void Apply()
        {
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            string script =
                "$u=[Security.Principal.WindowsIdentity]::GetCurrent().Name;" +
                "$a=New-ScheduledTaskAction -Execute " + Sh.Q(exe) + " -Argument '--minimized';" +
                "$t=New-ScheduledTaskTrigger -AtLogOn -User $u;" +
                "$p=New-ScheduledTaskPrincipal -UserId $u -LogonType Interactive -RunLevel Highest;" +
                "$s=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero);" +
                "Register-ScheduledTask -TaskName " + Sh.Q(TaskName) + " -Action $a -Trigger $t -Principal $p -Settings $s -Force | Out-Null;'OK'";
            var r = Sh.PowerShell(script);
            if (!r.Ok) throw new InvalidOperationException(r.Text);
        }

        public override void Revert()
        {
            var r = Sh.PowerShell("Unregister-ScheduledTask -TaskName " + Sh.Q(TaskName) + " -Confirm:$false -ErrorAction SilentlyContinue;'OK'");
            if (!r.Ok) throw new InvalidOperationException(r.Text);
        }

        public override string Snapshot() { return "task=" + IsApplied(); }
    }

    // ------------------------------------------------------------------------------------------
    // MTU: nur senken, wenn der Pfad kleinere Pakete verlangt als die Karte sendet
    // ------------------------------------------------------------------------------------------
    sealed class MtuTweak : Tweak
    {
        const string KeyPrefix = "bk|mtu|";

        public int? PathMtu { get; private set; }
        public int CurrentMtu { get; private set; }
        public string NicName { get; private set; }
        public string NicId { get; private set; }
        public bool Measured { get; private set; }

        public override string Id { get { return "mtu"; } }
        public override string Title { get { return L.T("Paketgröße (MTU)", "Packet size (MTU)"); } }
        public override string Summary { get { return L.T("Verhindert Paketfragmentierung auf dem Weg ins Internet", "Avoids packet fragmentation on the way to the internet"); } }
        public override bool WeApplied { get { return Store.Keys(KeyPrefix).Count > 0; } }
        public bool CanLower { get { return PathMtu.HasValue && PathMtu.Value < CurrentMtu; } }

        public override string Detail
        {
            get
            {
                if (!Measured) return Summary;
                if (WeApplied) return L.T("MTU auf " + CurrentMtu + " gesetzt", "MTU set to " + CurrentMtu);
                if (!PathMtu.HasValue) return L.T("Messung nicht möglich (Ziel antwortet nicht)", "Could not measure (target does not answer)");
                if (CanLower) return L.T("Pfad erlaubt nur " + PathMtu + ", Karte sendet " + CurrentMtu, "Path allows only " + PathMtu + ", adapter sends " + CurrentMtu);
                return L.T("Optimal: " + PathMtu + " Byte passen ohne Fragmentierung", "Optimal: " + PathMtu + " bytes pass without fragmentation");
            }
        }

        /// <summary>Misst die Pfad-MTU zu 1.1.1.1 (blockierend, auf Arbeitsthread aufrufen).</summary>
        public void Measure()
        {
            var nics = Nics.Active(false);
            var nic = Nics.Primary(nics);
            if (nic == null) { Measured = true; return; }
            NicName = nic.Name;
            NicId = nic.Id;
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                    if (ni.Id == nic.Id) CurrentMtu = ni.GetIPProperties().GetIPv4Properties().Mtu;
            }
            catch { }
            var ip = Probe.Resolve("1.1.1.1").GetAwaiter().GetResult();
            PathMtu = ip == null ? null : MtuTool.FindPathMtu(ip, CancellationToken.None).GetAwaiter().GetResult();
            Measured = true;
        }

        /// <summary>Nur für den Selbsttest: simuliert einen kleineren Pfad, damit der Schreibpfad geprüft werden kann.</summary>
        public void ForceForTest(int pathMtu) { PathMtu = pathMtu; Measured = true; }

        public override bool IsApplied() { return WeApplied || (Measured && PathMtu.HasValue && !CanLower); }

        public override void Apply()
        {
            if (!Measured || !CanLower) throw new InvalidOperationException(L.T("Keine Anpassung nötig.", "No change needed."));
            string key = KeyPrefix + NicId;
            if (Store.Get(key) == null) Store.Set(key, CurrentMtu + "|" + NicName);
            var r = Sh.Netsh("interface ipv4 set subinterface \"" + NicName + "\" mtu=" + PathMtu.Value + " store=persistent");
            if (!r.Ok) { Store.Set(key, null); throw new InvalidOperationException(r.Text); }
            CurrentMtu = PathMtu.Value;
        }

        public override void Revert()
        {
            foreach (var key in Store.Keys(KeyPrefix))
            {
                var parts = (Store.Get(key) ?? "").Split('|');
                if (parts.Length < 2) { Store.Set(key, null); continue; }
                string id = key.Substring(KeyPrefix.Length);
                var nic = Nics.Active(false).FirstOrDefault(n => string.Equals(n.Id, id, StringComparison.OrdinalIgnoreCase));
                string name = nic != null ? nic.Name : parts[1];
                var r = Sh.Netsh("interface ipv4 set subinterface \"" + name + "\" mtu=" + parts[0] + " store=persistent");
                if (!r.Ok) throw new InvalidOperationException(r.Text);
                CurrentMtu = int.Parse(parts[0], CultureInfo.InvariantCulture);
                Store.Set(key, null);
            }
        }

        public override string Snapshot()
        {
            var parts = new List<string>();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    parts.Add(ni.Name + "=" + ni.GetIPProperties().GetIPv4Properties().Mtu);
                }
                catch { }
            }
            return string.Join(" ", parts);
        }
    }

    // ------------------------------------------------------------------------------------------
    // Vollbild-Optimierungen für das Spiel deaktivieren (Kompatibilitäts-Flag, pro Benutzer)
    // ------------------------------------------------------------------------------------------
    sealed class FsoTweak : Tweak
    {
        const string Layers = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
        const string Flag = "DISABLEDXMAXIMIZEDWINDOWEDMODE";

        public string ExePath { get; set; }

        public override string Id { get { return "fso"; } }
        public override string Title { get { return L.T("Vollbild-Optimierung aus", "Fullscreen optimisations off"); } }
        public override string Summary { get { return L.T("Spiel läuft direkt ohne zusätzliche Fenster-Komposition", "Game runs directly, without extra window composition"); } }
        public override bool Relevant { get { return !string.IsNullOrEmpty(ExePath); } }
        public override string Detail
        {
            get
            {
                return Relevant ? Summary : L.T("Spielordner noch nicht gefunden – starte das Spiel einmal oder wähle die .exe", "Game folder not found yet – start the game once or pick the .exe");
            }
        }

        string Current() { return Relevant ? Reg.Get(Layers, ExePath, true) as string : null; }

        public override bool IsApplied()
        {
            var cur = Current();
            return cur != null && cur.IndexOf(Flag, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        string Key { get { return "bk|fso|" + ExePath; } }

        public override void Apply()
        {
            if (!Relevant) throw new InvalidOperationException(L.T("Kein Spielpfad bekannt.", "No game path known."));
            var cur = Current();
            if (cur != null && cur.IndexOf(Flag, StringComparison.OrdinalIgnoreCase) >= 0) return;
            if (Store.Get(Key) == null) Store.Set(Key, cur ?? "absent");
            string next = string.IsNullOrWhiteSpace(cur) ? "~ " + Flag : cur.TrimEnd() + " " + Flag;
            Reg.Set(Layers, ExePath, next, RegistryValueKind.String, true);
        }

        public override void Revert()
        {
            if (!Relevant) return;
            var cur = Current();
            string saved = Store.Get(Key);
            if (saved != null)
            {
                if (saved == "absent") Reg.Delete(Layers, ExePath, true);
                else Reg.Set(Layers, ExePath, saved, RegistryValueKind.String, true);
                Store.Set(Key, null);
            }
            else if (cur != null)
            {
                // vorher schon gesetzt: nur unser Flag entfernen, andere Flags bleiben
                string next = Regex.Replace(cur, @"\s*" + Flag, "", RegexOptions.IgnoreCase).Trim();
                if (next == "" || next == "~") Reg.Delete(Layers, ExePath, true);
                else Reg.Set(Layers, ExePath, next, RegistryValueKind.String, true);
            }
        }

        public override bool WeApplied { get { return Relevant && Store.Get(Key) != null; } }
        public override string Snapshot() { return "layer=" + (Current() ?? "(none)"); }
    }

    // ------------------------------------------------------------------------------------------
    // Mausbeschleunigung ("Zeigerbeschleunigung verbessern") aus
    // ------------------------------------------------------------------------------------------
    sealed class MouseAccelTweak : Tweak
    {
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SystemParametersInfo(uint action, uint param, int[] vparam, uint winIni);

        const uint SPI_GETMOUSE = 3, SPI_SETMOUSE = 4, SPIF_UPDATEINIFILE = 1, SPIF_SENDCHANGE = 2;
        const string BackupKey = "bk|mouse";

        public override string Id { get { return "mouse"; } }
        public override string Title { get { return L.T("Mausbeschleunigung aus", "Mouse acceleration off"); } }
        public override string Summary { get { return L.T("Gleiche Mausbewegung ergibt immer die gleiche Zeigerbewegung", "Same hand movement always gives the same cursor movement"); } }
        public override bool WeApplied { get { return Store.Get(BackupKey) != null; } }

        static int[] Get()
        {
            var m = new int[3];
            if (!SystemParametersInfo(SPI_GETMOUSE, 0, m, 0)) throw new InvalidOperationException("SPI_GETMOUSE");
            return m;
        }

        static void Set(int[] m)
        {
            if (!SystemParametersInfo(SPI_SETMOUSE, 0, m, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE))
                throw new InvalidOperationException("SPI_SETMOUSE");
        }

        public override bool IsApplied() { return Get()[2] == 0; }

        public override void Apply()
        {
            var m = Get();
            if (m[2] == 0) return;
            if (Store.Get(BackupKey) == null) Store.Set(BackupKey, string.Join(",", m));
            Set(new[] { 0, 0, 0 });
        }

        public override void Revert()
        {
            var saved = Store.Get(BackupKey);
            int[] m = { 6, 10, 1 };                       // Windows-Standard
            if (saved != null)
            {
                var p = saved.Split(',');
                if (p.Length == 3) m = p.Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            }
            Set(m);
            Store.Set(BackupKey, null);
        }

        public override string Snapshot() { return "mouse=" + string.Join(",", Get()); }
    }

    // ------------------------------------------------------------------------------------------
    // Engine.ini: Render-Thread läuft nicht mehr ein Bild hinterher (weniger Eingabeverzögerung)
    // ------------------------------------------------------------------------------------------
    sealed class EngineLatencyTweak : Tweak
    {
        const string Section = "[SystemSettings]";
        const string Cvar = "r.OneFrameThreadLag";
        const string BackupKey = "bk|engine|" + Cvar;

        public string IniPath { get; set; }

        public EngineLatencyTweak()
        {
            IniPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"FortniteGame\Saved\Config\WindowsClient\Engine.ini");
        }

        public override string Id { get { return "engine"; } }
        public override string Title { get { return L.T("Render-Verzögerung (experimentell)", "Render lag (experimental)"); } }
        public override string Summary
        {
            get { return L.T("r.OneFrameThreadLag=0 · weniger Eingabe-Delay, kann etwas FPS kosten · gilt ab nächstem Spielstart", "r.OneFrameThreadLag=0 · less input delay, may cost some FPS · applies from next launch"); }
        }
        public override bool Relevant { get { return File.Exists(IniPath); } }
        public override bool WeApplied { get { return Store.Get(BackupKey) != null; } }
        public override string Detail
        {
            get { return Relevant ? Summary : L.T("Engine.ini nicht gefunden – das Spiel muss einmal gestartet worden sein", "Engine.ini not found – the game must have been started once"); }
        }

        // --- Datei-Zugriff, der Kodierung und Zeilenumbrüche unverändert lässt -------------------

        static string ReadText(string path, out Encoding enc)
        {
            var bytes = File.ReadAllBytes(path);
            bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            enc = new UTF8Encoding(bom);
            return enc.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        }

        static void WriteText(string path, string text, Encoding enc)
        {
            var attrs = File.GetAttributes(path);
            bool ro = (attrs & FileAttributes.ReadOnly) != 0;
            if (ro) File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
            try
            {
                var body = enc.GetBytes(text);
                var pre = enc.GetPreamble();
                var all = new byte[pre.Length + body.Length];
                Buffer.BlockCopy(pre, 0, all, 0, pre.Length);
                Buffer.BlockCopy(body, 0, all, pre.Length, body.Length);
                File.WriteAllBytes(path, all);
            }
            finally { if (ro) File.SetAttributes(path, attrs); }
        }

        static readonly Regex CvarLine = new Regex(@"^\s*" + Regex.Escape(Cvar) + @"\s*=\s*(.*?)\s*$", RegexOptions.IgnoreCase);

        /// <summary>Findet den Wert der Variable im Abschnitt [SystemSettings] (null = nicht vorhanden).</summary>
        static string Find(List<string> lines, out int lineIndex, out int sectionIndex)
        {
            lineIndex = -1;
            sectionIndex = lines.FindIndex(l => l.Trim().Equals(Section, StringComparison.OrdinalIgnoreCase));
            if (sectionIndex < 0) return null;
            for (int i = sectionIndex + 1; i < lines.Count; i++)
            {
                string t = lines[i].Trim();
                if (t.StartsWith("[") && t.EndsWith("]")) break;
                var m = CvarLine.Match(lines[i].TrimEnd('\r'));
                if (m.Success) { lineIndex = i; return m.Groups[1].Value; }
            }
            return null;
        }

        static string Newline(string text) { return text.Contains("\r\n") ? "\r\n" : "\n"; }

        string Value()
        {
            if (!Relevant) return null;
            Encoding enc;
            var lines = ReadText(IniPath, out enc).Split('\n').ToList();
            int li, si;
            return Find(lines, out li, out si);
        }

        int lastRemovedIndex = -1;

        void Write(string newValue)       // null = Zeile entfernen
        {
            Encoding enc;
            string text = ReadText(IniPath, out enc);
            string nl = Newline(text);
            var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            int li, si;
            Find(lines, out li, out si);

            if (li >= 0)
            {
                if (newValue == null) { lastRemovedIndex = li; lines.RemoveAt(li); }
                else lines[li] = Cvar + "=" + newValue;
            }
            else if (newValue != null)
            {
                if (si >= 0)
                {
                    int targetIdx = (lastRemovedIndex > si && lastRemovedIndex <= lines.Count) ? lastRemovedIndex : si + 1;
                    lines.Insert(targetIdx, Cvar + "=" + newValue);
                }
                else
                {
                    if (lines.Count > 0 && lines[lines.Count - 1].Trim() != "") lines.Add("");
                    lines.Add(Section);
                    lines.Add(Cvar + "=" + newValue);
                }
            }
            WriteText(IniPath, string.Join(nl, lines), enc);
        }

        public override bool IsApplied() { return Value() == "0"; }

        public override void Apply()
        {
            if (!Relevant) throw new InvalidOperationException(L.T("Engine.ini nicht gefunden.", "Engine.ini not found."));
            string cur = Value();
            if (cur == "0") return;

            string bak = IniPath + ".retracboost.bak";
            if (!File.Exists(bak)) File.Copy(IniPath, bak);              // einmalige Komplettsicherung
            if (Store.Get(BackupKey) == null) Store.Set(BackupKey, cur ?? "absent");
            Write("0");
        }

        public override void Revert()
        {
            if (!Relevant) { Store.Set(BackupKey, null); return; }
            string saved = Store.Get(BackupKey);
            if (saved != null) Write(saved == "absent" ? null : saved);
            else if (Value() == "0") Write(null);                         // war schon vorher gesetzt: Zeile entfernen = Standard
            Store.Set(BackupKey, null);
        }

        public override string Snapshot() { return Cvar + "=" + (Value() ?? "(none)"); }
    }

    // ------------------------------------------------------------------------------------------
    // Standby-RAM-Bereinigung (leert den Windows-Standby-Cache ohne aktive Programme anzufassen)
    // ------------------------------------------------------------------------------------------
    static class RamCleaner
    {
        const int SystemMemoryListInformation = 80;
        const int MemoryPurgeStandbyList = 4;
        const uint TokenAdjustPrivileges = 0x0020;
        const uint TokenQuery = 0x0008;
        const uint SePrivilegeEnabled = 0x00000002;

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct Luid
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct TokenPrivileges
        {
            public uint PrivilegeCount;
            public Luid Luid;
            public uint Attributes;
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool LookupPrivilegeValue(string lpSystemName, string lpName, out Luid lpLuid);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAllPrivileges, ref TokenPrivileges newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr hObject);

        [DllImport("ntdll.dll")]
        static extern int NtSetSystemInformation(int infoClass, ref int info, int length);

        [DllImport("ntdll.dll")]
        static extern int NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int returnLength);

        // RAM-Wächter: geleert wird nur, wenn wirklich kaum freier RAM übrig ist und der Standby-Cache groß ist
        const long LowFreeBytes = 1024L * 1024 * 1024;
        const long MinStandbyBytes = 1024L * 1024 * 1024;

        static bool EnablePrivilege()
        {
            IntPtr token;
            if (!OpenProcessToken(Process.GetCurrentProcess().Handle, TokenAdjustPrivileges | TokenQuery, out token))
                return false;
            try
            {
                Luid luid;
                if (!LookupPrivilegeValue(null, "SeProfileSingleProcessPrivilege", out luid))
                    return false;
                var tp = new TokenPrivileges
                {
                    PrivilegeCount = 1,
                    Luid = luid,
                    Attributes = SePrivilegeEnabled
                };
                return AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            }
            finally { CloseHandle(token); }
        }

        public static bool PurgeStandbyList()
        {
            try
            {
                if (!EnablePrivilege()) return false;
                int cmd = MemoryPurgeStandbyList;
                return NtSetSystemInformation(SystemMemoryListInformation, ref cmd, sizeof(int)) == 0;
            }
            catch { return false; }
        }

        /// <summary>Freier (leerer) RAM und Standby-Cache in Bytes; false, wenn Windows die Listen nicht liefert.</summary>
        public static bool QueryLists(out long freeBytes, out long standbyBytes)
        {
            freeBytes = standbyBytes = 0;
            // SYSTEM_MEMORY_LIST_INFORMATION: Zero, Free, Modified, ModifiedNoWrite, Bad, PageCountByPriority[8], Repurposed[8], ModifiedPageFile
            int ptr = IntPtr.Size, size = ptr * 22;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                if (!EnablePrivilege()) return false;
                int ret;
                if (NtQuerySystemInformation(SystemMemoryListInformation, buf, size, out ret) != 0) return false;
                Func<int, long> at = i => ptr == 8 ? Marshal.ReadInt64(buf, i * 8) : Marshal.ReadInt32(buf, i * 4);
                long page = Environment.SystemPageSize;
                freeBytes = (at(0) + at(1)) * page;
                for (int i = 5; i < 13; i++) standbyBytes += at(i) * page;
                return true;
            }
            catch { return false; }
            finally { Marshal.FreeHGlobal(buf); }
        }

        /// <summary>Leert den Standby-Cache nur, wenn der freie RAM knapp wird (wie ISLC). true = geleert.</summary>
        public static bool PurgeIfLow()
        {
            long free, standby;
            if (!QueryLists(out free, out standby)) return false;
            return free < LowFreeBytes && standby > MinStandbyBytes && PurgeStandbyList();
        }
    }

    // ------------------------------------------------------------------------------------------
    // Gemeinsame INI-Hilfsfunktionen (kodierungs- und zeilenumbruchtreu, mit exakter Sicherung)
    // ------------------------------------------------------------------------------------------
    sealed class IniEntry
    {
        public string Section, Key, Value;
        public bool OnlyIfPresent;
        public bool KeepOnBlindRevert;
    }

    static class IniEditor
    {
        public static string ReadText(string path, out Encoding enc)
        {
            var bytes = File.ReadAllBytes(path);
            bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            enc = new UTF8Encoding(bom);
            return enc.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        }

        public static void WriteText(string path, string text, Encoding enc)
        {
            var attrs = File.GetAttributes(path);
            bool ro = (attrs & FileAttributes.ReadOnly) != 0;
            if (ro) File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
            try
            {
                var body = enc.GetBytes(text);
                var pre = enc.GetPreamble();
                var all = new byte[pre.Length + body.Length];
                Buffer.BlockCopy(pre, 0, all, 0, pre.Length);
                Buffer.BlockCopy(body, 0, all, pre.Length, body.Length);
                File.WriteAllBytes(path, all);
            }
            finally { if (ro) File.SetAttributes(path, attrs); }
        }

        public static string FindKey(List<string> lines, string section, string key, out int lineIndex, out int sectionIndex)
        {
            lineIndex = -1;
            sectionIndex = lines.FindIndex(l => l.Trim().Equals(section, StringComparison.OrdinalIgnoreCase));
            if (sectionIndex < 0) return null;
            var rx = new Regex(@"^\s*" + Regex.Escape(key) + @"\s*=\s*(.*?)\s*$", RegexOptions.IgnoreCase);
            for (int i = sectionIndex + 1; i < lines.Count; i++)
            {
                string t = lines[i].Trim();
                if (t.StartsWith("[") && t.EndsWith("]")) break;
                var m = rx.Match(lines[i]);
                if (m.Success) { lineIndex = i; return m.Groups[1].Value; }
            }
            return null;
        }

        public static bool AllIniSet(string path, List<IniEntry> entries)
        {
            if (!File.Exists(path)) return true;
            Encoding enc;
            var lines = ReadText(path, out enc).Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            foreach (var e in entries)
            {
                if (e.OnlyIfPresent) continue;
                int li, si;
                string v = FindKey(lines, e.Section, e.Key, out li, out si);
                if (!string.Equals(v, e.Value, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        public static void ApplyIni(string backupPrefix, string path, string tag, List<IniEntry> entries)
        {
            if (!File.Exists(path)) return;
            string bak = path + ".retracboost.bak";
            if (!File.Exists(bak)) File.Copy(path, bak);

            Encoding enc;
            string raw = ReadText(path, out enc);
            string nl = raw.Contains("\r\n") ? "\r\n" : "\n";
            var lines = raw.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

            // Rückwärts durchlaufen, damit neu direkt nach [Section] eingefügte Zeilen in Originalreihenfolge stehen
            for (int idx = entries.Count - 1; idx >= 0; idx--)
            {
                var e = entries[idx];
                int li, si;
                string cur = FindKey(lines, e.Section, e.Key, out li, out si);
                string bkKey = backupPrefix + tag + "|" + e.Section + "|" + e.Key;

                if (li >= 0)
                {
                    if (!string.Equals(cur, e.Value, StringComparison.Ordinal))
                    {
                        if (Store.Get(bkKey) == null) Store.Set(bkKey, "V:" + cur);
                        lines[li] = e.Key + "=" + e.Value;
                    }
                }
                else if (!e.OnlyIfPresent)
                {
                    if (Store.Get(bkKey) == null) Store.Set(bkKey, "absent");
                    if (si < 0)
                    {
                        string secBk = backupPrefix + tag + "|sec|" + e.Section;
                        bool addBlank = lines.Count > 0 && lines[lines.Count - 1].Trim() != "";
                        if (Store.Get(secBk) == null) Store.Set(secBk, addBlank ? "blank" : "noblank");
                        if (addBlank) lines.Add("");
                        lines.Add(e.Section);
                        si = lines.Count - 1;
                    }
                    lines.Insert(si + 1, e.Key + "=" + e.Value);
                }
            }
            WriteText(path, string.Join(nl, lines), enc);
        }

        public static void RevertIni(string backupPrefix, string path, string tag, List<IniEntry> entries, bool hasAnyBackup)
        {
            if (!File.Exists(path))
            {
                foreach (var k in Store.Keys(backupPrefix + tag + "|")) Store.Set(k, null);
                return;
            }
            Encoding enc;
            string raw = ReadText(path, out enc);
            string nl = raw.Contains("\r\n") ? "\r\n" : "\n";
            var lines = raw.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

            foreach (var e in entries)
            {
                string bkKey = backupPrefix + tag + "|" + e.Section + "|" + e.Key;
                string saved = Store.Get(bkKey);
                int li, si;
                FindKey(lines, e.Section, e.Key, out li, out si);

                if (saved != null)
                {
                    if (saved == "absent")
                    {
                        if (li >= 0) lines.RemoveAt(li);
                    }
                    else if (saved.StartsWith("V:", StringComparison.Ordinal))
                    {
                        if (li >= 0) lines[li] = e.Key + "=" + saved.Substring(2);
                    }
                    Store.Set(bkKey, null);
                }
                else if (!hasAnyBackup && !e.KeepOnBlindRevert && li >= 0)
                {
                    lines.RemoveAt(li);
                }
            }

            foreach (var secKey in Store.Keys(backupPrefix + tag + "|sec|"))
            {
                string sec = secKey.Substring((backupPrefix + tag + "|sec|").Length);
                string mode = Store.Get(secKey);
                int si = lines.FindIndex(l => l.Trim().Equals(sec, StringComparison.OrdinalIgnoreCase));
                if (si >= 0)
                {
                    bool empty = true;
                    for (int i = si + 1; i < lines.Count; i++)
                    {
                        string t = lines[i].Trim();
                        if (t.StartsWith("[") && t.EndsWith("]")) break;
                        if (t.Length > 0) { empty = false; break; }
                    }
                    if (empty)
                    {
                        lines.RemoveAt(si);
                        if (mode == "blank" && si - 1 >= 0 && lines[si - 1].Trim() == "")
                            lines.RemoveAt(si - 1);
                    }
                }
                Store.Set(secKey, null);
            }

            WriteText(path, string.Join(nl, lines), enc);
        }
    }

    // ------------------------------------------------------------------------------------------
    // NVIDIA Driver Settings (DRS) über nvapi64.dll
    // ------------------------------------------------------------------------------------------
    static class NvDrs
    {
        const uint StructVersion = 0x00013020u;     // NVDRS_SETTING_VER1 (12320 Bytes)
        const int StructSize = 12320;
        const int StOk = 0;
        const int StSettingNotFound = -160;

        // FPS-Booster (optisch 100% unsichtbar: Energie, Latenz, Multi-Threading, VSync)
        public const uint IdPowerManagement = 0x1057EB71u;             // PREFERRED_PSTATE_ID (1 = Prefer max performance)
        public const uint IdThreadedOpt = 0x20C1221Eu;                 // WGL_THREAD_CONTROL_ID (1 = On)
        public const uint IdPreRenderLimit = 0x007BA09Eu;              // PRERENDERLIMIT_ID (1 = Low Latency Mode On)
        public const uint IdVsyncMode = 0x00A879CFu;                   // VSYNCMODE_ID (0x08416747 = Force Off)

        // Potato-Grafik (LOD-Bias & Texturfilterung)
        public const uint IdTransparencySupersampling = 0x10D48A85u;   // AA_MODE_REPLAY_ID
        public const uint IdDriverControlledLodBias = 0x00638E8Fu;     // AUTO_LODBIASADJUST_ID
        public const uint IdDxLodBias = 0x00738E8Fu;                   // LODBIASADJUST_ID
        public const uint IdNegativeLodBias = 0x0019BB68u;             // PS_TEXFILTER_NO_NEG_LODBIAS_ID
        public const uint IdTextureQuality = 0x00CE2691u;              // QUALITY_ENHANCEMENTS_ID
        public const uint IdTrilinearOpt = 0x002ECAF2u;                // TEXTURE_FILTER_TRILINEAR_OPTIMIZATION_ID
        public const uint IdAnisoSampleOpt = 0x00E73211u;              // TEXTURE_FILTER_ANISO_SAMPLE_OPTIMIZATION_ID

        [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr QueryInterface(uint id);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int NvAPI_InitializeDel();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int NvAPI_DRS_CreateSessionDel(out IntPtr hSession);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int NvAPI_DRS_DestroySessionDel(IntPtr hSession);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int NvAPI_DRS_LoadSettingsDel(IntPtr hSession);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int NvAPI_DRS_SaveSettingsDel(IntPtr hSession);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int NvAPI_DRS_FindProfileByNameDel(IntPtr hSession, IntPtr profileName, out IntPtr hProfile);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int NvAPI_DRS_GetSettingDel(IntPtr hSession, IntPtr hProfile, uint settingId, IntPtr pSetting);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int NvAPI_DRS_SetSettingDel(IntPtr hSession, IntPtr hProfile, IntPtr pSetting);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int NvAPI_DRS_DeleteProfileSettingDel(IntPtr hSession, IntPtr hProfile, uint settingId);

        static T Func<T>(uint id) where T : class
        {
            IntPtr ptr = QueryInterface(id);
            if (ptr == IntPtr.Zero) throw new InvalidOperationException("NVAPI function 0x" + id.ToString("X8") + " missing.");
            return Marshal.GetDelegateForFunctionPointer(ptr, typeof(T)) as T;
        }

        static bool? available;

        public static bool IsAvailable()
        {
            if (available.HasValue) return available.Value;
            try
            {
                bool ok = WithFortniteProfile((sess, prof) => { }, false);
                available = ok;
                return ok;
            }
            catch
            {
                available = false;
                return false;
            }
        }

        static bool WithFortniteProfile(Action<IntPtr, IntPtr> action, bool saveChanges)
        {
            if (Func<NvAPI_InitializeDel>(0x0150E828u)() != StOk) return false;
            IntPtr sess;
            if (Func<NvAPI_DRS_CreateSessionDel>(0x0694D52Eu)(out sess) != StOk) return false;
            try
            {
                if (Func<NvAPI_DRS_LoadSettingsDel>(0x375DBD6Bu)(sess) != StOk) return false;
                byte[] nameBytes = new byte[4096];
                Encoding.Unicode.GetBytes("Fortnite", 0, "Fortnite".Length, nameBytes, 0);
                IntPtr prof;
                GCHandle h = GCHandle.Alloc(nameBytes, GCHandleType.Pinned);
                int st;
                try { st = Func<NvAPI_DRS_FindProfileByNameDel>(0x7E4A9A0Bu)(sess, h.AddrOfPinnedObject(), out prof); }
                finally { h.Free(); }
                if (st != StOk || prof == IntPtr.Zero) return false;

                action(sess, prof);
                if (saveChanges)
                {
                    int sv = Func<NvAPI_DRS_SaveSettingsDel>(0xFCBC7E14u)(sess);
                    if (sv != StOk) throw new InvalidOperationException("NvAPI_DRS_SaveSettings (" + sv + ")");
                }
                return true;
            }
            finally
            {
                try { Func<NvAPI_DRS_DestroySessionDel>(0xDAD9CFF8u)(sess); } catch { }
            }
        }

        public static Dictionary<uint, uint?> ReadAll(uint[] settingIds)
        {
            var res = new Dictionary<uint, uint?>();
            foreach (var id in settingIds) res[id] = null;
            if (!IsAvailable()) return res;

            try
            {
                WithFortniteProfile((sess, prof) =>
                {
                    var getSetting = Func<NvAPI_DRS_GetSettingDel>(0x73BF8338u);
                    byte[] buf = new byte[StructSize];
                    GCHandle h = GCHandle.Alloc(buf, GCHandleType.Pinned);
                    try
                    {
                        foreach (var id in settingIds)
                        {
                            Array.Clear(buf, 0, StructSize);
                            BitConverter.GetBytes(StructVersion).CopyTo(buf, 0);
                            int st = getSetting(sess, prof, id, h.AddrOfPinnedObject());
                            if (st == StOk)
                            {
                                uint loc = BitConverter.ToUInt32(buf, 4108);      // 0 = NVDRS_CURRENT_PROFILE_LOCATION
                                uint isPre = BitConverter.ToUInt32(buf, 4112);    // 0 = benutzerdefiniert
                                if (loc == 0 && isPre == 0)
                                    res[id] = BitConverter.ToUInt32(buf, 8220);
                            }
                        }
                    }
                    finally { h.Free(); }
                }, false);
            }
            catch { }
            return res;
        }

        public static void WriteAll(Dictionary<uint, uint?> values)
        {
            WithFortniteProfile((sess, prof) =>
            {
                var setSetting = Func<NvAPI_DRS_SetSettingDel>(0x577DD202u);
                var delSetting = Func<NvAPI_DRS_DeleteProfileSettingDel>(0xE4A26362u);
                byte[] buf = new byte[StructSize];
                GCHandle h = GCHandle.Alloc(buf, GCHandleType.Pinned);
                try
                {
                    foreach (var kv in values)
                    {
                        if (!kv.Value.HasValue)
                        {
                            int stDel = delSetting(sess, prof, kv.Key);
                            if (stDel != StOk && stDel != StSettingNotFound)
                                throw new InvalidOperationException("NvAPI_DRS_DeleteProfileSetting 0x" + kv.Key.ToString("X8") + " (" + stDel + ")");
                        }
                        else
                        {
                            Array.Clear(buf, 0, StructSize);
                            BitConverter.GetBytes(StructVersion).CopyTo(buf, 0);
                            BitConverter.GetBytes(kv.Key).CopyTo(buf, 4100);
                            BitConverter.GetBytes(0u).CopyTo(buf, 4104); // NVDRS_DWORD_TYPE
                            BitConverter.GetBytes(kv.Value.Value).CopyTo(buf, 8220);
                            int stSet = setSetting(sess, prof, h.AddrOfPinnedObject());
                            if (stSet != StOk)
                                throw new InvalidOperationException("NvAPI_DRS_SetSetting 0x" + kv.Key.ToString("X8") + " (" + stSet + ")");
                        }
                    }
                }
                finally { h.Free(); }
            }, true);
        }
    }

    // ------------------------------------------------------------------------------------------
    // FPS-Booster (100% unsichtbar im Spiel – keine Grafikverschlechterung, voller FPS- & 1%-Low-Gewinn):
    //   1) NVIDIA-Treiberprofil "Fortnite" (nvapi64.dll):
    //      - Power Management Mode = Prefer Maximum Performance (kein GPU-Heruntertakten im Fight)
    //      - Low Latency Mode = On (Max Pre-Rendered Frames = 1, minimale Render-Queue)
    //      - Threaded Optimization = On (Multi-Core-Treiber-Rendering)
    //      - Vertical Sync = Force Off
    //   2) Windows-Kernel, Speicher & Grafik-Pipeline (Registry):
    //      - Win32PrioritySeparation = 0x26 (38): kurze, variable CPU-Quanten mit maximalem Vordergrund-Boost
    //      - PowerThrottlingOff = 1: keine Hintergrund-Drosselung von Spiel-/Audio-Threads
    //      - DisablePagingExecutive = 1: Kernel & Treiber bleiben dauerhaft im physischen RAM
    //      - HwSchMode = 2 (HAGS) & DWM OverlayTestMode = 5 (verhindert Multiplane-Overlay-Mikroruckler)
    //   3) Unreal Engine (Engine.ini & GameUserSettings.ini – optisch 100% identisch):
    //      - Asynchrone Shader-Kompilierung & Pipeline-Cache gegen Nachladeruckler
    //      - Textur-Streaming-Boost & reduzierter Overhead für verdeckte Objekte hinter Wänden
    //      - Komplettes Abschalten der Match-Logdatei ([Core.Log] Global=off) & GPU-Crash-Debugging aus
    //      - NVIDIA Reflex Low-Latency in Fortnite aktiviert (bLatencyTweak1=True, LatencyTweak2=1)
    //   4) Live Standby-RAM-Bereinigung (NtSetSystemInformation) für freien Arbeitsspeicher ohne Page-Faults,
    //      während des Spiels automatisch, sobald der freie RAM knapp wird (RAM-Wächter)
    //   5) DirectX: Spiel immer auf der Hochleistungs-GPU (Notebooks mit Onboard-Grafik) und
    //      Flip-Modell auch im randlosen Fenster (Optimierungen für Spiele im Fenstermodus)
    // ------------------------------------------------------------------------------------------
    sealed class FpsBoostTweak : Tweak
    {
        const string BackupPrefix = "bk|fpsboost|";
        const string PrioPath = @"SYSTEM\CurrentControlSet\Control\PriorityControl";
        const string ThrottlePath = @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling";
        const string MemPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
        const string GfxPath = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
        const string DwmPath = @"SOFTWARE\Microsoft\Windows\Dwm";
        const string SysProfilePath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
        const string GamesProfilePath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games";
        const string GameConfigPath = @"System\GameConfigStore";
        const string GpuPrefPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
        const string GpuPrefGlobal = "DirectXUserGlobalSettings";

        public string EngineIniPath { get; set; }
        public string GameUserIniPath { get; set; }
        public bool UseNvidiaDrs { get; set; }
        public bool IncludeReg { get; set; }

        string RetracConfigDir
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"RetracGame\Saved\Config\WindowsClient");
            }
        }

        bool IsDefaultConfigPath
        {
            get
            {
                if (string.IsNullOrEmpty(EngineIniPath)) return false;
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return EngineIniPath.StartsWith(local, StringComparison.OrdinalIgnoreCase);
            }
        }

        public FpsBoostTweak()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"FortniteGame\Saved\Config\WindowsClient");
            EngineIniPath = Path.Combine(dir, "Engine.ini");
            GameUserIniPath = Path.Combine(dir, "GameUserSettings.ini");
            UseNvidiaDrs = true;
            IncludeReg = true;
        }

        public override string Id { get { return "fpsboost"; } }
        public override string Title { get { return L.T("FPS-Booster", "FPS booster"); } }
        public override string Summary
        {
            get
            {
                return L.T("NVIDIA-Profil, Windows-Scheduler, starke Grafikkarte fürs Spiel, Multithreaded Rendering, weniger Eingabeverzögerung, RAM-Wächter",
                           "NVIDIA profile, Windows scheduler, dedicated GPU for the game, multithreaded rendering, less input lag, RAM guard");
            }
        }
        public override bool WeApplied
        {
            get { return RegStore.HasBackup(Id) || Store.Keys(BackupPrefix).Count > 0; }
        }

        /// <summary>Setzt in einer DirectX-Einstellungszeile ("A=1;B=2;") einen Eintrag, ohne die übrigen anzufassen.</summary>
        static string WithFlag(string current, string key, string value)
        {
            var parts = (current ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            if (parts.Any(p => p.Equals(key + "=" + value, StringComparison.OrdinalIgnoreCase))) return current;
            parts.RemoveAll(p => p.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase));
            parts.Add(key + "=" + value);
            return string.Join(";", parts) + ";";
        }

        /// <summary>DirectX-Werte in HKCU: Ziel ergibt sich aus dem aktuellen Wert, ohne Windows-Standard (Zurücksetzen nur per Sicherung).</summary>
        static RegItem DxFlag(string name, string key, string value)
        {
            var cur = Reg.Get(GpuPrefPath, name, true) as string;
            return new RegItem { Path = GpuPrefPath, Name = name, Kind = RegistryValueKind.String, Target = WithFlag(cur, key, value), User = true };
        }

        static List<RegItem> RegItems()
        {
            var list = BaseRegItems();
            // Flip-Modell auch im randlosen Fenster: weniger Eingabeverzögerung, VRR/G-Sync funktioniert
            list.Add(DxFlag(GpuPrefGlobal, "SwapEffectUpgradeEnable", "1"));
            // Spiel immer auf der Hochleistungs-GPU (wichtig bei Notebooks mit Onboard- und Grafikkarte)
            string exe = Store.Get("ogfn.path");
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe)) list.Add(DxFlag(exe, "GpuPreference", "2"));
            return list;
        }

        static List<RegItem> BaseRegItems()
        {
            return new List<RegItem>
            {
                RegItem.Dw(PrioPath, "Win32PrioritySeparation", 38, 2),
                RegItem.Dw(ThrottlePath, "PowerThrottlingOff", 1, null),
                RegItem.Dw(MemPath, "DisablePagingExecutive", 1, 0),
                RegItem.Dw(MemPath, "LargeSystemCache", 0, 0),
                RegItem.Dw(GfxPath, "HwSchMode", 2, 2),
                RegItem.Dw(DwmPath, "OverlayTestMode", 5, null),
                // Windows Multimedia System Profile: 100 % CPU-Zuteilung an Spiele
                RegItem.Dw(SysProfilePath, "SystemResponsiveness", 0, 20),
                RegItem.Dw(SysProfilePath, "NetworkThrottlingIndex", unchecked((int)0xFFFFFFFFu), 10),
                // Games Task Priorität: Maximale Priorität & High Scheduling
                RegItem.Dw(GamesProfilePath, "GPU Priority", 8, 8),
                RegItem.Dw(GamesProfilePath, "Priority", 6, 2),
                RegItem.Str(GamesProfilePath, "Scheduling Category", "High", "Medium"),
                RegItem.Str(GamesProfilePath, "SFIO Priority", "High", "Normal"),
                // Exklusiver Fullscreen / Direct Flip Mode
                RegItem.Dw(GameConfigPath, "GameDVR_FSEBehaviorMode", 2, null, null, true),
                RegItem.Dw(GameConfigPath, "GameDVR_HonorUserFSEBehaviorMode", 1, null, null, true),
                RegItem.Dw(GameConfigPath, "GameDVR_DXGIHonorFSEWindowsCompatible", 1, null, null, true)
            };
        }

        static Dictionary<uint, uint> TargetDrs()
        {
            return new Dictionary<uint, uint>
            {
                { NvDrs.IdPowerManagement, 0x00000001u }, // Max Performance
                { NvDrs.IdThreadedOpt, 0x00000001u },     // Multi-Threaded Optimization On
                { NvDrs.IdPreRenderLimit, 0x00000001u },  // Ultra Low Latency (1 Frame Buffer)
                { NvDrs.IdVsyncMode, 0x08416747u },       // Vsync Force Off
                { 0x00A85214u, 0x00000000u },             // Silk Smoothness Off (keine Glättungsverzögerung)
                { 0x0027EB53u, 0x00000000u }              // Cuda Force P2 State Off (verhindert Taktreduktion)
            };
        }

        static List<IniEntry> EngineEntries()
        {
            const string s = "[SystemSettings]";
            const string log = "[Core.Log]";
            return new List<IniEntry>
            {
                // Multi-Thread Shader-Kompilierung & Pipeline-Cache
                new IniEntry { Section = s, Key = "r.ThreadedShaderCompilation", Value = "1" },
                new IniEntry { Section = s, Key = "r.ShaderPipelineCache.Enabled", Value = "1" },
                new IniEntry { Section = s, Key = "r.ShaderPipelineCache.BatchSize", Value = "32" },
                new IniEntry { Section = s, Key = "r.CreateShadersOnLoad", Value = "1" },

                // 0 Frame Thread Lag: Eliminiert 1 Frame CPU-GPU Pufferzeit -> drastisch weniger Input Delay!
                new IniEntry { Section = s, Key = "r.OneFrameThreadLag", Value = "0" },
                new IniEntry { Section = s, Key = "r.FinishCurrentFrame", Value = "0" },
                new IniEntry { Section = s, Key = "r.GTSyncType", Value = "1" },

                // DirectX 11 Multi-Threading & Async Texture Loading (keine Frame-Drops beim Asset-Streamen)
                new IniEntry { Section = s, Key = "r.D3D11.ZeroInitialise", Value = "0" },
                new IniEntry { Section = s, Key = "r.D3D11.UseAsyncTextureCreation", Value = "1" },
                new IniEntry { Section = s, Key = "r.ParallelRendering", Value = "1" },
                new IniEntry { Section = s, Key = "r.RHICmdBypass", Value = "0" },
                new IniEntry { Section = s, Key = "r.RHICmdWidth", Value = "8" },
                new IniEntry { Section = s, Key = "r.RHIThread.Enable", Value = "1" },

                // Occlusion Culling (verringert Draw-Calls auf der CPU in dichten Zonen)
                new IniEntry { Section = s, Key = "r.HZBOcclusion", Value = "1" },
                new IniEntry { Section = s, Key = "r.AllowOcclusionQueries", Value = "1" },
                new IniEntry { Section = s, Key = "r.EarlyZPass", Value = "2" },

                // Glattes Streaming ohne CPU-Hänger
                new IniEntry { Section = s, Key = "s.AsyncLoadingTime", Value = "2.0" },
                new IniEntry { Section = s, Key = "s.PriorityAsyncLoadingExtraTime", Value = "1.0" },
                new IniEntry { Section = s, Key = "s.LevelStreamingActorsUpdateTimeDuringLevelStreaming", Value = "1.0" },

                // Performance-Fresser deaktivieren
                new IniEntry { Section = s, Key = "r.DynamicRes.OperationMode", Value = "0" },
                new IniEntry { Section = s, Key = "r.CustomDepth", Value = "0" },
                new IniEntry { Section = s, Key = "r.VolumetricFog", Value = "0" },
                new IniEntry { Section = s, Key = "r.DoStaticMeshReflections", Value = "0" },

                // Engine-Logging aus (spart I/O & CPU)
                new IniEntry { Section = s, Key = "bUseLoggingInShipping", Value = "0" },
                new IniEntry { Section = log, Key = "Global", Value = "off" },
                new IniEntry { Section = log, Key = "LogInit", Value = "off" },
                new IniEntry { Section = log, Key = "LogOnline", Value = "off" },
                new IniEntry { Section = log, Key = "LogAnalytics", Value = "off" },
                new IniEntry { Section = log, Key = "LogTelemetry", Value = "off" }
            };
        }

        static List<IniEntry> GameUserEntries()
        {
            const string fn = "[/Script/FortniteGame.FortGameUserSettings]";
            return new List<IniEntry>
            {
                new IniEntry { Section = fn, Key = "bLatencyTweak1", Value = "True" },
                new IniEntry { Section = fn, Key = "LatencyTweak2", Value = "1" },
                new IniEntry { Section = fn, Key = "bUseGPUCrashDebugging", Value = "False" },
                new IniEntry { Section = fn, Key = "bShowViewerCount", Value = "False" },
                new IniEntry { Section = fn, Key = "bDisableMouseAcceleration", Value = "True" },
                // Multithreaded Rendering (DirectX 11): Draw-Calls auf mehrere Kerne verteilt
                new IniEntry { Section = fn, Key = "bAllowMultithreadedRendering", Value = "True" },
                // VSync aus und DirectX 11 statt 12 (14.x ruckelt unter DX12 beim Shader-Kompilieren) - nur wenn das Spiel die Werte führt
                new IniEntry { Section = fn, Key = "bUseVSync", Value = "False", OnlyIfPresent = true },
                new IniEntry { Section = "[D3DRHIPreference]", Key = "bUseD3D12InGame", Value = "False", OnlyIfPresent = true }
            };
        }

        public override bool IsApplied()
        {
            if (IncludeReg && !RegStore.AllSet(RegItems())) return false;

            if (UseNvidiaDrs && NvDrs.IsAvailable())
            {
                var targets = TargetDrs();
                var cur = NvDrs.ReadAll(targets.Keys.ToArray());
                foreach (var kv in targets)
                {
                    uint? v;
                    if (!cur.TryGetValue(kv.Key, out v) || !v.HasValue || v.Value != kv.Value)
                        return false;
                }
            }
            if (File.Exists(EngineIniPath) && !IniEditor.AllIniSet(EngineIniPath, EngineEntries()))
                return false;
            if (File.Exists(GameUserIniPath) && !IniEditor.AllIniSet(GameUserIniPath, GameUserEntries()))
                return false;

            return true;
        }

        public override void Apply()
        {
            if (IncludeReg) RegStore.Apply(Id, RegItems());

            if (UseNvidiaDrs && NvDrs.IsAvailable())
            {
                var targets = TargetDrs();
                var cur = NvDrs.ReadAll(targets.Keys.ToArray());
                var toWrite = new Dictionary<uint, uint?>();
                foreach (var kv in targets)
                {
                    uint? c = cur[kv.Key];
                    string bkKey = BackupPrefix + "drs|" + kv.Key.ToString("X8");
                    if (!c.HasValue || c.Value != kv.Value)
                    {
                        if (Store.Get(bkKey) == null)
                            Store.Set(bkKey, c.HasValue ? "D:" + c.Value.ToString(CultureInfo.InvariantCulture) : "absent");
                        toWrite[kv.Key] = kv.Value;
                    }
                }
                if (toWrite.Count > 0) NvDrs.WriteAll(toWrite);
            }

            IniEditor.ApplyIni(BackupPrefix, EngineIniPath, "eng", EngineEntries());
            IniEditor.ApplyIni(BackupPrefix, GameUserIniPath, "gus", GameUserEntries());

            if (IsDefaultConfigPath && Directory.Exists(RetracConfigDir))
            {
                string rEng = Path.Combine(RetracConfigDir, "Engine.ini");
                string rGus = Path.Combine(RetracConfigDir, "GameUserSettings.ini");
                if (File.Exists(rEng)) IniEditor.ApplyIni(BackupPrefix, rEng, "retrac_eng", EngineEntries());
                if (File.Exists(rGus)) IniEditor.ApplyIni(BackupPrefix, rGus, "retrac_gus", GameUserEntries());
            }

            RamCleaner.PurgeStandbyList();
        }

        public override void Revert()
        {
            bool hasIniOrDrsBackup = Store.Keys(BackupPrefix).Count > 0;
            if (IncludeReg)
            {
                RegStore.Revert(Id, RegItems());
                Reg.DeleteKeyIfEmpty(ThrottlePath);
            }

            if (UseNvidiaDrs && NvDrs.IsAvailable())
            {
                var targets = TargetDrs();
                var toWrite = new Dictionary<uint, uint?>();
                foreach (var id in targets.Keys)
                {
                    string bkKey = BackupPrefix + "drs|" + id.ToString("X8");
                    string saved = Store.Get(bkKey);
                    if (saved != null)
                    {
                        if (saved == "absent") toWrite[id] = null;
                        else if (saved.StartsWith("D:", StringComparison.Ordinal))
                            toWrite[id] = uint.Parse(saved.Substring(2), CultureInfo.InvariantCulture);
                        Store.Set(bkKey, null);
                    }
                    else if (!hasIniOrDrsBackup)
                    {
                        toWrite[id] = null;
                    }
                }
                if (toWrite.Count > 0) NvDrs.WriteAll(toWrite);
            }
            else
            {
                foreach (var k in Store.Keys(BackupPrefix + "drs|")) Store.Set(k, null);
            }

            IniEditor.RevertIni(BackupPrefix, EngineIniPath, "eng", EngineEntries(), hasIniOrDrsBackup);
            IniEditor.RevertIni(BackupPrefix, GameUserIniPath, "gus", GameUserEntries(), hasIniOrDrsBackup);

            if (IsDefaultConfigPath && Directory.Exists(RetracConfigDir))
            {
                string rEng = Path.Combine(RetracConfigDir, "Engine.ini");
                string rGus = Path.Combine(RetracConfigDir, "GameUserSettings.ini");
                if (File.Exists(rEng)) IniEditor.RevertIni(BackupPrefix, rEng, "retrac_eng", EngineEntries(), hasIniOrDrsBackup);
                if (File.Exists(rGus)) IniEditor.RevertIni(BackupPrefix, rGus, "retrac_gus", GameUserEntries(), hasIniOrDrsBackup);
            }
        }

        public override string Snapshot()
        {
            var parts = new List<string>();
            if (IncludeReg) parts.Add(Snap(RegItems()));
            if (UseNvidiaDrs && NvDrs.IsAvailable())
            {
                var ids = TargetDrs().Keys.ToArray();
                var cur = NvDrs.ReadAll(ids);
                parts.Add("drs:" + string.Join(",", ids.Select(id => id.ToString("X8") + "=" + (cur[id].HasValue ? "0x" + cur[id].Value.ToString("X") : "-"))));
            }
            if (File.Exists(EngineIniPath))
            {
                Encoding enc;
                var lines = IniEditor.ReadText(EngineIniPath, out enc).Split('\n').Select(l => l.TrimEnd('\r')).ToList();
                int li, si;
                parts.Add("eng.shd=" + (IniEditor.FindKey(lines, "[SystemSettings]", "r.ThreadedShaderCompilation", out li, out si) ?? "-")
                          + ",log=" + (IniEditor.FindKey(lines, "[Core.Log]", "Global", out li, out si) ?? "-"));
            }
            if (File.Exists(GameUserIniPath))
            {
                Encoding enc;
                var lines = IniEditor.ReadText(GameUserIniPath, out enc).Split('\n').Select(l => l.TrimEnd('\r')).ToList();
                int li, si;
                parts.Add("gus.lat=" + (IniEditor.FindKey(lines, "[/Script/FortniteGame.FortGameUserSettings]", "LatencyTweak2", out li, out si) ?? "-"));
            }
            return string.Join(" | ", parts);
        }
    }

    // ------------------------------------------------------------------------------------------
    // Frankfurt Server-Lock (100 % Frankfurt):
    //   1) Sperrt per Windows-Firewall ausgehendes UDP des Spiels zu ALLEN AWS-Bereichen in Europa außer Frankfurt
    //      (London, Irland, Paris, Stockholm, Mailand, Spanien, Zürich, European Sovereign Cloud; IPv4 + IPv6).
    //      Die Ping-Messung des Matchmakings erreicht dann nur noch Frankfurt (eu-central-1).
    //   2) Match-Server-Wächter: erkennt die App im Match einen Server, der nachweislich nicht in Frankfurt steht
    //      (anderer Hoster/Standort), wird seine Adresse nach dem Match zusätzlich gesperrt.
    //   3) Die Regel gilt nur für die Spiel-Exe; ist der Pfad noch unbekannt, wird sie beim nächsten Spielstart
    //      automatisch auf die Exe eingeschränkt.
    // ------------------------------------------------------------------------------------------
    sealed class ServerLockTweak : Tweak
    {
        const string RulePrefix = "RetracBoost-ServerLock-";
        const string BackupPrefix = "bk|serverlock|";
        const string ActiveKey = BackupPrefix + "active";
        const string LearnedKey = "serverlock.learned";
        const string PendingKey = "serverlock.pending";
        const string ProgramKey = "serverlock.program";
        const string StampKey = "serverlock.stamp";
        const string RuleCountKey = "serverlock.rules";
        const string FnSection = "[/Script/FortniteGame.FortGameUserSettings]";
        const string StandardExe = @"C:\Program Files\Epic Games\Fortnite\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe";
        const int ChunkSize = 200;      // Adressen pro Firewall-Regel (hält die netsh-Befehlszeile kurz)
        const int MaxLearned = 128;

        public override string Id { get { return "serverlock"; } }
        public override string Title { get { return L.T("Server-Lock (nur Frankfurt)", "Server lock (Frankfurt only)"); } }
        public override string Summary
        {
            get
            {
                return L.T("Sperrt alle anderen EU-Standorte (London, Irland, Paris, Stockholm, Mailand, Spanien, Zürich) · Matchmaking sieht nur Frankfurt",
                           "Blocks every other EU location (London, Ireland, Paris, Stockholm, Milan, Spain, Zurich) · matchmaking only sees Frankfurt");
            }
        }

        public override string Detail
        {
            get
            {
                if (!WeApplied)
                    return L.T("Aus: Matchmaking kann dich auf London/Paris/Irland (25-50 ms) setzen", "Off: matchmaking may place you on London/Paris/Ireland (25-50 ms)");
                int learned = Learned().Count;
                string text = L.T("Aktiv: ", "Active: ") + BlockList().Count + L.T(" Bereiche gesperrt, nur Frankfurt erlaubt", " ranges blocked, only Frankfurt allowed");
                if (learned > 0) text += L.T(" · " + learned + " fremde Match-Server gelernt", " · " + learned + " foreign match servers learned");
                if (string.IsNullOrEmpty(Store.Get(ProgramKey))) text += L.T(" · gilt systemweit, bis das Spiel einmal läuft", " · system-wide until the game has run once");
                return text;
            }
        }

        public override bool WeApplied { get { return Store.Get(ActiveKey) == "1"; } }

        /// <summary>Vom Match-Server-Wächter gelernte Adressen.</summary>
        static List<string> Learned()
        {
            return (Store.Get(LearnedKey) ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        static List<string> BlockList()
        {
            var list = ServerLockData.BlockedCidrs.ToList();
            list.AddRange(Learned());
            return list;
        }

        /// <summary>Merkt sich einen nachweislich nicht-Frankfurter Match-Server. true = neu gelernt.</summary>
        public static bool Learn(IPAddress ip)
        {
            if (ip == null || Probe.IsPrivate(ip) || ServerLockData.InFrankfurt(ip) || ServerLockData.InBlocked(ip)) return false;
            var list = Learned();
            string s = ip.ToString();
            if (list.Contains(s)) return false;
            list.Add(s);
            if (list.Count > MaxLearned) list.RemoveRange(0, list.Count - MaxLearned);
            Store.TrySet(LearnedKey, string.Join(",", list));
            Store.TrySet(PendingKey, "1");
            return true;
        }

        /// <summary>true, wenn gelernte Server oder ein bekannt gewordener Spielpfad noch nicht in den Regeln stehen.</summary>
        public bool NeedsRefresh
        {
            get
            {
                if (!WeApplied) return false;
                if (Store.Get(PendingKey) == "1" || Store.Get(StampKey) != ServerLockData.Stamp) return true;
                string exe = GameExe();
                return exe != null && !string.Equals(exe, Store.Get(ProgramKey), StringComparison.OrdinalIgnoreCase);
            }
        }

        static string GameExe()
        {
            string exe = Store.Get("ogfn.path");
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe)) return exe;
            return File.Exists(StandardExe) ? StandardExe : null;
        }

        static bool RuleExists(int n)
        {
            var r = Sh.Netsh("advfirewall firewall show rule name=\"" + RulePrefix + n + "\"");
            return r.Ok && r.Out.Contains(RulePrefix + n);
        }

        public override bool IsApplied()
        {
            int n;
            if (!int.TryParse(Store.Get(RuleCountKey), out n) || n <= 0) return false;
            // Regeln einer älteren Version (andere Datenbasis) gelten als nicht aktiv und werden neu geschrieben.
            if (Store.Get(StampKey) != ServerLockData.Stamp) return false;
            return RuleExists(1) && RuleExists(n);
        }

        public override void Apply()
        {
            RemoveRules();

            string exe = GameExe();
            string progArg = exe != null ? " program=\"" + exe + "\"" : "";
            var all = BlockList();
            int rules = 0;
            for (int i = 0; i < all.Count; i += ChunkSize)
            {
                rules++;
                string ips = string.Join(",", all.Skip(i).Take(ChunkSize));
                var r = Sh.Netsh("advfirewall firewall add rule name=\"" + RulePrefix + rules + "\" dir=out action=block protocol=UDP" + progArg +
                                 " remoteip=" + ips + " enable=yes profile=any");
                if (!r.Ok)
                {
                    RemoveRules();
                    throw new InvalidOperationException(L.T("Firewall-Regel fehlgeschlagen: ", "Firewall rule failed: ") + r.Text);
                }
            }

            Store.Set(RuleCountKey, rules.ToString(CultureInfo.InvariantCulture));
            Store.Set(StampKey, ServerLockData.Stamp);
            Store.Set(ProgramKey, exe);
            Store.Set(PendingKey, null);
            Store.Set(ActiveKey, "1");
            EnsureGameUserRegion();
        }

        public override void Revert()
        {
            RemoveRules();
            foreach (var t in Targets()) IniEditor.RevertIni(BackupPrefix, t.Value, t.Key, RegionEntries(), true);
            foreach (var k in new[] { RuleCountKey, StampKey, ProgramKey, PendingKey, LearnedKey, ActiveKey }) Store.Set(k, null);
        }

        static void RemoveRules()
        {
            // Entfernt alle Regeln dieser App (auch die der älteren Version mit fester Anzahl).
            var r = Sh.PowerShell("Get-NetFirewallRule -DisplayName '" + RulePrefix + "*' -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue;'OK'");
            if (!r.Ok)
                for (int i = 1; i <= 16; i++) Sh.Netsh("advfirewall firewall delete rule name=\"" + RulePrefix + i + "\"");
        }

        static Dictionary<string, string> Targets()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return new Dictionary<string, string>
            {
                { "gus", Path.Combine(local, @"FortniteGame\Saved\Config\WindowsClient\GameUserSettings.ini") },
                { "rgus", Path.Combine(local, @"RetracGame\Saved\Config\WindowsClient\GameUserSettings.ini") }
            };
        }

        static List<IniEntry> RegionEntries()
        {
            return new List<IniEntry> { new IniEntry { Section = FnSection, Key = "RegionId", Value = "EU" } };
        }

        static void EnsureGameUserRegion()
        {
            foreach (var t in Targets())
            {
                try { if (File.Exists(t.Value)) IniEditor.ApplyIni(BackupPrefix, t.Value, t.Key, RegionEntries()); }
                catch { }
            }
        }

        public override string Snapshot()
        {
            return "serverlock=" + (IsApplied() ? "active" : "inactive") + " rules=" + (Store.Get(RuleCountKey) ?? "-")
                   + " ranges=" + BlockList().Count + " learned=" + Learned().Count;
        }
    }

    // ------------------------------------------------------------------------------------------
    // Potato-Grafik (wie EM Tweaks, kostenlos):
    //   1) NVIDIA-Treiberprofil "Fortnite" direkt über nvapi64.dll (DRS)
    //   2) Scalability.ini + Engine.ini: erzwingt r.Streaming.PoolSize=1 & r.Streaming.MipBias=15 auch NACH Cloud-Settings-Login (Frame 380)
    //   3) GameUserSettings.ini: alle ScalabilityGroups auf 0, MotionBlur/Gras/RayTracing aus - danach schreibgeschützt,
    //      damit das Spiel die Werte beim Beenden oder nach dem Login nicht wieder hochsetzt (wie EM Tweaks)
    // ------------------------------------------------------------------------------------------
    sealed class PotatoPreset
    {
        public string Key { get; set; }
        public string Name { get; set; }
        public string Info { get; set; }
        public string ActionLabel { get; set; }
        public string LodLabel { get; set; }
        public uint ReplayModeHex { get; set; }
        public uint DxLodBiasHex { get; set; }
        public string MipBias { get; set; }
        public string MeshBias { get; set; }
    }

    sealed class PotatoTweak : Tweak
    {
        const string BackupPrefix = "bk|potato|";
        const string PresetStoreKey = "potato.preset";
        const string RoPrefix = BackupPrefix + "ro|";

        public string EngineIniPath { get; set; }
        public string GameUserIniPath { get; set; }
        public string ScalabilityIniPath { get; set; }
        public bool UseNvidiaDrs { get; set; }

        string EffectiveScalabilityPath
        {
            get
            {
                if (!string.IsNullOrEmpty(ScalabilityIniPath)) return ScalabilityIniPath;
                if (!string.IsNullOrEmpty(EngineIniPath))
                    return Path.Combine(Path.GetDirectoryName(EngineIniPath), "Scalability.ini");
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"FortniteGame\Saved\Config\WindowsClient");
                return Path.Combine(dir, "Scalability.ini");
            }
        }

        string RetracConfigDir
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"RetracGame\Saved\Config\WindowsClient");
            }
        }

        bool IsDefaultConfigPath
        {
            get
            {
                if (string.IsNullOrEmpty(EngineIniPath)) return false;
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return EngineIniPath.StartsWith(local, StringComparison.OrdinalIgnoreCase);
            }
        }

        public static PotatoPreset[] AllPresets
        {
            get
            {
                return new[]
                {
                    new PotatoPreset
                    {
                        Key = "stealth",
                        Name = L.T("Dezent", "Subtle"),
                        ActionLabel = L.T("Stufe: Dezent", "Level: Subtle"),
                        LodLabel = L.T("+0,375", "+0.375"),
                        Info = L.T("Texturen bleiben scharf. Aus: Schatten, Gras, Bloom, Umgebungsverdeckung, Nachbearbeitung",
                                   "Textures stay sharp. Off: shadows, grass, bloom, ambient occlusion, post-processing"),
                        ReplayModeHex = 0x00000000u,
                        DxLodBiasHex = 0x00000003u,
                        MipBias = "0",
                        MeshBias = "0"
                    },
                    new PotatoPreset
                    {
                        Key = "clean",
                        Name = L.T("Leicht", "Light"),
                        ActionLabel = L.T("Stufe: Leicht", "Level: Light"),
                        LodLabel = L.T("+1,0", "+1.0"),
                        Info = L.T("LOD-Bias +1,0. Glattere Texturen, Hügel und Kanten bleiben normal",
                                   "LOD bias +1.0. Smoother textures, hills and edges stay normal"),
                        ReplayModeHex = 0x00000008u,
                        DxLodBiasHex = 0x00000008u,
                        MipBias = "1",
                        MeshBias = "0"
                    },
                    new PotatoPreset
                    {
                        Key = "medium",
                        Name = L.T("Mittel", "Medium"),
                        ActionLabel = L.T("Stufe: Mittel", "Level: Medium"),
                        LodLabel = L.T("+1,5", "+1.5"),
                        Info = L.T("LOD-Bias +1,5. Typischer Potato-Look, Berge bleiben rund",
                                   "LOD bias +1.5. Typical potato look, hills stay round"),
                        ReplayModeHex = 0x00000008u,
                        DxLodBiasHex = 0x0000000Cu,
                        MipBias = "2",
                        MeshBias = "0"
                    },
                    new PotatoPreset
                    {
                        Key = "strong",
                        Name = L.T("Stark", "Strong"),
                        ActionLabel = L.T("Stufe: Stark", "Level: Strong"),
                        LodLabel = L.T("+3,0", "+3.0"),
                        Info = L.T("LOD-Bias +3,0. Kaum noch Texturen, Berge bleiben rund",
                                   "LOD bias +3.0. Barely any textures left, hills stay round"),
                        ReplayModeHex = 0x00000008u,
                        DxLodBiasHex = 0x00000018u,
                        MipBias = "10",
                        MeshBias = "1"
                    },
                    new PotatoPreset
                    {
                        Key = "ultra",
                        Name = L.T("Extrem", "Extreme"),
                        ActionLabel = L.T("Stufe: Extrem", "Level: Extreme"),
                        LodLabel = L.T("+15", "+15"),
                        Info = L.T("LOD-Bias +15. Keine Texturen mehr, alles sieht aus wie Knete",
                                   "LOD bias +15. No textures left, everything looks like clay"),
                        ReplayModeHex = 0x00000008u,
                        DxLodBiasHex = 0x00000078u,
                        MipBias = "15",
                        MeshBias = "1"
                    }
                };
            }
        }

        string presetKey;
        public string PresetKey
        {
            get { return presetKey ?? Store.Get(PresetStoreKey) ?? "ultra"; }
            set { presetKey = value; }
        }

        public PotatoPreset CurrentPreset
        {
            get
            {
                var all = AllPresets;
                return all.FirstOrDefault(p => string.Equals(p.Key, PresetKey, StringComparison.OrdinalIgnoreCase)) ?? all[4];
            }
        }

        public PotatoTweak()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"FortniteGame\Saved\Config\WindowsClient");
            EngineIniPath = Path.Combine(dir, "Engine.ini");
            GameUserIniPath = Path.Combine(dir, "GameUserSettings.ini");
            UseNvidiaDrs = true;
        }

        public override string Id { get { return "potato"; } }
        public override string Title { get { return L.T("Potato-Grafik", "Potato graphics"); } }
        public override string Summary
        {
            get
            {
                var p = CurrentPreset;
                bool nv = UseNvidiaDrs && NvDrs.IsAvailable();
                return nv
                    ? L.T("NVIDIA LOD-Bias " + p.LodLabel + ", Schatten, Effekte und Gras aus · Spieleinstellungen gesperrt",
                          "NVIDIA LOD bias " + p.LodLabel + ", shadows, effects and grass off · game settings locked")
                    : L.T("LOD-Bias " + p.LodLabel + ", Schatten, Effekte und Gras aus · Spieleinstellungen gesperrt",
                          "LOD bias " + p.LodLabel + ", shadows, effects and grass off · game settings locked");
            }
        }
        public override bool Relevant
        {
            get { return (UseNvidiaDrs && NvDrs.IsAvailable()) || File.Exists(EngineIniPath) || File.Exists(GameUserIniPath) || Directory.Exists(RetracConfigDir); }
        }
        public override bool WeApplied { get { return Store.Keys(BackupPrefix).Count > 0; } }
        public override string Detail
        {
            get
            {
                if (!Relevant)
                    return L.T("Spielkonfiguration noch nicht gefunden – starte Retrac einmal", "Game config not found yet – start Retrac once");
                return Summary;
            }
        }

        static readonly string[] PlasticTextureGroups = new[]
        {
            "TEXTUREGROUP_World",
            "TEXTUREGROUP_WorldNormalMap",
            "TEXTUREGROUP_WorldSpecular",
            "TEXTUREGROUP_Character",
            "TEXTUREGROUP_CharacterNormalMap",
            "TEXTUREGROUP_CharacterSpecular",
            "TEXTUREGROUP_Weapon",
            "TEXTUREGROUP_WeaponNormalMap",
            "TEXTUREGROUP_WeaponSpecular",
            "TEXTUREGROUP_Vehicle",
            "TEXTUREGROUP_VehicleNormalMap",
            "TEXTUREGROUP_VehicleSpecular",
            "TEXTUREGROUP_Cinematic",
            "TEXTUREGROUP_Effects",
            "TEXTUREGROUP_EffectsNotFiltered",
            "TEXTUREGROUP_Skybox",
            "TEXTUREGROUP_RenderTarget"
        };

        List<IniEntry> EngineEntries(PotatoPreset p)
        {
            bool isUltra = string.Equals(p.Key, "ultra", StringComparison.OrdinalIgnoreCase);
            bool isStrong = string.Equals(p.Key, "strong", StringComparison.OrdinalIgnoreCase);
            bool stripTextures = isUltra || isStrong;
            var list = new List<IniEntry>();

            foreach (var s in new[] { "[SystemSettings]", "[ConsoleVariables]" })
            {
                list.Add(new IniEntry { Section = s, Key = "r.MipMapLODBias", Value = s == "[ConsoleVariables]" ? "0" : p.MipBias });
                list.Add(new IniEntry { Section = s, Key = "r.Streaming.MipBias", Value = p.MipBias });
                list.Add(new IniEntry { Section = s, Key = "r.Streaming.PoolSize", Value = stripTextures ? "1" : "1200", KeepOnBlindRevert = true });
                list.Add(new IniEntry { Section = s, Key = "r.Streaming.LimitPoolSizeToVRAM", Value = "0", KeepOnBlindRevert = true });
                list.Add(new IniEntry { Section = s, Key = "r.Streaming.MaxTempMemoryAllowed", Value = stripTextures ? "1" : "256", KeepOnBlindRevert = true });
                list.Add(new IniEntry { Section = s, Key = "r.Streaming.MaxEffectiveScreenSize", Value = stripTextures ? "1" : "0" });
                list.Add(new IniEntry { Section = s, Key = "r.Streaming.UsePerTextureBias", Value = stripTextures ? "0" : "1" });
                list.Add(new IniEntry { Section = s, Key = "r.Streaming.FullyLoadUsedTextures", Value = "0" });
                list.Add(new IniEntry { Section = s, Key = "r.Streaming.UseAllMips", Value = "0" });
                list.Add(new IniEntry { Section = s, Key = "r.Streaming.DropMips", Value = stripTextures ? "2" : "0" });
                list.Add(new IniEntry { Section = s, Key = "r.Streaming.Boost", Value = stripTextures ? "0" : "1", KeepOnBlindRevert = true });
                list.Add(new IniEntry { Section = s, Key = "r.Streaming.HiddenPrimitiveScale", Value = stripTextures ? "0" : "0.5", KeepOnBlindRevert = true });
                list.Add(new IniEntry { Section = s, Key = "r.VT.MaxAnisotropy", Value = "1" });
                list.Add(new IniEntry { Section = s, Key = "r.VT.PoolSizeScale", Value = stripTextures ? "0.01" : "0.5" });
                list.Add(new IniEntry { Section = s, Key = "r.VT.RVT.TileCountBias", Value = stripTextures ? "-10" : "0" });
                list.Add(new IniEntry { Section = s, Key = "r.LandscapeLODBias", Value = "-1" });
                list.Add(new IniEntry { Section = s, Key = "r.LandscapeLOD0DistributionScale", Value = "1.0" });
                list.Add(new IniEntry { Section = s, Key = "r.LandscapeLODDistributionScale", Value = "1.0" });
                list.Add(new IniEntry { Section = s, Key = "r.MinRoughnessOverride", Value = stripTextures ? "1.0" : "0.0" });
                list.Add(new IniEntry { Section = s, Key = "r.ReflectionEnvironment", Value = "0" });
                list.Add(new IniEntry { Section = s, Key = "r.SkyLight.RealTimeReflectionCapture", Value = "0" });
                list.Add(new IniEntry { Section = s, Key = "r.SkyLightingQuality", Value = "0" });
                list.Add(new IniEntry { Section = s, Key = "r.AllowLandscapeShadows", Value = "0" });
                list.Add(new IniEntry { Section = s, Key = "r.AOHeightfieldOcclusion", Value = "0" });
                list.Add(new IniEntry { Section = s, Key = "r.SkeletalMeshLODBias", Value = p.MeshBias });
                list.Add(new IniEntry { Section = s, Key = "r.ParticleLODBias", Value = "2" });
                list.Add(new IniEntry { Section = s, Key = "r.DetailMode", Value = "0" });
                list.Add(new IniEntry { Section = s, Key = "r.MaterialQualityLevel", Value = "0" });
                list.Add(new IniEntry { Section = s, Key = "r.MaxAnisotropy", Value = "1" });
                list.Add(new IniEntry { Section = s, Key = "r.ShadowQuality", Value = "0" });
                list.Add(new IniEntry { Section = s, Key = "r.LightFunctionQuality", Value = "0" });
                list.Add(new IniEntry { Section = s, Key = "r.AmbientOcclusionLevels", Value = "0" });
                list.Add(new IniEntry { Section = s, Key = "r.SSR.Quality", Value = "0" });
                list.Add(new IniEntry { Section = s, Key = "r.DepthOfFieldQuality", Value = "0" });
                list.Add(new IniEntry { Section = s, Key = "r.LensFlareQuality", Value = "0" });
                list.Add(new IniEntry { Section = s, Key = "r.SceneColorFringeQuality", Value = "0" });
                list.Add(new IniEntry { Section = s, Key = "r.BloomQuality", Value = "0" });
                list.Add(new IniEntry { Section = s, Key = "r.EyeAdaptationQuality", Value = "0" });
                list.Add(new IniEntry { Section = s, Key = "r.FastBlurThreshold", Value = "0" });
                list.Add(new IniEntry { Section = s, Key = "foliage.DensityScale", Value = "0" });
                list.Add(new IniEntry { Section = s, Key = "grass.DensityScale", Value = "0" });
                // Nebel aus (klarere Sicht auf Distanz) und grobere Beleuchtung durchsichtiger Effekte (Rauch, Glas, Wasser)
                list.Add(new IniEntry { Section = s, Key = "r.Fog", Value = stripTextures ? "0" : "1" });
                list.Add(new IniEntry { Section = s, Key = "r.TranslucencyLightingVolumeDim", Value = stripTextures ? "4" : "64" });
                list.Add(new IniEntry { Section = s, Key = "r.Decal.FadeDurationScale", Value = "0.1" });
            }
            return list;
        }

        static List<IniEntry> ScalabilityEntries(PotatoPreset p)
        {
            bool isUltra = string.Equals(p.Key, "ultra", StringComparison.OrdinalIgnoreCase);
            bool isStrong = string.Equals(p.Key, "strong", StringComparison.OrdinalIgnoreCase);
            bool stripTextures = isUltra || isStrong;
            var list = new List<IniEntry>();

            foreach (var lvl in new[] { "0", "1", "2", "3", "Cine" })
            {
                string tex = "[TextureQuality@" + lvl + "]";
                list.Add(new IniEntry { Section = tex, Key = "r.Streaming.MipBias", Value = p.MipBias });
                list.Add(new IniEntry { Section = tex, Key = "r.Streaming.PoolSize", Value = stripTextures ? "1" : "400" });
                list.Add(new IniEntry { Section = tex, Key = "r.Streaming.LimitPoolSizeToVRAM", Value = "0" });
                list.Add(new IniEntry { Section = tex, Key = "r.Streaming.MaxEffectiveScreenSize", Value = stripTextures ? "1" : "0" });
                list.Add(new IniEntry { Section = tex, Key = "r.Streaming.UsePerTextureBias", Value = stripTextures ? "0" : "1" });
                list.Add(new IniEntry { Section = tex, Key = "r.Streaming.FullyLoadUsedTextures", Value = "0" });
                list.Add(new IniEntry { Section = tex, Key = "r.Streaming.UseAllMips", Value = "0" });
                list.Add(new IniEntry { Section = tex, Key = "r.Streaming.DropMips", Value = stripTextures ? "2" : "0" });
                list.Add(new IniEntry { Section = tex, Key = "r.Streaming.Boost", Value = stripTextures ? "0" : "1" });
                list.Add(new IniEntry { Section = tex, Key = "r.MaxAnisotropy", Value = "1" });
                list.Add(new IniEntry { Section = tex, Key = "r.VT.MaxAnisotropy", Value = "1" });
                list.Add(new IniEntry { Section = tex, Key = "r.VT.PoolSizeScale", Value = stripTextures ? "0.01" : "0.5" });
                list.Add(new IniEntry { Section = tex, Key = "r.VT.PoolSizeScale.Group0", Value = stripTextures ? "0.01" : "0.5" });
                list.Add(new IniEntry { Section = tex, Key = "r.VT.PoolSizeScale.Group1", Value = stripTextures ? "0.01" : "0.5" });
                list.Add(new IniEntry { Section = tex, Key = "r.VT.PoolSizeScale.Group2", Value = stripTextures ? "0.01" : "0.5" });
                list.Add(new IniEntry { Section = tex, Key = "r.VT.RVT.TileCountBias", Value = stripTextures ? "-10" : "0" });
                list.Add(new IniEntry { Section = tex, Key = "r.VT.RVT.TileCountBias.Group0", Value = stripTextures ? "-10" : "0" });
                list.Add(new IniEntry { Section = tex, Key = "r.VT.RVT.TileCountBias.Group1", Value = stripTextures ? "-10" : "0" });
                list.Add(new IniEntry { Section = tex, Key = "r.VT.RVT.TileCountBias.Group2", Value = stripTextures ? "-10" : "0" });
                list.Add(new IniEntry { Section = tex, Key = "r.VT.MaxUploadsPerFrame", Value = "1" });

                string vd = "[ViewDistanceQuality@" + lvl + "]";
                list.Add(new IniEntry { Section = vd, Key = "r.SkeletalMeshLODBias", Value = p.MeshBias });
                list.Add(new IniEntry { Section = vd, Key = "r.ViewDistanceScale", Value = "2.0" });
                list.Add(new IniEntry { Section = vd, Key = "r.StaticMeshLODDistanceScale", Value = "1" });
                list.Add(new IniEntry { Section = vd, Key = "r.LandscapeLODBias", Value = "-2" });
                list.Add(new IniEntry { Section = vd, Key = "r.LandscapeLOD0DistributionScale", Value = "4.0" });
                list.Add(new IniEntry { Section = vd, Key = "r.LandscapeLODDistributionScale", Value = "4.0" });

                string shd = "[ShadowQuality@" + lvl + "]";
                list.Add(new IniEntry { Section = shd, Key = "r.LightFunctionQuality", Value = "0" });
                list.Add(new IniEntry { Section = shd, Key = "r.ShadowQuality", Value = "0" });
                list.Add(new IniEntry { Section = shd, Key = "r.Shadow.CSM.MaxCascades", Value = "0" });
                list.Add(new IniEntry { Section = shd, Key = "r.Shadow.MaxResolution", Value = "0" });
                list.Add(new IniEntry { Section = shd, Key = "r.Shadow.MaxCSMResolution", Value = "0" });
                list.Add(new IniEntry { Section = shd, Key = "r.Shadow.DistanceScale", Value = "0" });
                list.Add(new IniEntry { Section = shd, Key = "r.DistanceFieldShadowing", Value = "0" });
                list.Add(new IniEntry { Section = shd, Key = "r.DistanceFieldAO", Value = "0" });
                list.Add(new IniEntry { Section = shd, Key = "r.AllowLandscapeShadows", Value = "0" });
                list.Add(new IniEntry { Section = shd, Key = "r.AOQuality", Value = "0" });
                list.Add(new IniEntry { Section = shd, Key = "r.AOHeightfieldOcclusion", Value = "0" });
                list.Add(new IniEntry { Section = shd, Key = "r.VolumetricFog", Value = "0" });
                list.Add(new IniEntry { Section = shd, Key = "r.CapsuleShadows", Value = "0" });
                list.Add(new IniEntry { Section = shd, Key = "r.ContactShadows", Value = "0" });
                list.Add(new IniEntry { Section = shd, Key = "r.SkylightIntensityMultiplier", Value = "1.0" });

                string pp = "[PostProcessQuality@" + lvl + "]";
                list.Add(new IniEntry { Section = pp, Key = "r.MotionBlurQuality", Value = "0" });
                list.Add(new IniEntry { Section = pp, Key = "r.AmbientOcclusionMipLevelFactor", Value = "1.0" });
                list.Add(new IniEntry { Section = pp, Key = "r.AmbientOcclusionMaxQuality", Value = "0" });
                list.Add(new IniEntry { Section = pp, Key = "r.AmbientOcclusionLevels", Value = "0" });
                list.Add(new IniEntry { Section = pp, Key = "r.AmbientOcclusionRadiusScale", Value = "0.0" });
                list.Add(new IniEntry { Section = pp, Key = "r.DepthOfFieldQuality", Value = "0" });
                list.Add(new IniEntry { Section = pp, Key = "r.LensFlareQuality", Value = "0" });
                list.Add(new IniEntry { Section = pp, Key = "r.SceneColorFringeQuality", Value = "0" });
                list.Add(new IniEntry { Section = pp, Key = "r.EyeAdaptationQuality", Value = "0" });
                list.Add(new IniEntry { Section = pp, Key = "r.BloomQuality", Value = "0" });
                list.Add(new IniEntry { Section = pp, Key = "r.FastBlurThreshold", Value = "0" });
                list.Add(new IniEntry { Section = pp, Key = "r.LightShaftQuality", Value = "0" });
                list.Add(new IniEntry { Section = pp, Key = "r.Tonemapper.GrainQuantization", Value = "0" });
                list.Add(new IniEntry { Section = pp, Key = "r.Tonemapper.Quality", Value = "0" });

                string fx = "[EffectsQuality@" + lvl + "]";
                list.Add(new IniEntry { Section = fx, Key = "r.RefractionQuality", Value = "0" });
                list.Add(new IniEntry { Section = fx, Key = "r.SSR.Quality", Value = "0" });
                list.Add(new IniEntry { Section = fx, Key = "r.ReflectionEnvironment", Value = "0" });
                list.Add(new IniEntry { Section = fx, Key = "r.SkyLight.RealTimeReflectionCapture", Value = "0" });
                list.Add(new IniEntry { Section = fx, Key = "r.SkyLightingQuality", Value = "0" });
                list.Add(new IniEntry { Section = fx, Key = "r.DetailMode", Value = "0" });
                list.Add(new IniEntry { Section = fx, Key = "r.TranslucencyVolumeBlur", Value = "0" });
                list.Add(new IniEntry { Section = fx, Key = "r.MaterialQualityLevel", Value = "0" });
                list.Add(new IniEntry { Section = fx, Key = "r.SSS.Scale", Value = "0" });
                list.Add(new IniEntry { Section = fx, Key = "r.SSS.SampleSet", Value = "0" });
                list.Add(new IniEntry { Section = fx, Key = "r.SSS.Quality", Value = "0" });
                list.Add(new IniEntry { Section = fx, Key = "r.SSGI.Quality", Value = "0" });
                list.Add(new IniEntry { Section = fx, Key = "r.ParticleLightQuality", Value = "0" });
                list.Add(new IniEntry { Section = fx, Key = "fx.Niagara.QualityLevel", Value = "0" });
                list.Add(new IniEntry { Section = fx, Key = "Fort.Wind", Value = "0" });
                list.Add(new IniEntry { Section = fx, Key = "p.AnimDynamicsWind", Value = "0" });
                list.Add(new IniEntry { Section = fx, Key = "r.Water.EnableShallowWaterSimulation", Value = "0" });

                string fol = "[FoliageQuality@" + lvl + "]";
                list.Add(new IniEntry { Section = fol, Key = "foliage.DensityScale", Value = "0" });
                list.Add(new IniEntry { Section = fol, Key = "grass.DensityScale", Value = "0" });

                string aa = "[AntiAliasingQuality@" + lvl + "]";
                list.Add(new IniEntry { Section = aa, Key = "r.PostProcessAAQuality", Value = "0" });
                list.Add(new IniEntry { Section = aa, Key = "r.Tonemapper.Sharpen", Value = "0" });
                list.Add(new IniEntry { Section = aa, Key = "r.MinRoughnessOverride", Value = stripTextures ? "1.0" : "0.0" });
            }
            return list;
        }

        static List<IniEntry> GameUserEntries()
        {
            const string sg = "[ScalabilityGroups]";
            const string fn = "[/Script/FortniteGame.FortGameUserSettings]";
            // ViewDistanceQuality=3 (Episch) + r.LandscapeLODBias=-2 sorgt dafür, dass Hügel & Berge rund und ohne Stufen bleiben,
            // während TextureQuality=0 (Niedrig) + ShadingQuality=0 allen Skins, Bäumen und Wänden die Texturdetails entzieht.
            return new List<IniEntry>
            {
                new IniEntry { Section = sg, Key = "sg.ViewDistanceQuality", Value = "3", KeepOnBlindRevert = true },
                new IniEntry { Section = sg, Key = "sg.AntiAliasingQuality", Value = "0", KeepOnBlindRevert = true },
                new IniEntry { Section = sg, Key = "sg.ShadowQuality", Value = "0", KeepOnBlindRevert = true },
                new IniEntry { Section = sg, Key = "sg.PostProcessQuality", Value = "0", KeepOnBlindRevert = true },
                new IniEntry { Section = sg, Key = "sg.TextureQuality", Value = "0", KeepOnBlindRevert = true },
                new IniEntry { Section = sg, Key = "sg.EffectsQuality", Value = "0", KeepOnBlindRevert = true },
                new IniEntry { Section = sg, Key = "sg.FoliageQuality", Value = "0", KeepOnBlindRevert = true },
                new IniEntry { Section = sg, Key = "sg.ShadingQuality", Value = "0", KeepOnBlindRevert = true },
                new IniEntry { Section = fn, Key = "bMotionBlur", Value = "False", OnlyIfPresent = true, KeepOnBlindRevert = true },
                new IniEntry { Section = fn, Key = "bShowGrass", Value = "False", OnlyIfPresent = true, KeepOnBlindRevert = true },
                new IniEntry { Section = fn, Key = "bRayTracing", Value = "False", OnlyIfPresent = true, KeepOnBlindRevert = true },
                new IniEntry { Section = fn, Key = "sg.ViewDistanceQuality", Value = "3", OnlyIfPresent = true, KeepOnBlindRevert = true },
                new IniEntry { Section = fn, Key = "sg.AntiAliasingQuality", Value = "0", OnlyIfPresent = true, KeepOnBlindRevert = true },
                new IniEntry { Section = fn, Key = "sg.ShadowQuality", Value = "0", OnlyIfPresent = true, KeepOnBlindRevert = true },
                new IniEntry { Section = fn, Key = "sg.PostProcessQuality", Value = "0", OnlyIfPresent = true, KeepOnBlindRevert = true },
                new IniEntry { Section = fn, Key = "sg.TextureQuality", Value = "0", OnlyIfPresent = true, KeepOnBlindRevert = true },
                new IniEntry { Section = fn, Key = "sg.EffectsQuality", Value = "0", OnlyIfPresent = true, KeepOnBlindRevert = true },
                new IniEntry { Section = fn, Key = "sg.FoliageQuality", Value = "0", OnlyIfPresent = true, KeepOnBlindRevert = true }
            };
        }

        static Dictionary<uint, uint> TargetDrs(PotatoPreset p)
        {
            return new Dictionary<uint, uint>
            {
                { NvDrs.IdTransparencySupersampling, p.ReplayModeHex },
                { NvDrs.IdDriverControlledLodBias, 0x00000000u },
                { NvDrs.IdDxLodBias, p.DxLodBiasHex },
                { NvDrs.IdNegativeLodBias, 0x00000000u },
                { NvDrs.IdTextureQuality, 0x00000014u },
                { NvDrs.IdTrilinearOpt, 0x00000000u },
                { NvDrs.IdAnisoSampleOpt, 0x00000001u }
            };
        }

        public override bool IsApplied()
        {
            if (!Relevant) return false;
            var p = CurrentPreset;
            bool anyChecked = false;

            if (UseNvidiaDrs && NvDrs.IsAvailable())
            {
                anyChecked = true;
                var targets = TargetDrs(p);
                var cur = NvDrs.ReadAll(targets.Keys.ToArray());
                foreach (var kv in targets)
                {
                    uint? v;
                    if (!cur.TryGetValue(kv.Key, out v) || !v.HasValue || v.Value != kv.Value)
                        return false;
                }
            }
            if (File.Exists(EngineIniPath))
            {
                anyChecked = true;
                if (!IniEditor.AllIniSet(EngineIniPath, EngineEntries(p))) return false;
                string scaPath = EffectiveScalabilityPath;
                if (!File.Exists(scaPath) || !IniEditor.AllIniSet(scaPath, ScalabilityEntries(p))) return false;
            }
            if (File.Exists(GameUserIniPath))
            {
                anyChecked = true;
                if (!IniEditor.AllIniSet(GameUserIniPath, GameUserEntries())) return false;
            }
            return anyChecked;
        }

        /// <summary>
        /// Entfernt Zeilen, die nur ältere Versionen geschrieben haben (Texturgruppen in [SystemSettings]).
        /// Eigener Tag, damit die Sicherungen der aktuellen Werte und der "Abschnitt angelegt"-Vermerk unberührt bleiben;
        /// Schlüssel, die die aktuelle Version selbst verwaltet, fasst sie nicht an.
        /// </summary>
        void CleanLegacyEngineLods()
        {
            if (!File.Exists(EngineIniPath)) return;
            var legacy = new List<IniEntry>
            {
                new IniEntry { Section = "[SystemSettings]", Key = "TEXTUREGROUP_Terrain_Heightmap", Value = "" },
                new IniEntry { Section = "[SystemSettings]", Key = "TEXTUREGROUP_Terrain_Weightmap", Value = "" },
                new IniEntry { Section = "[SystemSettings]", Key = "r.StaticMeshLODDistanceScale", Value = "" }
            };
            foreach (var tg in PlasticTextureGroups)
                legacy.Add(new IniEntry { Section = "[SystemSettings]", Key = tg, Value = "" });
            IniEditor.RevertIni(BackupPrefix, EngineIniPath, "legacy", legacy, false);
        }

        public override void Apply()
        {
            if (!Relevant) throw new InvalidOperationException(L.T("Keine Spielkonfiguration oder NVIDIA-Profil gefunden.", "No game config or NVIDIA profile found."));
            var p = CurrentPreset;

            if (UseNvidiaDrs && NvDrs.IsAvailable())
            {
                var targets = TargetDrs(p);
                var cur = NvDrs.ReadAll(targets.Keys.ToArray());
                var toWrite = new Dictionary<uint, uint?>();
                foreach (var kv in targets)
                {
                    uint? c = cur[kv.Key];
                    string bkKey = BackupPrefix + "drs|" + kv.Key.ToString("X8");
                    if (!c.HasValue || c.Value != kv.Value)
                    {
                        if (Store.Get(bkKey) == null)
                            Store.Set(bkKey, c.HasValue ? "D:" + c.Value.ToString(CultureInfo.InvariantCulture) : "absent");
                        toWrite[kv.Key] = kv.Value;
                    }
                }
                if (toWrite.Count > 0) NvDrs.WriteAll(toWrite);
            }

            CleanLegacyEngineLods();
            IniEditor.ApplyIni(BackupPrefix, EngineIniPath, "eng", EngineEntries(p));
            IniEditor.ApplyIni(BackupPrefix, GameUserIniPath, "gus", GameUserEntries());
            LockFile(GameUserIniPath, "gus");

            if (File.Exists(EngineIniPath))
            {
                string scaPath = EffectiveScalabilityPath;
                if (!File.Exists(scaPath))
                {
                    if (Store.Get(BackupPrefix + "sca|file") == null)
                        Store.Set(BackupPrefix + "sca|file", "absent");
                    File.WriteAllText(scaPath, "", new UTF8Encoding(false));
                }
                IniEditor.ApplyIni(BackupPrefix, scaPath, "sca", ScalabilityEntries(p));
                try { File.SetAttributes(scaPath, File.GetAttributes(scaPath) | FileAttributes.ReadOnly); } catch { }
            }

            if (IsDefaultConfigPath && Directory.Exists(RetracConfigDir))
            {
                string rEng = Path.Combine(RetracConfigDir, "Engine.ini");
                string rSca = Path.Combine(RetracConfigDir, "Scalability.ini");
                string rGus = Path.Combine(RetracConfigDir, "GameUserSettings.ini");

                try
                {
                    if (!File.Exists(rEng))
                    {
                        if (Store.Get(BackupPrefix + "reng|file") == null)
                            Store.Set(BackupPrefix + "reng|file", "absent");
                        File.WriteAllText(rEng, "", new UTF8Encoding(false));
                    }
                    IniEditor.ApplyIni(BackupPrefix, rEng, "reng", EngineEntries(p));
                    File.SetAttributes(rEng, File.GetAttributes(rEng) | FileAttributes.ReadOnly);
                }
                catch { }

                try
                {
                    if (!File.Exists(rSca))
                    {
                        if (Store.Get(BackupPrefix + "rsca|file") == null)
                            Store.Set(BackupPrefix + "rsca|file", "absent");
                        File.WriteAllText(rSca, "", new UTF8Encoding(false));
                    }
                    IniEditor.ApplyIni(BackupPrefix, rSca, "rsca", ScalabilityEntries(p));
                    File.SetAttributes(rSca, File.GetAttributes(rSca) | FileAttributes.ReadOnly);
                }
                catch { }

                try
                {
                    if (File.Exists(rGus))
                    {
                        IniEditor.ApplyIni(BackupPrefix, rGus, "rgus", GameUserEntries());
                        LockFile(rGus, "rgus");
                    }
                }
                catch { }
            }
        }

        /// <summary>Setzt den Schreibschutz und merkt sich, ob er vorher schon gesetzt war.</summary>
        static void LockFile(string path, string tag)
        {
            if (!File.Exists(path)) return;
            var attrs = File.GetAttributes(path);
            string key = RoPrefix + tag;
            if (Store.Get(key) == null) Store.Set(key, (attrs & FileAttributes.ReadOnly) != 0 ? "1" : "0");
            File.SetAttributes(path, attrs | FileAttributes.ReadOnly);
        }

        /// <summary>Stellt den ursprünglichen Schreibschutz-Zustand wieder her.</summary>
        static void UnlockFile(string path, string tag)
        {
            string key = RoPrefix + tag;
            string saved = Store.Get(key);
            if (saved == null) return;
            if (saved == "0" && File.Exists(path))
                File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            Store.Set(key, null);
        }

        public override void Revert()
        {
            bool hasAnyBackup = WeApplied;
            var p = CurrentPreset;

            if (UseNvidiaDrs && NvDrs.IsAvailable())
            {
                var targets = TargetDrs(p);
                var toWrite = new Dictionary<uint, uint?>();
                foreach (var id in targets.Keys)
                {
                    string bkKey = BackupPrefix + "drs|" + id.ToString("X8");
                    string saved = Store.Get(bkKey);
                    if (saved != null)
                    {
                        if (saved == "absent") toWrite[id] = null;
                        else if (saved.StartsWith("D:", StringComparison.Ordinal))
                            toWrite[id] = uint.Parse(saved.Substring(2), CultureInfo.InvariantCulture);
                        Store.Set(bkKey, null);
                    }
                    else if (!hasAnyBackup)
                    {
                        toWrite[id] = null;
                    }
                }
                if (toWrite.Count > 0) NvDrs.WriteAll(toWrite);
            }
            else
            {
                foreach (var k in Store.Keys(BackupPrefix + "drs|")) Store.Set(k, null);
            }

            IniEditor.RevertIni(BackupPrefix, EngineIniPath, "eng", EngineEntries(p), hasAnyBackup);
            IniEditor.RevertIni(BackupPrefix, GameUserIniPath, "gus", GameUserEntries(), hasAnyBackup);
            UnlockFile(GameUserIniPath, "gus");

            string scaPath = EffectiveScalabilityPath;
            try
            {
                if (File.Exists(scaPath))
                    File.SetAttributes(scaPath, File.GetAttributes(scaPath) & ~FileAttributes.ReadOnly);
            }
            catch { }
            IniEditor.RevertIni(BackupPrefix, scaPath, "sca", ScalabilityEntries(p), hasAnyBackup);
            string scaFileBk = Store.Get(BackupPrefix + "sca|file");
            if (scaFileBk == "absent" || (File.Exists(scaPath) && string.IsNullOrWhiteSpace(File.ReadAllText(scaPath))))
            {
                try { if (File.Exists(scaPath)) File.Delete(scaPath); } catch { }
            }
            if (scaFileBk != null) Store.Set(BackupPrefix + "sca|file", null);

            if (IsDefaultConfigPath && Directory.Exists(RetracConfigDir))
            {
                string rEng = Path.Combine(RetracConfigDir, "Engine.ini");
                string rSca = Path.Combine(RetracConfigDir, "Scalability.ini");
                string rGus = Path.Combine(RetracConfigDir, "GameUserSettings.ini");

                try
                {
                    if (File.Exists(rEng))
                        File.SetAttributes(rEng, File.GetAttributes(rEng) & ~FileAttributes.ReadOnly);
                    IniEditor.RevertIni(BackupPrefix, rEng, "reng", EngineEntries(p), hasAnyBackup);
                    string rEngBk = Store.Get(BackupPrefix + "reng|file");
                    if (rEngBk == "absent" || (File.Exists(rEng) && string.IsNullOrWhiteSpace(File.ReadAllText(rEng))))
                        if (File.Exists(rEng)) File.Delete(rEng);
                    if (rEngBk != null) Store.Set(BackupPrefix + "reng|file", null);
                }
                catch { }

                try
                {
                    if (File.Exists(rSca))
                        File.SetAttributes(rSca, File.GetAttributes(rSca) & ~FileAttributes.ReadOnly);
                    IniEditor.RevertIni(BackupPrefix, rSca, "rsca", ScalabilityEntries(p), hasAnyBackup);
                    string rScaBk = Store.Get(BackupPrefix + "rsca|file");
                    if (rScaBk == "absent" || (File.Exists(rSca) && string.IsNullOrWhiteSpace(File.ReadAllText(rSca))))
                        if (File.Exists(rSca)) File.Delete(rSca);
                    if (rScaBk != null) Store.Set(BackupPrefix + "rsca|file", null);
                }
                catch { }

                try
                {
                    if (File.Exists(rGus))
                        IniEditor.RevertIni(BackupPrefix, rGus, "rgus", GameUserEntries(), hasAnyBackup);
                    UnlockFile(rGus, "rgus");
                }
                catch { }
            }
        }

        public override string Snapshot()
        {
            var parts = new List<string>();
            if (UseNvidiaDrs && NvDrs.IsAvailable())
            {
                var ids = TargetDrs(CurrentPreset).Keys.ToArray();
                var cur = NvDrs.ReadAll(ids);
                parts.Add("drs:" + string.Join(",", ids.Select(id => id.ToString("X8") + "=" + (cur[id].HasValue ? "0x" + cur[id].Value.ToString("X") : "-"))));
            }
            if (File.Exists(EngineIniPath))
            {
                Encoding enc;
                var lines = IniEditor.ReadText(EngineIniPath, out enc).Split('\n').Select(l => l.TrimEnd('\r')).ToList();
                int li, si;
                parts.Add("eng.mip=" + (IniEditor.FindKey(lines, "[SystemSettings]", "r.MipMapLODBias", out li, out si) ?? "-")
                          + ",bloom=" + (IniEditor.FindKey(lines, "[SystemSettings]", "r.BloomQuality", out li, out si) ?? "-"));
            }
            if (File.Exists(GameUserIniPath))
            {
                Encoding enc;
                var lines = IniEditor.ReadText(GameUserIniPath, out enc).Split('\n').Select(l => l.TrimEnd('\r')).ToList();
                int li, si;
                parts.Add("gus.shd=" + (IniEditor.FindKey(lines, "[ScalabilityGroups]", "sg.ShadingQuality", out li, out si) ?? "-")
                          + ",tex=" + (IniEditor.FindKey(lines, "[ScalabilityGroups]", "sg.TextureQuality", out li, out si) ?? "-"));
            }
            return string.Join(" | ", parts);
        }
    }
}
