using System.Diagnostics;
using TaskReminderTray.Services;

namespace TaskReminderTray;

internal sealed class NotificationCenterForm : Form
{
    private static readonly Color Background = Color.FromArgb(24, 27, 33);
    private static readonly Color Surface = Color.FromArgb(31, 35, 43);
    private static readonly Color SurfaceUnread = Color.FromArgb(35, 42, 52);
    private static readonly Color Border = Color.FromArgb(54, 60, 71);
    private static readonly Color PrimaryText = Color.FromArgb(244, 246, 250);
    private static readonly Color SecondaryText = Color.FromArgb(184, 194, 210);
    private static readonly Color MutedText = Color.FromArgb(126, 139, 158);
    private static readonly Color Blue = Color.FromArgb(88, 142, 238);
    private static readonly Color Orange = Color.FromArgb(244, 171, 68);

    private readonly Label _summaryLabel = new();
    private readonly Button _acknowledgeAllButton = new();
    private readonly FlowLayoutPanel _headingStack = new();
    private readonly FlowLayoutPanel _list = new();
    private readonly Label _emptyLabel = new();
    private IReadOnlyList<PersistentNotification> _notifications = [];
    private bool _shuttingDown;

    public event EventHandler<string>? AcknowledgeRequested;
    public event EventHandler? AcknowledgeAllRequested;

    public NotificationCenterForm()
    {
        Text = "通知中心";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(640, 560);
        MinimumSize = new Size(560, 420);
        BackColor = Background;
        Font = new Font("Microsoft YaHei UI", 9F);
        BuildInterface();
        Deactivate += (_, _) => Hide();
        Paint += (_, e) =>
        {
            using var pen = new Pen(Border);
            e.Graphics.DrawRectangle(pen, 0, 0,
                ClientSize.Width - 1, ClientSize.Height - 1);
        };
    }

    public void ShowCenter(IReadOnlyList<PersistentNotification> notifications,
        Rectangle anchorBounds)
    {
        SetNotifications(notifications);
        PositionNear(anchorBounds);
        if (!Visible)
        {
            Show();
        }

        Activate();
        BringToFront();
    }

    public void SetNotifications(IReadOnlyList<PersistentNotification> notifications)
    {
        _notifications = notifications;
        var unreadCount = notifications.Count(notification => !notification.IsRead);
        _summaryLabel.Text = unreadCount > 0
            ? $"{unreadCount} 条未读 · 最近 {notifications.Count} 条变化"
            : notifications.Count > 0
                ? $"全部已读 · 最近 {notifications.Count} 条变化"
                : "工单状态变化会保存在这里";
        _acknowledgeAllButton.Enabled = unreadCount > 0;
        _acknowledgeAllButton.Visible = unreadCount > 0;
        RebuildList();
    }

