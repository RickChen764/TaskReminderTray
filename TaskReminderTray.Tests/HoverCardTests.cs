using System.Drawing;
using TaskReminderTray;
using TaskReminderTray.Models;
using UsageTray;
using Xunit;

namespace TaskReminderTray.Tests;

public sealed class HoverCardTests
{
    [Fact]
    public void ScheduleCard_MeasureAndDraw_ProducesVisibleTimeline()
    {
        var today = new DateOnly(2026, 8, 3);
        var issues = new[]
        {
            new IssueItem("1", "OPS-142", "优化任务提醒工具的日历 Hover",
                IssueKind.Task, "进行中", "started", today.AddDays(-1), today.AddDays(2),
                null, "个人效率", false, string.Empty),
            new IssueItem("2", "APP-87", "修复登录态刷新后列表为空",
                IssueKind.Bug, "待修复", "unstarted", today, today,
                null, "工作台", false, string.Empty)
        };
        var summary = ScheduleSummary.Create(
            issues, today, 2);
        var content = HoverCardContent.CreateSchedule(summary, today,
            new DateTime(2026, 8, 3, 17, 0, 0), Color.Orange);

        var size = UsageHoverCardRenderer.Measure(content, 96);
        using var bitmap = new Bitmap(size.Width, size.Height);
        using var graphics = Graphics.FromImage(bitmap);
        UsageHoverCardRenderer.Draw(graphics, new Rectangle(Point.Empty, size), 96, content);

        Assert.Equal(760, size.Width);
        Assert.Equal(700, size.Height);

        Assert.NotEqual(bitmap.GetPixel(size.Width / 2, size.Height / 2), Color.Empty);
        Assert.Contains("任务", content.ToPlainText());
        Assert.Contains("APP-87", content.ToPlainText());
    }

    [Fact]
    public void ScheduleCard_WeekNavigationDefaultsToCurrentAndMapsAllControls()
    {
        var today = new DateOnly(2026, 8, 6);
        var summary = ScheduleSummary.Create([], today, 2);
        var content = HoverCardContent.CreateSchedule(summary, today, DateTime.Now, Color.Green);
        var map = new ScheduleInteractionMap();
        var size = UsageHoverCardRenderer.Measure(content, 96);
        using var bitmap = new Bitmap(size.Width, size.Height);
        using var graphics = Graphics.FromImage(bitmap);

        UsageHoverCardRenderer.Draw(graphics,
            new Rectangle(Point.Empty, size), 96, content, map);

        Assert.Equal(0, content.WeekOffset);
        Assert.Equal(new DateOnly(2026, 8, 3), content.DisplayedWeekStart);
        Assert.Equal(3, map.WeekNavigation.Count);
        Assert.Collection(map.WeekNavigation,
            item => Assert.Equal(ScheduleWeekNavigation.Previous, item.Navigation),
            item => Assert.Equal(ScheduleWeekNavigation.Current, item.Navigation),
            item => Assert.Equal(ScheduleWeekNavigation.Next, item.Navigation));
        Assert.All(map.WeekNavigation, item => Assert.False(item.Bounds.IsEmpty));
        Assert.True(map.WeekNavigation[0].Bounds.Right <= map.WeekNavigation[1].Bounds.Left);
        Assert.True(map.WeekNavigation[1].Bounds.Right <= map.WeekNavigation[2].Bounds.Left);
        Assert.All(map.WeekNavigation, item => Assert.True(item.Bounds.Bottom < 50));
        Assert.False(map.NotificationCenterBounds.IsEmpty);
        Assert.True(map.NotificationCenterBounds.Bottom < 50);
    }

    [Theory]
    [InlineData(-1, 2026, 7, 27, "上周开发安排")]
    [InlineData(0, 2026, 8, 3, "本周开发安排")]
    [InlineData(1, 2026, 8, 10, "下周开发安排")]
    [InlineData(2, 2026, 8, 17, "周开发安排")]
    public void ScheduleCard_WeekOffsetSelectsExpectedWeek(
        int offset, int year, int month, int day, string title)
    {
        var today = new DateOnly(2026, 8, 6);
        var content = HoverCardContent.CreateSchedule(
            ScheduleSummary.Create([], today, 2), today, DateTime.Now, Color.Green);

        content.WeekOffset = offset;

        Assert.Equal(new DateOnly(year, month, day), content.DisplayedWeekStart);
        Assert.Equal(title, content.DisplayedScheduleTitle);
        Assert.Contains(content.DisplayedWeekStart.ToString("M/d"), content.ToPlainText());
    }

    [Fact]
    public void ScheduleCard_PreviousWeekIncludesCompletedScheduledWork()
    {
        var today = new DateOnly(2026, 8, 6);
        var previousMonday = new DateOnly(2026, 7, 27);
        var completed = new IssueItem("done", "SJ-700", "已完成历史工作",
            IssueKind.Task, "已完成", "completed", previousMonday,
            previousMonday.AddDays(2), null, "SJ", true, string.Empty);
        var content = HoverCardContent.CreateSchedule(
            ScheduleSummary.Create([completed], today, 2), today, DateTime.Now, Color.Green);

        Assert.Empty(content.GetDisplayedIssuesForDate(previousMonday));

        content.WeekOffset = -1;

        Assert.Single(content.GetDisplayedIssuesForDate(previousMonday));
        Assert.Contains("SJ-700", content.ToPlainText());
    }

