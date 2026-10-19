using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace RetracBoost
{
    // ------------------------------------------------------------------------------------------
    // Regionstest: alle Ziele gleichzeitig messen
    // ------------------------------------------------------------------------------------------
    sealed class RegionResult
    {
        public string Name, Host;
        public double? Ms;
        public int Lost;
    }

    static class RegionTest
    {
        /// <summary>Misst jedes Ziel <paramref name="samples"/>-mal (ICMP, sonst TCP:443) und liefert den Median.</summary>
        public static async Task<List<RegionResult>> Run(IEnumerable<KeyValuePair<string, string>> targets, int samples, Action<int, int> progress, CancellationToken ct)
        {
            var list = targets.ToList();
            int done = 0;
            var tasks = list.Select(async t =>
            {
                var res = new RegionResult { Name = t.Key, Host = t.Value };
                try
                {
                    var ip = await Probe.Resolve(t.Value);
                    if (ip != null)
                    {
                        var vals = new List<double>();
                        bool useTcp = false;
                        using (var ping = new Ping())
                        {
                            for (int i = 0; i < samples && !ct.IsCancellationRequested; i++)
                            {
                                double? v = null;
                                if (!useTcp)
                                {
                                    v = await Probe.Icmp(ping, ip, 1000);
                                    if (v == null && i == 0) useTcp = true;       // Ziel antwortet nicht auf ICMP
                                }
                                if (v == null && useTcp) v = await Probe.Tcp(ip, ct);
                                if (v.HasValue) vals.Add(v.Value); else res.Lost++;
                                await Task.Delay(100, ct);
                            }
                        }
                        if (vals.Count > 0) res.Ms = Probe.Median(vals);
                    }
                }
                catch (OperationCanceledException) { }
                catch { }
                int n = Interlocked.Increment(ref done);
                if (progress != null) progress(n, list.Count);
                return res;
            }).ToList();

            var all = await Task.WhenAll(tasks);
            return all.OrderBy(r => r.Ms ?? double.MaxValue).ToList();
        }
    }

    // ------------------------------------------------------------------------------------------
    // Traceroute: jeder Zwischenschritt bis zum Ziel
    // ------------------------------------------------------------------------------------------
    sealed class Hop
    {
        public int Ttl, Sent, Lost;
        public string Address, Name;
        public double? Avg, Best;
        public bool Reached;
        public bool IsPrivate;
        public string Label { get { return string.IsNullOrEmpty(Name) || Name == Address ? (Address ?? "*") : Name + "  (" + Address + ")"; } }
    }

    static class Traceroute
    {
        public static async Task<List<Hop>> Run(IPAddress dest, int maxHops, int probes, CancellationToken ct)
        {
            var tasks = Enumerable.Range(1, maxHops).Select(ttl => Task.Run(async () =>
            {
                var hop = new Hop { Ttl = ttl, Sent = probes };
                var rtts = new List<double>();
                using (var ping = new Ping())
                {
                    for (int i = 0; i < probes && !ct.IsCancellationRequested; i++)
                    {
                        try
                        {
                            var clock = Stopwatch.StartNew();
                            var r = await ping.SendPingAsync(dest, 1500, Probe.Payload, new PingOptions(ttl, true));
                            clock.Stop();
                            if (r.Status == IPStatus.TtlExpired || r.Status == IPStatus.Success)
                            {
                                if (r.Address != null) hop.Address = r.Address.ToString();
                                rtts.Add(r.RoundtripTime > 0 ? r.RoundtripTime : Math.Max(0.1, clock.Elapsed.TotalMilliseconds));
                                if (r.Status == IPStatus.Success) hop.Reached = true;
                            }
                            else hop.Lost++;
                        }
                        catch { hop.Lost++; }
                    }
                }
                if (rtts.Count > 0) { hop.Avg = rtts.Average(); hop.Best = rtts.Min(); }
                return hop;
            })).ToList();

            var hops = (await Task.WhenAll(tasks)).OrderBy(h => h.Ttl).ToList();

            // Nach dem Ziel (oder dem letzten antwortenden Hop) abschneiden.
            int reached = hops.FindIndex(h => h.Reached);
            if (reached >= 0) hops = hops.Take(reached + 1).ToList();
            else
            {
                int last = hops.FindLastIndex(h => h.Address != null);
                hops = last >= 0 ? hops.Take(last + 1).ToList() : new List<Hop>();
            }

            // Namen auflösen (parallel, mit kurzem Timeout).
            var named = hops.Where(h => h.Address != null).Select(async h =>
            {
                try
                {
                    var ip = IPAddress.Parse(h.Address);
                    h.IsPrivate = Probe.IsPrivate(ip);
                    var lookup = Dns.GetHostEntryAsync(ip);
                    if (await Task.WhenAny(lookup, Task.Delay(1500)) == lookup && !lookup.IsFaulted)
                        h.Name = lookup.Result.HostName;
                }
                catch { }
            }).ToList();
            await Task.WhenAll(named);
            return hops;
        }
    }

    // ------------------------------------------------------------------------------------------
    // Ping unter Last (Bufferbloat): Latenz im Leerlauf vs. während Download/Upload
    // ------------------------------------------------------------------------------------------
    sealed class LoadResult
    {
        public double IdleMs, DownLoadedMs, UpLoadedMs, DownMbps, UpMbps;
        public double Increase { get { return Math.Max(DownLoadedMs, UpLoadedMs) - IdleMs; } }

        public string Grade
        {
            get
            {
                double d = Increase;
                return d < 5 ? "A+" : d < 30 ? "A" : d < 60 ? "B" : d < 200 ? "C" : d < 400 ? "D" : "F";
            }
        }
    }

    static class LoadTest
    {
        const string DownUrl = "https://speed.cloudflare.com/__down?bytes=200000000";
        const string UpUrl = "https://speed.cloudflare.com/__up";
        const int Streams = 4;

        public static async Task<LoadResult> Run(Action<string> phase, int phaseSeconds, CancellationToken ct)
        {
            var res = new LoadResult();
            var reference = await Probe.Resolve("1.1.1.1");
            if (reference == null) throw new InvalidOperationException(L.T("Keine Verbindung.", "No connection."));

            phase("idle");
            var idle = await SampleFor(reference, TimeSpan.FromSeconds(3), ct, null);
            if (idle.Count < 5) throw new InvalidOperationException(L.T("Referenzziel antwortet nicht.", "Reference host does not answer."));
            res.IdleMs = Probe.Median(idle);

            phase("down");
            long bytes = 0;
            var down = await Load(reference, phaseSeconds, ct, token => DownloadWorker(token, b => Interlocked.Add(ref bytes, b)));
            res.DownLoadedMs = down.Item1.Count > 0 ? Probe.Median(down.Item1) : res.IdleMs;
            res.DownMbps = down.Item2 > 0 ? Interlocked.Read(ref bytes) * 8.0 / down.Item2 / 1e6 : 0;

            phase("up");
            long sent = 0;
            var up = await Load(reference, phaseSeconds, ct, token => UploadWorker(token, b => Interlocked.Add(ref sent, b)));
            res.UpLoadedMs = up.Item1.Count > 0 ? Probe.Median(up.Item1) : res.IdleMs;
            res.UpMbps = up.Item2 > 0 ? Interlocked.Read(ref sent) * 8.0 / up.Item2 / 1e6 : 0;
            return res;
        }

        static async Task<List<double>> SampleFor(IPAddress ip, TimeSpan duration, CancellationToken ct, Action<double> each)
        {
            var vals = new List<double>();
            var clock = Stopwatch.StartNew();
            using (var ping = new Ping())
            {
                while (clock.Elapsed < duration && !ct.IsCancellationRequested)
                {
                    var t0 = Stopwatch.StartNew();
                    var v = await Probe.Icmp(ping, ip, 1500);
                    if (v.HasValue) { vals.Add(v.Value); if (each != null) each(v.Value); }
                    int wait = 150 - (int)t0.ElapsedMilliseconds;
                    if (wait > 0) await Task.Delay(wait, ct);
                }
            }
            return vals;
        }

        /// <summary>Startet die Last und misst währenddessen die Latenz. Liefert Messwerte und die effektive Dauer.</summary>
        static async Task<Tuple<List<double>, double>> Load(IPAddress ip, int seconds, CancellationToken ct, Func<CancellationToken, Task> worker)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var workers = Enumerable.Range(0, Streams).Select(_ => Task.Run(() => worker(cts.Token))).ToList();
                var clock = Stopwatch.StartNew();
                await Task.Delay(1500, ct);                        // Anlaufphase, zählt nicht
                var vals = await SampleFor(ip, TimeSpan.FromSeconds(Math.Max(2, seconds - 1.5)), ct, null);
                double effective = clock.Elapsed.TotalSeconds;
                cts.Cancel();
                try { await Task.WhenAll(workers); } catch { }
                return Tuple.Create(vals, Math.Max(0.5, effective));
            }
        }

        static async Task DownloadWorker(CancellationToken ct, Action<int> count)
        {
            var buf = new byte[65536];
            while (!ct.IsCancellationRequested)
            {
                HttpWebRequest req = null;
                try
                {
                    req = (HttpWebRequest)WebRequest.Create(DownUrl);
                    req.Timeout = 8000;
                    req.UserAgent = "RetracBoost";
                    using (ct.Register(() => { try { req.Abort(); } catch { } }))
                    using (var resp = await req.GetResponseAsync())
                    using (var s = resp.GetResponseStream())
                    {
                        int n;
                        while (!ct.IsCancellationRequested && (n = await s.ReadAsync(buf, 0, buf.Length, ct)) > 0) count(n);
                    }
                }
                catch { if (ct.IsCancellationRequested) return; await Task.Delay(200); }
            }
        }

        static async Task UploadWorker(CancellationToken ct, Action<int> count)
        {
            var buf = new byte[65536];
            new Random().NextBytes(buf);
            while (!ct.IsCancellationRequested)
            {
                HttpWebRequest req = null;
                try
                {
                    req = (HttpWebRequest)WebRequest.Create(UpUrl);
                    req.Method = "POST";
                    req.Timeout = 8000;
                    req.UserAgent = "RetracBoost";
                    req.ContentType = "application/octet-stream";
                    req.AllowWriteStreamBuffering = false;
                    req.ContentLength = 100L * 1000 * 1000;
                    using (ct.Register(() => { try { req.Abort(); } catch { } }))
                    using (var s = await req.GetRequestStreamAsync())
                    {
                        long written = 0;
                        while (!ct.IsCancellationRequested && written < req.ContentLength)
                        {
                            await s.WriteAsync(buf, 0, buf.Length, ct);
                            written += buf.Length;
                            count(buf.Length);
                        }
                    }
                }
                catch { if (ct.IsCancellationRequested) return; await Task.Delay(200); }
            }
        }
    }

    // ------------------------------------------------------------------------------------------
    // MTU: größte Paketgröße, die ohne Fragmentierung durchkommt
    // ------------------------------------------------------------------------------------------
    static class MtuTool
    {
        /// <summary>Liefert die Pfad-MTU (IP-Paketgröße) oder null, wenn das Ziel nicht auf Ping antwortet.</summary>
        public static async Task<int?> FindPathMtu(IPAddress ip, CancellationToken ct)
        {
            using (var ping = new Ping())
            {
                var opts = new PingOptions(128, true);        // DontFragment

                Func<int, Task<bool>> fits = async size =>
                {
                    for (int attempt = 0; attempt < 2; attempt++)
                    {
                        try
                        {
                            var r = await ping.SendPingAsync(ip, 1500, new byte[size], opts);
                            if (r.Status == IPStatus.Success) return true;
                            if (r.Status == IPStatus.PacketTooBig) return false;
                        }
                        catch (PingException) { return false; }
                    }
                    return false;
                };

                if (!await fits(548)) return null;             // 548 + 28 = 576: muss überall durchgehen
                int lo = 548, hi = 1472;                       // 1472 + 28 = 1500
                while (lo < hi && !ct.IsCancellationRequested)
                {
                    int mid = (lo + hi + 1) / 2;
                    if (await fits(mid)) lo = mid; else hi = mid - 1;
                }
                return lo + 28;
            }
        }
    }
}
