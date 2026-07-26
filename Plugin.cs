using ClassIsland.Core;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using ClassIsland.Shared.Helpers;
using EveningSelfStudyClock.Models;
using EveningSelfStudyClock.Services;
using EveningSelfStudyClock.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EveningSelfStudyClock;

[PluginEntrance]
public class Plugin : PluginBase
{
    public static PluginSettings? Settings { get; private set; }
    public static IServiceProvider? ServiceProvider { get; set; }
    public static string? ConfigFolder { get; private set; }

    /// <summary>
    /// 主动保存设置到文件
    /// </summary>
    public static void SaveSettings()
    {
        if (Settings == null || ConfigFolder == null) return;
        var path = Path.Combine(ConfigFolder, "Settings.json");
        var dir = Path.GetDirectoryName(path);
        if (dir != null) Directory.CreateDirectory(dir);
        ConfigureFileHelper.SaveConfig(path, Settings);
    }

    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        ConfigFolder = PluginConfigFolder;

        Settings = ConfigureFileHelper.LoadConfig<PluginSettings>(
            Path.Combine(ConfigFolder, "Settings.json"));

        // 任何属性变更都触发保存
        Settings.PropertyChanged += (_, _) => SaveSettings();

        services.AddSingleton(Settings);

        services.AddSingleton<DecibelMeterService>();
        services.AddSingleton<FullScreenClockViewModel>();
        services.AddSingleton<AutoTriggerService>();

        services.AddHostedService<ServiceProviderCapture>();
        services.AddHostedService<AutoTriggerService>();

        services.AddSettingsPage<EveningSelfStudyClock.Settings.SettingsPage>();
    }
}
