using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared.Enums;
using EveningSelfStudyClock.Models;
using EveningSelfStudyClock.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EveningSelfStudyClock.Services;

/// <summary>
/// 自动触发服务：监听课程事件，在目标课程（如晚自习）时自动显示全屏时钟窗口。
///
/// 触发逻辑：
/// - OnClass 事件 + 当前课程匹配目标 → 显示时钟（幂等）
/// - OnClass 事件 + 当前不匹配 + 时钟正在显示 → 隐藏时钟
/// - AfterSchool → 隐藏时钟
/// - Breaking 课间不做操作（连堂自然保持显示）
/// - 窗口被手动关闭后，下一节目标课程 OnClass 会自动重新打开（天然幂等）
/// </summary>
public class AutoTriggerService : BackgroundService
{
    private readonly PluginSettings _settings;
    private readonly FullScreenClockViewModel _viewModel;
    private readonly IServiceProvider _serviceProvider;
    private ILessonsService? _lessonsService;

    public AutoTriggerService(
        PluginSettings settings,
        FullScreenClockViewModel viewModel,
        IServiceProvider serviceProvider)
    {
        _settings = settings;
        _viewModel = viewModel;
        _serviceProvider = serviceProvider;

        // 捕获 ServiceProvider 供 SettingsPage 使用
        Plugin.ServiceProvider = serviceProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 等待应用启动完成
        await Task.Delay(1000, stoppingToken);

        try
        {
            _lessonsService = _serviceProvider.GetService<ILessonsService>();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AutoTrigger] 无法获取 ILessonsService: {ex}");
            return;
        }

        if (_lessonsService == null)
        {
            System.Diagnostics.Debug.WriteLine("[AutoTrigger] ILessonsService 不可用，自动触发禁用");
            return;
        }

        // 监听关键事件
        _lessonsService.OnClass += OnClassOrStateChanged;
        _lessonsService.OnAfterSchool += OnAfterSchool;
        _lessonsService.CurrentTimeStateChanged += OnClassOrStateChanged;

        // 初始检查
        DoCheck();

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // 正常取消
        }
        finally
        {
            _lessonsService.OnClass -= OnClassOrStateChanged;
            _lessonsService.OnAfterSchool -= OnAfterSchool;
            _lessonsService.CurrentTimeStateChanged -= OnClassOrStateChanged;
        }
    }

    private void OnClassOrStateChanged(object? sender, EventArgs e)
    {
        DoCheck();
    }

    private void OnAfterSchool(object? sender, EventArgs e)
    {
        _viewModel.Hide();
    }

    private void DoCheck()
    {
        if (_lessonsService == null) return;

        var currentState = _lessonsService.CurrentState;
        var currentSubject = _lessonsService.CurrentSubject;

        if (currentState == TimeState.OnClass)
        {
            string? subjectName = currentSubject?.Name;
            if (_settings.IsTargetCourse(subjectName))
            {
                // 进入目标课程 → 显示时钟（Show 幂等，已显示时无操作）
                _viewModel.Show();
            }
            else if (_viewModel.IsWindowVisible)
            {
                // 进入非目标课程且时钟开着 → 关闭
                _viewModel.Hide();
            }
        }
    }

    /// <summary>
    /// 手动显示全屏时钟（供自动化/手动调用）
    /// </summary>
    public void ManualShow() => _viewModel.Show();

    /// <summary>
    /// 手动隐藏全屏时钟
    /// </summary>
    public void ManualHide() => _viewModel.Hide();

    /// <summary>
    /// 当前是否正在显示
    /// </summary>
    public bool IsShowing => _viewModel.IsWindowVisible;
}
