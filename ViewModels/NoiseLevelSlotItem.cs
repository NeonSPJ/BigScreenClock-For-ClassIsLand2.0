using System.ComponentModel;
using Avalonia.Media;

namespace EveningSelfStudyClock.ViewModels;

/// <summary>
/// 音量条档位文字的数据项（安静/良好/一般/吵闹/嘈杂）。
/// 当前档位高亮放大、其余淡化，由 <see cref="IsCurrent"/> 驱动。
/// </summary>
public class NoiseLevelSlotItem : INotifyPropertyChanged
{
    private bool _isCurrent;

    public NoiseLevelSlotItem(string text, IBrush brush)
    {
        Text = text;
        Brush = brush;
    }

    /// <summary>档位名（安静/良好/一般/吵闹/嘈杂）。</summary>
    public string Text { get; }

    /// <summary>档位色（绿→黄绿→黄→橙→红）。</summary>
    public IBrush Brush { get; }

    /// <summary>是否当前档位。变化时同时触发文字透明度/字号刷新。</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value) return;
            _isCurrent = value;
            OnPropertyChanged(nameof(IsCurrent));
            OnPropertyChanged(nameof(TextOpacity));
            OnPropertyChanged(nameof(TextFontSize));
        }
    }

    /// <summary>当前档位全亮、其余淡化。</summary>
    public double TextOpacity => IsCurrent ? 1.0 : 0.35;

    /// <summary>当前档位放大、其余缩小。</summary>
    public double TextFontSize => IsCurrent ? 19 : 15;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
