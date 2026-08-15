using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Timers;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared.Enums;
using EveningSelfStudyClock.Models;
using EveningSelfStudyClock.Services;
using EveningSelfStudyClock.Views;
using Microsoft.Extensions.DependencyInjection;

namespace EveningSelfStudyClock.ViewModels;

public class FullScreenClockViewModel : INotifyPropertyChanged
{
    private readonly PluginSettings _settings;
    private readonly DecibelMeterService _decibelService;
    private readonly IServiceProvider _serviceProvider;
    private System.Timers.Timer? _updateTimer;
    private FullScreenClockWindow? _window;

    /// <summary>
    /// CI 里设置的时间偏移（秒）。正 = 打铃提前，负 = 打铃延后。
    /// </summary>
    private int _timeOffsetSeconds;

    // 计数显示颜色：一般 = 黄，吵闹 = 红
    private static readonly IBrush CountNormalBrush = new SolidColorBrush(Color.Parse("#ffff44"));
    private static readonly IBrush CountNoisyBrush = new SolidColorBrush(Color.Parse("#ff5555"));

    private string _currentTime = "00:00:00";
    private string _currentDate = "";
    private string _currentCourseName = "";
    private int _normalCount;       // 一般 次数
    private int _noisyCount;        // 吵闹+嘈杂 次数
    private string _currentClassName = "";
    private readonly List<ClassSummary> _classSummaries = new();
    private bool _isWindowVisible;
    private bool _isInBreak;
    private DateTime _classStartTime;
    private string _courseInfoText = "";
    private List<TimeSlot> _todaySlots = new();
    private System.Timers.Timer? _antiScreensaverTimer;

    public FullScreenClockViewModel(
        PluginSettings settings,
        DecibelMeterService decibelService,
        IServiceProvider serviceProvider)
    {
        _settings = settings;
        _decibelService = decibelService;
        _serviceProvider = serviceProvider;

        _decibelService.PropertyChanged += OnDecibelPropertyChanged;
        _settings.PropertyChanged += OnSettingsPropertyChanged;
    }

