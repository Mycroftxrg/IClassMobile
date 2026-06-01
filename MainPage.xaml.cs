using System.Text;
using IClassMobile.Services;
using Microsoft.Maui.Controls.Shapes;

namespace IClassMobile;

public partial class MainPage : ContentPage
{
    private const string DefaultStudentId = "";

    private static readonly string[] Weekdays = ["周一", "周二", "周三", "周四", "周五", "周六", "周日"];
    private static readonly TimeSlot[] TimeSlots =
    [
        new("morning", "上午", "08:00-12:15"),
        new("afternoon", "下午", "14:00-18:15"),
        new("night", "晚上", "19:00-22:25")
    ];

    private static readonly Color Primary = Color.FromArgb("#2563EB");
    private static readonly Color TextStrong = Color.FromArgb("#0F172A");
    private static readonly Color TextMuted = Color.FromArgb("#64748B");
    private static readonly Color PanelRaised = Color.FromArgb("#FFFFFF");
    private static readonly Color PanelAlt = Color.FromArgb("#F8FAFC");
    private static readonly Color ControlBg = Color.FromArgb("#EEF4FA");
    private static readonly Color BorderSoft = Color.FromArgb("#D9E2EC");
    private static readonly Color AccentSoft = Color.FromArgb("#CCFBF1");
    private static readonly Color AccentText = Color.FromArgb("#0F766E");
    private static readonly Color WarningSoft = Color.FromArgb("#FEF3C7");
    private static readonly Color WarningText = Color.FromArgb("#92400E");
    private static readonly Color Danger = Color.FromArgb("#DC2626");
    private static readonly Color Success = Color.FromArgb("#16A34A");

    private IClassClient? _client;
    private readonly AppUpdateService _updateService = new();
    private IClassLoginResult? _login;
    private readonly List<CourseDetailItem> _detailItems = [];
    private readonly Dictionary<string, CourseMessage> _courseMessages = [];
    private CourseDetailItem? _selectedCourse;
    private int _weekOffset;
    private bool _busy;
    private bool _autoLoginStarted;

    public MainPage()
    {
        InitializeComponent();
        StudentIdEntry.Text = DefaultStudentId;
        UpdateVpnMode();
        UpdateWeekRange();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (_autoLoginStarted || _login is not null)
        {
            return;
        }

        _autoLoginStarted = true;
        await Task.Delay(250);
        await CheckForUpdatesAsync(manual: false);

        // Auto-login is intentionally disabled so a personal student ID is never baked into releases.
    }

    private bool UseVpn => VpnSwitch.IsToggled;

    private async void OnLoginClicked(object? sender, EventArgs e)
    {
        await LoginWithCurrentInputAsync();
    }

    private async Task LoginWithCurrentInputAsync()
    {
        var studentId = StudentIdEntry.Text?.Trim() ?? string.Empty;
        var vpnUsername = VpnUsernameEntry.Text?.Trim() ?? string.Empty;
        var authPassword = AuthPasswordEntry.Text ?? string.Empty;

        LoginErrorLabel.IsVisible = false;
        if (string.IsNullOrWhiteSpace(studentId))
        {
            ShowLoginError("请输入学号");
            return;
        }

        if (string.IsNullOrWhiteSpace(authPassword))
        {
            ShowLoginError("请输入统一认证密码");
            return;
        }

        if (UseVpn && string.IsNullOrWhiteSpace(vpnUsername))
        {
            ShowLoginError("请输入 WebVPN 账号");
            return;
        }

        await RunBusyAsync("登录中...", async token =>
        {
            StudentIdEntry.Unfocus();
            VpnUsernameEntry.Unfocus();
            AuthPasswordEntry.Unfocus();

            _client?.Dispose();
            _client = new IClassClient(UseVpn);
            _login = await _client.LoginAsync(new IClassLoginInput(studentId, vpnUsername, authPassword), token);

            LoginPanel.IsVisible = false;
            SessionPanel.IsVisible = true;
            WeekPanel.IsVisible = true;
            SelectedPanel.IsVisible = false;
            ModeBadgeLabel.Text = UseVpn ? "VPN" : "直连";
            HeaderSubtitleLabel.Text = "课程签到页已打开";
            UserLabel.Text = $"欢迎，{_login.UserName}";
            StatusLabel.Text = "正在加载课程...";

            await LoadCalendarDataAsync(token);
        }, error => ShowLoginError(error));
    }

