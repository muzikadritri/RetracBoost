using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace RetracBoost
{
    /// <summary>Ein einzelner Registry-Wert, den ein Tweak setzt.</summary>
    sealed class RegItem
    {
        public string Path, Name, Group;
        public RegistryValueKind Kind;
        public object Target;
        public object WinDefault;      // Windows-Standard (null = Wert existiert standardmäßig nicht)
        public bool HasDefault;
        public bool User;              // true = HKCU, sonst HKLM

        public const string UserPrefix = "HKCU\\";

        public string BackupKey(string id) { return "bk|" + id + "|" + (User ? UserPrefix : "") + Path + "|" + Name; }

        public static RegItem Dw(string path, string name, int target, object winDefault, string group = null, bool user = false)
        {
            return new RegItem { Path = path, Name = name, Kind = RegistryValueKind.DWord, Target = target, WinDefault = winDefault, HasDefault = true, Group = group, User = user };
        }

        public static RegItem Str(string path, string name, string target, string winDefault, bool user = false)
        {
            return new RegItem { Path = path, Name = name, Kind = RegistryValueKind.String, Target = target, WinDefault = winDefault, HasDefault = true, User = user };
        }
    }

    /// <summary>Sichert Originalwerte vor dem Schreiben und stellt sie exakt wieder her.</summary>
    static class RegStore
    {
        static string Encode(object v)
        {
            if (v == null) return "absent";
            if (v is int) return "D:" + ((int)v).ToString(CultureInfo.InvariantCulture);
            return "S:" + Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        static object Decode(string s, out RegistryValueKind kind)
        {
            kind = RegistryValueKind.String;
            if (s == null || s == "absent") return null;
            if (s.StartsWith("D:")) { kind = RegistryValueKind.DWord; return int.Parse(s.Substring(2), CultureInfo.InvariantCulture); }
            return s.Substring(2);
        }

        public static bool AllSet(IEnumerable<RegItem> items)
        {
            foreach (var it in items)
                if (!Reg.Equal(Reg.Get(it.Path, it.Name, it.User), it.Target)) return false;
            return true;
        }

        /// <summary>Liefert die Einträge, die tatsächlich geändert wurden.</summary>
        public static List<RegItem> Apply(string id, IEnumerable<RegItem> items)
        {
            var changed = new List<RegItem>();
            foreach (var it in items)
            {
                object cur = Reg.Get(it.Path, it.Name, it.User);
                if (Reg.Equal(cur, it.Target)) continue;
                string key = it.BackupKey(id);
                if (Store.Get(key) == null) Store.Set(key, Encode(cur));   // wirft bei Fehler -> es wird nichts geändert
                Reg.Set(it.Path, it.Name, it.Target, it.Kind, it.User);
                changed.Add(it);
            }
            return changed;
        }

        public static List<RegItem> Revert(string id, IEnumerable<RegItem> items)
        {
            var changed = new List<RegItem>();
            string prefix = "bk|" + id + "|";
            var handled = new HashSet<string>();
            // Haben wir irgendetwas geändert, fassen wir nur genau das wieder an. Nur wenn es gar keine Sicherung gibt
            // (der Nutzer schaltet etwas aus, das schon vorher aktiv war), gilt der Windows-Standard.
            bool hasAnyBackup = Store.Keys(prefix).Count > 0;

            foreach (var it in items)
            {
                string key = it.BackupKey(id);
                handled.Add(key);
                string saved = Store.Get(key);
                if (saved == null && hasAnyBackup) continue;
                RegistryValueKind kind = it.Kind;
                object orig = null;
                bool useDefault = saved == null;
                if (!useDefault)
                {
                    orig = Decode(saved, out kind);
                    // Der gesicherte Wert entspricht schon dem Tweak (war vorher gesetzt) -> auf Windows-Standard zurück.
                    if (Reg.Equal(orig, it.Target)) useDefault = true;
                }
                if (useDefault)
                {
                    if (!it.HasDefault) { Store.Set(key, null); continue; }
                    orig = it.WinDefault;
                    kind = it.Kind;
                }
                if (WriteBack(it.Path, it.Name, orig, kind, it.User)) changed.Add(it);
                if (saved != null) Store.Set(key, null);
            }

            // Sicherungen zu Einträgen, die es jetzt nicht mehr gibt (z. B. Adapter getrennt).
            foreach (var key in Store.Keys(prefix))
            {
                if (handled.Contains(key)) continue;
                var parts = key.Split('|');
                if (parts.Length != 4) { Store.Set(key, null); continue; }
                RegistryValueKind kind;
                object orig = Decode(Store.Get(key), out kind);
                string path = parts[2];
                bool user = path.StartsWith(RegItem.UserPrefix, StringComparison.Ordinal);
                if (user) path = path.Substring(RegItem.UserPrefix.Length);
                try { WriteBack(path, parts[3], orig, kind, user); } catch { }
                Store.Set(key, null);
            }
            return changed;
        }

        public static bool HasBackup(string id) { return Store.Keys("bk|" + id + "|").Count > 0; }

        static bool WriteBack(string path, string name, object value, RegistryValueKind kind, bool user)
        {
            object cur = Reg.Get(path, name, user);
            if (Reg.Equal(cur, value)) return false;
            if (value == null) Reg.Delete(path, name, user); else Reg.Set(path, name, value, kind, user);
            return true;
        }
    }

    abstract class Tweak
    {
        public abstract string Id { get; }
        public abstract string Title { get; }
        public abstract string Summary { get; }
        /// <summary>Text unter dem Titel; darf dynamisch sein.</summary>
        public virtual string Detail { get { return Summary; } }
        /// <summary>false, wenn es im Moment nichts zu tun gibt (z. B. kein Spiel gewählt).</summary>
        public virtual bool Relevant { get { return true; } }
        public virtual string BusyText { get { return L.T("Wird angewendet …", "Applying …"); } }

        public abstract bool IsApplied();
        public abstract void Apply();
        public abstract void Revert();
        /// <summary>Textabbild des relevanten Systemzustands (für den Selbsttest).</summary>
        public abstract string Snapshot();
        /// <summary>true, wenn von uns gesicherte Originalwerte existieren (wir haben etwas verändert).</summary>
        public virtual bool WeApplied { get { return RegStore.HasBackup(Id); } }

        protected static string Snap(IEnumerable<RegItem> items)
        {
            return string.Join("\n", items.Select(i =>
            {
                var v = Reg.Get(i.Path, i.Name, i.User);
                return i.Path.Substring(Math.Max(0, i.Path.Length - 28)) + "|" + i.Name + "=" + (v == null ? "(none)" : Convert.ToString(v, CultureInfo.InvariantCulture));
            }));
        }
    }

    // ------------------------------------------------------------------------------------------
    // 1) Ping-Booster (Netzwerk-Stack & UDP-Fastpath):
    //    - Winsock AFD UDP-Fastpath (FastSendDatagramThreshold / FastCopyReceiveThreshold = 1500 Byte MTU)
    //      sendet & empfängt Spiel-UDP-Pakete synchron im Kernel ohne IRP-Warteschlange (Standard: 1024)
    //    - 64 KB Socket-Puffer gegen Paketverlust in Endgame-Fights
    //    - TCP-Nagle & Delayed-ACKs auf allen Adaptern aus, Port-Pool erweitert
    //    - MMCSS-Netzwerkdrosselung aus, Spiele-Scheduler auf High + Latency Sensitive
    //    - UDP Receive Offload (URO, Windows 11) aus: eingehende Spielpakete werden nicht mehr gebündelt,
    //      sondern einzeln und sofort zugestellt (weniger Jitter)
    // ------------------------------------------------------------------------------------------
    sealed class NetStackTweak : Tweak
    {
        const string Tcp = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\";
        const string TcpGlobal = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters";
        const string Afd = @"SYSTEM\CurrentControlSet\Services\AFD\Parameters";
        const string Mm = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
        const string UroKey = "net.uro";      // bewusst ohne "bk|net|"-Präfix: RegStore räumt diese Schlüssel sonst weg

        public override string Id { get { return "net"; } }
        public override string Title { get { return L.T("Ping-Booster (Netzwerk & UDP)", "Ping booster (network & UDP)"); } }
        public override string Summary
        {
            get { return L.T("UDP-Fastpath (1500 B), UDP-Bündelung aus, keine verzögerten ACKs, Drosselung aus & Echtzeit-Spiele-Scheduler", "UDP fast-path (1500 B), UDP coalescing off, no delayed ACKs, throttling off & low-latency game scheduler"); }
        }

        static List<RegItem> Items()
        {
            var list = new List<RegItem>();
            foreach (var nic in Nics.Active(false))
            {
                string p = Tcp + nic.Id;
                list.Add(RegItem.Dw(p, "TcpAckFrequency", 1, null));
                list.Add(RegItem.Dw(p, "TCPNoDelay", 1, null));
                list.Add(RegItem.Dw(p, "TcpDelAckTicks", 0, null));
            }
            // Winsock Kernel (afd.sys): UDP-Pakete bis 1500 Byte ohne IRP-Queue direkt durchreichen
            list.Add(RegItem.Dw(Afd, "FastSendDatagramThreshold", 1500, null));
            list.Add(RegItem.Dw(Afd, "FastCopyReceiveThreshold", 1500, null));
            list.Add(RegItem.Dw(Afd, "DefaultSendWindow", 65536, null));
            list.Add(RegItem.Dw(Afd, "DefaultReceiveWindow", 65536, null));

            // Globaler TCP/IP-Stack
            list.Add(RegItem.Dw(TcpGlobal, "MaxUserPort", 65534, null));
            list.Add(RegItem.Dw(TcpGlobal, "TcpTimedWaitDelay", 30, null));
            list.Add(RegItem.Dw(TcpGlobal, "DefaultTTL", 64, null));

            // Multimedia-Klassenplaner (MMCSS)
            list.Add(RegItem.Dw(Mm, "NetworkThrottlingIndex", unchecked((int)0xFFFFFFFF), 10));
            list.Add(RegItem.Dw(Mm, "SystemResponsiveness", 0, 20));
            string g = Mm + @"\Tasks\Games";
            list.Add(RegItem.Dw(g, "GPU Priority", 8, 8));
            list.Add(RegItem.Dw(g, "Priority", 6, 2));
            list.Add(RegItem.Str(g, "Scheduling Category", "High", "Medium"));
            list.Add(RegItem.Str(g, "SFIO Priority", "High", "Normal"));
            list.Add(RegItem.Str(g, "Latency Sensitive", "True", null));
            return list;
        }

        public override bool IsApplied() { return RegStore.AllSet(Items()); }
        public override bool WeApplied { get { return RegStore.HasBackup(Id) || Store.Get(UroKey) != null; } }

        public override void Apply()
        {
            RegStore.Apply(Id, Items());
            // URO gibt es erst ab Windows 11 / Server 2022 - auf älteren Systemen schlägt der Befehl fehl und es bleibt alles, wie es ist.
            if (Store.Get(UroKey) == null && Sh.Netsh("int udp set global uro=disabled").Ok) Store.Set(UroKey, "1");
        }

        public override void Revert()
        {
            RegStore.Revert(Id, Items());
            if (Store.Get(UroKey) == null) return;
            Sh.Netsh("int udp set global uro=enabled");
            Store.Set(UroKey, null);
        }

        public override string Snapshot() { return Snap(Items()) + "\nuro=" + (Store.Get(UroKey) ?? "-"); }
    }

    // ------------------------------------------------------------------------------------------
    // 2) Netzwerkkarte: Energiesparfunktionen, Interrupt-Moderation, Paket-Bündelung & Offloads
    // ------------------------------------------------------------------------------------------
    sealed class AdapterTweak : Tweak
    {
        const string ClassPath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

        // Standard- und gängige Herstellerschlüssel mit Zielwert. Gesetzt wird nur, was der Treiber
        // selbst als gültige Auswahl anbietet (Aufzählung oder Zahlenbereich).
        static readonly string[,] Keywords =
        {
            // Interrupts & Bündelung: jedes Paket sofort an die CPU
            { "*InterruptModeration", "0" }, { "ITR", "0" }, { "*PacketCoalescing", "0" },
            { "TxIntDelay", "0" }, { "RxIntDelay", "0" }, { "TxAbsIntDelay", "0" }, { "RxAbsIntDelay", "0" },
            // Energiesparen: Karte taktet nie herunter und schläft nicht ein
            { "*EEE", "0" }, { "AdvancedEEE", "0" }, { "EEELinkAdvertisement", "0" }, { "EnableGreenEthernet", "0" },
            { "GigaLite", "0" }, { "PowerSavingMode", "0" }, { "ULPMode", "0" }, { "*SelectiveSuspend", "0" },
            { "AutoPowerSaveModeEnabled", "0" }, { "EnableConnectedPowerGating", "0" }, { "EnableDynamicPowerGating", "0" },
            { "ReduceSpeedOnPowerDown", "0" }, { "*DeviceSleepOnDisconnect", "0" },
            // Kein Pausieren des Datenstroms, keine großen Sammel-Pakete
            { "*FlowControl", "0" }, { "*LsoV2IPv4", "0" }, { "*LsoV2IPv6", "0" }, { "*RscIPv4", "0" }, { "*RscIPv6", "0" },
            // Empfang auf mehrere CPU-Kerne verteilen
            { "*RSS", "1" },
            // WLAN (Intel u. a.): kein Energiesparen der Antennen, kein ständiges Suchen nach anderen Access Points
            { "MIMOPowerSaveMode", "3" }, { "RoamAggressiveness", "0" }
        };

        const int PnpNoPowerOff = 24;   // "Computer kann das Gerät ausschalten, um Energie zu sparen" aus

        public override string Id { get { return "adapter"; } }
        public override string Title { get { return L.T("Netzwerkkarte (Low-Latency)", "Network adapter (low-latency)"); } }
        public override string Summary
        {
            get { return L.T("Interrupt-Moderation, Paket-Bündelung, Energiesparen & WLAN-Roaming aus, RSS an · Karte verbindet kurz neu", "Interrupt moderation, packet coalescing, power saving & Wi-Fi roaming off, RSS on · adapter reconnects briefly"); }
        }
        public override string BusyText { get { return L.T("Karte wird neu verbunden …", "Reconnecting adapter …"); } }

        static List<RegItem> Items()
        {
            var items = new List<RegItem>();
            var nics = Nics.Active().Where(n => n.Physical).ToList();
            if (nics.Count == 0) return items;

            foreach (var sub in Reg.SubKeys(ClassPath))
            {
                if (sub.Length != 4) continue;
                string path = ClassPath + "\\" + sub;
                var inst = Reg.Get(path, "NetCfgInstanceId") as string;
                if (inst == null) continue;
                var nic = nics.FirstOrDefault(n => string.Equals(n.Id, inst, StringComparison.OrdinalIgnoreCase));
                if (nic == null) continue;

                for (int i = 0; i < Keywords.GetLength(0); i++)
                {
                    string kw = Keywords[i, 0], target = Keywords[i, 1];
                    if (!Accepts(path + @"\Ndi\params\" + kw, target)) continue;
                    items.Add(new RegItem { Path = path, Name = kw, Kind = RegistryValueKind.String, Target = target, Group = nic.Name });
                }
                items.Add(new RegItem { Path = path, Name = "PnPCapabilities", Kind = RegistryValueKind.DWord, Target = PnpNoPowerOff, Group = nic.Name });
            }
            return items;
        }

        /// <summary>true, wenn der Treiber die Option kennt und den Zielwert als Auswahl oder im Zahlenbereich anbietet.</summary>
        static bool Accepts(string ndi, string target)
        {
            if (Reg.Get(ndi, "default") == null) return false;                       // Treiber kennt die Option nicht
            if (Reg.Get(ndi + @"\enum", target) != null) return true;               // gültige Auswahl
            string type = (Convert.ToString(Reg.Get(ndi, "type"), CultureInfo.InvariantCulture) ?? "").ToLowerInvariant();
            if (type != "int" && type != "long" && type != "word" && type != "dword") return false;
            long t, min, max;
            if (!long.TryParse(target, NumberStyles.Integer, CultureInfo.InvariantCulture, out t)) return false;
            bool hasMin = long.TryParse(Convert.ToString(Reg.Get(ndi, "min"), CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out min);
            bool hasMax = long.TryParse(Convert.ToString(Reg.Get(ndi, "max"), CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out max);
            return (!hasMin || t >= min) && (!hasMax || t <= max);
        }

        /// <summary>Aktueller Wert; hat der Treiber nichts geschrieben, gilt der Standardwert aus seiner Parameterliste.</summary>
        static string Current(RegItem it)
        {
            var v = Reg.Get(it.Path, it.Name) ?? Reg.Get(it.Path + @"\Ndi\params\" + it.Name, "default");
            return v == null ? null : Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        static string Want(RegItem it) { return Convert.ToString(it.Target, CultureInfo.InvariantCulture); }

        public override bool IsApplied()
        {
            return Items().All(it => Current(it) == Want(it));
        }

        public override string Snapshot()
        {
            return string.Join("\n", Items().Select(i => (i.Group ?? "") + "|" + i.Name + "=" + (Current(i) ?? "(none)")));
        }

        public override void Apply()
        {
            var pending = Items().Where(it => Current(it) != Want(it)).ToList();
            var changed = RegStore.Apply(Id, pending);
            Restart(changed.Select(c => c.Group).Distinct().ToList());
        }

        public override void Revert()
        {
            var changed = RegStore.Revert(Id, Items());
            Restart(changed.Select(c => c.Group).Where(g => g != null).Distinct().ToList());
        }

        static void Restart(List<string> names)
        {
            foreach (var name in names)
            {
                var r = Sh.PowerShell("Restart-NetAdapter -Name " + Sh.Q(name) + " -Confirm:$false; 'OK'");
                if (!r.Ok) throw new InvalidOperationException(L.T("Adapter-Neustart fehlgeschlagen: ", "Adapter restart failed: ") + r.Text);
                WaitForLink(name, 20000);
            }
        }

        static void WaitForLink(string name, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (Nics.Active(false).Any(n => n.Name == name)) return;
                System.Threading.Thread.Sleep(500);
            }
        }
    }

    // ------------------------------------------------------------------------------------------
    // 3) Energiesparplan
    // ------------------------------------------------------------------------------------------
    sealed class PowerTweak : Tweak
    {
        const string High = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
        const string Ultimate = "e9a42b02-d5df-448d-aa00-03f14749eb61";
        const string Balanced = "381b4222-f694-41f0-9685-ba4fa59e3fc9";
        const string OrigKey = "bk|power|orig";
        const string OursKey = "power.ours";
        static readonly Regex Guid = new Regex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");

        public override string Id { get { return "power"; } }
        public override string Title { get { return L.T("Energiesparplan", "Power plan"); } }
        public override string Summary
        {
            get
            {
                return HasBattery()
                    ? L.T("Höchstleistung – kein Heruntertakten der CPU", "High performance – no CPU down-clocking")
                    : L.T("Ultimative Leistung – keine CPU-Schlafzustände, kein Heruntertakten", "Ultimate performance – no CPU sleep states, no down-clocking");
            }
        }

        static bool? battery;

        /// <summary>Notebook erkennen: dort bleibt es bei Höchstleistung, damit Akku und Temperatur im Rahmen bleiben.</summary>
        static bool HasBattery()
        {
            if (battery.HasValue) return battery.Value;
            try
            {
                using (var s = new System.Management.ManagementObjectSearcher("SELECT DeviceID FROM Win32_Battery"))
                    battery = s.Get().Count > 0;
            }
            catch { battery = false; }
            return battery.Value;
        }

        static string Active()
        {
            var r = Sh.PowerCfg("/getactivescheme");
            var m = Guid.Match(r.Out);
            return m.Success ? m.Value.ToLowerInvariant() : null;
        }

        /// <summary>Minimaler Prozessorzustand des aktiven Plans bei Netzbetrieb in % (100 = kein Heruntertakten).</summary>
        static int? MinProcessorAc()
        {
            var r = Sh.PowerCfg("/query SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMIN");
            // Ausgabe (sprachabhängig beschriftet): Min, Max, Schrittweite, aktueller AC-Index, aktueller DC-Index
            var hex = Regex.Matches(r.Out, "0x[0-9a-fA-F]{8}");
            if (hex.Count < 5) return null;
            return Convert.ToInt32(hex[3].Value.Substring(2), 16);
        }

        public override bool IsApplied()
        {
            var a = Active();
            if (a != null && (a == High || a == Ultimate || a == Store.Get(OursKey))) return true;
            // Auch selbst angelegte Kopien von Höchstleistung/Ultimative Leistung zählen.
            var min = MinProcessorAc();
            return min.HasValue && min.Value >= 100;
        }

        public override string Snapshot() { return "scheme=" + Active(); }

        public override void Apply()
        {
            string cur = Active();
            if (IsApplied()) return;                       // schon Höchstleistung/Ultimate: nicht herabstufen
            if (cur != null && Store.Get(OrigKey) == null) Store.Set(OrigKey, cur);

            if (!HasBattery() && ActivateCopy(Ultimate) == null) return;

            var r = Sh.PowerCfg("/setactive " + High);
            if (r.Ok) return;

            // Auf manchen Systemen (Modern Standby) existiert der Plan nicht - dann aus der Vorlage erzeugen.
            string err = ActivateCopy(High);
            if (err != null) throw new InvalidOperationException(L.T("Energiesparplan nicht verfügbar: ", "Power plan unavailable: ") + err);
        }

        /// <summary>Aktiviert eine (wiederverwendete oder neu angelegte) Kopie der Vorlage. null = Erfolg, sonst Fehlertext.</summary>
        static string ActivateCopy(string template)
        {
            string ours = Store.Get(OursKey);
            if (ours != null && Schemes().Contains(ours) && Sh.PowerCfg("/setactive " + ours).Ok) return null;

            var dup = Sh.PowerCfg("/duplicatescheme " + template);
            var m = Guid.Match(dup.Out);
            if (!dup.Ok || !m.Success) return dup.Text;
            string created = m.Value.ToLowerInvariant();
            var set = Sh.PowerCfg("/setactive " + created);
            if (!set.Ok)
            {
                Sh.PowerCfg("/delete " + created);
                return set.Text;
            }
            Store.Set(OursKey, created);
            return null;
        }

        static List<string> Schemes()
        {
            var r = Sh.PowerCfg("/list");
            return Guid.Matches(r.Out).Cast<Match>().Select(m => m.Value.ToLowerInvariant()).Distinct().ToList();
        }

        static int? MinProcessorOf(string scheme)
        {
            var r = Sh.PowerCfg("/query " + scheme + " SUB_PROCESSOR PROCTHROTTLEMIN");
            var hex = Regex.Matches(r.Out, "0x[0-9a-fA-F]{8}");
            return hex.Count >= 5 ? (int?)Convert.ToInt32(hex[3].Value.Substring(2), 16) : null;
        }

        public override void Revert()
        {
            var existing = Schemes();
            string active = Active();
            string target = null;

            // 1) der gesicherte ursprüngliche Plan, falls er noch existiert
            string orig = Store.Get(OrigKey);
            if (orig != null && existing.Contains(orig)) target = orig;

            // 2) sonst irgendein vorhandener Plan, der den Prozessor nicht dauerhaft auf 100 % hält
            if (target == null)
            {
                var candidates = existing.Where(s => s != active && s != High && s != Ultimate && (MinProcessorOf(s) ?? 100) < 100).ToList();
                target = candidates.FirstOrDefault(s => s.StartsWith("381b4222")) ?? candidates.FirstOrDefault();
            }

            // 3) sonst Ausbalanciert aus der Windows-Vorlage neu anlegen
            if (target == null)
            {
                var dup = Sh.PowerCfg("/duplicatescheme " + Balanced);
                var m = Guid.Match(dup.Out);
                if (!dup.Ok || !m.Success) throw new InvalidOperationException(L.T("Kein anderer Energiesparplan verfügbar: ", "No other power plan available: ") + dup.Text);
                target = m.Value.ToLowerInvariant();
            }

            var r = Sh.PowerCfg("/setactive " + target);
            if (!r.Ok) throw new InvalidOperationException(r.Text);

            // Den selbst angelegten Plan wieder entfernen, damit sich keine Kopien ansammeln.
            string ours = Store.Get(OursKey);
            if (ours != null && ours != target && existing.Contains(ours)) Sh.PowerCfg("/delete " + ours);
            Store.Set(OrigKey, null);
            Store.Set(OursKey, null);
        }
    }

    // ------------------------------------------------------------------------------------------
    // 4) DNS: schnellsten Resolver messen und (auf Wunsch) setzen
    // ------------------------------------------------------------------------------------------
    sealed class DnsProvider
    {
        public string Name;
        public string[] Servers;
    }

    sealed class DnsResult
    {
        public string Name;
        public string Server;
        public double? Score;
    }

    sealed class DnsPlan
    {
        public DnsResult Current;
        public DnsResult Best;
        public DnsProvider BestProvider;
        public bool Switch;
        public List<DnsResult> All = new List<DnsResult>();
    }

    sealed class DnsTweak : Tweak
    {
        public static readonly DnsProvider[] Providers =
        {
            new DnsProvider { Name = "Cloudflare", Servers = new[] { "1.1.1.1", "1.0.0.1" } },
            new DnsProvider { Name = "Google", Servers = new[] { "8.8.8.8", "8.8.4.4" } },
            new DnsProvider { Name = "Quad9", Servers = new[] { "9.9.9.9", "149.112.112.112" } },
            new DnsProvider { Name = "OpenDNS", Servers = new[] { "208.67.222.222", "208.67.220.220" } }
        };

        static readonly string[] CachedNames = { "www.google.com", "www.wikipedia.org", "www.amazon.com" };
        static readonly Random Rnd = new Random();

        public DnsPlan Plan { get; private set; }

        public override string Id { get { return "dns"; } }
        public override string Title { get { return "DNS"; } }
        public override string Summary { get { return L.T("Schnellsten Resolver messen und verwenden", "Measure and use the fastest resolver"); } }
        public override bool Relevant { get { return Plan != null; } }

        public override string Detail
        {
            get
            {
                var p = Plan;
                if (p == null) return L.T("Messe Antwortzeiten …", "Measuring response times …");
                if (HasBackup)
                {
                    return L.T("Aktiv: ", "Active: ") + (p.Current != null ? p.Current.Name : "") + (p.Current != null && p.Current.Score.HasValue ? " · " + Fmt(p.Current.Score.Value) : "");
                }
                string cur = p.Current != null ? p.Current.Name : L.T("Aktuell", "Current");
                bool curOk = p.Current != null && p.Current.Score.HasValue;
                if (p.Switch)
                {
                    string was = curOk ? Fmt(p.Current.Score.Value) : L.T("keiner Antwort", "no reply");
                    return p.BestProvider.Name + " · " + Fmt(p.Best.Score.Value) + L.T(" statt ", " instead of ") + was;
                }
                if (curOk)
                {
                    string ms = Fmt(p.Current.Score.Value);
                    return L.T("Dein DNS (" + cur + ") ist mit " + ms + " bereits schnell",
                               "Your DNS (" + cur + ") is already fast at " + ms);
                }
                return L.T("Kein Resolver erreichbar", "No resolver reachable");
            }
        }

        public bool HasBackup { get { return Store.Keys("bk|dns|").Count > 0; } }
        /// <summary>Schalter nutzbar: es gibt etwas umzustellen oder etwas zurückzusetzen.</summary>
        public bool CanToggle { get { return HasBackup || (Plan != null && Plan.Switch); } }

        static string Fmt(double ms) { return ms.ToString(ms < 10 ? "0.0" : "0", CultureInfo.CurrentCulture) + " ms"; }

        public override bool IsApplied()
        {
            if (HasBackup) return true;
            return Plan != null && !Plan.Switch;     // aktueller DNS ist bereits gut genug
        }

        public override string Snapshot()
        {
            return string.Join("\n", Nics.Active(false).Select(n => n.Name + "=" + (Reg.Get(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + n.Id, "NameServer") ?? "")));
        }

        // ---- Messung ----------------------------------------------------------------------

        public DnsPlan Benchmark()
        {
            var nics = Nics.Active();
            var primary = Nics.Primary(nics);
            IPAddress cur = primary == null ? null : primary.Dns.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);

            var jobs = new List<Task<DnsResult>>();
            DnsProvider currentIsProvider = cur == null ? null : Providers.FirstOrDefault(p => p.Servers.Contains(cur.ToString()));

            if (cur != null)
            {
                string label = currentIsProvider != null ? currentIsProvider.Name : IsPrivate(cur) ? L.T("Router", "Router") + " " + cur : cur.ToString();
                var c = cur;
                jobs.Add(Task.Run(() => Measure(label, c)));
            }
            foreach (var p in Providers)
            {
                if (currentIsProvider == p) continue;
                var prov = p;
                jobs.Add(Task.Run(() => Measure(prov.Name, IPAddress.Parse(prov.Servers[0]))));
            }
            Task.WaitAll(jobs.ToArray());

            var plan = new DnsPlan();
            foreach (var j in jobs) plan.All.Add(j.Result);
            plan.Current = cur == null ? null : plan.All[0];

            var ranked = plan.All.Where(r => r.Score.HasValue && Providers.Any(p => p.Servers.Contains(r.Server))).OrderBy(r => r.Score.Value).ToList();
            if (ranked.Count > 0)
            {
                plan.Best = ranked[0];
                plan.BestProvider = Providers.First(p => p.Servers.Contains(plan.Best.Server));
                bool bestIsCurrent = currentIsProvider == plan.BestProvider;
                if (!bestIsCurrent)
                {
                    double? c = plan.Current == null ? null : plan.Current.Score;
                    // Nur umstellen, wenn es spürbar besser ist (mind. 20 % und 3 ms).
                    plan.Switch = c == null || (plan.Best.Score.Value <= c.Value * 0.8 && c.Value - plan.Best.Score.Value >= 3);
                }
            }
            Plan = plan;
            return plan;
        }

        static bool IsPrivate(IPAddress a)
        {
            var b = a.GetAddressBytes();
            return b.Length == 4 && (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168));
        }

        static DnsResult Measure(string name, IPAddress server)
        {
            var res = new DnsResult { Name = name, Server = server.ToString() };

            // Bekannte Namen (Cache-Treffer): nach einem Warm-up zweimal messen.
            var cached = new List<double>();
            foreach (var n in CachedNames)
            {
                Query(server, n);
                for (int i = 0; i < 2; i++) { var t = Query(server, n); if (t.HasValue) cached.Add(t.Value); }
            }
            // Zufällige Namen: erzwingen eine echte Auflösung (NXDOMAIN ist eine gültige Antwort).
            var fresh = new List<double>();
            for (int i = 0; i < 3; i++)
            {
                var t = Query(server, "rb" + Rnd.Next(100000000, 999999999) + ".com");
                if (t.HasValue) fresh.Add(t.Value);
            }
            if (cached.Count >= 3 && fresh.Count >= 1)
                res.Score = (Median(cached) + Median(fresh)) / 2.0;
            else if (cached.Count >= 3)
                res.Score = Median(cached);
            return res;
        }

        static double Median(List<double> v)
        {
            var s = v.OrderBy(x => x).ToList();
            int m = s.Count / 2;
            return s.Count % 2 == 1 ? s[m] : (s[m - 1] + s[m]) / 2.0;
        }

        /// <summary>Eine UDP-DNS-Abfrage (A-Record). Liefert die Antwortzeit in ms oder null.</summary>
        static double? Query(IPAddress server, string name)
        {
            try
            {
                using (var udp = new UdpClient(AddressFamily.InterNetwork))
                {
                    udp.Client.ReceiveTimeout = 1200;
                    udp.Client.SendTimeout = 1200;
                    udp.Connect(server, 53);
                    ushort id = (ushort)Rnd.Next(1, 65535);
                    var q = new List<byte> { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };
                    foreach (var label in name.Split('.'))
                    {
                        var b = System.Text.Encoding.ASCII.GetBytes(label);
                        q.Add((byte)b.Length);
                        q.AddRange(b);
                    }
                    q.AddRange(new byte[] { 0, 0, 1, 0, 1 });
                    var packet = q.ToArray();

                    var clock = Stopwatch.StartNew();
                    udp.Send(packet, packet.Length);
                    IPEndPoint any = null;
                    var r = udp.Receive(ref any);
                    clock.Stop();
                    if (r.Length < 12 || ((r[0] << 8) | r[1]) != id) return null;
                    int rcode = r[3] & 0x0F;
                    if (rcode != 0 && rcode != 3) return null;
                    return clock.Elapsed.TotalMilliseconds;
                }
            }
            catch { return null; }
        }

        // ---- Anwenden / Zurücksetzen ------------------------------------------------------

        const string IfPath = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\";

        public override void Apply()
        {
            var plan = Plan;
            if (plan == null || !plan.Switch || plan.BestProvider == null)
                throw new InvalidOperationException(L.T("Kein besserer DNS gefunden.", "No better DNS found."));

            // Nur echte Netzwerkkarten - VPN- und virtuelle Adapter bleiben unangetastet.
            var nics = Nics.Active().Where(n => n.Physical).ToList();
            if (nics.Count == 0) throw new InvalidOperationException(L.T("Keine aktive Verbindung.", "No active connection."));

            foreach (var nic in nics)
            {
                string key = "bk|dns|" + nic.Id;
                if (Store.Get(key) == null)
                    Store.Set(key, (Reg.Get(IfPath + nic.Id, "NameServer") as string ?? "") + "|" + nic.Name);

                var s = plan.BestProvider.Servers;
                var r = Sh.Netsh("interface ipv4 set dnsservers name=\"" + nic.Name + "\" source=static address=" + s[0] + " validate=no");
                if (!r.Ok) throw new InvalidOperationException(r.Text);
                for (int i = 1; i < s.Length; i++)
                    Sh.Netsh("interface ipv4 add dnsservers name=\"" + nic.Name + "\" address=" + s[i] + " index=" + (i + 1) + " validate=no");
            }
            Sh.Ipconfig("/flushdns");
        }

        public override void Revert()
        {
            foreach (var key in Store.Keys("bk|dns|"))
            {
                string saved = Store.Get(key) ?? "";
                int cut = saved.LastIndexOf('|');
                string servers = cut >= 0 ? saved.Substring(0, cut) : saved;
                string name = cut >= 0 ? saved.Substring(cut + 1) : "";
                string guid = key.Substring("bk|dns|".Length);

                // Der Adaptername kann sich geändert haben - über die GUID neu auflösen.
                var nic = Nics.Active().FirstOrDefault(n => string.Equals(n.Id, guid, StringComparison.OrdinalIgnoreCase));
                if (nic != null) name = nic.Name;
                if (nic == null) { Store.Set(key, null); continue; }

                var list = servers.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
                ShResult r;
                if (list.Length == 0)
                    r = Sh.Netsh("interface ipv4 set dnsservers name=\"" + name + "\" source=dhcp");
                else
                {
                    r = Sh.Netsh("interface ipv4 set dnsservers name=\"" + name + "\" source=static address=" + list[0] + " validate=no");
                    for (int i = 1; i < list.Length; i++)
                        Sh.Netsh("interface ipv4 add dnsservers name=\"" + name + "\" address=" + list[i] + " index=" + (i + 1) + " validate=no");
                }
                if (!r.Ok) throw new InvalidOperationException(r.Text);
                Store.Set(key, null);
            }
            Sh.Ipconfig("/flushdns");
        }
    }

    // ------------------------------------------------------------------------------------------
    // 5) Spiel: DSCP-Markierung (QoS) + dauerhaft hohe CPU-Priorität.
    //    Die Priorität setzt Windows selbst beim Start des Spiels (Image File Execution Options),
    //    dieses Programm öffnet nie ein Handle auf den Spielprozess - wichtig bei Anti-Cheat.
    // ------------------------------------------------------------------------------------------
    sealed class GameTweak : Tweak
    {
        const string NlaPath = @"SYSTEM\CurrentControlSet\Services\Tcpip\QoS";
        const string IfeoPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\";
        const string NlaId = "qosnla";                 // gemeinsame Sicherung für alle QoS-Regeln dieser App
        const string OursPrefix = "qos.ours|";
        const string PolicyPrefix = "RetracBoost";
        static readonly Regex SafeExe = new Regex(@"^[\w\.\- \(\)\[\]]+\.exe$", RegexOptions.IgnoreCase);

        readonly string id, policy, title;

        public GameTweak() : this("game", "RetracBoost", null) { }

        public GameTweak(string id, string policy, string title)
        {
            this.id = id;
            this.policy = policy;
            this.title = title;
        }

        public string Exe { get; set; }

        public override string Id { get { return id; } }
        public override string Title { get { return title ?? L.T("Spiel- & Paket-Priorität", "Game & packet priority"); } }
        public override string Summary
        {
            get { return L.T("Echtzeit-Paketpriorität (DSCP 46) sowie hohe CPU-, I/O- & RAM-Priorität", "Real-time packet priority (DSCP 46) plus high CPU, I/O & RAM priority"); }
        }
        public override bool Relevant { get { return !string.IsNullOrEmpty(Exe); } }
        public override string Detail
        {
            get
            {
                if (!Relevant) return L.T("Wähle dein Spiel, um es zu priorisieren", "Pick your game to prioritise it");
                string text = Exe + " · " + L.T("DSCP 46 + CPU-, I/O- & RAM-Priorität hoch", "DSCP 46 + CPU, I/O & RAM priority high");
                if (CoveredByOther) text += L.T(" · gemeinsam mit der Spiel-Zeile im Boost-Tab", " · shared with the Game row on the Boost tab");
                return text;
            }
        }

        public override bool WeApplied { get { return Store.Get(OursPrefix + policy) == "1" || RegStore.HasBackup(Id); } }

        static RegItem Nla() { return RegItem.Str(NlaPath, "Do not use NLA", "1", null); }

        List<RegItem> Items()
        {
            var list = new List<RegItem>();
            if (Relevant && SafeExe.IsMatch(Exe))
            {
                string perf = IfeoPath + Exe + @"\PerfOptions";
                list.Add(RegItem.Dw(perf, "CpuPriorityClass", 3, null));   // 3 = Hoch
                list.Add(RegItem.Dw(perf, "IoPriority", 3, null));         // 3 = Hohe SSD/Disk-I/O-Priorität
                list.Add(RegItem.Dw(perf, "PagePriority", 5, null));       // 5 = Höchste normale RAM-Seitenpriorität
            }
            return list;
        }

        static void CheckExe(string exe)
        {
            if (string.IsNullOrEmpty(exe) || !SafeExe.IsMatch(exe))
                throw new InvalidOperationException(L.T("Ungültiger Programmname.", "Invalid program name."));
        }

        public static void BoostLiveProcess(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return;
            try
            {
                string procName = Path.GetFileNameWithoutExtension(exe);
                foreach (var p in Process.GetProcessesByName(procName))
                {
                    using (p)
                    {
                        try { p.PriorityBoostEnabled = true; } catch { }
                        try { p.PriorityClass = ProcessPriorityClass.High; } catch { }
                    }
                }
            }
            catch { }
        }

        /// <summary>true, wenn die Exe nicht durch die eigene, sondern durch eine andere Regel dieser App abgedeckt ist.</summary>
        public bool CoveredByOther { get; private set; }

        public override bool IsApplied()
        {
            if (!Relevant) return false;
            CheckExe(Exe);
            if (!RegStore.AllSet(Items()) || !RegStore.AllSet(new[] { Nla() })) return false;
            // OWN = unsere Regel; OTHER = andere RetracBoost-Regel (z. B. vom Boost-Tab) deckt dieselbe Exe ab
            string script =
                "$all=@(Get-NetQosPolicy -PolicyStore localhost -ErrorAction SilentlyContinue | Where-Object { $_.Name -like '" + PolicyPrefix + "*' -and $_.AppPathName -ieq " + Sh.Q(Exe) + " -and $_.DSCPAction -eq 46 });" +
                "if(@($all | Where-Object { $_.Name -eq " + Sh.Q(policy) + " }).Count -gt 0){'OWN'}elseif($all.Count -gt 0){'OTHER'}else{'NO'}";
            var r = Sh.PowerShell(script);
            if (!r.Ok) return false;
            CoveredByOther = r.Out.Contains("OTHER");
            return r.Out.Contains("OWN") || CoveredByOther;
        }

        public override void Apply()
        {
            CheckExe(Exe);
            RegStore.Apply(NlaId, new[] { Nla() });
            RegStore.Apply(Id, Items());
            string script =
                "Get-NetQosPolicy -PolicyStore localhost -Name " + Sh.Q(policy) + " -ErrorAction SilentlyContinue | Remove-NetQosPolicy -PolicyStore localhost -Confirm:$false;" +
                "$cov=@(Get-NetQosPolicy -PolicyStore localhost -ErrorAction SilentlyContinue | Where-Object { $_.Name -like '" + PolicyPrefix + "*' -and $_.AppPathName -ieq " + Sh.Q(Exe) + " -and $_.DSCPAction -eq 46 });" +
                "if($cov.Count -eq 0){New-NetQosPolicy -PolicyStore localhost -Name " + Sh.Q(policy) + " -AppPathNameMatchCondition " + Sh.Q(Exe) +
                " -DSCPAction 46 -NetworkProfile All | Out-Null};'OK'";
            var r = Sh.PowerShell(script);
            if (!r.Ok) throw new InvalidOperationException(r.Text);
            Store.Set(OursPrefix + policy, "1");
            BoostLiveProcess(Exe);
        }

        public override string Snapshot()
        {
            var r = Sh.PowerShell("$p=Get-NetQosPolicy -PolicyStore localhost -Name " + Sh.Q(policy) + " -ErrorAction SilentlyContinue;if($p){'policy'}else{'none'}");
            return "qos=" + r.Out.Trim() + "\n" + Snap(new[] { Nla() }) + "\n" + Snap(Items());
        }

        public override void Revert()
        {
            var r = Sh.PowerShell(
                "Get-NetQosPolicy -PolicyStore localhost -Name " + Sh.Q(policy) + " -ErrorAction SilentlyContinue | Remove-NetQosPolicy -PolicyStore localhost -Confirm:$false;" +
                // wie viele unserer Regeln bleiben übrig?
                "$n=@(Get-NetQosPolicy -PolicyStore localhost -ErrorAction SilentlyContinue | Where-Object { $_.Name -like '" + PolicyPrefix + "*' }).Count;'LEFT='+$n");
            if (!r.Ok) throw new InvalidOperationException(r.Text);
            Store.Set(OursPrefix + policy, null);

            // Vom Nutzer vorher selbst gesetzte Werte (keine Sicherung vorhanden) bleiben unangetastet.
            var mine = Items().Where(i => Store.Get(i.BackupKey(Id)) != null).ToList();
            RegStore.Revert(Id, mine);
            foreach (var it in mine)
            {
                Reg.DeleteKeyIfEmpty(it.Path);                                              // ...\PerfOptions
                Reg.DeleteKeyIfEmpty(it.Path.Substring(0, it.Path.LastIndexOf((char)92)));  // ...\<exe>
            }

            // Der gemeinsame NLA-Wert wird erst zurückgesetzt, wenn keine unserer Regeln mehr existiert.
            var m = Regex.Match(r.Out, @"LEFT=(\d+)");
            if (m.Success && m.Groups[1].Value == "0" && Store.Get("bk|" + NlaId + "|" + NlaPath + "|Do not use NLA") != null)
                RegStore.Revert(NlaId, new[] { Nla() });
        }
    }
}
