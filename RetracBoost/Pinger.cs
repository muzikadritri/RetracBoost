using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace RetracBoost
{
    sealed class PingSample
    {
        public string Host;
        public DateTime Time;
        public double? Ms;   // null = Paket verloren
    }

    enum ProbeMode { Auto, Icmp, Tcp }

    /// <summary>
    /// Misst einmal pro Sekunde die Antwortzeit zu einem Ziel. Bevorzugt ICMP; antwortet das Ziel nicht
    /// darauf (z. B. Cloud-Endpunkte), wird automatisch der TCP-Handshake auf Port 443 gemessen.
    /// </summary>
    sealed class Pinger
    {
        public event Action<PingSample> Sampled;
        public event Action<string, ProbeMode, string> StateChanged;   // host, Modus, aufgelöste Adresse

        CancellationTokenSource cts;
        public string Host { get; private set; }

        public void Start(string host)
        {
            Stop();
            Host = host;
            cts = new CancellationTokenSource();
            var token = cts.Token;
            Task.Run(() => Loop(host, token));
        }

        public void Stop()
        {
            var c = cts;
            cts = null;
            if (c != null) c.Cancel();
        }

        async Task Loop(string host, CancellationToken ct)
        {
            IPAddress ip = null;
            DateTime resolvedAt = DateTime.MinValue;
            var mode = ProbeMode.Auto;
            var reported = (ProbeMode?)null;
            string reportedIp = null;
            int misses = 0;

            using (var ping = new Ping())
            {
                while (!ct.IsCancellationRequested)
                {
                    var clock = Stopwatch.StartNew();
                    double? ms = null;
                    try
                    {
                        if (ip == null || (DateTime.UtcNow - resolvedAt).TotalMinutes > 5)
                        {
                            var fresh = await Probe.Resolve(host);
                            if (fresh != null) ip = fresh;
                            resolvedAt = DateTime.UtcNow;
                        }

                        if (ip != null)
                        {
                            if (mode != ProbeMode.Tcp)
                            {
                                ms = await Probe.Icmp(ping, ip);
                                if (ms != null) mode = ProbeMode.Icmp;
                            }
                            if (ms == null && mode != ProbeMode.Icmp)
                            {
                                ms = await Probe.Tcp(ip, ct);
                                if (ms != null) mode = ProbeMode.Tcp;
                            }
                        }
                    }
                    catch (OperationCanceledException) { return; }
                    catch { ms = null; }

                    if (ct.IsCancellationRequested) return;

                    misses = ms == null ? misses + 1 : 0;
                    if (misses >= 10) { mode = ProbeMode.Auto; misses = 0; }

                    string ipText = ip == null ? null : ip.ToString();
                    if (reported != mode || reportedIp != ipText)
                    {
                        reported = mode;
                        reportedIp = ipText;
                        var h = StateChanged;
                        if (h != null) h(host, mode, ipText);
                    }

                    var s = Sampled;
                    if (s != null) s(new PingSample { Host = host, Time = DateTime.UtcNow, Ms = ms });

                    int wait = 1000 - (int)clock.ElapsedMilliseconds;
                    if (wait > 0)
                    {
                        try { await Task.Delay(wait, ct); } catch (OperationCanceledException) { return; }
                    }
                }
            }
        }
    }

    sealed class Stats
    {
        public int Count, Lost;
        public int Replies { get { return Count - Lost; } }
        public double Avg, Min, Max, Jitter, Loss;

        public static Stats From(IEnumerable<PingSample> samples)
        {
            var list = samples.ToList();
            var ok = list.Where(s => s.Ms.HasValue).Select(s => s.Ms.Value).ToList();
            var st = new Stats { Count = list.Count, Lost = list.Count - ok.Count };
            if (list.Count > 0) st.Loss = 100.0 * st.Lost / list.Count;
            if (ok.Count > 0)
            {
                st.Avg = ok.Average();
                st.Min = ok.Min();
                st.Max = ok.Max();
                if (ok.Count > 1)
                {
                    double sum = 0;
                    for (int i = 1; i < ok.Count; i++) sum += Math.Abs(ok[i] - ok[i - 1]);
                    st.Jitter = sum / (ok.Count - 1);
                }
            }
            return st;
        }
    }
}