    private void OnLogoutClicked(object? sender, EventArgs e)
    {
        _client?.Dispose();
        _client = null;
        _login = null;
        _detailItems.Clear();
        _courseMessages.Clear();
        _selectedCourse = null;
        _weekOffset = 0;

        CalendarList.Clear();
        LoginPanel.IsVisible = true;
        SessionPanel.IsVisible = false;
        WeekPanel.IsVisible = false;
        SelectedPanel.IsVisible = false;
        QrPanel.IsVisible = false;
        LoginErrorLabel.IsVisible = false;
        StudentIdEntry.Text = DefaultStudentId;
        ModeBadgeLabel.Text = UseVpn ? "VPN" : "直连";
        HeaderSubtitleLabel.Text = "输入学号进入课程签到页";
        UpdateWeekRange();
    }

    private async void OnRefreshRequested(object? sender, EventArgs e)
    {
        try
        {
            if (_client is not null && _login is not null)
            {
                await LoadCalendarDataAsync(CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            StatusLabel.Text = ex.Message;
        }
        finally
        {
            RefreshHost.IsRefreshing = false;
        }
    }

    private void OnVpnToggled(object? sender, ToggledEventArgs e)
    {
        UpdateVpnMode();
    }

    private async void OnPreviousWeekClicked(object? sender, EventArgs e)
    {
        _weekOffset -= 1;
        await RenderCalendarAsync();
    }

    private async void OnNextWeekClicked(object? sender, EventArgs e)
    {
        _weekOffset += 1;
        await RenderCalendarAsync();
    }

    private async void OnCurrentWeekClicked(object? sender, EventArgs e)
    {
        _weekOffset = 0;
        await RenderCalendarAsync();
    }

    private async void OnSignClicked(object? sender, EventArgs e)
    {
        if (_client is null || _selectedCourse is null || _selectedCourse.SignStatus == "1")
        {
            return;
        }

        var courseSchedId = _selectedCourse.CourseSchedId;
        SetCourseMessage(courseSchedId, "签到中...", Primary);
        await RenderCalendarAsync();

        await RunBusyAsync("签到中...", async token =>
        {
            var result = await _client.SignNowAsync(courseSchedId, token);
            SetCourseMessage(
                courseSchedId,
                result.IsSuccess ? "签到成功" : $"签到结果: {result.DisplayMessage}",
                result.IsSuccess ? Success : Danger);

            if (result.IsSuccess)
            {
                ReplaceCourse(courseSchedId, _selectedCourse with { SignStatus = "1" });
                _selectedCourse = _selectedCourse with { SignStatus = "1" };
            }

            await RenderCalendarAsync();
        }, error =>
        {
            SetCourseMessage(courseSchedId, $"签到失败: {error}", Danger);
            _ = RenderCalendarAsync();
        });
    }

    private async void OnQrClicked(object? sender, EventArgs e)
    {
        if (_selectedCourse is null)
        {
            return;
        }

        SetCourseMessage(_selectedCourse.CourseSchedId, "正在生成二维码...", Primary);
        await RenderCalendarAsync();
        SetCourseMessage(_selectedCourse.CourseSchedId, "二维码已生成，请确认课程与时间", Success);
        await RenderCalendarAsync();
    }

    private async void OnCheckUpdateClicked(object? sender, EventArgs e)
    {
        await CheckForUpdatesAsync(manual: true);
    }

    private async Task LoadCalendarDataAsync(CancellationToken cancellationToken)
    {
        if (_client is null)
        {
            return;
        }

        var progress = new Progress<string>(message => StatusLabel.Text = message);
        var items = await _client.GetMergedCourseDetailsAsync(7, progress, cancellationToken);
        _detailItems.Clear();
        _detailItems.AddRange(items);
        StatusLabel.Text = $"已加载 {_detailItems.Count} 条课程记录";
        await RenderCalendarAsync();
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (CheckUpdateButton is null || UpdateStatusLabel is null)
        {
            return;
        }

        CheckUpdateButton.IsEnabled = false;
        UpdateStatusLabel.Text = "正在检查更新...";
        try
        {
            var result = await _updateService.CheckAsync();
            if (!result.IsUpdateAvailable)
            {
                UpdateStatusLabel.Text = $"当前已是最新版本 {result.CurrentVersion}";
                return;
            }

            if (!manual)
            {
                UpdateStatusLabel.Text = $"发现新版本 {result.LatestVersion}，点击检查可下载";
                return;
            }

            UpdateStatusLabel.Text = $"发现新版本 {result.LatestVersion}，准备下载安装包...";
            var confirm = await DisplayAlertAsync(
                "发现新版本",
                $"当前版本 {result.CurrentVersion}，最新版本 {result.LatestVersion}。是否下载并安装？",
                "下载",
                "稍后");
            if (!confirm)
            {
                UpdateStatusLabel.Text = $"发现新版本 {result.LatestVersion}，可稍后手动检查";
                return;
            }

            var progress = new Progress<double>(value =>
            {
                UpdateStatusLabel.Text = $"正在下载更新 {Math.Round(value * 100)}%";
            });
            var apkPath = await _updateService.DownloadApkAsync(result, progress);
            UpdateStatusLabel.Text = "下载完成，正在打开安装程序...";
            _updateService.LaunchInstaller(apkPath);
        }
        catch (Exception ex)
        {
            UpdateStatusLabel.Text = manual ? $"检查更新失败：{ex.Message}" : "自动检查更新失败，可稍后手动检查";
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private async Task RenderCalendarAsync()
    {
        UpdateWeekRange();
        CalendarList.Clear();
        QrPanel.IsVisible = false;

        var weekDates = GetWeekDates();
        var anyCourse = false;

        for (var dayIndex = 0; dayIndex < weekDates.Count; dayIndex++)
        {
            var date = weekDates[dayIndex];
            var dateYmd = date.ToString("yyyy-MM-dd");
            var dayItems = _detailItems
                .Where(item => item.Date == dateYmd)
                .OrderBy(item => ParseMinute(item.StartTime))
                .ThenBy(item => item.Name, StringComparer.CurrentCulture)
                .ToList();

            var dayPanel = new VerticalStackLayout { Spacing = 8 };
            var dayHeader = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto)
                }
            };
            dayHeader.Add(new Label
            {
                Text = $"{Weekdays[dayIndex]} {date:MM/dd}",
                FontFamily = "OpenSansSemibold",
                FontSize = 16,
                TextColor = TextStrong
            });
            dayHeader.Add(new Label
            {
                Text = dayItems.Count == 0 ? "无课程" : $"{dayItems.Count} 节",
                FontSize = 12,
                TextColor = TextMuted,
                VerticalTextAlignment = TextAlignment.Center
            }, 1);
            dayPanel.Add(dayHeader);

            if (dayItems.Count == 0)
            {
                dayPanel.Add(new Label
                {
                    Text = "暂无课程",
                    FontSize = 13,
                    TextColor = Color.FromArgb("#94A3B8"),
                    HorizontalTextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 4)
                });
            }
            else
            {
                anyCourse = true;
                foreach (var group in TimeSlots)
                {
                    var slotItems = dayItems.Where(item => GetSlotKey(item) == group.Key).ToList();
                    if (slotItems.Count == 0)
                    {
                        continue;
                    }

                    dayPanel.Add(new Label
                    {
                        Text = $"{group.Label} {group.Range}",
                        FontSize = 12,
                        TextColor = TextMuted,
                        Margin = new Thickness(0, 2, 0, 0)
                    });

                    foreach (var item in slotItems)
                    {
                        dayPanel.Add(BuildCourseCard(item));
                    }
                }
            }

            CalendarList.Add(new Border
            {
                Stroke = BorderSoft,
                StrokeThickness = 1,
                BackgroundColor = PanelRaised,
                Padding = 14,
                StrokeShape = new RoundRectangle { CornerRadius = 8 },
                Content = dayPanel
            });
        }

        if (!anyCourse)
        {
            StatusLabel.Text = "本周暂无课程，仍可切换周次查看";
        }

        UpdateSelectedPanel();
        await Task.CompletedTask;
    }

