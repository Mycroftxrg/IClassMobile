using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IClassMobile.Services;

public sealed class IClassClient : IDisposable
{
    private const string DirectBase = "https://iclass.buaa.edu.cn:8347";
    private const string Direct8081Base = "http://iclass.buaa.edu.cn:8081";
    private const string MyCenterUrl = "https://iclass.buaa.edu.cn:8346/?type=jumpMyCenter";
    private const string VpnServiceLogin = "https://d.buaa.edu.cn/login?cas_login=true";
    private static readonly string SsoVpnLogin = WebVpnUrl.Wrap($"https://sso.buaa.edu.cn/login?service={Escape(VpnServiceLogin)}");
    private const long VpnOffsetCorrectionMs = -1000;

    private static readonly IClassUrls DirectUrls = new(
        ServiceHome: DirectBase,
        UserLogin: $"{DirectBase}/app/user/login.action",
        CourseList: $"{DirectBase}/app/choosecourse/get_myall_course.action",
        SemesterList: $"{DirectBase}/app/course/get_base_school_year.action",
        CourseSignDetail: $"{DirectBase}/app/my/get_my_course_sign_detail.action",
        ScanSign: $"{Direct8081Base}/app/course/stu_scan_sign.action",
        Timestamp: $"{Direct8081Base}/app/common/get_timestamp.action",
        CourseScheduleByDate: $"{DirectBase}/app/course/get_stu_course_sched.action");

    private readonly bool _useVpn;
    private readonly SocketsHttpHandler _handler;
    private readonly HttpClient _http;

    public IClassClient(bool useVpn)
    {
        _useVpn = useVpn;
        _handler = new SocketsHttpHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };
        _http = new HttpClient(_handler)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    public IClassUser? User { get; private set; }

    public string? SessionId { get; private set; }

    public long ServerTimeOffsetMs { get; private set; }

    private IClassUrls Urls => _useVpn ? DirectUrls.WrapForWebVpn() : DirectUrls;

    public async Task<IClassLoginResult> LoginAsync(IClassLoginInput input, CancellationToken cancellationToken = default)
    {
        var studentId = (input.StudentId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(studentId))
        {
            throw new InvalidOperationException("studentId 不能为空");
        }

        if (_useVpn)
        {
            await VpnLoginAsync(input.VpnUsername ?? string.Empty, input.VpnPassword ?? string.Empty, cancellationToken);
        }

        await FetchUserInfoWithCandidatesAsync(studentId, cancellationToken);

        if (User is null || string.IsNullOrWhiteSpace(SessionId))
        {
            throw new InvalidOperationException("登录成功但用户信息不完整，请重试");
        }

        return new IClassLoginResult(User.Id, User.DisplayName, SessionId, _useVpn);
    }

