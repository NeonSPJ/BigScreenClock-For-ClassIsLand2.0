using System.Reflection;

namespace EveningSelfStudyClock.Services;

/// <summary>
/// 从 CI 主界面组件的设置对象中提取倒计时/文本框数据。
/// 组件设置类是 CI 主程序（ClassIsland.dll）里的类型（如 CountDownComponentSettings），
/// 插件不引用主程序程序集，只能按类型名反射读取。
/// </summary>
public static class CiComponentsReader
{
    /// <summary>CI 倒计时组件设置类的类型名。</summary>
    public const string CountdownSettingsTypeName = "CountDownComponentSettings";

    /// <summary>CI 文本组件设置类的类型名。</summary>
    public const string TextSettingsTypeName = "TextComponentSettings";

    /// <summary>
    /// 从单个组件设置对象提取数据。
    /// </summary>
    /// <returns>(倒计时标题, 倒计时剩余文本, 文本框内容)；该组件不是倒计时/文本框时为 (null, null, null)。</returns>
    public static (string? Title, string? Remaining, string? Text) Extract(object? settings, DateTime now)
    {
        if (settings is null) return (null, null, null);
        var type = settings.GetType();

        if (type.Name == CountdownSettingsTypeName)
        {
            var title = GetProp<string>(settings, "CountDownName");
            var overTime = GetProp<DateTime?>(settings, "OverTime");
            var daysLeft = GetProp<int?>(settings, "DaysLeft");
            var remaining = FormatRemaining(overTime ?? DateTime.MinValue, daysLeft, now);
            return (string.IsNullOrWhiteSpace(title) ? null : title, remaining, null);
        }

        if (type.Name == TextSettingsTypeName)
        {
            var text = GetProp<string>(settings, "TextContent");
            return (null, null, string.IsNullOrWhiteSpace(text) ? null : text);
        }

        return (null, null, null);
    }

    /// <summary>
    /// 剩余时间文本。OverTime 是用户明确配置的目标日期，优先按它实时计算；
    /// DaysLeft（CI 存字段，未初始化时默认 0）只在 OverTime 无效时兜底。
    /// </summary>
    public static string? FormatRemaining(DateTime overTime, int? daysLeft, DateTime now)
    {
        int days;
        if (overTime != DateTime.MinValue)
        {
            days = (int)Math.Ceiling((overTime - now).TotalDays);
        }
        else if (daysLeft is not null)
        {
            days = daysLeft.Value;
        }
        else return null;

        return days switch
        {
            > 1 => $"还有 {days} 天",
            1 => "还有 1 天",
            0 => "就是今天",
            _ => $"已过 {-days} 天",
        };
    }

    private static T? GetProp<T>(object obj, string name)
    {
        var prop = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (prop is null) return default;
        var v = prop.GetValue(obj);
        return v is null ? default : (T)v;
    }
}
