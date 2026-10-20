using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;

namespace RetracBoost
{
    /// <summary>
    /// Ingame-Menü (Alt+L): eigenes randloses Fenster über dem Spiel. Es wird nichts in das Spiel geladen.
    /// Die Werte wirken über die Gamma-Kurve (siehe <see cref="GammaRamp"/>), aber nur auf dem Bildschirm des Spiels
    /// und nur, solange das Spiel im Vordergrund ist. Welches Fenster das ist, meldet <see cref="SetGameWindow"/>.
    /// </summary>
    sealed class GameMenuWindow : Window
    {
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct MONITORINFO
        {
            public int cbSize; public RECT rcMonitor; public RECT rcWork; public int dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }

        const uint MonitorDefaultToNearest = 2;
        const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80;
        const double MenuWidth = 340;

        static readonly Brush TextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF0, 0xF3, 0xF9)));
        static readonly Brush MutedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0xA7)));
        static readonly Brush WarnBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)));
        static readonly Brush LineBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x20, 0x27, 0x38)));
        static readonly Brush AccentBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x00, 0xF5, 0xA0)));
        static readonly Brush DefaultBorderBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x2D, 0x38, 0x50)));

        const string SliderXaml = @"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
       xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Slider'>
  <Setter Property='Focusable' Value='False'/>
  <Setter Property='IsMoveToPointEnabled' Value='True'/>
  <Setter Property='Cursor' Value='Hand'/>
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='Slider'>
        <Grid Height='22' Background='Transparent'>
          <Border Height='4' CornerRadius='2' Background='#1D2433' VerticalAlignment='Center'/>
          <Track x:Name='PART_Track'>
            <Track.DecreaseRepeatButton>
              <RepeatButton Command='Slider.DecreaseLarge' Focusable='False'>
                <RepeatButton.Template>
                  <ControlTemplate TargetType='RepeatButton'>
                    <Border Height='4' CornerRadius='2' Background='#00F5A0' VerticalAlignment='Center'/>
                  </ControlTemplate>
                </RepeatButton.Template>
              </RepeatButton>
            </Track.DecreaseRepeatButton>
            <Track.IncreaseRepeatButton>
              <RepeatButton Command='Slider.IncreaseLarge' Focusable='False'>
                <RepeatButton.Template>
                  <ControlTemplate TargetType='RepeatButton'>
                    <Border Background='Transparent'/>
                  </ControlTemplate>
                </RepeatButton.Template>
              </RepeatButton>
            </Track.IncreaseRepeatButton>
            <Track.Thumb>
              <Thumb Focusable='False'>
                <Thumb.Template>
                  <ControlTemplate TargetType='Thumb'>
                    <Ellipse Width='14' Height='14' Fill='#FFFFFF'/>
                  </ControlTemplate>
                </Thumb.Template>
              </Thumb>
            </Track.Thumb>
          </Track>
        </Grid>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";

        const string ButtonXaml = @"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
       xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>
  <Setter Property='Focusable' Value='False'/>
  <Setter Property='Cursor' Value='Hand'/>
  <Setter Property='Foreground' Value='#F0F3F9'/>
  <Setter Property='FontSize' Value='12'/>
  <Setter Property='BorderBrush' Value='#2D3850'/>
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='Button'>
        <Border x:Name='B' Background='#161C2B' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' CornerRadius='7' Padding='10,6'>
          <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'>
            <Setter TargetName='B' Property='Background' Value='#1E263A'/>
          </Trigger>
          <Trigger Property='IsEnabled' Value='False'>
            <Setter TargetName='B' Property='Opacity' Value='0.4'/>
          </Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";

        readonly ColorSettings settings;
        readonly Slider gamma, brightness, contrast, warmth, saturation;
        readonly TextBlock gammaValue = ValueText(), brightnessValue = ValueText(), contrastValue = ValueText(), warmthValue = ValueText(), saturationValue = ValueText();
        readonly Dictionary<string, Button> filterButtons = new Dictionary<string, Button>();

        /// <summary>Farbfilter: fertige Kombinationen der Bildschirm-Regler. Sättigung braucht eine NVIDIA-Karte.</summary>
        sealed class ColorFilter
        {
            public string Name; public double Gamma, Brightness, Contrast, Warmth, Saturation;
        }

        static readonly ColorFilter[] Filters =
        {
            new ColorFilter { Name = "Retrac Edit", Gamma = 1.06, Brightness = 0.02, Contrast = 1.12, Warmth = 0.1, Saturation = 0.85 },
            new ColorFilter { Name = L.T("Kräftig", "Vivid"), Gamma = 1.0, Brightness = 0, Contrast = 1.08, Warmth = 0, Saturation = 0.5 },
            new ColorFilter { Name = L.T("Kühl", "Cool"), Gamma = 1.04, Brightness = 0, Contrast = 1.08, Warmth = -0.35, Saturation = 0.45 },
            new ColorFilter { Name = "Warm", Gamma = 1.0, Brightness = 0.01, Contrast = 1.05, Warmth = 0.4, Saturation = 0.35 },
            new ColorFilter { Name = L.T("Hell", "Bright"), Gamma = 1.25, Brightness = 0.04, Contrast = 1.0, Warmth = 0, Saturation = 0.25 },
            new ColorFilter { Name = "Neutral", Gamma = 1, Brightness = 0, Contrast = 1, Warmth = 0, Saturation = 0 }
        };
        readonly TextBlock pingText, status;
        readonly CheckBox pingSwitch;
        readonly Func<bool> pingOverlayOn;
        readonly Func<GameDef> presetGame;
        readonly TextBlock presetTitle, presetStatus;
        readonly Dictionary<string, Button> presetButtons = new Dictionary<string, Button>();
        readonly Action<bool> setPingOverlay;
        IntPtr previous, gameWindow;
        bool loading, dirty, hiding, enabled;

        public GameMenuWindow(Style switchStyle, Func<bool> pingOverlayOn, Action<bool> setPingOverlay, Func<GameDef> presetGame)
        {
            this.pingOverlayOn = pingOverlayOn;
            this.presetGame = presetGame;
            this.setPingOverlay = setPingOverlay;
            settings = ColorSettings.Load();

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            Width = MenuWidth;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Title = "Retrac Boost Menü";
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

            var sliderStyle = (Style)XamlReader.Parse(SliderXaml);
            var buttonStyle = (Style)XamlReader.Parse(ButtonXaml);

            var root = new StackPanel();

            // Kopfzeile
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            pingText = new TextBlock { Foreground = MutedBrush, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(pingText, Dock.Right);
            head.Children.Add(pingText);
            head.Children.Add(new TextBlock { Text = "Retrac Boost", Foreground = TextBrush, FontSize = 15, FontWeight = FontWeights.SemiBold });
            root.Children.Add(head);
            root.Children.Add(new TextBlock
            {
                Text = L.T("Alt+L oder Esc zum Schließen", "Alt+L or Esc to close"),
                Foreground = MutedBrush, FontSize = 11, Margin = new Thickness(0, 0, 0, 12)
            });

            root.Children.Add(SectionTitle(L.T("Farbfilter", "Colour filters")));
            var filterRow = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 6) };
            for (int i = 0; i < Filters.Length; i++)
            {
                var f = Filters[i];
                var fb = new Button { Content = f.Name, Style = buttonStyle, Margin = new Thickness(i % 3 == 0 ? 0 : 3, 0, i % 3 == 2 ? 0 : 3, 6) };
                fb.Click += (s, e) => ApplyFilter(f);
                filterButtons[f.Name] = fb;
                filterRow.Children.Add(fb);
            }
            root.Children.Add(filterRow);

            root.Children.Add(SectionTitle(L.T("Bildschirm", "Screen")));
            gamma = AddSlider(root, sliderStyle, "Gamma", gammaValue, ColorSettings.GammaMin, ColorSettings.GammaMax, 0.01);
            brightness = AddSlider(root, sliderStyle, L.T("Helligkeit", "Brightness"), brightnessValue, ColorSettings.BrightnessMin, ColorSettings.BrightnessMax, 0.01);
            contrast = AddSlider(root, sliderStyle, L.T("Kontrast", "Contrast"), contrastValue, ColorSettings.ContrastMin, ColorSettings.ContrastMax, 0.01);
            warmth = AddSlider(root, sliderStyle, L.T("Farbton", "Color tone"), warmthValue, ColorSettings.WarmthMin, ColorSettings.WarmthMax, 0.05);
            saturation = AddSlider(root, sliderStyle, L.T("Farbsättigung", "Saturation"), saturationValue, ColorSettings.SaturationMin, ColorSettings.SaturationMax, 0.05);
            if (!Vibrance.Available)
            {
                saturation.IsEnabled = false;
                root.Children.Add(new TextBlock
                {
                    Text = L.T("Farbsättigung geht nur mit einer NVIDIA-Karte (Digitale Farbanpassung im Treiber). Die Filter wirken dann etwas schwächer.",
                               "Saturation needs an NVIDIA card (digital vibrance in the driver). Filters look a bit weaker without it."),
                    Foreground = MutedBrush, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, -4, 0, 8)
                });
            }

            status = new TextBlock { Foreground = WarnBrush, FontSize = 11, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 2, 0, 8) };
            root.Children.Add(status);

            root.Children.Add(new Border { Height = 1, Background = LineBrush, Margin = new Thickness(0, 6, 0, 10) });

            // Grafik-Presets: ändern die Grafikstufen in der Config des Spiels
            presetTitle = SectionTitle(L.T("Grafik im Spiel", "In-game graphics"));
            root.Children.Add(presetTitle);
            var presetRow = new UniformGrid { Columns = 2, Margin = new Thickness(0, 4, 0, 0) };
            var presetIds = new[] { GraphicsPresets.Pretty, GraphicsPresets.Balanced, GraphicsPresets.Performance, GraphicsPresets.Original };
            for (int i = 0; i < presetIds.Length; i++)
            {
                string id = presetIds[i];
                var b = new Button { Content = GraphicsPresets.Name(id), Style = buttonStyle, Margin = i % 2 == 0 ? new Thickness(0, 0, 3, 6) : new Thickness(3, 0, 0, 6) };
                string preset = id;
                b.Click += (s, e) => ChoosePreset(preset);
                presetButtons[id] = b;
                presetRow.Children.Add(b);
            }
            root.Children.Add(presetRow);
            presetStatus = new TextBlock { Foreground = MutedBrush, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
            root.Children.Add(presetStatus);

            root.Children.Add(new Border { Height = 1, Background = LineBrush, Margin = new Thickness(0, 6, 0, 10) });

            // Ping-Anzeige
            var pingRow = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            pingSwitch = new CheckBox { Style = switchStyle, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(pingSwitch, Dock.Right);
            pingRow.Children.Add(pingSwitch);
            pingRow.Children.Add(new TextBlock { Text = L.T("Ping-Anzeige", "Ping display"), Foreground = TextBrush, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            root.Children.Add(pingRow);
            pingSwitch.Click += (s, e) =>
            {
                if (this.setPingOverlay != null) this.setPingOverlay(pingSwitch.IsChecked == true);
                pingSwitch.IsChecked = this.pingOverlayOn != null && this.pingOverlayOn();
            };

            var reset = new Button { Content = L.T("Zurücksetzen", "Reset"), Style = buttonStyle, HorizontalAlignment = HorizontalAlignment.Left };
            reset.Click += (s, e) => ResetValues();
            root.Children.Add(reset);

            root.Children.Add(new TextBlock
            {
                Text = L.T("Farbfilter und Bildschirm-Regler ändern den ganzen Bildschirm, auf dem das Spiel läuft, solange es vorne ist. Nur das Spielbild allein lässt sich ohne Eingriff ins Spiel nicht färben, und das wäre ein Bann-Risiko. Im randlosen Vollbild deckt das Spiel den Bildschirm aber ganz ab. Die Grafik-Presets ändern die Einstellungen des Spiels selbst. Das Menü ist nicht im exklusiven Vollbild sichtbar.",
                           "Colour filters and screen sliders change the whole screen the game runs on, while it is in front. Colouring only the game picture is not possible without touching the game, which would risk a ban. In borderless fullscreen the game covers the screen anyway. The graphics presets change the game's own settings. The menu is not visible in exclusive fullscreen."),
                Foreground = MutedBrush, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0)
            });

            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x0B, 0x0D, 0x12)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2D, 0x38, 0x50)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(18, 14, 18, 16),
                Child = root
            };

            loading = true;
            gamma.Value = settings.Gamma;
            brightness.Value = settings.Brightness;
            contrast.Value = settings.Contrast;
            warmth.Value = settings.Warmth;
            saturation.Value = settings.Saturation;
            loading = false;
            UpdateLabels();

            SourceInitialized += (s, e) =>
            {
                var h = new WindowInteropHelper(this).Handle;
                SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);
            };
            KeyDown += (s, e) => { if (e.Key == Key.Escape) { e.Handled = true; HideMenu(true); } };
            Deactivated += (s, e) => { if (IsVisible) HideMenu(false); };
        }

        /// <summary>false, wenn das Ingame-Menü ausgeschaltet ist; dann bleibt die Kurve immer normal.</summary>
        public bool Enabled
        {
            get { return enabled; }
            set { enabled = value; Apply(); }
        }

        /// <summary>
        /// Das Spielfenster, das gerade im Vordergrund ist, oder IntPtr.Zero, wenn etwas anderes vorne ist.
        /// Ohne Spiel vorne wird sofort zurückgesetzt.
        /// </summary>
        public void SetGameWindow(IntPtr hwnd)
        {
            gameWindow = hwnd;
            Apply();
        }

        /// <summary>Zeigt Spielname, aktives und vorgemerktes Preset neu an.</summary>
        public void RefreshPresets()
        {
            var g = presetGame != null ? presetGame() : null;
            bool ok = GraphicsPresets.Supports(g);
            presetTitle.Text = ok ? L.T("Grafik in ", "Graphics in ") + g.Name : L.T("Grafik im Spiel", "In-game graphics");
            foreach (var b in presetButtons.Values) b.IsEnabled = ok;
            string current = ok ? GraphicsPresets.Current(g) : null, pending = ok ? GraphicsPresets.Pending(g) : null;
            foreach (var kv in presetButtons)
                kv.Value.BorderBrush = kv.Key == (pending ?? current) ? AccentBrush : DefaultBorderBrush;
            if (!ok)
                presetStatus.Text = g == null ? L.T("Kein Spiel erkannt.", "No game detected.")
                    : string.Format(L.T("Presets gibt es für Fortnite, VALORANT und PUBG. {0} gehört nicht dazu.",
                                        "Presets exist for Fortnite, VALORANT and PUBG. {0} is not one of them."), g.Name);
            else if (pending != null)
                presetStatus.Text = string.Format(L.T("{0} wird eingetragen, sobald du {1} schließt. Gilt ab dem nächsten Start.",
                                                      "{0} will be written once you close {1}. Applies from the next start."), GraphicsPresets.Name(pending), g.Name);
            else
                presetStatus.Text = L.T("Schön = Episch, Ausgewogen = Hoch, Leistung = niedrig mit guter Sichtweite. Original holt deine alten Werte zurück.",
                                        "Pretty = Epic, Balanced = High, Performance = low with good view distance. Original brings back your old values.");
        }

        void ChoosePreset(string preset)
        {
            var g = presetGame != null ? presetGame() : null;
            string msg;
            try { msg = GraphicsPresets.Choose(g, preset); }
            catch (Exception ex) { msg = ex.Message; }
            RefreshPresets();
            presetStatus.Text = msg;
        }

        public bool IsOwnWindow(IntPtr hwnd) { return hwnd != IntPtr.Zero && hwnd == new WindowInteropHelper(this).Handle; }

        public void Toggle()
        {
            if (IsVisible) HideMenu(true);
            else Open();
        }

        public void SetPing(string text) { pingText.Text = text; }

        void Open()
        {
            var fg = GetForegroundWindow();
            var own = new WindowInteropHelper(this).EnsureHandle();
            previous = fg == own ? IntPtr.Zero : fg;
            PlaceOn(previous != IntPtr.Zero ? previous : own);
            pingSwitch.IsChecked = pingOverlayOn != null && pingOverlayOn();
            RefreshPresets();
            Show();
            Activate();
            SetForegroundWindow(own);
        }

        /// <summary>Blendet das Menü aus. Mit giveBack bekommt das Spiel den Fokus zurück.</summary>
        void HideMenu(bool giveBack)
        {
            if (hiding) return;
            hiding = true;
            var target = previous;
            previous = IntPtr.Zero;
            try
            {
                Hide();
                if (dirty) { settings.Save(); dirty = false; }
                if (giveBack && target != IntPtr.Zero) SetForegroundWindow(target);
            }
            finally { hiding = false; }
        }

        void PlaceOn(IntPtr window)
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            var mon = MonitorFromWindow(window, MonitorDefaultToNearest);
            if (mon == IntPtr.Zero || !GetMonitorInfo(mon, ref info))
            {
                Left = SystemParameters.WorkArea.Right - MenuWidth - 24;
                Top = SystemParameters.WorkArea.Top + 24;
                return;
            }
            var src = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            var m = src != null && src.CompositionTarget != null ? src.CompositionTarget.TransformFromDevice : Matrix.Identity;
            var topRight = m.Transform(new Point(info.rcMonitor.Right, info.rcMonitor.Top));
            Left = topRight.X - MenuWidth - 24;
            Top = topRight.Y + 24;
        }

        Slider AddSlider(Panel parent, Style style, string label, TextBlock valueText, double min, double max, double step)
        {
            var head = new DockPanel();
            DockPanel.SetDock(valueText, Dock.Right);
            head.Children.Add(valueText);
            head.Children.Add(new TextBlock { Text = label, Foreground = TextBrush, FontSize = 13 });
            parent.Children.Add(head);

            var slider = new Slider
            {
                Style = style, Minimum = min, Maximum = max,
                SmallChange = step, LargeChange = step * 5,
                TickFrequency = step, IsSnapToTickEnabled = true,
                Margin = new Thickness(0, 2, 0, 10)
            };
            slider.ValueChanged += (s, e) => OnSliderChanged();
            slider.MouseDoubleClick += (s, e) =>
            {
                // Doppelklick setzt nur diesen Regler auf Standard
                if (slider == gamma || slider == contrast) slider.Value = 1; else slider.Value = 0;
            };
            parent.Children.Add(slider);
            return slider;
        }

        void OnSliderChanged()
        {
            if (loading) return;
            settings.Gamma = gamma.Value;
            settings.Brightness = brightness.Value;
            settings.Contrast = contrast.Value;
            settings.Warmth = warmth.Value;
            settings.Saturation = saturation.Value;
            dirty = true;
            UpdateLabels();
            Apply();
        }

        void Apply()
        {
            bool ok;
            string device = enabled && gameWindow != IntPtr.Zero ? DeviceOf(gameWindow) : null;
            try { ok = GammaRamp.Apply(settings, device); }
            catch { ok = false; }
            try { if (Vibrance.Available) Vibrance.Apply(settings.Saturation, device); }
            catch { }
            status.Text = L.T("Windows hat diese Einstellung nicht übernommen. Etwas schwächer einstellen oder den Nachtmodus von Windows ausschalten.",
                              "Windows did not accept this setting. Try a weaker value or turn off Windows night light.");
            status.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>Gerätename des Bildschirms (z. B. \\.\DISPLAY1), auf dem das Fenster liegt.</summary>
        static string DeviceOf(IntPtr hwnd)
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            var mon = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            return mon != IntPtr.Zero && GetMonitorInfo(mon, ref info) && !string.IsNullOrEmpty(info.szDevice) ? info.szDevice : null;
        }

        void ResetValues()
        {
            loading = true;
            gamma.Value = 1;
            brightness.Value = 0;
            contrast.Value = 1;
            warmth.Value = 0;
            saturation.Value = 0;
            loading = false;
            OnSliderChanged();
            settings.Save();
            dirty = false;
        }

        void ApplyFilter(ColorFilter f)
        {
            loading = true;
            gamma.Value = f.Gamma;
            brightness.Value = f.Brightness;
            contrast.Value = f.Contrast;
            warmth.Value = f.Warmth;
            saturation.Value = Vibrance.Available ? f.Saturation : 0;
            loading = false;
            OnSliderChanged();
            settings.Save();
            dirty = false;
        }

        /// <summary>Markiert den Filter, der zu den aktuellen Reglern passt.</summary>
        void MarkFilter()
        {
            foreach (var f in Filters)
            {
                bool match = Math.Abs(settings.Gamma - f.Gamma) < 0.006 && Math.Abs(settings.Brightness - f.Brightness) < 0.006
                          && Math.Abs(settings.Contrast - f.Contrast) < 0.006 && Math.Abs(settings.Warmth - f.Warmth) < 0.026
                          && (!Vibrance.Available || Math.Abs(settings.Saturation - f.Saturation) < 0.026);
                filterButtons[f.Name].BorderBrush = match ? AccentBrush : DefaultBorderBrush;
            }
        }

        void UpdateLabels()
        {
            var c = CultureInfo.CurrentCulture;
            gammaValue.Text = settings.Gamma.ToString("0.00", c);
            int b = (int)Math.Round(settings.Brightness * 100);
            brightnessValue.Text = (b > 0 ? "+" : "") + b.ToString(c) + " %";
            contrastValue.Text = Math.Round(settings.Contrast * 100).ToString("0", c) + " %";
            int w = (int)Math.Round(settings.Warmth * 100);
            warmthValue.Text = w == 0 ? "Neutral"
                : w > 0 ? L.T("Wärmer ", "Warmer ") + w.ToString(c) + " %"
                : L.T("Kühler ", "Cooler ") + (-w).ToString(c) + " %";
            int sat = (int)Math.Round(settings.Saturation * 100);
            saturationValue.Text = Vibrance.Available ? (sat > 0 ? "+" : "") + sat.ToString(c) + " %" : "–";
            MarkFilter();
        }

        static TextBlock SectionTitle(string text)
        {
            return new TextBlock { Text = text, Foreground = MutedBrush, FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
        }

        static TextBlock ValueText()
        {
            return new TextBlock { Foreground = MutedBrush, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        }

        static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }
    }
}
