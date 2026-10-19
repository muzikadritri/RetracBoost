using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;

namespace RetracBoost
{
    /// <summary>
    /// Spiele-Plugins als ZIP: manifest.json + optional ein Bild. Ein Plugin beschreibt nur, welche der fest eingebauten
    /// Aktionen es nutzt (siehe docs/plugins.md). Es wird kein Code aus dem Plugin ausgeführt, und alle Pfade müssen mit
    /// einem bekannten Platzhalter beginnen. Installiert wird nach %LOCALAPPDATA%\RetracBoost\plugins\&lt;id&gt;.
    /// </summary>
    static class GamePlugins
    {
        public const int FormatVersion = 1;
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RetracBoost", "plugins");

        const long MaxZipBytes = 20L * 1024 * 1024;
        const long MaxEntryBytes = 8L * 1024 * 1024;
        const long MaxTotalBytes = 16L * 1024 * 1024;
        const int MaxEntries = 16;
        const int MaxManifestBytes = 256 * 1024;

        static readonly Regex IdRe = new Regex(@"^[a-z0-9][a-z0-9_\-]{1,39}$");
        static readonly Regex ImageNameRe = new Regex(@"^[\w\-\. ]{1,60}\.(png|jpg|jpeg)$", RegexOptions.IgnoreCase);
        static readonly Regex KeyRe = new Regex(@"^[\w\.\-:/ \[\]]{1,80}$");
        static readonly Regex ActionIdRe = new Regex(@"^[a-z0-9_\-]{1,24}$");
        static readonly string[] ConfigExtensions = { ".ini", ".txt", ".cfg", ".xml", ".conf" };
        static readonly string[] OtherAllowed = { ".txt", ".md" };
        public static readonly string[] ActionTypes = { "priority", "io_priority", "network_qos", "gpu_high_performance", "fullscreen_optimizations_off", "config", "launch_options" };

        public sealed class Package
        {
            public GameDef Game;
            public string ManifestJson;
            public byte[] ImageBytes;
            public string ImageName;
            public GameDef Existing;       // gleiches Plugin ist schon installiert
            public string ImageHost;       // Bild wird aus dem Internet geladen
        }

        sealed class PluginException : InvalidOperationException
        {
            public PluginException(string message) : base(message) { }
        }

        static PluginException Fail(string de, string en) { return new PluginException(L.T(de, en)); }

        // ------------------------------------------------------------------ Laden

        public static List<GameDef> LoadAll(ISet<string> takenIds, List<string> errors)
        {
            var list = new List<GameDef>();
            if (!Directory.Exists(Dir)) return list;
            string[] dirs;
            try { dirs = Directory.GetDirectories(Dir); } catch { return list; }
            foreach (var d in dirs.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                string folder = Path.GetFileName(d);
                if (folder.StartsWith(".")) continue;   // Reste einer abgebrochenen Installation
                try
                {
                    string manifest = Path.Combine(d, "manifest.json");
                    if (!File.Exists(manifest)) throw Fail("manifest.json fehlt", "manifest.json is missing");
                    if (new FileInfo(manifest).Length > MaxManifestBytes) throw Fail("manifest.json ist zu groß", "manifest.json is too large");
                    var g = Parse(File.ReadAllText(manifest, Encoding.UTF8));
                    if (!string.Equals(g.Id, folder, StringComparison.OrdinalIgnoreCase))
                        throw Fail("Ordnername passt nicht zur id „" + g.Id + "“", "folder name does not match id “" + g.Id + "”");
                    if (takenIds.Contains(g.Id)) throw Fail("id „" + g.Id + "“ ist schon vergeben", "id “" + g.Id + "” is already taken");
                    g.PluginDir = d;
                    if (g.ImageFile != null) g.ImageFile = Path.Combine(d, g.ImageFile);
                    takenIds.Add(g.Id);
                    list.Add(g);
                }
                catch (Exception ex)
                {
                    errors.Add(folder + ": " + ex.Message);
                }
            }
            return list;
        }

        // ------------------------------------------------------------------ ZIP prüfen

