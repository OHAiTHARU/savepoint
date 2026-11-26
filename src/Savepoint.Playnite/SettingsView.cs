using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

namespace Savepoint.Playnite
{
    /// <summary>Settings page, built in code (no XAML): linked games and launch options.</summary>
    public class SettingsView : UserControl
    {
        private readonly SavepointPlugin _plugin;
        private readonly SavepointSettingsViewModel _vm;
        private readonly DataGrid _grid;
        private readonly DataGridComboBoxColumn _savepointColumn;
        private readonly TextBlock _status = new TextBlock { Margin = new Thickness(0, 6, 0, 6), TextWrapping = TextWrapping.Wrap };
        private List<AgentGame> _games = new List<AgentGame>();

        public SettingsView(SavepointPlugin plugin, SavepointSettingsViewModel vm)
        {
            _plugin = plugin;
            _vm = vm;
            DataContext = vm;

            var root = new StackPanel { Margin = new Thickness(20) };

            root.Children.Add(Heading("Linked games"));
            root.Children.Add(Text(
                "Linked games pull the latest save from Savepoint before they start and back up after they stop. " +
                "Link games from their right-click menu (Savepoint → Link to Savepoint game), or auto-link below."));
            root.Children.Add(_status);

            _savepointColumn = new DataGridComboBoxColumn
            {
                Header = "Savepoint game",
                DisplayMemberPath = nameof(AgentGame.Name),
                SelectedValuePath = nameof(AgentGame.Id),
                SelectedValueBinding = new Binding(nameof(GameLink.SavepointGameId)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            };
            _grid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = true,
                SelectionMode = DataGridSelectionMode.Extended,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                MinHeight = 160,
                MaxHeight = 360,
                Margin = new Thickness(0, 0, 0, 8),
            };
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = "Playnite game",
                Binding = new Binding(nameof(GameLink.PlayniteGameName)),
                IsReadOnly = true,
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            });
            _grid.Columns.Add(_savepointColumn);
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = "Last known name",
                Binding = new Binding(nameof(GameLink.SavepointGameName)),
                IsReadOnly = true,
                Width = new DataGridLength(0.7, DataGridLengthUnitType.Star),
            });
            _grid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("Settings.Links"));
            root.Children.Add(_grid);

            var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 16) };
            buttons.Children.Add(Button("Auto-link library", (s, e) =>
            {
                _plugin.AutoLink(_plugin.Api.Database.Games.ToList(), showSummary: true);
                LoadGames(refresh: false);
            }));
            buttons.Children.Add(Button("Remove selected", (s, e) =>
            {
                foreach (var link in _grid.SelectedItems.Cast<GameLink>().ToList()) _vm.Settings.Links.Remove(link);
            }));
            buttons.Children.Add(Button("Refresh Savepoint games", (s, e) => LoadGames(refresh: true)));
            root.Children.Add(buttons);

            root.Children.Add(Heading("Launching"));
            var wait = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
            wait.Children.Add(new TextBlock { Text = "Wait up to", VerticalAlignment = VerticalAlignment.Center });
            var waitBox = new TextBox { Width = 60, Margin = new Thickness(8, 0, 8, 0) };
            waitBox.SetBinding(TextBox.TextProperty, new Binding("Settings.MaxWaitSeconds") { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            wait.Children.Add(waitBox);
            wait.Children.Add(new TextBlock { Text = "seconds for the save sync before starting the game", VerticalAlignment = VerticalAlignment.Center });
            root.Children.Add(wait);
            root.Children.Add(Check("Ask before launching if saves couldn't be synced (otherwise launch and show a notification)", "Settings.AskWhenUnsynced"));
            root.Children.Add(Check("Show a notification when a newer save was restored before launch", "Settings.NotifyOnRestore"));

            Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Loaded += (s, e) => LoadGames(refresh: true);
        }

        private void LoadGames(bool refresh)
        {
            var games = _plugin.GetSavepointGames(refresh);
            if (games == null)
            {
                _status.Text = "⚠ The Savepoint agent isn't running, so the game list can't be loaded. Existing links still work once it's running.";
                _games = new List<AgentGame>();
            }
            else
            {
                _games = games;
                _status.Text = $"Savepoint agent connected: {games.Count} game(s) available.";
                // Keep cached names current, and flag links to games that no longer exist.
                foreach (var link in _vm.Settings.Links)
                {
                    var g = games.FirstOrDefault(x => x.Id == link.SavepointGameId);
                    if (g != null) link.SavepointGameName = g.Name;
                    else if (!(link.SavepointGameName ?? "").StartsWith("⚠")) link.SavepointGameName = "⚠ missing: " + link.SavepointGameName;
                }
            }
            _savepointColumn.ItemsSource = _games;
            foreach (var link in _vm.Settings.Links)
            {
                link.PropertyChanged -= OnLinkChanged;
                link.PropertyChanged += OnLinkChanged;
            }
        }

        private void OnLinkChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(GameLink.SavepointGameId)) return;
            var link = (GameLink)sender;
            var g = _games.FirstOrDefault(x => x.Id == link.SavepointGameId);
            if (g != null) link.SavepointGameName = g.Name;
        }

        private static TextBlock Heading(string text) =>
            new TextBlock { Text = text, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 4) };

        private static TextBlock Text(string text) =>
            new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };

        private static Button Button(string text, RoutedEventHandler onClick)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(10, 4, 10, 4) };
            b.Click += onClick;
            return b;
        }

        private static CheckBox Check(string text, string path)
        {
            var c = new CheckBox { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, Margin = new Thickness(0, 6, 0, 0) };
            c.SetBinding(ToggleButton.IsCheckedProperty, new Binding(path));
            return c;
        }
    }
}
