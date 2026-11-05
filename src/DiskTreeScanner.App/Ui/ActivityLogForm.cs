using DiskTreeScanner.Core.Scanning;

namespace DiskTreeScanner.App.Ui;

/// <summary>Live view of the activity log (scan decisions, raw volume access, warnings).</summary>
internal sealed class ActivityLogForm : Form
{
    private readonly ActivityLog _log;
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
        BorderStyle = BorderStyle.None,
    };

    public ActivityLogForm(ActivityLog log, Icon icon)
    {
        _log = log;
        Text = L.S("LogTitle");
        Icon = icon;
        Size = new Size(this.Px(900), this.Px(420));
        StartPosition = FormStartPosition.CenterParent;
        Font = Theme.Body(this);
        _list.Columns.Add("", this.Px(90));
        _list.Columns.Add("", this.Px(80));
        _list.Columns.Add("", this.Px(700));
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = this.Px(40), Padding = new Padding(this.Px(6)) };
        var close = new Button { Text = L.S("Close"), AutoSize = true };
        close.Click += (_, _) => Close();
        var copy = new Button { Text = L.S("CopyAll"), AutoSize = true };
        copy.Click += (_, _) => Shell.CopyPath(string.Join(Environment.NewLine, _log.Snapshot().Select(e => e.ToString())));
        var folder = new Button { Text = L.S("OpenLogFolder"), AutoSize = true };
        folder.Click += (_, _) =>
        {
            if (_log.FilePath is { } path)
                Shell.OpenInExplorer(path, isDirectory: false);
        };
        buttons.Controls.AddRange([close, copy, folder]);
        Controls.Add(_list);
        Controls.Add(buttons);
        foreach (LogEntry entry in log.Snapshot())
            Add(entry);
        log.EntryAdded += OnEntry;
    }

    private void OnEntry(LogEntry entry)
    {
        if (IsDisposed)
            return;
        BeginInvoke(() => Add(entry));
    }

    private void Add(LogEntry entry)
    {
        var item = new ListViewItem([entry.Time.ToString("HH:mm:ss"), entry.Level.ToString(), entry.Message]);
        if (entry.Level == LogLevel.Warning)
            item.ForeColor = Theme.Warning;
        else if (entry.Level == LogLevel.Error)
            item.ForeColor = Theme.StopActive;
        _list.Items.Add(item);
        item.EnsureVisible();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _log.EntryAdded -= OnEntry;
        base.OnFormClosed(e);
    }
}
