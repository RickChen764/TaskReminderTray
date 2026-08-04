using System.Drawing.Drawing2D;
using UsageTray;

namespace TaskReminderTray;

/// <summary>
/// 可交互的排期详情窗口。后续按钮、链接等控件可以直接加入此窗体，
/// 不再受原生 ToolTip 无法接收稳定交互的限制。
/// </summary>
internal sealed class ScheduleDetailsForm : Form
{
    private readonly DetailsSurface _surface = new();
    private bool _allowDeactivateClose = true;
    private Rectangle _anchorBounds;

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
        var size = UsageHoverCardRenderer.Measure(content, DeviceDpi);
        ClientSize = size;
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
        UpdateRoundedRegion();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateRoundedRegion();
    }

    private void UpdateRoundedRegion()
    {
        if (Width <= 0 || Height <= 0)
        {
            return;
        }

        using var path = new GraphicsPath();
        var radius = Math.Max(6, (int)Math.Round(8 * DeviceDpi / 96F));
        var diameter = radius * 2;
        var bounds = new Rectangle(0, 0, Width, Height);
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        Region?.Dispose();
        Region = new Region(path);
    }

    private sealed class DetailsSurface : Control
    {
        private readonly System.Windows.Forms.Timer _animationTimer = new() { Interval = 50 };
        private HoverCardContent? _content;

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
                UsageHoverCardRenderer.Draw(e.Graphics, ClientRectangle, DeviceDpi, _content);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _animationTimer.Stop();
                _animationTimer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