    public async Task<string?> GetCurrentSemesterAsync(CancellationToken cancellationToken = default)
    {
        EnsureLoggedIn();

        var url = WithQuery(Urls.SemesterList, new Dictionary<string, string>
        {
            ["userId"] = User!.Id,
            ["type"] = "2"
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("sessionId", SessionId);
        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (response.StatusCode != HttpStatusCode.OK)
        {
            return null;
        }

        using var doc = ParseJsonOrNull(body);
        if (doc is null || GetString(doc.RootElement, "STATUS") != "0")
        {
            return null;
        }

        if (!doc.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? fallback = null;
        foreach (var semester in result.EnumerateArray())
        {
            var code = GetString(semester, "code");
            fallback ??= code;
            if (GetString(semester, "yearStatus") == "1")
            {
                return code;
            }
        }

        return fallback;
    }

    public async Task<IReadOnlyList<CourseItem>> GetCoursesAsync(string semesterCode, CancellationToken cancellationToken = default)
    {
        EnsureLoggedIn();

        var url = WithQuery(Urls.CourseList, new Dictionary<string, string>
        {
            ["user_type"] = "1",
            ["id"] = User!.Id,
            ["xq_code"] = semesterCode
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("sessionId", SessionId);
        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (response.StatusCode != HttpStatusCode.OK)
        {
            return Array.Empty<CourseItem>();
        }

        using var doc = ParseJsonOrNull(body);
        if (doc is null || GetString(doc.RootElement, "STATUS") != "0")
        {
            return Array.Empty<CourseItem>();
        }

        if (!doc.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<CourseItem>();
        }

        var courses = new List<CourseItem>();
        foreach (var course in result.EnumerateArray())
        {
            var id = GetString(course, "course_id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var name = GetString(course, "course_name");
            courses.Add(new CourseItem(string.IsNullOrWhiteSpace(name) ? "未知课程" : name, id));
        }

        return courses;
    }

    public async Task<IReadOnlyList<CourseDetailItem>> GetCourseDetailsAsync(IReadOnlyList<CourseItem> courses, CancellationToken cancellationToken = default)
    {
        EnsureLoggedIn();
        var details = new List<CourseDetailItem>();

        foreach (var course in courses)
        {
            var url = $"{Urls.CourseSignDetail}?id={Escape(User!.Id)}&courseId={Escape(course.Id)}&sessionId={Escape(SessionId!)}";
            using var response = await _http.GetAsync(url, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = ParseJsonOrNull(body);

            if (doc is null || !doc.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var records = result.EnumerateArray()
                .OrderByDescending(record => GetString(record, "teachTime"), StringComparer.Ordinal)
                .ToList();

            foreach (var record in records)
            {
                var schedId = GetString(record, "courseSchedId");
                if (string.IsNullOrWhiteSpace(schedId))
                {
                    continue;
                }

                details.Add(new CourseDetailItem(
                    Name: course.Name,
                    Id: course.Id,
                    CourseSchedId: schedId,
                    Date: NormalizeDateDisplay(GetString(record, "teachTime")),
                    StartTime: NormalizeTimeDisplay(GetString(record, "classBeginTime")),
                    EndTime: NormalizeTimeDisplay(GetString(record, "classEndTime")),
                    SignStatus: GetString(record, "signStatus")));
            }
        }

        return details;
    }

    public async Task<IReadOnlyList<CourseDetailItem>> GetCourseByDateAsync(string dateStr, CancellationToken cancellationToken = default)
    {
        EnsureLoggedIn();
        if (!IsValidDateStr(dateStr))
        {
            throw new InvalidOperationException($"dateStr 格式错误，应为 YYYYMMDD，当前值: {dateStr}");
        }

        var url = WithQuery(Urls.CourseScheduleByDate, new Dictionary<string, string>
        {
            ["id"] = User!.Id,
            ["dateStr"] = dateStr
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("sessionId", SessionId);
        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (response.StatusCode != HttpStatusCode.OK)
        {
            return Array.Empty<CourseDetailItem>();
        }

        using var doc = ParseJsonOrNull(body);
        if (doc is null)
        {
            return Array.Empty<CourseDetailItem>();
        }

        var status = GetString(doc.RootElement, "STATUS");
        if (status == "2")
        {
            return Array.Empty<CourseDetailItem>();
        }

        if (status != "0" || !doc.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<CourseDetailItem>();
        }

        var details = new List<CourseDetailItem>();
        foreach (var record in result.EnumerateArray())
        {
            var schedId = GetString(record, "id");
            if (string.IsNullOrWhiteSpace(schedId))
            {
                continue;
            }

            details.Add(new CourseDetailItem(
                Name: Coalesce(GetString(record, "courseName"), "未知课程"),
                Id: GetString(record, "courseId"),
                CourseSchedId: schedId,
                Date: NormalizeDateDisplay(Coalesce(GetString(record, "teachTime"), dateStr)),
                StartTime: NormalizeTimeDisplay(GetString(record, "classBeginTime")),
                EndTime: NormalizeTimeDisplay(GetString(record, "classEndTime")),
                SignStatus: GetString(record, "signStatus")));
        }

        return details;
    }

    public async Task<IReadOnlyList<CourseDetailItem>> GetMergedCourseDetailsAsync(
        int futureDays = 7,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report("正在获取学期信息...");
        var semesterCode = await GetCurrentSemesterAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(semesterCode))
        {
            throw new InvalidOperationException("未获取到当前学期");
        }

        progress?.Report("正在读取课程列表...");
        var courses = await GetCoursesAsync(semesterCode, cancellationToken);
        progress?.Report("正在获取签到信息...");
        var fromDetail = await GetCourseDetailsAsync(courses, cancellationToken);
        var fromDateQuery = new List<CourseDetailItem>();

        for (var offset = 0; offset <= futureDays; offset++)
        {
            progress?.Report(offset == 0 ? "正在同步今日课程..." : $"正在同步未来第 {offset} 天课程...");
            var dateStr = DateTime.Today.AddDays(offset).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            var dayDetails = await GetCourseByDateAsync(dateStr, cancellationToken);
            fromDateQuery.AddRange(dayDetails);
        }

        return MergeDetails(fromDetail, fromDateQuery);
    }

    public async Task<IClassSignResult> SignNowAsync(string courseSchedId, CancellationToken cancellationToken = default)
    {
        EnsureLoggedIn();
        if (string.IsNullOrWhiteSpace(courseSchedId))
        {
            throw new InvalidOperationException("courseSchedId 不能为空");
        }

        var timestamp = await GetServerTimestampAsync(cancellationToken);
        var url = WithQuery(Urls.ScanSign, new Dictionary<string, string>
        {
            ["courseSchedId"] = courseSchedId,
            ["timestamp"] = timestamp.ToString(CultureInfo.InvariantCulture)
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.TryAddWithoutValidation("sessionId", SessionId);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Linux; Android 13; M2012K11AC Build/TKQ1.221114.001; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/120.0.0.0 Mobile Safari/537.36 wxwork/4.1.30 MicroMessenger/7.0.1 Language/zh");
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id"] = User!.Id
        });

        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = ParseJsonOrNull(body);

        if (doc is null)
        {
            return new IClassSignResult("1", "签到接口返回非 JSON", (int)response.StatusCode, response.Headers.Location?.ToString(), body[..Math.Min(body.Length, 200)]);
        }

        return new IClassSignResult(
            Status: GetString(doc.RootElement, "STATUS"),
            ErrorMessage: GetString(doc.RootElement, "ERRMSG"),
            HttpStatusCode: (int)response.StatusCode,
            RedirectLocation: response.Headers.Location?.ToString(),
            RawPreview: body[..Math.Min(body.Length, 500)]);
    }

    public string BuildSignQrUrl(string courseSchedId)
    {
        if (_useVpn)
        {
            throw new InvalidOperationException("VPN 模式不支持生成二维码，请使用直接签到");
        }

        if (string.IsNullOrWhiteSpace(courseSchedId))
        {
            throw new InvalidOperationException("courseSchedId 不能为空");
        }

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ServerTimeOffsetMs;
        return WithQuery(Urls.ScanSign, new Dictionary<string, string>
        {
            ["courseSchedId"] = courseSchedId,
            ["timestamp"] = timestamp.ToString(CultureInfo.InvariantCulture)
        });
    }

    public void Dispose()
    {
        _http.Dispose();
        _handler.Dispose();
    }

    private async Task VpnLoginAsync(string username, string password, CancellationToken cancellationToken)
    {
        username = username.Trim();
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException("Username or password not provided");
        }

        using var loginPage = await _http.GetAsync(SsoVpnLogin, cancellationToken);
        var loginHtml = await loginPage.Content.ReadAsStringAsync(cancellationToken);
        if ((int)loginPage.StatusCode >= 500)
        {
            throw new InvalidOperationException($"Server error: {(int)loginPage.StatusCode}");
        }

        var execution = ExtractExecution(loginHtml);
        if (string.IsNullOrWhiteSpace(execution))
        {
            throw new InvalidOperationException("Could not find execution parameter");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, SsoVpnLogin);
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        request.Headers.TryAddWithoutValidation("Referer", SsoVpnLogin);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = username,
            ["password"] = password,
            ["submit"] = "登录",
            ["type"] = "username_password",
            ["execution"] = execution,
            ["_eventId"] = "submit"
        });

        using var loginResponse = await _http.SendAsync(request, cancellationToken);
        await HandleLoginResponseAsync(loginResponse, cancellationToken);
    }

    private async Task HandleLoginResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException("登录失败：账号或密码错误，或密码过弱需先修改后再登录");
        }

        if (IsRedirect(response.StatusCode))
        {
            var redirectUrl = response.Headers.Location?.ToString();
            if (string.IsNullOrWhiteSpace(redirectUrl))
            {
                throw new InvalidOperationException("登录跳转缺少重定向地址");
            }

            var finalResponse = await FollowGetRedirectsAsync(ResolveUrl(response.RequestMessage?.RequestUri?.ToString() ?? SsoVpnLogin, redirectUrl), cancellationToken);
            await FinalizeLoginAsync(finalResponse, cancellationToken);
            finalResponse.Dispose();
            return;
        }

        await FinalizeLoginAsync(response, cancellationToken);
    }

    private async Task FinalizeLoginAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? $"status {(int)response.StatusCode}";
        if (LooksLikeIclassUrl(finalUrl))
        {
            return;
        }

        if (LooksLikeVpnPortalHome(finalUrl))
        {
            await EnterIclassServiceAsync(cancellationToken);
            return;
        }

        throw new InvalidOperationException($"登录失败，最终 URL: {finalUrl}");
    }

    private async Task EnterIclassServiceAsync(CancellationToken cancellationToken)
    {
        using var response = await FollowGetRedirectsAsync($"{Urls.ServiceHome.TrimEnd('/')}/", cancellationToken);
        var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? $"status {(int)response.StatusCode}";
        if (!LooksLikeIclassUrl(finalUrl))
        {
            throw new InvalidOperationException($"VPN 登录后进入 iClass 失败，最终 URL: {finalUrl}");
        }
    }

    private async Task<string> ResolveLoginNameAsync(string fallback, CancellationToken cancellationToken)
    {
        var current = _useVpn ? WebVpnUrl.Wrap(MyCenterUrl) : MyCenterUrl;
        for (var i = 0; i < 8; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await _http.SendAsync(request, cancellationToken);
            var requestUrl = WebVpnUrl.Unwrap(response.RequestMessage?.RequestUri?.ToString() ?? current);
            var loginName = ExtractLoginName(requestUrl);
            if (!string.IsNullOrWhiteSpace(loginName))
            {
                return loginName;
            }

            var location = response.Headers.Location?.ToString();
            if (!string.IsNullOrWhiteSpace(location))
            {
                var resolved = ResolveUrl(current, location);
                var normalized = WebVpnUrl.Unwrap(resolved);
                loginName = ExtractLoginName(normalized);
                if (!string.IsNullOrWhiteSpace(loginName))
                {
                    return loginName;
                }

                if (IsRedirect(response.StatusCode))
                {
                    current = _useVpn ? WebVpnUrl.Wrap(normalized) : normalized;
                    continue;
                }
            }

            break;
        }

        return fallback;
    }

    private async Task<HttpResponseMessage> FollowGetRedirectsAsync(string url, CancellationToken cancellationToken)
    {
        var current = url;
        for (var i = 0; i < 10; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, current);
            var response = await _http.SendAsync(request, cancellationToken);
            if (!IsRedirect(response.StatusCode))
            {
                return response;
            }

            var location = response.Headers.Location?.ToString();
            response.Dispose();
            if (string.IsNullOrWhiteSpace(location))
            {
                throw new InvalidOperationException("重定向缺少 Location");
            }

            current = ResolveUrl(current, location);
        }

        throw new InvalidOperationException("重定向次数过多");
    }

    private async Task FetchUserInfoAsync(string username, CancellationToken cancellationToken)
    {
        var url = WithQuery(Urls.UserLogin, new Dictionary<string, string>
        {
            ["phone"] = username,
            ["password"] = string.Empty,
            ["verificationType"] = "2",
            ["verificationUrl"] = string.Empty,
            ["userLevel"] = "1"
        });

        using var response = await _http.GetAsync(url, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        SyncServerTime(response);

        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException($"请求 iClass API 失败，HTTP 状态: {(int)response.StatusCode}");
        }

        using var doc = ParseJsonOrNull(body);
        if (doc is null)
        {
            throw new InvalidOperationException("iClass API 返回了无法解析的 JSON");
        }

        if (GetString(doc.RootElement, "STATUS") != "0")
        {
            throw new IClassApiException(
                GetString(doc.RootElement, "ERRCODE"),
                GetString(doc.RootElement, "ERRMSG"),
                $"iClass API 返回错误: {body[..Math.Min(body.Length, 300)]}");
        }

        if (!doc.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"iClass API 返回的用户信息格式异常: {body[..Math.Min(body.Length, 300)]}");
        }

        var userId = GetString(result, "id");
        var sessionId = GetString(result, "sessionId");
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(sessionId))
        {
            throw new InvalidOperationException("登录成功但用户信息缺少 userId 或 sessionId");
        }

        User = new IClassUser(
            Id: userId,
            DisplayName: Coalesce(GetString(result, "realName"), GetString(result, "name"), username));
        SessionId = sessionId;
    }

