using System.Text.Json;
using TaskReminderTray.Models;

namespace TaskReminderTray.Services;

internal sealed record IssueChange(
    string IssueId,
    string IssueKey,
    string Title,
    string PreviousStatus,
    string CurrentStatus,
    DateTimeOffset ChangedAt,
    string SourceUrl);

internal sealed class IssueSnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string SnapshotPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TaskReminderTray",
        "issue-snapshot.json");

    public IReadOnlyList<IssueChange> CompareAndSave(IReadOnlyList<IssueItem> issues)
    {
        var previous = Load();
        var current = issues.ToDictionary(
            issue => issue.Id,
            issue => new SnapshotIssue(issue.Key, issue.Title, issue.Status),
            StringComparer.OrdinalIgnoreCase);
        var changes = DetectChanges(
            previous.ToDictionary(pair => pair.Key, pair => pair.Value.Status,
                StringComparer.OrdinalIgnoreCase), issues);

        var directory = Path.GetDirectoryName(SnapshotPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = SnapshotPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(current, JsonOptions));
        File.Move(temporaryPath, SnapshotPath, overwrite: true);
        return changes;
    }

    internal static IReadOnlyList<IssueChange> DetectChanges(
        IReadOnlyDictionary<string, string> previousStatuses,
        IReadOnlyList<IssueItem> currentIssues) => currentIssues
        .Where(issue => previousStatuses.TryGetValue(issue.Id, out var old) &&
                        !string.Equals(old, issue.Status, StringComparison.OrdinalIgnoreCase))
        .Select(issue => new IssueChange(issue.Id, issue.Key, issue.Title,
            previousStatuses[issue.Id], issue.Status,
            issue.UpdatedAt ?? DateTimeOffset.Now, issue.SourceUrl))
        .ToArray();

    private Dictionary<string, SnapshotIssue> Load()
    {
        if (!File.Exists(SnapshotPath))
        {
            return new Dictionary<string, SnapshotIssue>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, SnapshotIssue>>(
                       File.ReadAllText(SnapshotPath), JsonOptions) ??
                   new Dictionary<string, SnapshotIssue>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, SnapshotIssue>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private sealed record SnapshotIssue(string Key, string Title, string Status);
}

internal sealed class ReminderEvaluator
{
    private readonly Dictionary<string, string> _lastDueReminder =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<IssueItem> GetDueReminders(
        IReadOnlyList<IssueItem> issues,
        DateOnly today,
        int dueSoonDays)
    {
        var limit = today.AddDays(Math.Max(0, dueSoonDays));
        var reminders = new List<IssueItem>();
        foreach (var issue in issues.Where(issue => !issue.IsCompleted && issue.DueDate is not null))
        {
            var due = issue.DueDate!.Value;
            if (due > limit)
            {
                continue;
            }

            var reminderKey = $"{today:yyyy-MM-dd}:{due:yyyy-MM-dd}";
            if (_lastDueReminder.TryGetValue(issue.Id, out var previous) && previous == reminderKey)
            {
                continue;
            }

            _lastDueReminder[issue.Id] = reminderKey;
            reminders.Add(issue);
        }

        return reminders;
    }
}