        /// <summary>Liest und prüft ein Plugin-ZIP, ohne etwas zu installieren. Wirft mit lesbarer Meldung.</summary>
        public static Package Inspect(string zipPath, ICollection<GameDef> installed, ISet<string> builtInIds)
        {
            var info = new FileInfo(zipPath);
            if (!info.Exists) throw Fail("Datei nicht gefunden.", "File not found.");
            if (info.Length > MaxZipBytes) throw Fail("Das ZIP ist größer als 20 MB.", "The ZIP is larger than 20 MB.");

            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            ZipArchive zip;
            try { zip = ZipFile.OpenRead(zipPath); }
            catch { throw Fail("Die Datei ist kein gültiges ZIP.", "The file is not a valid ZIP."); }
            using (zip)
            {
                var entries = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
                if (entries.Count == 0) throw Fail("Das ZIP ist leer.", "The ZIP is empty.");
                if (entries.Count > MaxEntries) throw Fail("Zu viele Dateien im ZIP (höchstens " + MaxEntries + ").", "Too many files in the ZIP (max " + MaxEntries + ").");

                // Liegt alles in einem gemeinsamen Ordner (ZIP vom Ordner statt vom Inhalt), wird der Ordner ignoriert.
                var names = entries.Select(e => e.FullName.Replace('\\', '/')).ToList();
                string prefix = "";
                int slash = names[0].IndexOf('/');
                if (slash > 0)
                {
                    string candidate = names[0].Substring(0, slash + 1);
                    if (names.All(n => n.StartsWith(candidate, StringComparison.Ordinal))) prefix = candidate;
                }

                long total = 0;
                for (int i = 0; i < entries.Count; i++)
                {
                    string name = names[i].Substring(prefix.Length);
                    if (name.Contains("/") || name.Contains("..") || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                        throw Fail("Unterordner sind im Plugin nicht erlaubt: " + names[i], "Subfolders are not allowed in a plugin: " + names[i]);
                    string ext = Path.GetExtension(name).ToLowerInvariant();
                    bool allowed = name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) || ImageNameRe.IsMatch(name) || OtherAllowed.Contains(ext);
                    if (!allowed)
                        throw Fail("Nicht erlaubte Datei im Plugin: " + name + ". Erlaubt sind manifest.json, PNG/JPG-Bilder und Text.",
                                   "File not allowed in a plugin: " + name + ". Allowed are manifest.json, PNG/JPG images and text.");
                    if (entries[i].Length > MaxEntryBytes) throw Fail(name + " ist größer als 8 MB.", name + " is larger than 8 MB.");
                    total += entries[i].Length;
                    if (total > MaxTotalBytes) throw Fail("Der Inhalt ist größer als 16 MB.", "The content is larger than 16 MB.");
                    if (files.ContainsKey(name)) throw Fail("Datei doppelt im ZIP: " + name, "Duplicate file in the ZIP: " + name);
                    files[name] = ReadCapped(entries[i], MaxEntryBytes);
                }
            }

            byte[] manifestBytes;
            if (!files.TryGetValue("manifest.json", out manifestBytes)) throw Fail("manifest.json fehlt im ZIP.", "manifest.json is missing from the ZIP.");
            if (manifestBytes.Length > MaxManifestBytes) throw Fail("manifest.json ist zu groß.", "manifest.json is too large.");
            string json = DecodeText(manifestBytes);
            var game = Parse(json);

            if (builtInIds.Contains(game.Id)) throw Fail("Die id „" + game.Id + "“ gehört zu einem eingebauten Spiel.", "The id “" + game.Id + "” belongs to a built-in game.");

            var pkg = new Package { Game = game, ManifestJson = json };
            if (game.ImageFile != null)
            {
                byte[] img;
                if (!files.TryGetValue(game.ImageFile, out img))
                    throw Fail("Das Bild „" + game.ImageFile + "“ aus manifest.json fehlt im ZIP.", "The image “" + game.ImageFile + "” named in manifest.json is missing from the ZIP.");
                if (GameImages.Decode(img) == null) throw Fail("„" + game.ImageFile + "“ ist kein lesbares Bild.", "“" + game.ImageFile + "” is not a readable image.");
                pkg.ImageBytes = img;
                pkg.ImageName = game.ImageFile;
            }
            else if (game.SteamAppId > 0) pkg.ImageHost = "Steam (steamstatic.com)";
            else if (game.ImagePage != null) pkg.ImageHost = new Uri(game.ImagePage).Host;

            pkg.Existing = installed.FirstOrDefault(g => g.IsPlugin && string.Equals(g.Id, game.Id, StringComparison.OrdinalIgnoreCase));
            return pkg;
        }

