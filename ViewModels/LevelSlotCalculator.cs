namespace EveningSelfStudyClock.ViewModels;

/// <summary>
/// 音量档位 → 索引/颜色的纯计算类（无 Avalonia 依赖，供单元测试源文件编译）。
/// 档位边界与 DecibelMeterService.MapToDisplay 硬编码的 25/50/75/95 保持一致：
/// 0-安静 / 1-良好 / 2-一般 / 3-吵闹 / 4-嘈杂。
/// </summary>
public static class LevelSlotCalculator
{
    /// <summary>由 MapToDisplay 的 0–100 进度反推档位索引。</summary>
    public static int SlotFromProgress(double progress) => progress switch
    {
        < 25 => 0,
        < 50 => 1,
        < 75 => 2,
        < 95 => 3,
        _ => 4
    };

    /// <summary>五档色（ARGB，按吵闹程度：绿→黄绿→黄→橙→红），与 XAML 档位文字/轨道填充共用。</summary>
    public static readonly uint[] SlotColors =
        { 0xFF4CAF50, 0xFFAED581, 0xFFFFDD44, 0xFFFF9933, 0xFFFF5555 };
}
