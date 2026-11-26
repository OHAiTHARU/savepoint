using Savepoint.Agent.Services;
using Savepoint.Core.Api;
using Savepoint.Core.Paths;

namespace Savepoint.Agent.UI;

/// <summary>Add/Edit game: name, roots (with Browse… for common save locations), exe names,
/// excludes, sync mode and this PC's path overrides (DESIGN §6 "Tray UI").</summary>
public sealed class GameEditForm : Form
{
    private static readonly (string Label, string Template)[] CommonLocations =
    {
        ("AppData (Roaming)", "%APPDATA%"),
        ("Local AppData", "%LOCALAPPDATA%"),
        ("LocalLow", "%LOCALLOW%"),
        ("Documents", "%DOCUMENTS%"),
        ("Documents\\My Games", @"%DOCUMENTS%\My Games"),
        ("Saved Games", "%SAVEDGAMES%"),
        ("CODEX (Public Documents)", @"%PUBLIC%\Documents\Steam\CODEX"),
        ("Goldberg SteamEmu Saves", @"%APPDATA%\Goldberg SteamEmu Saves"),
        ("GSE Saves", @"%APPDATA%\GSE Saves"),
    };

    private readonly AgentService _agent;
    private readonly Game? _game;
    private readonly TextBox _name = new() { Width = 420 };
    private readonly DataGridView _roots = new()
    {
        Width = 640,
        Height = 140,
        AllowUserToAddRows = true,
        AllowUserToResizeRows = false,
        RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        SelectionMode = DataGridViewSelectionMode.CellSelect,
        EditMode = DataGridViewEditMode.EditOnEnter,
    };
    private readonly Label _preview = new() { AutoSize = true, MaximumSize = new Size(640, 0), ForeColor = SystemColors.GrayText };
    private readonly TextBox _exes = new() { Multiline = true, Width = 300, Height = 80, ScrollBars = ScrollBars.Vertical };
    private readonly TextBox _excludes = new() { Multiline = true, Width = 320, Height = 80, ScrollBars = ScrollBars.Vertical, PlaceholderText = "**/*.tmp" };
    private readonly RadioButton _auto = new() { Text = "Auto: restore and resolve conflicts automatically (newer wins)", AutoSize = true };
    private readonly RadioButton _manual = new() { Text = "Manual: ask me first", AutoSize = true };
    private readonly Button _save = new() { Text = "Save", AutoSize = true };

    public GameEditForm(AgentService agent, Game? game)
    {
        _agent = agent;
        _game = game;
        Text = game is null ? "Add game" : $"Edit {game.Name}";
        Icon = TrayIcons.App;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);
        Font = SystemFonts.MessageBoxFont ?? Font;

        _roots.Columns.Add(new DataGridViewTextBoxColumn { Name = "alias", HeaderText = "Alias", FillWeight = 18 });
        _roots.Columns.Add(new DataGridViewTextBoxColumn { Name = "path", HeaderText = "Path template (all PCs)", FillWeight = 45 });
        _roots.Columns.Add(new DataGridViewTextBoxColumn { Name = "override", HeaderText = "This PC only (optional)", FillWeight = 30 });
        _roots.Columns.Add(new DataGridViewButtonColumn { Name = "browse", HeaderText = "", Text = "Browse…", UseColumnTextForButtonValue = true, FillWeight = 12 });

        if (game is not null)
        {
            _name.Text = game.Name;
            foreach (var r in game.Roots)
            {
                var ovr = r.ResolvedTemplate is not null && r.ResolvedTemplate != r.PathTemplate ? r.ResolvedTemplate : "";
                _roots.Rows.Add(r.Alias, r.PathTemplate, ovr);
            }
            _exes.Text = string.Join(Environment.NewLine, game.ExeNames);
            _excludes.Text = string.Join(Environment.NewLine, game.Excludes);
            (game.IsAuto ? _auto : _manual).Checked = true;
        }
        else
        {
            _roots.Rows.Add("saves", "", "");
            _auto.Checked = true;
        }

        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        layout.Controls.Add(Caption("Name"));
        layout.Controls.Add(_name);
        layout.Controls.Add(Caption("Save folders"));
        layout.Controls.Add(Hint("Variables: %APPDATA% %LOCALAPPDATA% %LOCALLOW% %USERPROFILE% %DOCUMENTS% %SAVEDGAMES% %PUBLIC%, or an absolute path."));
        layout.Controls.Add(_roots);
        layout.Controls.Add(_preview);

