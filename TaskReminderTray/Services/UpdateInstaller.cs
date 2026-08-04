using System.Diagnostics;
using System.Security.Cryptography;

namespace TaskReminderTray.Services;

internal static class UpdateInstaller
{
    private const string ApplyArgument = "--apply-update";
    private const string FinishArgument = "--finish-update";

    public static void Launch(string downloadedExecutable, string expectedHash)
    {
        var target = Environment.ProcessPath ?? Application.ExecutablePath;
        var startInfo = new ProcessStartInfo
        {
            FileName = downloadedExecutable,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(ApplyArgument);
        startInfo.ArgumentList.Add(target);
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        startInfo.ArgumentList.Add(expectedHash);
        if (Process.Start(startInfo) is null)
        {
            throw new UpdateException("无法启动更新程序。");
        }
    }

    public static bool TryRunApplyMode(string[] args)
    {
        if (args.Length == 0 || !string.Equals(args[0], ApplyArgument, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            if (args.Length != 4 || !int.TryParse(args[2], out var oldProcessId))
            {
                throw new UpdateException("更新参数无效。");
            }

            Apply(args[1], oldProcessId, args[3]);
        }
        catch (Exception ex)
        {
            WriteLog("安装更新失败", ex);
            if (args.Length > 1 && File.Exists(args[1]))
            {
                TryStart(args[1]);
            }

            MessageBox.Show($"安装更新失败，旧版本未被删除。\n\n{ex.Message}",
                "TaskReminderTray 更新", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        return true;
    }

    public static void ScheduleCleanupIfNeeded(string[] args)
    {
        if (args.Length != 4 || !string.Equals(args[0], FinishArgument, StringComparison.Ordinal) ||
            !int.TryParse(args[3], out var updaterProcessId))
        {
            return;
        }

        var downloadedExecutable = args[1];
        var backupPath = args[2];
        _ = Task.Run(async () =>
        {
            await WaitForExitAsync(updaterProcessId, TimeSpan.FromSeconds(30));
            await DeleteWithRetryAsync(downloadedExecutable);
            await DeleteWithRetryAsync(backupPath);
            TryDeleteEmptyParents(Path.GetDirectoryName(downloadedExecutable));
        });
    }

    private static void Apply(string targetPath, int oldProcessId, string expectedHash)
    {
        targetPath = Path.GetFullPath(targetPath);
        var updaterPath = Path.GetFullPath(Environment.ProcessPath ?? Application.ExecutablePath);
        if (string.Equals(targetPath, updaterPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateException("更新程序不能覆盖自身路径。");
        }

        ValidateExpectedHash(expectedHash);
        var updaterHash = ComputeSha256(updaterPath);
        if (!string.Equals(updaterHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateException("更新程序 SHA-256 校验失败。");
        }

        if (!WaitForExit(oldProcessId, TimeSpan.FromSeconds(60)))
        {
            throw new UpdateException("旧版本未能在 60 秒内退出。");
        }

        var backupPath = targetPath + ".update-backup";
        TryDelete(backupPath);
        var targetMoved = false;
        try
        {
            File.Move(targetPath, backupPath, overwrite: true);
            targetMoved = true;
            File.Copy(updaterPath, targetPath, overwrite: true);
            if (!string.Equals(ComputeSha256(targetPath), expectedHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdateException("替换后的程序校验失败。");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = targetPath,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(targetPath) ?? Environment.CurrentDirectory
            };
            startInfo.ArgumentList.Add(FinishArgument);
            startInfo.ArgumentList.Add(updaterPath);
            startInfo.ArgumentList.Add(backupPath);
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
            if (Process.Start(startInfo) is null)
            {
                throw new UpdateException("新版程序无法启动。");
            }
        }
        catch
        {
            TryDelete(targetPath);
            if (targetMoved && File.Exists(backupPath))
            {
                File.Move(backupPath, targetPath, overwrite: true);
            }

            throw;
        }
    }

    private static bool WaitForExit(int processId, TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.WaitForExit((int)timeout.TotalMilliseconds);
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static async Task WaitForExitAsync(int processId, TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            using var cancellation = new CancellationTokenSource(timeout);
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (Exception ex) when (ex is ArgumentException or OperationCanceledException)
        {
            // 进程已退出或等待超时；后续删除重试会处理短暂文件锁。
        }
    }

    private static async Task DeleteWithRetryAsync(string path)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (IOException)
            {
                await Task.Delay(500);
            }
            catch (UnauthorizedAccessException)
            {
                await Task.Delay(500);
            }
        }
    }

    private static void TryDeleteEmptyParents(string? directory)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory) &&
                !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch
        {
            // 更新已完成，目录清理失败不影响运行。
        }
    }

    private static void ValidateExpectedHash(string hash)
    {
        if (hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new UpdateException("更新校验值格式无效。");
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // 由恢复或下次更新覆盖处理。
        }
    }

    private static void TryStart(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = false });
        }
        catch
        {
            // 原始文件已恢复，用户仍可手动启动。
        }
    }

    private static void WriteLog(string message, Exception exception)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TaskReminderTray");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "update.log"),
                $"[{DateTime.Now:O}] {message}\n{exception}\n\n");
        }
        catch
        {
            // 记录日志不能覆盖原始错误。
        }
    }
}
