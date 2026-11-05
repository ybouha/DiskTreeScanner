using System.Drawing.Drawing2D;

namespace DiskTreeScanner.App.Ui;

/// <summary>Dark status bar: activity log (count + last message), indicators, Logs link.</summary>
internal sealed class DarkStatusBar : PaintedControl
{
    private string _message = string.Empty;
    private int _logCount;
    private (Color Dot, string Text)[] _indicators = [];
    private Rectangle _activityRect;
    private Rectangle _logsRect;
    private bool _hoverLogs;
    private bool _hoverActivity;

    public DarkStatusBar()
    {
        BackColor = Theme.Chrome;
    }

    public event EventHandler? ActivityClicked;

    public event EventHandler? LogsClicked;

    public string Message
    {
        get => _message;
        set
        {
            _message = value;
            Invalidate();
        }
    }

    public int LogCount
    {
        get => _logCount;
        set
        {
            _logCount = value;
            Invalidate();
        }
    }

    public (Color Dot, string Text)[] Indicators
    {
        get => _indicators;
        set
        {
            _indicators = value;
            Invalidate();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool logs = _logsRect.Contains(e.Location);
        bool activity = _activityRect.Contains(e.Location);
        Cursor = logs || activity ? Cursors.Hand : Cursors.Default;
        if (logs != _hoverLogs || activity != _hoverActivity)
        {
            _hoverLogs = logs;
            _hoverActivity = activity;
            Invalidate();
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (_logsRect.Contains(e.Location))
            LogsClicked?.Invoke(this, EventArgs.Empty);
        else if (_activityRect.Contains(e.Location))
            ActivityClicked?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Theme.Chrome);
        float s = DpiScale;
        TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
        Font font = Theme.Small(this);
        Font bold = Theme.Font(this, 12, FontStyle.Bold);

        // Right side first, to know how much room the message has.
        string logs = L.S("Logs");
        int logsWidth = TextRenderer.MeasureText(g, logs, font, Size.Empty, flags).Width;
        int right = Width - this.Px(12);
        _logsRect = new Rectangle(right - logsWidth, 0, logsWidth, Height);
        using (var underline = new Font(font, FontStyle.Underline))
            TextRenderer.DrawText(g, logs, underline, _logsRect, _hoverLogs ? Color.White : Theme.ChromeText, flags);
        right = _logsRect.Left - this.Px(18);
        using var dot = new SolidBrush(Color.White);
        for (int i = _indicators.Length - 1; i >= 0; i--)
        {
            var (color, text) = _indicators[i];
            int width = TextRenderer.MeasureText(g, text, font, Size.Empty, flags).Width;
            int left = right - width;
            TextRenderer.DrawText(g, text, font, new Rectangle(left, 0, width, Height), Theme.ChromeText, flags);
            dot.Color = color;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillEllipse(dot, left - this.Px(14), (Height - 8 * s) / 2, 8 * s, 8 * s);
            g.SmoothingMode = SmoothingMode.None;
            right = left - this.Px(14) - this.Px(18);
        }

        // Left: caret, "Activity log", badge, last message.
        int x = this.Px(12);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Icons.Caret(g, x, (Height - 10 * s) / 2, s, up: true, _hoverActivity ? Color.White : Theme.ChromeText);
        g.SmoothingMode = SmoothingMode.None;
        x += this.Px(18);
        string activity = L.S("ActivityLog");
        int activityWidth = TextRenderer.MeasureText(g, activity, bold, Size.Empty, flags).Width;
        TextRenderer.DrawText(g, activity, bold, new Rectangle(x, 0, activityWidth, Height), _hoverActivity ? Color.White : Theme.ChromeText, flags);
        x += activityWidth + this.Px(8);
        string count = Fmt.Count(_logCount);
        int countWidth = TextRenderer.MeasureText(g, count, Theme.Font(this, 11), Size.Empty, flags).Width + this.Px(12);
        var badge = new Rectangle(x, (Height - this.Px(16)) / 2, countWidth, this.Px(16));
        using (var badgeBack = new SolidBrush(Theme.ChromeBorder))
        using (var path = new GraphicsPath())
        {
            Icons.AddRoundedRect(path, badge.X, badge.Y, badge.Width, badge.Height, badge.Height / 2f);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillPath(badgeBack, path);
            g.SmoothingMode = SmoothingMode.None;
        }
        TextRenderer.DrawText(g, count, Theme.Font(this, 11), badge, Theme.ChromeText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        _activityRect = new Rectangle(this.Px(8), 0, badge.Right - this.Px(8), Height);
        x = badge.Right + this.Px(10);
        TextRenderer.DrawText(g, _message, font, new Rectangle(x, 0, Math.Max(0, right - x), Height), Theme.ChromeMenu, flags | TextFormatFlags.EndEllipsis);
    }
}
