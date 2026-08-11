using System.Drawing.Drawing2D;
using System.Diagnostics;
using System.Runtime.InteropServices;
using TaskReminderTray.Models;
using TaskReminderTray.Services;
using UsageTray;

namespace TaskReminderTray;

/// <summary>
/// 可交互的排期详情窗口。后续按钮、链接等控件可以直接加入此窗体，
/// 不再受原生 ToolTip 无法接收稳定交互的限制。
/// </summary>
internal sealed class ScheduleDetailsForm : Form
{
    private const int DwmWindowCornerPreference = 33;
    private const int DwmWindowCornerRound = 2;
    private readonly DetailsSurface _surface = new();
    private bool _allowDeactivateClose = true;
    private Rectangle _anchorBounds;

    public event Action<IssueItem?>? FocusChanged;
    public event Action<IssueItem, DateTimeOffset>? SnoozeRequested;
    public event Action? NotificationCenterRequested;

    public ScheduleDetailsForm()
    {
        Text = "TaskReminderTray 详情";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        TopMost = true;
        KeyPreview = true;
        BackColor = Color.FromArgb(24, 27, 33);
        Controls.Add(_surface);
        _surface.Dock = DockStyle.Fill;
        _surface.LayoutChanged += ResizeForContent;
        _surface.FocusChanged += issue => FocusChanged?.Invoke(issue);
        _surface.SnoozeRequested += (issue, remindAt) =>
            SnoozeRequested?.Invoke(issue, remindAt);
        _surface.NotificationCenterRequested += () => NotificationCenterRequested?.Invoke();
        _surface.MenuOpening += () => _allowDeactivateClose = false;
        _surface.MenuClosed += () =>
        {
            _allowDeactivateClose = true;
            if (!ContainsFocus && !Bounds.Contains(Cursor.Position))
            {
                Hide();
            }
        };
        Deactivate += (_, _) =>
        {
            // 点击工具条本身时先不要因失焦关闭，让随后到达的工具条点击事件
            // 执行切换关闭；否则会出现“先关闭、又重新打开”的视觉反跳。
            if (_allowDeactivateClose && !_anchorBounds.Contains(Cursor.Position))
            {
                Hide();
            }
        };
        KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.KeyCode == Keys.Escape)
            {
                Hide();
            }
        };
    }

    public void SetContent(HoverCardContent content)
    {
        _surface.Content = content;
        ResizeForContent();
    }

    private void ResizeForContent()
    {
        if (_surface.Content is null)
        {
            return;
        }

        var size = UsageHoverCardRenderer.Measure(_surface.Content, DeviceDpi);
        ClientSize = size;
        PerformLayout();
        _surface.RefreshInteractionLayout();
        if (Visible && !_anchorBounds.IsEmpty)
        {
            PositionNear(_anchorBounds);
        }
        _surface.Invalidate();
    }

    public void Toggle(Rectangle anchorBounds)
    {
        _anchorBounds = anchorBounds;
        if (Visible)
        {
            Hide();
            return;
        }

        PositionNear(anchorBounds);
        _allowDeactivateClose = false;
        Show();
        Activate();
        BeginInvoke(() => _allowDeactivateClose = true);
    }

    public void Shutdown()
    {
        _allowDeactivateClose = false;
        Close();
    }

    private void PositionNear(Rectangle anchorBounds)
    {
        var workingArea = Screen.FromRectangle(anchorBounds).WorkingArea;
        var margin = Math.Max(6, (int)Math.Round(6 * DeviceDpi / 96F));
        var horizontalTaskbar = anchorBounds.Width >= anchorBounds.Height;
        int x;
        int y;
        if (horizontalTaskbar)
        {
            x = anchorBounds.Right - Width;
            y = anchorBounds.Top >= workingArea.Bottom
                ? workingArea.Bottom - Height - margin
                : workingArea.Top + margin;
        }
        else
        {
            x = anchorBounds.Left >= workingArea.Right
                ? workingArea.Right - Width - margin
                : workingArea.Left + margin;
            y = anchorBounds.Bottom - Height;
        }

        Location = new Point(
            Math.Clamp(x, workingArea.Left + margin,
                Math.Max(workingArea.Left + margin, workingArea.Right - Width - margin)),
            Math.Clamp(y, workingArea.Top + margin,
                Math.Max(workingArea.Top + margin, workingArea.Bottom - Height - margin)));
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        EnableSystemRoundedCorners();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        EnableSystemRoundedCorners();
    }

    private void EnableSystemRoundedCorners()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        // WinForms Region 是 1-bit 硬裁剪，高 DPI 下圆角边缘会出现黑色锯齿。
        // Windows 11 由 DWM 合成抗锯齿圆角；旧系统失败时保持矩形，
        // 也比带黑边的硬裁剪更稳定。
        Region?.Dispose();
        Region = null;
        var preference = DwmWindowCornerRound;
        _ = DwmSetWindowAttribute(Handle, DwmWindowCornerPreference,
            ref preference, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr window, int attribute, ref int value, int valueSize);

    private sealed class DetailsSurface : Control
    {
        private readonly System.Windows.Forms.Timer _animationTimer = new() { Interval = 50 };
        private readonly ScheduleInteractionMap _interactions = new();
        private readonly ToolTip _toolTip = new()
        {
            InitialDelay = 250,
            ReshowDelay = 100,
            AutoPopDelay = 1800,
            ShowAlways = true
        };
        private readonly ContextMenuStrip _issueMenu = new();
        private readonly List<IssueActionButton> _actionButtons = [];
        private HoverCardContent? _content;
        private IssueItem? _menuIssue;

        public event Action? LayoutChanged;
        public event Action<IssueItem?>? FocusChanged;
        public event Action<IssueItem, DateTimeOffset>? SnoozeRequested;
        public event Action? NotificationCenterRequested;
        public event Action? MenuOpening;
        public event Action? MenuClosed;

        public DetailsSurface()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
            _animationTimer.Tick += (_, _) =>
            {
                if (Visible && _content?.Summary is not null)
                {
                    Invalidate();
                }
            };
            BuildIssueMenu();
            MouseMove += DetailsSurface_MouseMove;
            MouseLeave += (_, _) =>
            {
                SetHoveredWeekNavigation(null);
                SetNotificationCenterHovered(false);
            };
            MouseUp += DetailsSurface_MouseUp;
        }

        private void BuildIssueMenu()
        {
            _issueMenu.Items.Add("打开工单", null, (_, _) => OpenIssue(_menuIssue));
            _issueMenu.Items.Add("复制单信息", null, (_, _) =>
                CopyText(_menuIssue is null ? null :
                    PersonalWorkStore.FormatIssueInformation(_menuIssue)));
            _issueMenu.Items.Add("复制工单编号", null, (_, _) => CopyText(_menuIssue?.Key));
            _issueMenu.Items.Add("复制工单标题", null, (_, _) => CopyText(_menuIssue?.Title));
            _issueMenu.Items.Add(new ToolStripSeparator());
            _issueMenu.Items.Add("设为当前重点", null, (_, _) => ToggleFocus());
            var snooze = new ToolStripMenuItem("稍后提醒");
            snooze.DropDownItems.Add("30 分钟后", null, (_, _) => Snooze(TimeSpan.FromMinutes(30)));
            snooze.DropDownItems.Add("1 小时后", null, (_, _) => Snooze(TimeSpan.FromHours(1)));
            snooze.DropDownItems.Add("今天下班前（17:30）", null, (_, _) =>
                SnoozeAt(TodayAtOrTomorrow(17, 30)));
            snooze.DropDownItems.Add("明天上午（09:00）", null, (_, _) =>
                SnoozeAt(DateTime.Today.AddDays(1).AddHours(9)));
            _issueMenu.Items.Add(snooze);
            _issueMenu.Opening += (_, _) =>
            {
                MenuOpening?.Invoke();
                var focusItem = _issueMenu.Items[5];
                focusItem.Text = _menuIssue is not null &&
                                 string.Equals(_menuIssue.Id, _content?.FocusIssueId,
                                     StringComparison.OrdinalIgnoreCase)
                    ? "取消当前重点"
                    : "设为当前重点";
            };
            _issueMenu.Closed += (_, _) => MenuClosed?.Invoke();
        }

        private void DetailsSurface_MouseMove(object? sender, MouseEventArgs e)
        {
            var weekNavigation = _interactions.WeekNavigation.LastOrDefault(item =>
                item.Bounds.Contains(e.Location));
            SetHoveredWeekNavigation(weekNavigation?.Navigation);
            SetNotificationCenterHovered(
                _interactions.NotificationCenterBounds.Contains(e.Location));
            Cursor = _interactions.Expanders.Any(expander => expander.Bounds.Contains(e.Location)) ||
                     weekNavigation is not null ||
                     _interactions.NotificationCenterBounds.Contains(e.Location)
                ? Cursors.Hand
                : Cursors.Default;
        }

        private void SetNotificationCenterHovered(bool hovered)
        {
            if (_content is null || _content.NotificationCenterHovered == hovered)
            {
                return;
            }

            _content.NotificationCenterHovered = hovered;
            Invalidate(_interactions.NotificationCenterBounds);
        }

        private void SetHoveredWeekNavigation(ScheduleWeekNavigation? navigation)
        {
            if (_content is null || _content.HoveredWeekNavigation == navigation)
            {
                return;
            }

            _content.HoveredWeekNavigation = navigation;
            Invalidate();
        }

        private void DetailsSurface_MouseUp(object? sender, MouseEventArgs e)
        {
            var issueRegion = _interactions.Issues.LastOrDefault(item =>
                item.Bounds.Contains(e.Location) || item.StatusBounds.Contains(e.Location));
            if (e.Button == MouseButtons.Right && issueRegion is not null)
            {
                _menuIssue = issueRegion.Issue;
                _issueMenu.Show(this, e.Location);
                return;
            }

            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            if (_interactions.NotificationCenterBounds.Contains(e.Location))
            {
                NotificationCenterRequested?.Invoke();
                return;
            }

            var weekNavigation = _interactions.WeekNavigation.LastOrDefault(item =>
                item.Bounds.Contains(e.Location));
            if (weekNavigation is not null && _content is not null)
            {
                _content.WeekOffset = weekNavigation.Navigation switch
                {
                    ScheduleWeekNavigation.Previous => _content.WeekOffset - 1,
                    ScheduleWeekNavigation.Current => 0,
                    ScheduleWeekNavigation.Next => _content.WeekOffset + 1,
                    _ => _content.WeekOffset
                };
                LayoutChanged?.Invoke();
                return;
            }

            var expander = _interactions.Expanders.LastOrDefault(item =>
                item.Bounds.Contains(e.Location));
            if (expander is not null && _content is not null)
            {
                if (expander.IsExpanded)
                {
                    _content.ExpandedDates.Remove(expander.Date);
                }
                else
                {
                    _content.ExpandedDates.Add(expander.Date);
                }
                LayoutChanged?.Invoke();
                return;
            }

            if (issueRegion is not null)
            {
                // 标题和状态区域仅用于展示与右键菜单，不执行页面跳转。
                return;
            }
        }

        private void ToggleFocus()
        {
            if (_menuIssue is null || _content is null)
            {
                return;
            }

            FocusChanged?.Invoke(string.Equals(_menuIssue.Id, _content.FocusIssueId,
                StringComparison.OrdinalIgnoreCase) ? null : _menuIssue);
        }

        private void Snooze(TimeSpan delay) => SnoozeAt(DateTimeOffset.Now.Add(delay));

        private void SnoozeAt(DateTimeOffset remindAt)
        {
            if (_menuIssue is not null)
            {
                SnoozeRequested?.Invoke(_menuIssue, remindAt);
            }
        }

        private static DateTimeOffset TodayAtOrTomorrow(int hour, int minute)
        {
            var candidate = DateTime.Today.AddHours(hour).AddMinutes(minute);
            return candidate > DateTime.Now ? candidate : candidate.AddDays(1);
        }

        private static void OpenIssue(IssueItem? issue)
        {
            if (issue is not null && Uri.TryCreate(issue.SourceUrl,
                    UriKind.Absolute, out var uri))
            {
                Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
            }
        }

        private static void CopyText(string? text)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                Clipboard.SetText(text);
            }
        }

        public HoverCardContent? Content
        {
            get => _content;
            set
            {
                _content = value;
                if (value?.Summary is null)
                {
                    _animationTimer.Stop();
                }
                else
                {
                    _animationTimer.Start();
                }
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_content is not null)
            {
                UsageHoverCardRenderer.Draw(e.Graphics, ClientRectangle, DeviceDpi, _content,
                    _interactions);
            }
        }

        public void RefreshInteractionLayout()
        {
            if (_content is null || ClientSize.Width <= 0 || ClientSize.Height <= 0)
            {
                return;
            }

            using var bitmap = new Bitmap(ClientSize.Width, ClientSize.Height);
            using var graphics = Graphics.FromImage(bitmap);
            UsageHoverCardRenderer.Draw(graphics, new Rectangle(Point.Empty, ClientSize),
                DeviceDpi, _content, _interactions);
            SynchronizeActionButtons();
        }

        private void SynchronizeActionButtons()
        {
            var required = _interactions.Issues.Count * 2;
            while (_actionButtons.Count < required)
            {
                var button = new IssueActionButton();
                button.Click += ActionButton_Click;
                _actionButtons.Add(button);
                Controls.Add(button);
                button.BringToFront();
            }

            for (var index = 0; index < _actionButtons.Count; index++)
            {
                if (index >= required)
                {
                    _actionButtons[index].Visible = false;
                    continue;
                }

                var region = _interactions.Issues[index / 2];
                var open = (index & 1) == 0;
                var button = _actionButtons[index];
                button.Issue = region.Issue;
                button.Action = open ? IssueAction.Open : IssueAction.Copy;
                button.Bounds = open ? region.OpenBounds : region.CopyBounds;
                button.AccessibleName = open ? "打开工单" : "复制单信息";
                button.TabStop = true;
                button.Visible = true;
                _toolTip.SetToolTip(button, button.AccessibleName);
                button.BringToFront();
            }
        }

        private void ActionButton_Click(object? sender, EventArgs e)
        {
            if (sender is not IssueActionButton { Issue: { } issue } button)
            {
                return;
            }

            if (button.Action == IssueAction.Open)
            {
                OpenIssue(issue);
                return;
            }

            CopyText(PersonalWorkStore.FormatIssueInformation(issue));
            button.ShowCopiedFeedback();
            _toolTip.Show("已复制单信息", button, button.Width / 2, -28, 1300);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _animationTimer.Stop();
                _animationTimer.Dispose();
                _toolTip.Dispose();
                _issueMenu.Dispose();
                foreach (var button in _actionButtons)
                {
                    button.Dispose();
                }
                _actionButtons.Clear();
            }
            base.Dispose(disposing);
        }

        private enum IssueAction
        {
            Open,
            Copy
        }

        private sealed class IssueActionButton : Button
        {
            private readonly System.Windows.Forms.Timer _feedbackTimer = new()
            {
                Interval = 1000
            };
            private bool _copied;

            public IssueItem? Issue { get; set; }
            public IssueAction Action { get; set; }

            public IssueActionButton()
            {
                FlatStyle = FlatStyle.Flat;
                FlatAppearance.BorderSize = 0;
                BackColor = Color.FromArgb(31, 35, 43);
                ForeColor = Color.FromArgb(201, 208, 220);
                Cursor = Cursors.Hand;
                UseVisualStyleBackColor = false;
                SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
                _feedbackTimer.Tick += (_, _) =>
                {
                    _feedbackTimer.Stop();
                    _copied = false;
                    Invalidate();
                };
                SizeChanged += (_, _) => UpdateRoundedRegion();
            }

            public void ShowCopiedFeedback()
            {
                _copied = true;
                _feedbackTimer.Stop();
                _feedbackTimer.Start();
                Invalidate();
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                BackColor = Color.FromArgb(42, 48, 58);
                ForeColor = Color.White;
                Invalidate();
                base.OnMouseEnter(e);
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                BackColor = Color.FromArgb(31, 35, 43);
                ForeColor = Color.FromArgb(201, 208, 220);
                Invalidate();
                base.OnMouseLeave(e);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var path = RoundedButton(ClientRectangle, 5);
                using var brush = new SolidBrush(BackColor);
                using var borderPen = new Pen(Focused || ClientRectangle.Contains(PointToClient(Cursor.Position))
                    ? Color.FromArgb(91, 141, 239)
                    : Color.FromArgb(58, 65, 78), Focused ? 2F : 1F);
                using var pen = new Pen(_copied ? Color.FromArgb(57, 199, 127) : ForeColor, 1.4F)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                };
                e.Graphics.FillPath(brush, path);
                e.Graphics.DrawPath(borderPen, path);
                if (_copied)
                {
                    var centerX = Width / 2;
                    var centerY = Height / 2;
                    e.Graphics.DrawLine(pen, centerX - 5, centerY,
                        centerX - 1, centerY + 4);
                    e.Graphics.DrawLine(pen, centerX - 1, centerY + 4,
                        centerX + 6, centerY - 5);
                }
                else if (Action == IssueAction.Copy)
                {
                    var left = (Width - 8) / 2;
                    var top = (Height - 8) / 2;
                    e.Graphics.DrawRectangle(pen, left + 3, top, 8, 8);
                    e.Graphics.DrawRectangle(pen, left, top + 3, 8, 8);
                }
                else
                {
                    var box = new Rectangle((Width - 12) / 2, (Height - 8) / 2 + 2, 9, 8);
                    e.Graphics.DrawRectangle(pen, box);
                    e.Graphics.DrawLine(pen, box.Left + 5, box.Top - 3,
                        box.Right + 3, box.Top - 3);
                    e.Graphics.DrawLine(pen, box.Right + 3, box.Top - 3,
                        box.Right + 3, box.Top + 4);
                    e.Graphics.DrawLine(pen, box.Left + 5, box.Top + 4,
                        box.Right + 3, box.Top - 3);
                }
            }

            private void UpdateRoundedRegion()
            {
                if (Width <= 0 || Height <= 0)
                {
                    return;
                }

                using var path = RoundedButton(ClientRectangle, 6);
                Region?.Dispose();
                Region = new Region(path);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _feedbackTimer.Stop();
                    _feedbackTimer.Dispose();
                }

                base.Dispose(disposing);
            }

            private static GraphicsPath RoundedButton(Rectangle bounds, int radius)
            {
                var path = new GraphicsPath();
                var diameter = radius * 2;
                var safe = Rectangle.Inflate(bounds, -1, -1);
                path.AddArc(safe.Left, safe.Top, diameter, diameter, 180, 90);
                path.AddArc(safe.Right - diameter, safe.Top, diameter, diameter, 270, 90);
                path.AddArc(safe.Right - diameter, safe.Bottom - diameter,
                    diameter, diameter, 0, 90);
                path.AddArc(safe.Left, safe.Bottom - diameter,
                    diameter, diameter, 90, 90);
                path.CloseFigure();
                return path;
            }
        }
    }
}
