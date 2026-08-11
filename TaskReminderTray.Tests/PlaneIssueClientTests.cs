using System.Net;
using System.Text;
using TaskReminderTray.Models;
using TaskReminderTray.Services;
using Xunit;

namespace TaskReminderTray.Tests;

public sealed class PlaneIssueClientTests
{
    [Fact]
    public async Task PasswordAuthentication_CachesTokenAcrossRefreshes()
    {
        var handler = new AuthenticationHandler();
        using var client = new PlaneIssueClient(handler);
        var settings = PasswordSettings();

        await client.GetIssuesAsync(settings);
        await client.GetIssuesAsync(settings);

        Assert.Equal(1, handler.SignInCount);
        Assert.Equal(2, handler.UserRequestCount);
        Assert.All(handler.BearerTokens, token => Assert.Equal("token-1", token));
    }

    [Fact]
    public async Task PasswordAuthentication_RenewsRejectedCachedTokenOnce()
    {
        var handler = new AuthenticationHandler { RejectSecondUserRequest = true };
        using var client = new PlaneIssueClient(handler);
        var settings = PasswordSettings();

        await client.GetIssuesAsync(settings);
        await client.GetIssuesAsync(settings);

        Assert.Equal(2, handler.SignInCount);
        Assert.Equal(3, handler.UserRequestCount);
        Assert.Contains("token-2", handler.BearerTokens);
    }

    [Fact]
    public void ParseSourceUrl_MapsWorkspaceViewPageToIssuesApi()
    {
        var source = PlaneIssueClient.ParseSourceUrl(
            "https://plane.example.com/jx/workspace-views/my-all-issues");

        var endpoint = PlaneIssueClient.BuildIssuesEndpoint(source, "user-42");

        Assert.Equal("jx", source.WorkspaceSlug);
        Assert.Equal("my-all-issues", source.ViewId);
        Assert.Equal("/api/workspaces/jx/issues/", endpoint.AbsolutePath);
        Assert.Contains("viewId=my-all-issues", endpoint.Query);
        Assert.Contains("association__assignees=user-42", endpoint.Query);
        Assert.Contains("per_page=500", endpoint.Query);
    }

    [Fact]
    public void ParseIssues_ResolvesStateIdThroughWorkspaceStateDictionary()
    {
        const string json = """
        { "results": [{ "id": "a", "name": "任务", "state_id": "state-1" }] }
        """;
        var source = PlaneIssueClient.ParseSourceUrl(
            "https://plane.example.com/jx/workspace-views/my-all-issues");
        var states = new Dictionary<string, StateInfo>
        {
            ["state-1"] = new("已完成", "completed")
        };

        var issue = Assert.Single(PlaneIssueClient.ParseIssues(json, source, states));

        Assert.Equal("已完成", issue.Status);
        Assert.True(issue.IsCompleted);
    }

    [Fact]
    public void ParseIssues_ResolvesProjectTypePriorityAndWorkload()
    {
        const string json = """
        {
          "results": [{
            "id": "issue-1",
            "sequence_id": 816,
            "name": "图集引用功能",
            "project_id": "project-1",
            "issue_type_id": 1,
            "state_id": "state-1",
            "priority": "2",
            "custom_fields": { "customfield_20027": "1.5" }
          }]
        }
        """;
        var source = PlaneIssueClient.ParseSourceUrl(
            "https://plane.example.com/jx/workspace-views/my-all-issues");
        var states = new Dictionary<string, StateInfo>
        {
            ["state-1"] = new("Head待开发", string.Empty)
        };
        var projects = new Dictionary<string, string> { ["project-1"] = "SJ" };
        var types = new Dictionary<string, string> { ["1"] = "Bug" };

        var issue = Assert.Single(PlaneIssueClient.ParseIssues(
            json, source, states, projects, types));

        Assert.Equal("SJ-816", issue.Key);
        Assert.Equal(IssueKind.Bug, issue.Kind);
        Assert.Equal("A", issue.Priority);
        Assert.Equal(1.5m, issue.Workload);
        Assert.Equal(WorkStage.Development, issue.Stage);
        Assert.Equal(
            "https://plane.example.com/jx/projects/project-1/issues/issue-1#SJ-816",
            issue.SourceUrl);
    }

