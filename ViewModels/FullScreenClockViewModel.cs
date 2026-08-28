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
using EveningSelfStudyClock.NoiseDetection;
using EveningSelfStudyClock.Services;
using EveningSelfStudyClock.Views;
using Microsoft.Extensions.DependencyInjection;

namespace EveningSelfStudyClock.ViewModels;

public class FullScreenClockViewModel : INotifyPropertyChanged
{
    private readonly PluginSettings _settings;
    private readonly DecibelMeterService _decibelService;
    private readonly IServiceProvider _serviceProvider;
    private readonly NoiseCounter _counter = new();
    private readonly Lazy<IExactTimeService?> _exactTimeService;
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
    private string _currentClassName = "";
    private readonly List<ClassSummary> _classSummaries = new();
    private bool _isWindowVisible;
    private bool _isInBreak;
    private DateTime _classStartTime;
    private string _courseInfoText = "";
    private List<TimeSlot> _todaySlots = new();
    private System.Timers.Timer? _antiScreensaverTimer;
    private string _noisyDisplaySignature = "";
    private bool _showCountRules;

    public FullScreenClockViewModel(
        PluginSettings settings,
        DecibelMeterService decibelService,
        IServiceProvider serviceProvider)
    {
        _settings = settings;
        _decibelService = decibelService;
        _serviceProvider = serviceProvider;

        // 惰性缓存 IExactTimeService，避免每次调用查服务
        _exactTimeService = new Lazy<IExactTimeService?>(() =>
        {
            try { return _serviceProvider.GetService<IExactTimeService>(); }
            catch { return null; }
        });

        _decibelService.PropertyChanged += OnDecibelPropertyChanged;
        _decibelService.NoiseEventRaised += OnNoiseEventRaised;
        _settings.PropertyChanged += OnSettingsPropertyChanged;
    }

