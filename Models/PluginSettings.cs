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
    private string _progressColor = "#4CAF50";
    private bool _showDecibelMeter = true;
    private bool _showCourseInfo = true;
    private bool _showNoisyCounter = true;
    private bool _skipFirst3Min = true;
    private int _skipFirstMinutes = 3;
    private double _noisySustainSeconds = 1.0;
    private double _fallWindowSeconds = 5.0;
    private bool _enableNoiseDebugLog;
    private int _clockFontSize = 180;
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
    /// 强调色 (HEX)，用于分贝条和高亮
    /// </summary>
    public string AccentColor
    {
        get => _accentColor;
        set { _accentColor = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 课程进度条颜色 (HEX)。已进行部分用此色（不透明），未进行部分用半透明派生。
    /// </summary>
    public string ProgressColor
    {
        get => _progressColor;
        set { _progressColor = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 是否显示教室分贝
    /// </summary>
    public bool ShowDecibelMeter
    {
        get => _showDecibelMeter;
        set { _showDecibelMeter = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 是否显示课程信息
    /// </summary>
    public bool ShowCourseInfo
    {
        get => _showCourseInfo;
        set { _showCourseInfo = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 是否显示吵闹计数
    /// </summary>
    public bool ShowNoisyCounter
    {
        get => _showNoisyCounter;
        set { _showNoisyCounter = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 上课前 3 分钟是否不计数
    /// </summary>
    public bool SkipFirst3Min
    {
        get => _skipFirst3Min;
        set { _skipFirst3Min = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 上课初期保护时长（分钟）
    /// </summary>
    public int SkipFirstMinutes
    {
        get => _skipFirstMinutes;
        set { _skipFirstMinutes = Math.Clamp(value, 0, 10); OnPropertyChanged(); }
    }

    /// <summary>
    /// 起算底线（秒）：一段噪音内「有效时长」（处于一般/吵闹的累计时间）达到该值，
    /// 段结束才记一次；不足的短段（喷嚏/咳嗽）丢弃。默认 1 秒。
    /// </summary>
    public double NoisySustainSeconds
    {
        get => _noisySustainSeconds;
        set { _noisySustainSeconds = Math.Clamp(value, 0.5, 5.0); OnPropertyChanged(); }
    }

    /// <summary>
    /// 回落窗口（秒）：音量掉到安静/良好后，回落不超过该值则与前面并成一段（短暂停顿不断段）；
    /// 超过该值段才结束、记一次。默认 5 秒，范围 0~15。
    /// </summary>
    public double FallWindowSeconds
    {
        get => _fallWindowSeconds;
        set { _fallWindowSeconds = Math.Clamp(value, 0, 15); OnPropertyChanged(); }
    }

    /// <summary>
    /// 是否输出噪音调试日志（NoiseDebugLog.csv），用于收集真实数据校准阈值
    /// </summary>
    public bool EnableNoiseDebugLog
    {
        get => _enableNoiseDebugLog;
        set { _enableNoiseDebugLog = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 时钟字号 (px)
    /// </summary>
    public int ClockFontSize
    {
        get => _clockFontSize;
        set { _clockFontSize = value; OnPropertyChanged(); }
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