        var pick = new Button { Text = "Pick running…", AutoSize = true };
        var browseExe = new Button { Text = "Browse…", AutoSize = true };
        var exeButtons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Margin = new Padding(0) };
        exeButtons.Controls.AddRange(new Control[] { pick, browseExe });
        var exeBox = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        exeBox.Controls.AddRange(new Control[] { _exes, exeButtons });

        var lists = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
        lists.Controls.Add(Caption("Game exe names (one per line)"), 0, 0);
        lists.Controls.Add(Caption("Excludes (globs, one per line)"), 1, 0);
        lists.Controls.Add(exeBox, 0, 1);
        lists.Controls.Add(_excludes, 1, 1);
        layout.Controls.Add(lists);

        layout.Controls.Add(Caption("Sync mode"));
        var modes = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Margin = new Padding(0) };
        modes.Controls.AddRange(new Control[] { _auto, _manual });
        layout.Controls.Add(modes);

        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 12, 0, 0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_save);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AcceptButton = _save;
        CancelButton = cancel;

        _roots.CellContentClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.ColumnIndex == _roots.Columns["browse"]!.Index) ShowBrowseMenu(e.RowIndex);
        };
        _roots.CellEndEdit += (_, _) => UpdatePreview();
        _roots.CurrentCellChanged += (_, _) => UpdatePreview();
        _roots.DefaultValuesNeeded += (_, e) => e.Row.Cells["alias"].Value = NextAlias();
        pick.Click += (_, _) => PickRunning();
        browseExe.Click += (_, _) => BrowseExe();
        _save.Click += async (_, _) => await SaveAsync();
        Shown += (_, _) => UpdatePreview();
    }

    private static Label Caption(string text) =>
        new() { Text = text, AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont ?? DefaultFont, FontStyle.Bold), Margin = new Padding(0, 10, 0, 2) };

    private static Label Hint(string text) =>
        new() { Text = text, AutoSize = true, MaximumSize = new Size(640, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 4) };

    private string NextAlias()
    {
        var used = RootRows().Select(r => r.Alias).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!used.Contains("saves")) return "saves";
        for (var i = 2; ; i++) if (!used.Contains($"root{i}")) return $"root{i}";
    }

    private IEnumerable<(string Alias, string Path, string Override)> RootRows() =>
        _roots.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow).Select(r => (
            (r.Cells["alias"].Value as string ?? "").Trim(),
            (r.Cells["path"].Value as string ?? "").Trim(),
            (r.Cells["override"].Value as string ?? "").Trim()))
        .Where(r => r.Item1.Length > 0 || r.Item2.Length > 0);

    private void UpdatePreview()
    {
        var row = _roots.CurrentRow;
        if (row is null || row.IsNewRow) { _preview.Text = ""; return; }
        var path = (row.Cells["override"].Value as string)?.Trim();
        if (string.IsNullOrEmpty(path)) path = (row.Cells["path"].Value as string)?.Trim();
        _preview.Text = string.IsNullOrEmpty(path) ? "" : "On this PC: " + _agent.ResolvePreview(path);
    }

    private void ShowBrowseMenu(int rowIndex)
    {
        var menu = new ContextMenuStrip();
        foreach (var (label, template) in CommonLocations)
        {
            var start = _agent.ResolvePreview(template);
            var item = menu.Items.Add(label, null, (_, _) => BrowseFolder(rowIndex, start));
            item.ToolTipText = start;
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Other folder…", null, (_, _) => BrowseFolder(rowIndex, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        var rect = _roots.GetCellDisplayRectangle(_roots.Columns["browse"]!.Index, rowIndex, false);
        menu.Show(_roots, new Point(rect.Left, rect.Bottom));
    }

    private void BrowseFolder(int rowIndex, string start)
    {
        while (!string.IsNullOrEmpty(start) && !Directory.Exists(start)) start = Path.GetDirectoryName(start) ?? "";
        using var dlg = new FolderBrowserDialog
        {
            Description = "Choose the folder that holds the saves",
            UseDescriptionForTitle = true,
            InitialDirectory = start,
            ShowNewFolderButton = false,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var template = PathTemplate.ToTemplate(dlg.SelectedPath, _agent.Paths);
        if (rowIndex >= _roots.Rows.Count - 1 && _roots.Rows[rowIndex].IsNewRow)
            rowIndex = _roots.Rows.Add(NextAlias(), template, "");
        else
            _roots.Rows[rowIndex].Cells["path"].Value = template;
        _roots.CurrentCell = _roots.Rows[rowIndex].Cells["path"];
        UpdatePreview();
    }

    private void AddExes(IEnumerable<string> names)
    {
        var existing = _exes.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        foreach (var n in names)
        {
            if (!existing.Contains(n, StringComparer.OrdinalIgnoreCase)) existing.Add(n);
        }
        _exes.Lines = existing.ToArray();
    }

    private void PickRunning()
    {
        using var picker = new ProcessPickerForm();
        if (picker.ShowDialog(this) == DialogResult.OK) AddExes(picker.SelectedExeNames);
    }

    private void BrowseExe()
    {
        using var dlg = new OpenFileDialog { Filter = "Programs (*.exe)|*.exe", Title = "Choose the game's .exe" };
        if (dlg.ShowDialog(this) == DialogResult.OK) AddExes(new[] { Path.GetFileName(dlg.FileName) });
    }

    private async Task SaveAsync()
    {
        var rows = RootRows().ToList();
        string? problem = null;
        if (string.IsNullOrWhiteSpace(_name.Text)) problem = "Give the game a name.";
        else if (rows.Count == 0) problem = "Add at least one save folder.";
        else if (rows.Any(r => r.Alias.Length == 0 || r.Path.Length == 0)) problem = "Every save folder needs an alias and a path.";
        else if (rows.GroupBy(r => r.Alias, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1)) problem = "Aliases must be unique.";
        if (problem is not null)
        {
            MessageBox.Show(this, problem, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var input = new GameInput
        {
            Name = _name.Text.Trim(),
            Roots = rows.Select(r => new RootInput(r.Alias, r.Path)).ToList(),
            ExeNames = Lines(_exes),
            Excludes = Lines(_excludes),
            SyncMode = _auto.Checked ? SyncModes.Auto : SyncModes.Manual,
        };
        var overrides = rows.ToDictionary(r => r.Alias, r => r.Override.Length == 0 ? null : (string?)r.Override);

        _save.Enabled = false;
        UseWaitCursor = true;
        try
        {
            await _agent.SaveGameAsync(_game?.Id, input, overrides);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex) when (ex is ApiException or ServerUnreachableException or InvalidOperationException)
        {
            MessageBox.Show(this, $"Couldn't save the game:\n{ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _save.Enabled = true;
            UseWaitCursor = false;
        }
    }

    private static List<string> Lines(TextBox box) =>
        box.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().ToList();
}
