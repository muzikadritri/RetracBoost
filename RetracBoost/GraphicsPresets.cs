using System;
using System.Collections.Generic;
using System.Linq;

namespace RetracBoost
{
    /// <summary>
    /// Grafik-Presets für das Alt+L-Menü. Sie ändern die Grafikstufen, die das Spiel selbst in seine
    /// GameUserSettings.ini schreibt (Unreal-Engine-Spiele: Fortnite, VALORANT, PUBG). Läuft das Spiel,
    /// wird das Preset vorgemerkt und erst eingetragen, wenn das Spiel geschlossen ist, weil es die Datei
    /// beim Beenden selbst überschreibt. Das laufende Spiel wird nie angefasst.
    /// Die Werte vor dem ersten Preset werden gesichert; „Original“ schreibt sie zurück.
    /// </summary>
    static class GraphicsPresets
    {
        public const string Pretty = "pretty", Balanced = "balanced", Performance = "performance", Original = "original";

        const string Section = "ScalabilityGroups";
        const string RhiSection = "D3DRHIPreference";
        // Wie lange das Spiel schon beendet sein muss, bevor wir schreiben (es speichert beim Beenden)
        static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(6);

        static readonly Dictionary<string, string> Files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "fortnite", "{localappdata}/FortniteGame/Saved/Config/WindowsClient/GameUserSettings.ini" },
            { "valorant", "{localappdata}/VALORANT/Saved/Config/*/Windows/GameUserSettings.ini" },
            { "pubg", "{localappdata}/TslGame/Saved/Config/WindowsNoEditor/GameUserSettings.ini" }
        };

        static readonly string[] Groups =
        {
            "sg.ViewDistanceQuality", "sg.AntiAliasingQuality", "sg.ShadowQuality", "sg.GlobalIlluminationQuality",
            "sg.ReflectionQuality", "sg.PostProcessQuality", "sg.TextureQuality", "sg.EffectsQuality",
            "sg.FoliageQuality", "sg.ShadingQuality"
        };

        static readonly object Gate = new object();
        static readonly Dictionary<string, DateTime> notRunningSince = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        public static bool Supports(GameDef g) { return g != null && Files.ContainsKey(g.Id); }

        public static string Name(string preset)
        {
            switch (preset)
            {
                case Pretty: return L.T("Schön", "Pretty");
                case Balanced: return L.T("Ausgewogen", "Balanced");
                case Performance: return L.T("Leistung", "Performance");
                default: return "Original";
            }
        }

        /// <summary>Zuletzt eingetragenes Preset (oder null).</summary>
        public static string Current(GameDef g) { return g == null ? null : Store.Get("preset.current." + g.Id); }

        /// <summary>Vorgemerktes Preset, das beim nächsten Schließen des Spiels eingetragen wird (oder null).</summary>
        public static string Pending(GameDef g) { return g == null ? null : Store.Get("preset.pending." + g.Id); }

        /// <summary>Werte je Preset. Schön = Episch, Ausgewogen = Hoch, Leistung = niedrig mit voller Sichtweite.</summary>
        static List<ConfigEntry> Entries(string preset)
        {
            var list = new List<ConfigEntry>();
            foreach (var key in Groups)
            {
                string v;
                if (preset == Pretty) v = "3";
                else if (preset == Balanced) v = "2";
                else if (key == "sg.ViewDistanceQuality") v = "2";      // Gegner auf Entfernung sichtbar halten
                else if (key == "sg.TextureQuality") v = "1";
                else v = "0";
                list.Add(new ConfigEntry(Section, key, v));
            }
            list.Add(new ConfigEntry(Section, "sg.ResolutionQuality", "100"));
            return list;
        }

        /// <summary>
        /// Trägt das Preset ein oder merkt es vor, wenn das Spiel läuft. Liefert einen Satz für das Menü.
        /// </summary>
        public static string Choose(GameDef g, string preset)
        {
            if (!Supports(g)) return L.T("Für dieses Spiel gibt es keine Presets.", "No presets for this game.");
            lock (Gate)
            {
                if (g.IsRunning())
                {
                    Store.Set("preset.pending." + g.Id, preset);
                    notRunningSince.Remove(g.Id);
                    return string.Format(L.T("{0} wird eingetragen, sobald du {1} schließt. Gilt ab dem nächsten Start.",
                                             "{0} will be written once you close {1}. Applies from the next start."), Name(preset), g.Name);
                }
                Write(g, preset);
                Store.Set("preset.pending." + g.Id, null);
                return string.Format(L.T("{0} ist eingetragen. Gilt ab dem nächsten Start von {1}.",
                                         "{0} is written. Applies from the next start of {1}."), Name(preset), g.Name);
            }
        }

        /// <summary>Alle paar Sekunden aufrufen (Hintergrund-Thread): trägt vorgemerkte Presets nach dem Schließen ein.</summary>
        public static void Tick(IEnumerable<GameDef> games)
        {
            foreach (var g in games.Where(Supports))
            {
                lock (Gate)
                {
                    string pending = Pending(g);
                    if (pending == null) continue;
                    if (g.IsRunning()) { notRunningSince.Remove(g.Id); continue; }
                    DateTime since;
                    if (!notRunningSince.TryGetValue(g.Id, out since)) { notRunningSince[g.Id] = DateTime.UtcNow; continue; }
                    if (DateTime.UtcNow - since < ExitGrace) continue;
                    try
                    {
                        Write(g, pending);
                        Store.Set("preset.pending." + g.Id, null);
                    }
                    catch { }   // nächster Versuch beim nächsten Tick
                    notRunningSince.Remove(g.Id);
                }
            }
        }

        static void Write(GameDef g, string preset)
        {
            var files = GamePaths.Expand(Files[g.Id], g.GameDirs()).Take(16).ToList();
            if (files.Count == 0)
                throw new InvalidOperationException(string.Format(L.T("GameUserSettings.ini von {0} nicht gefunden. Starte das Spiel einmal.",
                                                                      "{0}'s GameUserSettings.ini not found. Start the game once."), g.Name));

            var entries = preset == Original ? null : Entries(preset);
            foreach (var f in files)
            {
                var doc = ConfigText.Read(f);
                string text = doc.Text;
                bool changed = false;

                var keys = Groups.Concat(new[] { "sg.ResolutionQuality" }).Select(k => new ConfigEntry(Section, k, null)).ToList();
                keys.Add(new ConfigEntry(RhiSection, "PreferredFeatureLevel", null));
                foreach (var e in keys)
                {
                    string cur = ConfigText.Get(text, ConfigFormat.Ini, e);
                    if (cur == null) continue;
                    string backup = BackupKey(g, f, e);

                    string target;
                    if (preset == Original)
                    {
                        target = Store.Get(backup);
                        if (target == null) continue;
                        Store.Set(backup, null);
                    }
                    else if (e.Section == RhiSection)
                    {
                        // Im Fortnite-Performance-Modus (es31) greifen hohe Stufen nicht. Für Schön und Ausgewogen auf DirectX-Grafik umstellen.
                        if (preset == Performance || !ConfigText.SameValue(cur, "es31")) continue;
                        target = "sm5";
                        if (Store.Get(backup) == null) Store.Set(backup, cur);
                    }
                    else
                    {
                        target = entries.First(x => x.Key == e.Key).Value;
                        if (Store.Get(backup) == null) Store.Set(backup, cur);
                    }

                    if (ConfigText.SameValue(cur, target)) continue;
                    text = ConfigText.Set(text, ConfigFormat.Ini, e, target);
                    changed = true;
                }

                if (!changed) continue;
                doc.Text = text;
                ConfigText.Write(f, doc);
            }
            Store.Set("preset.current." + g.Id, preset == Original ? null : preset);
        }

        static string BackupKey(GameDef g, string file, ConfigEntry e) { return "preset|" + g.Id + "|" + file + "|" + e.Section + "|" + e.Key; }
    }
}
