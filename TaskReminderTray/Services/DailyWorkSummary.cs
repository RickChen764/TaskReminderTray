using TaskReminderTray.Models;

namespace TaskReminderTray.Services;

internal sealed record DailyWorkSummary(
    DateOnly Date,
    IssueItem? FocusIssue,
    IReadOnlyList<IssueItem> TodayIssues,
    IReadOnlyList<IssueItem> DueTodayIssues,
    IReadOnlyList<IssueItem> OverdueIssues,
    IReadOnlyList<IssueItem> TomorrowIssues,
    IReadOnlyList<IssueItem> UnscheduledIssues,
    IReadOnlyList<PersistentNotification> RecentStatusChanges)
{
    public static DailyWorkSummary Create(
        IReadOnlyList<IssueItem> issues,
        IReadOnlyList<PersistentNotification> notifications,
        DateOnly date,
        string? focusIssueId)
    {
        var active = issues.Where(issue => !issue.IsCompleted).ToArray();
        var focus = active.FirstOrDefault(issue => string.Equals(issue.Id, focusIssueId,
                        StringComparison.OrdinalIgnoreCase)) ??
                    Order(active.Where(issue => issue.Stage == WorkStage.Development), date)
                        .FirstOrDefault();
        var recentStart = date.AddDays(-1).ToDateTime(TimeOnly.MinValue);

        return new DailyWorkSummary(
            date,
            focus,
            Order(active.Where(issue => IsScheduledOn(issue, date) &&
                                        issue.Stage is WorkStage.Development or WorkStage.FollowUp),
                date),
            Order(active.Where(issue => issue.DueDate == date), date),
            Order(active.Where(issue => issue.DueDate is { } due && due < date), date),
            Order(active.Where(issue => IsScheduledOn(issue, date.AddDays(1)) &&
                                        issue.Stage is WorkStage.Development or WorkStage.FollowUp),
                date.AddDays(1)),
            Order(active.Where(issue => issue.Stage == WorkStage.Development &&
                                        issue.StartDate is null && issue.DueDate is null), date),
            notifications
                .Where(notification => notification.Kind == NotificationKind.StatusChange &&
                                       notification.ChangedAt.ToLocalTime().DateTime >= recentStart)
                .OrderByDescending(notification => notification.ChangedAt)
                .ToArray());
    }

    private static IssueItem[] Order(IEnumerable<IssueItem> issues, DateOnly date) => issues
        .OrderBy(issue => issue.DueDate is { } due && due < date ? 0 : 1)
        .ThenBy(issue => issue.PriorityRank)
        .ThenBy(issue => issue.DueDate ?? DateOnly.MaxValue)
        .ThenBy(issue => issue.Key, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static bool IsScheduledOn(IssueItem issue, DateOnly date)
    {
        var start = issue.StartDate ?? issue.DueDate;
        var end = issue.DueDate ?? issue.StartDate;
        return start is not null && end is not null &&
               start.Value <= date && end.Value >= date;
    }
}
