using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Windows;

namespace RetracBoostSetup
{
    static class AdminCheck
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
        [STAThread]
        static int Main(string[] args)
        {
            int selfTestIdx = Array.IndexOf(args, "--selftest-setup");
            if (selfTestIdx >= 0 && selfTestIdx + 1 < args.Length)
                return RunSelfTest(args[selfTestIdx + 1]);

            bool noElevate = args.Contains("--noelevate");
            if (!noElevate && !AdminCheck.IsElevated)
            {
                try
                {
                    var psi = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName,
                        string.Join(" ", args.Select(a => "\"" + a + "\"")))
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
                        "Das Retrac Boost Setup benötigt Administratorrechte für die Installation und die System-Tweaks.",
                        "Retrac Boost Setup", MessageBoxButton.OK, MessageBoxImage.Information);
                    return 1;
                }
            }

            bool uninstall = args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)
                                        || a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase));

            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.DispatcherUnhandledException += (s, e) =>
            {
                MessageBox.Show(e.Exception.Message, "Retrac Boost Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
                e.Handled = true;
            };
            return app.Run(new SetupWindow(uninstall));
        }

        static int RunSelfTest(string targetDir)
        {
            try
            {
                Directory.CreateDirectory(targetDir);
                InstallerEngine.ExtractResource("Payload.RetracBoost.exe", Path.Combine(targetDir, "RetracBoost.exe"));
                InstallerEngine.ExtractResource("Payload.README.md", Path.Combine(targetDir, "README.md"));
                InstallerEngine.ExtractResource("Payload.app.ico", Path.Combine(targetDir, "app.ico"));
                string lnkPath = Path.Combine(targetDir, "Retrac Boost Test.lnk");
                InstallerEngine.CreateShortcut(lnkPath, Path.Combine(targetDir, "RetracBoost.exe"), "", "Retrac Boost", Path.Combine(targetDir, "app.ico"));

                bool ok = File.Exists(Path.Combine(targetDir, "RetracBoost.exe"))
                       && new FileInfo(Path.Combine(targetDir, "RetracBoost.exe")).Length > 100000
                       && File.Exists(Path.Combine(targetDir, "README.md"))
                       && File.Exists(Path.Combine(targetDir, "app.ico"))
                       && File.Exists(lnkPath);

                File.WriteAllText(Path.Combine(targetDir, "selftest-result.txt"), ok ? "PASS" : "FAIL");
                return ok ? 0 : 1;
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(Path.Combine(targetDir, "selftest-result.txt"), "FAIL: " + ex.Message); } catch { }
                return 1;
            }
        }
    }
}
