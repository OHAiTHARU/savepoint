using System.Diagnostics;

namespace Savepoint.Agent.UI;

/// <summary>Pick a running process to watch (for the exe names list).</summary>
public sealed class ProcessPickerForm : Form
{
    private readonly ListView _list = new()
    {
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = true,
        Dock = DockStyle.Fill,
        HideSelection = false,
    };

    public ProcessPickerForm()
    {
        Text = "Pick running game";
        Icon = TrayIcons.App;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(560, 480);
        MinimizeBox = false;
        Font = SystemFonts.MessageBoxFont ?? Font;

        _list.Columns.Add("Process", 200);
        _list.Columns.Add("Window title", 320);

        var hint = new Label
        {
            Text = "Start the game, then pick it here. Processes with a window are listed first.",
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 28,
            Padding = new Padding(4, 6, 4, 0),
        };
        var ok = new Button { Text = "Add", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        Controls.Add(_list);
        Controls.Add(hint);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;
        _list.DoubleClick += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        Load += (_, _) => Fill();
    }

    public IReadOnlyList<string> SelectedExeNames =>
        _list.SelectedItems.Cast<ListViewItem>().Select(i => i.Text + ".exe").ToList();

    private void Fill()
    {
        var rows = new List<(string Name, string Title)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.SessionId == 0 || !seen.Add(p.ProcessName)) continue;
                rows.Add((p.ProcessName, p.MainWindowTitle));
            }
            catch (InvalidOperationException) { }
            finally { p.Dispose(); }
        }
        _list.BeginUpdate();
        foreach (var (name, title) in rows.OrderBy(r => string.IsNullOrEmpty(r.Title)).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            _list.Items.Add(new ListViewItem(new[] { name, title }));
        _list.EndUpdate();
    }
}
