using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TaskReminderTray.Models;

namespace TaskReminderTray.Services;

internal sealed class PlaneIssueClient : IDisposable
{
    private readonly HttpClient _httpClient;

    public PlaneIssueClient(HttpMessageHandler? handler = null)
    {
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler);
        _httpClient.Timeout = TimeSpan.FromSeconds(25);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("TaskReminderTray/0.1");
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<IReadOnlyList<IssueItem>> GetIssuesAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        var source = ParseSourceUrl(settings.SourceUrl);
        var token = settings.AuthenticationMode == AuthenticationMode.Password
            ? await SignInAsync(source.BaseUri, settings.UserName, settings.GetSecret(), cancellationToken)
            : settings.GetSecret();

        var currentUserJson = await GetJsonAsync(new Uri(source.BaseUri, "/api/users/me/"),
            token, "读取当前用户失败", cancellationToken);
        var currentUserId = ParseCurrentUserId(currentUserJson);
        var requestUri = BuildIssuesEndpoint(source, currentUserId);
        var issuesJson = await GetJsonAsync(requestUri, token, "读取任务失败", cancellationToken);
        var workspace = Uri.EscapeDataString(source.WorkspaceSlug);
        var statesTask = TryGetJsonAsync(new Uri(source.BaseUri,
            $"/api/workspaces/{workspace}/states/"), token, cancellationToken);
        var projectsTask = TryGetJsonAsync(new Uri(source.BaseUri,
            $"/api/workspaces/{workspace}/projects/"), token, cancellationToken);
        var issueTypesTask = TryGetJsonAsync(new Uri(source.BaseUri,
            "/api/global/issue-type"), token, cancellationToken);
        await Task.WhenAll(statesTask, projectsTask, issueTypesTask);
        var statesJson = await statesTask;
        var states = statesJson is null
            ? new Dictionary<string, StateInfo>(StringComparer.OrdinalIgnoreCase)
            : ParseStates(statesJson);
        var projects = ParseLookup(await projectsTask, "identifier", "name");
        var issueTypes = ParseLookup(await issueTypesTask, "name");
        return ParseIssues(issuesJson, source, states, projects, issueTypes);
    }

    public async Task<int> TestConnectionAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default) =>
        (await GetIssuesAsync(settings, cancellationToken)).Count;

    internal static SourceLocation ParseSourceUrl(string sourceUrl)
    {
        if (!Uri.TryCreate(sourceUrl.Trim(), UriKind.Absolute, out var source) ||
            source.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("数据地址必须是有效的 HTTP/HTTPS 地址。");
        }

        var segments = source.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var workspaceIndex = Array.FindIndex(segments,
            segment => string.Equals(segment, "workspace-views", StringComparison.OrdinalIgnoreCase));
        if (workspaceIndex <= 0 || workspaceIndex + 1 >= segments.Length)
        {
            throw new InvalidOperationException(
                "数据地址应类似 http://服务器/jx/workspace-views/my-all-issues。");
        }

        var baseUri = new Uri(source.GetLeftPart(UriPartial.Authority));
        return new SourceLocation(
            baseUri,
            Uri.UnescapeDataString(segments[workspaceIndex - 1]),
            Uri.UnescapeDataString(segments[workspaceIndex + 1]),
            source);
    }

    internal static Uri BuildIssuesEndpoint(SourceLocation source, string? currentUserId = null)
    {
        var workspace = Uri.EscapeDataString(source.WorkspaceSlug);
        var view = Uri.EscapeDataString(source.ViewId);
        var currentUserFilter = string.IsNullOrWhiteSpace(currentUserId)
            ? string.Empty
            : $"&association__assignees={Uri.EscapeDataString(currentUserId)}";
        return new Uri(source.BaseUri,
            $"/api/workspaces/{workspace}/issues/?viewId={view}" +
            currentUserFilter +
            $"&showCompleted=true&sub_issue=true&expand=issue_attachment" +
            $"&per_page=500&cursor=500%3A0%3A0&order_by=target_date");
    }

    internal static IReadOnlyList<IssueItem> ParseIssues(
        string json,
        SourceLocation source,
        IReadOnlyDictionary<string, StateInfo>? states = null,
        IReadOnlyDictionary<string, string>? projects = null,
        IReadOnlyDictionary<string, string>? issueTypes = null)
    {
        using var document = JsonDocument.Parse(json);
        var issueArray = FindIssueArray(document.RootElement);
        if (issueArray is null)
        {
            throw new InvalidOperationException("接口返回成功，但没有识别到任务列表字段。");
        }

        var issues = new List<IssueItem>();
        foreach (var item in issueArray.Value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = Text(item, "id", "uuid", "issue_id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var sequence = Text(item, "sequence_id", "sequence", "number");
            var projectId = Text(item, "project_id") ?? Text(item, "project");
            string? projectIdentifier = null;
            if (projects is not null && projectId is not null)
            {
                projects.TryGetValue(projectId, out projectIdentifier);
            }
            var identifier = Text(item, "project_identifier", "identifier") ??
                             projectIdentifier;
            var key = !string.IsNullOrWhiteSpace(identifier) && !string.IsNullOrWhiteSpace(sequence)
                ? $"{identifier}-{sequence}"
                : Text(item, "key", "issue_key") ?? id[..Math.Min(8, id.Length)];
            var title = Text(item, "name", "title", "summary") ?? "未命名任务";
            var state = Object(item, "state", "state_detail", "state_data");
            var stateId = Text(item, "state_id") ?? Text(item, "state");
            StateInfo? stateFromMap = null;
            if (states is not null && stateId is not null)
            {
                states.TryGetValue(stateId, out stateFromMap);
            }
            var status = Text(item, "state_name", "status_name", "status") ??
                         (state is { } stateObject
                             ? Text(stateObject, "name", "label", "title")
                             : null) ?? stateFromMap?.Name ?? stateId ?? "未设置";
            var stateGroup = Text(item, "state_group", "group") ??
                             (state is { } groupObject
                                 ? Text(groupObject, "group", "state_group", "type")
                                 : null) ?? stateFromMap?.Group ?? string.Empty;
            var issueType = Object(item, "issue_type", "issue_type_detail", "type");
            var issueTypeId = Text(item, "issue_type_id") ??
                              Text(item, "issue_type", "type");
            string? issueTypeName = null;
            if (issueTypes is not null && issueTypeId is not null)
            {
                issueTypes.TryGetValue(issueTypeId, out issueTypeName);
            }
            var issueTypeText = Text(item, "issue_type_name", "type_name") ??
                                (issueType is { } typeObject
                                    ? Text(typeObject, "name", "label", "title")
                                    : Text(item, "issue_type", "type")) ??
                                issueTypeName ?? issueTypeId ?? string.Empty;
            var kind = IsBug(issueTypeText) ? IssueKind.Bug : IssueKind.Task;
            var project = Object(item, "project", "project_detail");
            var projectName = Text(item, "project_name") ??
                              (project is { } projectObject
                                  ? Text(projectObject, "name", "identifier")
                                  : null) ?? projectIdentifier ?? string.Empty;
            var startDate = Date(item, "start_date", "startDate", "planned_start_date");
            var dueDate = Date(item, "target_date", "due_date", "end_date", "dueDate");
            var updated = DateTimeValue(item, "updated_at", "updatedAt", "modified_at");
            var priority = Priority(Text(item, "priority", "priority_id"));
            var workload = DecimalValue(item, "workload") ??
                           CustomFieldDecimal(item, "customfield_20027");
            var parentId = Text(item, "parent_id");
            var subIssueCount = Integer(item, "sub_issues_count", "sub_issue_count");
            var completed = Boolean(item, "completed", "is_completed") ||
                            IsCompletedState(stateGroup, status);
            var issueUrl = new Uri(source.BaseUri,
                $"/{Uri.EscapeDataString(source.WorkspaceSlug)}/issues/{Uri.EscapeDataString(id)}");

            issues.Add(new IssueItem(id, key, title, kind, status, stateGroup,
                startDate, dueDate, updated, projectName, completed, issueUrl.ToString(),
                priority, workload, parentId, subIssueCount));
        }

        return issues;
    }

    internal static string ParseCurrentUserId(string json)
    {
        using var document = JsonDocument.Parse(json);
        var id = Text(document.RootElement, "id", "uuid", "user_id");
        return string.IsNullOrWhiteSpace(id)
            ? throw new InvalidOperationException("当前用户响应中没有用户 ID，无法限定“我的任务”。")
            : id;
    }

    internal static IReadOnlyDictionary<string, StateInfo> ParseStates(string json)
    {
        using var document = JsonDocument.Parse(json);
        var array = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement
            : FindIssueArray(document.RootElement);
        var states = new Dictionary<string, StateInfo>(StringComparer.OrdinalIgnoreCase);
        if (array is null)
        {
            return states;
        }

        foreach (var item in array.Value.EnumerateArray())
        {
            var id = Text(item, "id", "uuid");
            var name = Text(item, "name", "label", "title");
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
            {
                states[id] = new StateInfo(name,
                    Text(item, "group", "state_group", "type") ?? string.Empty);
            }
        }

        return states;
    }

    internal static IReadOnlyDictionary<string, string> ParseLookup(
        string? json,
        params string[] valueNames)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        using var document = JsonDocument.Parse(json);
        var array = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement
            : FindIssueArray(document.RootElement);
        if (array is null)
        {
            return result;
        }

        foreach (var item in array.Value.EnumerateArray())
        {
            var id = Text(item, "id", "uuid", "issue_tid");
            var value = Text(item, valueNames);
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(value))
            {
                result[id] = value;
            }
        }

        return result;
    }

    private async Task<string> SignInAsync(
        Uri baseUri,
        string userName,
        string password,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("账号和密码必须填写。");
        }

        using var response = await _httpClient.PostAsJsonAsync(
            new Uri(baseUri, "/api/sign-in/"),
            new { email = userName.Trim(), password },
            cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateHttpError("登录失败", response.StatusCode, content);
        }

        using var document = JsonDocument.Parse(content);
        var token = Text(document.RootElement, "access_token", "accessToken", "token");
        return string.IsNullOrWhiteSpace(token)
            ? throw new InvalidOperationException("登录成功，但响应中没有 access_token。")
            : token;
    }

    private async Task<string> GetJsonAsync(
        Uri uri,
        string token,
        string errorPrefix,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException("登录已失效或没有该视图的访问权限，请检查账号/令牌。");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw CreateHttpError(errorPrefix, response.StatusCode, content);
        }

        return content;
    }

    private async Task<string?> TryGetJsonAsync(
        Uri uri,
        string token,
        CancellationToken cancellationToken)
    {
        try
        {
            return await GetJsonAsync(uri, token, "读取状态字典失败", cancellationToken);
        }
        catch
        {
            // 状态字典用于把 UUID 翻译为名称，不应阻塞主体任务列表。
            return null;
        }
    }

    private static JsonElement? FindIssueArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in new[] { "results", "issues", "items", "data" })
        {
            if (!TryGet(root, name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Array)
            {
                return value;
            }

            var nested = FindIssueArray(value);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static string? Text(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGet(element, name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }

            if (value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            {
                return value.ToString();
            }
        }

        return null;
    }

    private static JsonElement? Object(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGet(element, name, out var value) && value.ValueKind == JsonValueKind.Object)
            {
                return value;
            }
        }

        return null;
    }

    private static DateOnly? Date(JsonElement element, params string[] names)
    {
        var value = Text(element, names);
        return DateOnly.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces, out var date)
            ? date
            : null;
    }

    private static DateTimeOffset? DateTimeValue(JsonElement element, params string[] names)
    {
        var value = Text(element, names);
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces, out var result)
            ? result
            : null;
    }

    private static decimal? DecimalValue(JsonElement element, params string[] names)
    {
        var value = Text(element, names);
        return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture,
            out var result)
            ? result
            : null;
    }

    private static decimal? CustomFieldDecimal(JsonElement element, string fieldName)
    {
        if (!TryGet(element, "custom_fields", out var customFields) ||
            customFields.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return DecimalValue(customFields, fieldName);
    }

    private static int Integer(JsonElement element, params string[] names)
    {
        var value = Text(element, names);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture,
            out var result)
            ? result
            : 0;
    }

    private static string Priority(string? value) => value?.Trim() switch
    {
        "1" => "S",
        "2" => "A",
        "3" => "B",
        "4" => "C",
        "5" => "未确定",
        { Length: > 0 } text => text,
        _ => "未确定"
    };

    private static bool Boolean(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGet(element, name, out var value))
            {
                if (value.ValueKind == JsonValueKind.True)
                {
                    return true;
                }

                if (value.ValueKind == JsonValueKind.String &&
                    bool.TryParse(value.GetString(), out var result))
                {
                    return result;
                }
            }
        }

        return false;
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static bool IsBug(string issueType) =>
        issueType.Trim() == "1" ||
        issueType.Contains("bug", StringComparison.OrdinalIgnoreCase) ||
        issueType.Contains("缺陷", StringComparison.OrdinalIgnoreCase);

    private static bool IsCompletedState(string group, string status) =>
        group.Equals("completed", StringComparison.OrdinalIgnoreCase) ||
        group.Equals("cancelled", StringComparison.OrdinalIgnoreCase) ||
        status.Contains("完成", StringComparison.OrdinalIgnoreCase) ||
        status.Contains("关闭", StringComparison.OrdinalIgnoreCase) ||
        status.Contains("取消", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("done", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("closed", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("cancelled", StringComparison.OrdinalIgnoreCase);

    private static InvalidOperationException CreateHttpError(
        string prefix,
        HttpStatusCode statusCode,
        string content)
    {
        var message = ExtractError(content);
        return new InvalidOperationException(
            $"{prefix}（HTTP {(int)statusCode}）：{message}");
    }

    private static string ExtractError(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            return Text(document.RootElement, "error", "detail", "message") ??
                   "服务器未提供错误详情";
        }
        catch
        {
            var clean = WebUtility.HtmlDecode(content).Trim();
            return clean.Length == 0
                ? "服务器未提供错误详情"
                : clean[..Math.Min(clean.Length, 180)];
        }
    }

    public void Dispose() => _httpClient.Dispose();
}

internal sealed record SourceLocation(
    Uri BaseUri,
    string WorkspaceSlug,
    string ViewId,
    Uri PageUri);

internal sealed record StateInfo(string Name, string Group);