    private View BuildCourseCard(CourseDetailItem item)
    {
        var signed = item.SignStatus == "1";
        var selected = _selectedCourse?.CourseSchedId == item.CourseSchedId;

        var statusChip = new Border
        {
            Padding = new Thickness(9, 5),
            BackgroundColor = signed ? AccentSoft : WarningSoft,
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 8 },
            VerticalOptions = LayoutOptions.Start,
            Content = new Label
            {
                Text = signed ? "已签到" : "待签到",
                FontFamily = "OpenSansSemibold",
                FontSize = 12,
                TextColor = signed ? AccentText : WarningText
            }
        };

        var summary = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            },
            ColumnSpacing = 10
        };

        summary.Add(new VerticalStackLayout
        {
            Spacing = 4,
            Children =
            {
                new Label
                {
                    Text = item.Name,
                    FontFamily = "OpenSansSemibold",
                    FontSize = 15,
                    TextColor = TextStrong,
                    MaxLines = 2
                },
                new Label
                {
                    Text = $"{item.Date}  {item.StartTime}-{item.EndTime}",
                    FontSize = 12,
                    TextColor = TextMuted
                }
            }
        });
        summary.Add(statusChip, 1);

        var stack = new VerticalStackLayout { Spacing = selected ? 10 : 0 };
        stack.Add(summary);

        if (selected)
        {
            stack.Add(BuildCourseDetail(item, signed));
        }

        var card = new Border
        {
            Padding = new Thickness(12, 10),
            MinimumHeightRequest = 62,
            BackgroundColor = PanelRaised,
            Stroke = selected ? Primary : BorderSoft,
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 8 },
            Content = stack
        };

        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) =>
        {
            _selectedCourse = item;
            UpdateSelectedPanel();
            _ = RenderCalendarAsync();
        };
        card.GestureRecognizers.Add(tap);
        return card;
    }

    private View BuildCourseDetail(CourseDetailItem item, bool signed)
    {
        var detail = new VerticalStackLayout { Spacing = 10 };
        detail.Add(new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            },
            Children =
            {
                new Label
                {
                    Text = "签到信息",
                    FontFamily = "OpenSansSemibold",
                    FontSize = 14,
                    TextColor = TextStrong
                }
            }
        });

        detail.Add(BuildInfoRow("课程时间", $"{item.Date} {item.StartTime}-{item.EndTime}"));
        detail.Add(BuildInfoRow("签到状态", signed ? "已签到" : "未签到"));

        if (_courseMessages.TryGetValue(item.CourseSchedId, out var message))
        {
            detail.Add(new Label
            {
                Text = message.Text,
                FontSize = 13,
                TextColor = message.Color
            });
        }

        var signButton = new Button
        {
            Text = signed ? "已签到" : "直接签到",
            IsEnabled = !signed && !_busy,
            BackgroundColor = ControlBg,
            TextColor = signed ? TextMuted : TextStrong,
            MinimumHeightRequest = 44
        };
        signButton.Clicked += OnSignClicked;
        detail.Add(signButton);

        if (_login?.UseVpn == true)
        {
            detail.Add(new Label
            {
                Text = "VPN 模式下请使用直接签到。",
                FontSize = 12,
                TextColor = TextMuted
            });
        }
        else if (_client is not null)
        {
            try
            {
                var qrUrl = _client.BuildSignQrUrl(item.CourseSchedId);
                detail.Add(new Label
                {
                    Text = "签到二维码",
                    FontFamily = "OpenSansSemibold",
                    FontSize = 13,
                    TextColor = TextStrong
                });
                detail.Add(BuildQrCodeView(qrUrl));
            }
            catch (Exception ex)
            {
                detail.Add(new Label
                {
                    Text = ex.Message,
                    FontSize = 13,
                    TextColor = Danger
                });
            }
        }

        return new Border
        {
            Padding = 12,
            BackgroundColor = PanelAlt,
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 8 },
            Content = detail
        };
    }

    private static View BuildInfoRow(string label, string value)
    {
        var row = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star)
            },
            ColumnSpacing = 10
        };
        row.Add(new Label
        {
            Text = label,
            FontSize = 12,
            TextColor = TextMuted,
            WidthRequest = 58
        });
        row.Add(new Label
        {
            Text = value,
            FontSize = 12,
            TextColor = TextStrong,
            LineBreakMode = LineBreakMode.WordWrap
        }, 1);
        return row;
    }

    private static View BuildQrCodeView(string payload)
    {
        var matrix = SimpleQrCode.Encode(payload);
        var size = matrix.GetLength(0);
        var grid = new Grid
        {
            WidthRequest = 188,
            HeightRequest = 188,
            RowSpacing = 0,
            ColumnSpacing = 0,
            BackgroundColor = Colors.White,
            HorizontalOptions = LayoutOptions.Center
        };

        for (var i = 0; i < size; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Star });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
        }

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                if (!matrix[y, x])
                {
                    continue;
                }

                grid.Add(new BoxView
                {
                    Color = Colors.Black,
                    Margin = 0
                }, x, y);
            }
        }

        return new Border
        {
            Padding = 10,
            Stroke = BorderSoft,
            StrokeThickness = 1,
            BackgroundColor = Colors.White,
            StrokeShape = new RoundRectangle { CornerRadius = 8 },
            HorizontalOptions = LayoutOptions.Center,
            Content = grid
        };
    }

    private void UpdateSelectedPanel()
    {
        if (_login is null)
        {
            return;
        }

        if (_selectedCourse is null)
        {
            SelectedCourseLabel.Text = "未选择课程";
            SignButton.IsEnabled = false;
            QrButton.IsEnabled = false;
            return;
        }

        var signed = _selectedCourse.SignStatus == "1";
        SelectedCourseLabel.Text = $"{_selectedCourse.Name} ({_selectedCourse.Date} {_selectedCourse.StartTime}-{_selectedCourse.EndTime})";
        SignButton.Text = signed ? "已签到" : "直接签到";
        SignButton.IsEnabled = !signed && !_busy;
        QrButton.IsVisible = !_login.UseVpn;
        QrButton.IsEnabled = !_login.UseVpn && !_busy;
    }

    private void UpdateVpnMode()
    {
        VpnFieldsPanel.IsVisible = UseVpn;
        LoginButton.Text = UseVpn ? "通过 WebVPN 进入课程" : "进入课程";
        ModeBadgeLabel.Text = UseVpn ? "VPN" : "直连";
        LoginErrorLabel.IsVisible = false;
    }

    private void UpdateWeekRange()
    {
        var weekDates = GetWeekDates();
        var label = $"{weekDates[0]:M/d} - {weekDates[6]:M/d}";
        WeekRangeLabel.Text = _weekOffset switch
        {
            0 => $"{label} (本周)",
            > 0 => $"{label} ({_weekOffset}周后)",
            _ => $"{label} ({Math.Abs(_weekOffset)}周前)"
        };
    }

    private IReadOnlyList<DateTime> GetWeekDates()
    {
        var now = DateTime.Today;
        var day = (int)now.DayOfWeek;
        var mondayShift = day == 0 ? 6 : day - 1;
        var monday = now.AddDays(-mondayShift + _weekOffset * 7);
        return Enumerable.Range(0, 7).Select(index => monday.AddDays(index)).ToList();
    }

    private async Task RunBusyAsync(string status, Func<CancellationToken, Task> action, Action<string>? onError = null)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        BusyIndicator.IsVisible = true;
        BusyIndicator.IsRunning = true;
        LoginButton.IsEnabled = false;
        CheckUpdateButton.IsEnabled = false;
        SignButton.IsEnabled = false;
        QrButton.IsEnabled = false;
        if (SessionPanel.IsVisible)
        {
            StatusLabel.Text = status;
        }

        try
        {
            await action(CancellationToken.None);
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex.Message);
            if (SessionPanel.IsVisible)
            {
                StatusLabel.Text = ex.Message;
            }
        }
        finally
        {
            _busy = false;
            BusyIndicator.IsRunning = false;
            BusyIndicator.IsVisible = false;
            LoginButton.IsEnabled = true;
            CheckUpdateButton.IsEnabled = true;
            UpdateSelectedPanel();
        }
    }

    private void ReplaceCourse(string courseSchedId, CourseDetailItem replacement)
    {
        for (var i = 0; i < _detailItems.Count; i++)
        {
            if (_detailItems[i].CourseSchedId == courseSchedId)
            {
                _detailItems[i] = replacement;
            }
        }
    }

    private void SetCourseMessage(string courseSchedId, string text, Color color)
    {
        if (!string.IsNullOrWhiteSpace(courseSchedId))
        {
            _courseMessages[courseSchedId] = new CourseMessage(text, color);
        }
    }

    private void ShowLoginError(string message)
    {
        LoginErrorLabel.Text = message;
        LoginErrorLabel.IsVisible = true;
    }

    private static string GetSlotKey(CourseDetailItem item)
    {
        var hour = ParseHour(item.StartTime);
        if (hour >= 8 && hour < 12)
        {
            return "morning";
        }

        if (hour >= 14 && hour < 18)
        {
            return "afternoon";
        }

        return "night";
    }

    private static int ParseHour(string time)
    {
        var parts = (time ?? string.Empty).Split(':');
        return parts.Length > 0 && int.TryParse(parts[0], out var hour) ? hour : -1;
    }

    private static int ParseMinute(string time)
    {
        var parts = (time ?? string.Empty).Split(':');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var hour) || !int.TryParse(parts[1], out var minute))
        {
            return int.MaxValue;
        }

        return hour * 60 + minute;
    }

    private sealed record CourseMessage(string Text, Color Color);

    private sealed record TimeSlot(string Key, string Label, string Range);

    private sealed record QrSpec(int Version, int DataCodewords, int EccCodewordsPerBlock, int Blocks);

    private static class SimpleQrCode
    {
        private static readonly QrSpec[] Specs =
        [
            new(1, 19, 7, 1),
            new(2, 34, 10, 1),
            new(3, 55, 15, 1),
            new(4, 80, 20, 1),
            new(5, 108, 26, 1),
            new(6, 136, 18, 2),
            new(7, 156, 20, 2),
            new(8, 194, 24, 2),
            new(9, 232, 30, 2)
        ];

        public static bool[,] Encode(string text)
        {
            var data = Encoding.UTF8.GetBytes(text);
            var spec = Specs.FirstOrDefault(item => Fits(item, data.Length))
                ?? throw new InvalidOperationException("二维码内容过长，无法在当前版本中展示");
            var dataCodewords = MakeDataCodewords(data, spec);
            var allCodewords = AddErrorCorrectionAndInterleave(dataCodewords, spec);
            return DrawMatrix(allCodewords, spec.Version);
        }

        private static bool Fits(QrSpec spec, int byteCount)
        {
            var capacityBits = spec.DataCodewords * 8;
            var payloadBits = 4 + 8 + byteCount * 8;
            return payloadBits <= capacityBits;
        }

        private static byte[] MakeDataCodewords(byte[] data, QrSpec spec)
        {
            var bits = new List<int>();
            AppendBits(bits, 0b0100, 4);
            AppendBits(bits, data.Length, 8);
            foreach (var value in data)
            {
                AppendBits(bits, value, 8);
            }

            var capacityBits = spec.DataCodewords * 8;
            AppendBits(bits, 0, Math.Min(4, capacityBits - bits.Count));
            while (bits.Count % 8 != 0)
            {
                bits.Add(0);
            }

            var result = new List<byte>();
            for (var i = 0; i < bits.Count; i += 8)
            {
                var value = 0;
                for (var j = 0; j < 8; j++)
                {
                    value = (value << 1) | bits[i + j];
                }
                result.Add((byte)value);
            }

            for (var pad = 0; result.Count < spec.DataCodewords; pad++)
            {
                result.Add((byte)(pad % 2 == 0 ? 0xEC : 0x11));
            }

            return result.ToArray();
        }

        private static byte[] AddErrorCorrectionAndInterleave(byte[] dataCodewords, QrSpec spec)
        {
            var divisor = ReedSolomonComputeDivisor(spec.EccCodewordsPerBlock);
            var blockLength = spec.DataCodewords / spec.Blocks;
            var dataBlocks = new List<byte[]>();
            var eccBlocks = new List<byte[]>();

            for (var i = 0; i < spec.Blocks; i++)
            {
                var block = dataCodewords.Skip(i * blockLength).Take(blockLength).ToArray();
                dataBlocks.Add(block);
                eccBlocks.Add(ReedSolomonComputeRemainder(block, divisor));
            }

            var result = new List<byte>();
            for (var i = 0; i < blockLength; i++)
            {
                foreach (var block in dataBlocks)
                {
                    result.Add(block[i]);
                }
            }

            for (var i = 0; i < spec.EccCodewordsPerBlock; i++)
            {
                foreach (var block in eccBlocks)
                {
                    result.Add(block[i]);
                }
            }

            return result.ToArray();
        }

        private static bool[,] DrawMatrix(byte[] codewords, int version)
        {
            const int mask = 0;
            var size = version * 4 + 17;
            var modules = new bool[size, size];
            var isFunction = new bool[size, size];

            void SetFunction(int x, int y, bool black)
            {
                modules[y, x] = black;
                isFunction[y, x] = true;
            }

            void Reserve(int x, int y)
            {
                if (x >= 0 && x < size && y >= 0 && y < size)
                {
                    isFunction[y, x] = true;
                }
            }

            DrawFinderPattern(3, 3, SetFunction, size);
            DrawFinderPattern(size - 4, 3, SetFunction, size);
            DrawFinderPattern(3, size - 4, SetFunction, size);

            for (var i = 0; i < size; i++)
            {
                if (!isFunction[6, i])
                {
                    SetFunction(i, 6, i % 2 == 0);
                }
                if (!isFunction[i, 6])
                {
                    SetFunction(6, i, i % 2 == 0);
                }
            }

            foreach (var x in GetAlignmentPatternPositions(version, size))
            {
                foreach (var y in GetAlignmentPatternPositions(version, size))
                {
                    var cornerOverlap = (x == 6 && y == 6)
                        || (x == 6 && y == size - 7)
                        || (x == size - 7 && y == 6);
                    if (!cornerOverlap)
                    {
                        DrawAlignmentPattern(x, y, SetFunction);
                    }
                }
            }

            for (var i = 0; i < 9; i++)
            {
                Reserve(8, i);
                Reserve(i, 8);
            }
            for (var i = 0; i < 8; i++)
            {
                Reserve(size - 1 - i, 8);
                Reserve(8, size - 1 - i);
            }

            SetFunction(8, size - 8, true);

            if (version >= 7)
            {
                DrawVersion(version, SetFunction, size);
            }

            var dataBits = new List<int>(codewords.Length * 8);
            foreach (var codeword in codewords)
            {
                AppendBits(dataBits, codeword, 8);
            }

            var bitIndex = 0;
            var upward = true;
            for (var right = size - 1; right >= 1; right -= 2)
            {
                if (right == 6)
                {
                    right--;
                }

                for (var vert = 0; vert < size; vert++)
                {
                    var y = upward ? size - 1 - vert : vert;
                    for (var j = 0; j < 2; j++)
                    {
                        var x = right - j;
                        if (isFunction[y, x])
                        {
                            continue;
                        }

                        var bit = bitIndex < dataBits.Count && dataBits[bitIndex++] == 1;
                        var masked = bit ^ ShouldMask(mask, x, y);
                        modules[y, x] = masked;
                    }
                }

                upward = !upward;
            }

            DrawFormat(mask, SetFunction, size);
            return modules;
        }

        private static void DrawFinderPattern(int centerX, int centerY, Action<int, int, bool> setFunction, int size)
        {
            for (var dy = -4; dy <= 4; dy++)
            {
                for (var dx = -4; dx <= 4; dx++)
                {
                    var x = centerX + dx;
                    var y = centerY + dy;
                    if (x < 0 || x >= size || y < 0 || y >= size)
                    {
                        continue;
                    }

                    var distance = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    setFunction(x, y, distance != 2 && distance != 4);
                }
            }
        }

        private static void DrawAlignmentPattern(int centerX, int centerY, Action<int, int, bool> setFunction)
        {
            for (var dy = -2; dy <= 2; dy++)
            {
                for (var dx = -2; dx <= 2; dx++)
                {
                    var distance = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    setFunction(centerX + dx, centerY + dy, distance != 1);
                }
            }
        }

        private static int[] GetAlignmentPatternPositions(int version, int size)
        {
            return version switch
            {
                1 => [],
                2 => [6, 18],
                3 => [6, 22],
                4 => [6, 26],
                5 => [6, 30],
                6 => [6, 34],
                7 => [6, 22, 38],
                8 => [6, 24, 42],
                9 => [6, 26, 46],
                _ => [6, size - 7]
            };
        }

        private static void DrawFormat(int mask, Action<int, int, bool> setFunction, int size)
        {
            const int eclFormatBits = 1;
            var data = (eclFormatBits << 3) | mask;
            var bits = (data << 10) | GetBchRemainder(data, 0x537, 10);
            bits ^= 0x5412;

            for (var i = 0; i <= 5; i++)
            {
                setFunction(8, i, GetBit(bits, i));
            }
            setFunction(8, 7, GetBit(bits, 6));
            setFunction(8, 8, GetBit(bits, 7));
            setFunction(7, 8, GetBit(bits, 8));
            for (var i = 9; i < 15; i++)
            {
                setFunction(14 - i, 8, GetBit(bits, i));
            }

            for (var i = 0; i < 8; i++)
            {
                setFunction(size - 1 - i, 8, GetBit(bits, i));
            }
            for (var i = 8; i < 15; i++)
            {
                setFunction(8, size - 15 + i, GetBit(bits, i));
            }
            setFunction(8, size - 8, true);
        }

        private static void DrawVersion(int version, Action<int, int, bool> setFunction, int size)
        {
            var bits = (version << 12) | GetBchRemainder(version, 0x1F25, 12);
            for (var i = 0; i < 18; i++)
            {
                var bit = GetBit(bits, i);
                var a = size - 11 + i % 3;
                var b = i / 3;
                setFunction(a, b, bit);
                setFunction(b, a, bit);
            }
        }

        private static int GetBchRemainder(int value, int polynomial, int degree)
        {
            var result = value << degree;
            for (var i = HighestBit(result); i >= degree; i--)
            {
                if (((result >> i) & 1) != 0)
                {
                    result ^= polynomial << (i - degree);
                }
            }

            return result & ((1 << degree) - 1);
        }

        private static int HighestBit(int value)
        {
            for (var i = 31; i >= 0; i--)
            {
                if (((value >> i) & 1) != 0)
                {
                    return i;
                }
            }

            return -1;
        }

        private static bool ShouldMask(int mask, int x, int y)
        {
            return mask switch
            {
                0 => (x + y) % 2 == 0,
                _ => false
            };
        }

        private static void AppendBits(List<int> bits, int value, int length)
        {
            for (var i = length - 1; i >= 0; i--)
            {
                bits.Add((value >> i) & 1);
            }
        }

        private static byte[] ReedSolomonComputeDivisor(int degree)
        {
            var result = new byte[degree];
            result[degree - 1] = 1;
            var root = 1;

            for (var i = 0; i < degree; i++)
            {
                for (var j = 0; j < result.Length; j++)
                {
                    result[j] = GaloisMultiply(result[j], root);
                    if (j + 1 < result.Length)
                    {
                        result[j] ^= result[j + 1];
                    }
                }
                root = GaloisMultiply(root, 0x02);
            }

            return result;
        }

        private static byte[] ReedSolomonComputeRemainder(byte[] data, byte[] divisor)
        {
            var result = new byte[divisor.Length];
            foreach (var value in data)
            {
                var factor = value ^ result[0];
                Array.Copy(result, 1, result, 0, result.Length - 1);
                result[^1] = 0;

                for (var i = 0; i < result.Length; i++)
                {
                    result[i] ^= GaloisMultiply(divisor[i], factor);
                }
            }

            return result;
        }

        private static byte GaloisMultiply(int x, int y)
        {
            var result = 0;
            for (var i = 7; i >= 0; i--)
            {
                result = (result << 1) ^ ((result >> 7) * 0x11D);
                result ^= (((y >> i) & 1) != 0) ? x : 0;
            }

            return (byte)result;
        }

        private static bool GetBit(int value, int index)
        {
            return ((value >> index) & 1) != 0;
        }
    }
}
