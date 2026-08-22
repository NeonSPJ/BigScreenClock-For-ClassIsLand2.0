using System;

namespace EveningSelfStudyClock.NoiseDetection;

/// <summary>一次持续事件在计数层的处理结果。</summary>
public enum CountDecision
{
    None = 0,
    CountNormal = 1,
    CountNoisy = 2
}

/// <summary>
/// V2 计数规则（纯算法，无任何外部依赖，便于单元测试）。
/// 职责：把检测器产出的「持续事件」应用 冷却 / 保护 规则，维护 一般/吵闹 计数。
///
/// 「持续一段算一次、段内按占比归属」由 NoiseEventDetector 保证（每段至多一次事件）；
/// 本类负责把事件变成计数：
/// - 一般、吵闹各自独立冷却，互不阻挡（一般持续几秒能记上一般，不会被吵闹的冷却挡住）；
/// - 课间 / 上课初期保护期内不计数。
/// </summary>
public sealed class NoiseCounter
{
    /// <summary>每个等级计数后的冷却时间（秒）。一般、吵闹各自独立。</summary>
    public double CooldownSeconds { get; set; } = 60;

    private bool _inBreak;
    private DateTime _lastNormalTime = DateTime.MinValue;
    private DateTime _lastNoisyTime = DateTime.MinValue;

    public int NormalCount { get; private set; }
    public int NoisyCount { get; private set; }

    /// <summary>课间状态。课间内的任何事件都不计数。</summary>
    public void SetBreak(bool inBreak) => _inBreak = inBreak;

    /// <summary>新一节课开始：清空计数（冷却计时保留，与 v1 行为一致）。</summary>
    public void StartNewClass()
    {
        NormalCount = 0;
        NoisyCount = 0;
    }

    /// <summary>
    /// 处理一次持续事件。
    /// isProtected = 上课初期保护期内（不计数）。
    /// </summary>
    public CountDecision OnEvent(NoiseLevel level, DateTime now, bool isProtected)
    {
        if (_inBreak || isProtected) return CountDecision.None;
        if (level != NoiseLevel.Normal && level != NoiseLevel.Noisy) return CountDecision.None;

        if (level == NoiseLevel.Noisy)
        {
            // 吵闹有独立的冷却，不被一般计数影响
            if ((now - _lastNoisyTime).TotalSeconds < CooldownSeconds) return CountDecision.None;

            NoisyCount++;
            _lastNoisyTime = now;
            return CountDecision.CountNoisy;
        }
        else // NoiseLevel.Normal
        {
            if ((now - _lastNormalTime).TotalSeconds < CooldownSeconds) return CountDecision.None;

            NormalCount++;
            _lastNormalTime = now;
            return CountDecision.CountNormal;
        }
    }
}
