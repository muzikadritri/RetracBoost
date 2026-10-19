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

    enum GameActionKind { Priority, GpuHighPerformance, FullscreenOptimizationsOff, Config, LaunchOptions }

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
    // ------------------------------------------------------------------------------------------
    static class BuiltInGames
    {
        public static List<GameDef> All()
        {
            var vsyncOff = L.T("VSync aus", "VSync off");

            return new List<GameDef>
            {
                new GameDef
                {
                    Id = "minecraft", Name = "Minecraft", Publisher = "Mojang Studios",
                    ImagePage = "https://www.minecraft.net/en-us",
                    Description = L.T("Java Edition. Die Werte stehen in options.txt, Minecraft vorher schließen.",
                                      "Java Edition. The values live in options.txt, close Minecraft first."),
                    Actions =
                    {
                        GameAction.Config("fps", L.T("VSync aus, FPS ohne Limit", "VSync off, no FPS limit"),
                            L.T("enableVsync = false, maxFps = 260 (in Minecraft „Unbegrenzt“)", "enableVsync = false, maxFps = 260 (Minecraft's “Unlimited”)"),
                            "{appdata}/.minecraft/options.txt", ConfigFormat.Colon,
                            new ConfigEntry(null, "enableVsync", "false"),
                            new ConfigEntry(null, "maxFps", "260")),
                        GameAction.Config("light", L.T("Leichtere Grafik", "Lighter graphics"),
                            L.T("Grafik „Schnell“, keine Wolken und Objektschatten, wenige Partikel, Biom-Übergang aus",
                                "Graphics “Fast”, no clouds or entity shadows, few particles, biome blend off"),
                            "{appdata}/.minecraft/options.txt", ConfigFormat.Colon,
                            new ConfigEntry(null, "graphicsMode", "0"),
                            new ConfigEntry(null, "renderClouds", "false"),
                            new ConfigEntry(null, "entityShadows", "false"),
                            new ConfigEntry(null, "particles", "2"),
                            new ConfigEntry(null, "biomeBlendRadius", "0")),
                        GameAction.Config("distance", L.T("Sichtweite 8 Chunks", "Render distance 8 chunks"),
                            L.T("renderDistance und simulationDistance auf 8. Spürbar mehr FPS, man sieht weniger weit",
                                "renderDistance and simulationDistance set to 8. Clearly more FPS, you see less far"),
                            "{appdata}/.minecraft/options.txt", ConfigFormat.Colon,
                            new ConfigEntry(null, "renderDistance", "8"),
                            new ConfigEntry(null, "simulationDistance", "8"))
                    }
                },
                new GameDef
                {
                    Id = "cs2", Name = "Counter-Strike 2", Publisher = "Valve", AntiCheat = "VAC", SteamAppId = 730,
                    Exes = new[] { "cs2.exe" },
                    ExePaths = new[] { "{steam}/Counter-Strike Global Offensive/game/bin/win64/cs2.exe" },
                    Actions =
                    {
                        GameAction.Priority(), GameAction.Gpu(), GameAction.Fso(),
                        GameAction.Config("video", L.T("VSync aus, NVIDIA Reflex an", "VSync off, NVIDIA Reflex on"),
                            L.T("setting.mat_vsync = 0, setting.r_low_latency = 1 in cs2_video.txt (alle Steam-Konten)",
                                "setting.mat_vsync = 0, setting.r_low_latency = 1 in cs2_video.txt (all Steam accounts)"),
                            "{steamroot}/userdata/*/730/local/cfg/cs2_video.txt", ConfigFormat.KeyValues,
                            new ConfigEntry(null, "setting.mat_vsync", "0"),
                            new ConfigEntry(null, "setting.r_low_latency", "1"))
                    }
                },
                new GameDef
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
                    },
                    Actions =
                    {
                        GameAction.Priority(), GameAction.Gpu(), GameAction.Fso(),
                        GameAction.Config("vsync", vsyncOff, L.T("VSync value = 0 in settings.xml", "VSync value = 0 in settings.xml"),
                            "{documents}/Rockstar Games/GTA V/settings.xml", ConfigFormat.Xml,
                            new ConfigEntry(null, "VSync", "0"))
                    }
                },
                new GameDef
                {
                    Id = "fortnite", Name = "Fortnite", Publisher = "Epic Games", AntiCheat = "Easy Anti-Cheat, BattlEye",
                    ImagePage = "https://www.fortnite.com/",
                    Exes = new[] { "FortniteClient-Win64-Shipping.exe" },
                    ExePaths = new[]
                    {
                        "{epic}/FortniteGame/Binaries/Win64/FortniteClient-Win64-Shipping.exe",
                        "{programfiles}/Epic Games/Fortnite/FortniteGame/Binaries/Win64/FortniteClient-Win64-Shipping.exe"
                    },
                    Actions =
                    {
                        GameAction.Priority(), GameAction.Gpu(), GameAction.Fso(),
                        GameAction.Config("vsync", vsyncOff, L.T("bUseVSync = False in GameUserSettings.ini", "bUseVSync = False in GameUserSettings.ini"),
                            "{localappdata}/FortniteGame/Saved/Config/WindowsClient/GameUserSettings.ini", ConfigFormat.Ini,
                            new ConfigEntry(null, "bUseVSync", "False")),
                        GameAction.Config("shadows", L.T("Schatten aus", "Shadows off"),
                            L.T("sg.ShadowQuality = 0. Kostet sonst viel FPS", "sg.ShadowQuality = 0. Costs a lot of FPS otherwise"),
                            "{localappdata}/FortniteGame/Saved/Config/WindowsClient/GameUserSettings.ini", ConfigFormat.Ini,
                            new ConfigEntry("ScalabilityGroups", "sg.ShadowQuality", "0"))
                    }
                },
                new GameDef
                {
                    Id = "valorant", Name = "VALORANT", Publisher = "Riot Games", AntiCheat = "Riot Vanguard",
                    ImagePage = "https://playvalorant.com/en-us/",
                    Description = L.T("Grafikwerte speichert VALORANT im Riot-Konto, deshalb hier nur Windows-Einstellungen.",
                                      "VALORANT stores graphics values in the Riot account, so only Windows settings here."),
                    Exes = new[] { "VALORANT-Win64-Shipping.exe" },
                    ExePaths = new[]
                    {
                        "{riot}/ShooterGame/Binaries/Win64/VALORANT-Win64-Shipping.exe",
                        "{drives}/Riot Games/VALORANT/live/ShooterGame/Binaries/Win64/VALORANT-Win64-Shipping.exe"
                    },
                    Actions = { GameAction.Priority(), GameAction.Gpu(), GameAction.Fso() }
                },
                new GameDef
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
                    },
                    Actions =
                    {
                        GameAction.Priority(), GameAction.Gpu(), GameAction.Fso(),
                        GameAction.Config("vsync", vsyncOff, L.T("setting.mat_vsync_mode = 0 in videoconfig.txt", "setting.mat_vsync_mode = 0 in videoconfig.txt"),
                            "{savedgames}/Respawn/Apex/local/videoconfig.txt", ConfigFormat.KeyValues,
                            new ConfigEntry(null, "setting.mat_vsync_mode", "0")),
                        GameAction.Launch("steam", "+fps_max 0", L.T("Hebt die eingebaute FPS-Grenze auf", "Removes the built-in FPS cap"))
                    }
                },
                new GameDef
                {
                    Id = "rocketleague", Name = "Rocket League", Publisher = "Psyonix", AntiCheat = "Easy Anti-Cheat", SteamAppId = 252950,
                    Exes = new[] { "RocketLeague.exe" },
                    ExePaths = new[]
                    {
                        "{steam}/rocketleague/Binaries/Win64/RocketLeague.exe",
                        "{epic}/Binaries/Win64/RocketLeague.exe"
                    },
                    Actions =
                    {
                        GameAction.Priority(), GameAction.Gpu(), GameAction.Fso(),
                        GameAction.Config("vsync", vsyncOff, L.T("UseVsync = False in TASystemSettings.ini", "UseVsync = False in TASystemSettings.ini"),
                            "{documents}/My Games/Rocket League/TAGame/Config/TASystemSettings.ini", ConfigFormat.Ini,
                            new ConfigEntry("SystemSettings", "UseVsync", "False"))
                    }
                },
                new GameDef
                {
                    Id = "pubg", Name = "PUBG: BATTLEGROUNDS", Publisher = "KRAFTON", AntiCheat = "BattlEye", SteamAppId = 578080,
                    Exes = new[] { "TslGame.exe" },
                    ExePaths = new[] { "{steam}/PUBG/TslGame/Binaries/Win64/TslGame.exe" },
                    Actions =
                    {
                        GameAction.Priority(), GameAction.Gpu(), GameAction.Fso(),
                        GameAction.Config("vsync", vsyncOff, L.T("bUseVSync = False in GameUserSettings.ini", "bUseVSync = False in GameUserSettings.ini"),
                            "{localappdata}/TslGame/Saved/Config/WindowsNoEditor/GameUserSettings.ini", ConfigFormat.Ini,
                            new ConfigEntry(null, "bUseVSync", "False"))
                    }
                }
            };
        }
    }
}
