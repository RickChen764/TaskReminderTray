using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TaskReminderTray.Models;

namespace TaskReminderTray.Services;

internal enum NotificationKind
{
    StatusChange,
    DueToday
}

internal sealed record PersistentNotification(
    string Id,
    string IssueId,
    string IssueKey,
    string Title,
    string PreviousStatus,
    string CurrentStatus,
    DateTimeOffset ChangedAt,
    string SourceUrl,
    DateTimeOffset? ReadAt = null,
    NotificationKind Kind = NotificationKind.StatusChange)
{
    public bool IsRead => ReadAt is not null;

    public static PersistentNotification FromChange(IssueChange change)
    {
        var identity = string.Join("\n", change.IssueId, change.PreviousStatus,
            change.CurrentStatus, change.ChangedAt.ToUniversalTime().Ticks);
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return new PersistentNotification(id, change.IssueId, change.IssueKey,
            change.Title, change.PreviousStatus, change.CurrentStatus,
            change.ChangedAt, change.SourceUrl);
    }

    public static PersistentNotification DueToday(IssueItem issue, DateOnly today,
        DateTimeOffset detectedAt)
    {
        var identity = string.Join("\n", issue.Id, "due-today", today.ToString("yyyy-MM-dd"));
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return new PersistentNotification(id, issue.Id, issue.Key, issue.Title,
            issue.Status, issue.Status, detectedAt, issue.SourceUrl, null,
            NotificationKind.DueToday);
    }
}

internal sealed class NotificationStore
{
    private const int MaximumHistoryCount = 200;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public NotificationStore(string? notificationPath = null)
    {
        NotificationPath = notificationPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TaskReminderTray",
            "pending-notifications.json");
    }

    public string NotificationPath { get; }

    public IReadOnlyList<PersistentNotification> LoadPending()
        => SortPending(LoadStored());

    public IReadOnlyList<PersistentNotification> LoadHistory()
        => LoadStored()
            .OrderByDescending(notification => notification.ChangedAt)
            .ToArray();

    private IReadOnlyList<PersistentNotification> LoadStored()
    {
        if (!File.Exists(NotificationPath))
        {
            return [];
        }

        try
        {
            return (JsonSerializer.Deserialize<List<PersistentNotification>>(
                        File.ReadAllText(NotificationPath), JsonOptions) ?? [])
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    public IReadOnlyList<PersistentNotification> AddChanges(IEnumerable<IssueChange> changes)
        => AddNotifications(changes.Select(PersistentNotification.FromChange));

    public IReadOnlyList<PersistentNotification> AddDueToday(
        IEnumerable<IssueItem> issues,
        DateOnly today,
        DateTimeOffset detectedAt) => AddNotifications(issues
        .Where(issue => !issue.IsCompleted && issue.DueDate == today)
        .Select(issue => PersistentNotification.DueToday(issue, today, detectedAt)));

    private IReadOnlyList<PersistentNotification> AddNotifications(
        IEnumerable<PersistentNotification> notifications)
    {
        var history = LoadStored().ToList();
        var knownIds = history.Select(notification => notification.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var notification in notifications)
        {
            if (knownIds.Add(notification.Id))
            {
                history.Add(notification);
            }
        }

        history = history
            .OrderByDescending(notification => notification.ChangedAt)
            .Take(MaximumHistoryCount)
            .ToList();
        Save(history);
        return SortPending(history);
    }

    public IReadOnlyList<PersistentNotification> Acknowledge(string notificationId)
    {
        var history = LoadStored()
            .Select(notification => string.Equals(notification.Id, notificationId,
                    StringComparison.OrdinalIgnoreCase) && !notification.IsRead
                ? notification with { ReadAt = DateTimeOffset.Now }
                : notification)
            .ToArray();
        Save(history);
        return SortPending(history);
    }

    public IReadOnlyList<PersistentNotification> AcknowledgeAll()
    {
        var readAt = DateTimeOffset.Now;
        var history = LoadStored()
            .Select(notification => notification.IsRead
                ? notification
                : notification with { ReadAt = readAt })
            .ToArray();
        Save(history);
        return [];
    }

    private static PersistentNotification[] SortPending(
        IEnumerable<PersistentNotification> notifications) => notifications
        .Where(notification => !notification.IsRead)
        .OrderBy(notification => notification.Kind == NotificationKind.DueToday ? 0 : 1)
        .ThenBy(notification => notification.ChangedAt)
        .ToArray();

    private void Save(IReadOnlyCollection<PersistentNotification> notifications)
    {
        var directory = Path.GetDirectoryName(NotificationPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = NotificationPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(notifications, JsonOptions));
        File.Move(temporaryPath, NotificationPath, overwrite: true);
    }
}
