using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ClassIsland.Core.Helpers;

namespace EveningSelfStudyClock.Models;

public class PluginSettings : INotifyPropertyChanged
{
    private ObservableCollection<string> _targetCourseNames = new() { "晚自习" };
    private double _decibelQuietThreshold = 0.005;
    private double _decibelGoodThreshold = 0.015;
    private double _decibelNormalThreshold = 0.04;
    private double _decibelNoisyThreshold = 0.08;
    private string _backgroundColor = "#000000";
    private string _fontColor = "#ffffff";
    private string _accentColor = "#4CAF50";
    private int _microphoneDeviceIndex;
    private string _windowTitle = "大屏时钟";

    public PluginSettings()
    {
        _targetCourseNames.CollectionChanged += (_, _) => OnPropertyChanged(nameof(TargetCourseNames));
    }

    /// <summary>
    /// 触发全屏时钟的课程名称列表（支持模糊匹配，如"晚自习"可匹配"晚自习（语文）"）
    /// </summary>
    public ObservableCollection<string> TargetCourseNames
    {
        get => _targetCourseNames;
        set { _targetCourseNames = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 安静阈值 (RMS)，低于此值为"安静"
    /// </summary>
    public double DecibelQuietThreshold
    {
        get => _decibelQuietThreshold;
        set { _decibelQuietThreshold = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 良好阈值 (RMS)，低于此值为"良好"
    /// </summary>
    public double DecibelGoodThreshold
    {
        get => _decibelGoodThreshold;
        set { _decibelGoodThreshold = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 一般阈值 (RMS)，低于此值为"一般"
    /// </summary>
    public double DecibelNormalThreshold
    {
        get => _decibelNormalThreshold;
        set { _decibelNormalThreshold = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 吵闹阈值 (RMS)，高于此值为"吵闹"，以上为"嘈杂"
    /// </summary>
    public double DecibelNoisyThreshold
    {
        get => _decibelNoisyThreshold;
        set { _decibelNoisyThreshold = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 背景颜色 (HEX)
    /// </summary>
    public string BackgroundColor
    {
        get => _backgroundColor;
        set { _backgroundColor = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 文字颜色 (HEX)
    /// </summary>
    public string FontColor
    {
        get => _fontColor;
        set { _fontColor = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 强调色 (HEX)，用于进度条和高亮
    /// </summary>
    public string AccentColor
    {
        get => _accentColor;
        set { _accentColor = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 选择的麦克风设备索引
    /// </summary>
    public int MicrophoneDeviceIndex
    {
        get => _microphoneDeviceIndex;
        set { _microphoneDeviceIndex = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 窗口标题
    /// </summary>
    public string WindowTitle
    {
        get => _windowTitle;
        set { _windowTitle = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// 检查课程名是否在目标列表中
    /// </summary>
    public bool IsTargetCourse(string? courseName)
    {
        if (string.IsNullOrWhiteSpace(courseName))
            return false;

        return TargetCourseNames.Any(target =>
            courseName.Contains(target, StringComparison.OrdinalIgnoreCase));
    }
}
