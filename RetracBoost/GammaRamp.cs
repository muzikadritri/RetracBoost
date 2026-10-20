using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;

namespace RetracBoost
{
    /// <summary>Bildwerte für die Gamma-Kurve. Standard = keine Änderung.</summary>
    sealed class ColorSettings
    {
        public const double GammaMin = 0.6, GammaMax = 1.8;
        public const double BrightnessMin = -0.3, BrightnessMax = 0.3;
        public const double ContrastMin = 0.7, ContrastMax = 1.4;
        public const double WarmthMin = -1, WarmthMax = 1;
        public const double SaturationMin = 0, SaturationMax = 1;

        public double Gamma = 1;
        public double Brightness;      // -0.3 … +0.3
        public double Contrast = 1;
        public double Warmth;          // -1 kühl … +1 warm
        public double Saturation;      // 0 … 1 zusätzlich, über den Grafiktreiber (siehe Vibrance)

        /// <summary>true, wenn die Gamma-Kurve unverändert bleibt.</summary>
        public bool CurvesDefault
        {
            get { return Math.Abs(Gamma - 1) < 0.005 && Math.Abs(Brightness) < 0.005 && Math.Abs(Contrast - 1) < 0.005 && Math.Abs(Warmth) < 0.005; }
        }

        public bool IsDefault { get { return CurvesDefault && Saturation < 0.005; } }

        public ColorSettings Clone() { return (ColorSettings)MemberwiseClone(); }

        public static ColorSettings Load()
        {
            return new ColorSettings
            {
                Gamma = Read("color.gamma", 1, GammaMin, GammaMax),
                Brightness = Read("color.brightness", 0, BrightnessMin, BrightnessMax),
                Contrast = Read("color.contrast", 1, ContrastMin, ContrastMax),
                Warmth = Read("color.warmth", 0, WarmthMin, WarmthMax),
                Saturation = Read("color.saturation", 0, SaturationMin, SaturationMax)
            };
        }

        public void Save()
        {
            Write("color.gamma", Gamma);
            Write("color.brightness", Brightness);
            Write("color.contrast", Contrast);
            Write("color.warmth", Warmth);
            Write("color.saturation", Saturation);
        }

        static double Read(string key, double def, double min, double max)
        {
            double v;
            if (!double.TryParse(Store.Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out v) || double.IsNaN(v)) return def;
            return Math.Max(min, Math.Min(max, v));
        }

        static void Write(string key, double v) { Store.TrySet(key, v.ToString("0.###", CultureInfo.InvariantCulture)); }
    }

    /// <summary>
    /// Ändert Gamma, Helligkeit, Kontrast und Farbton über die Gamma-Kurve der Grafikkarte (SetDeviceGammaRamp),
    /// nur auf dem Bildschirm des Spiels und nur, solange das Spiel im Vordergrund ist. Nichts davon greift ins Spiel ein.
    /// Die Originalkurven werden vor der ersten Änderung gesichert, auch auf die Platte, damit sie nach einem Absturz
    /// beim nächsten Start zurückkommen. Beim Beenden der App wird automatisch zurückgesetzt.
    /// </summary>
    static class GammaRamp
    {
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr CreateDC(string driver, string device, string output, IntPtr initData);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern bool GetDeviceGammaRamp(IntPtr hdc, [Out] ushort[] ramp);
        [DllImport("gdi32.dll")] static extern bool SetDeviceGammaRamp(IntPtr hdc, [In] ushort[] ramp);

        const int RampSize = 256 * 3;
        const string ActiveKey = "color.active";
        const string OrigPrefix = "color.orig|";

        static readonly object Gate = new object();
        static readonly Dictionary<string, ushort[]> originals = new Dictionary<string, ushort[]>(StringComparer.OrdinalIgnoreCase);
        static bool hooked;

        /// <summary>true, solange eine geänderte Kurve aktiv ist.</summary>
        public static bool Active { get; private set; }

        /// <summary>Beim Start aufrufen: stellt Kurven wieder her, falls die App beim letzten Mal abgestürzt ist.</summary>
        public static void RecoverAfterCrash()
        {
            lock (Gate)
            {
                if (Store.Get(ActiveKey) != "1") return;
                foreach (var key in Store.Keys(OrigPrefix))
                {
                    var ramp = Decode(Store.Get(key));
                    if (ramp != null) Set(key.Substring(OrigPrefix.Length), ramp);
                    Store.TrySet(key, null);
                }
                Store.TrySet(ActiveKey, null);
            }
        }

