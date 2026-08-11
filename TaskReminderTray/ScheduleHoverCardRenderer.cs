using System.Drawing.Drawing2D;
using System.Drawing.Text;
using TaskReminderTray.Models;

namespace UsageTray;

internal sealed class HoverCardContent
{
    private long _animationStartedAt;
    private long _lastAnimationFrameAt;

    public string Title { get; init; } = "任务提醒";
    public string Message { get; init; } = string.Empty;
    public Color AccentColor { get; init; } = Color.FromArgb(69, 139, 226);
    public ScheduleSummary? Summary { get; init; }
    public DateOnly Today { get; init; } = DateOnly.FromDateTime(DateTime.Now);
    public DateTime UpdatedAt { get; init; } = DateTime.Now;
    public HashSet<DateOnly> ExpandedDates { get; } = [];
    public string? FocusIssueId { get; set; }
    public int WeekOffset { get; set; }
    public ScheduleWeekNavigation? HoveredWeekNavigation { get; set; }
    public int UnreadNotificationCount { get; set; }
    public bool NotificationCenterHovered { get; set; }

    public DateOnly DisplayedWeekStart =>
        ScheduleSummary.StartOfWeek(Today).AddDays(WeekOffset * 7);

    public string DisplayedScheduleTitle => WeekOffset switch
    {
        0 => "本周开发安排",
        -1 => "上周开发安排",
        1 => "下周开发安排",
        _ => "周开发安排"
    };

    public IReadOnlyList<IssueItem> GetDisplayedIssuesForDate(DateOnly date) =>
        Summary?.GetIssuesForDate(date, includeCompleted: WeekOffset < 0) ?? [];

    public IssueItem? FocusIssue => Summary?.Issues.FirstOrDefault(issue =>
        !issue.IsCompleted && string.Equals(issue.Id, FocusIssueId,
            StringComparison.OrdinalIgnoreCase)) ?? Summary?.CurrentFocus;

    public static HoverCardContent CreateStatus(
        string title,
        string message,
        Color accentColor,
        string? footer = null) => new()
        {
            Title = title,
            Message = string.IsNullOrWhiteSpace(footer) ? message : $"{message}\n{footer}",
            AccentColor = accentColor
        };

    public static HoverCardContent CreateSchedule(
        ScheduleSummary summary,
        DateOnly today,
        DateTime updatedAt,
        Color accentColor) => new()
        {
            Title = "本周开发安排",
            Message = "按天查看当前开发工作",
            Summary = summary,
            Today = today,
            UpdatedAt = updatedAt,
            AccentColor = accentColor
        };

