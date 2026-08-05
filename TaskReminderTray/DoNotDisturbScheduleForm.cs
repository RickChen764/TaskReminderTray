using TaskReminderTray.Services;

namespace TaskReminderTray;

internal sealed class DoNotDisturbScheduleForm : Form
{
    private readonly ListView _rangesList = new();
    private readonly DateTimePicker _startTime = CreateTimePicker();
    private readonly DateTimePicker _endTime = CreateTimePicker();
    private readonly Button _addButton = new();
    private readonly List<DoNotDisturbRange> _ranges;

    public IReadOnlyList<DoNotDisturbRange> Result { get; private set; }

    public DoNotDisturbScheduleForm(IEnumerable<DoNotDisturbRange> ranges)
    {
        _ranges = ranges.Distinct().OrderBy(range => range.Start).ToList();
        Result = [.. _ranges];
        Text = "免打扰时间段";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(540, 420);
        Font = new Font("Microsoft YaHei UI", 9F);
        BuildInterface();
        RefreshRanges();
    }

    private void BuildInterface()
    {
        var heading = new Label
        {
            Text = "每日重复时间段",
            Font = new Font(Font, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(24, 21)
        };
        var hint = new Label
        {
            Text = "可添加多段时间；结束时间早于开始时间时按跨午夜处理。",
            ForeColor = SystemColors.GrayText,
            AutoSize = true,
            Location = new Point(24, 49)
        };

        _rangesList.View = View.Details;
        _rangesList.FullRowSelect = true;
        _rangesList.HideSelection = false;
        _rangesList.MultiSelect = false;
        _rangesList.Columns.Add("开始", 105);
        _rangesList.Columns.Add("结束", 135);
        _rangesList.Columns.Add("说明", 225);
        _rangesList.SetBounds(24, 82, 492, 205);
        _rangesList.SelectedIndexChanged += (_, _) => LoadSelectedRange();
        _rangesList.DoubleClick += (_, _) => LoadSelectedRange();

        var editor = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };
        editor.SetBounds(24, 303, 492, 38);
        editor.Controls.Add(new Label
        {
            Text = "开始",
            AutoSize = true,
            Margin = new Padding(0, 8, 6, 0)
        });
        editor.Controls.Add(_startTime);
        editor.Controls.Add(new Label
        {
            Text = "结束",
            AutoSize = true,
            Margin = new Padding(14, 8, 6, 0)
        });
        editor.Controls.Add(_endTime);
        _addButton.Text = "添加";
        _addButton.Size = new Size(74, 30);
        _addButton.Margin = new Padding(14, 1, 0, 0);
        _addButton.Click += (_, _) => AddOrUpdateRange();
        editor.Controls.Add(_addButton);

        var deleteButton = new Button
        {
            Text = "删除选中",
            Size = new Size(96, 32),
            Location = new Point(24, 359)
        };
        deleteButton.Click += (_, _) => DeleteSelectedRange();
        var cancelButton = new Button
        {
            Text = "取消",
            Size = new Size(88, 32),
            Location = new Point(332, 359),
            DialogResult = DialogResult.Cancel
        };
        var saveButton = new Button
        {
            Text = "保存",
            Size = new Size(88, 32),
            Location = new Point(428, 359)
        };
        saveButton.Click += (_, _) =>
        {
            Result = [.. _ranges.OrderBy(range => range.Start)];
            DialogResult = DialogResult.OK;
            Close();
        };

        AcceptButton = saveButton;
        CancelButton = cancelButton;
        Controls.AddRange([heading, hint, _rangesList, editor,
            deleteButton, cancelButton, saveButton]);
    }

    private void AddOrUpdateRange()
    {
        var range = new DoNotDisturbRange(
            TimeOnly.FromDateTime(_startTime.Value),
            TimeOnly.FromDateTime(_endTime.Value));
        if (range.Start == range.End)
        {
            MessageBox.Show(this, "开始和结束时间不能相同。", "免打扰时间段",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (_rangesList.SelectedIndices.Count > 0)
        {
            _ranges[_rangesList.SelectedIndices[0]] = range;
        }
        else if (!_ranges.Contains(range))
        {
            _ranges.Add(range);
        }

        _ranges.Sort((left, right) => left.Start.CompareTo(right.Start));
        RefreshRanges();
    }

    private void DeleteSelectedRange()
    {
        if (_rangesList.SelectedIndices.Count == 0)
        {
            return;
        }

        _ranges.RemoveAt(_rangesList.SelectedIndices[0]);
        RefreshRanges();
    }

    private void LoadSelectedRange()
    {
        if (_rangesList.SelectedIndices.Count == 0)
        {
            _addButton.Text = "添加";
            return;
        }

        var range = _ranges[_rangesList.SelectedIndices[0]];
        _startTime.Value = DateTime.Today.Add(range.Start.ToTimeSpan());
        _endTime.Value = DateTime.Today.Add(range.End.ToTimeSpan());
        _addButton.Text = "更新";
    }

    private void RefreshRanges()
    {
        _rangesList.BeginUpdate();
        _rangesList.Items.Clear();
        foreach (var range in _ranges)
        {
            _rangesList.Items.Add(new ListViewItem([
                range.Start.ToString("HH:mm"),
                range.CrossesMidnight ? $"次日 {range.End:HH:mm}" : range.End.ToString("HH:mm"),
                range.CrossesMidnight ? "跨午夜" : "当日"
            ]));
        }
        _rangesList.EndUpdate();
        _addButton.Text = "添加";
    }

    private static DateTimePicker CreateTimePicker() => new()
    {
        Format = DateTimePickerFormat.Custom,
        CustomFormat = "HH:mm",
        ShowUpDown = true,
        Width = 92,
        Value = DateTime.Today.AddHours(12)
    };
}
