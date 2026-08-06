using System.Drawing.Drawing2D;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using UsageTray.Services;

namespace UsageTray;

internal sealed class TaskbarToolbarForm : Form
{
    private const int MinimumToolbarWidth = 150;
    private const int MaximumToolbarWidth = 440;
    private const float HorizontalFontSize = 11F;
    private const int VerticalToolbarHeight = 72;
    private const long WsChild = 0x40000000L;
    private const long WsPopup = 0x80000000L;
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int SwpNoActivate = 0x0010;
    private const int SwpShowWindow = 0x0040;
    private const int SwpNoSize = 0x0001;
    private const int SwpNoZOrder = 0x0004;
    private const int SwpNoMove = 0x0002;
    private const int SwpHideWindow = 0x0080;
    private const int WmWindowPosChanging = 0x0046;
    private const int WmEraseBackground = 0x0014;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private static readonly IntPtr HwndTop = IntPtr.Zero;

    private readonly System.Windows.Forms.Timer _attachmentTimer = new() { Interval = 1500 };
    private readonly System.Windows.Forms.Timer _toolTipAnimationTimer = new() { Interval = 50 };
    private readonly ToolTip _toolTip = new();
    private readonly ToolTipWindowSubclass _toolTipWindowSubclass;
    private readonly string? _avoidProcessName;
    private IntPtr _taskbarHandle;
    private Rectangle _lastTaskbarBounds;
    private Rectangle _lastTrayBounds;
    private Rectangle _lastAvoidedBounds;
    private string _displayText = "等待配置";
    private HoverCardContent _hoverContent = HoverCardContent.CreateStatus(
        "等待配置", "请先完成 API 地址与密钥设置。",
        Color.FromArgb(124, 132, 145));
    private Color _statusColor = Color.FromArgb(124, 132, 145);
    private int _desiredToolbarWidth = MinimumToolbarWidth;
    private int _pendingToolTipWidth;
    private int _pendingToolTipHeight;
    private bool _toolTipPositionArmed;
    private bool _hovered;
    private bool _pressed;
    private bool _hoverEnabled;
    private Bitmap? _toolTipFrame;

    public event EventHandler? DetailsRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler<bool>? AttachmentChanged;

    public bool IsAttached { get; private set; }

    public TaskbarToolbarForm(ContextMenuStrip contextMenu, string? avoidProcessName = null)
    {
        _avoidProcessName = avoidProcessName;
        _toolTipWindowSubclass = new ToolTipWindowSubclass(this);
        FormBorderStyle = FormBorderStyle.None;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(52, 57, 65);
        ContextMenuStrip = contextMenu;
        DoubleBuffered = true;
        Cursor = Cursors.Hand;

        _toolTip.AutoPopDelay = 20000;
        _toolTip.InitialDelay = 300;
        _toolTip.ReshowDelay = 100;
        _toolTip.ShowAlways = true;
        _toolTip.OwnerDraw = true;
        _toolTip.Popup += ToolTip_Popup;
        _toolTip.Draw += ToolTip_Draw;
        _toolTip.UseAnimation = false;
        _toolTip.UseFading = false;
        _toolTip.SetToolTip(this, null);

        _attachmentTimer.Tick += (_, _) => AttachOrReposition();
        _toolTipAnimationTimer.Tick += (_, _) => RedrawVisibleToolTip();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExToolWindow | WsExNoActivate;
            return parameters;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        AttachOrReposition(force: true);
        // 提前接管原生 ToolTip 窗口，确保第一次 Hover 的首个
        // WM_WINDOWPOSCHANGING 就能被修正，不需要等 Popup/Draw 后再移动。
        EnsureToolTipSubclass();
        _attachmentTimer.Start();
    }

    public void SetDisplay(
        string text,
        HoverCardContent hoverContent,
        Color statusColor)
    {
        _displayText = text;
        _hoverContent = hoverContent;
        _statusColor = statusColor;
        _toolTip.SetToolTip(this, _hoverEnabled ? hoverContent.ToPlainText() : null);
        var widthChanged = UpdateDesiredToolbarWidth();
        if (!IsAttached || widthChanged)
        {
            AttachOrReposition(force: true);
        }
        Invalidate();
    }