        static byte[] ReadCapped(ZipArchiveEntry e, long max)
        {
            using (var s = e.Open())
            using (var ms = new MemoryStream())
            {
                var buf = new byte[81920];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    ms.Write(buf, 0, n);
                    if (ms.Length > max) throw Fail(e.Name + " ist zu groß.", e.Name + " is too large.");
                }
                return ms.ToArray();
            }
        }

        static string DecodeText(byte[] b)
        {
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);
            if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
            return Encoding.UTF8.GetString(b);
        }

        // ------------------------------------------------------------------ Installieren / Entfernen

        public static void Install(Package p)
        {
            Directory.CreateDirectory(Dir);
            string target = Path.Combine(Dir, p.Game.Id);
            string temp = Path.Combine(Dir, "." + p.Game.Id + "." + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(temp);
            try
            {
                File.WriteAllText(Path.Combine(temp, "manifest.json"), p.ManifestJson, new UTF8Encoding(false));
                if (p.ImageBytes != null) File.WriteAllBytes(Path.Combine(temp, p.ImageName), p.ImageBytes);
                if (Directory.Exists(target))
                {
                    string old = temp + ".old";
                    Directory.Move(target, old);
                    Directory.Move(temp, target);
                    try { Directory.Delete(old, true); } catch { }
                }
                else Directory.Move(temp, target);
            }
            catch
            {
                try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { }
                throw;
            }
            p.Game.PluginDir = target;
            if (p.ImageName != null) p.Game.ImageFile = Path.Combine(target, p.ImageName);
        }

        public static void Remove(GameDef g)
        {
            if (!g.IsPlugin || string.IsNullOrEmpty(g.PluginDir)) return;
            string full = Path.GetFullPath(g.PluginDir);
            if (!full.StartsWith(Path.GetFullPath(Dir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }

        // ------------------------------------------------------------------ manifest.json

        public static GameDef Parse(string json)
        {
            Dictionary<string, object> root;
            try
            {
                root = MiniJson.Parse(json) as Dictionary<string, object>;
            }
            catch (Exception ex) { throw Fail("manifest.json ist kein gültiges JSON: " + ex.Message, "manifest.json is not valid JSON: " + ex.Message); }
            if (root == null) throw Fail("manifest.json muss ein Objekt sein.", "manifest.json must be an object.");

            object fmt;
            if (!root.TryGetValue("format", out fmt) || !(fmt is int) || (int)fmt != FormatVersion)
                throw Fail("\"format\" muss " + FormatVersion + " sein.", "\"format\" must be " + FormatVersion + ".");

            var g = new GameDef { IsPlugin = true };
            g.Id = Str(root, "id", 40, true);
            if (!IdRe.IsMatch(g.Id)) throw Fail("\"id\" darf nur a-z, 0-9, - und _ enthalten (2 bis 40 Zeichen).", "\"id\" may only contain a-z, 0-9, - and _ (2 to 40 characters).");
            g.Name = Text(root, "name", 60, true);
            g.Version = Str(root, "version", 20, false) ?? "1.0";
            g.Author = Str(root, "author", 60, false);
            g.Description = Text(root, "description", 300, false);
            g.Publisher = Str(root, "publisher", 60, false);
            g.AntiCheat = Str(root, "anti_cheat", 60, false);

            g.Exes = StrArray(root, "exes", 8).ToArray();
            foreach (var e in g.Exes)
                if (!GamePriorityTweak.SafeExe.IsMatch(e)) throw Fail("Ungültiger Exe-Name: " + e, "Invalid exe name: " + e);

            var paths = StrArray(root, "paths", 16);
            foreach (var p in paths)
            {
                string token; string[] parts;
                if (!GamePaths.TryParse(p, out token, out parts) || token == "game")
                    throw Fail("Ungültiger Pfad: " + p + ". Er muss mit einem Platzhalter wie {steam} beginnen.", "Invalid path: " + p + ". It must start with a placeholder like {steam}.");
                if (!g.Exes.Contains(parts[parts.Length - 1], StringComparer.OrdinalIgnoreCase))
                    throw Fail("Pfad endet nicht auf eine Exe aus \"exes\": " + p, "Path does not end in an exe listed in \"exes\": " + p);
            }
            g.ExePaths = paths.ToArray();

            object appId;
            if (root.TryGetValue("steam_appid", out appId))
            {
                if (!(appId is int) || (int)appId <= 0) throw Fail("\"steam_appid\" muss eine positive Zahl sein.", "\"steam_appid\" must be a positive number.");
                g.SteamAppId = (int)appId;
            }
            string page = Str(root, "image_page", 300, false);
            if (page != null)
            {
                Uri u;
                if (!Uri.TryCreate(page, UriKind.Absolute, out u) || u.Scheme != Uri.UriSchemeHttps)
                    throw Fail("\"image_page\" muss eine https-Adresse sein.", "\"image_page\" must be an https address.");
                g.ImagePage = u.AbsoluteUri;
            }
            string image = Str(root, "image", 64, false);
            if (image != null)
            {
                if (!ImageNameRe.IsMatch(image)) throw Fail("\"image\" muss eine PNG- oder JPG-Datei im ZIP sein.", "\"image\" must be a PNG or JPG file in the ZIP.");
                g.ImageFile = image;
            }

            object raw;
            if (!root.TryGetValue("optimizations", out raw) || !(raw is object[]))
                throw Fail("\"optimizations\" fehlt oder ist keine Liste.", "\"optimizations\" is missing or not a list.");
            var arr = (object[])raw;
            if (arr.Length == 0 || arr.Length > 16) throw Fail("\"optimizations\" braucht 1 bis 16 Einträge.", "\"optimizations\" needs 1 to 16 entries.");
            var keys = new HashSet<string>();
            for (int i = 0; i < arr.Length; i++)
            {
                var o = arr[i] as Dictionary<string, object>;
                if (o == null) throw Fail("Eintrag " + (i + 1) + " in \"optimizations\" ist kein Objekt.", "Entry " + (i + 1) + " in \"optimizations\" is not an object.");
                var a = ParseAction(o, g, i);
                if (!keys.Add(a.Key)) throw Fail("Doppelte Optimierungs-id: " + a.Key, "Duplicate optimisation id: " + a.Key);
                g.Actions.Add(a);
            }
            return g;
        }

        static GameAction ParseAction(Dictionary<string, object> o, GameDef g, int index)
        {
            string type = Str(o, "type", 40, true);
            string where = L.T(" (Optimierung " + (index + 1) + ")", " (optimisation " + (index + 1) + ")");
            string id = Str(o, "id", 24, false);
            if (id != null && !ActionIdRe.IsMatch(id)) throw Fail("Ungültige id" + where, "Invalid id" + where);
            string key = "p-" + (id ?? (index + 1).ToString());
            string title = Text(o, "title", 80, false);
            string summary = Text(o, "summary", 200, false);

            Action needExes = () =>
            {
                if (g.Exes.Length == 0) throw Fail("\"" + type + "\" braucht mindestens eine Exe in \"exes\"" + where, "\"" + type + "\" needs at least one exe in \"exes\"" + where);
            };

            switch (type)
            {
                case "priority":
                    {
                        needExes();
                        string level = Str(o, "level", 20, false) ?? "high";
                        if (level != "high" && level != "abovenormal") throw Fail("\"level\" muss high oder abovenormal sein" + where, "\"level\" must be high or abovenormal" + where);
                        return new GameAction { Kind = GameActionKind.Priority, Key = key, Level = level, Title = title, Summary = summary };
                    }
                case "io_priority":
                    needExes();
                    return new GameAction { Kind = GameActionKind.IoPriority, Key = key, Title = title, Summary = summary };
                case "network_qos":
                    needExes();
                    return new GameAction { Kind = GameActionKind.NetworkQos, Key = key, Title = title, Summary = summary };
                case "gpu_high_performance":
                    needExes();
                    return new GameAction { Kind = GameActionKind.GpuHighPerformance, Key = key, Title = title, Summary = summary };
                case "fullscreen_optimizations_off":
                    needExes();
                    return new GameAction { Kind = GameActionKind.FullscreenOptimizationsOff, Key = key, Title = title, Summary = summary };
                case "config":
                    {
                        string file = Str(o, "file", 260, true);
                        string token; string[] parts;
                        if (!GamePaths.TryParse(file, out token, out parts) || !GamePaths.ConfigTokens.Contains(token))
                            throw Fail("\"file\" muss mit {appdata}, {localappdata}, {documents}, {savedgames}, {steam}, {steamroot}, {epic}, {riot} oder {game} beginnen" + where,
                                       "\"file\" must start with {appdata}, {localappdata}, {documents}, {savedgames}, {steam}, {steamroot}, {epic}, {riot} or {game}" + where);
                        if (!ConfigExtensions.Contains(Path.GetExtension(parts[parts.Length - 1]).ToLowerInvariant()))
                            throw Fail("Nur .ini, .txt, .cfg, .xml und .conf dürfen geändert werden" + where, "Only .ini, .txt, .cfg, .xml and .conf may be changed" + where);
                        ConfigFormat format;
                        switch (Str(o, "format", 10, true))
                        {
                            case "ini": format = ConfigFormat.Ini; break;
                            case "kv": format = ConfigFormat.KeyValues; break;
                            case "colon": format = ConfigFormat.Colon; break;
                            case "xml": format = ConfigFormat.Xml; break;
                            default: throw Fail("\"format\" muss ini, kv, colon oder xml sein" + where, "\"format\" must be ini, kv, colon or xml" + where);
                        }
                        object setRaw;
                        if (!o.TryGetValue("set", out setRaw) || !(setRaw is object[]) || ((object[])setRaw).Length == 0 || ((object[])setRaw).Length > 32)
                            throw Fail("\"set\" braucht 1 bis 32 Einträge" + where, "\"set\" needs 1 to 32 entries" + where);
                        var a = new GameAction { Kind = GameActionKind.Config, Key = key, File = file, Format = format, Title = title, Summary = summary };
                        foreach (var item in (object[])setRaw)
                        {
                            var d = item as Dictionary<string, object>;
                            if (d == null) throw Fail("Eintrag in \"set\" ist kein Objekt" + where, "Entry in \"set\" is not an object" + where);
                            string k = Str(d, "key", 80, true);
                            string v = ValueText(d, "value");
                            string section = Str(d, "section", 120, false);
                            if (!KeyRe.IsMatch(k)) throw Fail("Ungültiger Schlüssel „" + k + "“" + where, "Invalid key “" + k + "”" + where);
                            if (v.Length > 200 || v.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) throw Fail("Ungültiger Wert für " + k + where, "Invalid value for " + k + where);
                            if ((format == ConfigFormat.KeyValues || format == ConfigFormat.Xml) && v.IndexOfAny(new[] { '"', '<', '>' }) >= 0)
                                throw Fail("Wert für " + k + " darf keine \" < > enthalten" + where, "Value for " + k + " must not contain \" < >" + where);
                            if (section != null && (section.Contains("]") || section.IndexOfAny(new[] { '\r', '\n' }) >= 0))
                                throw Fail("Ungültige section für " + k + where, "Invalid section for " + k + where);
                            a.Entries.Add(new ConfigEntry(section, k, v));
                        }
                        return a;
                    }
                case "launch_options":
                    {
                        string launcher = Str(o, "launcher", 10, false) ?? "other";
                        if (!new[] { "steam", "epic", "ea", "riot", "other" }.Contains(launcher))
                            throw Fail("\"launcher\" muss steam, epic, ea, riot oder other sein" + where, "\"launcher\" must be steam, epic, ea, riot or other" + where);
                        string value = Str(o, "value", 200, true);
                        if (value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) throw Fail("Ungültige Startoption" + where, "Invalid launch option" + where);
                        return new GameAction { Kind = GameActionKind.LaunchOptions, Key = key, Launcher = launcher, Value = value, Title = title, Summary = summary };
                    }
            }
            throw Fail("Unbekannter Typ „" + type + "“" + where + ". Erlaubt: " + string.Join(", ", ActionTypes),
                       "Unknown type “" + type + "”" + where + ". Allowed: " + string.Join(", ", ActionTypes));
        }

        // ------------------------------------------------------------------ JSON-Helfer

        static string Clean(string s, int max, string field)
        {
            s = s.Trim();
            if (s.Length > max) throw Fail("\"" + field + "\" ist länger als " + max + " Zeichen.", "\"" + field + "\" is longer than " + max + " characters.");
            if (s.Any(c => char.IsControl(c))) throw Fail("\"" + field + "\" enthält Steuerzeichen.", "\"" + field + "\" contains control characters.");
            return s;
        }

        static string Str(Dictionary<string, object> d, string key, int max, bool required)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null)
            {
                if (required) throw Fail("\"" + key + "\" fehlt.", "\"" + key + "\" is missing.");
                return null;
            }
            var s = v as string;
            if (s == null) throw Fail("\"" + key + "\" muss Text sein.", "\"" + key + "\" must be text.");
            s = Clean(s, max, key);
            if (s.Length == 0)
            {
                if (required) throw Fail("\"" + key + "\" ist leer.", "\"" + key + "\" is empty.");
                return null;
            }
            return s;
        }

        /// <summary>Text oder {"de": "...", "en": "..."}.</summary>
        static string Text(Dictionary<string, object> d, string key, int max, bool required)
        {
            object v;
            if (d.TryGetValue(key, out v) && v is Dictionary<string, object>)
            {
                var t = (Dictionary<string, object>)v;
                string de = Str(t, "de", max, false), en = Str(t, "en", max, false);
                string pick = L.T(de ?? en, en ?? de);
                if (pick == null && required) throw Fail("\"" + key + "\" braucht \"de\" oder \"en\".", "\"" + key + "\" needs \"de\" or \"en\".");
                return pick;
            }
            return Str(d, key, max, required);
        }

        /// <summary>Werte dürfen als Text, Zahl oder true/false angegeben werden.</summary>
        static string ValueText(Dictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) throw Fail("\"" + key + "\" fehlt.", "\"" + key + "\" is missing.");
            if (v is string) return Clean((string)v, 200, key);
            if (v is bool) return (bool)v ? "true" : "false";
            if (v is int || v is long || v is decimal || v is double)
                return Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
            throw Fail("\"" + key + "\" muss Text, Zahl oder true/false sein.", "\"" + key + "\" must be text, a number or true/false.");
        }

        static List<string> StrArray(Dictionary<string, object> d, string key, int maxItems)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return new List<string>();
            var arr = v as object[];
            if (arr == null) throw Fail("\"" + key + "\" muss eine Liste sein.", "\"" + key + "\" must be a list.");
            if (arr.Length > maxItems) throw Fail("\"" + key + "\" hat mehr als " + maxItems + " Einträge.", "\"" + key + "\" has more than " + maxItems + " entries.");
            var list = new List<string>();
            foreach (var x in arr)
            {
                var s = x as string;
                if (s == null) throw Fail("\"" + key + "\" darf nur Text enthalten.", "\"" + key + "\" may only contain text.");
                list.Add(Clean(s, 260, key));
            }
            return list;
        }
    }

    /// <summary>
    /// Kleiner, strenger JSON-Leser für manifest.json. Ergebnis: Dictionary&lt;string, object&gt;, object[], string,
    /// int (ganze Zahlen), double, bool oder null. Begrenzt die Verschachtelungstiefe.
    /// </summary>
    static class MiniJson
    {
        const int MaxDepth = 16;

        public static object Parse(string text)
        {
            int i = 0;
            var v = Value(text, ref i, 0);
            Skip(text, ref i);
            if (i != text.Length) throw Error(text, i, "Zeichen nach dem Ende");
            return v;
        }

        static FormatException Error(string t, int i, string what)
        {
            int line = 1;
            for (int k = 0; k < i && k < t.Length; k++) if (t[k] == '\n') line++;
            return new FormatException(what + " (Zeile " + line + ")");
        }

        static void Skip(string t, ref int i)
        {
            while (i < t.Length && (t[i] == ' ' || t[i] == '\t' || t[i] == '\r' || t[i] == '\n' || t[i] == '\uFEFF')) i++;
        }

        static object Value(string t, ref int i, int depth)
        {
            if (depth > MaxDepth) throw Error(t, i, "zu tief verschachtelt");
            Skip(t, ref i);
            if (i >= t.Length) throw Error(t, i, "unerwartetes Ende");
            char c = t[i];
            if (c == '{')
            {
                i++;
                var d = new Dictionary<string, object>(StringComparer.Ordinal);
                Skip(t, ref i);
                if (i < t.Length && t[i] == '}') { i++; return d; }
                while (true)
                {
                    Skip(t, ref i);
                    if (i >= t.Length || t[i] != '"') throw Error(t, i, "Schlüssel in Anführungszeichen erwartet");
                    string key = Str(t, ref i);
                    Skip(t, ref i);
                    if (i >= t.Length || t[i] != ':') throw Error(t, i, "':' erwartet");
                    i++;
                    if (d.ContainsKey(key)) throw Error(t, i, "Schlüssel doppelt: " + key);
                    d[key] = Value(t, ref i, depth + 1);
                    Skip(t, ref i);
                    if (i < t.Length && t[i] == ',') { i++; continue; }
                    if (i < t.Length && t[i] == '}') { i++; return d; }
                    throw Error(t, i, "',' oder '}' erwartet");
                }
            }
            if (c == '[')
            {
                i++;
                var list = new List<object>();
                Skip(t, ref i);
                if (i < t.Length && t[i] == ']') { i++; return list.ToArray(); }
                while (true)
                {
                    list.Add(Value(t, ref i, depth + 1));
                    Skip(t, ref i);
                    if (i < t.Length && t[i] == ',') { i++; continue; }
                    if (i < t.Length && t[i] == ']') { i++; return list.ToArray(); }
                    throw Error(t, i, "',' oder ']' erwartet");
                }
            }
            if (c == '"') return Str(t, ref i);
            if (Word(t, ref i, "true")) return true;
            if (Word(t, ref i, "false")) return false;
            if (Word(t, ref i, "null")) return null;
            if (c == '-' || (c >= '0' && c <= '9')) return Number(t, ref i);
            throw Error(t, i, "unerwartetes Zeichen '" + c + "'");
        }

        static bool Word(string t, ref int i, string w)
        {
            if (string.CompareOrdinal(t, i, w, 0, w.Length) != 0) return false;
            i += w.Length;
            return true;
        }

        static object Number(string t, ref int i)
        {
            int start = i;
            if (t[i] == '-') i++;
            while (i < t.Length && "0123456789.eE+-".IndexOf(t[i]) >= 0) i++;
            string s = t.Substring(start, i - start);
            int n;
            if (int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n)) return n;
            double d;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            throw Error(t, start, "ungültige Zahl");
        }

        static string Str(string t, ref int i)
        {
            i++; // "
            var sb = new StringBuilder();
            while (i < t.Length)
            {
                char c = t[i++];
                if (c == '"') return sb.ToString();
                if (c < ' ') throw Error(t, i, "Steuerzeichen im Text");
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= t.Length) break;
                char e = t[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        int code;
                        if (i + 4 > t.Length || !int.TryParse(t.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                            throw Error(t, i, "ungültiges \\u");
                        sb.Append((char)code);
                        i += 4;
                        break;
                    default: throw Error(t, i, "ungültiges Escape");
                }
            }
            throw Error(t, i, "Text ohne Ende");
        }
    }
}
