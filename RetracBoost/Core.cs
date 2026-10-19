using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Microsoft.Win32;

namespace RetracBoost
{
    /// <summary>Kleine DE/EN-Umschaltung anhand der Windows-Sprache.</summary>
    static class L
    {
        static readonly bool De = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de";
        public static string T(string de, string en) { return De ? de : en; }
    }

    /// <summary>
    /// Persistenz für Einstellungen und für die Originalwerte, die wir vor jeder Änderung sichern.
    /// Liegt in ProgramData, damit die Sicherung unabhängig vom Benutzerkonto gefunden wird.
    /// </summary>
    static class Store
    {
        static readonly object Gate = new object();
        static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RetracBoost");
        static readonly string FilePath = Path.Combine(Dir, "state.ini");
        static Dictionary<string, string> data;

        static void Load()
        {
            if (data != null) return;
            data = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    int i = line.IndexOf('=');
                    if (i <= 0) continue;
                    data[Uri.UnescapeDataString(line.Substring(0, i))] = Uri.UnescapeDataString(line.Substring(i + 1));
                }
            }
            catch { }
        }

        public static string Get(string key)
        {
            lock (Gate) { Load(); string v; return data.TryGetValue(key, out v) ? v : null; }
        }

        public static List<string> Keys(string prefix)
        {
            lock (Gate) { Load(); return data.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList(); }
        }

        /// <summary>Schreibt sofort auf die Platte und wirft bei Fehlern - Backups dürfen nie still verloren gehen.</summary>
        public static void Set(string key, string value)
        {
            lock (Gate)
            {
                Load();
                if (value == null) data.Remove(key); else data[key] = value;
                Directory.CreateDirectory(Dir);
                var lines = data.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)).ToArray();
                File.WriteAllLines(FilePath, lines, Encoding.UTF8);
            }
        }

        public static void TrySet(string key, string value)
        {
            try { Set(key, value); } catch { }
        }
    }

    sealed class ShResult
    {
        public int Code;
        public string Out = "", Err = "";
        public bool Ok { get { return Code == 0; } }
        public string Text { get { return (Out + " " + Err).Trim(); } }
    }

    /// <summary>Startet Systemprogramme unsichtbar und liefert Ausgabe + Exitcode.</summary>
    static class Sh
    {
        static readonly string Sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        static readonly Encoding Oem = GetOem();

        static Encoding GetOem()
        {
            try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
            catch { return Encoding.UTF8; }
        }

        public static ShResult Run(string file, string args, int timeoutMs, Encoding enc)
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = enc,
                StandardErrorEncoding = enc
            };
            using (var p = Process.Start(psi))
            {
                var o = p.StandardOutput.ReadToEndAsync();
                var e = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    return new ShResult { Code = -1, Err = "Zeitüberschreitung / timeout" };
                }
                p.WaitForExit();
                return new ShResult { Code = p.ExitCode, Out = o.Result, Err = e.Result };
            }
        }

        public static ShResult Netsh(string args) { return Run(Path.Combine(Sys, "netsh.exe"), args, 30000, Oem); }
        public static ShResult PowerCfg(string args) { return Run(Path.Combine(Sys, "powercfg.exe"), args, 30000, Oem); }
        public static ShResult Ipconfig(string args) { return Run(Path.Combine(Sys, "ipconfig.exe"), args, 30000, Oem); }

        /// <summary>Führt ein PowerShell-Skript aus. Fehler kommen als "ERR: ..." in Out, Code ist dann 1.</summary>
        public static ShResult PowerShell(string script, int timeoutMs = 90000)
        {
            string full =
                "$ProgressPreference='SilentlyContinue';$ErrorActionPreference='Stop';" +
                "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8;" +
                "try{" + script + "}catch{[Console]::Out.WriteLine('ERR: '+$_.Exception.Message);exit 1}";
            string enc = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
            string exe = Path.Combine(Sys, @"WindowsPowerShell\v1.0\powershell.exe");
            return Run(exe, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + enc, timeoutMs, Encoding.UTF8);
        }

        /// <summary>Maskiert einen Text für ein PowerShell-Literal in einfachen Anführungszeichen.</summary>
        public static string Q(string s) { return "'" + (s ?? "").Replace("'", "''") + "'"; }
    }

    /// <summary>Zugriff auf HKLM bzw. HKCU (immer die 64-Bit-Sicht).</summary>
    static class Reg
    {
        static RegistryKey Base(bool user)
        {
            return RegistryKey.OpenBaseKey(user ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, RegistryView.Registry64);
        }

        public static object Get(string path, string name, bool user = false)
        {
            using (var b = Base(user))
            using (var k = b.OpenSubKey(path))
                return k == null ? null : k.GetValue(name, null);
        }

        public static string[] SubKeys(string path, bool user = false)
        {
            using (var b = Base(user))
            using (var k = b.OpenSubKey(path))
                return k == null ? new string[0] : k.GetSubKeyNames();
        }

        /// <summary>Löscht den Schlüssel, falls er weder Werte noch Unterschlüssel enthält.</summary>
        public static void DeleteKeyIfEmpty(string path, bool user = false)
        {
            using (var b = Base(user))
            {
                using (var k = b.OpenSubKey(path))
                    if (k == null || k.SubKeyCount > 0 || k.ValueCount > 0) return;
                b.DeleteSubKey(path, false);
            }
        }

        public static void DeleteTree(string path, bool user = false)
        {
            using (var b = Base(user))
                b.DeleteSubKeyTree(path, false);
        }

        public static string[] ValueNames(string path, bool user = false)
        {
            using (var b = Base(user))
            using (var k = b.OpenSubKey(path))
                return k == null ? new string[0] : k.GetValueNames();
        }

        public static void Set(string path, string name, object value, RegistryValueKind kind, bool user = false)
        {
            using (var b = Base(user))
            using (var k = b.CreateSubKey(path))
                k.SetValue(name, value, kind);
        }

        public static void Delete(string path, string name, bool user = false)
        {
            using (var b = Base(user))
            using (var k = b.OpenSubKey(path, true))
                if (k != null && k.GetValue(name, null) != null) k.DeleteValue(name, false);
        }

        public static bool Equal(object a, object b)
        {
            if (a == null || b == null) return a == null && b == null;
            if (a is int && b is int) return (int)a == (int)b;
            return string.Equals(Convert.ToString(a, CultureInfo.InvariantCulture), Convert.ToString(b, CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
        }
    }

    sealed class Nic
    {
        public string Name, Id, Description;
        public int Index;
        public bool Physical;
        public IPAddress Address;      // erste IPv4-Adresse
        public List<IPAddress> Dns = new List<IPAddress>();
    }

    static class Nics
    {
        [System.Runtime.InteropServices.DllImport("iphlpapi.dll")]
        static extern int GetBestInterface(uint destAddr, out int ifIndex);

        /// <summary>Der Adapter, über den Windows tatsächlich ins Internet routet.</summary>
        public static Nic Primary(List<Nic> nics)
        {
            try
            {
                int idx;
                uint addr = BitConverter.ToUInt32(IPAddress.Parse("1.1.1.1").GetAddressBytes(), 0);
                if (GetBestInterface(addr, out idx) == 0)
                {
                    var match = nics.FirstOrDefault(n => n.Index == idx);
                    if (match != null) return match;
                }
            }
            catch { }
            return nics.FirstOrDefault(n => n.Physical) ?? nics.FirstOrDefault();
        }

        static HashSet<string> PhysicalIds()
        {
            try
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var s = new ManagementObjectSearcher("SELECT GUID FROM Win32_NetworkAdapter WHERE PhysicalAdapter = TRUE"))
                    foreach (ManagementBaseObject o in s.Get())
                    {
                        var g = o["GUID"] as string;
                        if (!string.IsNullOrEmpty(g)) set.Add(g);
                    }
                return set;
            }
            catch { return null; }
        }

        static bool LooksVirtual(string d)
        {
            d = (d ?? "").ToLowerInvariant();
            string[] marks = { "virtual", "vpn", "tap-", "tap ", "tunnel", "hyper-v", "vmware", "vbox", "loopback", "wireguard", "radmin", "zerotier", "hamachi" };
            return marks.Any(d.Contains);
        }

        /// <summary>Alle aktiven Adapter mit IPv4-Standardgateway (also die, über die Verkehr ins Internet läuft).</summary>
        public static List<Nic> Active(bool resolvePhysical = true)
        {
            var phys = resolvePhysical ? PhysicalIds() : null;
            var list = new List<Nic>();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    var ip = ni.GetIPProperties();
                    bool gw = ip.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                    if (!gw) continue;
                    var nic = new Nic
                    {
                        Name = ni.Name,
                        Id = ni.Id,
                        Description = ni.Description,
                        // WMI kennt auch manche VPN-Treiber als "physisch" - die Beschreibung entscheidet mit.
                        Physical = (phys == null || phys.Contains(ni.Id)) && !LooksVirtual(ni.Description)
                    };
                    try { nic.Index = ip.GetIPv4Properties().Index; } catch { }
                    var v4 = ip.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                    if (v4 != null) nic.Address = v4.Address;
                    nic.Dns.AddRange(ip.DnsAddresses);
                    list.Add(nic);
                }
                catch { }
            }
            return list;
        }
    }
}
