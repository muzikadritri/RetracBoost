using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows;

namespace RetracBoost
{
    static class Admin
    {
        public static bool IsElevated
        {
            get
            {
                using (var id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
        }
    }

    static class Program
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr h);

        [STAThread]
        static int Main(string[] args)
        {
            bool selfTest = args.Contains("--selftest");
            bool revertAll = args.Contains("--revert-all");
            int setupIdx = Array.IndexOf(args, "--setup-preset");
            bool noElevate = args.Contains("--noelevate");
            int screens = Array.IndexOf(args, "--screens");
            bool needsAdmin = !noElevate && screens < 0 && (!selfTest || args.Contains("--apply") || args.Contains("--server") || args.Contains("--repair"));

            if (needsAdmin && !Admin.IsElevated)
            {
                try
                {
                    var psi = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, string.Join(" ", args.Select(a => "\"" + a + "\"")))
                    {
                        UseShellExecute = true,
                        Verb = "runas"
                    };
                    Process.Start(psi);
                    return 0;
                }
                catch
                {
                    MessageBox.Show(
                        L.T("Retrac Boost braucht Administratorrechte, um Netzwerkeinstellungen zu ändern.",
                            "Retrac Boost needs administrator rights to change network settings."),
                        "Retrac Boost", MessageBoxButton.OK, MessageBoxImage.Information);
                    return 1;
                }
            }

            if (screens >= 0 && screens + 1 < args.Length) return ScreenShots.Run(args[screens + 1]);
            if (selfTest) return SelfTest.Run(args);
            if (revertAll) return RunRevertAll();
            if (setupIdx >= 0 && setupIdx + 1 < args.Length) return RunSetupPreset(args[setupIdx + 1]);

            bool created;
            using (var mutex = new Mutex(true, "RetracBoost.SingleInstance", out created))
            {
                // Nach einem Update läuft die alte Version noch kurz - auf sie warten statt abzubrechen
                if (!created && args.Contains(AutoUpdater.UpdatedArg))
                {
                    try { created = mutex.WaitOne(TimeSpan.FromSeconds(15)); }
                    catch (AbandonedMutexException) { created = true; }
                }

                if (!created)
                {
                    var h = FindWindow(null, "Retrac Boost");
                    if (h != IntPtr.Zero) { ShowWindow(h, 9); SetForegroundWindow(h); }
                    return 0;
                }

                AutoUpdater.CleanupAfterUpdate();

                var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                app.DispatcherUnhandledException += (s, e) =>
                {
                    MessageBox.Show(e.Exception.Message, "Retrac Boost", MessageBoxButton.OK, MessageBoxImage.Warning);
                    e.Handled = true;
                };
                return app.Run(new MainWindow());
            }
        }

        static List<Tweak> AllSystemTweaks()
        {
            var dvr = new RegValueTweak("dvr", "Game DVR", "",
                () => new List<RegItem>
                {
                    RegItem.Dw(@"System\GameConfigStore", "GameDVR_Enabled", 0, 1, null, true),
                    RegItem.Dw(@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, 1, null, true),
                    RegItem.Dw(@"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, null, null, true)
                });
            var ogfn = new GameTweak("ogfn", "RetracBoost-OGFN", "") { Exe = "FortniteClient-Win64-Shipping.exe" };
            var fso = new FsoTweak { ExePath = Store.Get("ogfn.path") };
            var game = new GameTweak { Exe = Store.Get("game") };
            return new List<Tweak>
            {
                new NetStackTweak(),
                new AdapterTweak(),
                new PowerTweak(),
                new FpsBoostTweak(),
                game,
                new DnsTweak(),
                new TcpTuneTweak(),
                new BackgroundTweak(),
                new AutoStartTweak(),
                new MtuTweak(),
                ogfn,
                new ServerLockTweak(),
                new PotatoTweak(),
                fso,
                dvr,
                new MouseAccelTweak(),
                new EngineLatencyTweak()
            };
        }

        static int RunRevertAll()
        {
            foreach (var t in AllSystemTweaks())
            {
                try
                {
                    if (t.WeApplied || t is AutoStartTweak && t.IsApplied())
                        t.Revert();
                }
                catch { }
            }
            return 0;
        }

        static int RunSetupPreset(string spec)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in (spec ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq > 0) map[part.Substring(0, eq).Trim()] = part.Substring(eq + 1).Trim();
            }

            Func<string, bool> isOn = k =>
            {
                string v;
                return map.TryGetValue(k, out v) && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));
            };

            if (map.ContainsKey("autoboost"))
                Store.TrySet("setting.autoboost", isOn("autoboost") ? "1" : "0");

            if (map.ContainsKey("autostart"))
            {
                try
                {
                    var a = new AutoStartTweak();
                    if (isOn("autostart")) a.Apply();
                    else if (a.IsApplied()) a.Revert();
                }
                catch { }
            }

            if (isOn("ping"))
            {
                foreach (var t in new Tweak[] { new NetStackTweak(), new AdapterTweak(), new TcpTuneTweak() })
                {
                    try { if (t.Relevant && !t.IsApplied()) t.Apply(); } catch { }
                }
            }

            if (isOn("fps"))
            {
                foreach (var t in new Tweak[] { new PowerTweak(), new FpsBoostTweak() })
                {
                    try { if (t.Relevant && !t.IsApplied()) t.Apply(); } catch { }
                }
                try { RamCleaner.PurgeStandbyList(); } catch { }
            }

            if (isOn("retrac"))
            {
                Store.TrySet("game", "FortniteClient-Win64-Shipping.exe");
                var dvr = new RegValueTweak("dvr", "Game DVR", "",
                    () => new List<RegItem>
                    {
                        RegItem.Dw(@"System\GameConfigStore", "GameDVR_Enabled", 0, 1, null, true),
                        RegItem.Dw(@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, 1, null, true),
                        RegItem.Dw(@"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, null, null, true)
                    });
                var ogfn = new GameTweak("ogfn", "RetracBoost-OGFN", "") { Exe = "FortniteClient-Win64-Shipping.exe" };
                var fso = new FsoTweak { ExePath = Store.Get("ogfn.path") };
                foreach (var t in new Tweak[] { ogfn, fso, dvr, new MouseAccelTweak(), new EngineLatencyTweak() })
                {
                    try { if (t.Relevant && !t.IsApplied()) t.Apply(); } catch { }
                }
            }

            string potatoVal;
            if (map.TryGetValue("potato", out potatoVal) && !string.IsNullOrEmpty(potatoVal))
            {
                try
                {
                    var pot = new PotatoTweak();
                    if (potatoVal.Equals("off", StringComparison.OrdinalIgnoreCase) || potatoVal == "0")
                    {
                        if (pot.WeApplied || pot.IsApplied()) pot.Revert();
                    }
                    else
                    {
                        pot.PresetKey = potatoVal;
                        Store.TrySet("potato.preset", potatoVal);
                        if (pot.Relevant) pot.Apply();
                    }
                }
                catch { }
            }

            if (map.ContainsKey("serverlock"))
            {
                try
                {
                    var sl = new ServerLockTweak();
                    if (isOn("serverlock"))
                    {
                        if (!sl.IsApplied()) sl.Apply();
                    }
                    else
                    {
                        if (sl.IsApplied()) sl.Revert();
                    }
                }
                catch { }
            }

            return 0;
        }
    }
}