    public string ToPlainText()
    {
        if (Summary is null)
        {
            return $"{Title}\n{Message}";
        }

        var weekStart = DisplayedWeekStart;
        var lines = new List<string>
        {
            $"{DisplayedScheduleTitle} {weekStart:M/d}–{weekStart.AddDays(6):M/d}"
        };
        for (var index = 0; index < 7; index++)
        {
            var date = weekStart.AddDays(index);
            var issues = GetDisplayedIssuesForDate(date);
            lines.Add(issues.Count == 0
                ? $"{Weekday(date)} {date:M/d}：暂无安排"
                : $"{Weekday(date)} {date:M/d}：" + string.Join("；",
                    issues.Select(issue => $"{issue.Key} {issue.DisplayTitle}")));
        }

        if (Summary.GetNextDevelopmentAfter(weekStart.AddDays(6)) is { } next)
        {
            lines.Add($"接下来：{next.Key} {next.DisplayTitle}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    internal long GetAnimationElapsedMilliseconds()
    {
        var now = Environment.TickCount64;
        if (_animationStartedAt == 0 || now - _lastAnimationFrameAt > 300)
        {
            _animationStartedAt = now;
        }

        _lastAnimationFrameAt = now;
        return Math.Max(0, now - _animationStartedAt);
    }

    private static string Weekday(DateOnly date) =>
        new[] { "周日", "周一", "周二", "周三", "周四", "周五", "周六" }[(int)date.DayOfWeek];
}

internal sealed record ScheduleIssueRegion(
    Rectangle Bounds,
    Rectangle StatusBounds,
    Rectangle OpenBounds,
    Rectangle CopyBounds,
    IssueItem Issue);

internal sealed record ScheduleExpandRegion(
    Rectangle Bounds,
    DateOnly Date,
    bool IsExpanded);

internal enum ScheduleWeekNavigation
{
    Previous,
    Current,
    Next
}

internal sealed record ScheduleWeekNavigationRegion(
    Rectangle Bounds,
    ScheduleWeekNavigation Navigation);

internal sealed class ScheduleInteractionMap
{
    public List<ScheduleIssueRegion> Issues { get; } = [];
    public List<ScheduleExpandRegion> Expanders { get; } = [];
    public List<ScheduleWeekNavigationRegion> WeekNavigation { get; } = [];
    public Rectangle NotificationCenterBounds { get; set; }

    public void Clear()
    {
        Issues.Clear();
        Expanders.Clear();
        WeekNavigation.Clear();
        NotificationCenterBounds = Rectangle.Empty;
        FocusIssue = null;
    }

    public ScheduleIssueRegion? FocusIssue { get; set; }
}

internal static class UsageHoverCardRenderer
{
    private const int LogicalWidth = 760;
    private const int StatusWidth = 560;
    private const int Padding = 16;
    private const int DayRowHeight = 40;
    private const int ExpandedIssueRowHeight = 28;

    private static readonly Color Background = Color.FromArgb(24, 27, 33);
    private static readonly Color Surface = Color.FromArgb(31, 35, 43);
    private static readonly Color SurfaceAlt = Color.FromArgb(35, 41, 51);
    private static readonly Color Border = Color.FromArgb(54, 60, 71);
    private static readonly Color PrimaryText = Color.FromArgb(244, 246, 250);
    private static readonly Color SecondaryText = Color.FromArgb(184, 194, 210);
    private static readonly Color MutedText = Color.FromArgb(126, 139, 158);
    private static readonly Color Blue = Color.FromArgb(88, 142, 238);
    private static readonly Color Green = Color.FromArgb(57, 199, 127);
    private static readonly Color Orange = Color.FromArgb(244, 171, 68);
    private static readonly Color Red = Color.FromArgb(235, 83, 96);
    private static readonly Color Purple = Color.FromArgb(170, 116, 232);

    public static Size Measure(HoverCardContent content, int dpi)
    {
        if (content.Summary is null)
        {
            var lines = Math.Max(1, content.Message.Split('\n').Length);
            return new Size(Scale(StatusWidth, dpi), Scale(124 + lines * 20, dpi));
        }

        var expandedExtra = 0;
        foreach (var date in content.ExpandedDates.Where(date =>
                     date >= content.DisplayedWeekStart &&
                     date <= content.DisplayedWeekStart.AddDays(6)))
        {
            var count = content.GetDisplayedIssuesForDate(date).Count;
            if (count > 1)
            {
                expandedExtra += 8 + (count - 1) * ExpandedIssueRowHeight;
            }
        }

        // 标题栏 + 当前重点 + 周列表 + 接下来 + 弱化摘要。
        return new Size(Scale(LogicalWidth, dpi), Scale(700 + expandedExtra, dpi));
    }

    public static void Draw(
        Graphics graphics,
        Rectangle bounds,
        int dpi,
        HoverCardContent content,
        ScheduleInteractionMap? interactions = null)
    {
        interactions?.Clear();
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        graphics.Clear(Background);
        using var border = new Pen(Border, ScaleF(1, dpi));
        graphics.DrawRectangle(border, bounds.Left, bounds.Top,
            Math.Max(0, bounds.Width - 1), Math.Max(0, bounds.Height - 1));
        if (content.Summary is null)
        {
            DrawStatus(graphics, bounds, dpi, content);
            return;
        }

        DrawWeek(graphics, bounds, dpi, content, interactions);
    }

    internal static Bitmap RenderPreview(
        HoverCardContent content,
        int dpi,
        ScheduleInteractionMap? interactions = null)
    {
        var size = Measure(content, dpi);
        var bitmap = new Bitmap(size.Width, size.Height);
        bitmap.SetResolution(dpi, dpi);
        using var graphics = Graphics.FromImage(bitmap);
        Draw(graphics, new Rectangle(Point.Empty, size), dpi, content, interactions);
        return bitmap;
    }

    private static void DrawStatus(
        Graphics graphics,
        Rectangle bounds,
        int dpi,
        HoverCardContent content)
    {
        var padding = Scale(18, dpi);
        using var titleFont = Font(12F, FontStyle.Bold, dpi);
        using var messageFont = Font(9F, FontStyle.Regular, dpi);
        using var titleBrush = new SolidBrush(PrimaryText);
        using var messageBrush = new SolidBrush(SecondaryText);
        using var accentBrush = new SolidBrush(content.AccentColor);
        graphics.FillRectangle(accentBrush, bounds.Left, bounds.Top, Scale(4, dpi), bounds.Height);
        graphics.DrawString(content.Title, titleFont, titleBrush,
            bounds.Left + padding, bounds.Top + padding);
        graphics.DrawString(content.Message, messageFont, messageBrush,
            new RectangleF(bounds.Left + padding, bounds.Top + Scale(52, dpi),
                bounds.Width - padding * 2, bounds.Height - Scale(68, dpi)));
    }

    private static void DrawWeek(
        Graphics graphics,
        Rectangle bounds,
        int dpi,
        HoverCardContent content,
        ScheduleInteractionMap? interactions)
    {
        var summary = content.Summary!;
        var animationMilliseconds = content.GetAnimationElapsedMilliseconds();
        var padding = Scale(Padding, dpi);
        var x = bounds.Left + padding;
        var width = bounds.Width - padding * 2;
        var y = bounds.Top + Scale(12, dpi);
        var weekStart = content.DisplayedWeekStart;
        var weekEnd = weekStart.AddDays(6);

        using var headingFont = Font(18F, FontStyle.Bold, dpi);
        using var sectionFont = Font(13F, FontStyle.Regular, dpi);
        using var dayFont = Font(11F, FontStyle.Bold, dpi);
        using var dateFont = Font(10F, FontStyle.Regular, dpi);
        using var taskFont = Font(12.5F, FontStyle.Regular, dpi);
        using var taskBoldFont = Font(13F, FontStyle.Bold, dpi);
        using var focusTitleFont = Font(16F, FontStyle.Bold, dpi);
        using var focusMetaFont = Font(12F, FontStyle.Regular, dpi);
        using var smallFont = Font(11F, FontStyle.Regular, dpi);
        using var primaryBrush = new SolidBrush(PrimaryText);
        using var secondaryBrush = new SolidBrush(SecondaryText);
        using var mutedBrush = new SolidBrush(MutedText);

        DrawHeader(graphics, x, width, y, dpi, content, weekStart, weekEnd,
            headingFont, taskFont, smallFont, primaryBrush, secondaryBrush, mutedBrush,
            interactions);
        y += Scale(52, dpi);

        var focus = content.FocusIssue;
        DrawSectionLabel(graphics, "当前重点", x, y, dpi, sectionFont, mutedBrush);
        y += Scale(20, dpi);
        y += Scale(6, dpi);
        var focusBounds = new Rectangle(x, y, width, Scale(84, dpi));
        DrawFocusCard(graphics, focusBounds, dpi, focus, content, focusTitleFont,
            focusMetaFont, smallFont, interactions);
        y += focusBounds.Height + Scale(6, dpi);

        DrawSectionLabel(graphics, "本周工作安排", x, y, dpi, sectionFont, mutedBrush);
        y += Scale(20, dpi);
        y += Scale(6, dpi);
        var listTop = y;
        var listExtra = 0;
        for (var index = 0; index < 7; index++)
        {
            var date = weekStart.AddDays(index);
            var count = content.GetDisplayedIssuesForDate(date).Count;
            if (count > 1 && content.ExpandedDates.Contains(date))
            {
                listExtra += 8 + (count - 1) * ExpandedIssueRowHeight;
            }
        }
        var listHeight = Scale(7 * DayRowHeight + listExtra, dpi);
        using var listPath = RoundedRectangle(new Rectangle(x, y, width,
            listHeight), Scale(9, dpi));
        using var listBrush = new SolidBrush(Color.FromArgb(29, 33, 40));
        graphics.FillPath(listBrush, listPath);
        using var listPen = new Pen(Border, ScaleF(1, dpi));
        graphics.DrawPath(listPen, listPath);
        for (var dayIndex = 0; dayIndex < 7; dayIndex++)
        {
            var date = weekStart.AddDays(dayIndex);
            var issues = content.GetDisplayedIssuesForDate(date);
            var expanded = issues.Count > 1 && content.ExpandedDates.Contains(date);
            var isToday = date == content.Today;
            var logicalHeight = expanded
                ? DayRowHeight + 8 + (issues.Count - 1) * ExpandedIssueRowHeight
                : DayRowHeight;
            var row = new Rectangle(x, y, width, Scale(logicalHeight, dpi));
            DrawDayRow(graphics, row, dpi, date, issues, isToday,
                dayFont, dateFont, taskFont, taskBoldFont, smallFont,
                animationMilliseconds, expanded, content, interactions);
            y += Scale(logicalHeight, dpi);
        }

        y = listTop + listHeight + Scale(8, dpi);
        using var nextLabelBrush = new SolidBrush(Blue);
        DrawSectionLabel(graphics, "接下来", x, y, dpi, sectionFont, nextLabelBrush);
        var next = summary.GetNextDevelopmentAfter(weekEnd);
        var nextDate = next?.StartDate ?? next?.DueDate;
        using (var right = new StringFormat { Alignment = StringAlignment.Far })
        {
            graphics.DrawString(nextDate?.ToString("M/d") ?? string.Empty, smallFont, mutedBrush,
                new RectangleF(x, y, width, Scale(22, dpi)), right);
        }
        y += Scale(20, dpi);
        y += Scale(6, dpi);
        DrawNext(graphics, new Rectangle(x, y, width, Scale(62, dpi)), dpi,
            next, taskFont, taskBoldFont, smallFont, animationMilliseconds,
            interactions);
        y += Scale(76, dpi);

        var overview = new Rectangle(x, y, width, Scale(34, dpi));
        FillRoundedRectangle(graphics, overview, Scale(7, dpi), Surface);
        var overviewText = $"待跟进 {summary.FollowUpCount}   ·   " +
                           $"等待输入 {summary.WaitingCount}   ·   Bug {summary.BugCount}";
        using (var centered = new StringFormat
               {
                   Alignment = StringAlignment.Center,
                   LineAlignment = StringAlignment.Center
               })
        {
            using var overviewFont = Font(12F, FontStyle.Regular, dpi);
            graphics.DrawString(overviewText, overviewFont, mutedBrush, overview, centered);
        }
    }

    private static void DrawHeader(Graphics graphics, int x, int width, int y, int dpi,
        HoverCardContent content, DateOnly weekStart, DateOnly weekEnd,
        Font headingFont, Font taskFont, Font smallFont, Brush primaryBrush,
        Brush secondaryBrush, Brush mutedBrush, ScheduleInteractionMap? interactions)
    {
        var dateWidth = Scale(116, dpi);
        var navigationWidth = Scale(152, dpi);
        var navigationBounds = new Rectangle(x + (width - navigationWidth) / 2,
            y, navigationWidth, Scale(32, dpi));
        var previousBounds = new Rectangle(navigationBounds.Left, navigationBounds.Top,
            Scale(40, dpi), navigationBounds.Height);
        var currentBounds = new Rectangle(previousBounds.Right, navigationBounds.Top,
            Scale(72, dpi), navigationBounds.Height);
        var nextBounds = new Rectangle(currentBounds.Right, navigationBounds.Top,
            Scale(40, dpi), navigationBounds.Height);
        graphics.DrawString("开发安排", headingFont, primaryBrush,
            new RectangleF(x, y + Scale(2, dpi),
                navigationBounds.Left - x - Scale(18, dpi), Scale(29, dpi)));
        using var right = new StringFormat { Alignment = StringAlignment.Far };
        graphics.DrawString($"{weekStart:M/d} – {weekEnd:M/d}", taskFont, secondaryBrush,
            new RectangleF(x + width - dateWidth, y, dateWidth, Scale(21, dpi)), right);
        graphics.DrawString($"{content.UpdatedAt:HH:mm} 更新", smallFont, mutedBrush,
            new RectangleF(x + width - dateWidth, y + Scale(20, dpi), dateWidth,
                Scale(16, dpi)), right);
        var notificationBounds = new Rectangle(
            x + width - dateWidth - Scale(94, dpi), y,
            Scale(80, dpi), Scale(32, dpi));
        DrawNotificationCenterButton(graphics, notificationBounds, dpi, content,
            smallFont);
        if (interactions is not null)
        {
            interactions.NotificationCenterBounds = notificationBounds;
        }
        DrawWeekNavigationControl(graphics, navigationBounds, previousBounds,
            currentBounds, nextBounds, dpi, content);
        interactions?.WeekNavigation.Add(new ScheduleWeekNavigationRegion(
            previousBounds, ScheduleWeekNavigation.Previous));
        interactions?.WeekNavigation.Add(new ScheduleWeekNavigationRegion(
            currentBounds, ScheduleWeekNavigation.Current));
        interactions?.WeekNavigation.Add(new ScheduleWeekNavigationRegion(
            nextBounds, ScheduleWeekNavigation.Next));
    }

    private static void DrawNotificationCenterButton(
        Graphics graphics,
        Rectangle bounds,
        int dpi,
        HoverCardContent content,
        Font font)
    {
        var fill = content.NotificationCenterHovered
            ? Color.FromArgb(42, 48, 58)
            : Color.FromArgb(28, 32, 39);
        FillRoundedRectangle(graphics, bounds, Scale(6, dpi), fill);
        using var path = RoundedRectangle(bounds, Scale(6, dpi));
        using var pen = new Pen(content.NotificationCenterHovered
            ? Color.FromArgb(79, 91, 110)
            : Border, ScaleF(1, dpi));
        graphics.DrawPath(pen, path);

        var unread = Math.Min(99, Math.Max(0, content.UnreadNotificationCount));
        var text = unread > 0 ? $"通知  {unread}" : "通知";
        using var brush = new SolidBrush(unread > 0 ? PrimaryText : SecondaryText);
        using var centered = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap
        };
        graphics.DrawString(text, font, brush, bounds, centered);
        if (unread > 0)
        {
            using var dot = new SolidBrush(Orange);
            graphics.FillEllipse(dot, bounds.Right - Scale(10, dpi),
                bounds.Top + Scale(5, dpi), Scale(5, dpi), Scale(5, dpi));
        }
    }

    private static void DrawSectionLabel(Graphics graphics, string text, int x, int y,
        int dpi, Font font, Brush brush) => graphics.DrawString(text, font, brush,
        new RectangleF(x, y, Scale(240, dpi), Scale(21, dpi)));

    private static void DrawFocusCard(Graphics graphics, Rectangle bounds, int dpi,
        IssueItem? issue, HoverCardContent content, Font titleFont, Font metaFont,
        Font smallFont, ScheduleInteractionMap? interactions)
    {
        FillRoundedRectangle(graphics, bounds, Scale(10, dpi), Color.FromArgb(32, 38, 46));
        using var path = RoundedRectangle(bounds, Scale(10, dpi));
        using var borderPen = new Pen(Color.FromArgb(150, Green), ScaleF(1, dpi));
        graphics.DrawPath(borderPen, path);
        using var accent = new SolidBrush(Green);
        graphics.FillRectangle(accent, bounds.Left, bounds.Top, Scale(4, dpi), bounds.Height);
        if (issue is null)
        {
            using var empty = new SolidBrush(MutedText);
            graphics.DrawString("暂无当前重点", metaFont, empty,
                bounds.Left + Scale(16, dpi), bounds.Top + Scale(31, dpi));
            return;
        }

        var buttonWidth = Scale(28, dpi);
        var copyBounds = new Rectangle(bounds.Right - Scale(16, dpi) - buttonWidth,
            bounds.Top + Scale(29, dpi), buttonWidth, buttonWidth);
        var openBounds = new Rectangle(copyBounds.Left - Scale(8, dpi) - buttonWidth,
            copyBounds.Top, buttonWidth, buttonWidth);
        var stateBounds = new Rectangle(openBounds.Left - Scale(16, dpi) - Scale(96, dpi),
            bounds.Top + Scale(30, dpi), Scale(96, dpi), Scale(24, dpi));
        var titleBounds = new RectangleF(bounds.Left + Scale(16, dpi),
            bounds.Top + Scale(14, dpi),
            Math.Max(40, stateBounds.Left - bounds.Left - Scale(24, dpi)), Scale(25, dpi));
        using var titleBrush = new SolidBrush(PrimaryText);
        DrawIssueTitle(graphics, issue, titleFont, titleBrush, titleBounds, dpi,
            content.GetAnimationElapsedMilliseconds());
        var meta = $"{ShortStatus(issue.Status)}  ·  优先级 {issue.Priority}  ·  预计 {(issue.DueDate ?? issue.StartDate)?.ToString("M/d") ?? "未排期"}";
        using var metaBrush = new SolidBrush(Color.FromArgb(145, 156, 175));
        graphics.DrawString(meta, metaFont, metaBrush,
            bounds.Left + Scale(16, dpi), bounds.Top + Scale(52, dpi));
        using var stateBrush = new SolidBrush(Green);
        using var stateFormat = new StringFormat { Alignment = StringAlignment.Far,
            LineAlignment = StringAlignment.Center };
        graphics.DrawString("今日重点", smallFont, stateBrush, stateBounds, stateFormat);
        if (interactions is not null)
        {
            var region = new ScheduleIssueRegion(Rectangle.Round(titleBounds), stateBounds,
                openBounds, copyBounds, issue);
            interactions.Issues.Add(region);
            interactions.FocusIssue = region;
        }
    }

    private static void DrawWeekNavigationControl(
        Graphics graphics,
        Rectangle bounds,
        Rectangle previousBounds,
        Rectangle currentBounds,
        Rectangle nextBounds,
        int dpi,
        HoverCardContent content)
    {
        var radius = Scale(6, dpi);
        FillRoundedRectangle(graphics, bounds, radius, Color.FromArgb(28, 32, 39));
        using var groupPath = RoundedRectangle(bounds, radius);
        var saved = graphics.Save();
        graphics.SetClip(groupPath);
        if (content.WeekOffset == 0)
        {
            using var selectedBrush = new SolidBrush(Color.FromArgb(43, 68, 101));
            graphics.FillRectangle(selectedBrush, currentBounds);
        }

        var hoveredBounds = content.HoveredWeekNavigation switch
        {
            ScheduleWeekNavigation.Previous => previousBounds,
            ScheduleWeekNavigation.Current => currentBounds,
            ScheduleWeekNavigation.Next => nextBounds,
            _ => Rectangle.Empty
        };
        if (!hoveredBounds.IsEmpty)
        {
            using var hoverBrush = new SolidBrush(Color.FromArgb(48, 55, 67));
            graphics.FillRectangle(hoverBrush, hoveredBounds);
        }
        graphics.Restore(saved);

        using var borderPen = new Pen(content.HoveredWeekNavigation is null
            ? Border
            : Color.FromArgb(79, 91, 110), ScaleF(1, dpi));
        graphics.DrawPath(borderPen, groupPath);
        using var separatorPen = new Pen(Border, ScaleF(1, dpi));
        graphics.DrawLine(separatorPen, previousBounds.Right, bounds.Top + Scale(5, dpi),
            previousBounds.Right, bounds.Bottom - Scale(5, dpi));
        graphics.DrawLine(separatorPen, currentBounds.Right, bounds.Top + Scale(5, dpi),
            currentBounds.Right, bounds.Bottom - Scale(5, dpi));

        using var arrowFont = Font(13F, FontStyle.Regular, dpi);
        using var labelFont = Font(11F,
            content.WeekOffset == 0 ? FontStyle.Bold : FontStyle.Regular, dpi);
        using var arrowBrush = new SolidBrush(content.HoveredWeekNavigation is
            ScheduleWeekNavigation.Previous or ScheduleWeekNavigation.Next
            ? PrimaryText
            : SecondaryText);
        using var labelBrush = new SolidBrush(content.WeekOffset == 0 ||
                                              content.HoveredWeekNavigation == ScheduleWeekNavigation.Current
            ? PrimaryText
            : SecondaryText);
        using var centered = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap
        };
        graphics.DrawString("‹", arrowFont, arrowBrush, previousBounds, centered);
        graphics.DrawString("本周", labelFont, labelBrush, currentBounds, centered);
        graphics.DrawString("›", arrowFont, arrowBrush, nextBounds, centered);
    }

    private static void DrawDayRow(
        Graphics graphics,
        Rectangle bounds,
        int dpi,
        DateOnly date,
        IReadOnlyList<IssueItem> issues,
        bool isToday,
        Font dayFont,
        Font dateFont,
        Font taskFont,
        Font taskBoldFont,
        Font smallFont,
        long animationMilliseconds,
        bool expanded,
        HoverCardContent content,
        ScheduleInteractionMap? interactions)
    {
        if (isToday)
        {
            using var todayBrush = new SolidBrush(SurfaceAlt);
            graphics.FillRectangle(todayBrush, bounds);
            using var accentBrush = new SolidBrush(Green);
            graphics.FillRectangle(accentBrush, bounds.Left, bounds.Top,
                Scale(3, dpi), bounds.Height);
        }
        else if (issues.Count == 0)
        {
            using var emptyBrush = new SolidBrush(Color.FromArgb(27, 31, 38));
            graphics.FillRectangle(emptyBrush, bounds);
        }
        using var separatorPen = new Pen(Color.FromArgb(48, 54, 65), ScaleF(1, dpi));
        graphics.DrawLine(separatorPen, bounds.Left, bounds.Bottom - 1,
            bounds.Right, bounds.Bottom - 1);

        var labelWidth = Scale(68, dpi);
        var metaWidth = Scale(88, dpi);
        var actionsWidth = Scale(68, dpi);
        using var dayBrush = new SolidBrush(isToday ? Green : SecondaryText);
        using var dateBrush = new SolidBrush(isToday ? Green : MutedText);
        var weekday = new[] { "周日", "周一", "周二", "周三", "周四", "周五", "周六" }[(int)date.DayOfWeek];
        graphics.DrawString(weekday, dayFont, dayBrush,
            bounds.Left + Scale(16, dpi), bounds.Top + Scale(7, dpi));
        graphics.DrawString(date.ToString("M/d"), dateFont, dateBrush,
            bounds.Left + Scale(16, dpi), bounds.Top + Scale(24, dpi));

        if (issues.Count == 0)
        {
            using var mutedBrush = new SolidBrush(MutedText);
            graphics.DrawString("暂无安排", taskFont, mutedBrush,
                bounds.Left + labelWidth + Scale(20, dpi), bounds.Top + Scale(14, dpi));
            return;
        }

        var orderedIssues = issues
            .OrderByDescending(issue => string.Equals(issue.Id, content.FocusIssueId,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var visibleIssues = expanded ? orderedIssues : orderedIssues.Take(1).ToArray();
        for (var index = 0; index < visibleIssues.Length; index++)
        {
            var issue = visibleIssues[index];
            var focused = string.Equals(issue.Id, content.FocusIssueId,
                StringComparison.OrdinalIgnoreCase);
            var rowTop = expanded
                ? bounds.Top + Scale(5 + index * ExpandedIssueRowHeight, dpi)
                : bounds.Top + Scale(10, dpi);
            using var priorityBrush = new SolidBrush(
                string.Equals(issue.Id, content.FocusIssueId, StringComparison.OrdinalIgnoreCase) ||
                isToday ? Green : TaskStatusColor(issue));
            using var titleBrush = new SolidBrush(focused || isToday ? PrimaryText : SecondaryText);
            using var metaBrush = new SolidBrush(issue.Stage == WorkStage.FollowUp ? Orange : MutedText);
            graphics.FillEllipse(priorityBrush, bounds.Left + labelWidth + Scale(9, dpi),
                rowTop + Scale(9, dpi), Scale(8, dpi), Scale(8, dpi));
            var textLeft = bounds.Left + labelWidth + Scale(28, dpi);
            var textWidth = Math.Max(20,
                bounds.Width - labelWidth - metaWidth - actionsWidth - Scale(48, dpi));
            var titleBounds = new Rectangle(textLeft, rowTop,
                textWidth, Scale(21, dpi));
            DrawIssueTitle(graphics, issue, focused || isToday ? taskBoldFont : taskFont,
                titleBrush, titleBounds, dpi, animationMilliseconds);

            var buttonWidth = Scale(28, dpi);
            var copyBounds = new Rectangle(bounds.Right - buttonWidth - Scale(10, dpi),
                rowTop - Scale(2, dpi), buttonWidth, buttonWidth);
            var openBounds = new Rectangle(copyBounds.Left - buttonWidth - Scale(8, dpi),
                copyBounds.Top, buttonWidth, copyBounds.Height);

            var meta = focused
                ? "当前重点"
                : FormatDayStatus(issue, date, DateOnly.FromDateTime(DateTime.Now));
            if (!expanded && orderedIssues.Length > 1)
            {
                meta += $"  +{orderedIssues.Length - 1}";
            }
            else if (expanded && index == 0 && orderedIssues.Length > 1)
            {
                meta += "  收起";
            }
            using var right = new StringFormat
            {
                Alignment = StringAlignment.Far,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            };
            var metaBounds = new Rectangle(openBounds.Left - Scale(12, dpi) - metaWidth,
                rowTop + Scale(3, dpi), metaWidth, Scale(22, dpi));
            graphics.DrawString(meta, smallFont, metaBrush, metaBounds, right);
            interactions?.Issues.Add(new ScheduleIssueRegion(titleBounds, metaBounds,
                openBounds, copyBounds, issue));
            if (index == 0 && orderedIssues.Length > 1)
            {
                interactions?.Expanders.Add(new ScheduleExpandRegion(metaBounds, date, expanded));
            }
        }
    }

    private static void DrawNext(
        Graphics graphics,
        Rectangle bounds,
        int dpi,
        IssueItem? issue,
        Font taskFont,
        Font taskBoldFont,
        Font smallFont,
        long animationMilliseconds,
        ScheduleInteractionMap? interactions)
    {
        FillRoundedRectangle(graphics, bounds, Scale(10, dpi), Color.FromArgb(31, 36, 44));
        using var cardPath = RoundedRectangle(bounds, Scale(10, dpi));
        using var cardPen = new Pen(Border, ScaleF(1, dpi));
        graphics.DrawPath(cardPen, cardPath);
        using var primaryBrush = new SolidBrush(PrimaryText);
        using var secondaryBrush = new SolidBrush(SecondaryText);
        using var mutedBrush = new SolidBrush(MutedText);
        if (issue is null)
        {
            graphics.DrawString("暂无后续开发任务", taskFont, mutedBrush,
                bounds.Left + Scale(16, dpi), bounds.Top + Scale(21, dpi));
            return;
        }

        using var right = new StringFormat { Alignment = StringAlignment.Far };
        var buttonWidth = Scale(28, dpi);
        var copyBounds = new Rectangle(bounds.Right - buttonWidth - Scale(14, dpi),
            bounds.Top + Scale(18, dpi), buttonWidth, buttonWidth);
        var openBounds = new Rectangle(copyBounds.Left - buttonWidth - Scale(8, dpi),
            copyBounds.Top, buttonWidth, copyBounds.Height);
        var statusRight = openBounds.Left - Scale(14, dpi);
        var statusWidth = Scale(96, dpi);
        var statusBounds = new Rectangle(statusRight - statusWidth,
            bounds.Top + Scale(19, dpi), statusWidth, Scale(24, dpi));
        var titleBounds = new RectangleF(bounds.Left + Scale(16, dpi),
            bounds.Top + Scale(18, dpi),
            Math.Max(20, statusBounds.Left - bounds.Left - Scale(28, dpi)),
            Scale(26, dpi));
        DrawIssueTitle(graphics, issue, taskBoldFont, primaryBrush,
            titleBounds, dpi, animationMilliseconds);
        interactions?.Issues.Add(new ScheduleIssueRegion(
            Rectangle.Round(titleBounds), statusBounds, openBounds, copyBounds, issue));
        var metadata = $"{issue.Priority} · {ShortStatus(issue.Status)}";
        graphics.DrawString(metadata, smallFont, secondaryBrush, statusBounds, right);
    }

    internal static string FormatDayStatus(IssueItem issue, DateOnly date, DateOnly today)
    {
        if (issue.Stage == WorkStage.FollowUp)
        {
            return ShortStatus(issue.Status);
        }

        if (date == today)
        {
            return "今日重点";
        }

        return date > today ? "计划开发" : ShortStatus(issue.Status);
    }

    private static void DrawIssueTitle(
        Graphics graphics,
        IssueItem issue,
        Font font,
        Brush brush,
        RectangleF bounds,
        int dpi,
        long animationMilliseconds)
    {
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.None
        };
        var keyText = issue.Key;
        var keyWidth = Math.Min(bounds.Width,
            graphics.MeasureString(keyText, font, int.MaxValue, format).Width);
        graphics.DrawString(keyText, font, brush,
            new PointF(bounds.Left, bounds.Top), format);

        var titleBounds = new RectangleF(bounds.Left + keyWidth + Scale(12, dpi), bounds.Top,
            Math.Max(0, bounds.Width - keyWidth - Scale(12, dpi)), bounds.Height);
        if (titleBounds.Width < Scale(16, dpi))
        {
            return;
        }

        var title = issue.DisplayTitle;
        var titleWidth = graphics.MeasureString(title, font, int.MaxValue, format).Width;
        var offset = CalculateMarqueeOffset(titleWidth, titleBounds.Width,
            animationMilliseconds, ScaleF(34F, dpi));
        var state = graphics.Save();
        graphics.SetClip(titleBounds);
        graphics.DrawString(title, font, brush,
            new PointF(titleBounds.Left - offset, titleBounds.Top), format);
        graphics.Restore(state);
    }

    internal static float CalculateMarqueeOffset(
        float contentWidth,
        float viewportWidth,
        long elapsedMilliseconds,
        float pixelsPerSecond = 34F)
    {
        var overflow = Math.Max(0, contentWidth - viewportWidth);
        if (overflow <= 0 || pixelsPerSecond <= 0)
        {
            return 0;
        }

        const double startPauseMilliseconds = 1400;
        const double endPauseMilliseconds = 1100;
        var travelMilliseconds = overflow / pixelsPerSecond * 1000D;
        var cycleMilliseconds = startPauseMilliseconds + travelMilliseconds +
                                endPauseMilliseconds + travelMilliseconds;
        var phase = Math.Max(0, elapsedMilliseconds) % cycleMilliseconds;
        if (phase < startPauseMilliseconds)
        {
            return 0;
        }

        phase -= startPauseMilliseconds;
        if (phase < travelMilliseconds)
        {
            return (float)(overflow * phase / travelMilliseconds);
        }

        phase -= travelMilliseconds;
        if (phase < endPauseMilliseconds)
        {
            return overflow;
        }

        phase -= endPauseMilliseconds;
        return (float)(overflow * (1D - phase / travelMilliseconds));
    }

    internal static string ShortStatus(string status)
    {
        if (status.Contains("待策划验收", StringComparison.OrdinalIgnoreCase))
        {
            return "待策划验收";
        }

        if (status.Contains("待性能验收", StringComparison.OrdinalIgnoreCase))
        {
            return "待性能验收";
        }

        if (status.Contains("待验收", StringComparison.OrdinalIgnoreCase))
        {
            return "待验收";
        }

        if (status.Contains("待测试", StringComparison.OrdinalIgnoreCase))
        {
            return "待测试";
        }

        if (status.Contains("待开发", StringComparison.OrdinalIgnoreCase))
        {
            return "待开发";
        }

        if (status.Contains("测试完成", StringComparison.OrdinalIgnoreCase))
        {
            return "测试完成";
        }

        return status.Length <= 8 ? status : status[..8] + "…";
    }

    private static Color TaskStatusColor(IssueItem issue) => issue.Stage switch
    {
        WorkStage.FollowUp => Orange,
        WorkStage.Waiting => Purple,
        _ when issue.Kind == IssueKind.Bug => Red,
        _ => Blue
    };

    private static void FillRoundedRectangle(
        Graphics graphics,
        Rectangle rectangle,
        int radius,
        Color color)
    {
        using var path = RoundedRectangle(rectangle, radius);
        using var brush = new SolidBrush(color);
        graphics.FillPath(brush, path);
    }

    private static GraphicsPath RoundedRectangle(Rectangle rectangle, int radius)
    {
        var safeRadius = Math.Max(1, Math.Min(radius,
            Math.Min(rectangle.Width, rectangle.Height) / 2));
        var diameter = safeRadius * 2;
        var path = new GraphicsPath();
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    internal static float FontPointSize(float dip) => dip * 72F / 96F;

    private static Font Font(float size, FontStyle style, int dpi)
    {
        // size 使用 DIP（1/96 英寸），而 Font 的 Point 单位已经会由 GDI
        // 按当前 Graphics.DpiX 换算成物理像素。这里不能再乘 dpi/96，
        // 否则在 150%/200% 缩放下会发生二次放大并挤出固定列。
        return new Font("Microsoft YaHei UI", FontPointSize(size), style,
            GraphicsUnit.Point);
    }

    private static int Scale(int value, int dpi) =>
        (int)Math.Round(value * dpi / 96F, MidpointRounding.AwayFromZero);

    private static float ScaleF(float value, int dpi) => value * dpi / 96F;
}
