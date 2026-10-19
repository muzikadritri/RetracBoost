using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace RetracBoost
{
    /// <summary>Ein IP-Bereich in CIDR-Schreibweise (IPv4 oder IPv6).</summary>
    sealed class Cidr
    {
        readonly byte[] net;
        readonly int bits;

        Cidr(byte[] net, int bits) { this.net = net; this.bits = bits; }

        public static Cidr Parse(string s)
        {
            int slash = s.IndexOf('/');
            var ip = IPAddress.Parse(slash < 0 ? s : s.Substring(0, slash));
            var b = ip.GetAddressBytes();
            int bits = slash < 0 ? b.Length * 8 : int.Parse(s.Substring(slash + 1));
            return new Cidr(b, bits);
        }

        public bool Contains(IPAddress ip)
        {
            var b = ip.GetAddressBytes();
            if (b.Length != net.Length) return false;
            int full = bits / 8, rest = bits % 8;
            for (int i = 0; i < full; i++) if (b[i] != net[i]) return false;
            if (rest == 0) return true;
            int mask = 0xFF << (8 - rest) & 0xFF;
            return (b[full] & mask) == (net[full] & mask);
        }
    }

    enum ServerSpot { Unknown, Frankfurt, Elsewhere }

    sealed class ServerVerdict
    {
        public ServerSpot Spot;
        /// <summary>true = über IP-Bereich oder Rechenzentrumsnamen belegt, nicht nur aus dem Ping geschätzt.</summary>
        public bool Sure;
        public string Reason;
    }

    static partial class ServerLockData
    {
        static List<Cidr> blocked, frankfurt;

        static List<Cidr> Blocked { get { return blocked ?? (blocked = BlockedCidrs.Select(Cidr.Parse).ToList()); } }
        static List<Cidr> Frankfurt { get { return frankfurt ?? (frankfurt = FrankfurtCidrs.Select(Cidr.Parse).ToList()); } }

        public static bool InFrankfurt(IPAddress ip) { return Frankfurt.Any(c => c.Contains(ip)); }
        public static bool InBlocked(IPAddress ip) { return Blocked.Any(c => c.Contains(ip)); }

        // Ab diesem Abstand zum Frankfurt-Referenzpunkt gilt ein Server per Ping als "nicht Frankfurt".
        const double FarMs = 12, NearMs = 6;

        /// <summary>Entscheidet, ob der erkannte Match-Server in Frankfurt steht.</summary>
        public static ServerVerdict Judge(ServerInfo s)
        {
            if (InFrankfurt(s.Ip))
                return new ServerVerdict { Spot = ServerSpot.Frankfurt, Sure = true, Reason = "AWS eu-central-1" };
            if (InBlocked(s.Ip))
                return new ServerVerdict { Spot = ServerSpot.Elsewhere, Sure = true, Reason = L.T("AWS außerhalb Frankfurts", "AWS outside Frankfurt") };

            if (!string.IsNullOrEmpty(s.Location))
            {
                // OVH Limburg liegt ~60 km von Frankfurt und hat praktisch denselben Ping.
                bool de = s.Location.IndexOf("Frankfurt", StringComparison.OrdinalIgnoreCase) >= 0
                       || s.Location.IndexOf("Limburg", StringComparison.OrdinalIgnoreCase) >= 0;
                return new ServerVerdict { Spot = de ? ServerSpot.Frankfurt : ServerSpot.Elsewhere, Sure = true, Reason = s.Location };
            }

            double? own = s.PingMs ?? s.ViaHopMs;
            if (own.HasValue && s.FrankfurtRefMs.HasValue)
            {
                double diff = own.Value - s.FrankfurtRefMs.Value;
                string why = L.T("Ping ", "Ping ") + own.Value.ToString("0") + L.T(" ms, Frankfurt ", " ms, Frankfurt ") + s.FrankfurtRefMs.Value.ToString("0") + " ms";
                if (diff <= NearMs) return new ServerVerdict { Spot = ServerSpot.Frankfurt, Reason = why };
                if (diff >= FarMs) return new ServerVerdict { Spot = ServerSpot.Elsewhere, Reason = why };
            }
            return new ServerVerdict { Spot = ServerSpot.Unknown, Reason = L.T("Standort nicht bestimmbar", "Location unknown") };
        }
    }
}
