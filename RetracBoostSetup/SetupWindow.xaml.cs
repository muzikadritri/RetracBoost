using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace RetracBoostSetup
{
    public sealed class LogRow
    {
        public string Icon { get; set; }
        public string Message { get; set; }
    }

    public static class InstallerEngine
    {
        public const string UninstallRegPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RetracBoost";

        public static string DefaultInstallDir
        {
            get
            {
                try
                {
                    using (var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(UninstallRegPath))
                    {
                        if (key != null)
                        {
                            string loc = key.GetValue("InstallLocation") as string;
                            if (!string.IsNullOrWhiteSpace(loc)) return loc;
                        }
                    }
                }
                catch { }
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RetracBoost");
            }
        }

        public static void ExtractResource(string logicalName, string targetPath)
        {
            var asm = Assembly.GetExecutingAssembly();
            using (var s = asm.GetManifestResourceStream(logicalName))
            {
                if (s == null) throw new InvalidOperationException("Eingebettete Datei fehlt: " + logicalName);
                string dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                using (var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    s.CopyTo(fs);
            }
        }

        public static void StopRunningInstances()
        {
            try
            {
                foreach (var p in Process.GetProcessesByName("RetracBoost"))
                {
                    using (p)
                    {
                        try { p.Kill(); p.WaitForExit(4000); } catch { }
                    }
                }
            }
            catch { }
        }

        public static void CreateShortcut(string shortcutPath, string targetExe, string arguments, string description, string iconLocation)
        {
            string dir = Path.GetDirectoryName(shortcutPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            object shell = Activator.CreateInstance(shellType);
            object sc = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });
            Type scType = sc.GetType();
            scType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { targetExe });
            scType.InvokeMember("Arguments", BindingFlags.SetProperty, null, sc, new object[] { arguments ?? "" });
            scType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(targetExe) ?? "" });
            scType.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { description ?? "Retrac Boost" });
            if (!string.IsNullOrEmpty(iconLocation) && File.Exists(iconLocation))
                scType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { iconLocation + ",0" });
            scType.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
        }

        public static void RegisterUninstaller(string installDir, string exePath, string uninstallerPath)
        {
            using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = baseKey.CreateSubKey(UninstallRegPath))
            {
                if (key == null) return;
                key.SetValue("DisplayName", "Retrac Boost", RegistryValueKind.String);
                key.SetValue("DisplayIcon", exePath + ",0", RegistryValueKind.String);
                key.SetValue("DisplayVersion", "1.0.1", RegistryValueKind.String);
                key.SetValue("Publisher", "Retrac Tools", RegistryValueKind.String);
                key.SetValue("InstallLocation", installDir, RegistryValueKind.String);
                key.SetValue("UninstallString", "\"" + uninstallerPath + "\" --uninstall", RegistryValueKind.String);
                key.SetValue("QuietUninstallString", "\"" + uninstallerPath + "\" --uninstall", RegistryValueKind.String);
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", 1400, RegistryValueKind.DWord);
            }
        }

        public static void RemoveShortcutsAndRegistry()
        {
            string[] desktopDirs =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
            };
            foreach (var d in desktopDirs.Where(x => !string.IsNullOrEmpty(x)))
            {
                string lnk = Path.Combine(d, "Retrac Boost.lnk");
                try { if (File.Exists(lnk)) File.Delete(lnk); } catch { }
            }

            string[] startDirs =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Retrac Boost"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Retrac Boost")
            };
            foreach (var s in startDirs)
            {
                try { if (Directory.Exists(s)) Directory.Delete(s, true); } catch { }
            }

            try
            {
                using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                    baseKey.DeleteSubKeyTree(UninstallRegPath, false);
            }
            catch { }

            try
            {
                using (var runKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (runKey != null) runKey.DeleteValue("RetracBoost", false);
                }
            }
            catch { }
        }
    }

    public partial class SetupWindow : Window
    {
        readonly ObservableCollection<LogRow> logs = new ObservableCollection<LogRow>();
        readonly bool isUninstallMode;
        int currentStep = 1;
        bool isInstalling;
        bool installFinished;
        string selectedPotato = "medium";
        string installedExePath;

        public SetupWindow(bool uninstallMode)
        {
            InitializeComponent();
            isUninstallMode = uninstallMode;
            LogItems.ItemsSource = logs;

            InstallPathBox.Text = InstallerEngine.DefaultInstallDir;
            UpdateDiskSpace();

            if (isUninstallMode)
            {
                EnterUninstallMode();
            }
            else
            {
                ShowStep(1);
            }
        }

        void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        }

        void Minimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        void Close_Click(object sender, RoutedEventArgs e)
        {
            if (isInstalling) return;
            Close();
        }

        void InstallPathBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateDiskSpace();
        }

        void UpdateDiskSpace()
        {
            if (DiskSpaceText == null) return;
            try
            {
                string path = (InstallPathBox.Text ?? "").Trim();
                string root = Path.GetPathRoot(path);
                if (!string.IsNullOrEmpty(root))
                {
                    var di = new DriveInfo(root);
                    if (di.IsReady)
                    {
                        double freeGb = di.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);
                        DiskSpaceText.Text = string.Format(CultureInfo.CurrentCulture,
                            "Benötigter Speicherplatz: ~1,4 MB  ·  Frei auf {0} {1:0.0} GB",
                            root.TrimEnd('\\'), freeGb);
                        return;
                    }
                }
            }
            catch { }
            DiskSpaceText.Text = "Benötigter Speicherplatz: ~1,4 MB";
        }

        void BrowseBtn_Click(object sender, RoutedEventArgs e)
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
            {
                dlg.Description = "Zielordner für Retrac Boost auswählen";
                dlg.SelectedPath = InstallPathBox.Text;
                dlg.ShowNewFolderButton = true;
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
                {
                    string chosen = dlg.SelectedPath;
                    if (!chosen.EndsWith("RetracBoost", StringComparison.OrdinalIgnoreCase))
                        chosen = Path.Combine(chosen, "RetracBoost");
                    InstallPathBox.Text = chosen;
                }
            }
        }

        // -------------------- Schnellprofile auf Seite 2 --------------------

        void ProfileEm_Click(object sender, RoutedEventArgs e)
        {
            ChkPingBoost.IsChecked = true;
            ChkFpsBoost.IsChecked = true;
            ChkRetracPack.IsChecked = true;
            ChkServerLock.IsChecked = true;
            RadPotMedium.IsChecked = true;
        }

        void ProfileStealth_Click(object sender, RoutedEventArgs e)
        {
            ChkPingBoost.IsChecked = true;
            ChkFpsBoost.IsChecked = true;
            ChkRetracPack.IsChecked = true;
            ChkServerLock.IsChecked = true;
            RadPotOff.IsChecked = true;
        }

        void ProfileUltra_Click(object sender, RoutedEventArgs e)
        {
            ChkPingBoost.IsChecked = true;
            ChkFpsBoost.IsChecked = true;
            ChkRetracPack.IsChecked = true;
            ChkServerLock.IsChecked = true;
            RadPotUltra.IsChecked = true;
        }

        void ProfileNone_Click(object sender, RoutedEventArgs e)
        {
            ChkPingBoost.IsChecked = false;
            ChkFpsBoost.IsChecked = false;
            ChkRetracPack.IsChecked = false;
            ChkServerLock.IsChecked = false;
            RadPotOff.IsChecked = false;
            RadPotOff.IsChecked = true;
        }

        void PotatoRadio_Checked(object sender, RoutedEventArgs e)
        {
            var rb = sender as RadioButton;
            if (rb == null || rb.Tag == null || PotatoSelectedInfo == null) return;
            selectedPotato = rb.Tag.ToString();

            switch (selectedPotato)
            {
                case "off":
                    PotatoSelectedInfo.Text = "Aus (Original-Grafik): Keine Grafik-Änderung im Spiel – nur der unsichtbare FPS- & Ping-Booster arbeitet im Hintergrund.";
                    break;
                case "stealth":
                    PotatoSelectedInfo.Text = "Dezent (+0,375): Scharfe Texturen & normale Hügel – schaltet nur Schatten, Gras, Bloom, AO & Nachbearbeitung für hohe FPS aus.";
                    break;
                case "clean":
                    PotatoSelectedInfo.Text = "Leicht (+1,0 · Clean): Glatte Potato-Texturen, 100% normale Berge & Klippen ohne Boden-Glitch.";
                    break;
                case "medium":
                    PotatoSelectedInfo.Text = "Mittel (+1,5 · EM Tweaks): Klassischer EM-Potato-Look ohne Berg-/Boden-Bug (setzt automatisch Texturen Hoch + Sichtweite Weit).";
                    break;
                case "strong":
                    PotatoSelectedInfo.Text = "Stark (+3,0 · Potato): Stark vereinfachte Play-Doh-Texturen (0x18 + MipBias 4) für maximale FPS.";
                    break;
                case "ultra":
                    PotatoSelectedInfo.Text = "Extrem (0x00000078 · Ultra Potato): Kompletter 1x1 Clay-/Plastik-Look auf Skins, Waffen & Builds wie im EM-Tweaks-Video (@Austvns)!";
                    break;
            }
        }

        // -------------------- Schritt-Steuerung --------------------

        void ShowStep(int step)
        {
            currentStep = step;
            PageStep1.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
            PageStep2.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
            PageStep3.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
            PageUninstall.Visibility = Visibility.Collapsed;

            StyleStepPill(StepPill1, StepNum1, StepLbl1, step == 1, step > 1);
            StyleStepPill(StepPill2, StepNum2, StepLbl2, step == 2, step > 2);
            StyleStepPill(StepPill3, StepNum3, StepLbl3, step == 3, installFinished);

            if (step == 1)
            {
                BackBtn.Visibility = Visibility.Collapsed;
                NextBtn.Content = "Weiter zu Custom Boost →";
            }
            else if (step == 2)
            {
                BackBtn.Visibility = Visibility.Visible;
                NextBtn.Content = "Jetzt installieren & anwenden";
            }
            else if (step == 3)
            {
                BackBtn.Visibility = Visibility.Collapsed;
            }
        }

        void StyleStepPill(Border pill, TextBlock num, TextBlock lbl, bool active, bool done)
        {
            var accent = (Brush)FindResource("Accent");
            var accentSoft = (Brush)FindResource("AccentSoft");
            var surface = (Brush)FindResource("Surface");
            var line = (Brush)FindResource("Line");
            var text = (Brush)FindResource("Text");
            var muted = (Brush)FindResource("Muted");

            if (active)
            {
                pill.Background = accentSoft;
                pill.BorderBrush = accent;
                num.Foreground = accent;
                lbl.Foreground = text;
            }
            else if (done)
            {
                pill.Background = surface;
                pill.BorderBrush = accent;
                num.Foreground = accent;
                lbl.Foreground = text;
            }
            else
            {
                pill.Background = surface;
                pill.BorderBrush = line;
                num.Foreground = muted;
                lbl.Foreground = muted;
            }
        }

        void BackBtn_Click(object sender, RoutedEventArgs e)
        {
            if (isInstalling) return;
            if (currentStep == 2) ShowStep(1);
        }

        async void NextBtn_Click(object sender, RoutedEventArgs e)
        {
            if (isInstalling) return;

            if (isUninstallMode)
            {
                if (installFinished)
                {
                    Close();
                    return;
                }
                await RunUninstallAsync();
                return;
            }

            if (currentStep == 1)
            {
                string dir = (InstallPathBox.Text ?? "").Trim();
                if (string.IsNullOrEmpty(dir))
                {
                    MessageBox.Show("Bitte gib einen gültigen Installationsordner an.", "Retrac Boost Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                ShowStep(2);
            }
            else if (currentStep == 2)
            {
                ShowStep(3);
                await RunInstallAsync();
            }
            else if (currentStep == 3 && installFinished)
            {
                if (ChkLaunchNow.IsChecked == true && !string.IsNullOrEmpty(installedExePath) && File.Exists(installedExePath))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(installedExePath)
                        {
                            UseShellExecute = true,
                            WorkingDirectory = Path.GetDirectoryName(installedExePath) ?? ""
                        });
                    }
                    catch { }
                }
                Close();
            }
        }

        void AddLog(string icon, string message)
        {
            logs.Add(new LogRow { Icon = icon, Message = message });
            LogScroller.ScrollToEnd();
        }

        void SetProgress(int percent, string stageText)
        {
            ProgressStageText.Text = stageText;
            ProgressPercentText.Text = percent + " %";
            double totalWidth = Math.Max(100, ActualWidth - 88);
            ProgressBarFill.Width = Math.Max(0, Math.Min(totalWidth, totalWidth * (percent / 100.0)));
        }

        async Task RunInstallAsync()
        {
            isInstalling = true;
            NextBtn.IsEnabled = false;
            NextBtn.Content = "Installiere …";
            logs.Clear();

            string installDir = InstallPathBox.Text.Trim();
            bool createDesktop = ChkDesktop.IsChecked == true;
            bool createStartMenu = ChkStartMenu.IsChecked == true;
            bool autoBoost = ChkAutoBoost.IsChecked == true;
            bool autoStart = ChkAutoStart.IsChecked == true;

            bool pingBoost = ChkPingBoost.IsChecked == true;
            bool fpsBoost = ChkFpsBoost.IsChecked == true;
            bool retracPack = ChkRetracPack.IsChecked == true;
            bool serverLock = ChkServerLock.IsChecked == true;
            string potatoMode = selectedPotato;

            try
            {
                SetProgress(10, "Prüfe laufende Prozesse …");
                AddLog("•", "Beende laufende Retrac Boost Instanzen (falls geöffnet) …");
                await Task.Run(() => InstallerEngine.StopRunningInstances());
                AddLog("✓", "Prozessprüfung abgeschlossen.");

                SetProgress(30, "Entpacke Programmdateien …");
                string exePath = Path.Combine(installDir, "RetracBoost.exe");
                string readmePath = Path.Combine(installDir, "README.md");
                string icoPath = Path.Combine(installDir, "app.ico");
                string uninstPath = Path.Combine(installDir, "Uninstall-RetracBoost.exe");

                await Task.Run(() =>
                {
                    Directory.CreateDirectory(installDir);
                    InstallerEngine.ExtractResource("Payload.RetracBoost.exe", exePath);
                    InstallerEngine.ExtractResource("Payload.README.md", readmePath);
                    InstallerEngine.ExtractResource("Payload.app.ico", icoPath);

                    string selfExe = Process.GetCurrentProcess().MainModule.FileName;
                    if (!string.Equals(Path.GetFullPath(selfExe), Path.GetFullPath(uninstPath), StringComparison.OrdinalIgnoreCase))
                        File.Copy(selfExe, uninstPath, true);
                });
                installedExePath = exePath;
                AddLog("✓", "RetracBoost.exe, Icon und Uninstaller nach „" + installDir + "“ installiert.");

                // Auch die RetracBoost.exe im Arbeitsordner aktualisieren, falls vorhanden
                try
                {
                    string localCopy = @"c:\Retrac tools\RetracBoost.exe";
                    if (Directory.Exists(@"c:\Retrac tools") && !string.Equals(Path.GetFullPath(exePath), Path.GetFullPath(localCopy), StringComparison.OrdinalIgnoreCase))
                        File.Copy(exePath, localCopy, true);
                }
                catch { }

                SetProgress(55, "Erstelle Verknüpfungen & System-Einträge …");
                await Task.Run(() =>
                {
                    if (createDesktop)
                    {
                        string deskLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Retrac Boost.lnk");
                        InstallerEngine.CreateShortcut(deskLnk, exePath, "", "Retrac Boost – Ping, FPS & Potato-Grafik Booster", icoPath);
                    }
                    if (createStartMenu)
                    {
                        string startDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Retrac Boost");
                        Directory.CreateDirectory(startDir);
                        InstallerEngine.CreateShortcut(Path.Combine(startDir, "Retrac Boost.lnk"), exePath, "", "Retrac Boost starten", icoPath);
                        InstallerEngine.CreateShortcut(Path.Combine(startDir, "Retrac Boost deinstallieren.lnk"), uninstPath, "--uninstall", "Retrac Boost deinstallieren", icoPath);
                        InstallerEngine.RegisterUninstaller(installDir, exePath, uninstPath);
                    }
                });
                if (createDesktop) AddLog("✓", "Desktop-Verknüpfung „Retrac Boost“ erstellt.");
                if (createStartMenu) AddLog("✓", "Startmenü-Eintrag & Windows-Uninstaller registriert.");

                SetProgress(80, "Wende dein Custom Boost- & Grafik-Setup an …");
                string presetArg = string.Format(CultureInfo.InvariantCulture,
                    "ping={0},fps={1},retrac={2},potato={3},serverlock={4},autoboost={5},autostart={6}",
                    pingBoost ? "1" : "0",
                    fpsBoost ? "1" : "0",
                    retracPack ? "1" : "0",
                    potatoMode,
                    serverLock ? "1" : "0",
                    autoBoost ? "1" : "0",
                    autoStart ? "1" : "0");

                AddLog("•", "Konfiguriere ausgewählte System-, Treiber- und Spiel-Tweaks …");
                await Task.Run(() =>
                {
                    var psi = new ProcessStartInfo(exePath, "--setup-preset \"" + presetArg + "\"")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = installDir
                    };
                    using (var p = Process.Start(psi))
                    {
                        if (p != null) p.WaitForExit(60000);
                    }
                });

                if (pingBoost) AddLog("✓", "Ping- & Netzwerk-Booster aktiviert (Nagle aus, AFD Fast-Datagram, QoS DSCP 46, Adapter-Tuning).");
                if (fpsBoost) AddLog("✓", "Unsichtbarer FPS-Booster aktiviert (NVIDIA Max Performance, Low Latency, Kernel-Priorität, HAGS, RAM-Clean).");
                if (retracPack) AddLog("✓", "Retrac / Fortnite Latenz-Paket aktiviert (FSO aus, Spielmodus an, 1:1 Maus, Engine-Latenz).");
                if (serverLock) AddLog("✓", "Bester Server-Lock aktiviert (302 Ausweich-IPs gesperrt – 100% Frankfurt eu-central-1 wie ExitLag).");
                if (potatoMode != "off")
                    AddLog("✓", "Potato-Grafik (Stufe: " + potatoMode.ToUpperInvariant() + ") inkl. Anti-Berg-Bug-Fix aktiviert.");
                else
                    AddLog("✓", "Original-Grafik beibehalten (Potato-Grafik deaktiviert).");

                SetProgress(100, "Installation erfolgreich abgeschlossen!");
                FinishCard.Visibility = Visibility.Visible;
                installFinished = true;
                StyleStepPill(StepPill3, StepNum3, StepLbl3, true, true);
                NextBtn.Content = "Fertigstellen";
                NextBtn.IsEnabled = true;
            }
            catch (Exception ex)
            {
                AddLog("✕", "Fehler: " + ex.Message);
                ProgressStageText.Text = "Fehler bei der Installation";
                NextBtn.Content = "Schließen";
                installFinished = true;
                NextBtn.IsEnabled = true;
            }
            finally
            {
                isInstalling = false;
            }
        }

        // -------------------- Uninstaller-Modus --------------------

        void EnterUninstallMode()
        {
            BadgeText.Text = "UNINSTALLER";
            HeaderSub.Text = "Saubere Deinstallation & Wiederherstellung aller Windows-/Grafik-Standards";
            StepBar.Visibility = Visibility.Collapsed;
            PageStep1.Visibility = Visibility.Collapsed;
            PageStep2.Visibility = Visibility.Collapsed;
            PageStep3.Visibility = Visibility.Collapsed;
            PageUninstall.Visibility = Visibility.Visible;

            UninstPathText.Text = InstallerEngine.DefaultInstallDir;
            BackBtn.Visibility = Visibility.Collapsed;
            NextBtn.Content = "Jetzt deinstallieren";
        }

        async Task RunUninstallAsync()
        {
            isInstalling = true;
            NextBtn.IsEnabled = false;
            NextBtn.Content = "Deinstalliere …";

            bool revertAll = ChkUninstRevert.IsChecked == true;
            bool purgeData = ChkUninstPurgeData.IsChecked == true;
            string installDir = InstallerEngine.DefaultInstallDir;

            PageUninstall.Visibility = Visibility.Collapsed;
            PageStep3.Visibility = Visibility.Visible;
            FinishCard.Visibility = Visibility.Collapsed;
            logs.Clear();

            try
            {
                SetProgress(15, "Beende laufende Prozesse …");
                await Task.Run(() => InstallerEngine.StopRunningInstances());
                AddLog("✓", "Laufende Retrac Boost Prozesse beendet.");

                string exePath = Path.Combine(installDir, "RetracBoost.exe");
                if (revertAll && File.Exists(exePath))
                {
                    SetProgress(45, "Setze alle Tweaks auf Windows-Standard zurück …");
                    AddLog("•", "Stelle Originalwerte in Registry, NVIDIA-Profil und Fortnite-INIs wieder her …");
                    await Task.Run(() =>
                    {
                        var psi = new ProcessStartInfo(exePath, "--revert-all")
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            WorkingDirectory = installDir
                        };
                        using (var p = Process.Start(psi))
                        {
                            if (p != null) p.WaitForExit(60000);
                        }
                    });
                    AddLog("✓", "Alle Tweaks wurden auf den ursprünglichen Zustand zurückgesetzt.");
                }

                SetProgress(75, "Entferne Verknüpfungen & Registry-Einträge …");
                await Task.Run(() =>
                {
                    InstallerEngine.RemoveShortcutsAndRegistry();
                    if (purgeData)
                    {
                        string progData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RetracBoost");
                        try { if (Directory.Exists(progData)) Directory.Delete(progData, true); } catch { }
                    }
                });
                AddLog("✓", "Desktop-, Startmenü- und Windows-Uninstaller-Einträge entfernt.");

                SetProgress(95, "Entferne Programmdateien …");
                await Task.Run(() =>
                {
                    if (Directory.Exists(installDir))
                    {
                        foreach (var f in Directory.GetFiles(installDir))
                        {
                            try { File.Delete(f); } catch { }
                        }
                        try { Directory.Delete(installDir, true); } catch { }
                    }
                });

                // Falls der Uninstaller selbst aus installDir läuft, Löschung nach Prozessende planen
                if (Directory.Exists(installDir))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 2 > nul & rmdir /s /q \"" + installDir + "\"")
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true
                        });
                    }
                    catch { }
                }

                SetProgress(100, "Retrac Boost wurde vollständig entfernt.");
                AddLog("✓", "Deinstallation erfolgreich abgeschlossen.");
                installFinished = true;
                NextBtn.Content = "Schließen";
                NextBtn.IsEnabled = true;
            }
            catch (Exception ex)
            {
                AddLog("✕", "Fehler: " + ex.Message);
                installFinished = true;
                NextBtn.Content = "Schließen";
                NextBtn.IsEnabled = true;
            }
            finally
            {
                isInstalling = false;
            }
        }
    }
}