    [Fact]
    public void DayStatus_UsesPlanLabelsInsteadOfRepeatingTotalWorkload()
    {
        var today = new DateOnly(2026, 8, 4);
        var issue = new IssueItem(
            "1", "SJ-790", "招募-程序-功能制作", IssueKind.Task,
            "Head待开发", string.Empty, today.AddDays(1), today.AddDays(3),
            null, "SJ", false, string.Empty, "S", 3m);

        var text = UsageHoverCardRenderer.FormatDayStatus(issue, today.AddDays(1), today);

        Assert.Equal("计划开发", text);
        Assert.DoesNotContain("人日", text);
    }

    [Fact]
    public void Marquee_OnlyMovesOverflowingTextAndPausesAtBothEnds()
    {
        Assert.Equal(0, UsageHoverCardRenderer.CalculateMarqueeOffset(
            contentWidth: 100, viewportWidth: 120, elapsedMilliseconds: 5000));
        Assert.Equal(0, UsageHoverCardRenderer.CalculateMarqueeOffset(
            contentWidth: 220, viewportWidth: 120, elapsedMilliseconds: 1000));

        var midway = UsageHoverCardRenderer.CalculateMarqueeOffset(
            contentWidth: 220, viewportWidth: 120, elapsedMilliseconds: 2400,
            pixelsPerSecond: 100);
        var atEndPause = UsageHoverCardRenderer.CalculateMarqueeOffset(
            contentWidth: 220, viewportWidth: 120, elapsedMilliseconds: 2500,
            pixelsPerSecond: 100);

        Assert.InRange(midway, 99F, 100F);
        Assert.Equal(100F, atEndPause);
    }

    [Theory]
    [InlineData(12F, 9F)]
    [InlineData(16F, 12F)]
    [InlineData(20F, 15F)]
    public void FontPointSize_ConvertsDipWithoutApplyingDpiTwice(
        float dip, float expectedPoints)
    {
        Assert.Equal(expectedPoints, UsageHoverCardRenderer.FontPointSize(dip));
    }

    [Theory]
    [InlineData(96, 760, 700)]
    [InlineData(144, 1140, 1050)]
    [InlineData(192, 1520, 1400)]
    public void ScheduleCard_DpiScalingKeepsLogicalSizeStable(
        int dpi, int expectedWidth, int expectedHeight)
    {
        var today = new DateOnly(2026, 8, 10);
        var content = HoverCardContent.CreateSchedule(
            ScheduleSummary.Create([], today, 2), today, DateTime.Now, Color.Green);

        var size = UsageHoverCardRenderer.Measure(content, dpi);

        Assert.Equal(expectedWidth, size.Width);
        Assert.Equal(expectedHeight, size.Height);
    }

    [Theory]
    [InlineData(96, 640, 560)]
    [InlineData(144, 960, 840)]
    [InlineData(192, 1280, 1120)]
    public void NotificationCenter_DpiScalingKeepsLogicalSizeStable(
        int dpi, int expectedWidth, int expectedHeight)
    {
        Assert.Equal(new Size(expectedWidth, expectedHeight),
            NotificationCenterForm.LogicalSizeForDpi(dpi));
    }

    [Fact]
    public void ScheduleCard_ExpandingDayIncreasesHeightAndMapsEveryIssue()
    {
        var today = new DateOnly(2026, 8, 4);
        var issues = new[]
        {
            new IssueItem("1", "SJ-1", "第一个任务", IssueKind.Task,
                "进行中", "started", today, today, null, "SJ", false,
                "https://plane.example.com/issues/1"),
            new IssueItem("2", "SJ-2", "第二个任务", IssueKind.Task,
                "待开发", "unstarted", today, today, null, "SJ", false,
                "https://plane.example.com/issues/2")
        };
        var summary = ScheduleSummary.Create(issues, today, 2);
        var content = HoverCardContent.CreateSchedule(summary, today, DateTime.Now, Color.Green);
        var collapsed = UsageHoverCardRenderer.Measure(content, 96);
        content.ExpandedDates.Add(today);
        var expanded = UsageHoverCardRenderer.Measure(content, 96);
        var map = new ScheduleInteractionMap();
        using var bitmap = new Bitmap(expanded.Width, expanded.Height);
        using var graphics = Graphics.FromImage(bitmap);

        UsageHoverCardRenderer.Draw(graphics,
            new Rectangle(Point.Empty, expanded), 96, content, map);

        Assert.True(expanded.Height > collapsed.Height);
        Assert.Contains(map.Expanders, region => region.Date == today && region.IsExpanded);
        Assert.Contains(map.Issues, region => region.Issue.Id == "1");
        Assert.Contains(map.Issues, region => region.Issue.Id == "2");
        Assert.All(map.Issues, region => Assert.False(region.OpenBounds.IsEmpty));
        Assert.All(map.Issues, region => Assert.False(region.CopyBounds.IsEmpty));
        Assert.All(map.Issues, region =>
        {
            Assert.True(region.Bounds.Right <= region.StatusBounds.Left);
            Assert.True(region.StatusBounds.Right <= region.OpenBounds.Left);
            Assert.True(region.OpenBounds.Right <= region.CopyBounds.Left);
        });
    }
}
