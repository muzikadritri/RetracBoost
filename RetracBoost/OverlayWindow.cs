using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace RetracBoost
{
    /// <summary>
    /// Kleines Ping-Overlay, immer im Vordergrund, nimmt nie den Fokus. Per Maus verschiebbar.
    /// Sichtbar über Spielen im Fenster- oder randlosen Vollbildmodus (nicht über exklusivem Vollbild).
    /// </summary>
    sealed class OverlayWindow : Window
    {
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
        const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

        readonly TextBlock value = new TextBlock();
        readonly TextBlock detail = new TextBlock();
        readonly Ellipse dot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };

        public OverlayWindow()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            Focusable = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            Title = "Retrac Boost Overlay";

            var family = new FontFamily("Segoe UI Variable Text, Segoe UI");
            value.FontFamily = family;
            value.FontSize = 16;
            value.FontWeight = FontWeights.SemiBold;
            value.Foreground = new SolidColorBrush(Color.FromRgb(0xE9, 0xEC, 0xF2));
            value.Margin = new Thickness(8, 0, 0, 0);
            detail.FontFamily = family;
            detail.FontSize = 11;
            detail.Foreground = new SolidColorBrush(Color.FromRgb(0x7B, 0x83, 0x94));
            detail.Margin = new Thickness(10, 0, 0, 0);
            detail.VerticalAlignment = VerticalAlignment.Center;

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(dot);
            row.Children.Add(value);
            row.Children.Add(detail);

            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x0B, 0x0D, 0x12)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2B, 0x31, 0x42)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 7, 14, 7),
                Child = row,
                Cursor = Cursors.SizeAll
            };

            double x, y;
            if (double.TryParse(Store.Get("overlay.x"), NumberStyles.Float, CultureInfo.InvariantCulture, out x) &&
                double.TryParse(Store.Get("overlay.y"), NumberStyles.Float, CultureInfo.InvariantCulture, out y) &&
                x >= SystemParameters.VirtualScreenLeft && x < SystemParameters.VirtualScreenWidth - 40 &&
                y >= SystemParameters.VirtualScreenTop && y < SystemParameters.VirtualScreenHeight - 20)
            {
                Left = x;
                Top = y;
            }
            else
            {
                Left = SystemParameters.WorkArea.Left + 24;
                Top = SystemParameters.WorkArea.Top + 24;
            }

            WindowStartupLocation = WindowStartupLocation.Manual;
            SourceInitialized += (s, e) =>
            {
                var h = new WindowInteropHelper(this).Handle;
                SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            };
            MouseLeftButtonDown += (s, e) =>
            {
                try { DragMove(); } catch { }
                Store.TrySet("overlay.x", Left.ToString(CultureInfo.InvariantCulture));
                Store.TrySet("overlay.y", Top.ToString(CultureInfo.InvariantCulture));
            };
            Update(null, "", Brushes.Gray);
        }

        public void Update(double? ms, string detailText, Brush dotBrush)
        {
            value.Text = ms.HasValue ? (ms.Value < 1 ? "<1 ms" : ms.Value.ToString("0", CultureInfo.CurrentCulture) + " ms") : "– ms";
            detail.Text = detailText;
            dot.Fill = dotBrush;
        }
    }
}
