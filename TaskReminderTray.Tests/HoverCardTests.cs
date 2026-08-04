using System.Drawing;
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

        Assert.Equal(540, size.Width);
        Assert.Equal(424, size.Height);
        Assert.NotEqual(bitmap.GetPixel(size.Width / 2, size.Height / 2), Color.Empty);
        Assert.Contains("任务", content.ToPlainText());
        Assert.Contains("APP-87", content.ToPlainText());
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
}
