using System.Runtime.InteropServices;

namespace UsageTray.Services;

internal sealed class ContextMenuDismissController : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WmClose = 0x0010;
    private const int WmQuit = 0x0012;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;
    private const int WmMButtonDown = 0x0207;
    private const int VkLButton = 0x01;
    private const int VkRButton = 0x02;
    private const int VkMButton = 0x04;

    private readonly ContextMenuStrip _menu;
    private readonly LowLevelMouseProc _mouseProc;
    private CancellationTokenSource? _monitorCancellation;
    private Task? _monitorTask;
    private Thread? _mouseThread;
    private IntPtr _mouseHook;
    private IntPtr _activeMenuWindow;
    private uint _mouseThreadId;
    private int _closeQueued;
    private bool _disposed;

    public ContextMenuDismissController(ContextMenuStrip menu)
    {
        _menu = menu;
        _mouseProc = MouseHookCallback;
        _menu.Opened += Menu_Opened;
        _menu.Closed += Menu_Closed;
    }

    private void Menu_Opened(object? sender, EventArgs e)
    {
        StopMonitor();
        Interlocked.Exchange(ref _closeQueued, 0);

        _menu.Focus();
        _ = SetForegroundWindow(_menu.Handle);
        _activeMenuWindow = _menu.Handle;
        StartMouseMonitor();

        var cancellation = new CancellationTokenSource();
        _monitorCancellation = cancellation;
        _monitorTask = Task.Run(() => MonitorMenuAsync(
            _menu.Handle, GetForegroundWindow(), cancellation.Token));
    }

    private void Menu_Closed(object? sender, ToolStripDropDownClosedEventArgs e)
    {
        StopMonitor();
        StopMouseMonitor();
        _activeMenuWindow = IntPtr.Zero;
        Interlocked.Exchange(ref _closeQueued, 0);
    }

    private async Task MonitorMenuAsync(
        IntPtr menuWindow,
        IntPtr foregroundBaseline,
        CancellationToken cancellationToken)
    {
        try
        {
            // 打开右键菜单的那次按键可能尚未松开，先等所有鼠标键恢复。
            while (IsAnyButtonDown() && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }

            var previousButtonDown = false;
            var foregroundSettledAt = DateTime.UtcNow.AddMilliseconds(250);
            while (!cancellationToken.IsCancellationRequested && IsWindowVisible(menuWindow))
            {
                var foreground = GetForegroundWindow();
                if (foreground == menuWindow)
                {
                    foregroundBaseline = menuWindow;
                }
                else if (foregroundBaseline != IntPtr.Zero &&
                         foreground != IntPtr.Zero &&
                         foreground != foregroundBaseline)
                {
                    QueueClose(menuWindow);
                    return;
                }
                else if (foregroundBaseline == IntPtr.Zero &&
                         foreground != IntPtr.Zero &&
                         DateTime.UtcNow >= foregroundSettledAt)
                {
                    // 打开时没有前台窗口，稍后出现非菜单前台窗口说明
                    // 用户已切换到其他应用。
                    QueueClose(menuWindow);
                    return;
                }

                var buttonDown = IsAnyButtonDown();
                if (GetCursorPos(out var point) &&
                    GetWindowRect(menuWindow, out var bounds) &&
                    !bounds.Contains(point) &&
                    buttonDown && !previousButtonDown)
                {
                    QueueClose(menuWindow);
                    return;
                }

                previousButtonDown = buttonDown;
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 菜单正常关闭或应用退出。
        }
    }

    private void QueueClose(IntPtr menuWindow)
    {
        if (Interlocked.Exchange(ref _closeQueued, 1) != 0)
        {
            return;
        }

        // ContextMenuStrip 在任务栏 WS_EX_NOACTIVATE 子窗口上会进入特殊的
        // 菜单消息循环，BeginInvoke 和 WinForms Timer 可能无法及时执行。
        // WM_CLOSE 可跨线程安全投递，并会走正常的 Closed/Dispose 流程。
        _ = PostMessage(menuWindow, WmClose, IntPtr.Zero, IntPtr.Zero);
    }

    private void StartMouseMonitor()
    {
        StopMouseMonitor();
        var ready = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _mouseThread = new Thread(() => RunMouseMonitor(ready))
        {
            IsBackground = true,
            Name = "UsageTray context menu monitor"
        };
        _mouseThread.Start();
        ready.Task.GetAwaiter().GetResult();
    }

    private void RunMouseMonitor(TaskCompletionSource ready)
    {
        _mouseThreadId = GetCurrentThreadId();
        // 先显式创建线程消息队列，确保菜单刚打开就关闭时 WM_QUIT 也不会丢失。
        _ = PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
        _mouseHook = SetWindowsHookEx(
            WhMouseLl, _mouseProc, GetModuleHandle(null), 0);
        ready.SetResult();

        try
        {
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                _ = TranslateMessage(ref message);
                _ = DispatchMessage(ref message);
            }
        }
        finally
        {
            if (_mouseHook != IntPtr.Zero)
            {
                _ = UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
            }

            _mouseThreadId = 0;
        }
    }

    private IntPtr MouseHookCallback(int code, IntPtr message, IntPtr hookData)
    {
        var menuWindow = _activeMenuWindow;
        if (code >= 0 && menuWindow != IntPtr.Zero &&
            message.ToInt32() is WmLButtonDown or WmRButtonDown or WmMButtonDown)
        {
            var input = Marshal.PtrToStructure<LowLevelMouseInput>(hookData);
            if (GetWindowRect(menuWindow, out var bounds) &&
                !bounds.Contains(input.Point))
            {
                QueueClose(menuWindow);
            }
        }

        return CallNextHookEx(_mouseHook, code, message, hookData);
    }

    private void StopMouseMonitor()
    {
        var thread = Interlocked.Exchange(ref _mouseThread, null);
        var threadId = _mouseThreadId;
        if (thread is null)
        {
            return;
        }

        if (threadId != 0)
        {
            _ = PostThreadMessage(threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        }

        if (thread != Thread.CurrentThread)
        {
            _ = thread.Join(TimeSpan.FromMilliseconds(500));
        }
    }

    private void StopMonitor()
    {
        var cancellation = Interlocked.Exchange(ref _monitorCancellation, null);
        var monitorTask = Interlocked.Exchange(ref _monitorTask, null);
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        if (monitorTask is null || monitorTask.IsCompleted)
        {
            cancellation.Dispose();
            return;
        }

        _ = monitorTask.ContinueWith(
            _ => cancellation.Dispose(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopMonitor();
        StopMouseMonitor();
        _menu.Opened -= Menu_Opened;
        _menu.Closed -= Menu_Closed;
    }

    private static bool IsAnyButtonDown() =>
        IsButtonDown(VkLButton) ||
        IsButtonDown(VkRButton) ||
        IsButtonDown(VkMButton);

    private static bool IsButtonDown(int virtualKey) =>
        (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    private delegate IntPtr LowLevelMouseProc(
        int code, IntPtr message, IntPtr hookData);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int hookId,
        LowLevelMouseProc callback,
        IntPtr module,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hook, int code, IntPtr message, IntPtr hookData);

    [DllImport("user32.dll")]
    private static extern int GetMessage(
        out NativeMessage message, IntPtr window, uint minimum, uint maximum);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(
        out NativeMessage message,
        IntPtr window,
        uint minimum,
        uint maximum,
        uint removeMessage);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref NativeMessage message);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(
        uint threadId, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(
        IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelMouseInput
    {
        public NativePoint Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public NativePoint Point;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly bool Contains(NativePoint point) =>
            point.X >= Left && point.X < Right &&
            point.Y >= Top && point.Y < Bottom;
    }

}
