using System;

namespace EveningSelfStudyClock.NoiseDetection;

/// <summary>
/// 平滑后的噪音等级。Noisy 同时覆盖显示上的「吵闹」和「嘈杂」。
/// </summary>
public enum NoiseLevel
{
    Quiet = 0,
    Good = 1,
    Normal = 2,
    Noisy = 3
}

/// <summary>
/// 单块（约 100ms）检测结果。
/// </summary>
public sealed class SampleResult
{
    public NoiseLevel Level;
    public bool LevelChanged;
    public double SmoothedRms;
    public bool EventFired;
    public NoiseLevel? EventLevel;
}

/// <summary>
/// V2 噪音事件检测器（纯算法，无任何外部依赖，便于单元测试）。
/// 职责：EMA 平滑 + 迟滞分级 + 持续判定状态机。
/// 输入：每块原始 RMS + 真实块时长；输出：显示等级 + 持续事件。
///
/// 「持续一段算一次」规则：
/// - 进入可计等级（一般/吵闹）后开段，分别累计段内「一般」「吵闹」的时长；
/// - 累计达到 ≥ SustainSeconds 产一次事件，事件归属按段内占比判定：
///   「吵闹」时长占比 ≥ NoisyRatioThreshold（默认 0.5）→ 记吵闹，否则记一般；
/// - 每段至多一次事件，段内一般↔吵闹来回波动不重置计时、不重触发（归属只看
///   到触发那一刻为止的占比，触发即定格）；
/// - 掉到不可计等级后进入宽限期（GraceSeconds），宽限期内短暂回落（如说话停顿）不断段，
///   累计时长不清零；超过宽限期段才结束。
/// </summary>
public sealed class NoiseEventDetector
{
    public double QuietThreshold { get; set; } = 0.005;
    public double GoodThreshold { get; set; } = 0.015;
    public double NormalThreshold { get; set; } = 0.04;
    public double NoisyThreshold { get; set; } = 0.08;
    public double SmoothAlpha { get; set; } = 0.2;
    public double HysteresisFactor { get; set; } = 0.8;
    public double SustainSeconds { get; set; } = 1.5;

    /// <summary>
    /// 段内「吵闹」时长占比达到该比例，本段即归属为「吵闹」；否则归属「一般」。
    /// 用于一般↔吵闹来回波动时按主体音量判定。默认 0.5 = 吵闹占一半及以上算吵闹。
    /// </summary>
    public double NoisyRatioThreshold { get; set; } = 0.5;

    /// <summary>
    /// 掉到不可计等级（良好/安静）后的宽限期（秒）。宽限期内短暂回落不断段、
    /// 累计时长不清零，用于容忍说话之间的小停顿；超过宽限期才算段结束。
    /// </summary>
    public double GraceSeconds { get; set; } = 1.0;

    private double _smooth;
    private bool _initialized;
    private NoiseLevel _level = NoiseLevel.Quiet;
    private double _time;

    // 持续段状态
    private bool _episodeActive;          // 当前是否有可计持续段
    private double _episodeNormalSeconds; // 段内「一般」累计时长（秒）
    private double _episodeNoisySeconds;  // 段内「吵闹」累计时长（秒）
    private bool _episodeFired;           // 本段是否已触发事件
    private double _graceLeft;            // 掉到不可计等级后的剩余宽限期（秒）

    public NoiseLevel Level => _level;
    public double SmoothedRms => _smooth;

    /// <summary>当前是否处于可计持续段。</summary>
    public bool IsEpisodeActive => _episodeActive;

    /// <summary>当前段是否已触发事件。</summary>
    public bool EpisodeFired => _episodeFired;

    public void Reset()
    {
        _initialized = false;
        _smooth = 0;
        _level = NoiseLevel.Quiet;
        _time = 0;
        _episodeActive = false;
        _episodeNormalSeconds = 0;
        _episodeNoisySeconds = 0;
        _episodeFired = false;
        _graceLeft = 0;
    }

