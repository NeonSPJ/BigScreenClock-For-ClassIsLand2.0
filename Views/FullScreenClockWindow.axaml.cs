using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using EveningSelfStudyClock.ViewModels;

namespace EveningSelfStudyClock.Views;

public partial class FullScreenClockWindow : Window
{
    private CancellationTokenSource? _hideCursorCts;
    private AboutPopupWindow? _aboutWindow;

    /// <summary>右上角日期连点计数（间隔超过 3 秒视为中断）</summary>
    private int _dateClickCount;
    private DateTime _lastDateClick = DateTime.MinValue;

    public FullScreenClockWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
        PointerMoved += OnPointerMoved;
        RulesPopup.PlacementTarget = CounterAreaBorder; // 规则浮层锚定在计数区域上方
    }

    private void DateText_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        var now = DateTime.Now;
        if ((now - _lastDateClick).TotalSeconds > 3) _dateClickCount = 0;
        _lastDateClick = now;
        _dateClickCount++;

        if (_dateClickCount >= 7)
        {
            _dateClickCount = 0;
            ShowAboutPopup();
        }
    }

    private void ShowAboutPopup()
    {
        if (_aboutWindow != null && _aboutWindow.IsVisible) return;
        _aboutWindow = new AboutPopupWindow();
        _aboutWindow.Closed += (_, _) => _aboutWindow = null;
        _aboutWindow.Show(this); // 以全屏窗口为 Owner（置顶、随主窗口关闭）
    }

    private void ExitButton_Click(object? sender, RoutedEventArgs e)
    {
        (DataContext as FullScreenClockViewModel)?.ExitFullScreen();
    }

    /// <summary>鼠标进入计数区域：显示底部记录规则</summary>
    private void CounterArea_PointerEntered(object? sender, PointerEventArgs e)
    {
        if (DataContext is FullScreenClockViewModel vm) vm.ShowCountRules = true;
    }

    /// <summary>鼠标离开计数区域：隐藏记录规则，仅留免责声明</summary>
    private void CounterArea_PointerExited(object? sender, PointerEventArgs e)
    {
        if (DataContext is FullScreenClockViewModel vm) vm.ShowCountRules = false;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.F11)
        {
            (DataContext as FullScreenClockViewModel)?.ExitFullScreen();
            e.Handled = true;
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        Cursor = Cursor.Default;
        _hideCursorCts?.Cancel();
        _hideCursorCts = new CancellationTokenSource();
        var token = _hideCursorCts.Token;
        Task.Delay(10000, token).ContinueWith(_ =>
        {
            if (!token.IsCancellationRequested)
                Dispatcher.UIThread.Post(() => Cursor = null);
        }, TaskScheduler.Default);
    }
}
