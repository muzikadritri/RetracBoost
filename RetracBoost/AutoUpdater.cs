using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace RetracBoost
{
    public sealed class UpdateInfo
    {
        public bool HasUpdate { get; set; }
        public string CurrentVersion { get; set; }
        public string LatestVersion { get; set; }
        public string Changelog { get; set; }
        public string DownloadUrl { get; set; }
        public string StatusMessage { get; set; }
    }

    public static class AutoUpdater
    {
        public const string CurrentVersion = "1.1.0";

        public const string DefaultGitHubRepo = "muzikadritri/RetracBoost";
        const string LocalManifestPath = @"C:\Retrac tools\update_manifest.json";

        public static string ResolveGitHubRepo()
        {
            try
            {
                string stored = Store.Get("github.repo");
                if (!string.IsNullOrWhiteSpace(stored)) return NormalizeRepo(stored);

                string appDir = AppDomain.CurrentDomain.BaseDirectory;
                string[] files =
                {
                    Path.Combine(appDir, "github_repo.txt"),
                    @"C:\Retrac tools\github_repo.txt",
                    Path.Combine(appDir, "updater_url.txt")
                };

                foreach (var f in files)
                {
                    if (File.Exists(f))
                    {
                        string line = File.ReadAllText(f).Trim();
                        if (!string.IsNullOrEmpty(line))
                            return NormalizeRepo(line);
                    }
                }
            }
            catch { }
            return DefaultGitHubRepo;
        }

        public static void SaveGitHubRepo(string repo)
        {
            try
            {
                string norm = NormalizeRepo(repo);
                Store.TrySet("github.repo", norm);
                string appDir = AppDomain.CurrentDomain.BaseDirectory;
                File.WriteAllText(Path.Combine(appDir, "github_repo.txt"), norm);
                try { File.WriteAllText(@"C:\Retrac tools\github_repo.txt", norm); } catch { }
            }
            catch { }
        }

        public static string NormalizeRepo(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return DefaultGitHubRepo;
            input = input.Trim();
            input = Regex.Replace(input, @"^https?://github\.com/", "", RegexOptions.IgnoreCase);
            input = Regex.Replace(input, @"^github\.com/", "", RegexOptions.IgnoreCase);
            input = Regex.Replace(input, @"/releases.*$", "", RegexOptions.IgnoreCase);
            input = Regex.Replace(input, @"\.git$", "", RegexOptions.IgnoreCase);
            input = input.Trim('/');
            return string.IsNullOrEmpty(input) ? DefaultGitHubRepo : input;
        }

        public static async Task<UpdateInfo> CheckForUpdatesAsync(string customRepo = null)
        {
            var info = new UpdateInfo
            {
                CurrentVersion = CurrentVersion,
                LatestVersion = CurrentVersion,
                HasUpdate = false,
                Changelog = "Keine neuen Updates verfügbar. Dein Retrac Boost ist auf dem neuesten Stand.",
                StatusMessage = "Aktuell"
            };

            await Task.Run(() =>
            {
                try
                {
                    try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }

                    string repo = NormalizeRepo(string.IsNullOrEmpty(customRepo) ? ResolveGitHubRepo() : customRepo);

                    // 1) Versuche direkte GitHub Releases API Abfrage
                    string apiUrl = "https://api.github.com/repos/" + repo + "/releases/latest";
                    string json = null;
                    try
                    {
                        var req = (HttpWebRequest)WebRequest.Create(apiUrl);
                        req.UserAgent = "RetracBoost-AutoUpdater/" + CurrentVersion;
                        req.Timeout = 5000;
                        req.Accept = "application/vnd.github.v3+json";
                        using (var resp = req.GetResponse())
                        using (var sr = new StreamReader(resp.GetResponseStream()))
                        {
                            json = sr.ReadToEnd();
                        }
                    }
                    catch { }

                    if (!string.IsNullOrEmpty(json))
                    {
                        var mTag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
                        if (mTag.Success)
                        {
                            string remoteVer = mTag.Groups[1].Value.Trim().TrimStart('v', 'V');
                            info.LatestVersion = remoteVer;

                            // Suche bevorzugt RetracBoost.exe (oder falls nicht vorhanden andere EXE / Setup)
                            var mAsset = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+?/RetracBoost\\.exe)\"", RegexOptions.IgnoreCase);
                            if (!mAsset.Success)
                                mAsset = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+?\\.exe)\"", RegexOptions.IgnoreCase);

                            if (mAsset.Success)
                                info.DownloadUrl = mAsset.Groups[1].Value;
                            else
                                info.DownloadUrl = "https://github.com/" + repo + "/releases/download/" + mTag.Groups[1].Value + "/RetracBoost.exe";

                            // Changelog aus body
                            var mBody = Regex.Match(json, "\"body\"\\s*:\\s*\"(.*?)(?<!\\\\)\"", RegexOptions.Singleline);
                            if (mBody.Success)
                            {
                                string body = mBody.Groups[1].Value
                                    .Replace("\\r\\n", "\n")
                                    .Replace("\\n", "\n")
                                    .Replace("\\\"", "\"")
                                    .Replace("\\\\", "\\");
                                if (!string.IsNullOrWhiteSpace(body))
                                    info.Changelog = body;
                            }

                            if (IsNewer(remoteVer, CurrentVersion))
                            {
                                info.HasUpdate = true;
                                info.StatusMessage = "Update verfügbar: v" + remoteVer;
                            }
                            else
                            {
                                info.StatusMessage = "Auf neuestem Stand (v" + CurrentVersion + ")";
                            }
                            return;
                        }
                    }

                    // 2) Fallback: Direkter GitHub Redirect-Check (ohne API-Rate-Limits)
                    try
                    {
                        string redirectUrl = "https://github.com/" + repo + "/releases/latest";
                        var req = (HttpWebRequest)WebRequest.Create(redirectUrl);
                        req.UserAgent = "Mozilla/5.0";
                        req.AllowAutoRedirect = false;
                        req.Timeout = 5000;
                        using (var resp = (HttpWebResponse)req.GetResponse())
                        {
                            string loc = resp.Headers["Location"];
                            if (!string.IsNullOrEmpty(loc))
                            {
                                int idx = loc.LastIndexOf('/');
                                if (idx > 0)
                                {
                                    string tag = loc.Substring(idx + 1).Trim();
                                    string remoteVer = tag.TrimStart('v', 'V');
                                    info.LatestVersion = remoteVer;
                                    info.DownloadUrl = "https://github.com/" + repo + "/releases/latest/download/RetracBoost.exe";
                                    info.Changelog = "Neues Release auf GitHub: " + tag;

                                    if (IsNewer(remoteVer, CurrentVersion))
                                    {
                                        info.HasUpdate = true;
                                        info.StatusMessage = "Update verfügbar: v" + remoteVer;
                                        return;
                                    }
                                }
                            }
                        }
                    }
                    catch { }

                    // 3) Fallback: Lokale Datei version.json (für Offline-/Testbetrieb)
                    string appDir = AppDomain.CurrentDomain.BaseDirectory;
                    string[] localFiles =
                    {
                        Path.Combine(appDir, "version.json"),
                        @"C:\Retrac tools\version.json"
                    };
                    foreach (var lf in localFiles)
                    {
                        if (File.Exists(lf))
                        {
                            try
                            {
                                string content = File.ReadAllText(lf);
                                var mVer = Regex.Match(content, "\"version\"\\s*:\\s*\"([^\"]+)\"");
                                var mUrl = Regex.Match(content, "\"url\"\\s*:\\s*\"([^\"]+)\"");
                                var mLog = Regex.Match(content, "\"changelog\"\\s*:\\s*\"([^\"]+)\"");
                                if (mVer.Success)
                                {
                                    string remoteVer = mVer.Groups[1].Value.Trim().TrimStart('v', 'V');
                                    info.LatestVersion = remoteVer;
                                    if (mUrl.Success) info.DownloadUrl = mUrl.Groups[1].Value;
                                    if (mLog.Success) info.Changelog = mLog.Groups[1].Value.Replace("\\n", "\n");
                                    if (IsNewer(remoteVer, CurrentVersion))
                                    {
                                        info.HasUpdate = true;
                                        info.StatusMessage = "Update verfügbar: v" + remoteVer;
                                    }
                                    return;
                                }
                            }
                            catch { }
                        }
                    }

                    info.StatusMessage = "v" + CurrentVersion + " (Aktuell)";
                }
                catch (Exception ex)
                {
                    info.StatusMessage = "v" + CurrentVersion;
                    info.Changelog = "GitHub: " + ex.Message;
                }
            });

            return info;
        }

        static bool IsNewer(string remote, string current)
        {
            try
            {
                var r = new Version(remote);
                var c = new Version(current);
                return r > c;
            }
            catch
            {
                return string.Compare(remote, current, StringComparison.OrdinalIgnoreCase) > 0;
            }
        }

        public static async Task<string> DownloadUpdateAsync(string url, Action<int> progress)
        {
            if (string.IsNullOrEmpty(url)) throw new ArgumentException("Download-URL fehlt.");

            string tempExe = Path.Combine(Path.GetTempPath(), "RetracBoost-Update-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
            using (var wc = new WebClient())
            {
                wc.Headers.Add("User-Agent", "RetracBoost-AutoUpdater");
                wc.DownloadProgressChanged += (s, e) =>
                {
                    if (progress != null) progress(e.ProgressPercentage);
                };

                await wc.DownloadFileTaskAsync(new Uri(url), tempExe);
            }

            if (!File.Exists(tempExe) || new FileInfo(tempExe).Length < 10000)
                throw new InvalidOperationException("Heruntergeladene Update-Datei ist unvollständig.");

            return tempExe;
        }

        public static void ApplyUpdateAndRestart(string newExePath)
        {
            string currentExe = Process.GetCurrentProcess().MainModule.FileName;
            string currentDir = Path.GetDirectoryName(currentExe);

            // Falls die Datei ein Setup-Installer ist (z.B. RetracBoost-Setup.exe), Installer starten
            if (Path.GetFileName(newExePath).IndexOf("setup", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(newExePath) { UseShellExecute = true });
                    Environment.Exit(0);
                    return;
                }
                catch { }
            }

            string scriptPath = Path.Combine(Path.GetTempPath(), "retracboost_updater.bat");

            // Batch-Skript: Wartet 1 s bis die aktuelle App schließt, überschreibt die EXE und startet sie neu
            string script = string.Format(
                "@echo off\r\n" +
                "timeout /t 1 /nobreak >nul\r\n" +
                "copy /y \"{0}\" \"{1}\" >nul\r\n" +
                "del \"{0}\" >nul 2>&1\r\n" +
                "start \"\" \"{1}\"\r\n" +
                "del \"%~f0\" >nul 2>&1\r\n",
                newExePath, currentExe);

            File.WriteAllText(scriptPath, script);

            var psi = new ProcessStartInfo("cmd.exe", "/c \"" + scriptPath + "\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = currentDir
            };

            Process.Start(psi);
            Environment.Exit(0);
        }
    }
}