    /// <summary>
    /// 处理一块音频。dtSeconds 为这块的真实时长（秒），由调用方按实际音频块长传入；
    /// 默认 0.1 对应 100ms 采样块，纯算法测试用。
    /// </summary>
    public SampleResult Process(double rawRms, double dtSeconds = 0.1)
    {
        if (dtSeconds <= 0) dtSeconds = 0.1;
        _time += dtSeconds;

        if (!_initialized)
        {
            _smooth = rawRms;
            _initialized = true;
        }
        else
        {
            _smooth = _smooth * (1 - SmoothAlpha) + rawRms * SmoothAlpha;
        }

        var prev = _level;
        _level = Classify(_smooth, _level);

        NoiseLevel? fired = null;

        if (IsCountable(_level))
        {
            _graceLeft = 0;

            if (!_episodeActive)
            {
                // 新开段
                _episodeActive = true;
                _episodeNormalSeconds = 0;
                _episodeNoisySeconds = 0;
                _episodeFired = false;
            }

            // 段内分别累计一般/吵闹时长（一般↔吵闹波动不影响累计，只影响占比）
            if (_level == NoiseLevel.Noisy) _episodeNoisySeconds += dtSeconds;
            else _episodeNormalSeconds += dtSeconds;

            // 持续够了且本段还没触发 → 按段内占比判定归属，每段只记一次（触发即定格）
            if (!_episodeFired)
            {
                var total = _episodeNormalSeconds + _episodeNoisySeconds;
                if (total >= SustainSeconds)
                {
                    _episodeFired = true;
                    var noisyRatio = total > 0 ? _episodeNoisySeconds / total : 0;
                    fired = noisyRatio >= NoisyRatioThreshold ? NoiseLevel.Noisy : NoiseLevel.Normal;
                }
            }
        }
        else
        {
            // 掉到不可计等级：进入宽限期，宽限期过才断段
            if (_episodeActive)
            {
                _graceLeft += dtSeconds;
                if (_graceLeft >= GraceSeconds)
                {
                    _episodeActive = false;
                    _episodeNormalSeconds = 0;
                    _episodeNoisySeconds = 0;
                    _episodeFired = false;
                    _graceLeft = 0;
                }
            }
        }

        return new SampleResult
        {
            Level = _level,
            LevelChanged = prev != _level,
            SmoothedRms = _smooth,
            EventFired = fired.HasValue,
            EventLevel = fired
        };
    }

    private static bool IsCountable(NoiseLevel level)
        => level == NoiseLevel.Normal || level == NoiseLevel.Noisy;

    /// <summary>
    /// 迟滞分级（Schmitt 触发）：升级用名义阈值，降级用名义阈值 × HysteresisFactor。
    /// </summary>
    private NoiseLevel Classify(double rms, NoiseLevel current)
    {
        double goodDown = GoodThreshold * HysteresisFactor;
        double normalDown = NormalThreshold * HysteresisFactor;
        double noisyDown = NoisyThreshold * HysteresisFactor;

        switch (current)
        {
            case NoiseLevel.Quiet:
                if (rms >= NoisyThreshold) return NoiseLevel.Noisy;
                if (rms >= NormalThreshold) return NoiseLevel.Normal;
                if (rms >= GoodThreshold) return NoiseLevel.Good;
                return NoiseLevel.Quiet;
            case NoiseLevel.Good:
                if (rms >= NoisyThreshold) return NoiseLevel.Noisy;
                if (rms >= NormalThreshold) return NoiseLevel.Normal;
                if (rms < goodDown) return NoiseLevel.Quiet;
                return NoiseLevel.Good;
            case NoiseLevel.Normal:
                if (rms >= NoisyThreshold) return NoiseLevel.Noisy;
                if (rms < normalDown)
                    return rms < goodDown ? NoiseLevel.Quiet : NoiseLevel.Good;
                return NoiseLevel.Normal;
            case NoiseLevel.Noisy:
                if (rms < noisyDown)
                    return rms >= NormalThreshold ? NoiseLevel.Normal
                         : rms >= GoodThreshold ? NoiseLevel.Good
                         : NoiseLevel.Quiet;
                return NoiseLevel.Noisy;
            default:
                return current;
        }
    }
}
