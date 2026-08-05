using TaskReminderTray.Services;
using Xunit;

namespace TaskReminderTray.Tests;

public sealed class SettingsStoreTests
{
    [Theory]
    [InlineData(9, 0, true)]
    [InlineData(17, 29, true)]
    [InlineData(17, 30, false)]
    public void DoNotDisturbEvaluator_UsesStartInclusiveAndEndExclusive(
        int hour, int minute, bool expected)
    {
        var range = new DoNotDisturbRange(new TimeOnly(9, 0), new TimeOnly(17, 30));

        var active = DoNotDisturbEvaluator.IsActive(false, [range], new TimeOnly(hour, minute));

        Assert.Equal(expected, active);
    }

    [Theory]
    [InlineData(22, 0, true)]
    [InlineData(23, 59, true)]
    [InlineData(8, 29, true)]
    [InlineData(8, 30, false)]
    [InlineData(12, 0, false)]
    public void DoNotDisturbEvaluator_SupportsRangesAcrossMidnight(
        int hour, int minute, bool expected)
    {
        var range = new DoNotDisturbRange(new TimeOnly(22, 0), new TimeOnly(8, 30));

        Assert.Equal(expected,
            DoNotDisturbEvaluator.IsActive(false, [range], new TimeOnly(hour, minute)));
    }

    [Fact]
    public void DoNotDisturbEvaluator_MatchesAnyRangeAndManualOverride()
    {
        var ranges = new[]
        {
            new DoNotDisturbRange(new TimeOnly(12, 0), new TimeOnly(13, 0)),
            new DoNotDisturbRange(new TimeOnly(18, 0), new TimeOnly(19, 0))
        };

        Assert.True(DoNotDisturbEvaluator.IsActive(false, ranges, new TimeOnly(18, 30)));
        Assert.True(DoNotDisturbEvaluator.IsActive(true, [], new TimeOnly(10, 0)));
        Assert.False(DoNotDisturbEvaluator.IsActive(false,
            [new DoNotDisturbRange(new TimeOnly(9, 0), new TimeOnly(9, 0))],
            new TimeOnly(9, 0)));
    }

    [Fact]
    public void AppSettings_ClonePreservesDoNotDisturbConfiguration()
    {
        var settings = new AppSettings
        {
            ManualDoNotDisturb = true,
            DoNotDisturbRanges =
            [
                new DoNotDisturbRange(new TimeOnly(22, 0), new TimeOnly(8, 30))
            ]
        };

        var clone = settings.Clone();

        Assert.True(clone.ManualDoNotDisturb);
        Assert.Equal(settings.DoNotDisturbRanges, clone.DoNotDisturbRanges);
        Assert.NotSame(settings.DoNotDisturbRanges, clone.DoNotDisturbRanges);
    }

    [Fact]
    public void SettingsStore_RoundTripsAndCleansDoNotDisturbRanges()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"task-reminder-settings-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");
        try
        {
            var store = new SettingsStore(path);
            store.Save(new AppSettings
            {
                ManualDoNotDisturb = true,
                DoNotDisturbRanges =
                [
                    new DoNotDisturbRange(new TimeOnly(22, 0), new TimeOnly(8, 30)),
                    new DoNotDisturbRange(new TimeOnly(9, 0), new TimeOnly(9, 0)),
                    new DoNotDisturbRange(new TimeOnly(22, 0), new TimeOnly(8, 30))
                ]
            });

            var loaded = store.Load(out var warning);

            Assert.Null(warning);
            Assert.True(loaded.ManualDoNotDisturb);
            Assert.Equal(new DoNotDisturbRange(
                new TimeOnly(22, 0), new TimeOnly(8, 30)),
                Assert.Single(loaded.DoNotDisturbRanges));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
