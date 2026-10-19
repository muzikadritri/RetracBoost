using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace RetracBoost
{
    sealed class GameItem
    {
        public string Exe { get; set; }
        public string Title { get; set; }
    }

    /// <summary>Eine Zeile in der Liste rechts (Schalter + Beschreibung).</summary>
    sealed class Row : INotifyPropertyChanged
    {
        readonly Func<bool> allowToggle;
        string detail, actionText;
        bool on, busy;

        public Row(Tweak tweak, Func<bool> allow) { Tweak = tweak; allowToggle = allow; ActionVisibility = Visibility.Collapsed; }

        public Tweak Tweak { get; private set; }
        public string Title { get { return Tweak.Title; } }
        public string ActionText { get { return actionText; } set { actionText = value; Raise("ActionText"); } }
        public Visibility ActionVisibility { get; set; }
        public Action<Row, bool> UserChanged;

        public string Detail { get { return detail; } set { detail = value; Raise("Detail"); } }

        /// <summary>Wird vom Schalter (TwoWay) gesetzt; löst die Aktion aus.</summary>
        public bool IsOn
        {
            get { return on; }
            set
            {
                if (on == value) return;
                on = value;
                Raise("IsOn");
                var h = UserChanged;
                if (h != null) h(this, value);
            }
        }

        /// <summary>Setzt den Zustand ohne Aktion auszulösen.</summary>
        public void SetOn(bool value)
        {
            if (on == value) return;
            on = value;
            Raise("IsOn");
        }

        public bool Busy { get { return busy; } set { busy = value; Raise("Busy"); Raise("CanToggle"); } }
        public bool CanToggle { get { return !busy && allowToggle(); } }
        public void RefreshEnabled() { Raise("CanToggle"); }

        public event PropertyChangedEventHandler PropertyChanged;
        void Raise(string name) { var h = PropertyChanged; if (h != null) h(this, new PropertyChangedEventArgs(name)); }
    }

    public partial class MainWindow : Window
    {
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("psapi.dll")]
        static extern int EmptyWorkingSet(IntPtr hwProc);

        public static void TrimWorkingSet()
        {
            try
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced);
                GC.WaitForPendingFinalizers();
                EmptyWorkingSet(Process.GetCurrentProcess().Handle);
            }
            catch { }
        }

        static Target[] Presets { get { return Regions.Presets; } }

        static readonly Brush BadBrush = new SolidColorBrush(Color.FromRgb(0xF0, 0x61, 0x6D));

        readonly Pinger pinger = new Pinger();
        readonly List<PingSample> samples = new List<PingSample>();
        readonly PingGraph graph = new PingGraph();
        readonly ObservableCollection<Row> rows = new ObservableCollection<Row>();
        readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        readonly DispatcherTimer watchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };

        const string OgfnExe = "FortniteClient-Win64-Shipping.exe";
        const string RetracProcess = "retrac";

        // Seite 1: Boost
        readonly NetStackTweak net = new NetStackTweak();
        readonly AdapterTweak adapter = new AdapterTweak();
        readonly PowerTweak power = new PowerTweak();
        readonly FpsBoostTweak fpsBoost = new FpsBoostTweak();
        readonly GameTweak game = new GameTweak();
        readonly DnsTweak dns = new DnsTweak();
        readonly Row rowNet, rowAdapter, rowPower, rowFps, rowGame, rowDns;

        // Seite 2: Werkzeuge
        readonly TcpTuneTweak tcp = new TcpTuneTweak();
        readonly BackgroundTweak background = new BackgroundTweak();
        readonly TimerTweak timerTweak = new TimerTweak();
        readonly AutoStartTweak autoStart = new AutoStartTweak();
        readonly MtuTweak mtu = new MtuTweak();
        readonly SettingTweak overlayTweak, autoBoostTweak, gameMenuTweak;
        readonly Row rowTcp, rowBg, rowTimer, rowAutoStart, rowOverlay, rowAutoBoost, rowGameMenu;

        // Seite 3: Retrac
        readonly GameTweak ogfn = new GameTweak("ogfn", "RetracBoost-OGFN", L.T("Netzwerk- und CPU-Priorität", "Network and CPU priority")) { Exe = OgfnExe };
        readonly ServerLockTweak serverLock = new ServerLockTweak();
        readonly PotatoTweak potato = new PotatoTweak();
        readonly FsoTweak fso = new FsoTweak();
        readonly RegValueTweak dvr;
        readonly MouseAccelTweak mouse = new MouseAccelTweak();
        readonly EngineLatencyTweak engine = new EngineLatencyTweak();
        readonly Row rowOgfn, rowServerLock, rowPotato, rowFso, rowDvr, rowMouse, rowEngine;

        OverlayWindow overlay;
        GameMenuWindow gameMenu;
        string host;
        bool sequence;
        Stats baseline;
        string baselineHost;
        DateTime? applyDoneAt;

        public MainWindow()
        {
            InitializeComponent();

            if (HeaderVersionBadge != null)
                HeaderVersionBadge.Text = "v" + AutoUpdater.CurrentVersion;
            LocalizeUpdater();
            AutoUpdater.ForgetStoredRepo();
            TxtGitHubRepo.Text = AutoUpdater.GitHubRepo;

            SourceInitialized += (s, e) => { ApplyTitleBar(); InitGameMenu(); };
            Closing += (s, e) => pinger.Stop();

            LabelAvg.Text = L.T("Durchschnitt", "Average");
            LabelJitter.Text = "Jitter";
            LabelLoss.Text = L.T("Verlust", "Loss");
            LabelMax.Text = L.T("Spitze", "Peak");
            CustomHint.Text = L.T("Eigenes Ziel: IP oder Hostname + Enter", "Custom target: IP or hostname + Enter");
            BrowseButton.Content = L.T("Programmdatei auswählen …", "Choose program file …");
            ClearGameButton.Content = L.T("Auswahl entfernen", "Clear selection");
            GameHeader.Text = L.T("Laufende Programme", "Running programs");
            GraphHost.Child = graph;

            game.Exe = Store.Get("game");

            Func<bool> elevated = () => Admin.IsElevated;
            rowNet = new Row(net, elevated);
            rowAdapter = new Row(adapter, elevated);
            rowPower = new Row(power, elevated);
            rowFps = new Row(fpsBoost, elevated);
            rowGame = new Row(game, elevated) { ActionText = L.T("Ändern", "Change"), ActionVisibility = Visibility.Visible };
            rowDns = new Row(dns, () => Admin.IsElevated && dns.CanToggle);
            foreach (var r in new[] { rowNet, rowAdapter, rowPower, rowFps, rowGame, rowDns })
                rows.Add(r);
            RowList.ItemsSource = rows;

            // Werkzeuge
            overlayTweak = new SettingTweak("overlay", L.T("Ping-Overlay", "Ping overlay"),
                L.T("Kleine Ping-Anzeige über dem Spiel (Fenster- oder randloser Modus), per Maus verschiebbar",
                    "Small ping display over the game (windowed or borderless), draggable"),
                on => Dispatcher.Invoke(new Action(() => SetOverlay(on))));
            autoBoostTweak = new SettingTweak("autoboost", L.T("Auto-Boost beim Spielstart", "Auto-boost on game start"),
                L.T("Aktiviert den Boost automatisch, sobald dein Spiel startet (ohne Netzwerkkarten-Neustart)",
                    "Turns the boost on automatically when your game starts (no adapter restart)"),
                null);
            gameMenuTweak = new SettingTweak("gamemenu", L.T("Ingame-Menü (Alt+L)", "In-game menu (Alt+L)"),
                L.T("Alt+L zeigt über dem Spiel Gamma, Helligkeit, Kontrast und Farbton",
                    "Alt+L shows gamma, brightness, contrast and color tone over the game"),
                on => Dispatcher.Invoke(new Action(() => SetGameMenuHotkey(on))), true);
            rowTcp = new Row(tcp, elevated);
            rowBg = new Row(background, elevated);
            rowTimer = new Row(timerTweak, elevated);
            rowAutoStart = new Row(autoStart, elevated);
            rowOverlay = new Row(overlayTweak, () => true);
            rowAutoBoost = new Row(autoBoostTweak, () => true);
            rowGameMenu = new Row(gameMenuTweak, () => true);
            SysListLeft.ItemsSource = new[] { rowTcp, rowBg, rowTimer, rowGameMenu };
            SysListRight.ItemsSource = new[] { rowAutoStart, rowOverlay, rowAutoBoost };

            // Retrac
            dvr = new RegValueTweak("dvr", L.T("Spielmodus an, Aufnahme aus", "Game Mode on, capture off"),
                L.T("Windows-Spielmodus aktiv, Xbox-Aufzeichnung im Hintergrund aus", "Windows Game Mode on, background Xbox capture off"),
                () => new List<RegItem>
                {
                    RegItem.Dw(@"System\GameConfigStore", "GameDVR_Enabled", 0, 1, null, true),
                    RegItem.Dw(@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, 1, null, true),
                    RegItem.Dw(@"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, null, null, true)
                });
            rowOgfn = new Row(ogfn, elevated);
            rowServerLock = new Row(serverLock, elevated);
            rowPotato = new Row(potato, () => Admin.IsElevated && potato.Relevant)
            {
                ActionText = potato.CurrentPreset.ActionLabel,
                ActionVisibility = Visibility.Visible
            };
            rowFso = new Row(fso, () => Admin.IsElevated && fso.Relevant);
            rowDvr = new Row(dvr, elevated);
            rowMouse = new Row(mouse, elevated);
            rowEngine = new Row(engine, () => Admin.IsElevated && engine.Relevant);
            RetracRowList.ItemsSource = new[] { rowNet, rowAdapter, rowPower, rowFps, rowOgfn, rowServerLock, rowPotato, rowFso, rowDvr, rowMouse, rowEngine };

            foreach (var r in new[] { rowNet, rowAdapter, rowPower, rowFps, rowGame, rowDns, rowTcp, rowBg, rowTimer, rowAutoStart,
                                      rowOverlay, rowAutoBoost, rowGameMenu, rowOgfn, rowServerLock, rowPotato, rowFso, rowDvr, rowMouse, rowEngine })
            {
                r.Busy = true;
                r.Detail = L.T("Prüfe …", "Checking …");
                r.UserChanged = OnRowToggled;
            }

            TabBoost.Content = "Boost";
            TabTools.Content = L.T("Werkzeuge", "Tools");
            TabRetrac.Content = "Retrac";
            LocalizeTools();
            LocalizeRetrac();

            TargetList.ItemsSource = Presets;
            if (!Admin.IsElevated)
                AdminText.Text = L.T("Ohne Administratorrechte sind keine Änderungen möglich.", "Changes need administrator rights.");

            pinger.Sampled += s => Dispatcher.BeginInvoke(new Action(() => OnSample(s)));
            pinger.StateChanged += (h, m, ip) => Dispatcher.BeginInvoke(new Action(() => OnProbeState(h, m, ip)));

            int tickCounter = 0;
            watchTimer.Tick += (s, e) =>
            {
                WatchTick();
                if (++tickCounter % 30 == 0) TrimWorkingSet(); // alle 60 s Working Set trimmen
            };
            watchTimer.Start();

            StateChanged += (s, e) =>
            {
                if (WindowState == WindowState.Minimized)
                {
                    watchTimer.Interval = TimeSpan.FromSeconds(5);
                    TrimWorkingSet();
                }
                else
                {
                    watchTimer.Interval = TimeSpan.FromSeconds(2);
                    var tail = samples.Skip(Math.Max(0, samples.Count - PingGraph.Points)).Select(x => x.Ms).ToArray();
                    graph.SetValues(tail);
                }
            };

            SetTarget(Store.Get("target") ?? "1.1.1.1");
            InitPopups();
            UpdateMaster();
            UpdateRetracMaster();
            Loaded += async (s, e) =>
            {
                await InitAsync();
                await Task.Delay(2500);
                TrimWorkingSet();
            };
            Closing += (s, e) =>
            {
                if (overlay != null) overlay.Close();
                CloseGameMenu();
            };

            if (Environment.GetCommandLineArgs().Any(a => a == "--minimized")) WindowState = WindowState.Minimized;
        }

        public void SelectTab(int index)
        {
            (index == 1 ? TabTools : index == 2 ? TabRetrac : TabBoost).IsChecked = true;
        }

        void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (PageBoost == null || PageTools == null || PageRetrac == null) return; // beim Laden

            FrameworkElement targetPage = null;
            TranslateTransform targetTrans = null;

            if (TabBoost.IsChecked == true)
            {
                targetPage = PageBoost;
                targetTrans = PageBoostTrans;
                PageTools.Visibility = Visibility.Collapsed;
                PageRetrac.Visibility = Visibility.Collapsed;
            }
            else if (TabTools.IsChecked == true)
            {
                targetPage = PageTools;
                targetTrans = PageToolsTrans;
                PageBoost.Visibility = Visibility.Collapsed;
                PageRetrac.Visibility = Visibility.Collapsed;
            }
            else if (TabRetrac.IsChecked == true)
            {
                targetPage = PageRetrac;
                targetTrans = PageRetracTrans;
                PageBoost.Visibility = Visibility.Collapsed;
                PageTools.Visibility = Visibility.Collapsed;
            }

            if (targetPage != null)
            {
                targetPage.Visibility = Visibility.Visible;
                var animFade = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                targetPage.BeginAnimation(UIElement.OpacityProperty, animFade);

                if (targetTrans != null)
                {
                    var animSlide = new DoubleAnimation(10.0, 0.0, TimeSpan.FromMilliseconds(220))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };
                    targetTrans.BeginAnimation(TranslateTransform.YProperty, animSlide);
                }
            }
        }

        // ------------------------------------------------------------------ Auto-Updater

        UpdateInfo latestUpdateInfo;

        void LocalizeUpdater()
        {
            UpdateBadgeText.Text = L.T("Aktuell", "Up to date") + " (v" + AutoUpdater.CurrentVersion + ")";
            UpdateBadgeButton.ToolTip = L.T("Updates und Änderungen", "Updates and changes");
            ModalSubtitle.Text = L.T("Versionen und Änderungen", "Versions and changes");
            ModalStatusHeader.Text = L.T("Noch nicht geprüft", "Not checked yet");
            ModalChangelog.Text = L.T("Klick auf „Nach Updates suchen“.", "Click “Check for updates”.");
            LabelGitHubRepo.Text = L.T("GitHub Repo:", "GitHub repo:");
            ModalProgressLabel.Text = L.T("Lade herunter …", "Downloading …");
            ModalCheckBtn.Content = L.T("Nach Updates suchen", "Check for updates");
            ModalActionBtn.Content = L.T("Schließen", "Close");
        }

        async Task CheckUpdatesAsync(bool silent)
        {
            try
            {
                if (!silent)
                {
                    ModalStatusHeader.Text = L.T("Suche nach Updates …", "Checking for updates …");
                    ModalChangelog.Text = L.T("Frage github.com/", "Asking github.com/") + AutoUpdater.ResolveGitHubRepo() + " …";
                    ModalCheckBtn.IsEnabled = false;
                }

                var info = await AutoUpdater.CheckForUpdatesAsync();
                latestUpdateInfo = info;

                if (TxtGitHubRepo != null)
                    TxtGitHubRepo.Text = AutoUpdater.ResolveGitHubRepo();

                if (info.CheckFailed)
                {
                    // Nicht prüfen können heißt nicht "aktuell"
                    latestUpdateInfo = null;
                    UpdateBadgeText.Text = "v" + AutoUpdater.CurrentVersion;
                    UpdateDot.Fill = (Brush)FindResource("Muted");
                    ModalStatusHeader.Text = L.T("Update-Suche fehlgeschlagen", "Update check failed");
                    ModalChangelog.Text = info.Changelog;
                    ModalActionBtn.Content = L.T("Schließen", "Close");
                    ModalActionBtn.Style = (Style)FindResource("Action");
                    ModalActionBtn.Click -= StartUpdateInstall_Click;
                    ModalActionBtn.Click -= UpdateClose_Click;
                    ModalActionBtn.Click += UpdateClose_Click;
                }
                else if (info.HasUpdate)
                {
                    UpdateBadgeText.Text = L.T("Update verfügbar", "Update available") + " (v" + info.LatestVersion + ")";
                    UpdateDot.Fill = new SolidColorBrush(Color.FromRgb(0xF2, 0xB8, 0x4B)); // Warn
                    ModalStatusHeader.Text = string.Format(L.T("Version {0} ist da", "Version {0} is out"), info.LatestVersion);
                    ModalChangelog.Text = string.IsNullOrEmpty(info.Changelog) ? L.T("Keine Beschreibung.", "No description.") : info.Changelog;
                    ModalActionBtn.Content = string.Format(L.T("Auf v{0} aktualisieren", "Update to v{0}"), info.LatestVersion);
                    ModalActionBtn.Style = (Style)FindResource("Primary");
                    ModalActionBtn.Click -= UpdateClose_Click;
                    ModalActionBtn.Click -= StartUpdateInstall_Click;
                    ModalActionBtn.Click += StartUpdateInstall_Click;
                }
                else
                {
                    UpdateBadgeText.Text = L.T("Aktuell", "Up to date") + " (v" + AutoUpdater.CurrentVersion + ")";
                    UpdateDot.Fill = new SolidColorBrush(Color.FromRgb(0x3D, 0xDC, 0x97)); // Accent
                    ModalStatusHeader.Text = L.T("Kein Update nötig", "You're up to date");
                    ModalChangelog.Text = L.T("Installiert: v", "Installed: v") + AutoUpdater.CurrentVersion + "\n" +
                                          L.T("Quelle: github.com/", "Source: github.com/") + AutoUpdater.ResolveGitHubRepo();
                    ModalActionBtn.Content = L.T("Schließen", "Close");
                    ModalActionBtn.Style = (Style)FindResource("Action");
                    ModalActionBtn.Click -= StartUpdateInstall_Click;
                    ModalActionBtn.Click -= UpdateClose_Click;
                    ModalActionBtn.Click += UpdateClose_Click;
                }
            }
            catch (Exception ex)
            {
                if (!silent)
                {
                    ModalStatusHeader.Text = L.T("Update-Suche fehlgeschlagen", "Update check failed");
                    ModalChangelog.Text = ex.Message;
                }
            }
            finally
            {
                ModalCheckBtn.IsEnabled = true;
            }
        }

        void UpdateBadge_Click(object sender, RoutedEventArgs e)
        {
            if (TxtGitHubRepo != null)
                TxtGitHubRepo.Text = AutoUpdater.ResolveGitHubRepo();

            UpdateModal.Visibility = Visibility.Visible;
            if (latestUpdateInfo == null)
            {
                _ = CheckUpdatesAsync(false);
            }
        }

        void OpenGitHub_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(AutoUpdater.ReleasesUrl) { UseShellExecute = true });
            }
            catch { }
        }

        void UpdateClose_Click(object sender, RoutedEventArgs e)
        {
            UpdateModal.Visibility = Visibility.Collapsed;
        }

        void UpdateModal_BackgroundClick(object sender, MouseButtonEventArgs e)
        {
            UpdateModal.Visibility = Visibility.Collapsed;
        }

        void UpdateModal_DialogClick(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
        }

        async void UpdateCheckNow_Click(object sender, RoutedEventArgs e)
        {
            await CheckUpdatesAsync(false);
        }

        async void StartUpdateInstall_Click(object sender, RoutedEventArgs e)
        {
            if (latestUpdateInfo == null || !latestUpdateInfo.HasUpdate || string.IsNullOrEmpty(latestUpdateInfo.DownloadUrl))
            {
                UpdateModal.Visibility = Visibility.Collapsed;
                return;
            }

            try
            {
                ModalProgressPanel.Visibility = Visibility.Visible;
                ModalActionBtn.IsEnabled = false;
                ModalCheckBtn.IsEnabled = false;

                string downloadedFile = await AutoUpdater.DownloadUpdateAsync(latestUpdateInfo.DownloadUrl, p =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        ModalProgressBar.Value = p;
                        ModalProgressPercent.Text = p + "%";
                    });
                });

                ModalStatusHeader.Text = L.T("Starte neu …", "Restarting …");
                AutoUpdater.ApplyUpdateAndRestart(downloadedFile);
            }
            catch (Exception ex)
            {
                ModalProgressPanel.Visibility = Visibility.Collapsed;
                ModalActionBtn.IsEnabled = true;
                ModalCheckBtn.IsEnabled = true;
                ModalStatusHeader.Text = L.T("Update fehlgeschlagen", "Update failed");
                ModalChangelog.Text = ex.Message;
            }
        }

        // ------------------------------------------------------------------ Fenster

        void ApplyTitleBar()
        {
            try
            {
                var h = new WindowInteropHelper(this).Handle;
                int on = 1;
                DwmSetWindowAttribute(h, 20, ref on, 4);          // dunkle Titelleiste
                int bg = 0x00120D0B;                              // #0B0D12 als COLORREF (BGR)
                DwmSetWindowAttribute(h, 35, ref bg, 4);          // Titelleistenfarbe (Windows 11)
                DwmSetWindowAttribute(h, 34, ref bg, 4);          // Rahmenfarbe
                int fg = 0x00F2ECE9;
                DwmSetWindowAttribute(h, 36, ref fg, 4);          // Titeltext
            }
            catch { }
        }

        // ------------------------------------------------------------------ Start

        Row[] AllRows()
        {
            return new[] { rowNet, rowAdapter, rowPower, rowFps, rowGame, rowDns, rowTcp, rowBg, rowTimer, rowAutoStart,
                           rowOverlay, rowAutoBoost, rowGameMenu, rowOgfn, rowServerLock, rowPotato, rowFso, rowDvr, rowMouse, rowEngine };
        }

        async Task InitAsync()
        {
            DetectOgfnPath();
            await gate.WaitAsync();
            try
            {
                var all = AllRows();
                var states = await Task.Run(() => all
                    .Select(r => { try { return r.Tweak.Relevant && r.Tweak.IsApplied(); } catch { return false; } }).ToArray());
                for (int i = 0; i < all.Length; i++)
                {
                    all[i].SetOn(states[i]);
                    all[i].Detail = all[i].Tweak.Detail;
                    all[i].Busy = false;
                    all[i].RefreshEnabled();
                }
                rowDns.Busy = true;
                rowDns.Detail = dns.Detail;

                if (rowTimer.IsOn) TimerHold.Enable();          // Systemuhr-Auflösung gilt nur, solange diese App läuft
                if (rowOverlay.IsOn) SetOverlay(true);
                SetGameMenuHotkey(rowGameMenu.IsOn);
            }
            finally { gate.Release(); }
            UpdateMaster();
            UpdateRetracMaster();

            await RefreshServerLockAsync();              // alte Regeln (ältere Version/Datenbasis) still erneuern

            // DNS-Messung läuft im Hintergrund, blockiert aber nichts.
            await MeasureDns();

            // Prüfe auf Updates im Hintergrund
            _ = CheckUpdatesAsync(true);
        }

        async Task MeasureDns()
        {
            rowDns.Busy = true;
            rowDns.Detail = L.T("Messe Antwortzeiten …", "Measuring response times …");
            try { await Task.Run(() => dns.Benchmark()); } catch { }
            bool applied = await Task.Run(() => { try { return dns.IsApplied(); } catch { return false; } });
            rowDns.SetOn(applied);
            rowDns.Detail = dns.Detail;
            rowDns.Busy = false;
            rowDns.RefreshEnabled();
            UpdateMaster();
        }

        // ------------------------------------------------------------------ Ping

        void SetTarget(string value)
        {
            host = value.Trim();
            samples.Clear();
            baseline = null;
            baselineHost = null;
            applyDoneAt = null;
            CompareText.Inlines.Clear();

            var preset = Presets.FirstOrDefault(p => string.Equals(p.Host, host, StringComparison.OrdinalIgnoreCase));
            TargetText.Text = preset != null ? preset.Name : host;
            ModeText.Text = "";
            PingValue.Text = "–";
            QualityText.Text = L.T("Messe …", "Measuring …");
            QualityDot.Fill = (Brush)FindResource("Muted");
            StatAvg.Text = StatJitter.Text = StatLoss.Text = StatMax.Text = "–";
            graph.SetValues(new double?[0]);

            Store.TrySet("target", host);
            pinger.Start(host);
        }

        void OnProbeState(string h, ProbeMode mode, string ip)
        {
            if (h != host) return;
            string how = mode == ProbeMode.Icmp ? "ICMP" : mode == ProbeMode.Tcp ? "TCP 443" : "";
            ModeText.Text = how == "" ? "" : how + (ip != null && ip != host ? " · " + ip : "");
        }

        void OnSample(PingSample s)
        {
            if (s.Host != host) return;
            samples.Add(s);
            if (samples.Count > 1200) samples.RemoveRange(0, 300);

            var window = samples.Skip(Math.Max(0, samples.Count - 60)).ToList();
            var st = Stats.From(window);

            if (overlay != null || gameMenu != null) UpdateOverlay(s, st);

            // Wenn das Fenster minimiert ist, keine WPF-Zeichenarbeit durchführen (0% CPU)
            if (WindowState == WindowState.Minimized) return;

            // große Zahl
            PingValue.Text = s.Ms.HasValue ? (s.Ms.Value < 1 ? "<1" : s.Ms.Value.ToString("0", CultureInfo.CurrentCulture)) : "–";

            // Qualität anhand der letzten 5 Messungen
            var last5 = samples.Skip(Math.Max(0, samples.Count - 5)).ToList();
            var ok5 = last5.Where(x => x.Ms.HasValue).Select(x => x.Ms.Value).ToList();
            if (ok5.Count == 0)
            {
                QualityText.Text = L.T("Keine Antwort", "No reply");
                QualityDot.Fill = BadBrush;
            }
            else
            {
                double v = ok5.Average();
                string label = v <= 30 ? L.T("Hervorragend", "Excellent")
                    : v <= 60 ? L.T("Sehr gut", "Very good")
                    : v <= 100 ? L.T("Gut", "Good")
                    : v <= 150 ? L.T("Spielbar", "Playable")
                    : L.T("Hoch", "High");
                QualityText.Text = label;
                QualityDot.Fill = v <= 100 ? (Brush)FindResource("Accent") : v <= 150 ? (Brush)FindResource("Warn") : BadBrush;
            }

            // Statistik
            if (st.Replies > 0)
            {
                StatAvg.Text = Ms(st.Avg);
                StatJitter.Text = Ms(st.Jitter);
                StatMax.Text = Ms(st.Max);
            }
            StatLoss.Text = st.Loss.ToString(st.Loss > 0 && st.Loss < 10 ? "0.0" : "0", CultureInfo.CurrentCulture) + " %";
            StatLoss.Foreground = st.Loss > 0 ? BadBrush : (Brush)FindResource("Text");

            // Graph (letzte 90 Sekunden)
            var tail = samples.Skip(Math.Max(0, samples.Count - PingGraph.Points)).Select(x => x.Ms).ToArray();
            graph.SetValues(tail);

            UpdateCompare();
        }

        static string Ms(double v)
        {
            return v.ToString(v < 10 ? "0.0" : "0", CultureInfo.CurrentCulture) + " ms";
        }

        // ------------------------------------------------------------------ Vorher / Nachher

        void UpdateCompare()
        {
            if (baseline == null || baselineHost != host || applyDoneAt == null) return;

            var cutoff = applyDoneAt.Value.AddSeconds(8);            // Adapter-Neustart abwarten
            var after = Stats.From(samples.Where(s => s.Time > cutoff));
            CompareText.Inlines.Clear();

            if (after.Replies < 10)
            {
                CompareText.Inlines.Add(new System.Windows.Documents.Run(
                    L.T("Messe Ergebnis … ", "Measuring result … ") + Math.Min(after.Replies, 10) + "/10"));
                return;
            }

            double diff = after.Avg - baseline.Avg;
            Brush tone = Math.Abs(diff) < 0.3 ? (Brush)FindResource("Muted") : diff < 0 ? (Brush)FindResource("Accent") : BadBrush;
            var text = (Brush)FindResource("Text");

            CompareText.Inlines.Add(new System.Windows.Documents.Run(L.T("Vorher  ", "Before  ")));
            CompareText.Inlines.Add(new System.Windows.Documents.Run(Ms(baseline.Avg) + " · Jitter " + Ms(baseline.Jitter)) { Foreground = text });
            CompareText.Inlines.Add(new System.Windows.Documents.LineBreak());
            CompareText.Inlines.Add(new System.Windows.Documents.Run(L.T("Jetzt     ", "Now       ")));
            CompareText.Inlines.Add(new System.Windows.Documents.Run(Ms(after.Avg) + " · Jitter " + Ms(after.Jitter)) { Foreground = text });
            CompareText.Inlines.Add(new System.Windows.Documents.Run("   " + (diff > 0 ? "+" : diff < 0 ? "−" : "±") + Math.Abs(diff).ToString("0.0", CultureInfo.CurrentCulture) + " ms") { Foreground = tone });
        }

        void CaptureBaseline()
        {
            if (baseline != null && baselineHost == host) return;
            var recent = samples.Skip(Math.Max(0, samples.Count - 30)).ToList();
            var st = Stats.From(recent);
            if (st.Replies >= 8) { baseline = st; baselineHost = host; }
        }

        // ------------------------------------------------------------------ Tweaks anwenden

        bool IsCore(Row r) { return r == rowNet || r == rowAdapter || r == rowPower || r == rowFps || r == rowGame; }

        bool CoreActive()
        {
            return rowNet.IsOn && rowAdapter.IsOn && rowPower.IsOn && rowFps.IsOn && (!game.Relevant || rowGame.IsOn);
        }

        void UpdateMaster()
        {
            bool busy = sequence || rows.Any(r => r.Busy && r != rowDns);
            bool core = CoreActive();
            bool anyOurs = rows.Any(r => r.IsOn && r.Tweak.Relevant && ((IsCore(r) && r.Tweak.WeApplied) || (r == rowDns && dns.HasBackup)));

            BoostButton.Tag = null;
            if (busy)
            {
                BoostButton.IsEnabled = false;
                BoostButton.Content = L.T("Einen Moment …", "One moment …");
            }
            else if (core && anyOurs)
            {
                BoostButton.IsEnabled = Admin.IsElevated;
                BoostButton.Tag = "on";
                BoostButton.Content = L.T("Boost aktiv", "Boost active");
                BoostHint.Text = L.T("Klicken, um deine Änderungen zurückzusetzen", "Click to undo the changes made here");
            }
            else if (core)
            {
                BoostButton.IsEnabled = false;
                BoostButton.Content = L.T("Alles bereits optimal", "Already optimal");
                BoostHint.Text = L.T("Dein System war schon so eingestellt – es gab nichts zu ändern.", "Your system was already set up like this – nothing to change.");
            }
            else
            {
                BoostButton.IsEnabled = !busy && Admin.IsElevated;
                BoostButton.Content = L.T("Boost starten", "Start boost");
                BoostHint.Text = L.T("Wendet die Optimierungen unten an", "Applies the optimisations below");
            }
            UpdateRetracMaster();
        }

        /// <summary>Nur Änderungen am Netzwerk fließen in den Vorher/Jetzt-Vergleich ein.</summary>
        static bool AffectsNetwork(Tweak t)
        {
            return t is NetStackTweak || t is AdapterTweak || t is PowerTweak || t is DnsTweak || t is GameTweak
                || t is TcpTuneTweak || t is BackgroundTweak;
        }

        async void BoostButton_Click(object sender, RoutedEventArgs e)
        {
            if (sequence) return;
            bool core = CoreActive();
            sequence = true;
            UpdateMaster();
            try
            {
                if (core)
                {
                    foreach (var r in rows.Where(r => r.IsOn && r.Tweak.Relevant && ((IsCore(r) && r.Tweak.WeApplied) || (r == rowDns && dns.HasBackup))).ToList())
                        await RunOne(r, false);
                    baseline = null;
                    CompareText.Inlines.Clear();
                }
                else
                {
                    foreach (var r in rows.Where(r => !r.IsOn && IsCore(r) && r.Tweak.Relevant).ToList())
                        await RunOne(r, true);
                }
            }
            finally
            {
                sequence = false;
                UpdateMaster();
            }
        }

        async void OnRowToggled(Row row, bool value)
        {
            if (sequence || row.Busy)
            {
                var _a = Dispatcher.BeginInvoke(new Action(() => row.SetOn(!value)));
                return;
            }
            if (row == rowGame && value && !game.Relevant)
            {
                var _b = Dispatcher.BeginInvoke(new Action(() => { row.SetOn(false); OpenGamePicker(); }));
                return;
            }
            await RunOne(row, value);
            UpdateMaster();
        }

        async Task RunOne(Row row, bool on)
        {
            await gate.WaitAsync();
            try
            {
                var t = row.Tweak;
                row.Busy = true;
                row.Detail = t.BusyText;
                UpdateMaster();
                if (on && AffectsNetwork(t)) CaptureBaseline();

                string error = await Task.Run(() =>
                {
                    try { if (on) t.Apply(); else t.Revert(); return (string)null; }
                    catch (Exception ex) { return ex.Message; }
                });

                if (t is DnsTweak)
                    try { await Task.Run(() => dns.Benchmark()); } catch { }

                bool applied = await Task.Run(() => { try { return t.Relevant && t.IsApplied(); } catch { return false; } });
                row.SetOn(applied);
                row.Detail = error != null ? L.T("Fehler: ", "Error: ") + Shorten(error) : t.Detail;
                row.Busy = false;
                row.RefreshEnabled();
                if (on && error == null && AffectsNetwork(t)) applyDoneAt = DateTime.UtcNow;
            }
            finally
            {
                gate.Release();
                UpdateMaster();
            }
        }

        static string Shorten(string s)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > 110 ? s.Substring(0, 107) + "…" : s;
        }

        // ------------------------------------------------------------------ Ziel-Popup

        void TargetButton_Click(object sender, RoutedEventArgs e)
        {
            TargetList.SelectedItem = null;
            CustomBox.Text = "";
            CustomHint.Visibility = Visibility.Visible;
            TargetPopup.IsOpen = true;
        }

        void TargetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var t = TargetList.SelectedItem as Target;
            if (t == null) return;
            TargetPopup.IsOpen = false;
            SetTarget(t.Host);
        }

        void CustomBox_KeyDown(object sender, KeyEventArgs e)
        {
            CustomHint.Visibility = Visibility.Collapsed;
            if (e.Key != Key.Enter) return;

            string v = (CustomBox.Text ?? "").Trim();
            int scheme = v.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) v = v.Substring(scheme + 3);
            int slash = v.IndexOf('/');
            if (slash >= 0) v = v.Substring(0, slash);
            if (v.Count(c => c == ':') == 1) v = v.Substring(0, v.IndexOf(':'));   // Port entfernen
            if (v.Length == 0 || v.Any(c => char.IsWhiteSpace(c))) return;

            TargetPopup.IsOpen = false;
            SetTarget(v);
        }

        void InitPopups()
        {
            PotatoPopup.CustomPopupPlacementCallback = (popupSize, targetSize, offset) =>
            {
                // Richtet die rechte Kante des Dropdown-Fensters an der rechten Kante der Zeile aus
                double x = targetSize.Width - popupSize.Width + 48;
                double yBelow = targetSize.Height - 6;
                double yAbove = -popupSize.Height + 6;

                return new[]
                {
                    new CustomPopupPlacement(new Point(x, yBelow), PopupPrimaryAxis.Horizontal),
                    new CustomPopupPlacement(new Point(x, yAbove), PopupPrimaryAxis.Horizontal)
                };
            };

            GamePopup.CustomPopupPlacementCallback = (popupSize, targetSize, offset) =>
            {
                double x = targetSize.Width - popupSize.Width + 48;
                double yBelow = targetSize.Height - 6;
                double yAbove = -popupSize.Height + 6;

                return new[]
                {
                    new CustomPopupPlacement(new Point(x, yBelow), PopupPrimaryAxis.Horizontal),
                    new CustomPopupPlacement(new Point(x, yAbove), PopupPrimaryAxis.Horizontal)
                };
            };
        }

        // ------------------------------------------------------------------ Spiel-Popup

        void Action_Click(object sender, RoutedEventArgs e)
        {
            var fe = sender as FrameworkElement;
            var r = fe != null ? fe.DataContext as Row : null;
            if (r == rowPotato) OpenPotatoPicker(fe);
            else OpenGamePicker(fe);
        }

        async void OpenGamePicker(FrameworkElement anchor = null)
        {
            ClearGameButton.Visibility = game.Relevant ? Visibility.Visible : Visibility.Collapsed;
            if (anchor != null) GamePopup.PlacementTarget = anchor;
            else GamePopup.PlacementTarget = RowList;
            var items = await Task.Run(() => RunningPrograms());
            GameList.SelectionChanged -= GameList_SelectionChanged;
            GameList.ItemsSource = items;
            GameList.SelectedItem = null;
            GameList.SelectionChanged += GameList_SelectionChanged;
            GamePopup.IsOpen = true;
        }

        static List<GameItem> RunningPrograms()
        {
            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "RetracBoost", "explorer", "ApplicationFrameHost", "TextInputHost", "SystemSettings", "ShellExperienceHost",
                "SearchHost", "StartMenuExperienceHost", "Widgets", "WidgetService", "LockApp", "Taskmgr", "dwm", "conhost",
                "WindowsTerminal", "powershell", "cmd"
            };
            var list = new List<GameItem>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.MainWindowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(p.MainWindowTitle)) continue;
                    if (skip.Contains(p.ProcessName)) continue;
                    list.Add(new GameItem { Exe = p.ProcessName + ".exe", Title = p.MainWindowTitle });
                }
                catch { }
                finally { p.Dispose(); }
            }
            return list.GroupBy(i => i.Exe, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).OrderBy(i => i.Exe, StringComparer.OrdinalIgnoreCase).ToList();
        }

        async void GameList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var item = GameList.SelectedItem as GameItem;
            if (item == null) return;
            GamePopup.IsOpen = false;
            await SetGame(item.Exe);
        }

        async void Browse_Click(object sender, RoutedEventArgs e)
        {
            GamePopup.IsOpen = false;
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = L.T("Programme (*.exe)|*.exe", "Programs (*.exe)|*.exe"),
                CheckFileExists = true,
                Title = L.T("Spiel auswählen", "Choose game")
            };
            if (dlg.ShowDialog(this) == true) await SetGame(Path.GetFileName(dlg.FileName));
        }

        async void ClearGame_Click(object sender, RoutedEventArgs e)
        {
            GamePopup.IsOpen = false;
            await SetGame(null);
        }

        async Task SetGame(string exe)
        {
            if (sequence || rowGame.Busy) return;
            if (rowGame.IsOn) await RunOne(rowGame, false);          // alte Regel entfernen

            game.Exe = exe;
            Store.TrySet("game", exe);
            rowGame.Detail = game.Detail;

            if (exe != null) await RunOne(rowGame, true);            // gewähltes Spiel sofort priorisieren
            rowGame.RefreshEnabled();
            UpdateMaster();
        }
    }
}
