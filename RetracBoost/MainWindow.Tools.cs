using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace RetracBoost
{
    /// <summary>Eine Ergebniszeile (Balken- oder Routenliste).</summary>
    sealed class ListRow
    {
        public string Left { get; set; }
        public string Middle { get; set; }
        public string Right { get; set; }
        public string Extra { get; set; }
        public double Fraction { get; set; }
        public bool Dim { get; set; }
        public Brush BarBrush { get; set; }
        public string Host { get; set; }          // Klick setzt dieses Ziel
    }

    public partial class MainWindow
    {
        Brush Res(string key) { return (Brush)FindResource(key); }

        void LocalizeTools()
        {
            RegionTitle.Text = L.T("Beste Region", "Best region");
            RegionDesc.Text = L.T("Misst alle Standorte gleichzeitig. Ein Klick auf eine Zeile setzt sie als Live-Ziel.",
                                  "Measures all locations at once. Click a row to use it as live target.");
            RegionRun.Content = L.T("Alle testen", "Test all");

            RouteTitle.Text = L.T("Verbindungs-Diagnose", "Connection diagnosis");
            RouteDesc.Text = L.T("Zeigt jeden Zwischenschritt bis zum aktuellen Ziel und wo die Zeit verloren geht.",
                                 "Shows every hop to the current target and where time is lost.");
            RouteRun.Content = L.T("Route verfolgen", "Trace route");

            LoadTitle.Text = L.T("Ping unter Last", "Ping under load");
            LoadDesc.Text = L.T("Misst, wie stark dein Ping bei vollem Download und Upload steigt (Bufferbloat). Belastet die Leitung ca. 25 Sekunden.",
                                "Measures how much your ping rises while downloading and uploading (bufferbloat). Loads your line for about 25 seconds.");
            LoadRun.Content = L.T("Test starten", "Start test");
            LoadLabelIdle.Text = L.T("Ping im Leerlauf", "Idle ping");
            LoadLabelDown.Text = "Download";
            LoadLabelUp.Text = "Upload";

            MtuTitle.Text = L.T("Paketgröße (MTU)", "Packet size (MTU)");
            MtuDesc.Text = L.T("Findet die größte Paketgröße, die ohne Fragmentierung durchkommt. Zu große Pakete werden zerteilt und kosten Zeit.",
                               "Finds the largest packet size that passes without fragmentation. Oversized packets get split and cost time.");
            MtuRun.Content = L.T("Messen", "Measure");
            MtuApply.Content = L.T("Anpassen", "Apply");

            RepairTitle.Text = L.T("Netzwerk reparieren", "Repair network");
            RepairDesc.Text = L.T("Räumt veraltete Zwischenspeicher auf oder setzt den Netzwerk-Unterbau zurück.",
                                  "Clears stale caches or resets the network stack.");
            RepairFlush.Content = L.T("Caches leeren", "Clear caches");
            RepairWinsock.Content = L.T("Winsock zurücksetzen", "Reset Winsock");

            SystemTitle.Text = L.T("System & Komfort", "System & comfort");
        }

        // ------------------------------------------------------------------ Overlay

        void SetOverlay(bool on)
        {
            if (on)
            {
                if (overlay == null) overlay = new OverlayWindow();
                overlay.Show();
            }
            else if (overlay != null) overlay.Hide();
        }

        void UpdateOverlay(PingSample s, Stats st)
        {
            if (gameMenu != null && gameMenu.IsVisible)
                gameMenu.SetPing(s.Ms.HasValue ? "Ping " + (s.Ms.Value < 1 ? "<1" : s.Ms.Value.ToString("0", CultureInfo.CurrentCulture)) + " ms" : "Ping – ms");
            if (overlay == null || !overlay.IsVisible) return;
            string detail = st.Replies > 0
                ? "Jitter " + st.Jitter.ToString("0.0", CultureInfo.CurrentCulture) + " · " + st.Loss.ToString("0", CultureInfo.CurrentCulture) + " %"
                : "";
            double v = s.Ms ?? double.NaN;
            Brush dot = !s.Ms.HasValue ? BadBrush : v <= 100 ? Res("Accent") : v <= 150 ? Res("Warn") : BadBrush;
            overlay.Update(s.Ms, detail, dot);
        }

        // ------------------------------------------------------------------ Ingame-Menü (Alt+L)

        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);

        const int WM_HOTKEY = 0x0312, GameMenuHotkeyId = 0x524C;
        const uint MOD_ALT = 0x1, MOD_NOREPEAT = 0x4000, VK_L = 0x4C;
        bool hotkeyRegistered;

        void InitGameMenu()
        {
            GammaRamp.RecoverAfterCrash();
            var src = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            if (src != null) src.AddHook(GameMenuHook);
        }

        IntPtr GameMenuHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == GameMenuHotkeyId)
            {
                handled = true;
                if (gameMenu != null) gameMenu.Toggle();
            }
            return IntPtr.Zero;
        }

        GameMenuWindow EnsureGameMenu()
        {
            if (gameMenu == null)
                gameMenu = new GameMenuWindow((Style)FindResource("Switch"), () => rowOverlay.IsOn, on =>
                {
                    if (rowOverlay.IsOn != on && rowOverlay.CanToggle) rowOverlay.IsOn = on;
                });
            return gameMenu;
        }

        /// <summary>Registriert Alt+L systemweit. Kein Hook ins Spiel, nur ein normales Windows-Tastenkürzel.</summary>
        void SetGameMenuHotkey(bool on)
        {
            var h = new WindowInteropHelper(this).Handle;
            if (on)
            {
                EnsureGameMenu().ApplySaved();
                if (!hotkeyRegistered && h != IntPtr.Zero)
                    hotkeyRegistered = RegisterHotKey(h, GameMenuHotkeyId, MOD_ALT | MOD_NOREPEAT, VK_L);
                gameMenuTweak.Note = hotkeyRegistered ? null
                    : L.T("Alt+L ist schon von einem anderen Programm belegt", "Alt+L is already used by another program");
            }
            else
            {
                if (hotkeyRegistered) UnregisterHotKey(h, GameMenuHotkeyId);
                hotkeyRegistered = false;
                gameMenuTweak.Note = null;
                if (gameMenu != null) gameMenu.Hide();
                GammaRamp.Restore();
            }
            rowGameMenu.Detail = gameMenuTweak.Detail;
        }

        void CloseGameMenu()
        {
            if (hotkeyRegistered) UnregisterHotKey(new WindowInteropHelper(this).Handle, GameMenuHotkeyId);
            hotkeyRegistered = false;
            if (gameMenu != null) gameMenu.Close();
            GammaRamp.Restore();
        }

        // ------------------------------------------------------------------ Klick auf Ergebniszeile

        void ListRow_Click(object sender, MouseButtonEventArgs e)
        {
            var row = (sender as FrameworkElement) == null ? null : ((FrameworkElement)sender).DataContext as ListRow;
            if (row == null || string.IsNullOrEmpty(row.Host)) return;
            SetTarget(row.Host);
            TabBoost.IsChecked = true;
        }

        // ------------------------------------------------------------------ Beste Region

        async void RegionRun_Click(object sender, RoutedEventArgs e)
        {
            RegionRun.IsEnabled = false;
            RegionList.ItemsSource = null;
            RegionStatus.Text = L.T("Messe …", "Measuring …");
            try
            {
                var targets = Regions.Aws.Select(t => new KeyValuePair<string, string>(t.Name.Replace("AWS · ", ""), t.Host));
                var results = await RegionTest.Run(targets, 5,
                    (d, n) => { var _ = Dispatcher.BeginInvoke(new Action(() => RegionStatus.Text = d + " / " + n)); },
                    CancellationToken.None);

                double max = Math.Max(1, results.Where(r => r.Ms.HasValue).Select(r => r.Ms.Value).DefaultIfEmpty(1).Max());
                var best = results.FirstOrDefault(r => r.Ms.HasValue);
                var list = new List<ListRow>();
                foreach (var r in results)
                {
                    list.Add(new ListRow
                    {
                        Left = r.Name,
                        Right = r.Ms.HasValue ? r.Ms.Value.ToString("0", CultureInfo.CurrentCulture) + " ms" : "–",
                        Fraction = r.Ms.HasValue ? r.Ms.Value / max : 0,
                        BarBrush = r == best ? Res("Accent") : Res("BarIdle"),
                        Dim = !r.Ms.HasValue,
                        Host = r.Host
                    });
                }
                RegionList.ItemsSource = list;
                RegionStatus.Text = best == null
                    ? L.T("Keine Antwort", "No reply")
                    : L.T("Am schnellsten: ", "Fastest: ") + best.Name + " (" + best.Ms.Value.ToString("0", CultureInfo.CurrentCulture) + " ms)";
            }
            catch (Exception ex) { RegionStatus.Text = Shorten(ex.Message); }
            finally { RegionRun.IsEnabled = true; }
        }

        // ------------------------------------------------------------------ Verbindungs-Diagnose

        async void RouteRun_Click(object sender, RoutedEventArgs e)
        {
            RouteRun.IsEnabled = false;
            RouteList.ItemsSource = null;
            RouteSummary.Text = "";
            RouteStatus.Text = L.T("Verfolge Route zu ", "Tracing route to ") + host + " …";
            try
            {
                var ip = await Probe.Resolve(host);
                if (ip == null) { RouteStatus.Text = L.T("Ziel nicht auflösbar", "Cannot resolve target"); return; }
                var hops = await Traceroute.Run(ip, 24, 3, CancellationToken.None);
                if (hops.Count == 0) { RouteStatus.Text = L.T("Keine Zwischenstationen antworten", "No hop answers"); return; }

                var rowsOut = new List<ListRow>();
                bool firstPrivate = true;
                foreach (var h in hops)
                {
                    string label = h.Label;
                    if (h.IsPrivate && firstPrivate) { label = L.T("Router · ", "Router · ") + label; firstPrivate = false; }
                    int lossPct = h.Sent == 0 ? 0 : (int)Math.Round(100.0 * h.Lost / h.Sent);
                    rowsOut.Add(new ListRow
                    {
                        Left = h.Ttl.ToString(CultureInfo.InvariantCulture),
                        Middle = h.Address == null ? "*" : label,
                        Right = h.Avg.HasValue ? h.Avg.Value.ToString(h.Avg.Value < 10 ? "0.0" : "0", CultureInfo.CurrentCulture) + " ms" : "*",
                        Extra = h.Address == null ? "" : lossPct + " %"
                    });
                }
                RouteList.ItemsSource = rowsOut;
                RouteStatus.Text = hops.Count + L.T(" Zwischenstationen", " hops");

                // Zusammenfassung: Router -> Provider -> Ziel, größter Sprung
                var withAvg = hops.Where(h => h.Avg.HasValue).ToList();
                var router = hops.FirstOrDefault(h => h.IsPrivate && h.Avg.HasValue);
                var provider = hops.FirstOrDefault(h => !h.IsPrivate && h.Address != null && h.Avg.HasValue);
                var last = withAvg.LastOrDefault();
                var parts = new List<string>();
                Func<double, string> f = v => v.ToString(v < 10 ? "0.0" : "0", CultureInfo.CurrentCulture) + " ms";
                if (router != null) parts.Add(L.T("Heimnetz ", "Home network ") + f(router.Avg.Value));
                if (provider != null) parts.Add(L.T("Provider ", "ISP ") + f(provider.Avg.Value));
                if (last != null) parts.Add(L.T("Ziel ", "Target ") + f(last.Avg.Value));
                string summary = string.Join("  ·  ", parts);

                Hop jump = null; double jumpBy = 0; double prev = 0;
                foreach (var h in withAvg)
                {
                    double d = h.Avg.Value - prev;
                    if (d > jumpBy) { jumpBy = d; jump = h; }
                    prev = h.Avg.Value;
                }
                if (jump != null && jumpBy >= 15)
                    summary += "\n" + L.T("Größter Sprung bei Hop ", "Biggest jump at hop ") + jump.Ttl + " (+" + jumpBy.ToString("0", CultureInfo.CurrentCulture) + " ms)";
                RouteSummary.Text = summary;
            }
            catch (Exception ex) { RouteStatus.Text = Shorten(ex.Message); }
            finally { RouteRun.IsEnabled = true; }
        }

        // ------------------------------------------------------------------ Ping unter Last

        async void LoadRun_Click(object sender, RoutedEventArgs e)
        {
            LoadRun.IsEnabled = false;
            LoadGrade.Text = "";
            LoadVerdict.Text = "";
            LoadIdle.Text = LoadDown.Text = LoadUp.Text = "–";
            LoadDownSub.Text = LoadUpSub.Text = "";
            try
            {
                Action<string> phase = p =>
                {
                    var _ = Dispatcher.BeginInvoke(new Action(() =>
                        LoadStatus.Text = p == "idle" ? L.T("Messe Ruhe-Ping …", "Measuring idle ping …")
                                        : p == "down" ? L.T("Download läuft …", "Downloading …")
                                        : L.T("Upload läuft …", "Uploading …")));
                };
                var res = await LoadTest.Run(phase, 8, CancellationToken.None);

                Func<double, string> ms = v => v.ToString(v < 10 ? "0.0" : "0", CultureInfo.CurrentCulture) + " ms";
                LoadIdle.Text = ms(res.IdleMs);
                LoadDown.Text = res.DownMbps.ToString("0", CultureInfo.CurrentCulture) + " Mbit/s";
                LoadUp.Text = res.UpMbps.ToString("0", CultureInfo.CurrentCulture) + " Mbit/s";
                LoadDownSub.Text = L.T("Ping ", "Ping ") + ms(res.DownLoadedMs) + " (+" + Math.Max(0, res.DownLoadedMs - res.IdleMs).ToString("0", CultureInfo.CurrentCulture) + ")";
                LoadUpSub.Text = L.T("Ping ", "Ping ") + ms(res.UpLoadedMs) + " (+" + Math.Max(0, res.UpLoadedMs - res.IdleMs).ToString("0", CultureInfo.CurrentCulture) + ")";

                string g = res.Grade;
                LoadGrade.Text = g;
                LoadGrade.Foreground = g.StartsWith("A") ? Res("Accent") : g == "B" ? Res("Warn") : BadBrush;
                LoadVerdict.Text =
                    g.StartsWith("A") ? L.T("Sehr gut: Auch bei voller Auslastung bleibt dein Ping stabil.", "Great: your ping stays stable even under full load.")
                    : g == "B" ? L.T("Okay: Unter Last steigt der Ping spürbar. Pausiere große Downloads beim Spielen.", "Okay: ping rises noticeably under load. Pause big downloads while gaming.")
                    : L.T("Bufferbloat: Bei Downloads oder Uploads schnellt dein Ping hoch. Hilfreich: im Router „Smart Queue“/SQM aktivieren und Down-/Upload auf ca. 90 % deiner Leitung begrenzen, sowie „Hintergrund pausieren“ unten.",
                          "Bufferbloat: your ping spikes during downloads or uploads. Helpful: enable Smart Queue/SQM on your router and limit down/upload to about 90 % of your line, plus “Pause background traffic” below.");
                LoadStatus.Text = L.T("Fertig", "Done");
            }
            catch (Exception ex) { LoadStatus.Text = Shorten(ex.Message); }
            finally { LoadRun.IsEnabled = true; }
        }

        // ------------------------------------------------------------------ MTU

        void ShowMtu()
        {
            MtuStatus.Text = mtu.Detail;
            MtuApply.Content = mtu.WeApplied ? L.T("Zurücksetzen", "Restore") : L.T("Anpassen", "Apply");
            MtuApply.IsEnabled = Admin.IsElevated && (mtu.WeApplied || mtu.CanLower);
        }

        async void MtuRun_Click(object sender, RoutedEventArgs e)
        {
            MtuRun.IsEnabled = false;
            MtuStatus.Text = L.T("Messe …", "Measuring …");
            try { await Task.Run(() => mtu.Measure()); }
            catch (Exception ex) { MtuStatus.Text = Shorten(ex.Message); MtuRun.IsEnabled = true; return; }
            ShowMtu();
            MtuRun.IsEnabled = true;
        }

        async void MtuApply_Click(object sender, RoutedEventArgs e)
        {
            MtuApply.IsEnabled = false;
            bool restore = mtu.WeApplied;
            string error = await Task.Run(() =>
            {
                try { if (restore) mtu.Revert(); else mtu.Apply(); return (string)null; }
                catch (Exception ex) { return ex.Message; }
            });
            ShowMtu();
            if (error != null) MtuStatus.Text = L.T("Fehler: ", "Error: ") + Shorten(error);
        }

        // ------------------------------------------------------------------ Reparatur

        async void RepairFlush_Click(object sender, RoutedEventArgs e)
        {
            RepairFlush.IsEnabled = false;
            RepairStatus.Text = L.T("Räume auf …", "Cleaning up …");
            await Task.Run(() =>
            {
                Sh.Ipconfig("/flushdns");
                Sh.Netsh("interface ip delete arpcache");
                try { Sh.Run(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nbtstat.exe"), "-R", 15000, Encoding.Default); } catch { }
            });
            RepairStatus.Text = L.T("DNS-, ARP- und NetBIOS-Cache geleert.", "DNS, ARP and NetBIOS caches cleared.");
            RepairFlush.IsEnabled = true;
        }

        async void RepairWinsock_Click(object sender, RoutedEventArgs e)
        {
            var answer = MessageBox.Show(this,
                L.T("Winsock wird auf den Windows-Standard zurückgesetzt. Danach ist ein Neustart nötig, und Programme, die sich in den Netzwerk-Stack einklinken (z. B. manche VPNs oder Virenscanner), müssen ggf. neu eingerichtet werden.\n\nFortfahren?",
                    "Winsock will be reset to the Windows default. A restart is needed afterwards, and programs that hook into the network stack (some VPNs or antivirus tools) may need to be set up again.\n\nContinue?"),
                "Retrac Boost", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            RepairWinsock.IsEnabled = false;
            var r = await Task.Run(() => Sh.Netsh("winsock reset"));
            RepairStatus.Text = r.Ok
                ? L.T("Zurückgesetzt – bitte Windows neu starten.", "Reset – please restart Windows.")
                : L.T("Fehler: ", "Error: ") + Shorten(r.Text);
            RepairWinsock.IsEnabled = true;
        }
    }
}
