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
        // true, wenn weder GitHub noch eine lokale version.json erreichbar war
        public bool CheckFailed { get; set; }
    }

    public static class AutoUpdater
    {
        public const string CurrentVersion = "1.0.10";

        // Fest eingebaut. Gespeicherte Werte aus alten Versionen (state.ini, github_repo.txt) werden ignoriert und aufgeräumt.
        public const string GitHubRepo = "muzikadritri/RetracBoost";
        public const string ReleasesUrl = "https://github.com/" + GitHubRepo + "/releases";
        const string LocalManifestPath = @"C:\Retrac tools\update_manifest.json";

        public static string ResolveGitHubRepo()
        {
            return GitHubRepo;
        }

        /// <summary>Entfernt Repo-Angaben, die ältere Versionen gespeichert haben (z. B. ein falscher Besitzer).</summary>
        public static void ForgetStoredRepo()
        {
            try { if (Store.Get("github.repo") != null) Store.Set("github.repo", null); } catch { }

            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] files =
            {
                Path.Combine(appDir, "github_repo.txt"),
                @"C:\Retrac tools\github_repo.txt",
                Path.Combine(appDir, "updater_url.txt")
            };
            foreach (var f in files)
            {
                try
                {
                    if (!File.Exists(f)) continue;
                    if (File.ReadAllText(f).Trim() != GitHubRepo) File.WriteAllText(f, GitHubRepo);
                }
                catch { }
            }
        }

        static bool IsOwnReleaseUrl(string url)
        {
            return !string.IsNullOrEmpty(url) &&
                   url.StartsWith("https://github.com/" + GitHubRepo + "/releases/", StringComparison.OrdinalIgnoreCase);
        }

        public static async Task<UpdateInfo> CheckForUpdatesAsync()
        {
            var info = new UpdateInfo
            {
                CurrentVersion = CurrentVersion,
                LatestVersion = CurrentVersion,
                HasUpdate = false,
                Changelog = L.T("Kein Update verfügbar.", "No update available."),
                StatusMessage = L.T("Aktuell", "Up to date")
            };

            await Task.Run(() =>
            {
                try
                {
                    try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }

                    string repo = GitHubRepo;

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
                                // Markdown-Zeichen entfernen - das Fenster zeigt nur reinen Text
                                body = Regex.Replace(body, @"^#+\s*", "", RegexOptions.Multiline).Replace("**", "").Replace("`", "").Trim();
                                if (!string.IsNullOrWhiteSpace(body))
                                    info.Changelog = body;
                            }

                            if (IsNewer(remoteVer, CurrentVersion))
                            {
                                info.HasUpdate = true;
                                info.StatusMessage = L.T("Update verfügbar: v", "Update available: v") + remoteVer;
                            }
                            else
                            {
                                info.StatusMessage = L.T("Auf neuestem Stand (v", "Up to date (v") + CurrentVersion + ")";
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
                                    info.Changelog = L.T("Neue Version: ", "New version: ") + tag;

                                    if (IsNewer(remoteVer, CurrentVersion))
                                    {
                                        info.HasUpdate = true;
                                        info.StatusMessage = L.T("Update verfügbar: v", "Update available: v") + remoteVer;
                                    }
                                    else
                                    {
                                        info.Changelog = L.T("Kein Update verfügbar.", "No update available.");
                                    }
                                    // GitHub hat geantwortet - die lokale version.json ist dann nicht mehr gefragt
                                    return;
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
                                    info.DownloadUrl = mUrl.Success && IsOwnReleaseUrl(mUrl.Groups[1].Value)
                                        ? mUrl.Groups[1].Value
                                        : ReleasesUrl + "/download/v" + remoteVer + "/RetracBoost.exe";
                                    if (mLog.Success) info.Changelog = mLog.Groups[1].Value.Replace("\\n", "\n");
                                    if (IsNewer(remoteVer, CurrentVersion))
                                    {
                                        info.HasUpdate = true;
                                        info.StatusMessage = L.T("Update verfügbar: v", "Update available: v") + remoteVer;
                                    }
                                    return;
                                }
                            }
                            catch { }
                        }
                    }

                    // Nichts erreicht: nicht so tun, als wäre alles aktuell
                    info.CheckFailed = true;
                    info.StatusMessage = "v" + CurrentVersion;
                    info.Changelog = L.T("GitHub ist gerade nicht erreichbar. Prüf die Internetverbindung und versuch es nochmal.",
                                         "GitHub can't be reached right now. Check your internet connection and try again.");
                }
                catch (Exception ex)
                {
                    info.CheckFailed = true;
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
            if (string.IsNullOrEmpty(url)) throw new ArgumentException(L.T("Download-URL fehlt.", "Download URL is missing."));

            // Eigener Ordner, Dateiname wie im Release (z. B. RetracBoost-Setup.exe),
            // damit ApplyUpdateAndRestart ein Setup am Namen erkennt
            string fileName = Path.GetFileName(new Uri(url).LocalPath);
            if (string.IsNullOrEmpty(fileName) || !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                fileName = "RetracBoost.exe";
            string tempDir = Path.Combine(Path.GetTempPath(), "RetracBoost-Update-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tempDir);
            string tempExe = Path.Combine(tempDir, fileName);

            using (var wc = new WebClient())
            {
                wc.Headers.Add("User-Agent", "RetracBoost-AutoUpdater/" + CurrentVersion);
                wc.DownloadProgressChanged += (s, e) =>
                {
                    if (progress != null) progress(e.ProgressPercentage);
                };

                await wc.DownloadFileTaskAsync(new Uri(url), tempExe);
            }

            if (!File.Exists(tempExe) || new FileInfo(tempExe).Length < 10000)
                throw new InvalidOperationException(L.T("Heruntergeladene Update-Datei ist unvollständig.", "The downloaded update file is incomplete."));

            // Nicht dieselbe (oder eine ältere) Version über sich selbst kopieren
            Version downloaded = ReadFileVersion(tempExe);
            Version current;
            if (downloaded != null && Version.TryParse(CurrentVersion, out current) && Normalize(downloaded) <= Normalize(current))
            {
                TryDeleteDirectory(tempDir);
                throw new InvalidOperationException(string.Format(
                    L.T("Die heruntergeladene Datei ist Version {0}, installiert ist {1}. Im Release liegt keine neuere Datei.",
                        "The downloaded file is version {0}, installed is {1}. The release doesn't contain a newer file."),
                    Normalize(downloaded).ToString(3), CurrentVersion));
            }

            return tempExe;
        }

        static Version ReadFileVersion(string path)
        {
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(path);
                if (vi.FileMajorPart == 0 && vi.FileMinorPart == 0 && vi.FileBuildPart == 0) return null;
                return new Version(vi.FileMajorPart, vi.FileMinorPart, vi.FileBuildPart);
            }
            catch { return null; }
        }

        static Version Normalize(Version v)
        {
            return new Version(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));
        }

        static void TryDeleteDirectory(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }

        public const string UpdatedArg = "--updated";
        const string BackupSuffix = ".old";

        public static void ApplyUpdateAndRestart(string newExePath)
        {
            string currentExe = Process.GetCurrentProcess().MainModule.FileName;
            string currentDir = Path.GetDirectoryName(currentExe);

            // Falls die Datei ein Setup-Installer ist (z.B. RetracBoost-Setup.exe), Installer starten
            if (Path.GetFileName(newExePath).IndexOf("setup", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Process.Start(new ProcessStartInfo(newExePath) { UseShellExecute = true });
                Environment.Exit(0);
                return;
            }

            // Eine laufende EXE lässt sich unter Windows nicht überschreiben, aber umbenennen.
            // Darum: laufende Datei zur Seite schieben, neue an ihren Platz, neu starten.
            // Das alte Batch-Skript hat kopiert, während die App noch lief - die Kopie schlug
            // still fehl und die alte Version startete wieder.
            string backup = currentExe + BackupSuffix;
            try { if (File.Exists(backup)) File.Delete(backup); } catch { }
            if (File.Exists(backup))
                backup = currentExe + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + BackupSuffix;

            try
            {
                File.Move(currentExe, backup);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(string.Format(
                    L.T("Retrac Boost kann sich in {0} nicht ersetzen ({1}). Starte es als Administrator und versuch es nochmal.",
                        "Retrac Boost can't replace itself in {0} ({1}). Run it as administrator and try again."),
                    currentDir, ex.Message), ex);
            }

            try
            {
                File.Copy(newExePath, currentExe, false);
            }
            catch
            {
                // Alte Datei zurück, damit die App weiter startet
                try { File.Move(backup, currentExe); } catch { }
                throw;
            }

            TryDeleteDirectory(Path.GetDirectoryName(newExePath));

            Process.Start(new ProcessStartInfo(currentExe, UpdatedArg)
            {
                UseShellExecute = false,
                WorkingDirectory = currentDir
            });
            Environment.Exit(0);
        }

        // Nach einem Update: die zur Seite geschobene alte EXE entfernen.
        // Der alte Prozess beendet sich evtl. gerade noch, darum ein paar Versuche.
        public static void CleanupAfterUpdate()
        {
            Task.Run(async () =>
            {
                try
                {
                    string currentExe = Process.GetCurrentProcess().MainModule.FileName;
                    string dir = Path.GetDirectoryName(currentExe);
                    string pattern = Path.GetFileName(currentExe) + "*" + BackupSuffix;
                    for (int attempt = 0; attempt < 10; attempt++)
                    {
                        string[] left = Directory.GetFiles(dir, pattern);
                        if (left.Length == 0) return;
                        foreach (var f in left)
                        {
                            try { File.Delete(f); } catch { }
                        }
                        await Task.Delay(1000);
                    }
                }
                catch { }
            });
        }
    }
}