    private async Task FetchUserInfoWithCandidatesAsync(string studentId, CancellationToken cancellationToken)
    {
        var candidates = new List<string> { studentId };
        var resolvedLoginName = await ResolveLoginNameAsync(studentId, cancellationToken);
        if (!resolvedLoginName.Equals(studentId, StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(resolvedLoginName);
        }

        var hashedLoginName = BuildMd5Base64LoginName(studentId);
        if (!candidates.Contains(hashedLoginName, StringComparer.OrdinalIgnoreCase))
        {
            candidates.Add(hashedLoginName);
        }

        IClassApiException? lastApiError = null;
        foreach (var candidate in candidates.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                await FetchUserInfoAsync(candidate, cancellationToken);
                return;
            }
            catch (IClassApiException ex) when (ex.ErrorCode == "106")
            {
                lastApiError = ex;
            }
        }

        throw new InvalidOperationException(lastApiError?.ErrorMessage is { Length: > 0 }
            ? $"iClass API 返回错误: {lastApiError.ErrorMessage}"
            : "iClass 不接受当前学号作为登录名，请尝试 WebVPN 模式或确认学校接口是否已调整。");
    }

    private async Task<long> GetServerTimestampAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Urls.Timestamp);
            if (!string.IsNullOrWhiteSpace(SessionId))
            {
                request.Headers.TryAddWithoutValidation("sessionId", SessionId);
            }

            using var response = await _http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return FallbackTimestamp();
            }

            using var doc = ParseJsonOrNull(body);
            if (doc is not null &&
                doc.RootElement.TryGetProperty("timestamp", out var timestampElement) &&
                long.TryParse(GetElementString(timestampElement), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }
        catch
        {
            // Fall back to the Date-header offset synced during login.
        }

        return FallbackTimestamp();
    }

    private void SyncServerTime(HttpResponseMessage response)
    {
        if (response.Headers.Date is not { } serverDate)
        {
            ServerTimeOffsetMs = 0;
            return;
        }

        var serverMs = serverDate.ToUnixTimeMilliseconds();
        var localMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var rawOffset = serverMs - localMs;
        ServerTimeOffsetMs = _useVpn ? rawOffset + VpnOffsetCorrectionMs : rawOffset;
    }

    private long FallbackTimestamp() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ServerTimeOffsetMs;

    private void EnsureLoggedIn()
    {
        if (User is null || string.IsNullOrWhiteSpace(SessionId))
        {
            throw new InvalidOperationException("缺少 userId 或 sessionId，请先登录并获取用户信息");
        }
    }

    private static IReadOnlyList<CourseDetailItem> MergeDetails(IEnumerable<CourseDetailItem> fromDetail, IEnumerable<CourseDetailItem> fromDateQuery)
    {
        var merged = new List<CourseDetailItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in fromDetail.Concat(fromDateQuery))
        {
            var key = !string.IsNullOrWhiteSpace(item.CourseSchedId)
                ? $"sched:{item.CourseSchedId}"
                : $"fallback:{item.Id}|{item.Date}|{item.Name}";
            if (seen.Add(key))
            {
                merged.Add(item);
            }
        }

        return new ReadOnlyCollection<CourseDetailItem>(merged);
    }

    private static string NormalizeDateDisplay(string raw)
    {
        var value = (raw ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var digits = Regex.Replace(value, "\\D", string.Empty);
        if (digits.Length >= 8)
        {
            return $"{digits[..4]}-{digits.Substring(4, 2)}-{digits.Substring(6, 2)}";
        }

        return value;
    }

    private static string NormalizeTimeDisplay(string raw)
    {
        var value = (raw ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var timePart = value.Contains(' ', StringComparison.Ordinal) ? value[(value.IndexOf(' ', StringComparison.Ordinal) + 1)..] : value;
        var match = Regex.Match(timePart, "^(\\d{1,2}):(\\d{2})");
        return match.Success ? $"{match.Groups[1].Value.PadLeft(2, '0')}:{match.Groups[2].Value}" : timePart;
    }

    private static bool IsValidDateStr(string dateStr)
    {
        return DateTime.TryParseExact(dateStr, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }

    private static string ExtractExecution(string html)
    {
        foreach (Match match in Regex.Matches(html, "<input\\b[^>]*>", RegexOptions.IgnoreCase))
        {
            var tag = match.Value;
            if (!Regex.IsMatch(tag, "\\bname\\s*=\\s*['\"]execution['\"]", RegexOptions.IgnoreCase))
            {
                continue;
            }

            var valueMatch = Regex.Match(tag, "\\bvalue\\s*=\\s*['\"]([^'\"]+)['\"]", RegexOptions.IgnoreCase);
            if (valueMatch.Success)
            {
                return WebUtility.HtmlDecode(valueMatch.Groups[1].Value);
            }
        }

        return string.Empty;
    }

    private static JsonDocument? ParseJsonOrNull(string body)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch
        {
            return null;
        }
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return string.Empty;
        }

        return GetElementString(value);
    }

    private static string GetElementString(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty
        };
    }

    private static string WithQuery(string url, IReadOnlyDictionary<string, string> query)
    {
        var builder = new StringBuilder(url);
        builder.Append(url.Contains('?', StringComparison.Ordinal) ? '&' : '?');
        builder.Append(string.Join("&", query.Select(pair => $"{Escape(pair.Key)}={Escape(pair.Value)}")));
        return builder.ToString();
    }

    private static string Escape(string value) => Uri.EscapeDataString(value ?? string.Empty);

    private static string Coalesce(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return code is 301 or 302 or 303 or 307 or 308;
    }

    private static string ResolveUrl(string baseUrl, string location)
    {
        return Uri.TryCreate(location, UriKind.Absolute, out var absolute)
            ? absolute.ToString()
            : new Uri(new Uri(baseUrl), location).ToString();
    }

    private static string ExtractLoginName(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            return string.Empty;
        }

        foreach (var part in parsed.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && pair[0].Equals("loginName", StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(pair[1]);
            }
        }

        return string.Empty;
    }

    private static string BuildMd5Base64LoginName(string studentId)
    {
        var md5Hex = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(studentId.Trim())));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(md5Hex));
    }

    private static bool LooksLikeIclassUrl(string url)
    {
        var normalized = WebVpnUrl.Unwrap(url);
        return normalized.Contains("iclass.buaa.edu.cn", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeVpnPortalHome(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        return WebVpnUrl.IsGatewayUrl(url)
            && !parsed.AbsolutePath.Contains("/login", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record IClassUrls(
        string ServiceHome,
        string UserLogin,
        string CourseList,
        string SemesterList,
        string CourseSignDetail,
        string ScanSign,
        string Timestamp,
        string CourseScheduleByDate)
    {
        public IClassUrls WrapForWebVpn()
        {
            return new IClassUrls(
                WebVpnUrl.Wrap(ServiceHome),
                WebVpnUrl.Wrap(UserLogin),
                WebVpnUrl.Wrap(CourseList),
                WebVpnUrl.Wrap(SemesterList),
                WebVpnUrl.Wrap(CourseSignDetail),
                WebVpnUrl.Wrap(ScanSign),
                WebVpnUrl.Wrap(Timestamp),
                WebVpnUrl.Wrap(CourseScheduleByDate));
        }
    }
}

public sealed class IClassApiException(string errorCode, string errorMessage, string message) : InvalidOperationException(message)
{
    public string ErrorCode { get; } = errorCode;

    public string ErrorMessage { get; } = errorMessage;
}

public sealed record IClassLoginInput(string StudentId, string? VpnUsername, string? VpnPassword);

public sealed record IClassLoginResult(string UserId, string UserName, string SessionId, bool UseVpn);

public sealed record IClassUser(string Id, string DisplayName);

public sealed record CourseItem(string Name, string Id);

public sealed record CourseDetailItem(
    string Name,
    string Id,
    string CourseSchedId,
    string Date,
    string StartTime,
    string EndTime,
    string SignStatus);

public sealed record IClassSignResult(
    string Status,
    string ErrorMessage,
    int HttpStatusCode,
    string? RedirectLocation,
    string RawPreview)
{
    public bool IsSuccess => Status == "0";

    public string DisplayMessage => IsSuccess ? "签到成功" : (string.IsNullOrWhiteSpace(ErrorMessage) ? "已提交，返回状态未确认" : ErrorMessage);
}
