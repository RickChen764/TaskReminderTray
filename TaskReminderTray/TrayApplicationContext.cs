using System.Diagnostics;
using TaskReminderTray.Models;
using TaskReminderTray.Services;
using UsageTray;
using UsageTray.Services;

namespace TaskReminderTray;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly SettingsStore _settingsStore = new();
    private readonly PlaneIssueClient _client = new();
    private readonly UpdateService _updateService = new();
    private readonly IssueSnapshotStore _snapshotStore = new();
    private readonly NotificationStore _notificationStore = new();
    private readonly PersonalWorkStore _personalWorkStore = new();
    private readonly ReminderEvaluator _reminderEvaluator = new();
    private readonly PersistentNotificationForm _notificationForm = new();
    private readonly ScheduleDetailsForm _detailsForm = new();
    private readonly SnoozedReminderForm _snoozedReminderForm = new();
    private readonly NotifyIcon _notifyIcon;
    private readonly TaskbarToolbarForm _toolbar;
    private readonly ContextMenuStrip _menu;
    private readonly ContextMenuDismissController _dismissController;
    private readonly System.Windows.Forms.Timer _refreshTimer = new();
    private readonly System.Windows.Forms.Timer _updateTimer = new();
    private readonly System.Windows.Forms.Timer _personalReminderTimer = new() { Interval = 15000 };
    private readonly System.Windows.Forms.Timer _balloonVisibilityTimer = new()
    {
        Interval = 7000
    };
    private readonly ToolStripMenuItem _summaryItem = new("等待读取") { Enabled = false };
    private readonly ToolStripMenuItem _dueItem = new("临期：--") { Enabled = false };
    private readonly ToolStripMenuItem _updatedItem = new("最后更新：--") { Enabled = false };
    private readonly ToolStripMenuItem _refreshItem = new("立即刷新");
    private readonly ToolStripMenuItem _doNotDisturbMenu = new("免打扰");
    private readonly ToolStripMenuItem _doNotDisturbStatusItem = new("当前：未开启")
    {
        Enabled = false
    };
    private readonly ToolStripMenuItem _manualDoNotDisturbItem = new("手动开启免打扰")
    {
        CheckOnClick = true
    };
    private readonly ToolStripMenuItem _manageDoNotDisturbItem = new("管理时间段…");
    private readonly ToolStripMenuItem _versionItem = new() { Enabled = false };
    private readonly ToolStripMenuItem _updateItem = new("检查更新…");
    private AppSettings _settings;
    private Icon? _currentIcon;
    private bool _refreshing;
    private bool _checkingUpdate;
    private bool _showingUpdatePrompt;
    private bool _installingUpdate;
    private bool _lastDoNotDisturbActive;
    private string? _lastError;
    private IReadOnlyList<IssueItem> _lastIssues = [];
    private IReadOnlyList<PersistentNotification> _pendingNotifications = [];
    private UpdateRelease? _availableUpdate;
    private HoverCardContent? _detailsContent;
    private PersonalWorkState _personalWorkState;

    public TrayApplicationContext()
    {
        _settings = _settingsStore.Load(out var warning);
        _personalWorkState = _personalWorkStore.Load();

        _menu = new ContextMenuStrip();
        _menu.Items.Add(_summaryItem);
        _menu.Items.Add(_dueItem);
        _menu.Items.Add(_updatedItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_refreshItem);
        _menu.Items.Add("打开任务页面", null, (_, _) => OpenSourcePage());
        _menu.Items.Add("设置…", null, (_, _) => ShowSettings());
        _doNotDisturbMenu.DropDownItems.Add(_doNotDisturbStatusItem);
        _doNotDisturbMenu.DropDownItems.Add(new ToolStripSeparator());
        _doNotDisturbMenu.DropDownItems.Add(_manualDoNotDisturbItem);
        _doNotDisturbMenu.DropDownItems.Add(_manageDoNotDisturbItem);
        _menu.Items.Add(_doNotDisturbMenu);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_versionItem);
        _menu.Items.Add(_updateItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("退出", null, (_, _) => ExitThread());
        _dismissController = new ContextMenuDismissController(_menu);
        _refreshItem.Click += async (_, _) => await RefreshAsync();
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        _updateItem.Click += async (_, _) => await UpdateMenuItem_ClickAsync();
        _manualDoNotDisturbItem.Click += (_, _) => ToggleManualDoNotDisturb();
        _manageDoNotDisturbItem.Click += (_, _) => ManageDoNotDisturbRanges();
        _updateTimer.Interval = checked(6 * 60 * 60 * 1000);
        _updateTimer.Tick += async (_, _) => await CheckForUpdatesAsync(notifyWhenCurrent: false);
        _versionItem.Text = $"当前版本：v{UpdateService.CurrentVersion.ToString(3)}";

        _notifyIcon = new NotifyIcon
        {
            Visible = false,
            ContextMenuStrip = _menu,
            Text = "TaskReminderTray - 等待配置"
        };
        UpdateIcon(null, TrayIconState.Loading);

        _toolbar = new TaskbarToolbarForm(_menu, avoidProcessName: "UsageTray");
        _toolbar.DetailsRequested += (_, _) => ToggleDetails();
        _toolbar.AttachmentChanged += (_, attached) => _notifyIcon.Visible = !attached;
        _notificationForm.AcknowledgeRequested += (_, notificationId) =>
            AcknowledgeNotification(notificationId);
        _detailsForm.FocusChanged += SetFocusIssue;
        _detailsForm.SnoozeRequested += AddSnoozedReminder;
        _snoozedReminderForm.AcknowledgeRequested += AcknowledgeSnoozedReminder;
        _snoozedReminderForm.RescheduleRequested += RescheduleSnoozedReminder;
        _personalReminderTimer.Tick += (_, _) =>
        {
            UpdateDoNotDisturbState();
            ShowDuePersonalReminder();
        };
        _balloonVisibilityTimer.Tick += (_, _) =>
        {
            _balloonVisibilityTimer.Stop();
            if (_toolbar.IsAttached)
            {
                _notifyIcon.Visible = false;
            }
        };
        _toolbar.SetDisplay("待配置",
            HoverCardContent.CreateStatus("待配置", "请配置任务地址与登录信息。",
                Color.FromArgb(124, 132, 145)),
            Color.FromArgb(124, 132, 145));
        _toolbar.SetHoverEnabled(false);
        _toolbar.Show();
        _notifyIcon.Visible = !_toolbar.IsAttached;

        _lastDoNotDisturbActive = IsDoNotDisturbActive;
        UpdateDoNotDisturbMenu(_lastDoNotDisturbActive);

        ConfigureTimer();
        _updateTimer.Start();
        _personalReminderTimer.Start();
        _ = CheckForUpdatesAfterStartupAsync();
        if (!string.IsNullOrWhiteSpace(warning))
        {
            ShowBalloon("配置读取失败", warning, ToolTipIcon.Warning);
        }

        if (_settings.IsConfigured)
        {
            _ = RefreshAsync();
        }

        _pendingNotifications = _notificationStore.LoadPending();
        ShowPendingNotification();
        ShowDuePersonalReminder();
    }

    private async Task RefreshAsync()
    {
        if (_refreshing || !_settings.IsConfigured)
        {
            return;
        }

        _refreshing = true;
        _refreshItem.Enabled = false;
        try
        {
            var issues = await _client.GetIssuesAsync(_settings);
            ApplyIssues(issues);
            NotifyChanges(issues);
            _lastIssues = issues;
            _lastError = null;
        }
        catch (Exception exception)
        {
            ApplyRefreshError(exception.Message);
        }
        finally
        {
            _refreshing = false;
            _refreshItem.Enabled = true;
        }
    }

    private void ApplyIssues(IReadOnlyList<IssueItem> issues)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var summary = ScheduleSummary.Create(issues, today, _settings.DueSoonDays);
        var focusIssue = issues.FirstOrDefault(issue =>
            !issue.IsCompleted && string.Equals(issue.Id, _personalWorkState.FocusIssueId,
                StringComparison.OrdinalIgnoreCase));
        if (_personalWorkState.FocusIssueId is not null && focusIssue is null)
        {
            _personalWorkState = _personalWorkStore.SetFocus(null);
        }
        _summaryItem.Text = $"当前：开发 {summary.DevelopmentCount} · 跟进 {summary.FollowUpCount}";
        _dueItem.Text = $"等待输入 {summary.WaitingCount} · Bug {summary.BugCount}";
        _updatedItem.Text = $"最后更新：{DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        var developmentOverdue = summary.DevelopmentIssues.Count(issue =>
            issue.DueDate is { } due && due < today);
        var color = developmentOverdue > 0
            ? Color.FromArgb(211, 66, 76)
            : summary.DueSoonCount > 0
                ? Color.FromArgb(230, 148, 48)
                : Color.FromArgb(61, 177, 103);
        var display = focusIssue is not null
            ? $"重点 {focusIssue.Key} · 后续 {Math.Max(0, summary.DevelopmentCount - 1)}"
            : summary.CurrentFocus is { } focus
            ? $"{focus.Key} · 后续 {Math.Max(0, summary.DevelopmentCount - 1)}"
            : "暂无开发任务";
        if (developmentOverdue > 0)
        {
            display += $" · 逾{developmentOverdue}";
        }

        var expandedDates = _detailsContent?.ExpandedDates.ToArray() ?? [];
        _detailsContent = HoverCardContent.CreateSchedule(summary, today, DateTime.Now, color);
        _detailsContent.ExpandedDates.UnionWith(expandedDates);
        _detailsContent.FocusIssueId = _personalWorkState.FocusIssueId;
        _toolbar.SetDisplay(display, _detailsContent, color);
        _detailsForm.SetContent(_detailsContent);
        SetTooltip($"TaskReminderTray - {display}");
        UpdateIcon(summary.TotalCount,
            developmentOverdue > 0 || summary.DueSoonCount > 0
                ? TrayIconState.Warning
                : TrayIconState.Healthy);
    }

    private void NotifyChanges(IReadOnlyList<IssueItem> issues)
    {
        var changes = _snapshotStore.CompareAndSave(issues);
        if (changes.Count > 0)
        {
            _pendingNotifications = _notificationStore.AddChanges(changes);
            ShowPendingNotification();
        }

        var due = _reminderEvaluator.GetDueReminders(issues,
            DateOnly.FromDateTime(DateTime.Now), _settings.DueSoonDays);
        if (due.Count > 0)
        {
            var first = due.OrderBy(issue => issue.DueDate).First();
            var dateText = first.DueDate < DateOnly.FromDateTime(DateTime.Now)
                ? $"已逾期 {DateOnly.FromDateTime(DateTime.Now).DayNumber - first.DueDate!.Value.DayNumber} 天"
                : first.DueDate == DateOnly.FromDateTime(DateTime.Now)
                    ? "今天到期"
                    : $"{first.DueDate:MM-dd} 到期";
            var extra = due.Count > 1 ? $"，另有 {due.Count - 1} 项" : string.Empty;
            ShowBalloon("任务到期提醒", $"{first.Key} {first.Title}\n{dateText}{extra}",
                ToolTipIcon.Warning);
        }
    }

    private void ApplyRefreshError(string message)
    {
        _updatedItem.Text = $"最后尝试：{DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        if (_lastIssues.Count == 0)
        {
            _summaryItem.Text = "读取失败";
            SetTooltip("TaskReminderTray - 读取失败");
            UpdateIcon(null, TrayIconState.Error);
        }
        if (!string.Equals(_lastError, message, StringComparison.Ordinal))
        {
            _lastError = message;
            ShowBalloon("任务读取失败", message, ToolTipIcon.Error);
        }
    }

    private void ToggleDetails()
    {
        if (!_settings.IsConfigured || _detailsContent is null)
        {
            return;
        }

        _detailsForm.Toggle(_toolbar.GetScreenBounds());
    }

    private void SetFocusIssue(IssueItem? issue)
    {
        _personalWorkState = _personalWorkStore.SetFocus(issue?.Id);
        if (_detailsContent is not null)
        {
            _detailsContent.FocusIssueId = _personalWorkState.FocusIssueId;
        }

        if (_lastIssues.Count > 0)
        {
            ApplyIssues(_lastIssues);
        }
        else if (_detailsContent is not null)
        {
            _detailsForm.SetContent(_detailsContent);
        }
    }

    private void AddSnoozedReminder(IssueItem issue, DateTimeOffset remindAt)
    {
        _personalWorkState = _personalWorkStore.AddReminder(issue, remindAt);
        ShowBalloon("已设置稍后提醒",
            $"{issue.Key} {issue.DisplayTitle}\n{remindAt.LocalDateTime:MM-dd HH:mm} 提醒",
            ToolTipIcon.Info);
    }

    private void AcknowledgeSnoozedReminder(string reminderId)
    {
        _personalWorkState = _personalWorkStore.RemoveReminder(reminderId);
        ShowDuePersonalReminder();
    }

    private void RescheduleSnoozedReminder(string reminderId, DateTimeOffset remindAt)
    {
        _personalWorkState = _personalWorkStore.RescheduleReminder(reminderId, remindAt);
        ShowDuePersonalReminder();
    }

    private void ShowDuePersonalReminder()
    {
        if (IsDoNotDisturbActive)
        {
            _snoozedReminderForm.HideReminder();
            return;
        }

        var due = _personalWorkState.Reminders
            .Where(reminder => reminder.RemindAt <= DateTimeOffset.Now)
            .OrderBy(reminder => reminder.RemindAt)
            .ToArray();
        if (due.Length == 0)
        {
            _snoozedReminderForm.HideReminder();
            return;
        }

        _snoozedReminderForm.ShowReminder(due[0], due.Length);
    }

    private void AcknowledgeNotification(string notificationId)
    {
        try
        {
            _pendingNotifications = _notificationStore.Acknowledge(notificationId);
            ShowPendingNotification();
        }
        catch (Exception exception)
        {
            MessageBox.Show($"无法清除这条通知：{exception.Message}", "任务提醒",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ShowPendingNotification()
    {
        if (IsDoNotDisturbActive)
        {
            _notificationForm.HideNotification();
            return;
        }

        if (_pendingNotifications.Count == 0)
        {
            _notificationForm.HideNotification();
            return;
        }

        _notificationForm.ShowNotification(_pendingNotifications[0],
            _pendingNotifications.Count);
    }

    private void ShowSettings()
    {
        using var form = new SettingsForm(_settings.Clone());
        if (form.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        try
        {
            StartupManager.SetEnabled(form.Result.StartWithWindows);
            _settingsStore.Save(form.Result);
            _settings = form.Result;
            ConfigureTimer();
            _ = RefreshAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show($"保存配置失败：{exception.Message}", "任务提醒",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private bool IsDoNotDisturbActive => DoNotDisturbEvaluator.IsActive(
        _settings.ManualDoNotDisturb,
        _settings.DoNotDisturbRanges,
        TimeOnly.FromDateTime(DateTime.Now));

    private void ToggleManualDoNotDisturb()
    {
        var previous = _settings.ManualDoNotDisturb;
        try
        {
            _settings.ManualDoNotDisturb = _manualDoNotDisturbItem.Checked;
            _settingsStore.Save(_settings);
            UpdateDoNotDisturbState(force: true);
        }
        catch (Exception exception)
        {
            _settings.ManualDoNotDisturb = previous;
            _manualDoNotDisturbItem.Checked = _settings.ManualDoNotDisturb;
            MessageBox.Show($"无法保存免打扰设置：{exception.Message}", "任务提醒",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ManageDoNotDisturbRanges()
    {
        using var form = new DoNotDisturbScheduleForm(_settings.DoNotDisturbRanges);
        if (form.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        var previous = _settings.DoNotDisturbRanges;
        try
        {
            _settings.DoNotDisturbRanges = [.. form.Result];
            _settingsStore.Save(_settings);
            UpdateDoNotDisturbState(force: true);
        }
        catch (Exception exception)
        {
            _settings.DoNotDisturbRanges = previous;
            MessageBox.Show($"无法保存免打扰时间段：{exception.Message}", "任务提醒",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UpdateDoNotDisturbState(bool force = false)
    {
        var active = IsDoNotDisturbActive;
        var stateChanged = active != _lastDoNotDisturbActive;
        UpdateDoNotDisturbMenu(active);
        if (!force && !stateChanged)
        {
            return;
        }

        _lastDoNotDisturbActive = active;
        if (stateChanged)
        {
            ConfigureTimer();
        }

        if (active)
        {
            _notificationForm.HideNotification();
            _snoozedReminderForm.HideReminder();
            return;
        }

        ShowPendingNotification();
        ShowDuePersonalReminder();
        if (stateChanged)
        {
            _ = RefreshAsync();
        }
    }

    private void UpdateDoNotDisturbMenu(bool active)
    {
        _manualDoNotDisturbItem.Checked = _settings.ManualDoNotDisturb;
        _doNotDisturbMenu.Text = active ? "免打扰（已开启）" : "免打扰";
        if (_settings.ManualDoNotDisturb)
        {
            _doNotDisturbStatusItem.Text = "当前：手动开启";
            return;
        }

        var activeRange = DoNotDisturbEvaluator.ActiveRange(
            _settings.DoNotDisturbRanges, TimeOnly.FromDateTime(DateTime.Now));
        _doNotDisturbStatusItem.Text = activeRange is not null
            ? $"当前：时间段 {activeRange.DisplayText}"
            : "当前：未开启";
    }

    private void ConfigureTimer()
    {
        _refreshTimer.Stop();
        var minutes = RefreshIntervalPolicy.GetMinutes(
            _settings.RefreshMinutes, IsDoNotDisturbActive);
        _refreshTimer.Interval = checked(minutes * 60 * 1000);
        if (_settings.IsConfigured)
        {
            _refreshTimer.Start();
        }
    }

    private void OpenSourcePage()
    {
        if (!Uri.TryCreate(_settings.SourceUrl, UriKind.Absolute, out var uri))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
    }

    private async Task CheckForUpdatesAfterStartupAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(12));
        await CheckForUpdatesAsync(notifyWhenCurrent: false);
    }

    private async Task CheckForUpdatesAsync(bool notifyWhenCurrent)
    {
        if (_checkingUpdate || _installingUpdate)
        {
            return;
        }

        _checkingUpdate = true;
        _updateItem.Enabled = false;
        SetUpdateBusyState("更新中：正在检查…");
        try
        {
            var release = await _updateService.CheckAsync();
            if (release is not null)
            {
                _availableUpdate = release;
                _updateItem.Text = $"发现新版本 v{release.Version.ToString(3)}（点击更新）";
                _updateItem.Enabled = true;
                ShowBalloon("TaskReminderTray 有新版本",
                    $"v{release.Version.ToString(3)} 已发布。右键工具条并选择更新。",
                    ToolTipIcon.Info);
                return;
            }

            _availableUpdate = null;
            _updateItem.Text = "检查更新…";
            if (notifyWhenCurrent)
            {
                MessageBox.Show($"当前已是最新版本 v{UpdateService.CurrentVersion.ToString(3)}。",
                    "TaskReminderTray 更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception exception)
        {
            _updateItem.Text = "检查更新失败（点击重试）";
            if (notifyWhenCurrent)
            {
                MessageBox.Show($"检查更新失败。\n\n{exception.Message}",
                    "TaskReminderTray 更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        finally
        {
            _checkingUpdate = false;
            if (!_installingUpdate)
            {
                _updateItem.Enabled = true;
            }
        }
    }

    private async Task UpdateMenuItem_ClickAsync()
    {
        if (_showingUpdatePrompt || _installingUpdate)
        {
            return;
        }

        if (_availableUpdate is null)
        {
            await CheckForUpdatesAsync(notifyWhenCurrent: true);
            return;
        }

        _showingUpdatePrompt = true;
        _updateItem.Enabled = false;
        try
        {
            var release = _availableUpdate;
            using var prompt = new UpdatePromptForm(release);
            if (prompt.ShowDialog() == DialogResult.Yes)
            {
                await InstallUpdateAsync(release);
            }
        }
        finally
        {
            _showingUpdatePrompt = false;
            if (!_installingUpdate)
            {
                _updateItem.Enabled = true;
            }
        }
    }

    private async Task InstallUpdateAsync(UpdateRelease release)
    {
        _installingUpdate = true;
        SetUpdateBusyState("更新中：准备下载…");
        try
        {
            var progress = new Progress<UpdateProgress>(value =>
                SetUpdateBusyState(FormatUpdateProgress(value)));
            var downloaded = await _updateService.DownloadAndVerifyAsync(release, progress);
            SetUpdateBusyState("更新中：正在安装并重启…");
            UpdateInstaller.Launch(downloaded.ExecutablePath, downloaded.Sha256);
            ExitThread();
        }
        catch (Exception exception)
        {
            _installingUpdate = false;
            _updateItem.Enabled = true;
            _updateItem.Text = $"更新 v{release.Version.ToString(3)}（点击重试）";
            MessageBox.Show($"更新失败，当前版本未被替换。\n\n{exception.Message}",
                "TaskReminderTray 更新", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SetUpdateBusyState(string text)
    {
        _updateItem.Text = text;
        _updateItem.Enabled = false;
    }

    private static string FormatUpdateProgress(UpdateProgress progress) => progress.Stage switch
    {
        UpdateProgressStage.Preparing => "更新中：准备下载…",
        UpdateProgressStage.Downloading when progress.Percentage is not null =>
            $"更新中：下载 {progress.Percentage.Value}%",
        UpdateProgressStage.Downloading => "更新中：正在下载…",
        UpdateProgressStage.Verifying => "更新中：正在校验…",
        _ => "更新中…"
    };

    private void UpdateIcon(int? count, TrayIconState state)
    {
        var next = TrayIconRenderer.Create(count, state);
        _notifyIcon.Icon = next;
        _currentIcon?.Dispose();
        _currentIcon = next;
    }

    private void SetTooltip(string text) =>
        _notifyIcon.Text = text.Length <= 63 ? text : text[..62] + "…";

    private void ShowBalloon(string title, string text, ToolTipIcon icon)
    {
        if (IsDoNotDisturbActive)
        {
            return;
        }

        // NotifyIcon 隐藏时 Windows 不显示气泡。嵌入式工具条工作期间仅在
        // 通知所需的几秒内注册图标，之后自动隐藏，避免长期出现重复入口。
        if (!_notifyIcon.Visible)
        {
            _notifyIcon.Visible = true;
            _balloonVisibilityTimer.Stop();
            _balloonVisibilityTimer.Start();
        }

        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = text.Length <= 240 ? text : text[..239] + "…";
        _notifyIcon.BalloonTipIcon = icon;
        _notifyIcon.ShowBalloonTip(5000);
    }

    protected override void ExitThreadCore()
    {
        _refreshTimer.Stop();
        _refreshTimer.Dispose();
        _updateTimer.Stop();
        _updateTimer.Dispose();
        _personalReminderTimer.Stop();
        _personalReminderTimer.Dispose();
        _balloonVisibilityTimer.Stop();
        _balloonVisibilityTimer.Dispose();
        _dismissController.Dispose();
        _notificationForm.Shutdown();
        _notificationForm.Dispose();
        _detailsForm.Shutdown();
        _detailsForm.Dispose();
        _snoozedReminderForm.Shutdown();
        _snoozedReminderForm.Dispose();
        _toolbar.Close();
        _toolbar.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _currentIcon?.Dispose();
        _client.Dispose();
        _updateService.Dispose();
        _menu.Dispose();
        base.ExitThreadCore();
    }
}