    public void SetHoverEnabled(bool enabled)
    {
        _hoverEnabled = enabled;
        if (enabled)
        {
            _toolTip.SetToolTip(this, _hoverContent.ToPlainText());
        }
        else
        {
            _toolTip.Hide(this);
            _toolTip.SetToolTip(this, null);
            _toolTipPositionArmed = false;
        }
    }

    public Rectangle GetScreenBounds()
    {
        return IsHandleCreated && GetWindowRect(Handle, out var bounds)
            ? bounds.ToRectangle()
            : RectangleToScreen(ClientRectangle);
    }

    private void ToolTip_Popup(object? sender, PopupEventArgs e)
    {
        var dpi = GetCurrentDpi();
        var desired = UsageHoverCardRenderer.Measure(_hoverContent, dpi);
        var margin = Math.Max(8, (int)Math.Round(10 * dpi / 96F));
        var workingArea = GetMonitorWorkArea(Handle);
        _pendingToolTipWidth = Math.Min(
            desired.Width, Math.Max(240, workingArea.Width - margin * 2));
        _pendingToolTipHeight = Math.Min(
            desired.Height, Math.Max(120, workingArea.Height - margin * 2));
        e.ToolTipSize = new Size(_pendingToolTipWidth, _pendingToolTipHeight);
        _toolTipPositionArmed = true;
        EnsureToolTipSubclass();
        _toolTipAnimationTimer.Start();
    }

    private void ToolTip_Draw(object? sender, DrawToolTipEventArgs e)
    {
        var dpi = GetCurrentDpi();
        EnsureToolTipFrame(e.Bounds.Size, e.Graphics);
        using (var frameGraphics = Graphics.FromImage(_toolTipFrame!))
        {
            UsageHoverCardRenderer.Draw(frameGraphics,
                new Rectangle(Point.Empty, e.Bounds.Size), dpi, _hoverContent);
        }

        // 原生 ToolTip 逐条执行 GDI 绘制时，中间状态可能被桌面合成器捕获，
        // 高频动画下就表现为整张卡片闪烁。先在内存中完成一帧，再一次性提交。
        e.Graphics.DrawImageUnscaled(_toolTipFrame!, e.Bounds.Location);
        EnsureToolTipSubclass();
    }

