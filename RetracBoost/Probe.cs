using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace RetracBoost
{
    /// <summary>Einzelmessungen (ICMP, TCP-Handshake, Namensauflösung), von Pinger und allen Werkzeugen genutzt.</summary>
    static class Probe
    {
        public static readonly byte[] Payload = System.Text.Encoding.ASCII.GetBytes("RetracBoostLatencyProbe_32bytes!");

        public static async Task<IPAddress> Resolve(string host)
        {
            try
            {
                IPAddress literal;
                if (IPAddress.TryParse(host, out literal)) return literal;
                var all = await Dns.GetHostAddressesAsync(host);
                return all.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? all.FirstOrDefault();
            }
            catch { return null; }
        }

        public static async Task<double?> Icmp(Ping ping, IPAddress ip, int timeoutMs = 1000)
        {
            try
            {
                var clock = Stopwatch.StartNew();
                var r = await ping.SendPingAsync(ip, timeoutMs, Payload);
                clock.Stop();
                if (r.Status != IPStatus.Success) return null;
                double rtt = r.RoundtripTime;                       // ganze Millisekunden
                return rtt > 0 ? rtt : Math.Min(0.9, Math.Max(0.1, clock.Elapsed.TotalMilliseconds));
            }
            catch { return null; }
        }

        public static async Task<double?> Tcp(IPAddress ip, CancellationToken ct, int port = 443, int timeoutMs = 1500)
        {
            try
            {
                using (var c = new TcpClient(ip.AddressFamily))
                {
                    var clock = Stopwatch.StartNew();
                    var connect = c.ConnectAsync(ip, port);
                    var done = await Task.WhenAny(connect, Task.Delay(timeoutMs, ct));
                    clock.Stop();
                    if (done != connect)
                    {
                        var observed = connect.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                        return null;
                    }
                    if (connect.IsFaulted)
                    {
                        // Ein zurückgewiesener Verbindungsversuch (RST) beweist die Erreichbarkeit und liefert die Laufzeit.
                        var se = connect.Exception.GetBaseException() as SocketException;
                        if (se != null && se.SocketErrorCode == SocketError.ConnectionRefused) return clock.Elapsed.TotalMilliseconds;
                        return null;
                    }
                    return c.Connected ? (double?)clock.Elapsed.TotalMilliseconds : null;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        public static double Median(IList<double> v)
        {
            var s = v.OrderBy(x => x).ToList();
            int m = s.Count / 2;
            return s.Count % 2 == 1 ? s[m] : (s[m - 1] + s[m]) / 2.0;
        }

        public static bool IsPrivate(IPAddress a)
        {
            var b = a.GetAddressBytes();
            if (b.Length != 4) return a.IsIPv6LinkLocal || a.IsIPv6SiteLocal;
            return b[0] == 10 || b[0] == 127 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);   // inkl. CGNAT
        }
    }

    sealed class Target
    {
        public string Name { get; set; }
        public string Host { get; set; }
    }

    static class Regions
    {
        static Target T(string name, string region) { return new Target { Name = name, Host = "ec2." + region + ".amazonaws.com" }; }

        /// <summary>Alle Ziele für die Auswahl oben links.</summary>
        public static readonly Target[] Presets =
        {
            new Target { Name = "Cloudflare", Host = "1.1.1.1" },
            new Target { Name = "Google", Host = "8.8.8.8" },
            T("AWS · Frankfurt", "eu-central-1"), T("AWS · Irland", "eu-west-1"), T("AWS · London", "eu-west-2"),
            T("AWS · Paris", "eu-west-3"), T("AWS · Stockholm", "eu-north-1"), T("AWS · Mailand", "eu-south-1"),
            T("AWS · N. Virginia", "us-east-1"), T("AWS · Ohio", "us-east-2"), T("AWS · N. Kalifornien", "us-west-1"),
            T("AWS · Oregon", "us-west-2"), T("AWS · Kanada", "ca-central-1"), T("AWS · São Paulo", "sa-east-1"),
            T("AWS · VAE", "me-central-1"), T("AWS · Kapstadt", "af-south-1"), T("AWS · Mumbai", "ap-south-1"),
            T("AWS · Tokio", "ap-northeast-1"), T("AWS · Seoul", "ap-northeast-2"), T("AWS · Singapur", "ap-southeast-1"),
            T("AWS · Sydney", "ap-southeast-2"), T("AWS · Hongkong", "ap-east-1")
        };

        /// <summary>Für den Regionstest: nur die AWS-Standorte.</summary>
        public static IEnumerable<Target> Aws { get { return Presets.Where(p => p.Host.StartsWith("ec2.")); } }
    }

    /// <summary>Matchmaking-Regionen von Retrac (wie in der Spielauswahl) mit je einem AWS-Referenzpunkt.</summary>
    sealed class OgfnRegion
    {
        public string Name;
        public string Host;           // Referenzpunkt in der gleichen Weltgegend
        public bool HasServers;       // laut Spielauswahl gibt es dort Server
    }

    static class OgfnRegions
    {
        static OgfnRegion R(string de, string en, string region, bool has)
        {
            return new OgfnRegion { Name = L.T(de, en), Host = "ec2." + region + ".amazonaws.com", HasServers = has };
        }

        public static readonly OgfnRegion[] All =
        {
            R("Europa", "Europe", "eu-central-1", true),
            R("North America East", "North America East", "us-east-1", true),
            R("North America West", "North America West", "us-west-1", true),
            R("Ozeanien", "Oceania", "ap-southeast-2", true),
            R("Naher Osten", "Middle East", "me-central-1", true),
            R("Asien", "Asia", "ap-northeast-1", true),
            R("South Africa", "South Africa", "af-south-1", true),
            R("Brasilien", "Brazil", "sa-east-1", false)
        };
    }
}
