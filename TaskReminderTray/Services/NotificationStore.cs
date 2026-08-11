using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TaskReminderTray.Services;

internal sealed record PersistentNotification(
    string Id,
    string IssueId,
    string IssueKey,
    string Title,
    string PreviousStatus,
    string CurrentStatus,
    DateTimeOffset ChangedAt,
    string SourceUrl,
    DateTimeOffset? ReadAt = null)
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
        => LoadStored()
            .Where(notification => !notification.IsRead)
            .OrderBy(notification => notification.ChangedAt)
            .ToArray();

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
    {
        var history = LoadStored().ToList();
        var knownIds = history.Select(notification => notification.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var notification in changes.Select(PersistentNotification.FromChange))
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
        return history
            .Where(notification => !notification.IsRead)
            .OrderBy(notification => notification.ChangedAt)
            .ToArray();
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
        return history
            .Where(notification => !notification.IsRead)
            .OrderBy(notification => notification.ChangedAt)
            .ToArray();
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

    private void Save(IReadOnlyCollection<PersistentNotification> notifications)
    {
        var directory = Path.GetDirectoryName(NotificationPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = NotificationPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(notifications, JsonOptions));
        File.Move(temporaryPath, NotificationPath, overwrite: true);
    }
}
