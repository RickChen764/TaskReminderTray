using System.Diagnostics;
using TaskReminderTray.Services;

namespace TaskReminderTray;

internal sealed class SnoozedReminderForm : Form
{
    private readonly Label _keyLabel = new();
    private readonly Label _titleLabel = new();
    private readonly Label _pendingLabel = new();
    private SnoozedWorkReminder? _reminder;
    private bool _shuttingDown;

    public event Action<string>? AcknowledgeRequested;
    public event Action<string, DateTimeOffset>? RescheduleRequested;

    public SnoozedReminderForm()
    {
        Text = "稍后提醒";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        ControlBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(400, 180);
        BackColor = Color.FromArgb(28, 31, 38);
        Font = new Font("Microsoft YaHei UI", 9F);
        BuildInterface();
    }

    public void ShowReminder(SnoozedWorkReminder reminder, int pendingCount)
    {
        _reminder = reminder;
        _keyLabel.Text = reminder.IssueKey;
        _titleLabel.Text = reminder.Title;
        _pendingLabel.Text = pendingCount > 1 ? $"还有 {pendingCount - 1} 条" : string.Empty;
        var area = Screen.PrimaryScreen?.WorkingArea ?? Screen.FromPoint(Cursor.Position).WorkingArea;
        // 状态变化通知占用右下角；个人稍后提醒固定显示在其上方。
        Location = new Point(area.Right - Width - 16, area.Bottom - Height - 256);
        if (!Visible)
        {
            Show();
        }
        Activate();
    }

    public void HideReminder()
    {
        _reminder = null;
        Hide();
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
        }
        base.OnFormClosing(e);
    }

    private void BuildInterface()
    {
        var heading = new Label
        {
            Text = "你设置的稍后提醒已到时间",
            ForeColor = Color.FromArgb(239, 242, 247),
            Font = new Font(Font.FontFamily, 10F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(18, 16)
        };
        _pendingLabel.ForeColor = Color.FromArgb(151, 159, 173);
        _pendingLabel.AutoSize = false;
        _pendingLabel.TextAlign = ContentAlignment.MiddleRight;
        _pendingLabel.SetBounds(280, 14, 100, 25);
        _keyLabel.ForeColor = Color.FromArgb(80, 145, 232);
        _keyLabel.Font = new Font(Font.FontFamily, 9F, FontStyle.Bold);
        _keyLabel.Cursor = Cursors.Hand;
        _keyLabel.AutoSize = false;
        _keyLabel.SetBounds(18, 59, 90, 25);
        _keyLabel.Click += (_, _) => OpenIssue();
        _titleLabel.ForeColor = Color.FromArgb(239, 242, 247);
        _titleLabel.AutoEllipsis = true;
        _titleLabel.Cursor = Cursors.Hand;
        _titleLabel.SetBounds(106, 59, 274, 25);
        _titleLabel.Click += (_, _) => OpenIssue();

        var laterButton = new Button
        {
            Text = "30 分钟后",
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.FromArgb(199, 216, 239),
            BackColor = Color.FromArgb(47, 54, 66),
            Size = new Size(112, 34),
            Location = new Point(148, 124)
        };
        laterButton.FlatAppearance.BorderSize = 0;
        laterButton.Click += (_, _) =>
        {
            if (_reminder is { } reminder)
            {
                RescheduleRequested?.Invoke(reminder.Id, DateTimeOffset.Now.AddMinutes(30));
            }
        };
        var acknowledgeButton = new Button
        {
            Text = "已知晓",
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(80, 145, 232),
            Size = new Size(112, 34),
            Location = new Point(268, 124)
        };
        acknowledgeButton.FlatAppearance.BorderSize = 0;
        acknowledgeButton.Click += (_, _) =>
        {
            if (_reminder is { } reminder)
            {
                AcknowledgeRequested?.Invoke(reminder.Id);
            }
        };
        Controls.AddRange([heading, _pendingLabel, _keyLabel, _titleLabel,
            laterButton, acknowledgeButton]);
    }

    private void OpenIssue()
    {
        if (_reminder is { SourceUrl: { Length: > 0 } sourceUrl } &&
            Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
        }
    }
}
