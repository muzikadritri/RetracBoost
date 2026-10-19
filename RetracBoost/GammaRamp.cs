using System;
using System.Collections.Generic;
using System.Globalization;
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

        public double Gamma = 1;
        public double Brightness;      // -0.3 … +0.3
        public double Contrast = 1;
        public double Warmth;          // -1 kühl … +1 warm

        public bool IsDefault
        {
            get { return Math.Abs(Gamma - 1) < 0.005 && Math.Abs(Brightness) < 0.005 && Math.Abs(Contrast - 1) < 0.005 && Math.Abs(Warmth) < 0.005; }
        }

        public ColorSettings Clone() { return (ColorSettings)MemberwiseClone(); }

        public static ColorSettings Load()
        {
            return new ColorSettings
            {
                Gamma = Read("color.gamma", 1, GammaMin, GammaMax),
                Brightness = Read("color.brightness", 0, BrightnessMin, BrightnessMax),
                Contrast = Read("color.contrast", 1, ContrastMin, ContrastMax),
                Warmth = Read("color.warmth", 0, WarmthMin, WarmthMax)
            };
        }

        public void Save()
        {
            Write("color.gamma", Gamma);
            Write("color.brightness", Brightness);
            Write("color.contrast", Contrast);
            Write("color.warmth", Warmth);
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
    /// Ändert Gamma, Helligkeit, Kontrast und Farbton systemweit über die Gamma-Kurve der Grafikkarte
    /// (SetDeviceGammaRamp). Nichts davon greift ins Spiel ein. Die Originalkurven werden vor der ersten
    /// Änderung gesichert, auch auf die Platte, damit sie nach einem Absturz beim nächsten Start
    /// zurückkommen. Beim Beenden der App wird automatisch zurückgesetzt.
    /// </summary>
    static class GammaRamp
    {
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr CreateDC(string driver, string device, string output, IntPtr initData);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern bool GetDeviceGammaRamp(IntPtr hdc, [Out] ushort[] ramp);
        [DllImport("gdi32.dll")] static extern bool SetDeviceGammaRamp(IntPtr hdc, [In] ushort[] ramp);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumDisplayDevices(string device, uint index, ref DISPLAY_DEVICE info, uint flags);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        const int AttachedToDesktop = 0x1;
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

        /// <summary>Setzt die Werte auf allen Bildschirmen. false, wenn Windows die Kurve auf keinem Bildschirm annimmt.</summary>
        public static bool Apply(ColorSettings s)
        {
            if (s.IsDefault) { Restore(); return true; }
            lock (Gate)
            {
                EnsureHooked();
                var ramp = Build(s);
                bool any = false;
                foreach (var dev in Displays())
                {
                    if (!originals.ContainsKey(dev))
                    {
                        var orig = Get(dev);
                        if (orig == null) continue;
                        originals[dev] = orig;
                        Store.TrySet(OrigPrefix + dev, Encode(orig));
                    }
                    Store.TrySet(ActiveKey, "1");
                    if (Set(dev, ramp)) any = true;
                }
                Active = any || Active;
                return any;
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

        static List<string> Displays()
        {
            var list = new List<string>();
            var d = new DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE)) };
            for (uint i = 0; EnumDisplayDevices(null, i, ref d, 0); i++)
            {
                if ((d.StateFlags & AttachedToDesktop) != 0 && !string.IsNullOrEmpty(d.DeviceName)) list.Add(d.DeviceName);
                d.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
            }
            return list;
        }

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
