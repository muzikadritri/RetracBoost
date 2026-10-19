using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;

namespace RetracBoost
{
    /// <summary>
    /// Ingame-Menü (Alt+L): eigenes randloses Fenster über dem Spiel. Es wird nichts in das Spiel geladen,
    /// die Werte wirken systemweit über die Gamma-Kurve (siehe <see cref="GammaRamp"/>).
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

        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public int dwFlags; }

        const uint MonitorDefaultToNearest = 2;
        const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80;
        const double MenuWidth = 340;

        static readonly Brush TextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF0, 0xF3, 0xF9)));
        static readonly Brush MutedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0xA7)));
        static readonly Brush WarnBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)));
        static readonly Brush LineBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x20, 0x27, 0x38)));

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
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='Button'>
        <Border x:Name='B' Background='#161C2B' BorderBrush='#2D3850' BorderThickness='1' CornerRadius='7' Padding='12,6'>
          <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'>
            <Setter TargetName='B' Property='Background' Value='#1E263A'/>
          </Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";

        readonly ColorSettings settings;
        readonly Slider gamma, brightness, contrast, warmth;
        readonly TextBlock gammaValue = ValueText(), brightnessValue = ValueText(), contrastValue = ValueText(), warmthValue = ValueText();
        readonly TextBlock pingText, status;
        readonly CheckBox pingSwitch;
        readonly Func<bool> pingOverlayOn;
        readonly Action<bool> setPingOverlay;
        IntPtr previous;
        bool loading, dirty, hiding;

        public GameMenuWindow(Style switchStyle, Func<bool> pingOverlayOn, Action<bool> setPingOverlay)
        {
            this.pingOverlayOn = pingOverlayOn;
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

            gamma = AddSlider(root, sliderStyle, "Gamma", gammaValue, ColorSettings.GammaMin, ColorSettings.GammaMax, 0.01);
            brightness = AddSlider(root, sliderStyle, L.T("Helligkeit", "Brightness"), brightnessValue, ColorSettings.BrightnessMin, ColorSettings.BrightnessMax, 0.01);
            contrast = AddSlider(root, sliderStyle, L.T("Kontrast", "Contrast"), contrastValue, ColorSettings.ContrastMin, ColorSettings.ContrastMax, 0.01);
            warmth = AddSlider(root, sliderStyle, L.T("Farbton", "Color tone"), warmthValue, ColorSettings.WarmthMin, ColorSettings.WarmthMax, 0.05);

            status = new TextBlock { Foreground = WarnBrush, FontSize = 11, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 2, 0, 8) };
            root.Children.Add(status);

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
                Text = L.T("Nur im Fenster- oder randlosen Modus sichtbar, nicht im exklusiven Vollbild. Die Werte gelten für den ganzen Bildschirm und werden beim Beenden von Retrac Boost zurückgesetzt.",
                           "Only visible in windowed or borderless mode, not in exclusive fullscreen. The values apply to the whole screen and are reset when Retrac Boost closes."),
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

        /// <summary>Gespeicherte Werte beim App-Start anwenden.</summary>
        public void ApplySaved()
        {
            if (!settings.IsDefault) Apply();
        }

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
            dirty = true;
            UpdateLabels();
            Apply();
        }

        void Apply()
        {
            bool ok;
            try { ok = GammaRamp.Apply(settings); }
            catch { ok = false; }
            status.Text = L.T("Windows hat diese Einstellung nicht übernommen. Etwas schwächer einstellen oder den Nachtmodus von Windows ausschalten.",
                              "Windows did not accept this setting. Try a weaker value or turn off Windows night light.");
            status.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        }

        void ResetValues()
        {
            loading = true;
            gamma.Value = 1;
            brightness.Value = 0;
            contrast.Value = 1;
            warmth.Value = 0;
            loading = false;
            OnSliderChanged();
            settings.Save();
            dirty = false;
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
        }

        static TextBlock ValueText()
        {
            return new TextBlock { Foreground = MutedBrush, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        }

        static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }
    }
}
