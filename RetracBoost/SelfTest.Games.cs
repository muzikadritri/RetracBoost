using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace RetracBoost
{
    /// <summary>
    /// Selbsttest für den Games-Tab. Braucht keine Administratorrechte und fasst keine Spieldateien an:
    /// alle Dateien entstehen in einem eigenen Testordner und werden danach gelöscht.
    ///   --selftest --games [--plugin beispiel.zip] [--out datei]
    /// </summary>
    static partial class SelfTest
    {
        static int RunGames(string outPath, string[] args)
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RetracBoost", "selftest-games-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(root);
                Line("Retrac Boost selftest (Games)  " + DateTime.Now.ToString("s"));

                GamesConfigText();
                GamesConfigFile(root);
                GamesPaths();
                GamesCatalog();
                GamesPlugins(root, args);
            }
            catch (Exception e) { Line("FEHLER: " + e); failures++; }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
            Line("");
            Line(failures == 0 ? "ERGEBNIS: alles bestanden" : "ERGEBNIS: " + failures + " Fehler");
            File.WriteAllText(outPath, log.ToString(), Encoding.UTF8);
            return failures == 0 ? 0 : 1;
        }

        static void GamesConfigText()
        {
            Line("");
            Line("[Konfig-Editor]");

            string ini = "[/Script/Engine.GameUserSettings]\r\nbUseVSync=True\r\nFrameRateLimit=0.000000\r\n; bUseVSync=True\r\n[ScalabilityGroups]\r\nsg.ShadowQuality=3\r\n";
            var vs = new ConfigEntry(null, "bUseVSync", "False");
            Check(ConfigText.Get(ini, ConfigFormat.Ini, vs) == "True", "ini: Wert lesen");
            string ini2 = ConfigText.Set(ini, ConfigFormat.Ini, vs, "False");
            Check(ini2.Contains("\r\nbUseVSync=False\r\n") && ini2.Contains("; bUseVSync=True"), "ini: Wert ersetzen, Kommentar und CRLF bleiben");
            Check(ConfigText.Get(ini, ConfigFormat.Ini, new ConfigEntry("ScalabilityGroups", "sg.ShadowQuality", "0")) == "3", "ini: Sektion beachtet");
            Check(ConfigText.Get(ini, ConfigFormat.Ini, new ConfigEntry("Andere", "sg.ShadowQuality", "0")) == null, "ini: falsche Sektion findet nichts");
            Check(ConfigText.Get(ini, ConfigFormat.Ini, new ConfigEntry(null, "bUse", "0")) == null, "ini: Teilname passt nicht");
            Check(ConfigText.Set(ini, ConfigFormat.Ini, new ConfigEntry(null, "Fehlt", "1"), "1") == ini, "ini: fehlender Schlüssel ändert nichts");

            string kv = "\"video.cfg\"\n{\n\t\"setting.mat_vsync\"\t\t\"1\"\n\t\"setting.r_low_latency\"\t\t\"0\"\n}\n";
            string kv2 = ConfigText.Set(kv, ConfigFormat.KeyValues, new ConfigEntry(null, "setting.mat_vsync", "0"), "0");
            Check(kv2.Contains("\t\"setting.mat_vsync\"\t\t\"0\"\n") && kv2.Contains("\"setting.r_low_latency\"\t\t\"0\""), "kv: Wert ersetzen");

            string colon = "version:3955\nrenderClouds:\"true\"\nmaxFps:120\nenableVsync:true\n";
            string c2 = ConfigText.Set(colon, ConfigFormat.Colon, new ConfigEntry(null, "renderClouds", "false"), "false");
            c2 = ConfigText.Set(c2, ConfigFormat.Colon, new ConfigEntry(null, "maxFps", "260"), "260");
            Check(c2.Contains("renderClouds:\"false\"\n") && c2.Contains("maxFps:260\n"), "colon: Anführungszeichen bleiben erhalten");
            Check(ConfigText.Get(colon, ConfigFormat.Colon, new ConfigEntry(null, "renderClouds", "")) == "true", "colon: Wert ohne Anführungszeichen gelesen");

            string xml = "<Settings>\r\n  <graphics>\r\n    <VSync value=\"1\" />\r\n  </graphics>\r\n</Settings>\r\n";
            string x2 = ConfigText.Set(xml, ConfigFormat.Xml, new ConfigEntry(null, "VSync", "0"), "0");
            Check(x2.Contains("    <VSync value=\"0\" />\r\n"), "xml: Attribut ersetzen");

            Check(ConfigText.SameValue("0", "0.000000") && ConfigText.SameValue("False", "false") && !ConfigText.SameValue("1", "0"), "Vergleich von Werten");
        }

        static void GamesConfigFile(string root)
        {
            Line("");
            Line("[Konfig-Datei an/aus]");
            string dir = Path.Combine(root, "game");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "GameUserSettings.ini");
            string original = "[/Script/Engine.GameUserSettings]\r\nbUseVSync=True\r\nResolutionSizeX=1920\r\n";
            var bytes = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes(original)).ToArray();   // UTF-16 wie bei manchen Unreal-Spielen
            File.WriteAllBytes(file, bytes);

            string pattern = "{localappdata}/RetracBoost/" + Path.GetFileName(root) + "/game/GameUserSettings.ini";
            var g = new GameDef { Id = "selftest-game", Name = "Selftest" };
            var action = GameAction.Config("vsync", null, null, pattern, ConfigFormat.Ini,
                new ConfigEntry(null, "bUseVSync", "False"), new ConfigEntry(null, "GibtEsNicht", "1"));
            g.Actions.Add(action);
            var t = action.CreateTweak(g);

            Check(t.Relevant, "Datei über Platzhalter gefunden");
            Check(!t.IsApplied(), "vorher nicht angewendet");
            t.Apply();
            Check(t.IsApplied(), "nach Anwenden aktiv");
            var after = File.ReadAllBytes(file);
            string text = Encoding.Unicode.GetString(after, 2, after.Length - 2);
            Check(after[0] == 0xFF && after[1] == 0xFE && text.Contains("bUseVSync=False\r\n") && !text.Contains("GibtEsNicht"), "UTF-16 bleibt, fehlender Schlüssel wird nicht angelegt");
            Check(t.WeApplied, "Sicherung vorhanden");
            t.Revert();
            Check(File.ReadAllBytes(file).SequenceEqual(bytes), "nach Zurücksetzen bytegleich");
            Check(!t.WeApplied, "Sicherung aufgeräumt");

            File.WriteAllText(file, "[A]\r\nNurAnderes=1\r\n");
            bool threw = false;
            try { t.Apply(); } catch (InvalidOperationException) { threw = true; }
            Check(threw, "Meldung, wenn keiner der Schlüssel in der Datei steht");
        }

        static void GamesPaths()
        {
            Line("");
            Line("[Pfadmuster]");
            string token; string[] parts;
            Check(GamePaths.TryParse("{steam}/Counter-Strike Global Offensive/game/bin/win64/cs2.exe", out token, out parts) && token == "steam" && parts.Length == 5, "gültiges Muster");
            Check(GamePaths.TryParse("{steamroot}/userdata/*/730/local/cfg/cs2_video.txt", out token, out parts), "Muster mit *");
            Check(!GamePaths.TryParse("{appdata}/../Windows/x.ini", out token, out parts), ".. abgelehnt");
            Check(!GamePaths.TryParse("C:/Windows/win.ini", out token, out parts), "Pfad ohne Platzhalter abgelehnt");
            Check(!GamePaths.TryParse("{system}/x.ini", out token, out parts), "unbekannter Platzhalter abgelehnt");
            Check(!GamePaths.TryParse("{appdata}/a*b/x.ini", out token, out parts), "halber Platzhalter * abgelehnt");
        }

        static void GamesCatalog()
        {
            Line("");
            Line("[Eingebaute Spiele]");
            var games = BuiltInGames.All();
            Check(games.Count == 8, "8 Spiele");
            Check(games.Select(x => x.Id).Distinct().Count() == games.Count, "ids eindeutig");
            foreach (var g in games)
            {
                Check(g.SteamAppId > 0 || !string.IsNullOrEmpty(g.ImagePage), g.Name + ": Bildquelle angegeben");
                foreach (var p in g.ExePaths)
                {
                    string token; string[] parts;
                    Check(GamePaths.TryParse(p, out token, out parts) && g.Exes.Contains(parts.Last(), StringComparer.OrdinalIgnoreCase), g.Name + ": Pfad gültig " + p);
                }
                try { g.Refresh(); } catch (Exception e) { Check(false, g.Name + ": Suche " + e.Message); }
                Line("  " + g.Name + ": " + (g.FoundExes.Count == 0 ? "nicht installiert" : string.Join(", ", g.FoundExes)));
                foreach (var a in g.Actions)
                {
                    var t = a.CreateTweak(g);
                    if (t == null) { Check(a.Kind == GameActionKind.LaunchOptions, g.Name + ": nur Startoptionen ohne Schalter"); continue; }
                    bool applied = false;
                    try { applied = t.Relevant && t.IsApplied(); t.Snapshot(); }
                    catch (Exception e) { Check(false, g.Name + " / " + t.Title + ": " + e.Message); }
                    Line("    " + t.Title.PadRight(32) + " relevant=" + t.Relevant + " applied=" + applied);
                }
            }
        }

        static void GamesPlugins(string root, string[] args)
        {
            Line("");
            Line("[Plugins]");
            var builtIn = new HashSet<string>(BuiltInGames.All().Select(g => g.Id), StringComparer.OrdinalIgnoreCase);
            var none = new List<GameDef>();

            int pi = Array.IndexOf(args, "--plugin");
            if (pi >= 0 && pi + 1 < args.Length)
            {
                try
                {
                    var pkg = GamePlugins.Inspect(args[pi + 1], none, builtIn);
                    Check(pkg.Game.Actions.Count > 0, "Beispiel-Plugin gelesen: " + pkg.Game.Name + " (" + pkg.Game.Actions.Count + " Optimierungen)");
                    foreach (var a in pkg.Game.Actions) Line("    " + a.Describe(pkg.Game));
                }
                catch (Exception e) { Check(false, "Beispiel-Plugin: " + e.Message); }
            }

            string good = "{\"format\":1,\"id\":\"test-spiel\",\"name\":\"Test\",\"exes\":[\"Test.exe\"],\"optimizations\":[{\"type\":\"priority\"}]}";
            Expect(root, "gültiges Minimal-Plugin", true, builtIn, new Dictionary<string, string> { { "manifest.json", good } });
            Expect(root, "Ordner im ZIP wird übersprungen", true, builtIn, new Dictionary<string, string> { { "test/manifest.json", good } });
            Expect(root, "DLL im ZIP abgelehnt", false, builtIn, new Dictionary<string, string> { { "manifest.json", good }, { "hook.dll", "MZ" } });
            Expect(root, "Unterordner abgelehnt", false, builtIn, new Dictionary<string, string> { { "manifest.json", good }, { "a/b.txt", "x" } });
            Expect(root, "unbekannte Aktion abgelehnt", false, builtIn, new Dictionary<string, string> { { "manifest.json", good.Replace("\"priority\"", "\"run\"") } });
            Expect(root, "eingebaute id abgelehnt", false, builtIn, new Dictionary<string, string> { { "manifest.json", good.Replace("test-spiel", "cs2") } });
            Expect(root, "Konfig außerhalb der Spieldaten abgelehnt", false, builtIn, new Dictionary<string, string> { { "manifest.json",
                "{\"format\":1,\"id\":\"x-spiel\",\"name\":\"X\",\"optimizations\":[{\"type\":\"config\",\"file\":\"{programfiles}/x/a.ini\",\"format\":\"ini\",\"set\":[{\"key\":\"a\",\"value\":1}]}]}" } });
            Expect(root, "falsche Dateiendung abgelehnt", false, builtIn, new Dictionary<string, string> { { "manifest.json",
                "{\"format\":1,\"id\":\"x-spiel\",\"name\":\"X\",\"optimizations\":[{\"type\":\"config\",\"file\":\"{appdata}/x/run.bat\",\"format\":\"ini\",\"set\":[{\"key\":\"a\",\"value\":1}]}]}" } });
            Expect(root, "fehlendes Bild abgelehnt", false, builtIn, new Dictionary<string, string> { { "manifest.json", good.Replace("\"exes\"", "\"image\":\"cover.png\",\"exes\"") } });
        }

        static void Expect(string root, string what, bool ok, ISet<string> builtIn, Dictionary<string, string> files)
        {
            string zip = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip");
            using (var fs = new FileStream(zip, FileMode.Create))
            using (var za = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (var kv in files)
                {
                    var e = za.CreateEntry(kv.Key);
                    using (var w = new StreamWriter(e.Open(), new UTF8Encoding(false))) w.Write(kv.Value);
                }
            }
            string error = null;
            try { GamePlugins.Inspect(zip, new List<GameDef>(), builtIn); }
            catch (Exception e) { error = e.Message; }
            Check((error == null) == ok, what + (error == null ? "" : "  (" + error + ")"));
        }
    }
}
