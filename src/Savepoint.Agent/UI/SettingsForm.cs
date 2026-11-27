using Savepoint.Agent.Services;
using Savepoint.Core.State;

namespace Savepoint.Agent.UI;

/// <summary>Server URL, API key, start with Windows, watcher quiet period, poll interval.</summary>
public sealed class SettingsForm : Form
{
    private readonly AgentConfig _original;
    private readonly TextBox _url = new() { Width = 340, PlaceholderText = "savepoint.example.com" };
    private readonly TextBox _key = new() { Width = 340, UseSystemPasswordChar = true, PlaceholderText = "sp_…" };
    private readonly CheckBox _showKey = new() { Text = "Show", AutoSize = true };
    private readonly Button _test = new() { Text = "Test connection", AutoSize = true };
    private readonly Label _testResult = new() { AutoSize = true, MaximumSize = new Size(420, 0) };
    private readonly CheckBox _startup = new() { Text = "Start with Windows", AutoSize = true };
    private readonly CheckBox _updates = new() { Text = "Check for updates automatically", AutoSize = true };
    private readonly NumericUpDown _quiet = new() { Minimum = 1, Maximum = 120, Width = 70 };
    private readonly NumericUpDown _poll = new() { Minimum = 15, Maximum = 3600, Increment = 15, Width = 70 };
    private readonly NumericUpDown _pullTimeout = new() { Minimum = 5, Maximum = 600, Width = 70 };

    public SettingsForm(AgentConfig config)
    {
        _original = config;
        Result = config;
        Text = "Savepoint settings";
        Icon = TrayIcons.App;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);
        Font = SystemFonts.MessageBoxFont ?? Font;

        _url.Text = config.ServerUrl;
        _key.Text = config.ApiKey;
        _startup.Checked = config.StartWithWindows;
        _updates.Checked = config.CheckForUpdates;
        _quiet.Value = Math.Clamp(config.QuietMinutes, 1, 120);
        _poll.Value = Math.Clamp(config.PollSeconds, 15, 3600);
        _pullTimeout.Value = Math.Clamp(config.PullTimeoutSeconds, 5, 600);

        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        void Row(string label, Control control)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 8, 0) });
            grid.Controls.Add(control);
        }

        Row("Server URL", _url);
        var keyRow = Flow(_key, _showKey);
        Row("API key", keyRow);
        Row("", Hint("Create a key for this PC on the server's Machines page."));
        Row("", Flow(_test, _testResult));
        Row("", _startup);
        Row("", _updates);
        Row("Back up after", Flow(_quiet, Hint("minutes without save changes (checkpoint)")));
        Row("Check server every", Flow(_poll, Hint("seconds")));
        Row("Playnite pull timeout", Flow(_pullTimeout, Hint("seconds")));

        var ok = new Button { Text = "Save", DialogResult = DialogResult.None, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Bottom, Padding = new Padding(0, 12, 0, 0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        var outer = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        outer.Controls.Add(grid);
        outer.Controls.Add(buttons);
        Controls.Add(outer);
        AcceptButton = ok;
        CancelButton = cancel;

        _showKey.CheckedChanged += (_, _) => _key.UseSystemPasswordChar = !_showKey.Checked;
        _test.Click += async (_, _) => await TestAsync();
        ok.Click += (_, _) => Save();
    }

    public AgentConfig Result { get; private set; }

    private static FlowLayoutPanel Flow(params Control[] controls)
    {
        var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        f.Controls.AddRange(controls);
        return f;
    }

    private static Label Hint(string text) =>
        new() { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(4, 6, 0, 0) };

    private async Task TestAsync()
    {
        _test.Enabled = false;
        _testResult.ForeColor = SystemColors.ControlText;
        _testResult.Text = "Connecting…";
        var (ok, message) = await AgentService.TestConnectionAsync(_url.Text, _key.Text.Trim());
        _testResult.ForeColor = ok ? Color.SeaGreen : Color.Firebrick;
        _testResult.Text = message;
        _test.Enabled = true;
    }

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(_url.Text) || string.IsNullOrWhiteSpace(_key.Text))
        {
            MessageBox.Show(this, "Enter the server URL and this PC's API key.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Result = new AgentConfig
        {
            ServerUrl = _url.Text.Trim(),
            ApiKey = _key.Text.Trim(),
            MachineId = _original.MachineId,
            MachineName = _original.MachineName,
            StartWithWindows = _startup.Checked,
            CheckForUpdates = _updates.Checked,
            QuietMinutes = (int)_quiet.Value,
            PollSeconds = (int)_poll.Value,
            RescanMinutes = _original.RescanMinutes,
            PullTimeoutSeconds = (int)_pullTimeout.Value,
            Paused = _original.Paused,
        };
        DialogResult = DialogResult.OK;
        Close();
    }
}
