using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Timers;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
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

    private string _currentTime = "00:00:00";
    private string _currentDate = "";
    private string _currentCourseName = "";
    private int _currentClassNoisyCount;
    private string _currentClassName = "";
    private string _noisyDisplayText = "";
    private readonly List<string> _classSummaries = new();
    private bool _isWindowVisible;
    private bool _isInBreak;
    private string _courseInfoText = "";
    private List<TimeSlot> _todaySlots = new();

    public FullScreenClockViewModel(
        PluginSettings settings,
        DecibelMeterService decibelService,
        IServiceProvider serviceProvider)
    {
        _settings = settings;
        _decibelService = decibelService;
        _serviceProvider = serviceProvider;

        _decibelService.PropertyChanged += OnDecibelPropertyChanged;
    }

    private void OnDecibelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(DecibelMeterService.NoiseLevelText)
            or nameof(DecibelMeterService.NoiseLevelEmoji)
            or nameof(DecibelMeterService.NoiseLevelProgress))
        {
            OnPropertyChanged(args.PropertyName!);
        }

        // 检测吵闹/嘈杂 → 计数
        if (args.PropertyName == nameof(DecibelMeterService.NoiseLevelText))
        {
            var level = _decibelService.NoiseLevelText;
            TryCountNoisy(level);
        }
    }

    private void TryCountNoisy(string level)
    {
        if (!level.Contains("吵闹") && !level.Contains("嘈杂")) return;
        if (_isInBreak) return;
        if ((DateTime.Now - _lastNoisyTime).TotalSeconds < 3) return;

        _currentClassNoisyCount++;
        _lastNoisyTime = DateTime.Now;
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
    public string NoisyDisplayText
    {
        get => _noisyDisplayText;
        set { _noisyDisplayText = value; OnPropertyChanged(); }
    }

    public string CourseInfoText
    {
        get => _courseInfoText;
        set { _courseInfoText = value; OnPropertyChanged(); }
    }

    private DateTime _lastNoisyTime;

    public string WindowTitle => _settings.WindowTitle;

    public void Show()
    {
        Dispatcher.UIThread.Post(() =>
        {
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
            _currentClassNoisyCount = 0;
            _currentClassName = "";
            _classSummaries.Clear();
            _isInBreak = false;
            _lastNoisyTime = DateTime.MinValue;
            NoisyDisplayText = "";
            CourseInfoText = "";
            LoadTodaySchedule();
            SubscribeToClassEvents();

            // 重新订阅分贝事件（确保计数链路有效）
            _decibelService.PropertyChanged -= OnDecibelPropertyChanged;
            _decibelService.PropertyChanged += OnDecibelPropertyChanged;

            StartUpdateTimer();
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

        UpdateCourseInfo(now);
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
            // 上课中：显示当前课程
            CourseInfoText = $"当前课程为 {current.SubjectName}   {current.Start:hh\\:mm} — {current.End:hh\\:mm}";
        }
        else if (_isInBreak && nextClass != null)
        {
            // 课间：显示下节课程
            CourseInfoText = $"下节课程为 {nextClass.SubjectName}   {nextClass.Start:hh\\:mm} — {nextClass.End:hh\\:mm}";
        }
        else
        {
            CourseInfoText = "";
        }
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
                    // 进入上课：如果课程变了，保存上一节课的总结
                    if (!string.IsNullOrEmpty(_currentClassName) && _currentClassName != subjectName)
                    {
                        _classSummaries.Add($"上节课 {_currentClassName}  共有 {_currentClassNoisyCount} 次吵闹");
                    }
                    _currentClassName = subjectName;
                    _currentClassNoisyCount = 0;
                    _isInBreak = false;
                    UpdateNoisyDisplay();
                }
                else if (state == ClassIsland.Shared.Enums.TimeState.Breaking)
                {
                    // 进入课间：保存当前课程总结，停止计数
                    if (!string.IsNullOrEmpty(_currentClassName) && _currentClassNoisyCount > 0)
                    {
                        _classSummaries.Add($"上节课 {_currentClassName}  共有 {_currentClassNoisyCount} 次吵闹");
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
        if (_isInBreak)
        {
            // 课间显示最近一条总结
            NoisyDisplayText = _classSummaries.Count > 0
                ? _classSummaries[^1] : "";
        }
        else
        {
            // 上课中显示当前课程计数
            NoisyDisplayText = !string.IsNullOrEmpty(_currentClassName)
                ? $"{_currentClassName}  吵闹 {_currentClassNoisyCount} 次"
                : "";
        }
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
