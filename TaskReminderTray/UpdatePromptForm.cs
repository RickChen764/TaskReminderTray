using System.Diagnostics;
using TaskReminderTray.Models;
using TaskReminderTray.Services;

namespace TaskReminderTray;

internal sealed class UpdatePromptForm : Form
{
    private readonly RichTextBox _notesTextBox = new();

    public UpdatePromptForm(UpdateRelease release)
    {
        Text = "TaskReminderTray 更新";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(700, 475);
        MinimumSize = new Size(620, 430);
        Font = new Font("Microsoft YaHei UI", 9F);

        BuildInterface(release);
    }

    private void BuildInterface(UpdateRelease release)
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24, 22, 24, 18),
            ColumnCount = 2,
            RowCount = 6
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 54));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var icon = new PictureBox
        {
            Image = SystemIcons.Information.ToBitmap(),
            SizeMode = PictureBoxSizeMode.CenterImage,
            Size = new Size(46, 46),
            Margin = new Padding(0, 0, 8, 8)
        };
        root.Controls.Add(icon, 0, 0);

        var heading = new Label
        {
            AutoSize = true,
            Text = $"发现新版本 v{release.Version}",
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 5, 0, 3)
        };
        root.Controls.Add(heading, 1, 0);

        var metadataParts = new List<string> { $"当前版本 v{UpdateService.CurrentVersion.ToString(3)}" };
        if (release.ExecutableSize is > 0)
        {
            metadataParts.Add($"下载大小 {FormatFileSize(release.ExecutableSize.Value)}");
        }

        var metadata = new Label
        {
            AutoSize = true,
            Text = string.Join("  ·  ", metadataParts),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 16)
        };
        root.Controls.Add(metadata, 1, 1);

        var notesHeader = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Margin = new Padding(0, 0, 0, 7)
        };
        notesHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        notesHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var notesLabel = new Label
        {
            AutoSize = true,
            Text = "更新内容",
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0)
        };
        var githubLink = new LinkLabel
        {
            AutoSize = true,
            Text = "GitHub 完整日志 ↗",
            Margin = new Padding(12, 0, 0, 0),
            TabStop = true
        };
        githubLink.LinkClicked += (_, _) => OpenGitHubChangelog();
        notesHeader.Controls.Add(notesLabel, 0, 0);
        notesHeader.Controls.Add(githubLink, 1, 0);
        root.Controls.Add(notesHeader, 1, 2);

        _notesTextBox.Dock = DockStyle.Fill;
        _notesTextBox.ReadOnly = true;
        _notesTextBox.DetectUrls = false;
        _notesTextBox.WordWrap = true;
        _notesTextBox.ScrollBars = RichTextBoxScrollBars.Vertical;
        _notesTextBox.BackColor = SystemColors.Window;
        _notesTextBox.BorderStyle = BorderStyle.FixedSingle;
        _notesTextBox.Text = FormatReleaseNotes(release.Notes);
        _notesTextBox.Margin = new Padding(0, 0, 0, 12);
        root.Controls.Add(_notesTextBox, 1, 3);

        var restartHint = new Label
        {
            AutoSize = true,
            Text = "下载并校验完成后，TaskReminderTray 会自动重启。",
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 14),
        };
        root.Controls.Add(restartHint, 1, 4);

        var actionPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = new Padding(0, 4, 0, 0)
        };
        var updateButton = new Button
        {
            Text = "立即更新",
            Size = new Size(112, 36),
            DialogResult = DialogResult.Yes,
            Margin = new Padding(8, 0, 0, 0)
        };
        var laterButton = new Button
        {
            Text = "稍后再说",
            Size = new Size(112, 36),
            DialogResult = DialogResult.No,
            Margin = new Padding(8, 0, 0, 0)
        };
        actionPanel.Controls.Add(updateButton);
        actionPanel.Controls.Add(laterButton);
        root.Controls.Add(actionPanel, 1, 5);

        AcceptButton = updateButton;
        CancelButton = laterButton;
        Controls.Add(root);

        Shown += (_, _) =>
        {
            _notesTextBox.SelectionStart = 0;
            _notesTextBox.SelectionLength = 0;
            updateButton.Focus();
        };
    }

    private static string FormatReleaseNotes(string notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return "该版本未提供更新说明。";
        }

        return System.Text.RegularExpressions.Regex.Replace(notes,
                @"(?m)^\s{0,3}#{1,6}\s+", string.Empty)
            .Replace("**", string.Empty, StringComparison.Ordinal)
            .Replace("`", string.Empty, StringComparison.Ordinal)
            .Trim();
    }

    private static string FormatFileSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):0.##} GB",
        >= 1024L * 1024 => $"{bytes / (1024d * 1024):0.##} MB",
        >= 1024L => $"{bytes / 1024d:0.##} KB",
        _ => $"{bytes} B"
    };

    private void OpenGitHubChangelog()
    {
        try
        {
            Process.Start(new ProcessStartInfo(UpdateService.ReleasesPage.AbsoluteUri)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"无法打开 GitHub。\n\n{ex.Message}",
                "TaskReminderTray 更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