        /// <summary>
        /// Setzt die Werte nur auf dem Bildschirm <paramref name="device"/> (z. B. \\.\DISPLAY1), auf dem das Spiel läuft.
        /// Andere Bildschirme, die vorher geändert waren, bekommen ihre Originalkurve zurück.
        /// Ohne Gerät oder mit Standardwerten wird alles zurückgesetzt. false, wenn Windows die Kurve ablehnt.
        /// </summary>
        public static bool Apply(ColorSettings s, string device)
        {
            if (s.CurvesDefault || string.IsNullOrEmpty(device)) { Restore(); return true; }
            lock (Gate)
            {
                EnsureHooked();
                foreach (var kv in originals.ToList())
                {
                    if (string.Equals(kv.Key, device, StringComparison.OrdinalIgnoreCase)) continue;
                    Set(kv.Key, kv.Value);
                    originals.Remove(kv.Key);
                    Store.TrySet(OrigPrefix + kv.Key, null);
                }
                if (!originals.ContainsKey(device))
                {
                    var orig = Get(device);
                    if (orig == null) return false;
                    originals[device] = orig;
                    Store.TrySet(OrigPrefix + device, Encode(orig));
                }
                Store.TrySet(ActiveKey, "1");
                Active = true;
                return Set(device, Build(s));
            }
        }

        /// <summary>Stellt die gesicherten Originalkurven wieder her.</summary>
        public static void Restore()
        {
            lock (Gate)
            {
                if (originals.Count == 0) return;
                foreach (var kv in originals)
                {
                    Set(kv.Key, kv.Value);
                    Store.TrySet(OrigPrefix + kv.Key, null);
                }
                originals.Clear();
                Store.TrySet(ActiveKey, null);
                Active = false;
            }
        }

        static void EnsureHooked()
        {
            if (hooked) return;
            hooked = true;
            // Läuft auch bei Environment.Exit (z. B. nach einem Update) und bei unbehandelten Fehlern
            AppDomain.CurrentDomain.ProcessExit += (s, e) => { try { Restore(); } catch { } };
            AppDomain.CurrentDomain.UnhandledException += (s, e) => { try { Restore(); } catch { } };
        }

        /// <summary>Kurve aus linearer Basis: Kontrast um die Mitte, dann Helligkeit, dann Gamma, dann Farbton.</summary>
        static ushort[] Build(ColorSettings s)
        {
            double t = s.Warmth;
            double mr = t < 0 ? 1 + 0.25 * t : 1;
            double mg = t < 0 ? 1 + 0.08 * t : 1 - 0.10 * t;
            double mb = t < 0 ? 1 : 1 - 0.30 * t;
            double inv = 1 / s.Gamma;

            var ramp = new ushort[RampSize];
            for (int i = 0; i < 256; i++)
            {
                double v = i / 255.0;
                v = (v - 0.5) * s.Contrast + 0.5 + s.Brightness;
                v = Math.Max(0, Math.Min(1, v));
                v = Math.Pow(v, inv);
                ramp[i] = ToWord(v * mr);
                ramp[256 + i] = ToWord(v * mg);
                ramp[512 + i] = ToWord(v * mb);
            }
            return ramp;
        }

        static ushort ToWord(double v) { return (ushort)Math.Round(Math.Max(0, Math.Min(1, v)) * 65535); }

        static ushort[] Get(string device)
        {
            var dc = CreateDC(null, device, null, IntPtr.Zero);
            if (dc == IntPtr.Zero) return null;
            try
            {
                var ramp = new ushort[RampSize];
                return GetDeviceGammaRamp(dc, ramp) ? ramp : null;
            }
            finally { DeleteDC(dc); }
        }

        static bool Set(string device, ushort[] ramp)
        {
            var dc = CreateDC(null, device, null, IntPtr.Zero);
            if (dc == IntPtr.Zero) return false;
            try { return SetDeviceGammaRamp(dc, ramp); }
            finally { DeleteDC(dc); }
        }

        static string Encode(ushort[] ramp)
        {
            var bytes = new byte[ramp.Length * 2];
            Buffer.BlockCopy(ramp, 0, bytes, 0, bytes.Length);
            return Convert.ToBase64String(bytes);
        }

        static ushort[] Decode(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            try
            {
                var bytes = Convert.FromBase64String(text);
                if (bytes.Length != RampSize * 2) return null;
                var ramp = new ushort[RampSize];
                Buffer.BlockCopy(bytes, 0, ramp, 0, bytes.Length);
                return ramp;
            }
            catch (FormatException) { return null; }
        }
    }
}
