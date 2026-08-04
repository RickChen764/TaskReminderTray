using System.Text.Json;
using TaskReminderTray.Models;

namespace TaskReminderTray.Services;

internal sealed record SnoozedWorkReminder(
    string Id,
    string IssueId,
    string IssueKey,
    string Title,
    string SourceUrl,
    DateTimeOffset RemindAt);

internal sealed record PersonalWorkState(
    string? FocusIssueId,
    IReadOnlyList<SnoozedWorkReminder> Reminders)
{
    public static PersonalWorkState Empty { get; } = new(null, []);
}

internal sealed class PersonalWorkStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public PersonalWorkStore(string? statePath = null)
    {
        StatePath = statePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TaskReminderTray", "personal-work.json");
    }

    public string StatePath { get; }

    public PersonalWorkState Load()
    {
        if (!File.Exists(StatePath))
        {
            return PersonalWorkState.Empty;
        }

        try
        {
            var state = JsonSerializer.Deserialize<PersonalWorkState>(
                File.ReadAllText(StatePath), JsonOptions);
            return state is null
                ? PersonalWorkState.Empty
                : state with { Reminders = state.Reminders ?? [] };
        }
        catch
        {
            return PersonalWorkState.Empty;
        }
    }

    public PersonalWorkState SetFocus(string? issueId)
    {
        var state = Load() with
        {
            FocusIssueId = string.IsNullOrWhiteSpace(issueId) ? null : issueId
        };
        Save(state);
        return state;
    }

    public PersonalWorkState AddReminder(IssueItem issue, DateTimeOffset remindAt)
    {
        var state = Load();
        var reminders = state.Reminders
            .Where(reminder => !string.Equals(reminder.IssueId, issue.Id,
                StringComparison.OrdinalIgnoreCase))
            .Append(new SnoozedWorkReminder(Guid.NewGuid().ToString("N"), issue.Id,
                issue.Key, issue.Title, issue.SourceUrl, remindAt))
            .OrderBy(reminder => reminder.RemindAt)
            .ToArray();
        state = state with { Reminders = reminders };
        Save(state);
        return state;
    }

    public PersonalWorkState RemoveReminder(string reminderId)
    {
        var current = Load();
        var state = current with
        {
            Reminders = current.Reminders
                .Where(reminder => !string.Equals(reminder.Id, reminderId,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray()
        };
        Save(state);
        return state;
    }

    public PersonalWorkState RescheduleReminder(string reminderId, DateTimeOffset remindAt)
    {
        var state = Load();
        state = state with
        {
            Reminders = state.Reminders.Select(reminder =>
                    string.Equals(reminder.Id, reminderId, StringComparison.OrdinalIgnoreCase)
                        ? reminder with { RemindAt = remindAt }
                        : reminder)
                .OrderBy(reminder => reminder.RemindAt)
                .ToArray()
        };
        Save(state);
        return state;
    }

    internal static string FormatIssueInformation(IssueItem issue)
    {
        var lines = new List<string>
        {
            $"{issue.Key} {issue.Title}",
            $"类型：{(issue.Kind == IssueKind.Bug ? "Bug" : "任务")}",
            $"状态：{issue.Status}",
            $"优先级：{issue.Priority}"
        };
        if (issue.StartDate is not null || issue.DueDate is not null)
        {
            lines.Add($"排期：{issue.StartDate?.ToString("yyyy-MM-dd") ?? "未设置"} ～ " +
                      $"{issue.DueDate?.ToString("yyyy-MM-dd") ?? "未设置"}");
        }
        if (!string.IsNullOrWhiteSpace(issue.SourceUrl))
        {
            lines.Add(issue.SourceUrl);
        }
        return string.Join(Environment.NewLine, lines);
    }

    private void Save(PersonalWorkState state)
    {
        var directory = Path.GetDirectoryName(StatePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = StatePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temporaryPath, StatePath, overwrite: true);
    }
}
