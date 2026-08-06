namespace TaskReminderTray.Models;

internal enum IssueKind
{
    Task,
    Bug
}

internal enum WorkStage
{
    Development,
    FollowUp,
    Waiting,
    Completed
}

internal sealed record IssueItem(
    string Id,
    string Key,
    string Title,
    IssueKind Kind,
    string Status,
    string StateGroup,
    DateOnly? StartDate,
    DateOnly? DueDate,
    DateTimeOffset? UpdatedAt,
    string Project,
    bool IsCompleted,
    string SourceUrl,
    string Priority = "未确定",
    decimal? Workload = null,
    string? ParentId = null,
    int SubIssueCount = 0)
{
    public WorkStage Stage => WorkStageClassifier.Classify(this);

    public string DisplayTitle => IssueTextFormatter.CompactTitle(Title);

    public int PriorityRank => Priority switch
    {
        "S" => 1,
        "A" => 2,
        "B" => 3,
        "C" => 4,
        _ => 5
    };
}

internal static class IssueTextFormatter
{
    private static readonly string[] LeadingNoise =
    [
        "功能开发", "程序开发", "功能制作", "程序制作", "UI接入"
    ];

    public static string CompactTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "未命名任务";
        }

        var parts = title.Trim()
            .Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 1)
        {
            return Normalize(parts[0]);
        }

        var last = parts[^1];
        foreach (var prefix in LeadingNoise)
        {
            if (last.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                last.Length > prefix.Length)
            {
                last = last[prefix.Length..].Trim();
                break;
            }
        }

        last = Normalize(last);
        if (last.Length >= 6)
        {
            return last;
        }

        var meaningful = parts
            .Where(part => !part.Equals("程序", StringComparison.OrdinalIgnoreCase) &&
                           !part.Equals("8月版本", StringComparison.OrdinalIgnoreCase) &&
                           !part.Equals("子级", StringComparison.OrdinalIgnoreCase))
            .Select(Normalize)
            .ToArray();
        return meaningful.Length == 0 ? Normalize(title) : string.Join(" · ", meaningful);
    }

    private static string Normalize(string value)
    {
        var result = value.Trim();
        if (result.StartsWith("ui", StringComparison.OrdinalIgnoreCase))
        {
            result = "UI" + result[2..];
        }

        return result;
    }
}

internal static class WorkStageClassifier
{
    public static WorkStage Classify(IssueItem issue)
    {
        if (issue.IsCompleted)
        {
            return WorkStage.Completed;
        }

        var status = issue.Status;
        if (ContainsAny(status,
                "待需求", "需求待", "Bug待确定", "Bug待排期", "需求待评审"))
        {
            return WorkStage.Waiting;
        }

        if (ContainsAny(status,
                "待测试", "待验收", "待策划验收", "待性能验收",
                "申请提交权限", "待制作人确认"))
        {
            return WorkStage.FollowUp;
        }

        return WorkStage.Development;
    }

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Contains(candidate,
            StringComparison.OrdinalIgnoreCase));
}

internal sealed class ScheduleSummary
{
    public required IReadOnlyList<IssueItem> Issues { get; init; }
    public required IReadOnlyList<IssueItem> AllIssues { get; init; }
    public int TaskCount { get; init; }
    public int BugCount { get; init; }
    public int DueSoonCount { get; init; }
    public int OverdueCount { get; init; }
    public int UnscheduledCount { get; init; }
    public int DevelopmentCount { get; init; }
    public int FollowUpCount { get; init; }
    public int WaitingCount { get; init; }
    public int UnscheduledDevelopmentCount { get; init; }

    public required IReadOnlyList<IssueItem> DevelopmentIssues { get; init; }
    public required IReadOnlyList<IssueItem> FollowUpIssues { get; init; }
    public required IReadOnlyList<IssueItem> WaitingIssues { get; init; }

    public IssueItem? CurrentFocus => DevelopmentIssues.FirstOrDefault();

