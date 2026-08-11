using System.Diagnostics;
using TaskReminderTray.Models;
using TaskReminderTray.Services;

namespace TaskReminderTray;

internal sealed class DailySummaryForm : Form
{
    private static readonly Color Background = Color.FromArgb(24, 27, 33);
    private static readonly Color Surface = Color.FromArgb(31, 35, 43);
    private static readonly Color SurfaceAlt = Color.FromArgb(35, 41, 51);
    private static readonly Color Border = Color.FromArgb(54, 60, 71);
    private static readonly Color PrimaryText = Color.FromArgb(244, 246, 250);
    private static readonly Color SecondaryText = Color.FromArgb(184, 194, 210);
    private static readonly Color MutedText = Color.FromArgb(126, 139, 158);
    private static readonly Color Blue = Color.FromArgb(88, 142, 238);
    private static readonly Color Green = Color.FromArgb(57, 199, 127);
    private static readonly Color Orange = Color.FromArgb(244, 171, 68);
    private static readonly Color Red = Color.FromArgb(235, 83, 96);

    private readonly Label _dateLabel = new();
    private readonly TableLayoutPanel _metrics = new();
    private readonly FlowLayoutPanel _content = new();
    private DailyWorkSummary? _summary;
    private bool _shuttingDown;

    internal static Size LogicalSizeForDpi(int dpi) => new(
        (int)Math.Round(680 * dpi / 96F),
        (int)Math.Round(660 * dpi / 96F));

