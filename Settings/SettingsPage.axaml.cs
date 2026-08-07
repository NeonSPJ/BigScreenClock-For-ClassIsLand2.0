using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Timers;
using System.Windows.Input;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Enums.SettingsWindow;
using EveningSelfStudyClock.Models;
using EveningSelfStudyClock.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace EveningSelfStudyClock.Settings;

[SettingsPageInfo("com.bigscreen.clock.settings", "大屏时钟", SettingsPageCategory.External)]
public partial class SettingsPage : SettingsPageBase, INotifyPropertyChanged
{
    private readonly PluginSettings _settings;
    private List<string> _allCourseNames = new();
    private System.Timers.Timer? _previewTimer;

    private string _previewTime = DateTime.Now.ToString("HH:mm:ss");
    private string _previewCourseText = "当前课程为 晚自习   18:30 — 21:30";

    public ObservableCollection<CourseSelectionItem> SelectedCourses { get; } = new();

    public SettingsPage()
    {
        InitializeComponent();
        _settings = Plugin.Settings!;
        DataContext = this;
        LoadAllCourseNames();
        RestoreSelectedCourses();
        StartPreviewTimer();
    }

    private void StartPreviewTimer()
    {
        _previewTimer = new System.Timers.Timer(1000);
        _previewTimer.Elapsed += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            PreviewTime = DateTime.Now.ToString("HH:mm:ss");
        });
        _previewTimer.Start();
    }

    public string PreviewTime
    {
        get => _previewTime;
        set { _previewTime = value; OnPropertyChanged(nameof(PreviewTime)); }
    }

    public string PreviewCourseText
    {
        get => _previewCourseText;
        set { _previewCourseText = value; OnPropertyChanged(nameof(PreviewCourseText)); }
    }

    public int PreviewFontSize => Math.Max(12, _settings.ClockFontSize / 5);

    // 滑块绑定需要 double
    public double ClockFontSize
    {
        get => _settings.ClockFontSize;
        set { _settings.ClockFontSize = (int)value; OnPropertyChanged(nameof(ClockFontSize)); OnPropertyChanged(nameof(PreviewFontSize)); }
    }

    private void LoadAllCourseNames()
    {
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
            if (!profileDoc.RootElement.TryGetProperty("Subjects", out var subjectsEl)) return;
            foreach (var subject in subjectsEl.EnumerateObject())
                if (subject.Value.TryGetProperty("Name", out var ne) && !string.IsNullOrWhiteSpace(ne.GetString()))
                    _allCourseNames.Add(ne.GetString()!);
        }
        catch { }
    }

    private void RestoreSelectedCourses()
    {
        SelectedCourses.Clear();
        foreach (var name in _settings.TargetCourseNames)
            SelectedCourses.Add(new CourseSelectionItem(_allCourseNames, name, this));
        if (SelectedCourses.Count == 0)
            SelectedCourses.Add(new CourseSelectionItem(_allCourseNames, null, this));
    }

    public void AddCourse_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SelectedCourses.Add(new CourseSelectionItem(_allCourseNames, null, this));
        SaveCourses();
    }

    internal void OnCourseSelectionChanged() => SaveCourses();

    internal void RemoveCourse(CourseSelectionItem item)
    {
        SelectedCourses.Remove(item);
        if (SelectedCourses.Count == 0)
            SelectedCourses.Add(new CourseSelectionItem(_allCourseNames, null, this));
        SaveCourses();
    }

    private void SaveCourses()
    {
        var names = SelectedCourses.Where(c => !string.IsNullOrWhiteSpace(c.SelectedName))
                                   .Select(c => c.SelectedName!).Distinct().ToList();
        _settings.TargetCourseNames.Clear();
        foreach (var n in names) _settings.TargetCourseNames.Add(n);
        Plugin.SaveSettings(); // 强制写盘
    }

    public void TestButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try { Plugin.ServiceProvider?.GetRequiredService<FullScreenClockViewModel>().Show(); }
        catch { }
    }

    // ===== 分贝阈值 =====
    public string DecibelQuietThresholdText
    {
        get => _settings.DecibelQuietThreshold.ToString("F4");
        set { if (double.TryParse(value, out var v)) _settings.DecibelQuietThreshold = v; }
    }
    public string DecibelGoodThresholdText
    {
        get => _settings.DecibelGoodThreshold.ToString("F4");
        set { if (double.TryParse(value, out var v)) _settings.DecibelGoodThreshold = v; }
    }
    public string DecibelNormalThresholdText
    {
        get => _settings.DecibelNormalThreshold.ToString("F4");
        set { if (double.TryParse(value, out var v)) _settings.DecibelNormalThreshold = v; }
    }
    public string DecibelNoisyThresholdText
    {
        get => _settings.DecibelNoisyThreshold.ToString("F4");
        set { if (double.TryParse(value, out var v)) _settings.DecibelNoisyThreshold = v; }
    }

    public bool ShowDecibelMeter
    {
        get => _settings.ShowDecibelMeter;
        set { _settings.ShowDecibelMeter = value; OnPropertyChanged(nameof(ShowDecibelMeter)); }
    }

    public bool ShowCourseInfo
    {
        get => _settings.ShowCourseInfo;
        set { _settings.ShowCourseInfo = value; OnPropertyChanged(nameof(ShowCourseInfo)); }
    }

    public bool ShowNoisyCounter
    {
        get => _settings.ShowNoisyCounter;
        set { _settings.ShowNoisyCounter = value; OnPropertyChanged(nameof(ShowNoisyCounter)); }
    }

    public string BackgroundColor { get => _settings.BackgroundColor; set => _settings.BackgroundColor = value; }
    public string FontColor { get => _settings.FontColor; set => _settings.FontColor = value; }
    public string ClockFontSizeText => $"{_settings.ClockFontSize}px";

    public string AccentColor { get => _settings.AccentColor; set => _settings.AccentColor = value; }

    public new event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public class CourseSelectionItem : INotifyPropertyChanged
{
    private readonly SettingsPage _parent;
    private string? _selectedName;
    public List<string> AllCourses { get; }
    public ICommand RemoveCommand { get; }

    public string? SelectedName
    {
        get => _selectedName;
        set
        {
            if (_selectedName == value) return;
            _selectedName = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedName)));
            if (!string.IsNullOrWhiteSpace(value)) _parent.OnCourseSelectionChanged();
        }
    }

    public CourseSelectionItem(List<string> allCourses, string? initial, SettingsPage parent)
    {
        _parent = parent; AllCourses = allCourses; _selectedName = initial;
        RemoveCommand = new RelayCommand(() => parent.RemoveCourse(this));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

internal class RelayCommand : ICommand
{
    private readonly Action _action;
    public RelayCommand(Action action) => _action = action;
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _action();
    public event EventHandler? CanExecuteChanged;
}
