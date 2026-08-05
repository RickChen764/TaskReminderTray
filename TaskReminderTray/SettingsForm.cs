using TaskReminderTray.Services;

namespace TaskReminderTray;

internal sealed class SettingsForm : Form
{
    private readonly TextBox _sourceUrl = new();
    private readonly ComboBox _authenticationMode = new();
    private readonly TextBox _userName = new();
    private readonly TextBox _secret = new();
    private readonly CheckBox _showSecret = new();
    private readonly NumericUpDown _refreshMinutes = new();
    private readonly NumericUpDown _dueSoonDays = new();
    private readonly CheckBox _startWithWindows = new();
    private readonly Button _testButton = new();
    private readonly Button _saveButton = new();
    private string _existingSecret = string.Empty;

    public AppSettings Result { get; private set; }

    public SettingsForm(AppSettings settings)
    {
        Result = settings;
        Text = "TaskReminderTray 设置";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(680, 545);
        Font = new Font("Microsoft YaHei UI", 9F);
        BuildInterface();
        LoadSettings(settings);
    }

    private void BuildInterface()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24, 20, 24, 16),
            ColumnCount = 2,
            RowCount = 10
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var index = 0; index < 9; index++)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        AddLabel(layout, "任务视图地址", 0);
        _sourceUrl.Dock = DockStyle.Fill;
        _sourceUrl.PlaceholderText = "http://服务器/jx/workspace-views/my-all-issues";
        _sourceUrl.Margin = new Padding(3, 1, 3, 10);
        layout.Controls.Add(_sourceUrl, 1, 0);

        AddLabel(layout, "认证方式", 1);
        _authenticationMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _authenticationMode.Items.AddRange(["账号密码", "Access Token"]);
        _authenticationMode.Width = 180;
        _authenticationMode.SelectedIndexChanged += (_, _) => UpdateAuthenticationFields();
        layout.Controls.Add(_authenticationMode, 1, 1);

        AddLabel(layout, "账号", 2);
        _userName.Dock = DockStyle.Fill;
        _userName.PlaceholderText = "邮箱或登录账号";
        _userName.Margin = new Padding(3, 1, 3, 10);
        layout.Controls.Add(_userName, 1, 2);

        AddLabel(layout, "密码 / Token", 3);
        _secret.Dock = DockStyle.Fill;
        _secret.UseSystemPasswordChar = true;
        _secret.Margin = new Padding(3, 1, 3, 4);
        layout.Controls.Add(_secret, 1, 3);

        _showSecret.Text = "显示密码 / Token";
        _showSecret.AutoSize = true;
        _showSecret.Margin = new Padding(3, 0, 3, 10);
        _showSecret.CheckedChanged += (_, _) =>
            _secret.UseSystemPasswordChar = !_showSecret.Checked;
        layout.Controls.Add(_showSecret, 1, 4);

        AddLabel(layout, "刷新间隔", 5);
        layout.Controls.Add(NumberPanel(_refreshMinutes, "分钟", 1, 1440), 1, 5);

        AddLabel(layout, "临期阈值", 6);
        layout.Controls.Add(NumberPanel(_dueSoonDays, "天内", 0, 30), 1, 6);

        _startWithWindows.Text = "登录 Windows 后自动启动";
        _startWithWindows.AutoSize = true;
        _startWithWindows.Margin = new Padding(3, 4, 3, 10);
        layout.Controls.Add(_startWithWindows, 1, 7);

        var tip = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(490, 0),
            ForeColor = SystemColors.GrayText,
            Text = "账号密码或 Token 会使用 Windows DPAPI 加密，仅当前 Windows 用户可解密。",
            Margin = new Padding(3, 2, 3, 10)
        };
        layout.Controls.Add(tip, 1, 8);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 10, 0, 0)
        };
        _saveButton.Text = "保存";
        _saveButton.Size = new Size(96, 36);
        _saveButton.Click += SaveButton_Click;
        var cancelButton = new Button
        {
            Text = "取消",
            Size = new Size(96, 36),
            DialogResult = DialogResult.Cancel
        };
        _testButton.Text = "测试连接";
        _testButton.Size = new Size(110, 36);
        _testButton.Click += TestButton_Click;
        buttons.Controls.Add(_saveButton);
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(_testButton);
        layout.SetColumnSpan(buttons, 2);
        layout.Controls.Add(buttons, 0, 9);

        AcceptButton = _saveButton;
        CancelButton = cancelButton;
        Controls.Add(layout);
    }

    private void LoadSettings(AppSettings settings)
    {
        _sourceUrl.Text = settings.SourceUrl;
        _authenticationMode.SelectedIndex =
            settings.AuthenticationMode == AuthenticationMode.Password ? 0 : 1;
        _userName.Text = settings.UserName;
        _refreshMinutes.Value = Math.Clamp(settings.RefreshMinutes, 1, 1440);
        _dueSoonDays.Value = Math.Clamp(settings.DueSoonDays, 0, 30);
        _startWithWindows.Checked = settings.StartWithWindows;
        try
        {
            _existingSecret = settings.GetSecret();
            _secret.Text = _existingSecret;
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"已保存的密码或 Token 无法解密，请重新输入。\n\n{exception.Message}",
                "任务提醒", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        UpdateAuthenticationFields();
    }

    private AppSettings? BuildSettings(bool showErrors)
    {
        var settings = new AppSettings
        {
            SourceUrl = _sourceUrl.Text.Trim().TrimEnd('/'),
            AuthenticationMode = _authenticationMode.SelectedIndex == 1
                ? AuthenticationMode.AccessToken
                : AuthenticationMode.Password,
            UserName = _userName.Text.Trim(),
            RefreshMinutes = (int)_refreshMinutes.Value,
            DueSoonDays = (int)_dueSoonDays.Value,
            StartWithWindows = _startWithWindows.Checked,
            ManualDoNotDisturb = Result.ManualDoNotDisturb,
            DoNotDisturbRanges = [.. Result.DoNotDisturbRanges]
        };
        try
        {
            _ = PlaneIssueClient.ParseSourceUrl(settings.SourceUrl);
        }
        catch (Exception exception)
        {
            if (showErrors)
            {
                MessageBox.Show(this, exception.Message, "配置有误",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return null;
        }

        if (settings.AuthenticationMode == AuthenticationMode.Password &&
            string.IsNullOrWhiteSpace(settings.UserName))
        {
            if (showErrors)
            {
                MessageBox.Show(this, "请输入登录账号。", "配置有误",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return null;
        }

        if (string.IsNullOrWhiteSpace(_secret.Text))
        {
            if (showErrors)
            {
                MessageBox.Show(this, "请输入密码或 Access Token。", "配置有误",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return null;
        }

        settings.SetSecret(_secret.Text);
        return settings;
    }

    private async void TestButton_Click(object? sender, EventArgs e)
    {
        var settings = BuildSettings(showErrors: true);
        if (settings is null)
        {
            return;
        }

        SetBusy(true);
        try
        {
            using var client = new PlaneIssueClient();
            var count = await client.TestConnectionAsync(settings);
            MessageBox.Show(this, $"连接成功，读取到 {count} 个工作项。", "任务提醒",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "测试失败",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SaveButton_Click(object? sender, EventArgs e)
    {
        try
        {
            var settings = BuildSettings(showErrors: true);
            if (settings is null)
            {
                return;
            }

            Result = settings;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"无法加密保存配置：{exception.Message}", "保存失败",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UpdateAuthenticationFields()
    {
        var passwordMode = _authenticationMode.SelectedIndex != 1;
        _sourceUrl.Enabled = true;
        _authenticationMode.Enabled = true;
        _userName.Enabled = passwordMode;
        _secret.Enabled = true;
        _showSecret.Enabled = true;
    }

    private void SetBusy(bool busy)
    {
        UseWaitCursor = busy;
        _testButton.Enabled = !busy;
        _saveButton.Enabled = !busy;
    }

    private static Control NumberPanel(
        NumericUpDown input,
        string unit,
        int minimum,
        int maximum)
    {
        input.Minimum = minimum;
        input.Maximum = maximum;
        input.Width = 112;
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 8)
        };
        panel.Controls.Add(input);
        panel.Controls.Add(new Label
        {
            Text = unit,
            AutoSize = true,
            Margin = new Padding(6, 5, 0, 0)
        });
        return panel;
    }

    private static void AddLabel(TableLayoutPanel layout, string text, int row)
    {
        layout.Controls.Add(new Label
        {
            Text = text,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 4, 3, 8)
        }, 0, row);
    }
}
