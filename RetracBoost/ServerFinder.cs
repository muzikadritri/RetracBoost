using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RetracBoost
{
    sealed class Peer
    {
        public IPAddress Ip;
        public int Port, LocalPort, Packets;
    }

    sealed class ServerInfo
    {
        public IPAddress Ip;
        public int Port, Packets;
        public string HostName;          // Reverse-DNS, falls vorhanden
        public string RegionGuess;       // aus dem Hostnamen abgeleitet (nur bei AWS erkennbar)
        public string Provider;          // z. B. "OVHcloud", aus dem Hostnamen
        public string Location;          // Standort aus den Routernamen vor dem Server (z. B. OVH-Rechenzentrum)
        public double? PingMs;           // direkter ICMP-Ping zum Server
        public double? ViaHopMs;         // Fallback: letzter antwortender Router vor dem Server
        public string ViaHopAddress;
        public List<Hop> Route = new List<Hop>();
    }

    /// <summary>
    /// Findet den Spielserver, mit dem ein laufendes Spiel gerade per UDP spricht. Dazu werden die UDP-Ports
    /// des Spielprozesses ermittelt und kurz die eingehenden IP-Pakete auf diese Ports ausgewertet
    /// (nur Absender-Adresse und -Port, kein Paketinhalt). Es wird kein Handle auf den Spielprozess geöffnet.
    /// </summary>
    static class ServerFinder
    {
        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern int GetExtendedUdpTable(IntPtr table, ref int size, bool sort, int af, int tableClass, int reserved);

        const int AF_INET = 2;
        const int UDP_TABLE_OWNER_PID = 1;

        /// <summary>Alle lokalen UDP-Ports, die zu den angegebenen Prozess-IDs gehören.</summary>
        public static HashSet<int> UdpPortsOf(ICollection<int> pids)
        {
            var ports = new HashSet<int>();
            int size = 0;
            GetExtendedUdpTable(IntPtr.Zero, ref size, false, AF_INET, UDP_TABLE_OWNER_PID, 0);
            if (size <= 0) return ports;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedUdpTable(buf, ref size, false, AF_INET, UDP_TABLE_OWNER_PID, 0) != 0) return ports;
                int count = Marshal.ReadInt32(buf);
                long row = buf.ToInt64() + 4;
                for (int i = 0; i < count; i++, row += 12)       // MIB_UDPROW_OWNER_PID = 3 x DWORD
                {
                    int rawPort = Marshal.ReadInt32(new IntPtr(row), 4);
                    int port = ((rawPort & 0xFF) << 8) | ((rawPort >> 8) & 0xFF);   // Netzwerk-Byte-Reihenfolge
                    int pid = Marshal.ReadInt32(new IntPtr(row), 8);
                    if (pids.Contains(pid)) ports.Add(port);
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return ports;
        }

        /// <summary>Lauscht <paramref name="ms"/> Millisekunden auf eingehende UDP-Pakete an die gegebenen lokalen Ports.</summary>
        public static Task<List<Peer>> Capture(IPAddress localIp, ICollection<int> localPorts, int ms)
        {
            return Task.Run(() =>
            {
                var peers = new Dictionary<string, Peer>();
                using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.IP))
                {
                    s.Bind(new IPEndPoint(localIp, 0));
                    s.IOControl(IOControlCode.ReceiveAll, BitConverter.GetBytes(3), null);   // RCVALL_IPLEVEL: alle IP-Pakete dieses Rechners
                    s.ReceiveTimeout = 150;
                    var buf = new byte[65536];
                    var clock = Stopwatch.StartNew();
                    while (clock.ElapsedMilliseconds < ms)
                    {
                        int n;
                        try { n = s.Receive(buf); }
                        catch (SocketException) { continue; }                // Timeout
                        if (n < 28 || (buf[0] >> 4) != 4 || buf[9] != 17) continue;   // IPv4 + UDP
                        int ihl = (buf[0] & 0x0F) * 4;
                        if (n < ihl + 8) continue;
                        int srcPort = (buf[ihl] << 8) | buf[ihl + 1];
                        int dstPort = (buf[ihl + 2] << 8) | buf[ihl + 3];
                        if (!localPorts.Contains(dstPort)) continue;
                        var src = new IPAddress(new[] { buf[12], buf[13], buf[14], buf[15] });
                        string key = src + ":" + srcPort;
                        Peer p;
                        if (!peers.TryGetValue(key, out p))
                            peers[key] = p = new Peer { Ip = src, Port = srcPort, LocalPort = dstPort };
                        p.Packets++;
                    }
                }
                return peers.Values.OrderByDescending(p => p.Packets).ToList();
            });
        }

        /// <summary>Wählt aus den Absendern den Spielserver: viel Verkehr, öffentliche Adresse, kein Standarddienst.</summary>
        public static Peer PickServer(IEnumerable<Peer> peers)
        {
            int[] noise = { 53, 67, 68, 123, 137, 138, 1900, 5353, 5355 };
            return peers
                .Where(p => !Probe.IsPrivate(p.Ip) && p.Packets >= 3 && !noise.Contains(p.Port))
                .OrderByDescending(p => p.Packets)
                .FirstOrDefault();
        }

        public static async Task<ServerInfo> Describe(Peer peer, CancellationToken ct)
        {
            var info = new ServerInfo { Ip = peer.Ip, Port = peer.Port, Packets = peer.Packets };

            // Name über Reverse-DNS (kurzer Timeout)
            try
            {
                var lookup = Dns.GetHostEntryAsync(peer.Ip);
                if (await Task.WhenAny(lookup, Task.Delay(1500, ct)) == lookup && !lookup.IsFaulted)
                {
                    info.HostName = lookup.Result.HostName;
                    info.RegionGuess = GuessRegion(info.HostName);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }

            // direkter Ping
            using (var ping = new Ping())
            {
                var vals = new List<double>();
                for (int i = 0; i < 4; i++)
                {
                    var v = await Probe.Icmp(ping, peer.Ip, 1000);
                    if (v.HasValue) vals.Add(v.Value);
                    await Task.Delay(100, ct);
                }
                if (vals.Count > 0) info.PingMs = Probe.Median(vals);
            }

            // Route - zeigt, wo die Zeit verloren geht; Fallback-Messpunkt, wenn der Server selbst nicht antwortet
            info.Route = await Traceroute.Run(peer.Ip, 24, 2, ct);
            if (!info.PingMs.HasValue)
            {
                var lastHop = info.Route.LastOrDefault(h => h.Avg.HasValue && !h.Reached);
                if (lastHop != null) { info.ViaHopMs = lastHop.Avg; info.ViaHopAddress = lastHop.Address; }
            }

            info.Provider = ProviderOf(info.HostName);
            if (info.Provider == "OVHcloud") info.Location = OvhSiteFromRoute(info.Route);
            return info;
        }

        static string ProviderOf(string host)
        {
            if (string.IsNullOrEmpty(host)) return null;
            string h = host.ToLowerInvariant();
            if (h.EndsWith(".amazonaws.com")) return "Amazon AWS";
            if (Regex.IsMatch(h, @"\.ip-\d+-\d+-\d+\.(eu|net|us|ca)$") || h.Contains("ovh")) return "OVHcloud";
            if (h.Contains("hetzner") || h.EndsWith(".your-server.de")) return "Hetzner";
            if (h.Contains("contabo")) return "Contabo";
            if (h.Contains("linode")) return "Linode";
            if (h.Contains("vultr")) return "Vultr";
            if (h.Contains("digitalocean")) return "DigitalOcean";
            if (h.EndsWith(".googleusercontent.com")) return "Google Cloud";
            if (h.Contains("azure")) return "Microsoft Azure";
            return null;
        }

        // OVH kodiert den Standort im Routernamen, z. B. "be105.lil2-gra1-sbb2-nc5.fr.eu" -> gra = Gravelines
        static readonly Dictionary<string, string> OvhSites = new Dictionary<string, string>
        {
            { "gra", "Gravelines (FR)" }, { "rbx", "Roubaix (FR)" }, { "sbg", "Straßburg (FR)" }, { "bhs", "Beauharnois (CA)" },
            { "waw", "Warschau (PL)" }, { "lim", "Limburg (DE)" }, { "eri", "Erith (UK)" }, { "sgp", "Singapur" },
            { "syd", "Sydney (AU)" }, { "vin", "Vint Hill (US)" }, { "hil", "Hillsboro (US)" }, { "fra", "Frankfurt (DE)" },
            { "lon", "London (UK)" }
        };

        static string OvhSiteFromRoute(List<Hop> route)
        {
            for (int i = route.Count - 1; i >= 0; i--)
            {
                var name = route[i].Name;
                if (string.IsNullOrEmpty(name)) continue;
                foreach (var token in Regex.Split(name.ToLowerInvariant(), @"[\.\-_]"))
                {
                    var m = Regex.Match(token, @"^([a-z]{3})\d*$");
                    string site;
                    if (m.Success && OvhSites.TryGetValue(m.Groups[1].Value, out site)) return site;
                }
            }
            return null;
        }

        static readonly Regex AwsRegion = new Regex(@"\.([a-z]{2}-[a-z]+-\d)\.compute\.amazonaws\.com$", RegexOptions.IgnoreCase);

        static string GuessRegion(string host)
        {
            if (string.IsNullOrEmpty(host)) return null;
            string code = null;
            var m = AwsRegion.Match(host);
            if (m.Success) code = m.Groups[1].Value.ToLowerInvariant();
            else if (host.EndsWith(".compute-1.amazonaws.com", StringComparison.OrdinalIgnoreCase)) code = "us-east-1";
            if (code == null) return null;
            var known = Regions.Presets.FirstOrDefault(p => p.Host == "ec2." + code + ".amazonaws.com");
            return known != null ? known.Name.Replace("AWS · ", "") + " (" + code + ")" : code;
        }
    }
}
