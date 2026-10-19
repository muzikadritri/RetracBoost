using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace RetracBoost
{
    /// <summary>Zeichnet den Ping-Verlauf: Fläche + Linie, verlorene Pakete als rote Markierung.</summary>
    sealed class PingGraph : FrameworkElement
    {
        public const int Points = 90;

        static readonly Brush Muted = Freeze(new SolidColorBrush(Color.FromRgb(0x5E, 0x66, 0x78)));
        static readonly Pen Grid = FreezePen(new Pen(new SolidColorBrush(Color.FromRgb(0x1B, 0x20, 0x2B)), 1));
        static readonly Color AccentColor = Color.FromRgb(0x3D, 0xDC, 0x97);
        static readonly Brush LostBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xCC, 0xF0, 0x61, 0x6D)));
        static readonly Brush LastPointOuter = Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0x3D, 0xDC, 0x97)));
        static readonly Brush LastPointInner = Freeze(new SolidColorBrush(Color.FromRgb(0x3D, 0xDC, 0x97)));
        static readonly LinearGradientBrush AreaBrush = FreezeBrush(new LinearGradientBrush(
            Color.FromArgb(0x55, 0x3D, 0xDC, 0x97),
            Color.FromArgb(0x00, 0x3D, 0xDC, 0x97), 90));
        static readonly Pen LinePen = FreezePen(new Pen(new SolidColorBrush(Color.FromRgb(0x3D, 0xDC, 0x97)), 2)
            { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        static readonly Typeface Face = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        double?[] values = new double?[0];

        static Brush Freeze(Brush b) { b.Freeze(); return b; }
        static LinearGradientBrush FreezeBrush(LinearGradientBrush b) { b.Freeze(); return b; }
        static Pen FreezePen(Pen p) { p.Freeze(); return p; }

        public void SetValues(double?[] v)
        {
            values = v ?? new double?[0];
            InvalidateVisual();
        }

        static double NiceTop(double v)
        {
            double[] steps = { 10, 20, 30, 50, 80, 100, 150, 200, 300, 500, 800, 1000 };
            foreach (var s in steps) if (v <= s) return s;
            return Math.Ceiling(v / 500) * 500;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 40 || h < 40) return;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));   // Treffer für Maus nicht nötig, aber stabil

            double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            const double padTop = 10, padBottom = 14, padRight = 0;
            double plotH = h - padTop - padBottom;

            var real = values.Where(v => v.HasValue).Select(v => v.Value).ToList();
            double max = real.Count > 0 ? real.Max() : 10;
            double top = NiceTop(Math.Max(10, max * 1.2));

            // Hilfslinien mit Beschriftung
            for (int i = 0; i <= 2; i++)
            {
                double y = padTop + plotH * i / 2.0;
                y = Math.Round(y) + 0.5;
                dc.DrawLine(Grid, new Point(0, y), new Point(w, y));
                double label = top * (1 - i / 2.0);
                var ft = new FormattedText(label.ToString("0", CultureInfo.CurrentCulture) + " ms", CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, Face, 10.5, Muted, ppd);
                dc.DrawText(ft, new Point(w - ft.Width - 2, y - ft.Height - 2 < 0 ? y + 2 : y - ft.Height - 2));
            }

            if (values.Length == 0) return;

            double step = (w - padRight) / (Points - 1);
            int offset = Points - values.Length;           // rechtsbündig: neueste Werte ganz rechts
            Func<int, double> X = i => (offset + i) * step;
            Func<double, double> Y = v => padTop + plotH * (1 - Math.Min(v, top) / top);

            var line = new StreamGeometry();
            var fill = new StreamGeometry();
            using (var lc = line.Open())
            using (var fc = fill.Open())
            {
                int i = 0;
                while (i < values.Length)
                {
                    if (!values[i].HasValue) { i++; continue; }
                    int start = i;
                    while (i < values.Length && values[i].HasValue) i++;
                    int end = i - 1;                        // Lauf start..end ohne Lücke

                    var pts = new Point[end - start + 1];
                    for (int k = 0; k < pts.Length; k++) pts[k] = new Point(X(start + k), Y(values[start + k].Value));

                    lc.BeginFigure(pts[0], false, false);
                    fc.BeginFigure(new Point(pts[0].X, padTop + plotH), true, true);
                    fc.LineTo(pts[0], false, false);
                    if (pts.Length == 1)
                    {
                        lc.LineTo(new Point(pts[0].X + 0.01, pts[0].Y), true, true);
                    }
                    else
                    {
                        // sanfte Glättung: quadratische Kurven über die Mittelpunkte
                        for (int k = 1; k < pts.Length - 1; k++)
                        {
                            var mid = new Point((pts[k].X + pts[k + 1].X) / 2, (pts[k].Y + pts[k + 1].Y) / 2);
                            lc.QuadraticBezierTo(pts[k], mid, true, true);
                            fc.QuadraticBezierTo(pts[k], mid, false, true);
                        }
                        lc.LineTo(pts[pts.Length - 1], true, true);
                        fc.LineTo(pts[pts.Length - 1], false, true);
                    }
                    fc.LineTo(new Point(pts[pts.Length - 1].X, padTop + plotH), false, false);
                }
            }
            line.Freeze();
            fill.Freeze();

            dc.DrawGeometry(AreaBrush, null, fill);
            dc.DrawGeometry(null, LinePen, line);

            // verlorene Pakete
            for (int i = 0; i < values.Length; i++)
                if (!values[i].HasValue)
                    dc.DrawRoundedRectangle(LostBrush, null, new Rect(X(i) - 1.5, padTop + plotH - 14, 3, 14), 1.5, 1.5);

            // aktueller Wert als Punkt
            int last = values.Length - 1;
            if (last >= 0 && values[last].HasValue)
            {
                var p = new Point(X(last), Y(values[last].Value));
                dc.DrawEllipse(LastPointOuter, null, p, 8, 8);
                dc.DrawEllipse(LastPointInner, null, p, 3.5, 3.5);
            }
        }
    }
}