    public DailySummaryForm()
    {
        Text = "今日工作摘要";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(680, 660);
        MinimumSize = new Size(600, 500);
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

    public void ShowSummary(DailyWorkSummary summary, Rectangle anchorBounds)
    {
        SetSummary(summary);
        PositionNear(anchorBounds);
        if (!Visible)
        {
            Show();
        }
        Activate();
        BringToFront();
    }

    internal void SetSummary(DailyWorkSummary summary)
    {
        _summary = summary;
        var weekday = new[] { "周日", "周一", "周二", "周三", "周四", "周五", "周六" }
            [(int)summary.Date.DayOfWeek];
        _dateLabel.Text = $"{summary.Date:yyyy年M月d日}  {weekday}";
        SetMetric(0, summary.TodayIssues.Count, "今天安排",
            summary.TodayIssues.Count > 0 ? Blue : MutedText);
        SetMetric(1, summary.DueTodayIssues.Count, "今天到期",
            summary.DueTodayIssues.Count > 0 ? Orange : MutedText);
        SetMetric(2, summary.OverdueIssues.Count, "已逾期",
            summary.OverdueIssues.Count > 0 ? Red : MutedText);
        SetMetric(3, summary.RecentStatusChanges.Count, "状态变化",
            summary.RecentStatusChanges.Count > 0 ? Green : MutedText);
        RebuildContent();
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
        var title = new Label
        {
            Text = "今日工作摘要",
            ForeColor = PrimaryText,
            Font = new Font(Font.FontFamily, 16F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(24, 17)
        };
        _dateLabel.ForeColor = MutedText;
        _dateLabel.AutoSize = true;
        _dateLabel.Location = new Point(26, 54);

        var separator = new Panel
        {
            BackColor = Border,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Location = new Point(24, 82),
            Size = new Size(ClientSize.Width - 48, 1)
        };

        _metrics.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _metrics.BackColor = Surface;
        _metrics.ColumnCount = 4;
        _metrics.RowCount = 1;
        _metrics.CellBorderStyle = TableLayoutPanelCellBorderStyle.Single;
        _metrics.SetBounds(24, 98, ClientSize.Width - 48, 66);
        for (var index = 0; index < 4; index++)
        {
            _metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
            var cell = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            var count = new Label
            {
                Name = "Count",
                Font = new Font(Font.FontFamily, 15F, FontStyle.Bold),
                AutoSize = false,
                TextAlign = ContentAlignment.BottomCenter,
                Dock = DockStyle.Top,
                Height = 36
            };
            var label = new Label
            {
                Name = "Label",
                ForeColor = MutedText,
                AutoSize = false,
                TextAlign = ContentAlignment.TopCenter,
                Dock = DockStyle.Fill
            };
            cell.Controls.Add(label);
            cell.Controls.Add(count);
            _metrics.Controls.Add(cell, index, 0);
        }

        _content.Anchor = AnchorStyles.Top | AnchorStyles.Bottom |
                          AnchorStyles.Left | AnchorStyles.Right;
        _content.AutoScroll = true;
        _content.FlowDirection = FlowDirection.TopDown;
        _content.WrapContents = false;
        _content.BackColor = Background;
        _content.Padding = new Padding(0, 0, 8, 0);
        _content.SetBounds(24, 180, ClientSize.Width - 48, ClientSize.Height - 204);
        _content.SizeChanged += (_, _) => ResizeSections();

        Controls.AddRange([title, _dateLabel, separator, _metrics, _content]);
    }

    private void SetMetric(int index, int count, string label, Color color)
    {
        var cell = _metrics.GetControlFromPosition(index, 0);
        if (cell?.Controls["Count"] is Label countLabel)
        {
            countLabel.Text = count.ToString();
            countLabel.ForeColor = color;
        }
        if (cell?.Controls["Label"] is Label textLabel)
        {
            textLabel.Text = label;
        }
    }

    private void RebuildContent()
    {
        if (_summary is null)
        {
            return;
        }
        _content.SuspendLayout();
        try
        {
            foreach (var control in _content.Controls.Cast<Control>().ToArray())
            {
                control.Dispose();
            }
            _content.Controls.Clear();

            AddFocusSection(_summary.FocusIssue);
            AddIssueSection("今天要做", _summary.TodayIssues, "今天暂无排期任务", Blue, 4);
            var risks = _summary.OverdueIssues.Concat(_summary.DueTodayIssues)
                .DistinctBy(issue => issue.Id, StringComparer.OrdinalIgnoreCase).ToArray();
            AddIssueSection("需要留意", risks, "今天没有到期或逾期工单", Orange, 3);
            AddIssueSection("明天预览", _summary.TomorrowIssues, "明天暂无排期任务",
                Green, 3);
            AddChangesSection(_summary.RecentStatusChanges);
            AddUnscheduledHint(_summary.UnscheduledIssues.Count);
            ResizeSections();
        }
        finally
        {
            _content.ResumeLayout();
        }
    }

    private void AddFocusSection(IssueItem? issue)
    {
        var section = CreateSection("当前重点", Green, issue is null ? 76 : 104);
        if (issue is null)
        {
            AddEmpty(section, "暂无当前重点");
        }
        else
        {
            AddIssueRow(section, issue, "优先级 " + issue.Priority + " · " +
                                       ShortStatus(issue.Status), emphasize: true);
        }
        _content.Controls.Add(section);
    }

    private void AddIssueSection(string title, IReadOnlyList<IssueItem> issues,
        string emptyText, Color accent, int maximum)
    {
        var shown = issues.Take(maximum).ToArray();
        var section = CreateSection(title, accent, shown.Length == 0 ? 76 : 48 + shown.Length * 52);
        if (shown.Length == 0)
        {
            AddEmpty(section, emptyText);
        }
        else
        {
            foreach (var issue in shown)
            {
                var metadata = issue.DueDate is { } due
                    ? $"{ShortStatus(issue.Status)} · 截止 {due:M/d}"
                    : ShortStatus(issue.Status);
                AddIssueRow(section, issue, metadata, emphasize: false);
            }
            if (issues.Count > shown.Length)
            {
                AddMore(section, $"另有 {issues.Count - shown.Length} 项，可在开发安排中查看");
                section.Height += 26;
            }
        }
        _content.Controls.Add(section);
    }

    private void AddChangesSection(IReadOnlyList<PersistentNotification> changes)
    {
        var shown = changes.Take(3).ToArray();
        var section = CreateSection("昨天以来的变化", Blue,
            shown.Length == 0 ? 76 : 48 + shown.Length * 48);
        if (shown.Length == 0)
        {
            AddEmpty(section, "暂无工单状态变化");
        }
        else
        {
            foreach (var change in shown)
            {
                var row = new Panel { Height = 46, Dock = DockStyle.Top };
                var title = TextLabel($"{change.IssueKey}  {change.Title}", PrimaryText,
                    FontStyle.Bold);
                title.SetBounds(14, 3, 410, 21);
                title.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
                var status = TextLabel($"{change.PreviousStatus} → {change.CurrentStatus}",
                    MutedText);
                status.SetBounds(14, 24, 380, 20);
                var time = TextLabel(change.ChangedAt.ToLocalTime().ToString("MM-dd HH:mm"),
                    MutedText);
                time.TextAlign = ContentAlignment.MiddleRight;
                time.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                time.SetBounds(row.Width - 110, 24, 96, 20);
                row.Controls.AddRange([title, status, time]);
                section.Controls.Add(row);
                row.BringToFront();
            }
        }
        _content.Controls.Add(section);
    }

    private void AddUnscheduledHint(int count)
    {
        if (count <= 0)
        {
            return;
        }
        var hint = new Label
        {
            Text = $"还有 {count} 项开发任务尚未安排日期",
            ForeColor = MutedText,
            BackColor = Surface,
            Height = 36,
            TextAlign = ContentAlignment.MiddleCenter,
            Margin = new Padding(0, 0, 0, 10)
        };
        _content.Controls.Add(hint);
    }

    private Panel CreateSection(string title, Color accent, int height)
    {
        var section = new Panel
        {
            Height = height,
            BackColor = Surface,
            Margin = new Padding(0, 0, 0, 12),
            Padding = new Padding(1)
        };
        section.Paint += (_, e) =>
        {
            using var border = new Pen(Border);
            e.Graphics.DrawRectangle(border, 0, 0, section.Width - 1, section.Height - 1);
            using var brush = new SolidBrush(accent);
            e.Graphics.FillRectangle(brush, 0, 0, 3, section.Height);
        };
        var heading = TextLabel(title, accent, FontStyle.Bold);
        heading.Dock = DockStyle.Top;
        heading.Height = 36;
        heading.Padding = new Padding(14, 8, 0, 0);
        section.Controls.Add(heading);
        return section;
    }

    private static void AddIssueRow(Panel section, IssueItem issue, string metadata,
        bool emphasize)
    {
        var row = new Panel { Height = emphasize ? 62 : 52, Dock = DockStyle.Top };
        row.Paint += (_, e) =>
        {
            using var titleFont = new Font("Microsoft YaHei UI", 9F,
                emphasize ? FontStyle.Bold : FontStyle.Regular);
            using var metaFont = new Font("Microsoft YaHei UI", 8.5F);
            TextRenderer.DrawText(e.Graphics, $"{issue.Key}  {issue.DisplayTitle}",
                titleFont, new Rectangle(14, 1, Math.Max(80, row.Width - 104), 25),
                emphasize ? PrimaryText : SecondaryText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPadding);
            TextRenderer.DrawText(e.Graphics, metadata, metaFont,
                new Rectangle(14, 26, Math.Max(80, row.Width - 104), 22),
                MutedText, TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                           TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
                           TextFormatFlags.NoPadding);
        };
        var open = new Button { Text = "打开" };
        StyleButton(open);
        open.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        open.SetBounds(row.Width - 76, 8, 62, 32);
        open.Enabled = Uri.TryCreate(issue.SourceUrl, UriKind.Absolute, out _);
        open.Click += (_, _) => OpenIssue(issue.SourceUrl);
        row.Controls.Add(open);
        section.Controls.Add(row);
        row.BringToFront();
    }

    private static void AddEmpty(Panel section, string text)
    {
        var label = TextLabel(text, MutedText);
        label.Dock = DockStyle.Fill;
        label.Padding = new Padding(14, 0, 0, 0);
        section.Controls.Add(label);
        label.BringToFront();
    }

    private static void AddMore(Panel section, string text)
    {
        var label = TextLabel(text, MutedText);
        label.Dock = DockStyle.Bottom;
        label.Height = 26;
        label.Padding = new Padding(14, 0, 0, 0);
        section.Controls.Add(label);
    }

    private static Label TextLabel(string text, Color color,
        FontStyle style = FontStyle.Regular) => new()
    {
        Text = text,
        ForeColor = color,
        BackColor = Color.Transparent,
        Font = new Font("Microsoft YaHei UI", 9F, style),
        AutoSize = false,
        AutoEllipsis = true,
        TextAlign = ContentAlignment.MiddleLeft
    };

    private static void StyleButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(46, 52, 63);
        button.BackColor = SurfaceAlt;
        button.ForeColor = SecondaryText;
        button.Cursor = Cursors.Hand;
        button.UseVisualStyleBackColor = false;
        button.EnabledChanged += (_, _) => button.ForeColor = button.Enabled
            ? SecondaryText
            : MutedText;
    }

    private void ResizeSections()
    {
        var width = Math.Max(360, _content.ClientSize.Width - _content.Padding.Horizontal -
                                   (_content.VerticalScroll.Visible
                                       ? SystemInformation.VerticalScrollBarWidth : 0));
        foreach (Control control in _content.Controls)
        {
            control.Width = width;
            control.PerformLayout();
        }
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

    private static string ShortStatus(string status) =>
        UsageTray.UsageHoverCardRenderer.ShortStatus(status);

    private static void OpenIssue(string sourceUrl)
    {
        if (Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
        }
    }
}