    private void EnsureToolTipFrame(Size size, Graphics targetGraphics)
    {
        if (_toolTipFrame?.Size == size)
        {
            return;
        }

        _toolTipFrame?.Dispose();
        _toolTipFrame = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height),
            System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        _toolTipFrame.SetResolution(targetGraphics.DpiX, targetGraphics.DpiY);
    }

    private void EnsureToolTipSubclass()
    {
        if (_toolTipWindowSubclass.Handle != IntPtr.Zero)
        {
            return;
        }

        var toolTipHandle = FindToolTipWindow(requireVisible: false);
        if (toolTipHandle != IntPtr.Zero)
        {
            _toolTipWindowSubclass.Attach(toolTipHandle);
        }
    }

    private void RedrawVisibleToolTip()
    {
        if (!_hovered || !_hoverEnabled)
        {
            _toolTipAnimationTimer.Stop();
            return;
        }

        var toolTipHandle = FindToolTipWindow(requireVisible: true);
        if (toolTipHandle != IntPtr.Zero)
        {
            _ = InvalidateRect(toolTipHandle, IntPtr.Zero, erase: false);
        }
    }

    private Rectangle CalculateCurrentToolTipBounds(Size toolTipSize)
    {
        var dpi = GetCurrentDpi();
        var monitor = GetMonitorBounds(Handle);
        var workingArea = monitor.WorkArea;
        var margin = Math.Max(4, (int)Math.Round(6 * dpi / 96F));
        _ = GetWindowRect(Handle, out var nativeAnchor);
        var anchor = nativeAnchor.ToRectangle();
        return CalculateToolTipBounds(
            toolTipSize,
            anchor,
            workingArea,
            monitor.MonitorArea,
            _lastTaskbarBounds,
            margin);
    }

    internal static Rectangle CalculateToolTipBounds(
        Size toolTipSize,
        Rectangle anchor,
        Rectangle workArea,
        Rectangle monitorArea,
        Rectangle taskbarBounds,
        int margin)
    {
        var horizontalTaskbar = taskbarBounds.Width >= taskbarBounds.Height;
        int x;
        int y;
        if (horizontalTaskbar)
        {
            var taskbarAtBottom = taskbarBounds.Top + taskbarBounds.Height / 2 >=
                                  monitorArea.Top + monitorArea.Height / 2;
            x = anchor.Right - toolTipSize.Width;
            y = taskbarAtBottom
                ? workArea.Bottom - margin - toolTipSize.Height
                : workArea.Top + margin;
        }
        else
        {
            var taskbarAtRight = taskbarBounds.Left + taskbarBounds.Width / 2 >=
                                 monitorArea.Left + monitorArea.Width / 2;
            x = taskbarAtRight
                ? workArea.Right - margin - toolTipSize.Width
                : workArea.Left + margin;
            y = anchor.Bottom - toolTipSize.Height;
        }

        var minX = workArea.Left + margin;
        var maxX = Math.Max(minX, workArea.Right - margin - toolTipSize.Width);
        var minY = workArea.Top + margin;
        var maxY = Math.Max(minY, workArea.Bottom - margin - toolTipSize.Height);
        return new Rectangle(
            Math.Clamp(x, minX, maxX),
            Math.Clamp(y, minY, maxY),
            toolTipSize.Width,
            toolTipSize.Height);
    }

    private int GetCurrentDpi()
    {
        var dpi = GetDpiForWindow(Handle);
        return dpi == 0 ? DeviceDpi : checked((int)dpi);
    }

    private static Rectangle GetMonitorWorkArea(IntPtr window) =>
        GetMonitorBounds(window).WorkArea;

    private static (Rectangle MonitorArea, Rectangle WorkArea) GetMonitorBounds(IntPtr window)
    {
        var monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
        {
            return (info.Monitor.ToRectangle(), info.Work.ToRectangle());
        }

        var fallback = Screen.FromHandle(window);
        return (fallback.Bounds, fallback.WorkingArea);
    }

    private IntPtr FindToolTipWindow(bool requireVisible)
    {
        var uiThreadId = GetWindowThreadProcessId(Handle, out _);
        IntPtr result = IntPtr.Zero;
        _ = EnumThreadWindows(uiThreadId, (window, parameter) =>
        {
            var className = new StringBuilder(64);
            _ = GetClassName(window, className, className.Capacity);
            if (className.ToString().Contains("tooltips_class32",
                    StringComparison.OrdinalIgnoreCase) &&
                (!requireVisible || IsWindowVisible(window)))
            {
                result = window;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return result;
    }

    public void AttachOrReposition(bool force = false)
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        var tray = taskbar != IntPtr.Zero
            ? FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null)
            : IntPtr.Zero;

        if (taskbar == IntPtr.Zero || tray == IntPtr.Zero ||
            !GetWindowRect(taskbar, out var taskbarRect) ||
            !GetWindowRect(tray, out var trayRect))
        {
            ChangeAttachmentState(false);
            return;
        }

        var taskbarBounds = taskbarRect.ToRectangle();
        var trayBounds = trayRect.ToRectangle();
        var avoidedBounds = FindAvoidedToolbarBounds(taskbar);
        if (!force && taskbar == _taskbarHandle && IsAttached &&
            taskbarBounds == _lastTaskbarBounds && trayBounds == _lastTrayBounds &&
            avoidedBounds == _lastAvoidedBounds &&
            GetParent(Handle) == taskbar)
        {
            return;
        }

        _taskbarHandle = taskbar;
        _lastTaskbarBounds = taskbarBounds;
        _lastTrayBounds = trayBounds;
        _lastAvoidedBounds = avoidedBounds;

        if (GetParent(Handle) != taskbar)
        {
            SetParent(Handle, taskbar);
            var style = GetWindowLongPtr(Handle, GwlStyle).ToInt64();
            style = (style | WsChild) & ~WsPopup;
            SetWindowLongPtr(Handle, GwlStyle, new IntPtr(style));

            var extendedStyle = GetWindowLongPtr(Handle, GwlExStyle).ToInt64();
            extendedStyle |= WsExToolWindow | WsExNoActivate;
            SetWindowLongPtr(Handle, GwlExStyle, new IntPtr(extendedStyle));
        }

        var horizontal = taskbarBounds.Width >= taskbarBounds.Height;
        int x;
        int y;
        int width;
        int height;

        if (horizontal)
        {
            var rightEdge = avoidedBounds.IsEmpty
                ? trayBounds.Left
                : Math.Min(trayBounds.Left, avoidedBounds.Left);
            width = Math.Min(_desiredToolbarWidth,
                Math.Max(80, rightEdge - taskbarBounds.Left));
            var taskbarHeight = taskbarBounds.Height;
            height = taskbarHeight >= 36 ? 36 : Math.Max(1, taskbarHeight - 2);
            x = Math.Max(0, rightEdge - taskbarBounds.Left - width);
            y = Math.Max(0, (taskbarHeight - height) / 2);
        }
        else
        {
            var bottomEdge = avoidedBounds.IsEmpty
                ? trayBounds.Top
                : Math.Min(trayBounds.Top, avoidedBounds.Top);
            width = taskbarBounds.Width;
            height = Math.Min(VerticalToolbarHeight, Math.Max(48, bottomEdge - taskbarBounds.Top));
            x = 0;
            y = Math.Max(0, bottomEdge - taskbarBounds.Top - height);
        }

        var attached = GetParent(Handle) == taskbar &&
                       SetWindowPos(Handle, HwndTop, x, y, width, height,
                           SwpNoActivate | SwpShowWindow);
        if (attached)
        {
            ApplyRoundedWindowRegion(width, height);
        }

        ChangeAttachmentState(attached);
        Invalidate();
    }

    private Rectangle FindAvoidedToolbarBounds(IntPtr taskbar)
    {
        if (string.IsNullOrWhiteSpace(_avoidProcessName))
        {
            return Rectangle.Empty;
        }

        var processIds = new HashSet<uint>();
        foreach (var process in Process.GetProcessesByName(_avoidProcessName))
        {
            using (process)
            {
                processIds.Add(checked((uint)process.Id));
            }
        }

        if (processIds.Count == 0)
        {
            return Rectangle.Empty;
        }

        var result = Rectangle.Empty;
        _ = EnumChildWindows(taskbar, (window, parameter) =>
        {
            _ = GetWindowThreadProcessId(window, out var processId);
            if (processIds.Contains(processId) && GetParent(window) == taskbar &&
                IsWindowVisible(window) && GetWindowRect(window, out var bounds))
            {
                var candidate = bounds.ToRectangle();
                if (candidate.Width > 0 && candidate.Height > 0)
                {
                    result = candidate;
                    return false;
                }
            }

            return true;
        }, IntPtr.Zero);
        return result;
    }

    private void ChangeAttachmentState(bool attached)
    {
        if (IsAttached == attached)
        {
            return;
        }

        IsAttached = attached;
        AttachmentChanged?.Invoke(this, attached);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var bounds = ClientRectangle;
        var vertical = bounds.Height > bounds.Width;
        var pill = Rectangle.Inflate(bounds, -1, -1);
        if (pill.Width <= 0 || pill.Height <= 0)
        {
            return;
        }

        var fillColor = _pressed
            ? Color.FromArgb(82, 87, 96)
            : _hovered
                ? Color.FromArgb(69, 74, 83)
                : Color.FromArgb(52, 57, 65);
        e.Graphics.Clear(fillColor);

        using var path = RoundedRectangle(pill, Math.Min(10, pill.Height / 2));
        using (var borderPen = new Pen(_hovered
                   ? Color.FromArgb(128, 139, 154)
                   : Color.FromArgb(77, 84, 94)))
        {
            e.Graphics.DrawPath(borderPen, path);
        }

        var dotSize = vertical ? 8 : 9;
        var dotX = pill.Left + (vertical ? (pill.Width - dotSize) / 2 : 12);
        var dotY = vertical ? pill.Top + 8 : pill.Top + (pill.Height - dotSize) / 2;
        using (var statusBrush = new SolidBrush(_statusColor))
        {
            e.Graphics.FillEllipse(statusBrush, dotX, dotY, dotSize, dotSize);
        }

        var textBounds = vertical
            ? new Rectangle(pill.Left + 3, dotY + dotSize + 4, pill.Width - 6,
                Math.Max(1, pill.Bottom - dotY - dotSize - 6))
            : new Rectangle(dotX + dotSize + 8, pill.Top + 1,
                Math.Max(1, pill.Right - dotX - dotSize - 16), pill.Height - 2);
        using var font = new Font("Microsoft YaHei UI", vertical ? 9F : HorizontalFontSize,
            FontStyle.Regular, GraphicsUnit.Point);
        using var textBrush = new SolidBrush(Color.FromArgb(242, 244, 247));
        using var format = new StringFormat
        {
            Alignment = vertical ? StringAlignment.Center : StringAlignment.Near,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };
        e.Graphics.DrawString(_displayText, font, textBrush, textBounds, format);
    }

    private bool UpdateDesiredToolbarWidth()
    {
        if (!IsHandleCreated)
        {
            return false;
        }

        using var graphics = CreateGraphics();
        using var font = new Font("Microsoft YaHei UI", HorizontalFontSize,
            FontStyle.Regular, GraphicsUnit.Point);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces
        };
        var textWidth = (int)Math.Ceiling(
            graphics.MeasureString(_displayText, font, int.MaxValue, format).Width);

        // 文字从 x=30 左右开始。高 DPI 下 CreateGraphics 与任务栏子窗口的
        // GDI 字形宽度会有少量偏差，因此额外保留约 14px 安全余量。
        var desiredWidth = Math.Clamp(textWidth + 60,
            MinimumToolbarWidth, MaximumToolbarWidth);
        if (desiredWidth == _desiredToolbarWidth)
        {
            return false;
        }

        _desiredToolbarWidth = desiredWidth;
        return true;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _toolTipAnimationTimer.Stop();
        _toolTipPositionArmed = false;
        _hovered = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _pressed = true;
            Invalidate();
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && _pressed)
        {
            _pressed = false;
            Invalidate();
            DetailsRequested?.Invoke(this, EventArgs.Empty);
        }

        base.OnMouseUp(e);
    }

    protected override void OnDoubleClick(EventArgs e)
    {
        SettingsRequested?.Invoke(this, EventArgs.Empty);
        base.OnDoubleClick(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _attachmentTimer.Stop();
            _attachmentTimer.Dispose();
            _toolTipAnimationTimer.Stop();
            _toolTipAnimationTimer.Dispose();
            _toolTipFrame?.Dispose();
            _toolTipFrame = null;
            _toolTipWindowSubclass.Dispose();
            _toolTip.Popup -= ToolTip_Popup;
            _toolTip.Draw -= ToolTip_Draw;
            _toolTip.Dispose();
        }

        base.Dispose(disposing);
    }

    private static GraphicsPath RoundedRectangle(Rectangle rectangle, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition
    {
        public IntPtr Window;
        public IntPtr InsertAfter;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public uint Flags;
    }

    private sealed class ToolTipWindowSubclass(TaskbarToolbarForm owner)
        : NativeWindow, IDisposable
    {
        public void Attach(IntPtr window)
        {
            if (Handle == window)
            {
                return;
            }

            if (Handle != IntPtr.Zero)
            {
                ReleaseHandle();
            }

            AssignHandle(window);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmEraseBackground)
            {
                // 每一帧都会完整覆盖窗口，禁止系统在绘制前先刷背景，避免闪白。
                message.Result = new IntPtr(1);
                return;
            }

            if (message.Msg == WmWindowPosChanging && message.LParam != IntPtr.Zero)
            {
                TryReposition(message.LParam);
            }

            // AssignHandle 接管的是 ToolTip 的原生窗口过程。所有消息必须
            // 继续转发，特别是 SWP_HIDEWINDOW；否则 ToolTip 会认为自己仍在
            // 显示，导致下一次 Hover 不再弹出。
            base.WndProc(ref message);
        }

        private void TryReposition(IntPtr windowPosition)
        {
            try
            {
                var position = Marshal.PtrToStructure<WindowPosition>(windowPosition);
                if (!owner._toolTipPositionArmed ||
                    (position.Flags & SwpHideWindow) != 0)
                {
                    return;
                }

                var currentSize = GetCurrentSize();
                var width = (position.Flags & SwpNoSize) == 0 && position.Width > 0
                    ? position.Width
                    : owner._pendingToolTipWidth > 0
                        ? owner._pendingToolTipWidth
                        : currentSize.Width;
                var height = (position.Flags & SwpNoSize) == 0 && position.Height > 0
                    ? position.Height
                    : owner._pendingToolTipHeight > 0
                        ? owner._pendingToolTipHeight
                        : currentSize.Height;
                if (width <= 0 || height <= 0)
                {
                    return;
                }

                // 坐标必须在原生窗口过程执行前写回；否则系统会先按默认
                // 位置绘制一帧，之后再纠正就会产生肉眼可见的闪跳。
                var target = owner.CalculateCurrentToolTipBounds(new Size(width, height));
                position.X = target.X;
                position.Y = target.Y;
                position.Flags &= ~(uint)SwpNoMove;
                Marshal.StructureToPtr(position, windowPosition, fDeleteOld: false);
            }
            catch
            {
                // 绝不允许托管异常穿过原生窗口过程。
            }
        }

        private Size GetCurrentSize()
        {
            return Handle != IntPtr.Zero &&
                   GetWindowRect(Handle, out var bounds)
                ? bounds.ToRectangle().Size
                : Size.Empty;
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                ReleaseHandle();
            }
        }
    }

    private void ApplyRoundedWindowRegion(int width, int height)
    {
        var radius = Math.Min(12, Math.Max(4, height / 3));
        var region = CreateRoundRectRgn(0, 0, width + 1, height + 1, radius * 2, radius * 2);
        if (region == IntPtr.Zero)
        {
            return;
        }

        // SetWindowRgn 成功后区域句柄归系统所有；失败时由当前进程释放。
        if (SetWindowRgn(Handle, region, redraw: true) == 0)
        {
            DeleteObject(region);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumThreadWindows(
        uint threadId, EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(
        IntPtr parent, EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(
        IntPtr window, StringBuilder className, int maximumCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InvalidateRect(
        IntPtr window, IntPtr rectangle, [MarshalAs(UnmanagedType.Bool)] bool erase);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public int Flags;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(
        IntPtr parent,
        IntPtr childAfter,
        string? className,
        string? windowName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr child, IntPtr newParent);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        int flags);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateRoundRectRgn(
        int left,
        int top,
        int right,
        int bottom,
        int ellipseWidth,
        int ellipseHeight);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(IntPtr window, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr objectHandle);

    private static IntPtr GetWindowLongPtr(IntPtr window, int index) => IntPtr.Size == 8
        ? GetWindowLongPtr64(window, index)
        : new IntPtr(GetWindowLong32(window, index));

    private static IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr newValue) => IntPtr.Size == 8
        ? SetWindowLongPtr64(window, index, newValue)
        : new IntPtr(SetWindowLong32(window, index, newValue.ToInt32()));

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr window, int index, int newValue);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr newValue);
}
