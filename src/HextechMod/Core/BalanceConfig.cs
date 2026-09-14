using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using UnityEngine;

namespace PeakModder.HextechMod;

/// <summary>
/// 服务器平衡配置（2026-09-14）：启动时从服务器拉 <c>balance.json</c>，按「类名.字段名」反射覆盖
/// 技能与词条的可调静态字段 —— 作者用配置器微调数值不用发新版，玩家启动时拉到什么用什么。
/// <para>
/// ⚠️ 时序：必须在 <see cref="HextechPlugin.Awake"/> 最开头调（早于词条注册与
/// <see cref="SkillRegistry"/> 静态构造 —— 技能定义在注册那一刻就把能量 / 冷却拷进了实例属性）。
/// </para>
/// <para>
/// 兜底链：服务器拉到 → 存缓存；拉不到 → 用上次的缓存；都没有 → 全默认值。
/// 任何异常只写日志，绝不影响正常进游戏。
/// </para>
/// <para>
/// 白名单 = <see cref="TunableTypes"/> 里「public static 的 float / int 字段」（const 自动跳过）——
/// 想让某个数值可配置，把它从 const 改成 static 即可，不用来这里登记。
/// 文件格式：<c>{"version":2,"overrides":{"DefaultHextechs.ThickHidePerStack":0.8}}</c>，
/// 与配置器（HextechMod.Configurator）的输出一致。
/// </para>
/// </summary>
internal static class BalanceConfig
{
    /// <summary>可被配置覆盖的静态字段所在类型。</summary>
    private static readonly Type[] TunableTypes =
    {
        typeof(DefaultHextechs),
        typeof(AdvancedHextechs),
        typeof(SkillRegistry),
        typeof(HextechAdvancedPatches),
        typeof(HextechAdvancedPatches.MechanicalHand),
        typeof(HextechAdvancedPatches.RefinedStomach),
        typeof(HextechQualityMath),
        typeof(HextechState),
    };

    private const int TimeoutMilliseconds = 2500;

    /// <summary>本次启动实际生效的覆盖数（日志报告里会带上，方便确认配置有没有拉到）。</summary>
    public static int AppliedCount { get; private set; }

    private static Dictionary<string, FieldInfo>? _index;

    /// <summary>拉取并应用服务器平衡配置。阻塞最多 2.5 秒（启动加载期，可接受）。</summary>
    public static void Load()
    {
        try
        {
            var text = TryDownload() ?? TryReadCache();

            if (string.IsNullOrEmpty(text))
            {
                HextechPlugin.Log.LogInfo("[平衡] 没拉到服务器配置，全部用默认数值。");
                return;
            }

            TryWriteCache(text);
            Apply(text);
        }
        catch (Exception exception)
        {
            HextechPlugin.Log.LogWarning($"[平衡] 配置加载失败（用默认数值）：{exception.Message}");
        }
    }

    private static void Apply(string text)
    {
        var index = GetFieldIndex();
        var applied = 0;

        // 手写正则解析 {"key": number} 对 —— 配置格式固定，不值得为它引一个 JSON 库。
        foreach (Match match in Regex.Matches(
            text,
            "\"(?<key>[A-Za-z_][\\w.]*)\"\\s*:\\s*(?<value>-?\\d+(?:\\.\\d+)?)"))
        {
            var key = match.Groups["key"].Value;

            if (key == "version" || !index.TryGetValue(key, out var field))
            {
                continue;
            }

            if (!float.TryParse(
                match.Groups["value"].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value))
            {
                continue;
            }

            try
            {
                if (field.FieldType == typeof(int))
                {
                    field.SetValue(null, Mathf.RoundToInt(value));
                }
                else
                {
                    field.SetValue(null, value);
                }

                applied++;
                HextechPlugin.Log.LogInfo($"[平衡] {key} = {value}");
            }
            catch (Exception exception)
            {
                HextechPlugin.Log.LogWarning($"[平衡] 覆盖 {key} 失败：{exception.Message}");
            }
        }

        AppliedCount = applied;
        HextechPlugin.Log.LogInfo($"[平衡] 生效覆盖 {applied} 项。");
    }

    /// <summary>
    /// 「类型名.字段名」→ 字段 的索引，只收 public static 的 float / int（const 自动跳过）。
    /// ⚠️ 嵌套类型（机械手 / 精致胃袋）同时登记**短键**（MechanicalHand.ExtraDistance）与
    /// **全链键**（HextechAdvancedPatches.MechanicalHand.ExtraDistance）两个别名 ——
    /// 配置器用的是全链键，漏了别名配置就静默不生效（2026-09-14 修）。
    /// </summary>
    private static Dictionary<string, FieldInfo> GetFieldIndex()
    {
        if (_index != null)
        {
            return _index;
        }

        _index = new Dictionary<string, FieldInfo>(StringComparer.Ordinal);

        foreach (var type in TunableTypes)
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.IsLiteral
                    || (field.FieldType != typeof(float) && field.FieldType != typeof(int)))
                {
                    continue;
                }

                _index[type.Name + "." + field.Name] = field;

                if (type.DeclaringType != null)
                {
                    _index[type.DeclaringType.Name + "." + type.Name + "." + field.Name] = field;
                }
            }
        }

        return _index;
    }

    /// <summary>balance.json 与 version.json 同目录：由内置更新源地址推导，没注入地址就返回 null。</summary>
    private static string? BalanceUrl()
    {
        var manifest = UpdateFeed.ManifestUrl;

        if (string.IsNullOrWhiteSpace(manifest))
        {
            return null;
        }

        var separator = manifest.LastIndexOf('/');

        return separator < 0 ? null : manifest.Substring(0, separator + 1) + "balance.json";
    }

    private static string? TryDownload()
    {
        var url = BalanceUrl();

        if (url == null)
        {
            return null;
        }

        try
        {
            // 与 LogReporter 通道一同款：Mono 的 HttpWebRequest、关掉代理自动探测、短超时。
            ServicePointManager.Expect100Continue = false;

            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Timeout = TimeoutMilliseconds;
            request.ReadWriteTimeout = TimeoutMilliseconds;
            request.Proxy = null;
            request.UserAgent = "HextechMod-Balance";

            using var response = request.GetResponse();
            using var stream = response.GetResponseStream();

            if (stream == null)
            {
                return null;
            }

            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();

            HextechPlugin.Log.LogInfo("[平衡] 服务器配置拉取成功。");
            return text;
        }
        catch (Exception exception)
        {
            HextechPlugin.Log.LogInfo($"[平衡] 服务器配置拉取失败（尝试缓存）：{exception.Message}");
            return null;
        }
    }

    private static string? TryReadCache()
    {
        try
        {
            if (!File.Exists(CachePath))
            {
                return null;
            }

            var text = File.ReadAllText(CachePath);
            HextechPlugin.Log.LogInfo("[平衡] 使用上次缓存的配置。");
            return text;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void TryWriteCache(string text)
    {
        try
        {
            File.WriteAllText(CachePath, text);
        }
        catch (Exception)
        {
            // 缓存写不进去无所谓，下次再拉。
        }
    }

    private static string CachePath => Path.Combine(Paths.CachePath, "hextech_balance.json");
}