    public IReadOnlyList<IssueItem> GetIssuesForDate(
        DateOnly date,
        bool includeCompleted = false) => (includeCompleted ? AllIssues : Issues)
        .Where(issue => (issue.Stage is WorkStage.Development or WorkStage.FollowUp ||
                         includeCompleted && issue.Stage == WorkStage.Completed) &&
                        IsScheduledOn(issue, date))
        .OrderBy(issue => issue.Stage == WorkStage.Development ? 0 : 1)
        .ThenBy(issue => issue.PriorityRank)
        .ThenBy(issue => issue.DueDate ?? DateOnly.MaxValue)
        .ToArray();

    public IssueItem? GetNextDevelopmentAfter(DateOnly date) => DevelopmentIssues
        .Where(issue => (issue.StartDate ?? issue.DueDate) is { } scheduled && scheduled > date)
        .OrderBy(issue => issue.StartDate ?? issue.DueDate)
        .ThenBy(issue => issue.PriorityRank)
        .FirstOrDefault();

    public static DateOnly StartOfWeek(DateOnly date)
    {
        var daysSinceMonday = ((int)date.DayOfWeek + 6) % 7;
        return date.AddDays(-daysSinceMonday);
    }

    public int TotalCount => TaskCount + BugCount;

    public static ScheduleSummary Create(
        IEnumerable<IssueItem> issues,
        DateOnly today,
        int dueSoonDays)
    {
        var all = issues.ToArray();
        var active = all.Where(issue => !issue.IsCompleted).ToArray();
        var dueLimit = today.AddDays(Math.Max(0, dueSoonDays));
        var development = active
            .Where(issue => issue.Stage == WorkStage.Development)
            .OrderBy(issue => DevelopmentOrder(issue, today))
            .ThenBy(issue => issue.DueDate ?? DateOnly.MaxValue)
            .ThenBy(issue => issue.PriorityRank)
            .ThenBy(issue => issue.SubIssueCount > 0 ? 1 : 0)
            .ThenByDescending(issue => issue.UpdatedAt)
            .ToArray();
        var followUp = active
            .Where(issue => issue.Stage == WorkStage.FollowUp)
            .OrderBy(issue => issue.DueDate ?? DateOnly.MaxValue)
            .ThenBy(issue => issue.PriorityRank)
            .ThenByDescending(issue => issue.UpdatedAt)
            .ToArray();
        var waiting = active
            .Where(issue => issue.Stage == WorkStage.Waiting)
            .OrderBy(issue => issue.PriorityRank)
            .ThenBy(issue => issue.DueDate ?? DateOnly.MaxValue)
            .ToArray();
        return new ScheduleSummary
        {
            Issues = active,
            AllIssues = all,
            DevelopmentIssues = development,
            FollowUpIssues = followUp,
            WaitingIssues = waiting,
            TaskCount = active.Count(issue => issue.Kind == IssueKind.Task),
            BugCount = active.Count(issue => issue.Kind == IssueKind.Bug),
            OverdueCount = active.Count(issue => issue.DueDate is { } due && due < today),
            DueSoonCount = active.Count(issue =>
                issue.DueDate is { } due && due >= today && due <= dueLimit),
            UnscheduledCount = active.Count(issue => issue.StartDate is null && issue.DueDate is null),
            DevelopmentCount = development.Length,
            FollowUpCount = followUp.Length,
            WaitingCount = waiting.Length,
            UnscheduledDevelopmentCount = development.Count(issue =>
                issue.StartDate is null && issue.DueDate is null)
        };
    }

    private static int DevelopmentOrder(IssueItem issue, DateOnly today)
    {
        if (issue.DueDate is { } due && due <= today)
        {
            return 0;
        }

        if (issue.StartDate is { } start && start <= today)
        {
            return 1;
        }

        if (issue.StartDate is not null || issue.DueDate is not null)
        {
            return 2;
        }

        return 3;
    }

    private static bool IsScheduledOn(IssueItem issue, DateOnly date)
    {
        var start = issue.StartDate ?? issue.DueDate;
        var end = issue.DueDate ?? issue.StartDate;
        return start is not null && end is not null &&
               start.Value <= date && end.Value >= date;
    }
}
