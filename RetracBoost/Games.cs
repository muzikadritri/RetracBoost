using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace RetracBoost
{
    // ------------------------------------------------------------------------------------------
    // Games-Tab: Spiele, ihre Optimierungen und wo sie auf dem PC liegen.
    // Alles wirkt von außen (Registry, Konfigurationsdateien, Startoptionen). Es wird nichts in ein
    // Spiel geladen und kein Handle auf einen Spielprozess geöffnet, damit Anti-Cheat nichts zu melden hat.
    // Eingebaute Spiele und Plugins benutzen dieselben Klassen (siehe GamePlugins.cs).
    // ------------------------------------------------------------------------------------------

    enum GameActionKind { Priority, IoPriority, NetworkQos, GpuHighPerformance, FullscreenOptimizationsOff, Config, LaunchOptions }

    enum ConfigFormat { Ini, KeyValues, Colon, Xml }

    sealed class ConfigEntry
    {
        public string Section, Key, Value;
        public ConfigEntry(string section, string key, string value) { Section = section; Key = key; Value = value; }
    }

    /// <summary>Eine Optimierung eines Spiels, so wie sie im Katalog oder in einem Plugin steht.</summary>
    sealed class GameAction
    {
        public GameActionKind Kind;
        public string Key;                 // eindeutig innerhalb des Spiels, Teil der Sicherungs-IDs
        public string Title, Summary;      // optional, sonst Standardtext
        public string Level = "high";      // Priority: high | abovenormal
        public string File;                // Config: Pfadmuster
        public ConfigFormat Format;
        public List<ConfigEntry> Entries = new List<ConfigEntry>();
        public string Launcher;            // LaunchOptions: steam | epic | ea | riot | other
        public string Value;               // LaunchOptions: Text zum Eintragen

        public static GameAction Priority(string level = "high")
        {
            return new GameAction { Kind = GameActionKind.Priority, Key = "prio", Level = level };
        }

        public static GameAction Io() { return new GameAction { Kind = GameActionKind.IoPriority, Key = "io" }; }

        public static GameAction Qos() { return new GameAction { Kind = GameActionKind.NetworkQos, Key = "qos" }; }

        public static GameAction Gpu() { return new GameAction { Kind = GameActionKind.GpuHighPerformance, Key = "gpu" }; }

        public static GameAction Fso() { return new GameAction { Kind = GameActionKind.FullscreenOptimizationsOff, Key = "fso" }; }

        public static GameAction Config(string key, string title, string summary, string file, ConfigFormat format, params ConfigEntry[] entries)
        {
            return new GameAction { Kind = GameActionKind.Config, Key = key, Title = title, Summary = summary, File = file, Format = format, Entries = entries.ToList() };
        }

        public static GameAction Launch(string launcher, string value, string summary)
        {
            return new GameAction { Kind = GameActionKind.LaunchOptions, Key = "launch", Launcher = launcher, Value = value, Summary = summary };
        }

        public static string FormatName(ConfigFormat f)
        {
            switch (f)
            {
                case ConfigFormat.KeyValues: return "kv";
                case ConfigFormat.Colon: return "colon";
                case ConfigFormat.Xml: return "xml";
                default: return "ini";
            }
        }

        public static string LevelText(string level)
        {
            return level == "abovenormal" ? L.T("höher als normal", "above normal") : L.T("hoch", "high");
        }

        public static string LauncherName(string launcher)
        {
            switch (launcher)
            {
                case "steam": return "Steam";
                case "epic": return "Epic Games Launcher";
                case "ea": return "EA app";
                case "riot": return "Riot Client";
                default: return L.T("Launcher", "launcher");
            }
        }

        public static string LauncherHint(string launcher)
        {
            switch (launcher)
            {
                case "steam": return L.T("Steam → Rechtsklick auf das Spiel → Eigenschaften → Startoptionen", "Steam → right-click the game → Properties → Launch options");
                case "epic": return L.T("Epic Games Launcher → Einstellungen → Spiel aufklappen → Zusätzliche Befehlszeilenargumente", "Epic Games Launcher → Settings → expand the game → Additional command line arguments");
                case "ea": return L.T("EA app → Bibliothek → Spiel → Verwalten → Eigenschaften → Erweiterte Startoptionen", "EA app → Library → game → Manage → Properties → Advanced launch options");
                default: return L.T("In den Startoptionen des Launchers eintragen", "Enter it in the launcher's launch options");
            }
        }

        /// <summary>Ein Satz, was genau geändert wird. Wird vor der Plugin-Installation angezeigt.</summary>
        public string Describe(GameDef g)
        {
            string exes = g.Exes.Length == 0 ? "–" : string.Join(", ", g.Exes);
            switch (Kind)
            {
                case GameActionKind.Priority:
                    return L.T("CPU-Priorität " + LevelText(Level) + " für " + exes + " (Registry, Image File Execution Options)",
                               "CPU priority " + LevelText(Level) + " for " + exes + " (registry, Image File Execution Options)");
                case GameActionKind.IoPriority:
                    return L.T("Datenträger- und Arbeitsspeicher-Priorität hoch für " + exes + " (Registry, Image File Execution Options)",
                               "High disk and memory priority for " + exes + " (registry, Image File Execution Options)");
                case GameActionKind.NetworkQos:
                    return L.T("Netzwerk-Priorität DSCP 46 für " + exes + " (Windows-QoS-Regel)", "Network priority DSCP 46 for " + exes + " (Windows QoS policy)");
                case GameActionKind.GpuHighPerformance:
                    return L.T("Windows-Grafikeinstellung „Hohe Leistung“ für " + exes, "Windows graphics setting “High performance” for " + exes);
                case GameActionKind.FullscreenOptimizationsOff:
                    return L.T("Vollbildoptimierung aus für " + exes + " (Kompatibilitäts-Flag)", "Fullscreen optimisations off for " + exes + " (compatibility flag)");
                case GameActionKind.Config:
                    return L.T("Ändert in " + File + ": ", "Changes in " + File + ": ")
                         + string.Join(", ", Entries.Select(e => (string.IsNullOrEmpty(e.Section) ? "" : "[" + e.Section + "] ") + e.Key + " → " + e.Value));
                case GameActionKind.LaunchOptions:
                    return L.T("Startoption zum Kopieren für " + LauncherName(Launcher) + ": ", "Launch option to copy for " + LauncherName(Launcher) + ": ") + Value;
            }
            return "";
        }

        public Tweak CreateTweak(GameDef g)
        {
            string id = "g." + g.Id + "." + Key;
            switch (Kind)
            {
                case GameActionKind.Priority: return new GamePriorityTweak(id, g, Level, Title, Summary);
                case GameActionKind.IoPriority: return new GameIoPriorityTweak(id, g, Title, Summary);
                case GameActionKind.NetworkQos: return new GameQosTweak(id, g, Title, Summary);
                case GameActionKind.GpuHighPerformance: return new GpuPreferenceTweak(id, g, Title, Summary);
                case GameActionKind.FullscreenOptimizationsOff: return new GameFsoTweak(id, g, Title, Summary);
                case GameActionKind.Config: return new ConfigFileTweak(id, g, this);
            }
            return null;
        }
    }

    sealed class GameDef
    {
        public string Id, Name, Publisher, AntiCheat, Description;
        public string[] Exes = new string[0];          // Dateinamen der Spiel-Exe
        public string[] ExePaths = new string[0];      // Pfadmuster, siehe GamePaths
        public int SteamAppId;                         // Bild vom Steam-CDN
        public string ImagePage;                       // offizielle Seite, Bild aus og:image
        public string ImageFile;                       // Bild im Plugin-Ordner
        public List<GameAction> Actions = new List<GameAction>();

        public bool IsPlugin;
        public string PluginDir, Version, Author;

        public string PathKey { get { return "games.path." + Id; } }

        List<string> found = new List<string>();
        bool configFound;

        /// <summary>Gefundene Exe-Pfade (nach <see cref="Refresh"/>).</summary>
        public List<string> FoundExes { get { lock (this) return found.ToList(); } }

        public bool Installed { get { lock (this) return found.Count > 0 || configFound; } }

        /// <summary>Sucht die Spieldateien. Läuft im Hintergrund, liest nur.</summary>
        public void Refresh()
        {
            var list = new List<string>();
            var names = new HashSet<string>(Exes, StringComparer.OrdinalIgnoreCase);
            string picked = Store.Get(PathKey);
            if (!string.IsNullOrEmpty(picked) && File.Exists(picked)) list.Add(picked);
            foreach (var pattern in ExePaths)
            {
                foreach (var p in GamePaths.Expand(pattern, null))
                {
                    if (!names.Contains(Path.GetFileName(p))) continue;
                    if (!list.Any(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase))) list.Add(p);
                }
            }

            bool cfg = false;
            var dirs = list.Select(Path.GetDirectoryName).ToList();
            foreach (var a in Actions.Where(a => a.Kind == GameActionKind.Config))
                if (GamePaths.Expand(a.File, dirs).Count > 0) { cfg = true; break; }

            lock (this) { found = list; configFound = cfg; }
        }

        public List<string> GameDirs() { return FoundExes.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase).ToList(); }

        /// <summary>Prüft nur die Prozessliste, öffnet kein Handle auf das Spiel.</summary>
        public bool IsRunning()
        {
            foreach (var exe in Exes)
            {
                Process[] procs;
                try { procs = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)); }
                catch { continue; }
                bool any = procs.Length > 0;
                foreach (var p in procs) p.Dispose();
                if (any) return true;
            }
            return false;
        }

        public string Initials
        {
            get
            {
                var parts = (Name ?? "?").Split(new[] { ' ', ':', '-' }, StringSplitOptions.RemoveEmptyEntries);
                string s = string.Concat(parts.Take(2).Select(p => char.ToUpperInvariant(p[0])));
                return s.Length == 0 ? "?" : s;
            }
        }
    }

    // ------------------------------------------------------------------------------------------
    // Pfadmuster. Ein Muster beginnt mit einem Platzhalter, danach folgen Ordner, '*' passt auf einen Ordnernamen.
    //   {steam}        jeder Steam-Bibliotheksordner ...\steamapps\common
    //   {steamroot}    Steam-Installationsordner (für userdata)
    //   {epic}         jeder Installationsordner aus den Manifesten des Epic Games Launchers
    //   {riot}         jeder Installationsordner aus den Metadaten des Riot Clients
    //   {drives}       jedes lokale Laufwerk (C:\, D:\, ...)
    //   {programfiles} {programfilesx86} {appdata} {localappdata} {documents} {userprofile} {savedgames}
    //   {game}         Ordner der gefundenen Spiel-Exe (nur für Konfigurationsdateien)
    // ------------------------------------------------------------------------------------------
    static class GamePaths
    {
        public static readonly string[] Tokens =
        {
            "steam", "steamroot", "epic", "riot", "drives", "programfiles", "programfilesx86",
            "appdata", "localappdata", "documents", "userprofile", "savedgames", "game"
        };

        /// <summary>Platzhalter, die auf Benutzerdaten oder Spielordner zeigen. Nur dort dürfen Konfigurationsdateien liegen.</summary>
        public static readonly string[] ConfigTokens = { "steam", "steamroot", "epic", "riot", "appdata", "localappdata", "documents", "savedgames", "game" };

        static readonly object Gate = new object();
        static List<string> steamLibs, epicDirs, riotDirs;
        static string steamRoot;

        public static void Reset() { lock (Gate) { steamLibs = null; epicDirs = null; riotDirs = null; steamRoot = null; } }

        static string SteamRoot()
        {
            lock (Gate)
            {
                if (steamRoot != null) return steamRoot;
                string p = null;
                try { p = Reg.Get(@"Software\Valve\Steam", "SteamPath", true) as string; } catch { }
                if (string.IsNullOrEmpty(p)) try { p = Reg.Get(@"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath") as string; } catch { }
                if (string.IsNullOrEmpty(p)) try { p = Reg.Get(@"SOFTWARE\Valve\Steam", "InstallPath") as string; } catch { }
                p = string.IsNullOrEmpty(p) ? "" : p.Replace('/', '\\');
                steamRoot = Directory.Exists(p) ? p : "";
                return steamRoot;
            }
        }

        static List<string> SteamLibraries()
        {
            lock (Gate)
            {
                if (steamLibs != null) return steamLibs;
                var roots = new List<string>();
                string root = SteamRoot();
                if (root != "") roots.Add(root);
                try
                {
                    string vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                    if (root != "" && File.Exists(vdf))
                        foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                            roots.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
                }
                catch { }
                steamLibs = roots.Select(r => Path.Combine(r, "steamapps", "common"))
                                 .Where(Directory.Exists)
                                 .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return steamLibs;
            }
        }

        static List<string> EpicInstalls()
        {
            lock (Gate)
            {
                if (epicDirs != null) return epicDirs;
                var list = new List<string>();
                try
                {
                    string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
                    if (Directory.Exists(dir))
                        foreach (var f in Directory.GetFiles(dir, "*.item"))
                        {
                            try
                            {
                                var m = Regex.Match(File.ReadAllText(f), "\"InstallLocation\"\\s*:\\s*\"([^\"]+)\"");
                                if (m.Success) list.Add(m.Groups[1].Value.Replace("\\\\", "\\").Replace('/', '\\'));
                            }
                            catch { }
                        }
                }
                catch { }
                epicDirs = list.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return epicDirs;
            }
        }

        static List<string> RiotInstalls()
        {
            lock (Gate)
            {
                if (riotDirs != null) return riotDirs;
                var list = new List<string>();
                try
                {
                    string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Riot Games\Metadata");
                    if (Directory.Exists(dir))
                        foreach (var f in Directory.GetFiles(dir, "*.product_settings.yaml", SearchOption.AllDirectories))
                        {
                            try
                            {
                                var m = Regex.Match(File.ReadAllText(f), "product_install_full_path:\\s*\"?([^\"\\r\\n]+)\"?");
                                if (m.Success) list.Add(m.Groups[1].Value.Trim().Replace('/', '\\'));
                            }
                            catch { }
                        }
                }
                catch { }
                riotDirs = list.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return riotDirs;
            }
        }

        static List<string> Bases(string token, List<string> gameDirs)
        {
            Func<Environment.SpecialFolder, List<string>> sf = f =>
            {
                string p = Environment.GetFolderPath(f);
                return string.IsNullOrEmpty(p) ? new List<string>() : new List<string> { p };
            };
            switch (token)
            {
                case "steam": return SteamLibraries();
                case "steamroot": { var r = SteamRoot(); return r == "" ? new List<string>() : new List<string> { r }; }
                case "epic": return EpicInstalls();
                case "riot": return RiotInstalls();
                case "drives":
                    try { return DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName).ToList(); }
                    catch { return new List<string>(); }
                case "programfiles": return sf(Environment.SpecialFolder.ProgramFiles);
                case "programfilesx86": return sf(Environment.SpecialFolder.ProgramFilesX86);
                case "appdata": return sf(Environment.SpecialFolder.ApplicationData);
                case "localappdata": return sf(Environment.SpecialFolder.LocalApplicationData);
                case "documents": return sf(Environment.SpecialFolder.MyDocuments);
                case "userprofile": return sf(Environment.SpecialFolder.UserProfile);
                case "savedgames":
                    {
                        var u = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                        return string.IsNullOrEmpty(u) ? new List<string>() : new List<string> { Path.Combine(u, "Saved Games") };
                    }
                case "game": return gameDirs ?? new List<string>();
            }
            return new List<string>();
        }

        /// <summary>Zerlegt ein Muster in Platzhalter und Ordnerteile. false, wenn es ungültig ist.</summary>
        public static bool TryParse(string pattern, out string token, out string[] parts)
        {
            token = null; parts = null;
            if (string.IsNullOrWhiteSpace(pattern)) return false;
            var m = Regex.Match(pattern.Trim(), @"^\{([a-z0-9]+)\}([\\/].*)?$");
            if (!m.Success || !Tokens.Contains(m.Groups[1].Value)) return false;
            token = m.Groups[1].Value;
            parts = (m.Groups[2].Value).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts)
            {
                if (p == "." || p == ".." || p.IndexOfAny(Path.GetInvalidFileNameChars().Where(c => c != '*').ToArray()) >= 0) return false;
                if (p.Contains("*") && p != "*") return false;
            }
            return parts.Length > 0;
        }

        /// <summary>Alle vorhandenen Dateien, auf die das Muster passt (höchstens 32).</summary>
        public static List<string> Expand(string pattern, List<string> gameDirs)
        {
            var result = new List<string>();
            string token; string[] parts;
            if (!TryParse(pattern, out token, out parts)) return result;

            var current = Bases(token, gameDirs).Where(Directory.Exists).ToList();
            for (int i = 0; i < parts.Length && current.Count > 0; i++)
            {
                bool last = i == parts.Length - 1;
                var next = new List<string>();
                foreach (var dir in current)
                {
                    try
                    {
                        if (parts[i] == "*")
                        {
                            if (!last) next.AddRange(Directory.GetDirectories(dir).Take(64));
                        }
                        else
                        {
                            string p = Path.Combine(dir, parts[i]);
                            if (last ? File.Exists(p) : Directory.Exists(p)) next.Add(p);
                        }
                    }
                    catch { }
                }
                current = next;
            }
            foreach (var f in current)
                if (!result.Contains(f, StringComparer.OrdinalIgnoreCase)) result.Add(f);
            return result.Take(32).ToList();
        }
    }

    // ------------------------------------------------------------------------------------------
    // CPU-Priorität über Image File Execution Options. Windows setzt sie beim Start des Spiels selbst.
    // Zurückgesetzt wird nur, was dieser Schalter selbst geändert hat (z. B. nicht die Spiel-Zeile im Boost-Tab).
    // ------------------------------------------------------------------------------------------
    sealed class GamePriorityTweak : Tweak
    {
        const string IfeoPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\";
        public static readonly Regex SafeExe = new Regex(@"^[\w\.\- \(\)\[\]]+\.exe$", RegexOptions.IgnoreCase);

        readonly string id, title, summary;
        readonly GameDef game;
        readonly int level;

        public GamePriorityTweak(string id, GameDef game, string level, string title, string summary)
        {
            this.id = id; this.game = game; this.title = title; this.summary = summary;
            this.level = level == "abovenormal" ? 6 : 3;   // 3 = Hoch, 6 = Höher als normal
        }

        List<RegItem> Items()
        {
            return game.Exes.Where(e => SafeExe.IsMatch(e))
                       .Select(e => RegItem.Dw(IfeoPath + e + @"\PerfOptions", "CpuPriorityClass", level, null)).ToList();
        }

        public override string Id { get { return id; } }
        public override string Title { get { return title ?? (level == 3 ? L.T("CPU-Priorität hoch", "High CPU priority") : L.T("CPU-Priorität erhöht", "Raised CPU priority")); } }
        public override string Summary
        {
            get
            {
                return summary ?? L.T("Windows startet " + string.Join(", ", game.Exes) + " mit Priorität „" + GameAction.LevelText(level == 6 ? "abovenormal" : "high") + "“",
                                      "Windows starts " + string.Join(", ", game.Exes) + " with “" + GameAction.LevelText(level == 6 ? "abovenormal" : "high") + "” priority");
            }
        }
        public override bool Relevant { get { return Items().Count > 0; } }
        public override string Detail
        {
            get
            {
                if (!Admin.IsElevated) return Summary + L.T(" · braucht Administratorrechte", " · needs administrator rights");
                if (RegStore.AllSet(Items()) && !WeApplied) return Summary + L.T(" · war schon gesetzt", " · was already set");
                return Summary;
            }
        }

        public override bool IsApplied() { return Relevant && RegStore.AllSet(Items()); }
        public override void Apply() { RegStore.Apply(id, Items()); }

        public override void Revert()
        {
            var mine = Items().Where(i => Store.Get(i.BackupKey(id)) != null).ToList();
            RegStore.Revert(id, mine);
            foreach (var it in mine)
            {
                Reg.DeleteKeyIfEmpty(it.Path);                                               // ...\PerfOptions
                Reg.DeleteKeyIfEmpty(it.Path.Substring(0, it.Path.LastIndexOf('\\')));       // ...\<exe>
            }
        }

        public override string Snapshot() { return Snap(Items()); }
    }

    // ------------------------------------------------------------------------------------------
    // Datenträger- und Speicherpriorität über Image File Execution Options (IoPriority, PagePriority).
    // Das Spiel lädt Texturen und Kartenteile dann vor Hintergrundprogrammen, und Windows lagert seine
    // Speicherseiten als letztes aus.
    // ------------------------------------------------------------------------------------------
    sealed class GameIoPriorityTweak : Tweak
    {
        const string IfeoPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\";

        readonly string id, title, summary;
        readonly GameDef game;

        public GameIoPriorityTweak(string id, GameDef game, string title, string summary)
        {
            this.id = id; this.game = game; this.title = title; this.summary = summary;
        }

        List<RegItem> Items()
        {
            var list = new List<RegItem>();
            foreach (var e in game.Exes.Where(x => GamePriorityTweak.SafeExe.IsMatch(x)))
            {
                list.Add(RegItem.Dw(IfeoPath + e + @"\PerfOptions", "IoPriority", 3, null));     // 3 = hoch
                list.Add(RegItem.Dw(IfeoPath + e + @"\PerfOptions", "PagePriority", 5, null));   // 5 = höchste normale Stufe
            }
            return list;
        }

        public override string Id { get { return id; } }
        public override string Title { get { return title ?? L.T("Datenträger- und RAM-Priorität hoch", "High disk and RAM priority"); } }
        public override string Summary
        {
            get
            {
                return summary ?? L.T("Spiel lädt vor Hintergrundprogrammen von der Platte, seine Daten bleiben länger im RAM. Hilft gegen Nachladeruckler",
                                      "The game reads from disk before background programs and its data stays in RAM longer. Helps against loading stutter");
            }
        }
        public override bool Relevant { get { return Items().Count > 0; } }
        public override string Detail
        {
            get
            {
                if (!Admin.IsElevated) return Summary + L.T(" · braucht Administratorrechte", " · needs administrator rights");
                if (RegStore.AllSet(Items()) && !WeApplied) return Summary + L.T(" · war schon gesetzt", " · was already set");
                return Summary;
            }
        }

        public override bool IsApplied() { return Relevant && RegStore.AllSet(Items()); }
        public override void Apply() { RegStore.Apply(id, Items()); }

        public override void Revert()
        {
            var mine = Items().Where(i => Store.Get(i.BackupKey(id)) != null).ToList();
            RegStore.Revert(id, mine);
            foreach (var it in mine)
            {
                Reg.DeleteKeyIfEmpty(it.Path);
                Reg.DeleteKeyIfEmpty(it.Path.Substring(0, it.Path.LastIndexOf('\\')));
            }
        }

        public override string Snapshot() { return Snap(Items()); }
    }

    // ------------------------------------------------------------------------------------------
    // Netzwerk-Priorität: Windows-QoS-Regel, die alle Pakete der Spiel-Exe mit DSCP 46 (Expedited Forwarding)
    // markiert. Router mit QoS/WMM schicken sie dann vor Downloads und Streams raus.
    // Teilt sich die NLA-Einstellung mit der Spiel-Zeile im Boost-Tab (gleiche Sicherung "qosnla").
    // ------------------------------------------------------------------------------------------
    sealed class GameQosTweak : Tweak
    {
        const string NlaPath = @"SYSTEM\CurrentControlSet\Services\Tcpip\QoS";
        const string NlaId = "qosnla";
        const string OursPrefix = "qos.ours|";

        readonly string id, title, summary;
        readonly GameDef game;

        public GameQosTweak(string id, GameDef game, string title, string summary)
        {
            this.id = id; this.game = game; this.title = title; this.summary = summary;
        }

        static RegItem Nla() { return RegItem.Str(NlaPath, "Do not use NLA", "1", null); }

        List<Tuple<string, string>> Policies()
        {
            var safe = System.Text.RegularExpressions.Regex.Replace(game.Id, @"[^a-z0-9\-]", "");
            return game.Exes.Where(e => GamePriorityTweak.SafeExe.IsMatch(e))
                       .Select((e, i) => Tuple.Create("RetracBoost-g-" + safe + "-" + i, e)).ToList();
        }

        public override string Id { get { return id; } }
        public override string Title { get { return title ?? L.T("Netzwerk-Priorität (DSCP 46)", "Network priority (DSCP 46)"); } }
        public override string Summary
        {
            get
            {
                return summary ?? L.T("Windows markiert die Pakete des Spiels als Echtzeit-Verkehr. Wirkt, wenn der Router QoS oder WMM beachtet",
                                      "Windows marks the game's packets as real-time traffic. Works when the router honours QoS or WMM");
            }
        }
        public override bool Relevant { get { return Policies().Count > 0; } }
        public override string Detail
        {
            get { return Admin.IsElevated ? Summary : Summary + L.T(" · braucht Administratorrechte", " · needs administrator rights"); }
        }
        public override string BusyText { get { return L.T("Setze QoS-Regel …", "Setting QoS policy …"); } }
        public override bool WeApplied { get { return Policies().Any(p => Store.Get(OursPrefix + p.Item1) == "1"); } }

        public override bool IsApplied()
        {
            var ps = Policies();
            if (ps.Count == 0 || !RegStore.AllSet(new[] { Nla() })) return false;
            string names = string.Join(",", ps.Select(p => Sh.Q(p.Item1)));
            var r = Sh.PowerShell("$n=@(Get-NetQosPolicy -PolicyStore localhost -ErrorAction SilentlyContinue | Where-Object { @(" + names + ") -contains $_.Name -and $_.DSCPAction -eq 46 }).Count;'N='+$n");
            return r.Ok && r.Out.Contains("N=" + ps.Count);
        }

        public override void Apply()
        {
            RegStore.Apply(NlaId, new[] { Nla() });
            foreach (var p in Policies())
            {
                var r = Sh.PowerShell(
                    "Get-NetQosPolicy -PolicyStore localhost -Name " + Sh.Q(p.Item1) + " -ErrorAction SilentlyContinue | Remove-NetQosPolicy -PolicyStore localhost -Confirm:$false;" +
                    "New-NetQosPolicy -PolicyStore localhost -Name " + Sh.Q(p.Item1) + " -AppPathNameMatchCondition " + Sh.Q(p.Item2) +
                    " -DSCPAction 46 -NetworkProfile All | Out-Null;'OK'");
                if (!r.Ok) throw new InvalidOperationException(r.Text);
                Store.Set(OursPrefix + p.Item1, "1");
            }
        }

        public override void Revert()
        {
            string left = "0";
            foreach (var p in Policies())
            {
                var r = Sh.PowerShell(
                    "Get-NetQosPolicy -PolicyStore localhost -Name " + Sh.Q(p.Item1) + " -ErrorAction SilentlyContinue | Remove-NetQosPolicy -PolicyStore localhost -Confirm:$false;" +
                    "$n=@(Get-NetQosPolicy -PolicyStore localhost -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'RetracBoost*' }).Count;'LEFT='+$n");
                if (!r.Ok) throw new InvalidOperationException(r.Text);
                Store.Set(OursPrefix + p.Item1, null);
                var m = System.Text.RegularExpressions.Regex.Match(r.Out, @"LEFT=(\d+)");
                if (m.Success) left = m.Groups[1].Value;
            }
            // NLA erst zurück, wenn keine Regel dieser App mehr existiert (auch nicht vom Boost-Tab)
            if (left == "0" && Store.Get("bk|" + NlaId + "|" + NlaPath + "|Do not use NLA") != null)
                RegStore.Revert(NlaId, new[] { Nla() });
        }

        public override string Snapshot()
        {
            var r = Sh.PowerShell("Get-NetQosPolicy -PolicyStore localhost -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'RetracBoost-g-*' } | ForEach-Object { $_.Name }");
            return "qos=" + r.Out.Trim();
        }
    }

    // ------------------------------------------------------------------------------------------
    // Windows-Grafikeinstellung pro Programm (Einstellungen → Anzeige → Grafik). Wirkt bei zwei Grafikkarten,
    // z. B. Laptop mit integrierter und dedizierter GPU: das Spiel läuft dann sicher auf der schnellen.
    // ------------------------------------------------------------------------------------------
    sealed class GpuPreferenceTweak : Tweak
    {
        const string GpuPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
        const string Value = "GpuPreference=2;";

        readonly string id, title, summary;
        readonly GameDef game;

        public GpuPreferenceTweak(string id, GameDef game, string title, string summary)
        {
            this.id = id; this.game = game; this.title = title; this.summary = summary;
        }

        List<RegItem> Items()
        {
            return game.FoundExes.Where(p => p.IndexOf('|') < 0)
                       .Select(p => RegItem.Str(GpuPath, p, Value, null, true)).ToList();
        }

        public override string Id { get { return id; } }
        public override string Title { get { return title ?? L.T("Grafikkarte: hohe Leistung", "Graphics card: high performance"); } }
        public override string Summary
        {
            get
            {
                return summary ?? L.T("Windows lässt das Spiel auf der schnelleren Grafikkarte laufen. Wirkt nur bei zwei GPUs, z. B. im Laptop",
                                      "Windows runs the game on the faster graphics card. Only matters with two GPUs, e.g. in a laptop");
            }
        }
        public override bool Relevant { get { return game.FoundExes.Count > 0; } }
        public override string Detail
        {
            get { return Relevant ? Summary : L.T("Spiel nicht gefunden. Wähle oben die .exe aus", "Game not found. Pick the .exe above"); }
        }

        public override bool IsApplied() { var items = Items(); return items.Count > 0 && RegStore.AllSet(items); }
        public override void Apply() { RegStore.Apply(id, Items()); }

        public override void Revert()
        {
            var mine = Items().Where(i => Store.Get(i.BackupKey(id)) != null).ToList();
            RegStore.Revert(id, mine);
        }

        public override string Snapshot() { return Snap(Items()); }
    }

    // ------------------------------------------------------------------------------------------
    // Vollbildoptimierung aus, für jede gefundene Exe des Spiels (nutzt denselben Speicher wie der Retrac-Tab).
    // ------------------------------------------------------------------------------------------
    sealed class GameFsoTweak : Tweak
    {
        readonly string id, title, summary;
        readonly GameDef game;

        public GameFsoTweak(string id, GameDef game, string title, string summary)
        {
            this.id = id; this.game = game; this.title = title; this.summary = summary;
        }

        List<FsoTweak> Parts() { return game.FoundExes.Select(p => new FsoTweak { ExePath = p }).ToList(); }

        public override string Id { get { return id; } }
        public override string Title { get { return title ?? L.T("Vollbildoptimierung aus", "Fullscreen optimisations off"); } }
        public override string Summary
        {
            get
            {
                return summary ?? L.T("Echtes Vollbild ohne Fenster-Komposition durch Windows. Hilft vor allem bei DirectX-11-Spielen",
                                      "Real fullscreen without window composition by Windows. Helps mostly with DirectX 11 games");
            }
        }
        public override bool Relevant { get { return game.FoundExes.Count > 0; } }
        public override string Detail
        {
            get { return Relevant ? Summary : L.T("Spiel nicht gefunden. Wähle oben die .exe aus", "Game not found. Pick the .exe above"); }
        }
        public override bool WeApplied { get { return Parts().Any(p => p.WeApplied); } }

        public override bool IsApplied() { var parts = Parts(); return parts.Count > 0 && parts.All(p => p.IsApplied()); }
        public override void Apply() { foreach (var p in Parts()) p.Apply(); }
        public override void Revert() { foreach (var p in Parts()) p.Revert(); }
        public override string Snapshot() { return string.Join("\n", Parts().Select(p => p.ExePath + " " + p.Snapshot())); }
    }

    // ------------------------------------------------------------------------------------------
    // Werte in einer Konfigurationsdatei des Spiels. Geändert werden nur Schlüssel, die das Spiel selbst
    // schon in die Datei geschrieben hat; fehlt ein Schlüssel, bleibt die Datei an der Stelle unverändert.
    // Der alte Wert jedes Schlüssels wird gesichert und beim Ausschalten zurückgeschrieben.
    // ------------------------------------------------------------------------------------------
    sealed class ConfigFileTweak : Tweak
    {
        static readonly string BackupDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RetracBoost", "config-backup");

        readonly string id;
        readonly GameDef game;
        readonly GameAction action;

        public ConfigFileTweak(string id, GameDef game, GameAction action) { this.id = id; this.game = game; this.action = action; }

        List<string> Files() { return GamePaths.Expand(action.File, game.GameDirs()).Take(16).ToList(); }

        string FileName { get { return Path.GetFileName(action.File.Replace('/', '\\')); } }

        public override string Id { get { return id; } }
        public override string Title { get { return action.Title ?? L.T("Einstellungen in ", "Settings in ") + FileName; } }
        public override string Summary
        {
            get { return action.Summary ?? string.Join(", ", action.Entries.Select(e => e.Key + " = " + e.Value)); }
        }
        public override bool Relevant { get { return Files().Count > 0; } }
        public override string Detail
        {
            get
            {
                if (!Relevant) return L.T(FileName + " nicht gefunden. Starte das Spiel einmal, dann legt es die Datei an",
                                          FileName + " not found. Start the game once so it creates the file");
                return Summary + " · " + FileName;
            }
        }
        public override bool WeApplied { get { return Store.Keys("cfg|" + id + "|").Count > 0; } }

        string BackupKey(string file, ConfigEntry e) { return "cfg|" + id + "|" + file + "|" + (e.Section ?? "") + "|" + e.Key; }

        public override bool IsApplied()
        {
            bool any = false;
            foreach (var f in Files())
            {
                string text;
                try { text = ConfigText.Read(f).Text; } catch { return false; }
                foreach (var e in action.Entries)
                {
                    string cur = ConfigText.Get(text, action.Format, e);
                    if (cur == null) continue;
                    any = true;
                    if (!ConfigText.SameValue(cur, e.Value)) return false;
                }
            }
            return any;
        }

        void CheckNotRunning()
        {
            if (game.IsRunning())
                throw new InvalidOperationException(L.T("Schließ " + game.Name + " zuerst, sonst überschreibt das Spiel die Datei beim Beenden.",
                                                        "Close " + game.Name + " first, otherwise the game overwrites the file when it exits."));
        }

        public override void Apply()
        {
            CheckNotRunning();
            bool anyKey = false;
            foreach (var f in Files())
            {
                var doc = ConfigText.Read(f);
                string text = doc.Text;
                bool changed = false;
                foreach (var e in action.Entries)
                {
                    string cur = ConfigText.Get(text, action.Format, e);
                    if (cur == null) continue;
                    anyKey = true;
                    if (ConfigText.SameValue(cur, e.Value)) continue;
                    string key = BackupKey(f, e);
                    if (Store.Get(key) == null) Store.Set(key, cur);
                    text = ConfigText.Set(text, action.Format, e, e.Value);
                    changed = true;
                }
                if (!changed) continue;
                SafetyCopy(f);
                doc.Text = text;
                ConfigText.Write(f, doc);
            }
            if (!anyKey)
                throw new InvalidOperationException(L.T("Keiner der Werte steht in " + FileName + ". Ändere im Spiel einmal eine Grafikeinstellung, dann schreibt es sie hinein.",
                                                        "None of the values are in " + FileName + ". Change a graphics setting in the game once, then it writes them."));
        }

        public override void Revert()
        {
            CheckNotRunning();
            var files = Files();
            foreach (var f in files)
            {
                var doc = ConfigText.Read(f);
                string text = doc.Text;
                bool changed = false;
                foreach (var e in action.Entries)
                {
                    string key = BackupKey(f, e);
                    string saved = Store.Get(key);
                    if (saved == null) continue;
                    if (ConfigText.Get(text, action.Format, e) != null)
                    {
                        text = ConfigText.Set(text, action.Format, e, saved);
                        changed = true;
                    }
                    Store.Set(key, null);
                }
                if (!changed) continue;
                doc.Text = text;
                ConfigText.Write(f, doc);
            }
            // Sicherungen zu Dateien, die es nicht mehr gibt
            foreach (var key in Store.Keys("cfg|" + id + "|"))
                if (!files.Any(f => key.StartsWith("cfg|" + id + "|" + f + "|", StringComparison.Ordinal))) Store.Set(key, null);
        }

        /// <summary>Einmalige Kopie der Originaldatei, bevor wir das erste Mal etwas ändern.</summary>
        void SafetyCopy(string file)
        {
            try
            {
                string name = id + "-" + ((uint)file.ToLowerInvariant().GetHashCode()).ToString("x8", CultureInfo.InvariantCulture) + "-" + Path.GetFileName(file) + ".bak";
                string target = Path.Combine(BackupDir, name);
                if (File.Exists(target)) return;
                Directory.CreateDirectory(BackupDir);
                File.Copy(file, target);
            }
            catch { }
        }

        public override string Snapshot()
        {
            var sb = new StringBuilder();
            foreach (var f in Files())
            {
                string text;
                try { text = ConfigText.Read(f).Text; } catch (Exception ex) { sb.AppendLine(f + ": " + ex.Message); continue; }
                foreach (var e in action.Entries) sb.AppendLine(Path.GetFileName(f) + "|" + e.Key + "=" + (ConfigText.Get(text, action.Format, e) ?? "(none)"));
            }
            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>Liest und ändert einzelne Werte in Textdateien, ohne den Rest der Datei anzufassen.</summary>
    static class ConfigText
    {
        public sealed class Doc
        {
            public string Text;
            public Encoding Encoding;
            public bool Bom;
        }

        const long MaxSize = 4 * 1024 * 1024;

        public static Doc Read(string path)
        {
            var info = new FileInfo(path);
            if (info.Length > MaxSize) throw new InvalidOperationException(L.T("Datei ist zu groß.", "File is too large."));
            byte[] b = File.ReadAllBytes(path);
            if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return new Doc { Text = Encoding.Unicode.GetString(b, 2, b.Length - 2), Encoding = Encoding.Unicode, Bom = true };
            if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return new Doc { Text = Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2), Encoding = Encoding.BigEndianUnicode, Bom = true };
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return new Doc { Text = Encoding.UTF8.GetString(b, 3, b.Length - 3), Encoding = new UTF8Encoding(false), Bom = true };
            return new Doc { Text = new UTF8Encoding(false).GetString(b), Encoding = new UTF8Encoding(false), Bom = false };
        }

        /// <summary>Schreibt über eine Zwischendatei, damit bei einem Fehler nie eine halbe Datei übrig bleibt.</summary>
        public static void Write(string path, Doc doc)
        {
            byte[] body = doc.Encoding.GetBytes(doc.Text);
            byte[] bom = doc.Bom ? doc.Encoding.GetPreamble() : new byte[0];
            if (doc.Bom && bom.Length == 0) bom = new byte[] { 0xEF, 0xBB, 0xBF };
            string tmp = path + ".retracboost.tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            {
                fs.Write(bom, 0, bom.Length);
                fs.Write(body, 0, body.Length);
            }
            var attr = File.GetAttributes(path);
            if ((attr & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attr & ~FileAttributes.ReadOnly);
            File.Copy(tmp, path, true);
            File.Delete(tmp);
            if ((attr & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attr);
        }

        static Regex LineRegex(ConfigFormat f, string key)
        {
            string k = Regex.Escape(key);
            switch (f)
            {
                case ConfigFormat.KeyValues: return new Regex("^(\\s*\"" + k + "\"\\s+\")([^\"]*)(\".*)$", RegexOptions.IgnoreCase);
                case ConfigFormat.Colon: return new Regex("^(\\s*" + k + "\\s*:)(.*?)(\\s*)$", RegexOptions.IgnoreCase);
                case ConfigFormat.Xml: return new Regex("^(.*<" + k + "\\b[^>]*?\\bvalue\\s*=\\s*\")([^\"]*)(\".*)$", RegexOptions.IgnoreCase);
                default: return new Regex("^(\\s*" + k + "\\s*=\\s*)(.*?)(\\s*)$", RegexOptions.IgnoreCase);
            }
        }

        /// <summary>Geht alle Zeilen durch; bei newValue != null wird jeder Treffer ersetzt. Liefert den ersten gefundenen Wert.</summary>
        static string Process(ref string text, ConfigFormat f, ConfigEntry e, string newValue)
        {
            var re = LineRegex(f, e.Key);
            var lines = text.Split('\n');
            string section = null, first = null;
            bool changed = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                bool cr = line.EndsWith("\r");
                string body = cr ? line.Substring(0, line.Length - 1) : line;
                string t = body.Trim();

                if (f == ConfigFormat.Ini)
                {
                    if (t.StartsWith(";") || t.StartsWith("#")) continue;
                    if (t.StartsWith("[") && t.EndsWith("]")) { section = t.Substring(1, t.Length - 2).Trim(); continue; }
                    if (!string.IsNullOrEmpty(e.Section) && !string.Equals(section, e.Section, StringComparison.OrdinalIgnoreCase)) continue;
                }
                else if (f == ConfigFormat.KeyValues && t.StartsWith("//")) continue;

                var m = re.Match(body);
                if (!m.Success) continue;
                string cur = m.Groups[2].Value;
                if (first == null) first = Unquote(f, cur);
                if (newValue == null) return first;

                string v = newValue;
                if (f == ConfigFormat.Colon && IsQuoted(cur) && !IsQuoted(v)) v = "\"" + v + "\"";   // Minecraft schreibt manche Werte in Anführungszeichen
                body = m.Groups[1].Value + v + m.Groups[3].Value;
                lines[i] = cr ? body + "\r" : body;
                changed = true;
            }
            if (changed) text = string.Join("\n", lines);
            return first;
        }

        static bool IsQuoted(string s) { s = s.Trim(); return s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"'; }

        static string Unquote(ConfigFormat f, string s)
        {
            s = s.Trim();
            return f == ConfigFormat.Colon && IsQuoted(s) ? s.Substring(1, s.Length - 2) : s;
        }

        public static string Get(string text, ConfigFormat f, ConfigEntry e) { return Process(ref text, f, e, null); }

        public static string Set(string text, ConfigFormat f, ConfigEntry e, string value)
        {
            Process(ref text, f, e, value);
            return text;
        }

        /// <summary>Vergleicht ohne Rücksicht auf Groß/Klein und Zahlformat ("0" = "0.000000").</summary>
        public static bool SameValue(string a, string b)
        {
            a = (a ?? "").Trim().Trim('"'); b = (b ?? "").Trim().Trim('"');
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
            double x, y;
            return double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out y)
                && Math.Abs(x - y) < 1e-9;
        }
    }

    // ------------------------------------------------------------------------------------------
    // Die 8 eingebauten Spiele. Bilder: Steam-Spiele vom Steam-CDN (offizielles Store-Bild),
    // die anderen aus dem og:image der offiziellen Seite des Herstellers.
    // Konfigurationswerte sind die Schlüssel, die das jeweilige Spiel selbst in seine Datei schreibt.
    // ------------------------------------------------------------------------------------------
    static class BuiltInGames
    {
        static ConfigEntry E(string key, string value) { return new ConfigEntry(null, key, value); }
        static ConfigEntry E(string section, string key, string value) { return new ConfigEntry(section, key, value); }

        /// <summary>Die Windows-Schalter, die jedes Spiel mit bekannter Exe bekommt.</summary>
        static GameAction[] WindowsActions()
        {
            return new[] { GameAction.Priority(), GameAction.Io(), GameAction.Qos(), GameAction.Gpu(), GameAction.Fso() };
        }

        static GameDef With(GameDef g, params GameAction[] actions)
        {
            if (g.Exes.Length > 0) g.Actions.AddRange(WindowsActions());
            g.Actions.AddRange(actions);
            return g;
        }

        public static List<GameDef> All()
        {
            var vsyncOff = L.T("VSync aus", "VSync off");

            const string mc = "{appdata}/.minecraft/options.txt";
            const string cs = "{steamroot}/userdata/*/730/local/cfg/cs2_video.txt";
            const string gta = "{documents}/Rockstar Games/GTA V/settings.xml";
            const string fn = "{localappdata}/FortniteGame/Saved/Config/WindowsClient/GameUserSettings.ini";
            const string val = "{localappdata}/VALORANT/Saved/Config/*/Windows/GameUserSettings.ini";
            const string apex = "{savedgames}/Respawn/Apex/local/videoconfig.txt";
            const string rl = "{documents}/My Games/Rocket League/TAGame/Config/TASystemSettings.ini";
            const string pubg = "{localappdata}/TslGame/Saved/Config/WindowsNoEditor/GameUserSettings.ini";

            return new List<GameDef>
            {
                With(new GameDef
                {
                    Id = "minecraft", Name = "Minecraft", Publisher = "Mojang Studios",
                    ImagePage = "https://www.minecraft.net/en-us",
                    Description = L.T("Java Edition. Die Werte stehen in options.txt, Minecraft vorher schließen. Minecraft läuft als javaw.exe, deshalb gibt es hier keine Prioritäts-Schalter.",
                                      "Java Edition. The values live in options.txt, close Minecraft first. Minecraft runs as javaw.exe, so there are no priority switches here.")
                },
                    GameAction.Config("fps", L.T("VSync aus, FPS ohne Limit", "VSync off, no FPS limit"),
                        L.T("enableVsync = false, maxFps = 260 (in Minecraft „Unbegrenzt“)", "enableVsync = false, maxFps = 260 (Minecraft's “Unlimited”)"),
                        mc, ConfigFormat.Colon, E("enableVsync", "false"), E("maxFps", "260")),
                    GameAction.Config("light", L.T("Leichtere Grafik", "Lighter graphics"),
                        L.T("Grafik „Schnell“, keine Wolken und Objektschatten, wenige Partikel, Biom-Übergang aus",
                            "Graphics “Fast”, no clouds or entity shadows, few particles, biome blend off"),
                        mc, ConfigFormat.Colon, E("graphicsMode", "0"), E("renderClouds", "false"), E("entityShadows", "false"), E("particles", "2"), E("biomeBlendRadius", "0")),
                    GameAction.Config("distance", L.T("Sichtweite 8 Chunks", "Render distance 8 chunks"),
                        L.T("renderDistance und simulationDistance auf 8. Spürbar mehr FPS, man sieht weniger weit",
                            "renderDistance and simulationDistance set to 8. Clearly more FPS, you see less far"),
                        mc, ConfigFormat.Colon, E("renderDistance", "8"), E("simulationDistance", "8")),
                    GameAction.Config("entities", L.T("Objekte erst näher zeigen", "Show entities closer only"),
                        L.T("entityDistanceScaling = 0.75: Mobs und Gegenstände erscheinen erst auf 75 % der Entfernung", "entityDistanceScaling = 0.75: mobs and items appear at 75 % of the distance"),
                        mc, ConfigFormat.Colon, E("entityDistanceScaling", "0.75")),
                    GameAction.Config("mipmap", L.T("Mipmaps aus", "Mipmaps off"),
                        L.T("mipmapLevels = 0. Etwas weniger Grafikarbeit, entfernte Blöcke sehen körniger aus", "mipmapLevels = 0. A bit less GPU work, distant blocks look grainier"),
                        mc, ConfigFormat.Colon, E("mipmapLevels", "0")),
                    GameAction.Config("effects", L.T("Bildschirm-Effekte aus", "Screen effects off"),
                        L.T("Kein Kamerawackeln beim Laufen, keine Sichtfeld- und Verzerrungseffekte", "No view bobbing, no FOV or distortion effects"),
                        mc, ConfigFormat.Colon, E("bobView", "false"), E("fovEffectScale", "0.0"), E("screenEffectScale", "0.0")),
                    GameAction.Config("input", L.T("Maus: Rohdaten", "Mouse: raw input"),
                        L.T("rawMouseInput = true: Mausbewegung ohne Windows-Beschleunigung", "rawMouseInput = true: mouse movement without Windows acceleration"),
                        mc, ConfigFormat.Colon, E("rawMouseInput", "true")),
                    GameAction.Config("fullscreen", L.T("Vollbild", "Fullscreen"),
                        L.T("fullscreen = true. Im Vollbild läuft Minecraft meist flüssiger als im Fenster", "fullscreen = true. Minecraft usually runs smoother in fullscreen than windowed"),
                        mc, ConfigFormat.Colon, E("fullscreen", "true"))),

                With(new GameDef
                {
                    Id = "cs2", Name = "Counter-Strike 2", Publisher = "Valve", AntiCheat = "VAC", SteamAppId = 730,
                    Exes = new[] { "cs2.exe" },
                    ExePaths = new[] { "{steam}/Counter-Strike Global Offensive/game/bin/win64/cs2.exe" }
                },
                    GameAction.Config("video", L.T("VSync aus, NVIDIA Reflex an", "VSync off, NVIDIA Reflex on"),
                        L.T("setting.mat_vsync = 0, setting.r_low_latency = 1 in cs2_video.txt (alle Steam-Konten)",
                            "setting.mat_vsync = 0, setting.r_low_latency = 1 in cs2_video.txt (all Steam accounts)"),
                        cs, ConfigFormat.KeyValues, E("setting.mat_vsync", "0"), E("setting.r_low_latency", "1")),
                    GameAction.Config("perf", L.T("Leistungs-Grafik", "Performance graphics"),
                        L.T("Shader, Partikel und Umgebungsverdeckung niedrig. Schatten bleiben, damit Gegner sichtbar bleiben",
                            "Shaders, particles and ambient occlusion low. Shadows stay so enemies remain visible"),
                        cs, ConfigFormat.KeyValues, E("setting.shaderquality", "0"), E("setting.videocfg_particle_detail", "0"), E("setting.videocfg_ao_detail", "0")),
                    GameAction.Config("aa", L.T("Kantenglättung aus", "Anti-aliasing off"),
                        L.T("setting.msaa_samples = 0, CMAA aus. Mehr FPS, Kanten etwas zackiger", "setting.msaa_samples = 0, CMAA off. More FPS, slightly jaggier edges"),
                        cs, ConfigFormat.KeyValues, E("setting.msaa_samples", "0"), E("setting.r_csgo_cmaa_enable", "0")),
                    GameAction.Config("fullscreen", L.T("Exklusives Vollbild", "Exclusive fullscreen"),
                        L.T("setting.fullscreen = 1. Geringere Eingabeverzögerung als im randlosen Fenster", "setting.fullscreen = 1. Lower input lag than borderless window"),
                        cs, ConfigFormat.KeyValues, E("setting.fullscreen", "1")),
                    GameAction.Launch("steam", "+fps_max 0", L.T("Hebt die FPS-Obergrenze auf", "Removes the FPS cap"))),

                With(new GameDef
                {
                    Id = "gta5", Name = "Grand Theft Auto V", Publisher = "Rockstar Games", AntiCheat = "BattlEye", SteamAppId = 271590,
                    Exes = new[] { "GTA5.exe", "GTA5_Enhanced.exe" },
                    ExePaths = new[]
                    {
                        "{steam}/Grand Theft Auto V/GTA5.exe",
                        "{steam}/Grand Theft Auto V Enhanced/GTA5_Enhanced.exe",
                        "{epic}/GTA5.exe",
                        "{epic}/GTA5_Enhanced.exe",
                        "{programfiles}/Rockstar Games/Grand Theft Auto V/GTA5.exe",
                        "{programfiles}/Rockstar Games/Grand Theft Auto V Enhanced/GTA5_Enhanced.exe"
                    }
                },
                    GameAction.Config("vsync", vsyncOff, L.T("VSync value = 0 in settings.xml", "VSync value = 0 in settings.xml"),
                        gta, ConfigFormat.Xml, E("VSync", "0")),
                    GameAction.Config("grass", L.T("Gras niedrig", "Grass low"),
                        L.T("GrassQuality = 0. Gras kostet in GTA V auf dem Land besonders viele FPS", "GrassQuality = 0. Grass costs a lot of FPS in the countryside"),
                        gta, ConfigFormat.Xml, E("GrassQuality", "0")),
                    GameAction.Config("postfx", L.T("Nachbearbeitung niedrig, keine Bewegungsunschärfe", "Post effects low, no motion blur"),
                        L.T("PostFX = 0, MotionBlurStrength = 0, Tiefenunschärfe aus", "PostFX = 0, MotionBlurStrength = 0, depth of field off"),
                        gta, ConfigFormat.Xml, E("PostFX", "0"), E("MotionBlurStrength", "0.000000"), E("DoF", "false")),
                    GameAction.Config("shadows", L.T("Weiche Schatten und Reflexionen niedrig", "Soft shadows and reflections low"),
                        L.T("Shadow_SoftShadows = 0, ReflectionQuality = 0, ReflectionMSAA = 0", "Shadow_SoftShadows = 0, ReflectionQuality = 0, ReflectionMSAA = 0"),
                        gta, ConfigFormat.Xml, E("Shadow_SoftShadows", "0"), E("ReflectionQuality", "0"), E("ReflectionMSAA", "0")),
                    GameAction.Config("msaa", L.T("MSAA aus, FXAA an", "MSAA off, FXAA on"),
                        L.T("MSAA kostet viel Leistung, FXAA glättet fast ohne Kosten", "MSAA costs a lot, FXAA smooths edges almost for free"),
                        gta, ConfigFormat.Xml, E("MSAA", "0"), E("FXAA_Enabled", "true"))),

                With(new GameDef
                {
                    Id = "fortnite", Name = "Fortnite", Publisher = "Epic Games", AntiCheat = "Easy Anti-Cheat, BattlEye",
                    ImagePage = "https://www.fortnite.com/",
                    Exes = new[] { "FortniteClient-Win64-Shipping.exe" },
                    ExePaths = new[]
                    {
                        "{epic}/FortniteGame/Binaries/Win64/FortniteClient-Win64-Shipping.exe",
                        "{programfiles}/Epic Games/Fortnite/FortniteGame/Binaries/Win64/FortniteClient-Win64-Shipping.exe"
                    }
                },
                    GameAction.Config("vsync", vsyncOff, L.T("bUseVSync = False in GameUserSettings.ini", "bUseVSync = False in GameUserSettings.ini"),
                        fn, ConfigFormat.Ini, E("bUseVSync", "False")),
                    GameAction.Config("shadows", L.T("Schatten aus", "Shadows off"),
                        L.T("sg.ShadowQuality = 0. Kostet sonst viel FPS", "sg.ShadowQuality = 0. Costs a lot of FPS otherwise"),
                        fn, ConfigFormat.Ini, E("ScalabilityGroups", "sg.ShadowQuality", "0")),
                    GameAction.Config("effects", L.T("Effekte, Nachbearbeitung und Beleuchtung niedrig", "Effects, post-processing and lighting low"),
                        L.T("sg.EffectsQuality, sg.PostProcessQuality, sg.GlobalIlluminationQuality, sg.ReflectionQuality und sg.FoliageQuality auf 0",
                            "sg.EffectsQuality, sg.PostProcessQuality, sg.GlobalIlluminationQuality, sg.ReflectionQuality and sg.FoliageQuality set to 0"),
                        fn, ConfigFormat.Ini,
                        E("ScalabilityGroups", "sg.EffectsQuality", "0"), E("ScalabilityGroups", "sg.PostProcessQuality", "0"),
                        E("ScalabilityGroups", "sg.GlobalIlluminationQuality", "0"), E("ScalabilityGroups", "sg.ReflectionQuality", "0"),
                        E("ScalabilityGroups", "sg.FoliageQuality", "0")),
                    GameAction.Config("blur", L.T("Bewegungsunschärfe und Raytracing aus", "Motion blur and ray tracing off"),
                        L.T("bMotionBlur = False, bRayTracing = False", "bMotionBlur = False, bRayTracing = False"),
                        fn, ConfigFormat.Ini, E("bMotionBlur", "False"), E("bRayTracing", "False")),
                    GameAction.Config("performance", L.T("Performance-Modus", "Performance mode"),
                        L.T("PreferredRHI = dx11 und PreferredFeatureLevel = es31: Fortnites eigener Performance-Modus. Gilt ab dem nächsten Start",
                            "PreferredRHI = dx11 and PreferredFeatureLevel = es31: Fortnite's own performance mode. Applies from the next start"),
                        fn, ConfigFormat.Ini, E("D3DRHIPreference", "PreferredRHI", "dx11"), E("D3DRHIPreference", "PreferredFeatureLevel", "es31"))),

                With(new GameDef
                {
                    Id = "valorant", Name = "VALORANT", Publisher = "Riot Games", AntiCheat = "Riot Vanguard",
                    ImagePage = "https://playvalorant.com/en-us/",
                    Description = L.T("VALORANT speichert viele Grafikwerte zusätzlich im Riot-Konto und kann lokale Werte beim Start überschreiben.",
                                      "VALORANT also stores many graphics values in the Riot account and may overwrite local values at start."),
                    Exes = new[] { "VALORANT-Win64-Shipping.exe" },
                    ExePaths = new[]
                    {
                        "{riot}/ShooterGame/Binaries/Win64/VALORANT-Win64-Shipping.exe",
                        "{drives}/Riot Games/VALORANT/live/ShooterGame/Binaries/Win64/VALORANT-Win64-Shipping.exe"
                    }
                },
                    GameAction.Config("vsync", vsyncOff, L.T("bUseVSync = False in GameUserSettings.ini (alle Riot-Konten)", "bUseVSync = False in GameUserSettings.ini (all Riot accounts)"),
                        val, ConfigFormat.Ini, E("bUseVSync", "False")),
                    GameAction.Config("low", L.T("Schatten, Effekte und Nachbearbeitung niedrig", "Shadows, effects and post-processing low"),
                        L.T("sg.ShadowQuality, sg.EffectsQuality und sg.PostProcessQuality auf 0", "sg.ShadowQuality, sg.EffectsQuality and sg.PostProcessQuality set to 0"),
                        val, ConfigFormat.Ini,
                        E("ScalabilityGroups", "sg.ShadowQuality", "0"), E("ScalabilityGroups", "sg.EffectsQuality", "0"), E("ScalabilityGroups", "sg.PostProcessQuality", "0"))),

                With(new GameDef
                {
                    Id = "apex", Name = "Apex Legends", Publisher = "Respawn / EA", AntiCheat = "Easy Anti-Cheat", SteamAppId = 1172470,
                    Exes = new[] { "r5apex.exe", "r5apex_dx12.exe" },
                    ExePaths = new[]
                    {
                        "{steam}/Apex Legends/r5apex.exe",
                        "{steam}/Apex Legends/r5apex_dx12.exe",
                        "{epic}/r5apex.exe",
                        "{epic}/r5apex_dx12.exe",
                        "{programfiles}/EA Games/Apex/r5apex.exe",
                        "{programfiles}/EA Games/Apex/r5apex_dx12.exe"
                    }
                },
                    GameAction.Config("vsync", vsyncOff, L.T("setting.mat_vsync_mode = 0 in videoconfig.txt", "setting.mat_vsync_mode = 0 in videoconfig.txt"),
                        apex, ConfigFormat.KeyValues, E("setting.mat_vsync_mode", "0")),
                    GameAction.Config("perf", L.T("Umgebungsverdeckung, Volumenlicht und Partikel niedrig", "Ambient occlusion, volumetric light and particles low"),
                        L.T("setting.ssao_enabled = 0, setting.volumetric_lighting = 0, setting.particle_cpu_level = 0",
                            "setting.ssao_enabled = 0, setting.volumetric_lighting = 0, setting.particle_cpu_level = 0"),
                        apex, ConfigFormat.KeyValues, E("setting.ssao_enabled", "0"), E("setting.volumetric_lighting", "0"), E("setting.particle_cpu_level", "0")),
                    GameAction.Config("sun", L.T("Sonnenschatten aus", "Sun shadows off"),
                        L.T("setting.csm_enabled = 0. Einer der teuersten Grafikwerte in Apex", "setting.csm_enabled = 0. One of the most expensive graphics values in Apex"),
                        apex, ConfigFormat.KeyValues, E("setting.csm_enabled", "0")),
                    GameAction.Config("decals", L.T("Einschusslöcher und Spuren weniger", "Fewer bullet holes and decals"),
                        L.T("setting.r_decals = 0. Weniger Arbeit in langen Kämpfen", "setting.r_decals = 0. Less work in long fights"),
                        apex, ConfigFormat.KeyValues, E("setting.r_decals", "0")),
                    GameAction.Launch("steam", "+fps_max 0", L.T("Hebt die eingebaute FPS-Grenze auf", "Removes the built-in FPS cap")),
                    GameAction.Launch("steam", "-novid", L.T("Überspringt das Intro-Video beim Start", "Skips the intro video at start"))),

                With(new GameDef
                {
                    Id = "rocketleague", Name = "Rocket League", Publisher = "Psyonix", AntiCheat = "Easy Anti-Cheat", SteamAppId = 252950,
                    Exes = new[] { "RocketLeague.exe" },
                    ExePaths = new[]
                    {
                        "{steam}/rocketleague/Binaries/Win64/RocketLeague.exe",
                        "{epic}/Binaries/Win64/RocketLeague.exe"
                    }
                },
                    GameAction.Config("vsync", vsyncOff, L.T("UseVsync = False in TASystemSettings.ini", "UseVsync = False in TASystemSettings.ini"),
                        rl, ConfigFormat.Ini, E("SystemSettings", "UseVsync", "False")),
                    GameAction.Config("effects", L.T("Bildeffekte aus", "Image effects off"),
                        L.T("Bewegungsunschärfe, Tiefenschärfe, Bloom, Lichtstrahlen und Linsenreflexe aus. Mehr FPS und klarere Sicht auf den Ball",
                            "Motion blur, depth of field, bloom, light shafts and lens flares off. More FPS and a clearer view of the ball"),
                        rl, ConfigFormat.Ini,
                        E("SystemSettings", "MotionBlur", "False"), E("SystemSettings", "DepthOfField", "False"), E("SystemSettings", "Bloom", "False"),
                        E("SystemSettings", "LightShafts", "False"), E("SystemSettings", "LensFlares", "False")),
                    GameAction.Config("shadows", L.T("Dynamische Schatten und Umgebungsverdeckung aus", "Dynamic shadows and ambient occlusion off"),
                        L.T("DynamicShadows = False, AmbientOcclusion = False", "DynamicShadows = False, AmbientOcclusion = False"),
                        rl, ConfigFormat.Ini, E("SystemSettings", "DynamicShadows", "False"), E("SystemSettings", "AmbientOcclusion", "False"))),

                With(new GameDef
                {
                    Id = "pubg", Name = "PUBG: BATTLEGROUNDS", Publisher = "KRAFTON", AntiCheat = "BattlEye", SteamAppId = 578080,
                    Exes = new[] { "TslGame.exe" },
                    ExePaths = new[] { "{steam}/PUBG/TslGame/Binaries/Win64/TslGame.exe" }
                },
                    GameAction.Config("vsync", vsyncOff, L.T("bUseVSync = False in GameUserSettings.ini", "bUseVSync = False in GameUserSettings.ini"),
                        pubg, ConfigFormat.Ini, E("bUseVSync", "False")),
                    GameAction.Config("low", L.T("Schatten, Effekte und Vegetation niedrig", "Shadows, effects and foliage low"),
                        L.T("sg.ShadowQuality, sg.EffectsQuality, sg.FoliageQuality und sg.PostProcessQuality auf 0. Weniger Gras heißt auch: Gegner im Gras sind leichter zu sehen",
                            "sg.ShadowQuality, sg.EffectsQuality, sg.FoliageQuality and sg.PostProcessQuality set to 0. Less grass also makes enemies in it easier to see"),
                        pubg, ConfigFormat.Ini,
                        E("ScalabilityGroups", "sg.ShadowQuality", "0"), E("ScalabilityGroups", "sg.EffectsQuality", "0"),
                        E("ScalabilityGroups", "sg.FoliageQuality", "0"), E("ScalabilityGroups", "sg.PostProcessQuality", "0")))
            };
        }
    }
}
