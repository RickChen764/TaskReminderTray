namespace TaskReminderTray.Models;

internal sealed record UpdateRelease(
    Version Version,
    string Tag,
    string Name,
    string Notes,
    Uri PageUrl,
    Uri ExecutableUrl,
    Uri ChecksumUrl,
    long? ExecutableSize);