    [Fact]
    public void BuildIssuePageUri_UsesPlaneProjectRouteAndIssueKeyAnchor()
    {
        var source = PlaneIssueClient.ParseSourceUrl(
            "https://plane.example.com/jx/workspace-views/my-all-issues");

        var uri = PlaneIssueClient.BuildIssuePageUri(source,
            "2332bfaf-26d9-4eee-866f-ad61f0835f3c",
            "39d2e6a3-44a1-4fb4-ba4c-729d3e0a7723",
            "SJ-816");

        Assert.Equal(
            "https://plane.example.com/jx/projects/2332bfaf-26d9-4eee-866f-ad61f0835f3c/" +
            "issues/39d2e6a3-44a1-4fb4-ba4c-729d3e0a7723#SJ-816",
            uri.AbsoluteUri);
    }

    [Fact]
    public void BuildIssueRelationsEndpoint_UsesEnterprisePlaneRoute()
    {
        var source = PlaneIssueClient.ParseSourceUrl(
            "https://plane.example.com/jx/workspace-views/my-all-issues");

        var uri = PlaneIssueClient.BuildIssueRelationsEndpoint(source,
            "project-1", "issue-1");

        Assert.Equal(
            "/api/workspaces/jx/projects/project-1/issues/issue-1/issue-relation/",
            uri.AbsolutePath);
    }

    [Fact]
    public void ParsePredecessors_ResolvesBlockedByIssueAndCompletion()
    {
        const string json = """
        {
          "blocking": [],
          "blocked_by": [
            {
              "id": "predecessor-1",
              "project_id": "project-1",
              "sequence_id": 793,
              "name": "招募-拼接-UI拼接制作",
              "state_id": "state-open",
              "relation_type": "blocked_by"
            },
            {
              "id": "predecessor-2",
              "project_id": "project-1",
              "sequence_id": 700,
              "name": "已完成前置",
              "state_id": "state-done",
              "relation_type": "blocked_by"
            }
          ]
        }
        """;
        var states = new Dictionary<string, StateInfo>
        {
            ["state-open"] = new("待开发", "unstarted"),
            ["state-done"] = new("已完成", "completed")
        };
        var projects = new Dictionary<string, string> { ["project-1"] = "SJ" };

        var predecessors = PlaneIssueClient.ParsePredecessors(json, states, projects);

        Assert.Equal(2, predecessors.Count);
        Assert.Equal("SJ-793", predecessors[0].Key);
        Assert.False(predecessors[0].IsCompleted);
        Assert.Equal("待开发", predecessors[0].Status);
        Assert.Equal("SJ-700", predecessors[1].Key);
        Assert.True(predecessors[1].IsCompleted);
    }

    [Fact]
    public async Task GetIssues_LoadsRelationsOnlyUntilFirstUnblockedFocusCandidate()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var handler = new AuthenticationHandler
        {
            IssuesJson = $$"""
            {
              "results": [
                {
                  "id": "issue-795",
                  "sequence_id": 795,
                  "project_identifier": "SJ",
                  "project_id": "project-1",
                  "name": "招募-程序-UI接入",
                  "state_name": "待开发",
                  "state_group": "unstarted",
                  "priority": "1",
                  "start_date": "{{today:yyyy-MM-dd}}",
                  "target_date": "{{today:yyyy-MM-dd}}"
                },
                {
                  "id": "issue-901",
                  "sequence_id": 901,
                  "project_identifier": "SJ",
                  "project_id": "project-1",
                  "name": "触发功能接入",
                  "state_name": "待开发",
                  "state_group": "unstarted",
                  "priority": "4",
                  "start_date": "{{today:yyyy-MM-dd}}",
                  "target_date": "{{today:yyyy-MM-dd}}"
                },
                {
                  "id": "issue-902",
                  "sequence_id": 902,
                  "project_identifier": "SJ",
                  "project_id": "project-1",
                  "name": "第三候选",
                  "state_name": "待开发",
                  "state_group": "unstarted",
                  "priority": "4",
                  "start_date": "{{today:yyyy-MM-dd}}",
                  "target_date": "{{today.AddDays(2):yyyy-MM-dd}}"
                }
              ]
            }
            """,
            RelationJsonByIssue =
            {
                ["issue-795"] = """
                    { "blocked_by": [{
                      "id": "issue-793", "sequence_id": 793,
                      "project_identifier": "SJ", "name": "前置任务",
                      "state_name": "待开发", "state_group": "unstarted"
                    }] }
                    """,
                ["issue-901"] = """{ "blocked_by": [] }"""
            }
        };
        using var client = new PlaneIssueClient(handler);

