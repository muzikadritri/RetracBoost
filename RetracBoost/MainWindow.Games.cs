using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace RetracBoost
{
    /// <summary>Karte im Games-Tab.</summary>
    sealed class GameCard : INotifyPropertyChanged
    {
        static readonly Brush FoundBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x00, 0xF5, 0xA0)));
        static readonly Brush MissingBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x4A, 0x58, 0x74)));

        static Brush Freeze(Brush b) { b.Freeze(); return b; }

        ImageSource image;
        string status = "";
        Brush statusBrush = MissingBrush;

        public GameCard(GameDef game) { Game = game; }

        public GameDef Game { get; private set; }
        public string Name { get { return Game.Name; } }
        public string Initials { get { return Game.Initials; } }
        public Visibility PluginVisibility { get { return Game.IsPlugin ? Visibility.Visible : Visibility.Collapsed; } }
        public string ImageOrigin { get; set; }
        public bool ImageDone { get; set; }
        public List<Row> Rows { get; set; }

        public ImageSource Image { get { return image; } set { image = value; Raise("Image"); } }
        public string Status { get { return status; } set { status = value; Raise("Status"); } }
        public Brush StatusBrush { get { return statusBrush; } private set { statusBrush = value; Raise("StatusBrush"); } }

        public void SetFound(bool found)
        {
            Status = found ? L.T("Auf diesem PC gefunden", "Found on this PC") : L.T("Nicht gefunden", "Not found");
            StatusBrush = found ? FoundBrush : MissingBrush;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        void Raise(string name) { var h = PropertyChanged; if (h != null) h(this, new PropertyChangedEventArgs(name)); }
    }

    /// <summary>Startoption im Games-Tab, wird nur zum Kopieren angezeigt.</summary>
    sealed class LaunchItem
    {
        public string Title { get; set; }
        public string Summary { get; set; }
        public string Value { get; set; }
        public string Hint { get; set; }
        public string CopyText { get; set; }
    }

    public partial class MainWindow
    {
        static readonly Brush NoteWarnBrush = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
        static readonly Brush NoteOkBrush = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0xA7));

        readonly ObservableCollection<GameCard> gameCards = new ObservableCollection<GameCard>();
        readonly HashSet<string> builtInGameIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        GameCard currentGame;

        void InitGames()
        {
            TabGames.Content = "Games";
            GamesTitle.Text = L.T("Spiele", "Games");
            GamesDesc.Text = L.T("Einstellungen pro Spiel. Geändert werden Windows-Werte und die Konfigurationsdateien der Spiele, nie das laufende Spiel. Für Anti-Cheat gibt es dabei nichts zu sehen.",
                                 "Settings per game. Changes go to Windows values and the games' config files, never the running game, so anti-cheat has nothing to see.");
            GamesAddBtn.Content = L.T("Plugin hinzufügen (ZIP)", "Add plugin (ZIP)");
            GamesFolderBtn.Content = L.T("Plugin-Ordner", "Plugin folder");
            GameBackBtn.Content = L.T("← Alle Spiele", "← All games");
            GameOptTitle.Text = L.T("Optimierungen", "Optimisations");
            GameOptDesc.Text = L.T("Jeder Schalter lässt sich wieder ausschalten, dann stehen die alten Werte wieder drin.",
                                   "Every switch can be turned off again, which puts the old values back.");
            GamePathTitle.Text = L.T("Spieldatei", "Game file");
            GamePathBtn.Content = L.T(".exe selbst wählen", "Pick the .exe yourself");
            GameRemoveBtn.Content = L.T("Plugin entfernen", "Remove plugin");
            GameLaunchTitle.Text = L.T("Startoptionen", "Launch options");

            var errors = new List<string>();
            var builtIn = BuiltInGames.All();
            foreach (var g in builtIn) builtInGameIds.Add(g.Id);
            var taken = new HashSet<string>(builtInGameIds, StringComparer.OrdinalIgnoreCase);
            var plugins = GamePlugins.LoadAll(taken, errors);

            foreach (var g in builtIn.Concat(plugins)) gameCards.Add(new GameCard(g));
            GameGrid.ItemsSource = gameCards;
            if (errors.Count > 0) ShowGamesNote(L.T("Nicht geladen: ", "Not loaded: ") + string.Join(" · ", errors), true);

            foreach (var c in gameCards) { var _ = RefreshCardAsync(c); }
        }

        void ShowGamesNote(string text, bool warn)
        {
            GamesNote.Text = text ?? "";
            GamesNote.Foreground = warn ? NoteWarnBrush : NoteOkBrush;
            GamesNote.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        async Task RefreshCardAsync(GameCard card)
        {
            card.Status = L.T("Suche …", "Looking …");
            try { await Task.Run(() => card.Game.Refresh()); } catch { }
            card.SetFound(card.Game.Installed);

            var result = await GameImages.LoadAsync(card.Game);
            card.Image = result.Item1;
            card.ImageOrigin = result.Item2;
            card.ImageDone = true;
            if (currentGame == card) ShowDetailImage(card);
        }

        // ------------------------------------------------------------------ Navigation

        void GameCard_Click(object sender, MouseButtonEventArgs e)
        {
            var card = (sender as FrameworkElement)?.DataContext as GameCard;
            if (card != null) ShowGameDetail(card);
        }

        void GameBack_Click(object sender, RoutedEventArgs e)
        {
            currentGame = null;
            GameDetailView.Visibility = Visibility.Collapsed;
            GameListView.Visibility = Visibility.Visible;
            FadeIn(GameListView);
        }

        static void FadeIn(UIElement el)
        {
            el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }

        void ShowDetailImage(GameCard card)
        {
            GameDetailImage.ImageSource = card.Image;
            GameDetailInitials.Text = card.Image == null ? card.Initials : "";
            GameDetailCredit.Text = card.ImageDone ? GameImages.Credit(card.Game, card.ImageOrigin) : "";
        }

        void ShowGameDetail(GameCard card)
        {
            currentGame = card;
            var g = card.Game;
            GameListView.Visibility = Visibility.Collapsed;
            GameDetailView.Visibility = Visibility.Visible;
            FadeIn(GameDetailView);

            GameDetailName.Text = g.Name;
            var sub = new List<string>();
            if (!string.IsNullOrEmpty(g.Publisher)) sub.Add(g.Publisher);
            sub.Add(string.IsNullOrEmpty(g.AntiCheat) ? L.T("kein Anti-Cheat", "no anti-cheat") : "Anti-Cheat: " + g.AntiCheat);
            GameDetailSub.Text = string.Join(" · ", sub);
            GameDetailDesc.Text = g.Description ?? "";
            GameDetailDesc.Visibility = string.IsNullOrEmpty(g.Description) ? Visibility.Collapsed : Visibility.Visible;
            ShowDetailImage(card);

            GamePathCard.Visibility = g.Exes.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateGamePathText(card);

            GamePluginCard.Visibility = g.IsPlugin ? Visibility.Visible : Visibility.Collapsed;
            if (g.IsPlugin)
                GamePluginInfo.Text = L.T("Plugin", "Plugin") + (string.IsNullOrEmpty(g.Author) ? "" : L.T(" von ", " by ") + g.Author)
                                    + " · " + L.T("Version ", "version ") + g.Version;

            if (card.Rows == null)
            {
                card.Rows = new List<Row>();
                foreach (var a in g.Actions)
                {
                    var t = a.CreateTweak(g);
                    if (t == null) continue;
                    bool needsAdmin = t is GamePriorityTweak;
                    var row = new Row(t, () => (!needsAdmin || Admin.IsElevated) && t.Relevant);
                    row.Busy = true;
                    row.Detail = L.T("Prüfe …", "Checking …");
                    row.UserChanged = OnRowToggled;
                    card.Rows.Add(row);
                }
            }
            GameRowList.ItemsSource = card.Rows;

            var launch = g.Actions.Where(a => a.Kind == GameActionKind.LaunchOptions).Select(a => new LaunchItem
            {
                Title = a.Title ?? L.T("Startoption für ", "Launch option for ") + GameAction.LauncherName(a.Launcher),
                Summary = a.Summary ?? "",
                Value = a.Value,
                Hint = GameAction.LauncherHint(a.Launcher),
                CopyText = L.T("Kopieren", "Copy")
            }).ToList();
            GameLaunchList.ItemsSource = launch;
            GameLaunchTitle.Visibility = launch.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            bool anyAdmin = g.Actions.Any(a => a.Kind == GameActionKind.Priority);
            GameAdminText.Text = L.T("Für die CPU-Priorität muss Retrac Boost als Administrator laufen.", "The CPU priority needs Retrac Boost to run as administrator.");
            GameAdminText.Visibility = anyAdmin && !Admin.IsElevated ? Visibility.Visible : Visibility.Collapsed;

            var _ = RefreshGameRowsAsync(card);
        }

        void UpdateGamePathText(GameCard card)
        {
            var found = card.Game.FoundExes;
            GamePathText.Text = found.Count > 0
                ? string.Join("\n", found)
                : L.T("Nicht gefunden. Gesucht wird in Steam-, Epic- und Riot-Bibliotheken nach ", "Not found. Looked in Steam, Epic and Riot libraries for ") + string.Join(", ", card.Game.Exes) + ".";
        }

        async Task RefreshGameRowsAsync(GameCard card)
        {
            await gate.WaitAsync();
            try
            {
                foreach (var row in card.Rows) { row.Busy = true; row.Detail = L.T("Prüfe …", "Checking …"); }
                try { await Task.Run(() => card.Game.Refresh()); } catch { }
                card.SetFound(card.Game.Installed);
                if (currentGame == card) UpdateGamePathText(card);

                foreach (var row in card.Rows)
                {
                    var t = row.Tweak;
                    var state = await Task.Run(() =>
                    {
                        bool on = false;
                        string detail;
                        try { on = t.Relevant && t.IsApplied(); } catch { }
                        try { detail = t.Detail; } catch (Exception ex) { detail = Shorten(ex.Message); }
                        return Tuple.Create(on, detail);
                    });
                    row.SetOn(state.Item1);
                    row.Detail = state.Item2;
                    row.Busy = false;
                    row.RefreshEnabled();
                }
            }
            finally { gate.Release(); }
        }

        // ------------------------------------------------------------------ Spieldatei / Startoptionen

        async void GamePath_Click(object sender, RoutedEventArgs e)
        {
            var card = currentGame;
            if (card == null || card.Game.Exes.Length == 0) return;
            var exes = card.Game.Exes;
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = card.Game.Name + " (" + string.Join(", ", exes) + ")|" + string.Join(";", exes),
                CheckFileExists = true,
                Title = L.T("Spieldatei von ", "Game file of ") + card.Game.Name
            };
            if (dlg.ShowDialog(this) != true) return;
            if (!exes.Contains(Path.GetFileName(dlg.FileName), StringComparer.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, L.T("Das ist nicht die Spieldatei. Gesucht: ", "That is not the game file. Expected: ") + string.Join(", ", exes),
                                "Retrac Boost", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            Store.TrySet(card.Game.PathKey, dlg.FileName);
            await RefreshGameRowsAsync(card);
        }

        void CopyLaunch_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            var text = btn?.Tag as string;
            if (string.IsNullOrEmpty(text)) return;
            try { Clipboard.SetText(text); }
            catch { return; }
            btn.Content = L.T("Kopiert", "Copied");
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
            timer.Tick += (s, a) => { timer.Stop(); btn.Content = L.T("Kopieren", "Copy"); };
            timer.Start();
        }

        // ------------------------------------------------------------------ Plugins

        void GamesFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(GamePlugins.Dir);
                Process.Start("explorer.exe", "\"" + GamePlugins.Dir + "\"");
            }
            catch (Exception ex) { ShowGamesNote(Shorten(ex.Message), true); }
        }

        async void GamesAdd_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = L.T("Plugin (*.zip)|*.zip", "Plugin (*.zip)|*.zip"),
                CheckFileExists = true,
                Title = L.T("Spiele-Plugin wählen", "Choose a game plugin")
            };
            if (dlg.ShowDialog(this) != true) return;

            GamePlugins.Package pkg;
            var installed = gameCards.Select(c => c.Game).ToList();
            try { pkg = await Task.Run(() => GamePlugins.Inspect(dlg.FileName, installed, builtInGameIds)); }
            catch (Exception ex)
            {
                ShowGamesNote(L.T("Plugin abgelehnt: ", "Plugin rejected: ") + ex.Message, true);
                return;
            }

            if (!PluginPreviewWindow.Ask(this, pkg)) return;

            try { await Task.Run(() => GamePlugins.Install(pkg)); }
            catch (Exception ex)
            {
                ShowGamesNote(L.T("Installation fehlgeschlagen: ", "Installation failed: ") + Shorten(ex.Message), true);
                return;
            }
            GameImages.Forget(pkg.Game.Id);

            var card = new GameCard(pkg.Game);
            var old = gameCards.FirstOrDefault(c => string.Equals(c.Game.Id, pkg.Game.Id, StringComparison.OrdinalIgnoreCase));
            if (old != null) gameCards[gameCards.IndexOf(old)] = card;
            else gameCards.Add(card);
            ShowGamesNote(L.T("Plugin „" + pkg.Game.Name + "“ installiert.", "Plugin “" + pkg.Game.Name + "” installed."), false);
            await RefreshCardAsync(card);
        }

        async void GameRemove_Click(object sender, RoutedEventArgs e)
        {
            var card = currentGame;
            if (card == null || !card.Game.IsPlugin) return;
            var answer = MessageBox.Show(this,
                L.T("„" + card.Game.Name + "“ entfernen? Eingeschaltete Optimierungen werden vorher zurückgesetzt.",
                    "Remove “" + card.Game.Name + "”? Optimisations that are on get reverted first."),
                "Retrac Boost", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            var failed = new List<string>();
            foreach (var row in card.Rows ?? new List<Row>())
            {
                if (!row.IsOn || !row.Tweak.WeApplied) continue;
                await RunOne(row, false);
                if (row.IsOn) failed.Add(row.Title);
            }
            if (failed.Count > 0)
            {
                ShowGamesNote(L.T("Nicht entfernt, weil sich das nicht zurücksetzen ließ: ", "Not removed because this could not be reverted: ") + string.Join(", ", failed), true);
                GameBack_Click(null, null);
                return;
            }

            try { await Task.Run(() => GamePlugins.Remove(card.Game)); }
            catch (Exception ex)
            {
                ShowGamesNote(L.T("Entfernen fehlgeschlagen: ", "Removing failed: ") + Shorten(ex.Message), true);
                return;
            }
            GameImages.Forget(card.Game.Id);
            gameCards.Remove(card);
            GameBack_Click(null, null);
            ShowGamesNote(L.T("Plugin „" + card.Game.Name + "“ entfernt.", "Plugin “" + card.Game.Name + "” removed."), false);
        }
    }
}
