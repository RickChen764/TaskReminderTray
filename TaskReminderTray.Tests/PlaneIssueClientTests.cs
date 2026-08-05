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
