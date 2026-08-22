using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using EveningSelfStudyClock.Models;
using EveningSelfStudyClock.NoiseDetection;
using NAudio.Wave;

namespace EveningSelfStudyClock.Services;

/// <summary>持续事件已触发（后台音频线程引发，调用方需自行调度到 UI 线程）。</summary>
public sealed class NoiseEventRaisedEventArgs : EventArgs
{
    public NoiseLevel Level { get; }

    public NoiseEventRaisedEventArgs(NoiseLevel level)
    {
        Level = level;
    }
}

/// <summary>
/// 麦克风音量检测服务。使用 NAudio 采集音频，走 V2 检测管线：
/// 原始 RMS/峰值 → EMA 平滑 → 迟滞分级 → 持续判定 → 显示等级 + 持续事件。
/// 检测逻辑全部在音频采样线程执行，不依赖任何 UI Timer。
/// </summary>
public class DecibelMeterService : INotifyPropertyChanged, IDisposable
{
    private readonly PluginSettings _settings;
    private readonly NoiseEventDetector _detector = new();
    private WaveInEvent? _waveIn;
    private DateTime _lastDataReceived;
    private System.Timers.Timer? _watchdogTimer;
    private double _currentRms;
    private int _displayLevel; // 0-100 正数显示
    private string _noiseLevelText = "等待检测...";
    private string _noiseLevelEmoji = "🤫";
    private double _noiseLevelProgress;
    private double _lastPostedProgress = -1;
    private bool _isMonitoring;
    private string _statusMessage = "";

    // 调试日志
    private StreamWriter? _logWriter;
    private int _logLines;

    public DecibelMeterService(PluginSettings settings)
    {
        _settings = settings;
    }

    /// <summary>持续事件（一般/吵闹）已触发。Level 取值 Normal 或 Noisy。</summary>
    public event EventHandler<NoiseEventRaisedEventArgs>? NoiseEventRaised;

