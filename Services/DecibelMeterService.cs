using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using EveningSelfStudyClock.Models;
using NAudio.Wave;

namespace EveningSelfStudyClock.Services;

/// <summary>
/// 麦克风分贝检测服务。使用 NAudio 采集音频并计算音量百分比。
/// </summary>
public class DecibelMeterService : INotifyPropertyChanged, IDisposable
{
    private readonly PluginSettings _settings;
    private WaveInEvent? _waveIn;
    private DateTime _lastDataReceived;
    private System.Timers.Timer? _watchdogTimer;
    private double _currentRms;
    private int _displayLevel; // 0-100 正数显示
    private string _noiseLevelText = "等待检测...";
    private string _noiseLevelEmoji = "🤫";
    private double _noiseLevelProgress;
    private bool _isMonitoring;
    private string _statusMessage = "";

    public DecibelMeterService(PluginSettings settings)
    {
        _settings = settings;
    }

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
    /// 获取可用麦克风设备列表
    /// </summary>
    public static List<(int Index, string Name)> GetMicrophoneDevices()
    {
        var devices = new List<(int, string)>();
        for (int i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            var caps = WaveInEvent.GetCapabilities(i);
            devices.Add((i, caps.ProductName));
        }
        return devices;
    }

    /// <summary>
    /// 开始监听。首选上次选择的设备，失败则自动搜索可用设备。
    /// </summary>
    public void StartMonitoring()
    {
        if (_isMonitoring) return;
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

            int[] tryOrder = Enumerable.Range(0, count).ToArray();
            int preferred = _settings.MicrophoneDeviceIndex;
            if (preferred >= 0 && preferred < count)
                tryOrder = new[] { preferred }.Concat(tryOrder.Where(i => i != preferred)).ToArray();

            foreach (var idx in tryOrder)
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
                    _settings.MicrophoneDeviceIndex = idx;
                    StatusMessage = "";
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

            for (int i = 0; i < e.BytesRecorded; i += 2)
            {
                short sample16 = (short)(e.Buffer[i] | (e.Buffer[i + 1] << 8));
                double sample = sample16 / 32768.0;
                sum += sample * sample;
            }

            double rms = Math.Sqrt(sum / samples);

            // 映射到 0-100 正数显示级别
            // RMS 0.0 ~ 0.5 映射到 0 ~ 100
            int level = (int)Math.Clamp(rms * 200, 0, 100);

            var (text, emoji, progress) = MapToNoiseLevel(rms);

            Dispatcher.UIThread.Post(() =>
            {
                CurrentRms = rms;
                DisplayLevel = level;
                NoiseLevelText = text;
                NoiseLevelEmoji = emoji;
                NoiseLevelProgress = progress;
            });
        }
        catch { }
    }

    private (string text, string emoji, double progress) MapToNoiseLevel(double rms)
    {
        if (rms <= _settings.DecibelQuietThreshold)
            return ("安静", "🤫", Math.Clamp(rms / _settings.DecibelQuietThreshold * 25, 0, 25));
        if (rms <= _settings.DecibelGoodThreshold)
            return ("良好", "🙂", 25 + Math.Clamp((rms - _settings.DecibelQuietThreshold) / (_settings.DecibelGoodThreshold - _settings.DecibelQuietThreshold) * 25, 0, 25));
        if (rms <= _settings.DecibelNormalThreshold)
            return ("一般", "💬", 50 + Math.Clamp((rms - _settings.DecibelGoodThreshold) / (_settings.DecibelNormalThreshold - _settings.DecibelGoodThreshold) * 25, 0, 25));
        if (rms <= _settings.DecibelNoisyThreshold)
            return ("吵闹", "🗣️", 75 + Math.Clamp((rms - _settings.DecibelNormalThreshold) / (_settings.DecibelNoisyThreshold - _settings.DecibelNormalThreshold) * 20, 0, 20));
        return ("嘈杂", "📢", 95 + Math.Clamp((rms - _settings.DecibelNoisyThreshold) / 0.1 * 5, 0, 5));
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
