using System.Diagnostics;
using System.Drawing.Drawing2D;
using TaskReminderTray.Services;

namespace TaskReminderTray;

internal sealed class PersistentNotificationForm : Form
{
    private static readonly Color Surface = Color.FromArgb(28, 31, 38);
    private static readonly Color Border = Color.FromArgb(57, 63, 74);
    private static readonly Color PrimaryText = Color.FromArgb(239, 242, 247);
    private static readonly Color SecondaryText = Color.FromArgb(151, 159, 173);
    private static readonly Color Accent = Color.FromArgb(80, 145, 232);

    private readonly Label _pendingLabel = new();
    private readonly LinkLabel _issueLink = new();
    private readonly Label _titleLabel = new();
    private readonly Label _statusLabel = new();
    private readonly Label _timeLabel = new();
    private readonly Button _acknowledgeButton = new();
    private PersistentNotification? _notification;
    private bool _shuttingDown;

    public event EventHandler<string>? AcknowledgeRequested;

    public PersistentNotificationForm()
    {
        Text = "任务状态变化";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(400, 224);
        BackColor = Surface;
        Font = new Font("Microsoft YaHei UI", 9F);
        BuildInterface();
        Paint += (_, e) =>
        {
            using var pen = new Pen(Border);
            e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        };
    }

    protected override bool ShowWithoutActivation => true;

    public void ShowNotification(PersistentNotification notification, int pendingCount)
    {
        _notification = notification;
        _pendingLabel.Text = pendingCount > 1 ? $"待确认 {pendingCount} 条" : "待确认";
        _issueLink.Text = notification.IssueKey;
        _issueLink.LinkColor = string.IsNullOrWhiteSpace(notification.SourceUrl)
            ? SecondaryText
            : Accent;
        _issueLink.Links.Clear();
        if (!string.IsNullOrWhiteSpace(notification.SourceUrl))
        {
            _issueLink.Links.Add(0, notification.IssueKey.Length, notification.SourceUrl);
        }
        _titleLabel.Text = notification.Title;
        _statusLabel.Text = $"{notification.PreviousStatus}  →  {notification.CurrentStatus}";
        _timeLabel.Text = $"变更于 {notification.ChangedAt.ToLocalTime():MM-dd HH:mm}";
        PositionAtBottomRight();
        if (!Visible)
        {
            Show();
        }
        else
        {
            Invalidate();
        }
    }

    public void HideNotification()
    {
        _notification = null;
        Hide();
    }

    public void Shutdown()
    {
        _shuttingDown = true;
        Close();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        UpdateRoundedRegion();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateRoundedRegion();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_shuttingDown && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            RequestAcknowledge();
        }
        base.OnFormClosing(e);
    }

    private void BuildInterface()
    {
        var heading = new Label
        {
            Text = "状态已更新",
            ForeColor = PrimaryText,
            Font = new Font(Font.FontFamily, 10.5F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(20, 17)
        };
        _pendingLabel.ForeColor = SecondaryText;
        _pendingLabel.AutoSize = false;
        _pendingLabel.TextAlign = ContentAlignment.MiddleRight;
        _pendingLabel.SetBounds(240, 16, 104, 24);
        var closeButton = new Button
        {
            Text = "×",
            FlatStyle = FlatStyle.Flat,
            ForeColor = SecondaryText,
            BackColor = Surface,
            TabStop = false,
            Font = new Font("Segoe UI", 13F),
            Cursor = Cursors.Hand
        };
        closeButton.FlatAppearance.BorderSize = 0;
        closeButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(47, 51, 60);
        closeButton.SetBounds(356, 8, 34, 34);
        closeButton.Click += (_, _) => RequestAcknowledge();

        var separator = new Panel
        {
            BackColor = Border,
            Location = new Point(20, 50),
            Size = new Size(360, 1)
        };
        _issueLink.Font = new Font(Font.FontFamily, 9F, FontStyle.Bold);
        _issueLink.ActiveLinkColor = Color.FromArgb(115, 171, 242);
        _issueLink.VisitedLinkColor = Accent;
        _issueLink.BackColor = Surface;
        _issueLink.AutoSize = false;
        _issueLink.SetBounds(20, 65, 100, 22);
        _issueLink.LinkClicked += (_, eventArgs) =>
            OpenIssue(eventArgs.Link?.LinkData as string);

        _titleLabel.ForeColor = PrimaryText;
        _titleLabel.AutoEllipsis = true;
        _titleLabel.AutoSize = false;
        _titleLabel.SetBounds(116, 64, 264, 24);

        _statusLabel.ForeColor = Color.FromArgb(199, 216, 239);
        _statusLabel.BackColor = Color.FromArgb(39, 49, 64);
        _statusLabel.Font = new Font(Font.FontFamily, 9.5F, FontStyle.Bold);
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.AutoEllipsis = true;
        _statusLabel.Padding = new Padding(10, 0, 10, 0);
        _statusLabel.SetBounds(20, 99, 360, 39);

        _timeLabel.ForeColor = SecondaryText;
        _timeLabel.AutoSize = true;
        _timeLabel.Location = new Point(20, 153);

        _acknowledgeButton.Text = "已知晓";
        _acknowledgeButton.ForeColor = Color.White;
        _acknowledgeButton.BackColor = Accent;
        _acknowledgeButton.FlatStyle = FlatStyle.Flat;
        _acknowledgeButton.FlatAppearance.BorderSize = 0;
        _acknowledgeButton.Cursor = Cursors.Hand;
        _acknowledgeButton.SetBounds(284, 175, 96, 34);
        _acknowledgeButton.Click += (_, _) => RequestAcknowledge();

        Controls.AddRange([heading, _pendingLabel, closeButton, separator,
            _issueLink, _titleLabel, _statusLabel, _timeLabel, _acknowledgeButton]);
    }

    private void RequestAcknowledge()
    {
        if (_notification is { } notification)
        {
            AcknowledgeRequested?.Invoke(this, notification.Id);
        }
    }

    private static void OpenIssue(string? sourceUrl)
    {
        if (Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
        }
    }

    private void PositionAtBottomRight()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16);
    }

    private void UpdateRoundedRegion()
    {
        if (Width <= 0 || Height <= 0)
        {
            return;
        }

        using var path = new GraphicsPath();
        const int diameter = 14;
        var bounds = new Rectangle(0, 0, Width, Height);
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        Region?.Dispose();
        Region = new Region(path);
    }
}
