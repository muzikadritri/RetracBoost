using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace RetracBoost
{
    /// <summary>
    /// --screens ordner: rendert die drei Seiten außerhalb des sichtbaren Bildschirms in PNG-Dateien
    /// (kein Fokus-Wechsel, keine Administratorrechte nötig).
    /// </summary>
    static class ScreenShots
    {
        public static int Run(string dir)
        {
            Directory.CreateDirectory(dir);
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var w = new MainWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000,
                Top = 0,
                ShowInTaskbar = false,
                ShowActivated = false
            };
            w.Show();

            int step = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            int waited = 0;
            timer.Tick += (s, e) =>
            {
                waited++;
                if (waited == 7) { w.SelectTab(0); }
                if (waited == 8) { Save(w, Path.Combine(dir, "1-boost.png")); w.SelectTab(1); Invoke(w, "RegionRun_Click"); Invoke(w, "RouteRun_Click"); Invoke(w, "MtuRun_Click"); }
                if (waited == 20) { Save(w, Path.Combine(dir, "2-werkzeuge.png")); w.SelectTab(2); Invoke(w, "OgRegionRun_Click"); }
                if (waited == 27) { Save(w, Path.Combine(dir, "3-retrac.png")); step = 99; }
                if (step == 99) { timer.Stop(); app.Shutdown(); }
            };
            timer.Start();
            app.Run();
            return 0;
        }

        static void Invoke(MainWindow w, string method)
        {
            var m = typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
            if (m != null) m.Invoke(w, new object[] { null, new RoutedEventArgs() });
        }

        static void Save(MainWindow w, string file)
        {
            var root = (FrameworkElement)w.Content;
            int pw = (int)Math.Ceiling(root.ActualWidth), ph = (int)Math.Ceiling(root.ActualHeight);
            var bg = new DrawingVisual();
            using (var dc = bg.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x0B, 0x0D, 0x12)), null, new Rect(0, 0, pw, ph));
                dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, pw, ph));
            }
            var rtb = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(bg);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(file)) enc.Save(fs);
        }
    }
}