    public double CurrentRms
    {
        get => _currentRms;
        private set { _currentRms = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 正数音量级别 0-100（方便阅读）
    /// </summary>
    public int DisplayLevel
    {
        get => _displayLevel;
        private set { _displayLevel = value; OnPropertyChanged(); }
    }

    public string NoiseLevelText
    {
        get => _noiseLevelText;
        private set { _noiseLevelText = value; OnPropertyChanged(); }
    }

    public string NoiseLevelEmoji
    {
        get => _noiseLevelEmoji;
        private set { _noiseLevelEmoji = value; OnPropertyChanged(); }
    }

    public double NoiseLevelProgress
    {
        get => _noiseLevelProgress;
        private set { _noiseLevelProgress = value; OnPropertyChanged(); }
    }

    public bool IsMonitoring
    {
        get => _isMonitoring;
        private set { _isMonitoring = value; OnPropertyChanged(); }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set { _statusMessage = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 开始监听。固定使用默认麦克风（设备 0），失败则自动搜索可用设备。
    /// </summary>
    public void StartMonitoring()
    {
        if (_isMonitoring) return;
        _detector.Reset();
        EnsureLogWriter();
        StartMonitoringInternal();
        StartWatchdog();
    }

    private void StartMonitoringInternal()
    {
        try
        {
            int count = WaveInEvent.DeviceCount;
            if (count == 0)
            {
                NoiseLevelText = "未检测到麦克风";
                NoiseLevelEmoji = "🔇";
                return;
            }

            // 固定用默认设备（0）；打开失败自动搜其他可用设备（如设备休眠失效后看门狗重启）
            foreach (var idx in Enumerable.Range(0, count))
            {
                try
                {
                    var wi = new WaveInEvent { DeviceNumber = idx, WaveFormat = new WaveFormat(44100, 16, 1) };
                    wi.DataAvailable += OnDataAvailable;
                    wi.RecordingStopped += OnRecordingStopped;
                    wi.StartRecording();
                    _waveIn = wi;
                    _isMonitoring = true;
                    _lastDataReceived = DateTime.Now;
                    return;
                }
                catch { }
            }

            NoiseLevelText = "无法打开任何麦克风";
            NoiseLevelEmoji = "🔇";
        }
        catch (Exception ex)
        {
            _isMonitoring = false;
            NoiseLevelText = $"麦克风错误: {ex.Message}";
            NoiseLevelEmoji = "❌";
        }
    }

    /// <summary>
    /// 看门狗：如果超过 5 秒没收到音频数据（说明设备休眠后失效），自动重启
    /// </summary>
    private void StartWatchdog()
    {
        _watchdogTimer?.Dispose();
        _watchdogTimer = new System.Timers.Timer(5000);
        _watchdogTimer.Elapsed += (_, _) =>
        {
            if (!_isMonitoring) return;
            if ((DateTime.Now - _lastDataReceived).TotalSeconds > 5)
            {
                System.Diagnostics.Debug.WriteLine("[DecibelMeter] 看门狗检测到数据中断，重启音频");
                Dispatcher.UIThread.Post(() =>
                {
                    StopMonitoringInternal();
                    StartMonitoringInternal();
                });
            }
        };
        _watchdogTimer.Start();
    }

    private void StopWatchdog()
    {
        _watchdogTimer?.Stop();
        _watchdogTimer?.Dispose();
        _watchdogTimer = null;
    }

    public void StopMonitoring()
    {
        StopWatchdog();
        StopMonitoringInternal();
        CloseLogWriter();
    }

    private void StopMonitoringInternal()
    {
        if (!_isMonitoring || _waveIn == null) return;

        try
        {
            _waveIn.StopRecording();
            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.RecordingStopped -= OnRecordingStopped;
            _waveIn.Dispose();
            _waveIn = null;
        }
        catch { }
        finally
        {
            _isMonitoring = false;
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        _isMonitoring = false;
        if (e.Exception != null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                StatusMessage = $"录音异常: {e.Exception.Message}";
                NoiseLevelEmoji = "⚠️";
            });
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        _lastDataReceived = DateTime.Now;
        if (e.BytesRecorded == 0) return;

        try
        {
            int samples = e.BytesRecorded / 2;
            double sum = 0;
            double peak = 0;

            for (int i = 0; i < e.BytesRecorded; i += 2)
            {
                short sample16 = (short)(e.Buffer[i] | (e.Buffer[i + 1] << 8));
                double abs = Math.Abs(sample16 / 32768.0);
                sum += abs * abs;
                if (abs > peak) peak = abs;
            }

            double rawRms = Math.Sqrt(sum / samples);

            // 从设置实时同步检测器配置
            _detector.QuietThreshold = _settings.DecibelQuietThreshold;
            _detector.GoodThreshold = _settings.DecibelGoodThreshold;
            _detector.NormalThreshold = _settings.DecibelNormalThreshold;
            _detector.NoisyThreshold = _settings.DecibelNoisyThreshold;
            _detector.SustainSeconds = _settings.NoisySustainSeconds;

            // 按真实音频块时长传 dt（samples / 采样率），不假定固定 100ms，
            // 这样「持续判定时长」在现实时间上是准的
            double dtSeconds = _waveIn?.WaveFormat is { SampleRate: > 0 } wf
                ? samples / (double)wf.SampleRate
                : 0.1;
            var result = _detector.Process(rawRms, dtSeconds);

            // 显示：仅在等级变化或进度明显变化时通知 UI（避免 10Hz 全量刷新）
            var (text, emoji, progress) = MapToDisplay(result.Level, result.SmoothedRms);
            if (result.LevelChanged || Math.Abs(progress - _lastPostedProgress) > 1.0)
            {
                _lastPostedProgress = progress;
                Dispatcher.UIThread.Post(() =>
                {
                    CurrentRms = result.SmoothedRms;
                    DisplayLevel = (int)Math.Clamp(result.SmoothedRms * 200, 0, 100);
                    NoiseLevelText = text;
                    NoiseLevelEmoji = emoji;
                    NoiseLevelProgress = progress;
                });
            }

            // 计数：持续事件驱动（事件等级已由探测器按段内占比判定）
            if (result.EventFired && result.EventLevel.HasValue)
            {
                NoiseEventRaised?.Invoke(this,
                    new NoiseEventRaisedEventArgs(result.EventLevel.Value));
            }

            WriteDebugLog(rawRms, peak, result);
        }
        catch { }
    }

    /// <summary>
    /// 平滑等级 → 显示文案/表情/进度。Noisy 等级按音量再细分为「吵闹/嘈杂」。
    /// </summary>
    private (string text, string emoji, double progress) MapToDisplay(NoiseLevel level, double smooth)
    {
        double quiet = _settings.DecibelQuietThreshold;
        double good = _settings.DecibelGoodThreshold;
        double normal = _settings.DecibelNormalThreshold;
        double noisy = _settings.DecibelNoisyThreshold;

        switch (level)
        {
            case NoiseLevel.Quiet:
                return ("安静", "🤫", Math.Clamp(smooth / quiet * 25, 0, 25));
            case NoiseLevel.Good:
                return ("良好", "🙂", 25 + Math.Clamp((smooth - quiet) / (good - quiet) * 25, 0, 25));
            case NoiseLevel.Normal:
                return ("一般", "💬", 50 + Math.Clamp((smooth - good) / (normal - good) * 25, 0, 25));
            default: // Noisy
                if (smooth >= noisy * 1.3)
                    return ("嘈杂", "📢", 95 + Math.Clamp((smooth - noisy) / 0.1 * 5, 0, 5));
                return ("吵闹", "🗣️", 75 + Math.Clamp((smooth - normal) / (noisy - normal) * 20, 0, 20));
        }
    }

    // ===== 调试日志（开发工具，设置项开关） =====

    private void EnsureLogWriter()
    {
        if (_logWriter != null) return;
        if (!_settings.EnableNoiseDebugLog) return;
        try
        {
            var dir = Plugin.ConfigFolder;
            if (string.IsNullOrEmpty(dir)) return;
            Directory.CreateDirectory(dir);

            // 每次开始监测 = 一个新文件，命名规则与 ClassIsland 自身日志一致：
            // 开始记录的 年-月-日-时-分-秒（同秒冲突时追加 -1/-2…）。
            var timestamp = DateTime.Now.ToString("y-M-d-HH-mm-ss");
            var path = Path.Combine(dir, $"NoiseDebugLog-{timestamp}.csv");
            for (int i = 1; File.Exists(path); i++)
                path = Path.Combine(dir, $"NoiseDebugLog-{timestamp}-{i}.csv");

            _logWriter = new StreamWriter(path, append: false, System.Text.Encoding.UTF8);
            _logWriter.WriteLine("Time,RawRMS,Peak,SmoothRMS,Level,EpisodeLevel,EventState,Fired");
            _logLines = 0;
        }
        catch { }
    }

    private void CloseLogWriter()
    {
        try { _logWriter?.Flush(); _logWriter?.Dispose(); } catch { }
        _logWriter = null;
    }

    private void WriteDebugLog(double rawRms, double peak, SampleResult result)
    {
        if (_logWriter == null) return;
        if (!_settings.EnableNoiseDebugLog) { CloseLogWriter(); return; }

        string eventState = !_detector.IsEpisodeActive ? "无段"
            : _detector.EpisodeFired ? "已触发" : "持续中";
        string episodeLevel = _detector.IsEpisodeActive ? _detector.Level.ToString() : "";

        _logWriter.WriteLine(
            $"{DateTime.Now:HH:mm:ss.fff},{rawRms:F4},{peak:F4},{result.SmoothedRms:F4}," +
            $"{result.Level},{episodeLevel},{eventState},{result.EventFired}");
        if (++_logLines >= 20) { _logWriter.Flush(); _logLines = 0; }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void Dispose()
    {
        StopMonitoring();
        GC.SuppressFinalize(this);
    }
}