    public void Shutdown()
    {
        _shuttingDown = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_shuttingDown && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnFormClosing(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Hide();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void BuildInterface()
    {
        var heading = new Label
        {
            Text = "通知中心",
            ForeColor = PrimaryText,
            Font = new Font(Font.FontFamily, 15F, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0)
        };
        _summaryLabel.ForeColor = MutedText;
        _summaryLabel.AutoSize = true;
        _summaryLabel.Margin = new Padding(1, 2, 0, 0);

        // 让布局系统按当前 DPI 下的真实字体高度排列两行文字，避免固定 Y 坐标
        // 在 125%/150% 缩放下造成标题裁切或副标题覆盖。
        _headingStack.FlowDirection = FlowDirection.TopDown;
        _headingStack.WrapContents = false;
        _headingStack.AutoSize = false;
        _headingStack.BackColor = Background;
        _headingStack.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _headingStack.SetBounds(22, 12, ClientSize.Width - 190, 66);
        _headingStack.Controls.AddRange([heading, _summaryLabel]);

        _acknowledgeAllButton.Text = "全部标为已读";
        StyleButton(_acknowledgeAllButton, secondary: false);
        _acknowledgeAllButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _acknowledgeAllButton.SetBounds(ClientSize.Width - 150, 20, 128, 36);
        _acknowledgeAllButton.Click += (_, _) => AcknowledgeAllRequested?.Invoke(this,
            EventArgs.Empty);

        var separator = new Panel
        {
            BackColor = Border,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Location = new Point(22, 82),
            Size = new Size(ClientSize.Width - 44, 1)
        };

        _list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom |
                       AnchorStyles.Left | AnchorStyles.Right;
        _list.AutoScroll = true;
        _list.FlowDirection = FlowDirection.TopDown;
        _list.WrapContents = false;
        _list.BackColor = Background;
        _list.Padding = new Padding(0, 0, 8, 0);
        _list.SetBounds(22, 99, ClientSize.Width - 44, ClientSize.Height - 121);
        _list.SizeChanged += (_, _) => ResizeRows();

        _emptyLabel.Text = "暂无状态变化\n刷新后检测到的工单状态变化会出现在这里";
        _emptyLabel.ForeColor = MutedText;
        _emptyLabel.TextAlign = ContentAlignment.MiddleCenter;
        _emptyLabel.Anchor = AnchorStyles.Top | AnchorStyles.Bottom |
                             AnchorStyles.Left | AnchorStyles.Right;
        _emptyLabel.SetBounds(22, 99, ClientSize.Width - 44, ClientSize.Height - 121);
        _emptyLabel.Visible = false;

        Controls.AddRange([_headingStack, _acknowledgeAllButton,
            separator, _list, _emptyLabel]);
    }

    private void RebuildList()
    {
        _list.SuspendLayout();
        try
        {
            foreach (var control in _list.Controls.Cast<Control>().ToArray())
            {
                control.Dispose();
            }
            _list.Controls.Clear();
            var isEmpty = _notifications.Count == 0;
            _list.Visible = !isEmpty;
            _emptyLabel.Visible = isEmpty;
            _emptyLabel.BringToFront();
            foreach (var notification in _notifications)
            {
                _list.Controls.Add(BuildNotificationRow(notification));
            }
            ResizeRows();
        }
        finally
        {
            _list.ResumeLayout();
        }
    }

    private Control BuildNotificationRow(PersistentNotification notification)
    {
        var rowWidth = Math.Max(300, _list.ClientSize.Width - _list.Padding.Horizontal -
                                    SystemInformation.VerticalScrollBarWidth);
        var row = new Panel
        {
            Width = rowWidth,
            Height = 108,
            Margin = new Padding(0, 0, 0, 10),
            BackColor = notification.IsRead ? Surface : SurfaceUnread,
            Tag = notification.Id
        };
        row.Paint += (_, e) =>
        {
            using var pen = new Pen(notification.IsRead ? Border : Color.FromArgb(62, 79, 101));
            e.Graphics.DrawRectangle(pen, 0, 0, row.Width - 1, row.Height - 1);
            if (!notification.IsRead)
            {
                using var accent = new SolidBrush(Orange);
                e.Graphics.FillRectangle(accent, 0, 0, 3, row.Height);
            }
            using var headingFont = new Font(Font.FontFamily, 10F, FontStyle.Bold);
            TextRenderer.DrawText(e.Graphics,
                $"{notification.IssueKey}  {notification.Title}", headingFont,
                new Rectangle(17, 10, Math.Max(80, row.ClientSize.Width - 34), 28),
                PrimaryText, TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                             TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
                             TextFormatFlags.NoPadding);
        };

        var status = new Label
        {
            Text = NotificationDescription(notification),
            ForeColor = notification.IsRead ? MutedText : Color.FromArgb(199, 216, 239),
            Font = new Font(Font.FontFamily, 9F, FontStyle.Bold),
            AutoSize = false,
            AutoEllipsis = true
        };
        status.SetBounds(17, 48, Math.Max(120, row.Width - 195), 24);
        var time = new Label
        {
            Text = $"{notification.ChangedAt.ToLocalTime():MM-dd HH:mm}" +
                   (notification.IsRead ? " · 已读" : " · 未读"),
            ForeColor = MutedText,
            AutoSize = true,
            Location = new Point(17, 78)
        };

        var open = new Button { Text = "打开" };
        StyleButton(open, secondary: true);
        open.SetBounds(row.Width - 161, 54, 62, 32);
        open.Enabled = Uri.TryCreate(notification.SourceUrl, UriKind.Absolute, out _);
        open.Click += (_, _) => OpenIssue(notification.SourceUrl);

        var acknowledge = new Button
        {
            Text = notification.IsRead ? "已读" : "标为已读",
            Enabled = !notification.IsRead
        };
        StyleButton(acknowledge, secondary: notification.IsRead);
        acknowledge.SetBounds(row.Width - 91, 54, 82, 32);
        acknowledge.Click += (_, _) => AcknowledgeRequested?.Invoke(this, notification.Id);

        row.Controls.AddRange([status, time, open, acknowledge]);
        row.Layout += (_, _) => LayoutNotificationRow(row, status, open,
            acknowledge);
        LayoutNotificationRow(row, status, open, acknowledge);
        return row;
    }

    internal static string NotificationDescription(PersistentNotification notification) =>
        notification.Kind == NotificationKind.DueToday
            ? $"今天到期  ·  {notification.CurrentStatus}"
            : $"{notification.PreviousStatus}  →  {notification.CurrentStatus}";

    private static void LayoutNotificationRow(
        Control row,
        Control status,
        Control open,
        Control acknowledge)
    {
        status.Width = Math.Max(100, row.ClientSize.Width - status.Left - 195);
        open.Left = row.ClientSize.Width - 161;
        acknowledge.Left = row.ClientSize.Width - 91;
    }

    private void ResizeRows()
    {
        var width = Math.Max(300, _list.ClientSize.Width - _list.Padding.Horizontal -
                                  (_list.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0));
        foreach (Control row in _list.Controls)
        {
            row.Width = width;
            row.PerformLayout();
        }
    }

    internal static Size LogicalSizeForDpi(int dpi) => new(
        (int)Math.Round(640 * dpi / 96F),
        (int)Math.Round(560 * dpi / 96F));

    private static void StyleButton(Button button, bool secondary)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = secondary ? 1 : 0;
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(46, 52, 63);
        button.BackColor = secondary ? Surface : Blue;
        button.ForeColor = secondary ? SecondaryText : Color.White;
        button.Cursor = Cursors.Hand;
        button.UseVisualStyleBackColor = false;
    }

    private void PositionNear(Rectangle anchorBounds)
    {
        var area = Screen.FromRectangle(anchorBounds).WorkingArea;
        var margin = Math.Max(10, (int)Math.Round(10 * DeviceDpi / 96F));
        var x = anchorBounds.Right - Width;
        var y = anchorBounds.Top >= area.Bottom
            ? area.Bottom - Height - margin
            : area.Top + margin;
        Location = new Point(
            Math.Clamp(x, area.Left + margin, Math.Max(area.Left + margin,
                area.Right - Width - margin)),
            Math.Clamp(y, area.Top + margin, Math.Max(area.Top + margin,
                area.Bottom - Height - margin)));
    }

    private static void OpenIssue(string sourceUrl)
    {
        if (Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
        }
    }
}
