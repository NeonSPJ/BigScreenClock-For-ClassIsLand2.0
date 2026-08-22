using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace EveningSelfStudyClock.Views;

/// <summary>
/// 关于窗口：显示制作者、版本号（正式/测试版）、GitHub 仓库链接。
/// 60 秒自动关闭，点击窗口外（失焦）也会关闭。
/// </summary>
public partial class AboutPopupWindow : Window
{
    private const string RepoUrl = "https://github.com/Human-air/BigScreenClock-For-ClassIsLand2.0";

    /// <summary>从插件目录的 manifest.yml 动态读取版本号，不再写死（避免升级后忘记同步）。</summary>
    private static readonly string PluginVersion = ReadManifestVersion();

    private readonly DispatcherTimer _autoCloseTimer;
    private readonly DateTime _shownAt = DateTime.Now;

    /// <summary>
    /// 读取插件安装目录下 manifest.yml 的 version 字段。
    /// manifest.yml 由 csproj 复制到输出目录，与 DLL 同目录，运行时可取到。
    /// </summary>
    private static string ReadManifestVersion()
    {
        try
        {
            var dir = Path.GetDirectoryName(typeof(AboutPopupWindow).Assembly.Location);
            if (!string.IsNullOrEmpty(dir))
            {
                var manifestPath = Path.Combine(dir, "manifest.yml");
                if (File.Exists(manifestPath))
                {
                    foreach (var line in File.ReadAllLines(manifestPath))
                    {
                        var trimmed = line.TrimStart();
                        if (trimmed.StartsWith("version:", StringComparison.OrdinalIgnoreCase))
                        {
                            var v = trimmed["version:".Length..].Trim().Trim('"', '\'');
                            if (v.Length > 0) return v;
                        }
                    }
                }
            }
        }
        catch { }
        return "未知";
    }

    public AboutPopupWindow()
    {
        InitializeComponent();

        // 版本号 + 正式/测试版判定：四位版本号最后一位非 0 = 测试版
        VersionNumberText.Text = $"版本号：{PluginVersion}";
        bool isTest = ParseLastDigit(PluginVersion) != 0;
        VersionTypeText.Text = isTest ? "测试版" : "正式版";
        VersionTypeText.Foreground = isTest
            ? new SolidColorBrush(Color.Parse("#ff5555"))   // 红 = 测试版
            : new SolidColorBrush(Color.Parse("#4caf50"));  // 绿 = 正式版

        // 点击链接打开浏览器
        GitHubLinkText.PointerPressed += (_, _) => OpenRepository();

        // 60 秒自动关闭
        _autoCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _autoCloseTimer.Tick += (_, _) => Close();
        _autoCloseTimer.Start();

        // 点击窗口外 → 失焦 → 关闭（加 0.5 秒宽限期，避免刚显示就误关）
        Deactivated += (_, _) =>
        {
            if ((DateTime.Now - _shownAt).TotalMilliseconds > 500) Close();
        };
        Closed += (_, _) => _autoCloseTimer.Stop();
    }

    private static int ParseLastDigit(string version)
    {
        var parts = version.Split('.');
        return parts.Length >= 4 && int.TryParse(parts[^1], out var n) ? n : 0;
    }

    private void OpenRepository()
    {
        try
        {
            Process.Start(new ProcessStartInfo(RepoUrl) { UseShellExecute = true });
        }
        catch { }
    }
}