    /// <summary>
    /// CI 的"虚拟本地时间"（含时间偏移），用于课程定位/保护计时。
    /// 大时钟显示仍用真实北京时间，学生看到的是准确时间。
    /// 惰性解析 IExactTimeService，失败则降级为 DateTime.Now。
    /// </summary>
    private DateTime NowVirtual
    {
        get
        {
            try
            {
                var svc = _serviceProvider.GetService<IExactTimeService>();
                return svc?.GetCurrentLocalDateTime() ?? DateTime.Now;
            }
            catch { return DateTime.Now; }
        }
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(PluginSettings.NoisyCooldownSeconds)
            or nameof(PluginSettings.SkipFirst3Min))
        {
            OnPropertyChanged(nameof(RulesText));
        }
    }

    /// <summary>
    /// 分贝属性变更：转发显示属性给全屏界面绑定，同时驱动计数逻辑。
    /// </summary>
    private void OnDecibelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(DecibelMeterService.NoiseLevelText)
            or nameof(DecibelMeterService.NoiseLevelEmoji)
            or nameof(DecibelMeterService.NoiseLevelProgress))
        {
            OnPropertyChanged(args.PropertyName!);
        }

        // 等级变化 → 计数
        if (args.PropertyName == nameof(DecibelMeterService.NoiseLevelText))
        {
            TryCountNoisy(_decibelService.NoiseLevelText);
        }
    }

    private void TryCountNoisy(string level)
    {
        if (!_settings.ShowNoisyCounter) return;
        if (_isInBreak) return;

        // 升级：登记的「一般」在 30 秒窗口内达到吵闹/嘈杂 → 该次改为吵闹
        if (_pendingNormalDeadline != default
            && (level.Contains("吵闹") || level.Contains("嘈杂"))
            && DateTime.Now <= _pendingNormalDeadline)
        {
            _normalCount--;
            _noisyCount++;
            _pendingNormalDeadline = default;
            _lastNoisyTime = DateTime.Now; // 冷却从吵闹时刻重新计时，避免重复计数
            UpdateNoisyDisplay();
            return;
        }

        // 上课前 N 分钟不计数（用 CI 虚拟时间，与实际打铃对齐）
        if (_settings.SkipFirst3Min && (NowVirtual - _classStartTime).TotalSeconds < 180) return;

        // 冷却
        var elapsed = (DateTime.Now - _lastNoisyTime).TotalSeconds;
        if (elapsed < _settings.NoisyCooldownSeconds) return;

        bool counted = false;

        // 分类计数
        if (level.Contains("吵闹") || level.Contains("嘈杂"))
        {
            _noisyCount++;
            _pendingNormalDeadline = default; // 直接到吵闹，作废任何遗留的待升级标记
            _lastNoisyTime = DateTime.Now;
            counted = true;
        }
        else if (level == "一般")
        {
            _normalCount++;
            _pendingNormalDeadline = DateTime.Now.AddSeconds(30); // 开启 30 秒升级窗口
            _lastNoisyTime = DateTime.Now;
            counted = true;
        }

        if (counted)
            UpdateNoisyDisplay();
    }

    public string CurrentTime
    {
        get => _currentTime;
        set { _currentTime = value; OnPropertyChanged(); }
    }

    public string CurrentDate
    {
        get => _currentDate;
        set { _currentDate = value; OnPropertyChanged(); }
    }

    public string CurrentCourseName
    {
        get => _currentCourseName;
        set { _currentCourseName = value; OnPropertyChanged(); }
    }

    public bool IsWindowVisible
    {
        get => _isWindowVisible;
        private set { _isWindowVisible = value; OnPropertyChanged(); }
    }

    public string BackgroundColor => _settings.BackgroundColor;
    public string FontColor => _settings.FontColor;
    public string AccentColor => _settings.AccentColor;
    public string NoiseLevelText => _decibelService.NoiseLevelText;
    public string NoiseLevelEmoji => _decibelService.NoiseLevelEmoji;
    public double NoiseLevelProgress => _decibelService.NoiseLevelProgress;
    public int DisplayLevel => _decibelService.DisplayLevel;
    /// <summary>
    /// 计数区域的分段显示：课程名/一般（黄）/吵闹（红）/保护提示。
    /// </summary>
    public ObservableCollection<NoisyDisplaySegment> NoisyDisplaySegments { get; } = new();

    private bool _showDecibelMeter = true;
    private bool _showCourseInfo = true;
    private int _clockFontSize = 180;

    public bool ShowDecibelMeter
    {
        get => _showDecibelMeter;
        set { _showDecibelMeter = value; OnPropertyChanged(); }
    }

    public bool ShowCourseInfo
    {
        get => _showCourseInfo;
        set { _showCourseInfo = value; OnPropertyChanged(); }
    }

    public string CourseInfoText
    {
        get => _courseInfoText;
        set { _courseInfoText = value; OnPropertyChanged(); }
    }

    private DateTime _lastNoisyTime;

    /// <summary>
    /// 待升级「一般」的 30 秒窗口截止时间。default 表示当前没有待升级的一般。
    /// </summary>
    private DateTime _pendingNormalDeadline;

    public int ClockFontSize
    {
        get => _clockFontSize;
        set { _clockFontSize = value; OnPropertyChanged(); }
    }

    public string WindowTitle => _settings.WindowTitle;

    /// <summary>
    /// 全屏界面底部的记录规则 + 免责声明（跟随设置动态生成）
    /// </summary>
    public string RulesText
    {
        get
        {
            var parts = new List<string>();
            parts.Add("一般记一次「一般」，30 秒内升级为吵闹则改记「吵闹」");
            parts.Add("吵闹/嘈杂记一次「吵闹」");
            parts.Add($"每次计数冷却 {_settings.NoisyCooldownSeconds} 秒");
            if (_settings.SkipFirst3Min) parts.Add("上课开始后 3 分钟内不记录");
            return string.Join("，", parts)
                + "　|　⚠ 分贝仅供参考，可能受风扇、空调、开关门、脚步声等环境杂音影响";
        }
    }

    public void Show()
    {
        Dispatcher.UIThread.Post(() =>
        {
            // 必须在创建窗口前同步，否则绑定读到的是旧值
            ShowDecibelMeter = _settings.ShowDecibelMeter;
            ShowCourseInfo = _settings.ShowCourseInfo;
            ClockFontSize = _settings.ClockFontSize;

            if (_window == null)
            {
                _window = new FullScreenClockWindow { DataContext = this };
                _window.Closed += (_, _) =>
                {
                    IsWindowVisible = false;
                    StopUpdateTimer();
                    _decibelService.StopMonitoring();
                    SetMainWindowVisible(true); // 无论何种方式关闭，都恢复主界面
                    _window = null;
                };
            }

            if (_window.IsVisible) { RefreshAll(); return; }

            // 重置新一轮记录
            _normalCount = 0;
            _noisyCount = 0;
            _currentClassName = "";
            _classSummaries.Clear();
            _isInBreak = false;
            _lastNoisyTime = DateTime.MinValue;
            _pendingNormalDeadline = default;
            NoisyDisplaySegments.Clear();
            CourseInfoText = "";
            LoadTodaySchedule();
            SubscribeToClassEvents();

            // 重新订阅分贝事件（先退订再订阅，保证不累积）
            _decibelService.PropertyChanged -= OnDecibelPropertyChanged;
            _decibelService.PropertyChanged += OnDecibelPropertyChanged;

            StartUpdateTimer();
            StartAntiScreensaver();
            _decibelService.StartMonitoring();
            RefreshAll();

            _window.WindowState = WindowState.FullScreen;
            _window.Show();
            IsWindowVisible = true;
            SetMainWindowVisible(false);
        });
    }

    public void Hide()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _window?.Hide();
            StopUpdateTimer();
            StopAntiScreensaver();
            _decibelService.StopMonitoring();
            IsWindowVisible = false;
            SetMainWindowVisible(true);
        });
    }

    private void StartUpdateTimer()
    {
        if (_updateTimer != null) return;
        _updateTimer = new System.Timers.Timer(200);
        _updateTimer.Elapsed += (_, _) => Dispatcher.UIThread.Post(RefreshAll);
        _updateTimer.AutoReset = true;
        _updateTimer.Start();
    }

    private void StopUpdateTimer()
    {
        if (_updateTimer == null) return;
        _updateTimer.Stop();
        _updateTimer.Dispose();
        _updateTimer = null;
    }

    private void RefreshAll()
    {
        var now = DateTime.Now;
        CurrentTime = now.ToString("HH:mm:ss");
        CurrentDate = now.ToString("yyyy年M月d日 dddd");

        try
        {
            var lessonsService = _serviceProvider.GetRequiredService<ILessonsService>();
            CurrentCourseName = lessonsService.CurrentSubject?.Name ?? "";
        }
        catch { }

        // 课程定位用 CI 虚拟时间（与实际打铃对齐）
        UpdateCourseInfo(NowVirtual);

        // 刷新计数区域（保护倒计时需要每秒更新）
        UpdateNoisyDisplay();
    }

    private void LoadTodaySchedule()
    {
        _todaySlots.Clear();
        try
        {
            var dataDir = Path.GetFullPath(Path.Combine(Plugin.ConfigFolder!, "..", "..", ".."));
            var settingsPath = Path.Combine(dataDir, "Settings.json");
            var profileFileName = "7.json";
            if (File.Exists(settingsPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
                if (doc.RootElement.TryGetProperty("SelectedProfile", out var sp))
                    profileFileName = sp.GetString() ?? "7.json";
                if (doc.RootElement.TryGetProperty("TimeOffsetSeconds", out var offset))
                    _timeOffsetSeconds = offset.GetInt32();
            }
            var profilePath = Path.Combine(dataDir, "Profiles", profileFileName);
            if (!File.Exists(profilePath)) return;

            using var profileDoc = JsonDocument.Parse(File.ReadAllText(profilePath));
            var root = profileDoc.RootElement;
            if (!root.TryGetProperty("TimeLayouts", out var layouts)) return;
            if (!root.TryGetProperty("ClassPlans", out var classPlans)) return;
            if (!root.TryGetProperty("Subjects", out var subjects)) return;

            var now = DateTime.Now;
            int todayDayOfWeek = (int)now.DayOfWeek;

            // 找今天的课表
            JsonElement? todayPlan = null;
            string? tlId = null;
            foreach (var cp in classPlans.EnumerateObject())
            {
                var v = cp.Value;
                if (v.TryGetProperty("IsOverlay", out var io) && io.GetBoolean()) continue;
                if (!v.TryGetProperty("TimeRule", out var tr)) continue;
                if (!tr.TryGetProperty("WeekDay", out var wd) || wd.GetInt32() != todayDayOfWeek) continue;
                if (!v.TryGetProperty("IsEnabled", out var ie) || !ie.GetBoolean()) continue;
                todayPlan = v;
                if (v.TryGetProperty("TimeLayoutId", out var t)) tlId = t.GetString();
                break;
            }
            if (todayPlan == null || tlId == null) return;
            if (!layouts.TryGetProperty(tlId, out var timeLayout)) return;
            if (!timeLayout.TryGetProperty("Layouts", out var items)) return;

            var classes = todayPlan.Value.GetProperty("Classes");
            int ci = 0;
            foreach (var item in items.EnumerateArray())
            {
                int timeType = item.GetProperty("TimeType").GetInt32();
                var start = TimeSpan.Parse(item.GetProperty("StartTime").GetString()!);
                var end = TimeSpan.Parse(item.GetProperty("EndTime").GetString()!);
                string? subjName = null;
                if (timeType == 0 && ci < classes.GetArrayLength())
                {
                    var ce = classes[ci];
                    if (ce.TryGetProperty("SubjectId", out var sid))
                    {
                        var sidStr = sid.GetString();
                        if (sidStr != null && subjects.TryGetProperty(sidStr, out var subj))
                            subjName = subj.GetProperty("Name").GetString();
                    }
                    ci++;
                }
                _todaySlots.Add(new TimeSlot { Start = start, End = end, IsClass = timeType == 0, SubjectName = subjName });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ViewModel] 加载课表: {ex}");
        }
    }

    private void UpdateCourseInfo(DateTime now)
    {
        var time = now.TimeOfDay;
        TimeSlot? current = null;
        TimeSlot? nextClass = null;

        for (int i = 0; i < _todaySlots.Count; i++)
        {
            var s = _todaySlots[i];
            if (time >= s.Start && time < s.End)
            {
                current = s;
                for (int j = i + 1; j < _todaySlots.Count; j++)
                {
                    if (_todaySlots[j].IsClass && !string.IsNullOrEmpty(_todaySlots[j].SubjectName))
                    { nextClass = _todaySlots[j]; break; }
                }
                break;
            }
        }

        if (current != null && current.IsClass && !string.IsNullOrEmpty(current.SubjectName))
        {
            // 上课中：显示当前课程（含实际打铃时间，如有偏移）
            CourseInfoText = $"当前课程为 {current.SubjectName}   {current.Start:hh\\:mm} — {current.End:hh\\:mm}{FormatBellTime(current)}";
        }
        else if (_isInBreak && nextClass != null)
        {
            // 课间：显示下节课程
            CourseInfoText = $"下节课程为 {nextClass.SubjectName}   {nextClass.Start:hh\\:mm} — {nextClass.End:hh\\:mm}{FormatBellTime(nextClass)}";
        }
        else
        {
            CourseInfoText = "";
        }
    }

    /// <summary>
    /// 把课表时间换算成实际打铃时间（北京真实时间）的说明文字。
    /// 实际打铃时间 = 课表时间 - TimeOffsetSeconds。无偏移时返回空串。
    /// </summary>
    private string FormatBellTime(TimeSlot slot)
    {
        if (_timeOffsetSeconds == 0) return "";
        var offset = TimeSpan.FromSeconds(_timeOffsetSeconds);
        var bellStart = slot.Start - offset;
        var bellEnd = slot.End - offset;
        var fmt = _timeOffsetSeconds % 60 == 0 ? @"hh\:mm" : @"hh\:mm\:ss";
        return $"（实际打铃 {bellStart.ToString(fmt)} — {bellEnd.ToString(fmt)}）";
    }

    private void SetMainWindowVisible(bool visible)
    {
        try
        {
            var lifetime = Avalonia.Application.Current?.ApplicationLifetime;
            if (lifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                foreach (var win in desktop.Windows)
                {
                    if (win == _window) continue;
                    var typeName = win.GetType().FullName ?? "";
                    if (typeName.Contains("MainWindow"))
                    {
                        if (visible) win.Show(); else win.Hide();
                        break;
                    }
                }
            }
        }
        catch { }
    }

    private void SubscribeToClassEvents()
    {
        try
        {
            var lessonsService = _serviceProvider.GetRequiredService<ILessonsService>();
            lessonsService.CurrentTimeStateChanged += OnTimeStateChanged;
            // 立即检查当前状态（用户可能在打开时钟时已经在上课）
            Dispatcher.UIThread.Post(() => OnTimeStateChanged(null, EventArgs.Empty));
        }
        catch { }
    }

    /// <summary>
    /// 从课表里查出当前课程的开始时间（用 CI 虚拟时间定位，与实际打铃对齐）。
    /// 如果课表没加载或找不到，返回 null，调用方降级使用虚拟时间。
    /// </summary>
    private DateTime? LookupActualClassStart(string subjectName)
    {
        if (string.IsNullOrEmpty(subjectName) || _todaySlots.Count == 0) return null;
        var now = NowVirtual;
        foreach (var slot in _todaySlots)
        {
            if (slot.IsClass && slot.SubjectName == subjectName
                && now.TimeOfDay >= slot.Start && now.TimeOfDay < slot.End)
            {
                return now.Date + slot.Start;
            }
        }
        return null;
    }

    private ClassSummary BuildSummary()
    {
        return new ClassSummary
        {
            ClassName = _currentClassName,
            Normal = _normalCount,
            Noisy = _noisyCount
        };
    }

    private void OnTimeStateChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var lessonsService = _serviceProvider.GetRequiredService<ILessonsService>();
                var state = lessonsService.CurrentState;
                var subjectName = lessonsService.CurrentSubject?.Name ?? "";

                if (state == ClassIsland.Shared.Enums.TimeState.OnClass)
                {
                    // 课程切换 → 保存上一节总结
                    if (!string.IsNullOrEmpty(_currentClassName) && _currentClassName != subjectName)
                    {
                        var summary = BuildSummary();
                        if (summary.Normal > 0 || summary.Noisy > 0) _classSummaries.Add(summary);
                    }
                    _currentClassName = subjectName;
                    _normalCount = 0;
                    _noisyCount = 0;
                    _pendingNormalDeadline = default;
                    _classStartTime = LookupActualClassStart(subjectName) ?? NowVirtual;
                    _isInBreak = false;
                    UpdateNoisyDisplay();
                }
                else if (state == ClassIsland.Shared.Enums.TimeState.Breaking)
                {
                    if (!string.IsNullOrEmpty(_currentClassName) && (_normalCount > 0 || _noisyCount > 0))
                    {
                        _classSummaries.Add(BuildSummary());
                    }
                    _isInBreak = true;
                    UpdateNoisyDisplay();
                }
            }
            catch { }
        });
    }

    private void UpdateNoisyDisplay()
    {
        NoisyDisplaySegments.Clear();
        if (!_settings.ShowNoisyCounter) return;

        if (_isInBreak)
        {
            if (_classSummaries.Count > 0)
            {
                var s = _classSummaries[^1];
                NoisyDisplaySegments.Add(new NoisyDisplaySegment { Text = $"上节课 {s.ClassName}" });
                if (s.Normal > 0)
                    NoisyDisplaySegments.Add(new NoisyDisplaySegment { Text = $"  一般 {s.Normal} 次", Foreground = CountNormalBrush });
                if (s.Noisy > 0)
                    NoisyDisplaySegments.Add(new NoisyDisplaySegment { Text = $"  吵闹 {s.Noisy} 次", Foreground = CountNoisyBrush });
            }
            return;
        }

        if (string.IsNullOrEmpty(_currentClassName)) return;

        NoisyDisplaySegments.Add(new NoisyDisplaySegment { Text = _currentClassName });

        if (_normalCount > 0 || _noisyCount > 0)
        {
            if (_normalCount > 0)
                NoisyDisplaySegments.Add(new NoisyDisplaySegment { Text = $"  一般 {_normalCount} 次", Foreground = CountNormalBrush });
            if (_noisyCount > 0)
                NoisyDisplaySegments.Add(new NoisyDisplaySegment { Text = $"  吵闹 {_noisyCount} 次", Foreground = CountNoisyBrush });
        }
        else if (_settings.SkipFirst3Min)
        {
            var remaining = 180 - (int)(NowVirtual - _classStartTime).TotalSeconds;
            if (remaining > 0)
                NoisyDisplaySegments.Add(new NoisyDisplaySegment { Text = $"  ⏳ 上课初期保护中（{remaining / 60}:{remaining % 60:D2} 后开始记录）" });
            else
                NoisyDisplaySegments.Add(new NoisyDisplaySegment { Text = "  ✅ 暂未记录到吵闹" });
        }
        else
        {
            NoisyDisplaySegments.Add(new NoisyDisplaySegment { Text = "  ✅ 暂未记录到吵闹" });
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);
    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_DISPLAY_REQUIRED = 0x00000002;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;

    private void StartAntiScreensaver()
    {
        _antiScreensaverTimer?.Dispose();
        _antiScreensaverTimer = new System.Timers.Timer(25 * 60 * 1000); // 每 25 分钟
        _antiScreensaverTimer.Elapsed += (_, _) =>
        {
            SetThreadExecutionState(ES_CONTINUOUS | ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED);
        };
        _antiScreensaverTimer.Start();
        // 立即执行一次
        SetThreadExecutionState(ES_CONTINUOUS | ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED);
    }

    private void StopAntiScreensaver()
    {
        _antiScreensaverTimer?.Dispose();
        _antiScreensaverTimer = null;
    }

    public void ExitFullScreen() => Hide();

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

internal class TimeSlot
{
    public TimeSpan Start { get; set; }
    public TimeSpan End { get; set; }
    public bool IsClass { get; set; }
    public string? SubjectName { get; set; }
}

/// <summary>
/// 一节课的吵闹记录摘要（课间/换课时显示用）。
/// </summary>
internal class ClassSummary
{
    public string ClassName { get; set; } = "";
    public int Normal { get; set; }
    public int Noisy { get; set; }
}

/// <summary>
/// 计数区域的单个显示片段（带颜色，用于一般/吵闹不同色）。
/// </summary>
public class NoisyDisplaySegment
{
    public string Text { get; set; } = "";
    public IBrush Foreground { get; set; } = new SolidColorBrush(Color.Parse("#ffff44"));
}