    /// <summary>
    /// CI 的"虚拟本地时间"（含时间偏移），用于课程定位/保护计时。
    /// 大时钟显示仍用真实北京时间，学生看到的是准确时间。
    /// </summary>
    private DateTime NowVirtual
    {
        get
        {
            try { return _exactTimeService.Value?.GetCurrentLocalDateTime() ?? DateTime.Now; }
            catch { return DateTime.Now; }
        }
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(PluginSettings.SkipFirst3Min)
            or nameof(PluginSettings.SkipFirstMinutes)
            or nameof(PluginSettings.NoisySustainSeconds)
            or nameof(PluginSettings.FallWindowSeconds))
        {
            OnPropertyChanged(nameof(CountRulesText));
        }
    }

    /// <summary>
    /// 分贝属性变更：仅转发显示属性给全屏界面绑定。计数由持续事件驱动（NoiseEventRaised）。
    /// </summary>
    private void OnDecibelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(DecibelMeterService.NoiseLevelText)
            or nameof(DecibelMeterService.NoiseLevelEmoji)
            or nameof(DecibelMeterService.NoiseLevelProgress))
        {
            OnPropertyChanged(args.PropertyName!);
        }
        else if (args.PropertyName == nameof(DecibelMeterService.CurrentSegmentLevel))
        {
            UpdateRecording();
        }
    }

    /// <summary>
    /// 持续事件（一般/吵闹）→ 计数。事件在音频线程引发，这里调度到 UI 线程。
    /// 事件等级已由探测器按段内一般/吵闹占比判定，计数层只应用 冷却/保护 规则。
    /// </summary>
    private void OnNoiseEventRaised(object? sender, NoiseEventRaisedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var decision = _counter.OnEvent(e.Level, IsInProtection());
            if (decision != CountDecision.None)
                UpdateNoisyDisplay();
        });
    }

    private bool IsInProtection()
        => _settings.SkipFirst3Min
           && (NowVirtual - _classStartTime).TotalSeconds < _settings.SkipFirstMinutes * 60;

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
    public string ProgressColor => _settings.ProgressColor;

    /// <summary>进度条「已进行」部分颜色（深色，不透明）。</summary>
    public IBrush ProgressFillBrush => new SolidColorBrush(Color.Parse(_settings.ProgressColor));

    /// <summary>进度条「未进行」部分颜色（浅色，由 ProgressColor 半透明派生）。</summary>
    public IBrush ProgressTrackBrush
    {
        get
        {
            var c = Color.Parse(_settings.ProgressColor);
            return new SolidColorBrush(new Color((byte)(c.A / 4), c.R, c.G, c.B));
        }
    }

    /// <summary>
    /// 触发所有外观属性变更通知，让窗口绑定重新求值。
    /// 窗口从 Hide 退出后复用时，这些表达式体 getter 不会自动刷新，需主动通知。
    /// </summary>
    private void RefreshAppearanceBindings()
    {
        OnPropertyChanged(nameof(BackgroundColor));
        OnPropertyChanged(nameof(FontColor));
        OnPropertyChanged(nameof(AccentColor));
        OnPropertyChanged(nameof(ProgressColor));
        OnPropertyChanged(nameof(ProgressFillBrush));
        OnPropertyChanged(nameof(ProgressTrackBrush));
        OnPropertyChanged(nameof(ClockFontSize));
        OnPropertyChanged(nameof(WindowTitle));
    }
    public string NoiseLevelText => _decibelService.NoiseLevelText;
    public string NoiseLevelEmoji => _decibelService.NoiseLevelEmoji;
    public double NoiseLevelProgress => _decibelService.NoiseLevelProgress;
    public int DisplayLevel => _decibelService.DisplayLevel;

    /// <summary>
    /// 计数区域的分段显示：课程名/一般（黄）/吵闹（红）/保护提示。
    /// </summary>
    public ObservableCollection<NoisyDisplaySegment> NoisyDisplaySegments { get; } = new();

    /// <summary>
    /// 底部记录规则是否显示（鼠标悬停计数区域时）。
    /// </summary>
    public bool ShowCountRules
    {
        get => _showCountRules;
        set { _showCountRules = value; OnPropertyChanged(); }
    }

    // ===== 「正在记录」提示（段起算后实时显示，一般黄 / 吵闹红） =====

    private bool _isRecordingVisible;
    private string _recordingText = "";
    private IBrush _recordingForeground = CountNormalBrush;

    /// <summary>是否显示「正在记录」提示（有段且已起算、上课中）。</summary>
    public bool IsRecordingVisible
    {
        get => _isRecordingVisible;
        set { _isRecordingVisible = value; OnPropertyChanged(); }
    }

    /// <summary>「正在记录：一般」/「正在记录：吵闹」。</summary>
    public string RecordingText
    {
        get => _recordingText;
        set { _recordingText = value; OnPropertyChanged(); }
    }

    /// <summary>提示颜色：一般=黄，吵闹=红。</summary>
    public IBrush RecordingForeground
    {
        get => _recordingForeground;
        set { _recordingForeground = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 根据检测器的实时段归属刷新「正在记录」提示。
    /// 仅在计数可见且上课时段显示；段起算前/段结束后隐藏。
    /// </summary>
    private void UpdateRecording()
    {
        var seg = _decibelService.CurrentSegmentLevel;
        bool show = seg.HasValue && _settings.ShowNoisyCounter
            && !_isInBreak && !string.IsNullOrEmpty(_currentClassName);
        IsRecordingVisible = show;
        if (!show) return;
        var noisy = seg == NoiseLevel.Noisy;
        RecordingText = noisy ? "正在记录：吵闹" : "正在记录：一般";
        RecordingForeground = noisy ? CountNoisyBrush : CountNormalBrush;
    }

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

    private double _courseProgress;

    /// <summary>当前时段进行进度（0~100）。上课=本课进度，课间=当前休息进度。</summary>
    public double CourseProgress
    {
        get => _courseProgress;
        set { _courseProgress = value; OnPropertyChanged(); }
    }

    private bool _showCourseProgress;

    /// <summary>是否显示进度条（当前存在时段槽时显示，放学/无课隐藏）。</summary>
    public bool ShowCourseProgress
    {
        get => _showCourseProgress;
        set { _showCourseProgress = value; OnPropertyChanged(); }
    }

    public int ClockFontSize
    {
        get => _clockFontSize;
        set { _clockFontSize = value; OnPropertyChanged(); }
    }

    public string WindowTitle => _settings.WindowTitle;

    /// <summary>
    /// 记录规则（鼠标悬停计数区域时以提示显示）。
    /// </summary>
    public string CountRulesText
    {
        get
        {
            var parts = new List<string>
            {
                $"一段噪音累计满 {_settings.NoisySustainSeconds:0.#} 秒记一次",
                $"一段内回落不超过 {_settings.FallWindowSeconds:0.#} 秒并成一次",
                "一段噪音按一般/吵闹的时长占比归属：吵闹占一半及以上记「吵闹」，否则记「一般」",
            };
            if (_settings.SkipFirst3Min) parts.Add($"上课开始后 {_settings.SkipFirstMinutes} 分钟内不记录");
            return string.Join("，", parts);
        }
    }

    /// <summary>
    /// 底部固定显示的免责声明。
    /// </summary>
    public string DisclaimerText
        => "⚠ 分贝仅供参考，可能受风扇、空调、开关门、桌椅移动、脚步声等环境杂音影响，不代表真实纪律状况";

    public void Show()
    {
        Dispatcher.UIThread.Post(() =>
        {
            // 必须在创建窗口前同步，否则绑定读到的是旧值
            ShowDecibelMeter = _settings.ShowDecibelMeter;
            ShowCourseInfo = _settings.ShowCourseInfo;
            ClockFontSize = _settings.ClockFontSize;
            // 外观颜色属性是直接读 _settings 的 getter，窗口从 Hide 退出后复用不自动刷新，
            // 主动触发通知让绑定重新求值（修复：改完颜色后要重启 CI 才生效的问题）
            RefreshAppearanceBindings();

            if (_window == null)
            {
                _window = new FullScreenClockWindow { DataContext = this };
                _window.Closed += (_, _) =>
                {
                    IsWindowVisible = false;
                    StopUpdateTimer();
                    _decibelService.StopMonitoring();
                    UnsubscribeToClassEvents();
                    SetMainWindowVisible(true);
                    _window = null;
                };
            }

            if (_window.IsVisible) { RefreshAll(); return; }

            // 重置新一轮记录
            _counter.StartNewClass();
            _counter.SetBreak(false);
            _currentClassName = "";
            _classSummaries.Clear();
            _isInBreak = false;
            _noisyDisplaySignature = "";
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
            UnsubscribeToClassEvents();
            IsWindowVisible = false;
            SetMainWindowVisible(true);
        });
    }

    private void StartUpdateTimer()
    {
        if (_updateTimer != null) return;
        _updateTimer = new System.Timers.Timer(1000); // 时间只显示到秒，1s 足够
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

        // 刷新计数区域（脏标记：内容未变不重建；保护倒计时需要每秒刷新）
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

        // 进度条：上课槽或课间槽都显示「当前时间状态」的进行进度。
        // time 与课程定位是同一时刻（NowVirtual），current 是同一时段槽 → 时间显示与进度天然一致。
        if (current != null)
        {
            CourseProgress = ProgressCalculator.Calc(time, current.Start, current.End);
            ShowCourseProgress = true;
        }
        else
        {
            ShowCourseProgress = false;
            CourseProgress = 0;
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
            // 幂等订阅：先退订再订阅，避免 Show/Hide 多次叠加（P0 修复）
            lessonsService.CurrentTimeStateChanged -= OnTimeStateChanged;
            lessonsService.CurrentTimeStateChanged += OnTimeStateChanged;
            // 立即检查当前状态（用户可能在打开时钟时已经在上课）
            Dispatcher.UIThread.Post(() => OnTimeStateChanged(null, EventArgs.Empty));
        }
        catch { }
    }

    private void UnsubscribeToClassEvents()
    {
        try
        {
            var lessonsService = _serviceProvider.GetRequiredService<ILessonsService>();
            lessonsService.CurrentTimeStateChanged -= OnTimeStateChanged;
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
            Normal = _counter.NormalCount,
            Noisy = _counter.NoisyCount
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
                    _counter.SetBreak(false);
                    _counter.StartNewClass();
                    _classStartTime = LookupActualClassStart(subjectName) ?? NowVirtual;
                    _isInBreak = false;
                    UpdateNoisyDisplay();
                    UpdateRecording();
                }
                else if (state == ClassIsland.Shared.Enums.TimeState.Breaking)
                {
                    if (!string.IsNullOrEmpty(_currentClassName) && (_counter.NormalCount > 0 || _counter.NoisyCount > 0))
                    {
                        _classSummaries.Add(BuildSummary());
                    }
                    _counter.SetBreak(true);
                    _isInBreak = true;
                    UpdateNoisyDisplay();
                    UpdateRecording();
                }
            }
            catch { }
        });
    }

    /// <summary>
    /// 构建计数区域分段（课程名/一般黄/吵闹红/保护提示）。
    /// 只在上课期间显示；课间/非上课时段内部仍可记录，但显示隐藏。
    /// </summary>
    private List<NoisyDisplaySegment> BuildNoisySegments()
    {
        var list = new List<NoisyDisplaySegment>();
        if (!_settings.ShowNoisyCounter) return list;

        // 课间或非上课（无当前课程）时段：隐藏计数显示
        if (_isInBreak) return list;
        if (string.IsNullOrEmpty(_currentClassName)) return list;

        list.Add(new NoisyDisplaySegment { Text = _currentClassName });
        AddNoisyCountSegments(list);
        return list;
    }

    /// <summary>追加一般/吵闹计数段（计数为 0 时按设置显示保护提示或「暂未记录」）。</summary>
    private void AddNoisyCountSegments(List<NoisyDisplaySegment> list)
    {
        if (_counter.NormalCount > 0 || _counter.NoisyCount > 0)
        {
            if (_counter.NormalCount > 0)
                list.Add(new NoisyDisplaySegment { Text = $"  一般 {_counter.NormalCount} 次", Foreground = CountNormalBrush });
            if (_counter.NoisyCount > 0)
                list.Add(new NoisyDisplaySegment { Text = $"  吵闹 {_counter.NoisyCount} 次", Foreground = CountNoisyBrush });
        }
        else if (_settings.SkipFirst3Min)
        {
            var remaining = _settings.SkipFirstMinutes * 60 - (int)(NowVirtual - _classStartTime).TotalSeconds;
            if (remaining > 0)
                list.Add(new NoisyDisplaySegment { Text = $"  ⏳ 上课初期保护中（{remaining / 60}:{remaining % 60:D2} 后开始记录）" });
            else
                list.Add(new NoisyDisplaySegment { Text = "  ✅ 暂未记录到吵闹" });
        }
        else
        {
            list.Add(new NoisyDisplaySegment { Text = "  ✅ 暂未记录到吵闹" });
        }
    }

    private void UpdateNoisyDisplay()
    {
        var segments = BuildNoisySegments();
        var signature = string.Join("|", segments.Select(s =>
            s.Text + ":" + (ReferenceEquals(s.Foreground, CountNoisyBrush) ? "R"
                : ReferenceEquals(s.Foreground, CountNormalBrush) ? "Y" : "P")));
        if (signature == _noisyDisplaySignature) return;

        _noisyDisplaySignature = signature;
        NoisyDisplaySegments.Clear();
        foreach (var s in segments) NoisyDisplaySegments.Add(s);
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
