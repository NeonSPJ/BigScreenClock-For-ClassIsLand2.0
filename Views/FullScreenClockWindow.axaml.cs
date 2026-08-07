using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using EveningSelfStudyClock.ViewModels;

namespace EveningSelfStudyClock.Views;

public partial class FullScreenClockWindow : Window
{
    private CancellationTokenSource? _hideCursorCts;

    public FullScreenClockWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
        PointerMoved += OnPointerMoved;
    }

    private void ExitButton_Click(object? sender, RoutedEventArgs e)
    {
        (DataContext as FullScreenClockViewModel)?.ExitFullScreen();
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
