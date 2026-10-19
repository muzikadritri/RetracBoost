using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RetracBoost
{
    /// <summary>
    /// Holt das offizielle Bild eines Spiels und legt es im Cache ab (%LOCALAPPDATA%\RetracBoost\images).
    ///   Steam-Spiele: Store-Bild (header.jpg) vom Steam-CDN.
    ///   Andere:       og:image der offiziellen Seite, also das Bild, das der Hersteller selbst zum Teilen hinterlegt.
    ///   Plugins:      Bild aus dem ZIP, sonst wie oben.
    /// Ohne Internet bleibt das zuletzt geladene Bild stehen.
    /// </summary>
    static class GameImages
    {
        static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RetracBoost", "images");
        static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);
        const int MaxImageBytes = 10 * 1024 * 1024;
        const int MaxPageBytes = 3 * 1024 * 1024;

        sealed class Result
        {
            public ImageSource Image;
            public string Source;
        }

        public static string SteamUrl(int appId) { return "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/" + appId + "/header.jpg"; }

        static IEnumerable<string> SteamUrls(int appId)
        {
            yield return SteamUrl(appId);
            yield return "https://cdn.cloudflare.steamstatic.com/steam/apps/" + appId + "/header.jpg";
            yield return "https://cdn.akamai.steamstatic.com/steam/apps/" + appId + "/header.jpg";
        }

        /// <summary>Lädt das Bild; source ist danach die Herkunft (Datei oder URL), sonst null.</summary>
        public static Task<Tuple<ImageSource, string>> LoadAsync(GameDef g)
        {
            return Task.Run(() =>
            {
                var r = Load(g);
                return Tuple.Create(r == null ? null : r.Image, r == null ? null : r.Source);
            });
        }

        static Result Load(GameDef g)
        {
            if (!string.IsNullOrEmpty(g.ImageFile) && File.Exists(g.ImageFile))
            {
                var img = Decode(SafeRead(g.ImageFile));
                if (img != null) return new Result { Image = img, Source = L.T("Bild aus dem Plugin", "Image from the plugin") };
            }

            string cache = Path.Combine(Dir, Safe(g.Id) + ".img");
            string cacheSrc = cache + ".src";
            bool fresh = File.Exists(cache) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cache) < MaxAge;
            if (fresh)
            {
                var img = Decode(SafeRead(cache));
                if (img != null) return new Result { Image = img, Source = ReadText(cacheSrc) };
            }

            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }

            var candidates = new List<string>();
            if (g.SteamAppId > 0) candidates.AddRange(SteamUrls(g.SteamAppId));
            if (!string.IsNullOrEmpty(g.ImagePage))
            {
                foreach (var u in PageImages(g.ImagePage)) candidates.Add(u);
            }

            foreach (var url in candidates)
            {
                byte[] bytes = Download(url, MaxImageBytes);
                var img = Decode(bytes);
                if (img == null) continue;
                try
                {
                    Directory.CreateDirectory(Dir);
                    File.WriteAllBytes(cache, bytes);
                    File.WriteAllText(cacheSrc, url, Encoding.UTF8);
                }
                catch { }
                return new Result { Image = img, Source = url };
            }

            // offline: altes Bild ist besser als keins
            if (File.Exists(cache))
            {
                var img = Decode(SafeRead(cache));
                if (img != null) return new Result { Image = img, Source = ReadText(cacheSrc) };
            }
            return null;
        }

        /// <summary>og:image und twitter:image einer offiziellen Seite.</summary>
        static IEnumerable<string> PageImages(string pageUrl)
        {
            var list = new List<string>();
            byte[] bytes = Download(pageUrl, MaxPageBytes);
            if (bytes == null) return list;
            string html = Encoding.UTF8.GetString(bytes);
            Uri baseUri;
            if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out baseUri)) return list;

            foreach (var prop in new[] { "og:image:secure_url", "og:image", "twitter:image" })
            {
                string p = Regex.Escape(prop);
                foreach (var pattern in new[]
                {
                    "<meta[^>]+(?:property|name)\\s*=\\s*[\"']" + p + "[\"'][^>]*?content\\s*=\\s*[\"']([^\"']+)[\"']",
                    "<meta[^>]+content\\s*=\\s*[\"']([^\"']+)[\"'][^>]*?(?:property|name)\\s*=\\s*[\"']" + p + "[\"']"
                })
                {
                    foreach (Match m in Regex.Matches(html, pattern, RegexOptions.IgnoreCase))
                    {
                        Uri u;
                        string raw = WebUtility.HtmlDecode(m.Groups[1].Value.Trim());
                        if (!Uri.TryCreate(baseUri, raw, out u) || u.Scheme != Uri.UriSchemeHttps) continue;
                        if (!list.Contains(u.AbsoluteUri)) list.Add(u.AbsoluteUri);
                    }
                }
            }
            return list;
        }

        static byte[] Download(string url, int maxBytes)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) RetracBoost/" + AutoUpdater.CurrentVersion;
                req.Accept = "text/html,image/avif,image/webp,image/png,image/jpeg,*/*;q=0.8";
                req.Timeout = 10000;
                req.ReadWriteTimeout = 10000;
                req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    if (resp.ResponseUri.Scheme != Uri.UriSchemeHttps) return null;
                    using (var s = resp.GetResponseStream())
                    using (var ms = new MemoryStream())
                    {
                        var buf = new byte[81920];
                        int n;
                        while ((n = s.Read(buf, 0, buf.Length)) > 0)
                        {
                            ms.Write(buf, 0, n);
                            if (ms.Length > maxBytes) return null;
                        }
                        return ms.ToArray();
                    }
                }
            }
            catch { return null; }
        }

        /// <summary>Dekodiert ein Bild auf beliebigem Thread (eingefroren, also im UI nutzbar). null, wenn es kein Bild ist.</summary>
        public static ImageSource Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 16) return null;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bmp.DecodePixelWidth = 920;
                bmp.StreamSource = new MemoryStream(bytes);
                bmp.EndInit();
                if (bmp.PixelWidth < 16 || bmp.PixelHeight < 16) return null;
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        static byte[] SafeRead(string path)
        {
            try { return new FileInfo(path).Length > MaxImageBytes ? null : File.ReadAllBytes(path); }
            catch { return null; }
        }

        static string ReadText(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8).Trim() : null; }
            catch { return null; }
        }

        static string Safe(string id) { return Regex.Replace(id ?? "game", @"[^\w\.\-]", "_"); }

        /// <summary>Löscht das zwischengespeicherte Bild, z. B. wenn ein Plugin ersetzt oder entfernt wird.</summary>
        public static void Forget(string id)
        {
            string cache = Path.Combine(Dir, Safe(id) + ".img");
            try { File.Delete(cache); } catch { }
            try { File.Delete(cache + ".src"); } catch { }
        }

        /// <summary>Kurzer Herkunftstext für die Detailansicht.</summary>
        public static string Credit(GameDef g, string source)
        {
            if (string.IsNullOrEmpty(source)) return L.T("Bild konnte nicht geladen werden", "Image could not be loaded");
            Uri u;
            if (!Uri.TryCreate(source, UriKind.Absolute, out u)) return source;
            if (u.Host.EndsWith("steamstatic.com", StringComparison.OrdinalIgnoreCase)) return L.T("Bild: Steam-Store (Valve)", "Image: Steam store (Valve)");
            Uri page;
            string host = !string.IsNullOrEmpty(g.ImagePage) && Uri.TryCreate(g.ImagePage, UriKind.Absolute, out page) ? page.Host : u.Host;
            if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) host = host.Substring(4);
            return L.T("Bild: offizielle Seite ", "Image: official site ") + host;
        }
    }
}
