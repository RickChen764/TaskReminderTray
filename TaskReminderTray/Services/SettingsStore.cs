using System.Text.Json;
using UsageTray.Services;

namespace TaskReminderTray.Services;

internal enum AuthenticationMode
{
    Password,
    AccessToken
}

internal sealed class AppSettings
{
    public string SourceUrl { get; set; } = string.Empty;
    public AuthenticationMode AuthenticationMode { get; set; } = AuthenticationMode.Password;
    public string UserName { get; set; } = string.Empty;
    public string ProtectedSecret { get; set; } = string.Empty;
    public int RefreshMinutes { get; set; } = 5;
    public int DueSoonDays { get; set; } = 2;
    public bool StartWithWindows { get; set; }

    public bool IsConfigured => Uri.TryCreate(SourceUrl, UriKind.Absolute, out _) &&
        !string.IsNullOrWhiteSpace(ProtectedSecret) &&
        (AuthenticationMode == AuthenticationMode.AccessToken ||
         !string.IsNullOrWhiteSpace(UserName));

    public string GetSecret() => string.IsNullOrWhiteSpace(ProtectedSecret)
        ? string.Empty
        : SecretProtector.Unprotect(ProtectedSecret);

    public void SetSecret(string secret) =>
        ProtectedSecret = string.IsNullOrWhiteSpace(secret)
            ? string.Empty
            : SecretProtector.Protect(secret.Trim());

    public AppSettings Clone() => new()
    {
        SourceUrl = SourceUrl,
        AuthenticationMode = AuthenticationMode,
        UserName = UserName,
        ProtectedSecret = ProtectedSecret,
        RefreshMinutes = RefreshMinutes,
        DueSoonDays = DueSoonDays,
        StartWithWindows = StartWithWindows
    };
}

internal sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string SettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TaskReminderTray",
        "settings.json");

    public AppSettings Load(out string? warning)
    {
        warning = null;
        if (!File.Exists(SettingsPath))
        {
            return new AppSettings();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(
                File.ReadAllText(SettingsPath), JsonOptions) ?? new AppSettings();
            settings.RefreshMinutes = Math.Clamp(settings.RefreshMinutes, 1, 1440);
            settings.DueSoonDays = Math.Clamp(settings.DueSoonDays, 0, 30);
            return settings;
        }
        catch (Exception exception)
        {
            warning = $"无法读取配置：{exception.Message}";
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporaryPath, SettingsPath, overwrite: true);
    }
}
