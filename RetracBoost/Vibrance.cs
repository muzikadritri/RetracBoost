using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace RetracBoost
{
    /// <summary>
    /// Farbsättigung über den NVIDIA-Treiber (Digital Vibrance, NvAPI). Das ist dieselbe Einstellung wie
    /// „Digitale Farbanpassung“ in der NVIDIA-Systemsteuerung: sie gilt für den Bildschirm, nicht für das Spiel,
    /// und greift nicht ins Spiel ein. Die Gamma-Kurve kann keine Sättigung (sie kennt nur Kanäle einzeln),
    /// darum dieser Weg. Ohne NVIDIA-Karte ist <see cref="Available"/> false.
    /// Die Originalwerte werden gesichert, auch auf die Platte, und beim Beenden oder nach einem Absturz zurückgesetzt.
    /// </summary>
    static class Vibrance
    {
        [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr QueryInterface(uint id);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int InitializeFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int EnumDisplayFn(int index, out IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GetNameFn(IntPtr handle, [Out] byte[] name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GetDvcInfoFn(IntPtr handle, int outputId, ref DvcInfo info);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SetDvcLevelFn(IntPtr handle, int outputId, int level);

        [StructLayout(LayoutKind.Sequential)]
        struct DvcInfo { public uint Version; public int Current, Min, Max; }

        const uint IdInitialize = 0x0150E828, IdEnumDisplay = 0x9ABDD40D, IdGetName = 0x22A78B05,
                   IdGetDvcInfo = 0x4085DE45, IdSetDvcLevel = 0x172409B4;
        const string ActiveKey = "vib.active", OrigPrefix = "vib.orig|";

        static readonly object Gate = new object();
        static readonly Dictionary<string, int> originals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        static bool loaded, available, hooked;
        static EnumDisplayFn enumDisplay;
        static GetNameFn getName;
        static GetDvcInfoFn getInfo;
        static SetDvcLevelFn setLevel;

        /// <summary>true, wenn eine NVIDIA-Karte mit NvAPI da ist.</summary>
        public static bool Available { get { lock (Gate) { Load(); return available; } } }

        static T Fn<T>(uint id) where T : class
        {
            var p = QueryInterface(id);
            return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
        }

        static void Load()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                var init = Fn<InitializeFn>(IdInitialize);
                if (init == null || init() != 0) return;
                enumDisplay = Fn<EnumDisplayFn>(IdEnumDisplay);
                getName = Fn<GetNameFn>(IdGetName);
                getInfo = Fn<GetDvcInfoFn>(IdGetDvcInfo);
                setLevel = Fn<SetDvcLevelFn>(IdSetDvcLevel);
                available = enumDisplay != null && getName != null && getInfo != null && setLevel != null;
            }
            catch (DllNotFoundException) { }     // keine NVIDIA-Karte
            catch (EntryPointNotFoundException) { }
            catch (BadImageFormatException) { }
        }

        /// <summary>NvAPI-Handle des Bildschirms (z. B. \\.\DISPLAY1), oder IntPtr.Zero.</summary>
        static IntPtr Handle(string device)
        {
            for (int i = 0; i < 16; i++)
            {
                IntPtr h;
                if (enumDisplay(i, out h) != 0) break;
                var buf = new byte[64];
                if (getName(h, buf) != 0) continue;
                string name = Encoding.ASCII.GetString(buf).TrimEnd('\0');
                if (string.Equals(name, device, StringComparison.OrdinalIgnoreCase)) return h;
            }
            return IntPtr.Zero;
        }

        static bool Info(IntPtr h, out DvcInfo info)
        {
            info = new DvcInfo { Version = (uint)Marshal.SizeOf(typeof(DvcInfo)) | (1u << 16) };
            return getInfo(h, 0, ref info) == 0 && info.Max > info.Min;
        }

        static void SetLevel(string device, int level)
        {
            var h = Handle(device);
            if (h != IntPtr.Zero) setLevel(h, 0, level);
        }

        /// <summary>Beim Start aufrufen: stellt die Sättigung wieder her, falls die App abgestürzt ist.</summary>
        public static void RecoverAfterCrash()
        {
            lock (Gate)
            {
                if (Store.Get(ActiveKey) != "1") return;
                Load();
                foreach (var key in Store.Keys(OrigPrefix))
                {
                    int level;
                    if (available && int.TryParse(Store.Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out level))
                        try { SetLevel(key.Substring(OrigPrefix.Length), level); } catch { }
                    Store.TrySet(key, null);
                }
                Store.TrySet(ActiveKey, null);
            }
        }

        /// <summary>
        /// Zusätzliche Sättigung 0 … 1 auf dem Bildschirm <paramref name="device"/>: 0 = Originalwert, 1 = Maximum des Treibers.
        /// Andere Bildschirme bekommen ihren Originalwert zurück. Ohne Gerät oder bei 0 wird alles zurückgesetzt.
        /// </summary>
        public static bool Apply(double extra, string device)
        {
            if (extra < 0.005 || string.IsNullOrEmpty(device)) { Restore(); return true; }
            lock (Gate)
            {
                Load();
                if (!available) return false;
                if (!hooked)
                {
                    hooked = true;
                    AppDomain.CurrentDomain.ProcessExit += (s, e) => { try { Restore(); } catch { } };
                    AppDomain.CurrentDomain.UnhandledException += (s, e) => { try { Restore(); } catch { } };
                }
                foreach (var kv in originals.ToList())
                {
                    if (string.Equals(kv.Key, device, StringComparison.OrdinalIgnoreCase)) continue;
                    try { SetLevel(kv.Key, kv.Value); } catch { }
                    originals.Remove(kv.Key);
                    Store.TrySet(OrigPrefix + kv.Key, null);
                }

                var h = Handle(device);
                DvcInfo info;
                if (h == IntPtr.Zero || !Info(h, out info)) return false;
                int orig;
                if (!originals.TryGetValue(device, out orig))
                {
                    orig = info.Current;
                    originals[device] = orig;
                    Store.TrySet(OrigPrefix + device, orig.ToString(CultureInfo.InvariantCulture));
                    Store.TrySet(ActiveKey, "1");
                }
                int level = orig + (int)Math.Round((info.Max - orig) * Math.Min(1, extra));
                return setLevel(h, 0, level) == 0;
            }
        }

        public static void Restore()
        {
            lock (Gate)
            {
                if (originals.Count == 0) return;
                foreach (var kv in originals)
                {
                    try { SetLevel(kv.Key, kv.Value); } catch { }
                    Store.TrySet(OrigPrefix + kv.Key, null);
                }
                originals.Clear();
                Store.TrySet(ActiveKey, null);
            }
        }
    }
}
