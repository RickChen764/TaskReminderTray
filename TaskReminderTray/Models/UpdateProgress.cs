namespace TaskReminderTray.Models;

internal enum UpdateProgressStage
{
    Preparing,
    Downloading,
    Verifying
}

internal sealed record UpdateProgress(UpdateProgressStage Stage, int? Percentage = null);