        var issues = await client.GetIssuesAsync(PasswordSettings());
        var summary = ScheduleSummary.Create(issues, today, 2);

        Assert.Equal(2, handler.RelationRequestCount);
        Assert.True(issues.Single(issue => issue.Key == "SJ-795")
            .HasIncompletePredecessor);
        Assert.Equal("SJ-793", issues.Single(issue => issue.Key == "SJ-795")
            .BlockedBy[0].Key);
        Assert.Equal("SJ-901", summary.CurrentFocus?.Key);
        Assert.DoesNotContain("issue-902", handler.RelationRequestedIssueIds);
    }

    [Fact]
    public async Task GetIssues_CompletedPredecessorDoesNotBlockFocus()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var handler = new AuthenticationHandler
        {
            IssuesJson = $$"""
            { "results": [{
              "id": "issue-795", "sequence_id": 795,
              "project_identifier": "SJ", "project_id": "project-1",
              "name": "招募-程序-UI接入", "state_name": "待开发",
              "state_group": "unstarted", "priority": "1",
              "start_date": "{{today:yyyy-MM-dd}}",
              "target_date": "{{today:yyyy-MM-dd}}"
            }] }
            """,
            RelationJsonByIssue =
            {
                ["issue-795"] = """
                    { "blocked_by": [{
                      "id": "issue-793", "sequence_id": 793,
                      "project_identifier": "SJ", "name": "已完成前置",
                      "state_name": "已完成", "state_group": "completed"
                    }] }
                    """
            }
        };
        using var client = new PlaneIssueClient(handler);

        var issues = await client.GetIssuesAsync(PasswordSettings());
        var focus = ScheduleSummary.Create(issues, today, 2).CurrentFocus;

        Assert.Equal(1, handler.RelationRequestCount);
        Assert.Equal("SJ-795", focus?.Key);
        Assert.False(focus?.HasIncompletePredecessor);
        Assert.True(Assert.Single(focus!.BlockedBy).IsCompleted);
    }

    [Fact]
    public void ParseIssues_HandlesEnterprisePlaneFields()
    {
        const string json = """
        {
          "results": [
            {
              "id": "7f0f0ad1",
              "sequence_id": 142,
              "project_identifier": "OPS",
              "name": "修复任务提醒",
              "issue_type_detail": { "name": "Bug" },
              "state": { "name": "处理中", "group": "started" },
              "project": { "name": "个人效率" },
              "start_date": "2026-08-01",
              "target_date": "2026-08-04",
              "updated_at": "2026-08-03T12:00:00+08:00"
            },
            {
              "id": "done-1",
              "key": "OPS-100",
              "title": "已完成任务",
              "type_name": "任务",
              "status": "已完成",
              "due_date": "2026-08-02"
            }
          ]
        }
        """;
        var source = PlaneIssueClient.ParseSourceUrl(
            "https://plane.example.com/jx/workspace-views/my-all-issues");

        var issues = PlaneIssueClient.ParseIssues(json, source);

        var bug = Assert.Single(issues, issue => issue.Id == "7f0f0ad1");
        Assert.Equal("OPS-142", bug.Key);
        Assert.Equal(IssueKind.Bug, bug.Kind);
        Assert.Equal("处理中", bug.Status);
        Assert.Equal(new DateOnly(2026, 8, 1), bug.StartDate);
        Assert.Equal(new DateOnly(2026, 8, 4), bug.DueDate);
        Assert.False(bug.IsCompleted);
        Assert.True(issues.Single(issue => issue.Id == "done-1").IsCompleted);
    }

    [Fact]
    public void ScheduleSummary_CountsOnlyActiveWorkAndDeadlines()
    {
        var today = new DateOnly(2026, 8, 3);
        var issues = new[]
        {
            Issue("task", IssueKind.Task, today.AddDays(1)),
            Issue("bug", IssueKind.Bug, today.AddDays(-1)),
            Issue("later", IssueKind.Task, today.AddDays(9)),
            Issue("done", IssueKind.Bug, today, completed: true)
        };

        var summary = ScheduleSummary.Create(issues, today, dueSoonDays: 2);

        Assert.Equal(2, summary.TaskCount);
        Assert.Equal(1, summary.BugCount);
        Assert.Equal(1, summary.DueSoonCount);
        Assert.Equal(1, summary.OverdueCount);
    }

    [Fact]
    public void ScheduleSummary_SeparatesDevelopmentFollowUpAndWaitingWork()
    {
        var today = new DateOnly(2026, 8, 4);
        var issues = new[]
        {
            Issue("dev", IssueKind.Task, today) with
                { Status = "Head待开发", Priority = "C" },
            Issue("follow", IssueKind.Task, today.AddDays(-2)) with
                { Status = "Head待测试", Priority = "A" },
            Issue("wait", IssueKind.Task, today.AddDays(5)) with
                { Status = "待需求", Priority = "S" }
        };

        var summary = ScheduleSummary.Create(issues, today, 2);

        Assert.Single(summary.DevelopmentIssues);
        Assert.Single(summary.FollowUpIssues);
        Assert.Single(summary.WaitingIssues);
        Assert.Equal("dev", summary.CurrentFocus?.Id);
    }

    [Fact]
    public void FocusSelector_ExcludesFutureAndUnscheduledHighPriorityWork()
    {
        var today = new DateOnly(2026, 8, 11);
        var futureHighPriority = Issue("future", IssueKind.Task, today.AddDays(8)) with
        {
            Key = "SJ-795",
            Title = "招募-程序-UI接入",
            Status = "待开发",
            StateGroup = "unstarted",
            Priority = "S",
            StartDate = today.AddDays(7),
            DueDate = today.AddDays(8),
            ParentId = "parent"
        };
        var unscheduledHighPriority = Issue("unscheduled", IssueKind.Task, today) with
        {
            Key = "SJ-796",
            Status = "待开发",
            StateGroup = "unstarted",
            Priority = "S",
            StartDate = null,
            DueDate = null
        };
        var todayLowerPriority = Issue("today", IssueKind.Task, today) with
        {
            Key = "SJ-901",
            Status = "待开发",
            StateGroup = "unstarted",
            Priority = "C",
            StartDate = today,
            DueDate = today
        };

        var focus = FocusIssueSelector.SelectAutomatic(
            [futureHighPriority, unscheduledHighPriority, todayLowerPriority], today);

        Assert.Equal("SJ-901", focus?.Key);
        Assert.False(FocusIssueSelector.IsExecutableToday(futureHighPriority, today));
        Assert.False(FocusIssueSelector.IsExecutableToday(unscheduledHighPriority, today));
        Assert.True(FocusIssueSelector.IsExecutableToday(todayLowerPriority, today));
    }

    [Fact]
    public void FocusSelector_PrefersInProgressWorkButHonorsManualSelection()
    {
        var today = new DateOnly(2026, 8, 11);
        var waitingHighPriority = Issue("waiting", IssueKind.Task, today) with
        {
            Key = "SJ-900", Status = "待开发", StateGroup = "unstarted",
            Priority = "S", StartDate = today, DueDate = today
        };
        var inProgress = Issue("progress", IssueKind.Task, today.AddDays(2)) with
        {
            Key = "SJ-901", Status = "开发中", StateGroup = "started",
            Priority = "B", StartDate = today.AddDays(-1), DueDate = today.AddDays(2)
        };
        var futureManual = Issue("manual", IssueKind.Task, today.AddDays(8)) with
        {
            Key = "SJ-795", Status = "待开发", StateGroup = "unstarted",
            Priority = "S", StartDate = today.AddDays(7), DueDate = today.AddDays(8)
        };

        Assert.Equal(inProgress.Id, FocusIssueSelector.SelectAutomatic(
            [waitingHighPriority, inProgress, futureManual], today)?.Id);
        Assert.Equal(futureManual.Id, FocusIssueSelector.Select(
            [waitingHighPriority, inProgress, futureManual], today, futureManual.Id)?.Id);
    }

    [Fact]
    public void FocusSelector_ReturnsNoAutomaticFocusWhenNothingIsExecutable()
    {
        var today = new DateOnly(2026, 8, 11);
        var future = Issue("future", IssueKind.Task, today.AddDays(2)) with
        {
            StartDate = today.AddDays(1), DueDate = today.AddDays(2), Priority = "S"
        };
        var unscheduled = Issue("unscheduled", IssueKind.Task, today) with
        {
            StartDate = null, DueDate = null, Priority = "S"
        };

        Assert.Null(FocusIssueSelector.SelectAutomatic([future, unscheduled], today));
    }

    [Fact]
    public void ScheduleAndDailySummary_UseTheSameAutomaticFocus()
    {
        var today = new DateOnly(2026, 8, 11);
        var futureHighPriority = Issue("future", IssueKind.Task, today.AddDays(8)) with
        {
            Key = "SJ-795", Status = "待开发", StateGroup = "unstarted",
            Priority = "S", StartDate = today.AddDays(7), DueDate = today.AddDays(8)
        };
        var current = Issue("current", IssueKind.Task, today) with
        {
            Key = "SJ-901", Status = "待开发", StateGroup = "unstarted",
            Priority = "C", StartDate = today, DueDate = today
        };
        var issues = new[] { futureHighPriority, current };

        var schedule = ScheduleSummary.Create(issues, today, 2);
        var daily = DailyWorkSummary.Create(issues, [], today, null);

        Assert.Equal("SJ-901", schedule.CurrentFocus?.Key);
        Assert.Equal(schedule.CurrentFocus?.Id, daily.FocusIssue?.Id);
        Assert.False(daily.FocusIsManual);
    }

    [Fact]
    public void ScheduleSummary_GroupsMultiDayIssuesIntoEachWeekday()
    {
        var monday = new DateOnly(2026, 8, 3);
        var issue = Issue("week", IssueKind.Task, monday.AddDays(2)) with
        {
            Status = "Head待开发",
            StartDate = monday
        };
        var summary = ScheduleSummary.Create([issue], monday.AddDays(1), 2);

        Assert.Equal(monday, ScheduleSummary.StartOfWeek(monday.AddDays(3)));
        Assert.Single(summary.GetIssuesForDate(monday));
        Assert.Single(summary.GetIssuesForDate(monday.AddDays(2)));
        Assert.Empty(summary.GetIssuesForDate(monday.AddDays(3)));
    }

    [Fact]
    public void ScheduleSummary_CanIncludeCompletedIssuesForHistoricalWeeks()
    {
        var monday = new DateOnly(2026, 7, 27);
        var completed = Issue("done", IssueKind.Task, monday.AddDays(2), completed: true) with
        {
            StartDate = monday
        };
        var summary = ScheduleSummary.Create([completed], monday.AddDays(10), 2);

        Assert.Empty(summary.GetIssuesForDate(monday));
        Assert.Single(summary.GetIssuesForDate(monday, includeCompleted: true));
    }

    [Fact]
    public void CompactTitle_RemovesRepeatedDevelopmentPrefixes()
    {
        var title = IssueTextFormatter.CompactTitle(
            "UI底层支持1.0-8月版本-程序-功能开发图集查找预制引用功能编写");

        Assert.Equal("图集查找预制引用功能编写", title);
    }

    [Fact]
    public void ReminderEvaluator_RemindsSameDeadlineOnlyOncePerDay()
    {
        var evaluator = new ReminderEvaluator();
        var today = new DateOnly(2026, 8, 3);
        var issues = new[] { Issue("due", IssueKind.Task, today) };

        var first = evaluator.GetDueReminders(issues, today, 2);
        var second = evaluator.GetDueReminders(issues, today, 2);

        Assert.Single(first);
        Assert.Empty(second);
    }

    [Fact]
    public void IssueSnapshot_DetectsOnlyExistingIssueStatusChanges()
    {
        var today = new DateOnly(2026, 8, 3);
        var current = new[]
        {
            Issue("changed", IssueKind.Task, today) with { Status = "已完成" },
            Issue("same", IssueKind.Bug, today),
            Issue("new", IssueKind.Task, today)
        };
        var previous = new Dictionary<string, string>
        {
            ["changed"] = "进行中",
            ["same"] = "进行中"
        };

        var changes = IssueSnapshotStore.DetectChanges(previous, current);

        var change = Assert.Single(changes);
        Assert.Equal("进行中", change.PreviousStatus);
        Assert.Equal("已完成", change.CurrentStatus);
        Assert.NotEqual(default, change.ChangedAt);
    }

    [Fact]
    public void NotificationStore_PersistsDeduplicatesAndAcknowledgesChanges()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"task-reminder-tests-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "pending.json");
        try
        {
            var store = new NotificationStore(path);
            var changedAt = new DateTimeOffset(2026, 8, 4, 9, 30, 0,
                TimeSpan.FromHours(8));
            var change = new IssueChange("issue-1", "SJ-816", "任务提醒",
                "待开发", "开发中", changedAt, "http://localhost/issues/issue-1");

            var first = store.AddChanges([change]);
            var duplicate = store.AddChanges([change]);
            var reloaded = new NotificationStore(path).LoadPending();

            Assert.Single(first);
            Assert.Single(duplicate);
            Assert.Single(reloaded);
            Assert.Equal("SJ-816", reloaded[0].IssueKey);

            var remaining = store.Acknowledge(reloaded[0].Id);
            Assert.Empty(remaining);
            Assert.Empty(store.LoadPending());
            var history = store.LoadHistory();
            Assert.Single(history);
            Assert.True(history[0].IsRead);
            Assert.NotNull(history[0].ReadAt);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void NotificationStore_AcknowledgeAllKeepsReadableHistory()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"task-reminder-notifications-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "notifications.json");
        try
        {
            var store = new NotificationStore(path);
            var changedAt = DateTimeOffset.Parse("2026-08-04T09:30:00+08:00");
            store.AddChanges([
                new IssueChange("1", "SJ-1", "第一个任务", "待开发", "开发中",
                    changedAt, "https://plane.example.com/issues/1"),
                new IssueChange("2", "SJ-2", "第二个任务", "开发中", "待测试",
                    changedAt.AddMinutes(10), "https://plane.example.com/issues/2")
            ]);

            var pending = store.AcknowledgeAll();
            var history = store.LoadHistory();

            Assert.Empty(pending);
            Assert.Empty(store.LoadPending());
            Assert.Equal(2, history.Count);
            Assert.All(history, notification => Assert.True(notification.IsRead));
            Assert.True(history[0].ChangedAt > history[1].ChangedAt);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void NotificationStore_DueTodayRemindsOncePerIssuePerDay()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"task-reminder-due-today-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "notifications.json");
        try
        {
            var store = new NotificationStore(path);
            var today = new DateOnly(2026, 8, 11);
            var dueToday = Issue("due-today", IssueKind.Task, today) with
            {
                Key = "SJ-901",
                Title = "今天到期的功能开发",
                Status = "开发中",
                DueDate = today,
                SourceUrl = "https://plane.example.com/issues/due-today"
            };
            var later = Issue("later", IssueKind.Bug, today.AddDays(1)) with
            {
                DueDate = today.AddDays(1)
            };
            var detectedAt = DateTimeOffset.Parse("2026-08-11T09:00:00+08:00");

            var first = store.AddDueToday([dueToday, later], today, detectedAt);
            var duplicate = store.AddDueToday([dueToday], today, detectedAt.AddHours(1));

            var notification = Assert.Single(first);
            Assert.Single(duplicate);
            Assert.Equal(NotificationKind.DueToday, notification.Kind);
            Assert.Equal("SJ-901", notification.IssueKey);
            Assert.Equal("今天到期  ·  开发中",
                NotificationCenterForm.NotificationDescription(notification));

            store.Acknowledge(notification.Id);
            Assert.Empty(store.AddDueToday([dueToday], today, detectedAt.AddHours(2)));
            Assert.Single(store.LoadHistory());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void NotificationStore_KeepsSequentialChangesForTheSameIssue()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"task-reminder-tests-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "pending.json");
        try
        {
            var store = new NotificationStore(path);
            var firstAt = DateTimeOffset.Parse("2026-08-04T09:30:00+08:00");
            var first = new IssueChange("issue-1", "SJ-816", "任务提醒",
                "待开发", "开发中", firstAt, string.Empty);
            var second = new IssueChange("issue-1", "SJ-816", "任务提醒",
                "开发中", "待测试", firstAt.AddMinutes(30), string.Empty);

            var pending = store.AddChanges([first, second]);

            Assert.Equal(2, pending.Count);
            Assert.Equal("开发中", pending[0].CurrentStatus);
            Assert.Equal("待测试", pending[1].CurrentStatus);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void PersonalWorkStore_PersistsFocusReminderAndFormatsIssueInformation()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"task-reminder-personal-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "personal.json");
        try
        {
            var store = new PersonalWorkStore(path);
            var today = new DateOnly(2026, 8, 4);
            var issue = Issue("focus", IssueKind.Bug, today) with
            {
                Key = "SJ-816",
                Title = "图集查找预制引用功能编写",
                SourceUrl = "https://plane.example.com/issues/focus",
                Priority = "A"
            };
            var remindAt = DateTimeOffset.Parse("2026-08-04T17:30:00+08:00");

            store.SetFocus(issue.Id);
            var state = store.AddReminder(issue, remindAt);
            var reloaded = new PersonalWorkStore(path).Load();
            var information = PersonalWorkStore.FormatIssueInformation(issue);

            Assert.Equal(issue.Id, reloaded.FocusIssueId);
            Assert.Single(state.Reminders);
            Assert.Equal(remindAt, reloaded.Reminders[0].RemindAt);
            Assert.Contains("SJ-816 图集查找预制引用功能编写", information);
            Assert.Equal("SJ-816 图集查找预制引用功能编写", information);
            Assert.DoesNotContain("类型：", information);
            Assert.DoesNotContain(issue.SourceUrl, information);

            Assert.Empty(store.RemoveReminder(reloaded.Reminders[0].Id).Reminders);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void DailyWorkSummary_PrioritizesTodayRisksAndRecentChanges()
    {
        var today = new DateOnly(2026, 8, 11);
        var focus = Issue("focus", IssueKind.Task, today) with
        {
            Key = "SJ-901", Priority = "S", StartDate = today.AddDays(-1),
            DueDate = today, Status = "开发中"
        };
        var overdue = Issue("overdue", IssueKind.Bug, today.AddDays(-1)) with
        {
            Key = "SJ-902", DueDate = today.AddDays(-1), Status = "待测试"
        };
        var tomorrow = Issue("tomorrow", IssueKind.Task, today.AddDays(1)) with
        {
            Key = "SJ-903", StartDate = today.AddDays(1), DueDate = today.AddDays(1)
        };
        var unscheduled = Issue("unscheduled", IssueKind.Task, today) with
        {
            Key = "SJ-904", StartDate = null, DueDate = null
        };
        var changedAt = new DateTimeOffset(2026, 8, 11, 8, 30, 0,
            TimeSpan.FromHours(8));
        var changes = new[]
        {
            PersistentNotification.FromChange(new IssueChange(focus.Id, focus.Key,
                focus.Title, "待开发", "开发中", changedAt, focus.SourceUrl)),
            PersistentNotification.DueToday(focus, today, changedAt)
        };

        var summary = DailyWorkSummary.Create(
            [focus, overdue, tomorrow, unscheduled], changes, today, focus.Id);

        Assert.Equal(focus.Id, summary.FocusIssue?.Id);
        Assert.Contains(summary.TodayIssues, issue => issue.Id == focus.Id);
        Assert.Single(summary.DueTodayIssues);
        Assert.Single(summary.OverdueIssues);
        Assert.Single(summary.TomorrowIssues);
        Assert.Single(summary.UnscheduledIssues);
        Assert.Single(summary.RecentStatusChanges);
        Assert.Equal(NotificationKind.StatusChange, summary.RecentStatusChanges[0].Kind);
    }

    [Fact]
    public void PersonalWorkStore_PersistsDailySummaryShownDate()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"task-reminder-summary-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "personal.json");
        try
        {
            var store = new PersonalWorkStore(path);
            var date = new DateOnly(2026, 8, 11);

            store.MarkDailySummaryShown(date);

            Assert.Equal(date, store.Load().LastDailySummaryShownDate);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static IssueItem Issue(
        string id,
        IssueKind kind,
        DateOnly due,
        bool completed = false) => new(
        id, id.ToUpperInvariant(), id, kind, completed ? "已完成" : "进行中",
        completed ? "completed" : "started", due.AddDays(-1), due, null,
        "测试", completed, string.Empty);

    private static AppSettings PasswordSettings()
    {
        var settings = new AppSettings
        {
            SourceUrl = "https://plane.example.com/jx/workspace-views/my-all-issues",
            AuthenticationMode = AuthenticationMode.Password,
            UserName = "user@example.com"
        };
        settings.SetSecret("password");
        return settings;
    }

    private sealed class AuthenticationHandler : HttpMessageHandler
    {
        public int SignInCount { get; private set; }
        public int UserRequestCount { get; private set; }
        public bool RejectSecondUserRequest { get; init; }
        public string IssuesJson { get; init; } = "{\"results\":[]}";
        public Dictionary<string, string> RelationJsonByIssue { get; init; } = [];
        public int RelationRequestCount { get; private set; }
        public List<string> RelationRequestedIssueIds { get; } = [];
        public List<string?> BearerTokens { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/sign-in/")
            {
                SignInCount++;
                return Json($"{{\"access_token\":\"token-{SignInCount}\"}}");
            }

            BearerTokens.Add(request.Headers.Authorization?.Parameter);
            if (path == "/api/users/me/")
            {
                UserRequestCount++;
                if (RejectSecondUserRequest && UserRequestCount == 2)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    {
                        Content = new StringContent("unauthorized")
                    });
                }

                return Json("{\"id\":\"user-1\"}");
            }

            if (path.EndsWith("/issue-relation/", StringComparison.Ordinal))
            {
                RelationRequestCount++;
                var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var issueId = segments[^2];
                RelationRequestedIssueIds.Add(issueId);
                return Json(RelationJsonByIssue.GetValueOrDefault(issueId,
                    "{\"blocked_by\":[]}"));
            }

            if (path == "/api/workspaces/jx/issues/")
            {
                return Json(IssuesJson);
            }

            return path.Contains("/issues/", StringComparison.Ordinal)
                ? Json("{\"results\":[]}")
                : Json("[]");
        }

        private static Task<HttpResponseMessage> Json(string json) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }
}
