using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace RetracBoost
{
    /// <summary>
    /// Zeigt vor der Installation, was ein Plugin genau ändern würde. Installiert wird erst nach „Installieren“,
    /// und auch dann ändert sich am System noch nichts, bis der Nutzer einen Schalter umlegt.
    /// </summary>
    sealed class PluginPreviewWindow : Window
    {
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        static readonly Brush TextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF0, 0xF3, 0xF9)));
        static readonly Brush MutedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0xA7)));
        static readonly Brush WarnBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)));
        static readonly Brush AccentBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x00, 0xF5, 0xA0)));
        static readonly Brush LineBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x20, 0x27, 0x38)));
        static readonly Brush RaisedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x16, 0x1C, 0x2B)));
        static readonly Brush BgBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x0F, 0x13, 0x1D)));

        static Brush Freeze(Brush b) { b.Freeze(); return b; }

        public static bool Ask(Window owner, GamePlugins.Package p)
        {
            var w = new PluginPreviewWindow(owner, p) { Owner = owner };
            return w.ShowDialog() == true;
        }

        PluginPreviewWindow(Window owner, GamePlugins.Package p)
        {
            var g = p.Game;
            Title = L.T("Plugin prüfen", "Review plugin");
            Width = 540;
            SizeToContent = SizeToContent.Height;
            MaxHeight = 680;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Background = BgBrush;
            Foreground = TextBrush;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            FontSize = 13;
            UseLayoutRounding = true;
            SourceInitialized += (s, e) =>
            {
                try { int on = 1; DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref on, 4); } catch { }
            };

            var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };

            root.Children.Add(new TextBlock { Text = g.Name, FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            string sub = L.T("Version ", "Version ") + g.Version + (string.IsNullOrEmpty(g.Author) ? "" : L.T(" · von ", " · by ") + g.Author);
            root.Children.Add(new TextBlock { Text = sub, Foreground = MutedBrush, FontSize = 12, Margin = new Thickness(0, 2, 0, 0) });
            if (p.Existing != null)
                root.Children.Add(new TextBlock
                {
                    Text = L.T("Ersetzt die installierte Version " + p.Existing.Version + ".", "Replaces the installed version " + p.Existing.Version + "."),
                    Foreground = WarnBrush, FontSize = 12, Margin = new Thickness(0, 6, 0, 0)
                });
            if (!string.IsNullOrEmpty(g.Description))
                root.Children.Add(new TextBlock { Text = g.Description, Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });

            root.Children.Add(new TextBlock
            {
                Text = L.T("Das kann das Plugin ändern. Jede Zeile ist später ein eigener Schalter:", "This is what the plugin can change. Each line becomes its own switch:"),
                FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 8)
            });

            var list = new StackPanel();
            foreach (var a in g.Actions)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.Children.Add(new TextBlock { Text = "•", Foreground = AccentBrush, Margin = new Thickness(0, 0, 8, 0) });
                var tb = new TextBlock { Text = a.Describe(g), TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
                Grid.SetColumn(tb, 1);
                row.Children.Add(tb);
                list.Children.Add(row);
            }
            var box = new Border
            {
                Background = RaisedBrush, BorderBrush = LineBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 10, 12, 4),
                Child = new ScrollViewer { Content = list, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false }
            };
            root.Children.Add(box);

            var facts = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            if (g.ExePaths.Length > 0)
                facts.Children.Add(Fact(L.T("Sucht das Spiel unter: ", "Looks for the game in: ") + string.Join(", ", g.ExePaths)));
            if (p.ImageBytes != null) facts.Children.Add(Fact(L.T("Bild: im Plugin enthalten (", "Image: included in the plugin (") + p.ImageName + ")"));
            else if (p.ImageHost != null) facts.Children.Add(Fact(L.T("Bild wird geladen von: ", "Image is loaded from: ") + p.ImageHost));
            facts.Children.Add(Fact(L.T("Das Plugin enthält keinen Programmcode. Retrac Boost führt nur die oben genannten, eingebauten Aktionen aus.",
                                        "The plugin contains no program code. Retrac Boost only runs the built-in actions listed above.")));
            root.Children.Add(facts);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            var cancel = new Button { Content = L.T("Abbrechen", "Cancel"), IsCancel = true, MinWidth = 90 };
            var ok = new Button { Content = L.T("Installieren", "Install"), IsDefault = true, MinWidth = 110, Margin = new Thickness(0) };
            var action = owner.TryFindResource("Action") as Style;
            if (action != null) { cancel.Style = action; ok.Style = action; ok.Margin = new Thickness(0); }
            cancel.Click += (s, e) => { DialogResult = false; };
            ok.Click += (s, e) => { DialogResult = true; };
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            root.Children.Add(buttons);

            Content = root;
        }

        static TextBlock Fact(string text)
        {
            return new TextBlock { Text = text, Foreground = MutedBrush, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
        }
    }
}
