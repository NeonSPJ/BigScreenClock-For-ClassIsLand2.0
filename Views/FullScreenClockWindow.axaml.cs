using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using EveningSelfStudyClock.ViewModels;

namespace EveningSelfStudyClock.Views;

public partial class FullScreenClockWindow : Window
{
    public FullScreenClockWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
    }

    private void ExitButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is FullScreenClockViewModel vm)
            vm.ExitFullScreen();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.F11)
        {
            if (DataContext is FullScreenClockViewModel vm)
                vm.ExitFullScreen();
            e.Handled = true;
        }
    }
}
