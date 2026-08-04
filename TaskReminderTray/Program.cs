using System.Threading;
using TaskReminderTray.Services;

namespace TaskReminderTray;

internal static class Program
{
    private const string MutexName = @"Local\TaskReminderTray.SingleInstance";

    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (UpdateInstaller.TryRunApplyMode(args))
        {
            return;
        }

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("TaskReminderTray 已在运行，请查看任务栏右侧。",
                "任务提醒", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        UpdateInstaller.ScheduleCleanupIfNeeded(args);
        Application.Run(new TrayApplicationContext());
    }
}
