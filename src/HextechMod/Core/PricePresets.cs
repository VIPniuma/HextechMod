using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using UnityEngine;

namespace PeakModder.HextechMod;

/// <summary>
/// 一份「价格预设」：某一刻整套价格设置的快照 —— 逐件调价（<see cref="ShopPricing"/>）
/// 加上面板里的三个价格数值（物价倍率 / 功能溢价 / 每分钟代币）。
/// <para>
/// 为什么把数值项也算进来：这三项和逐件价一起决定「这件东西到底卖多少」（实际售价 = 基础价 × 倍率，
/// 功能性定价还会再补一截）。只存逐件价的话，玩家切个预设还得回去手动改倍率，等于只切了一半。
/// </para>
/// </summary>
internal sealed class PricePreset
{
    public string Name = string.Empty;

    /// <summary>逐件调价：商品 id → 价格。空集合 = 全部按默认基础价。</summary>
    public readonly Dictionary<string, int> Prices = new(StringComparer.Ordinal);

    public float Multiplier = 1f;
    public float Premium = 1f;
    public float Tokens = 1.5f;

    /// <summary>列表里跟在名字后面那行小字：改了多少件、倍率多少。</summary>
    public string Summary =>
        $"{Prices.Count} 件调价 · 倍率 {Multiplier.ToString("0.##", CultureInfo.InvariantCulture)}"
        + $" · 溢价 {Premium.ToString("0.##", CultureInfo.InvariantCulture)}"
        + $" · 代币 {Tokens.ToString("0.##", CultureInfo.InvariantCulture)}/分";
}

/// <summary>
/// 价格预设的存盘与切换。玩家调好一版价格存成「自定义价格表1」，之后想换流派就切另一份，
/// 或者一键切回默认配置。
/// <para>
/// 表存在 <c>BepInEx/config/HextechMod.prices.presets.txt</c>，和逐件调价表
/// （<c>HextechMod.prices.txt</c>）放同一个目录、用同一套「制表符分隔」的土办法 ——
/// 不开新格式、也不塞进 .cfg（一份预设几十上百行，塞 .cfg 会让配置面板顶出一大坨）。
/// </para>
/// <para>
/// 另外它还负责一件事：模组更新后第一次启动时，把玩家<b>更新前</b>的价格设置抢先备份成一份预设。
/// 因为 <see cref="ModConfig"/> 的「配置版本」机制会把数值项刷回本版默认值（那是刻意设计的，
/// 否则改了默认值老玩家吃不到），可价格这种东西是玩家自己一件件调出来的 —— 刷掉就没了。
/// 先备份再刷，玩家随时能在选择框里点回自己那份（见 <see cref="BackupOnUpdate"/>）。
/// </para>
/// </summary>
internal static class PricePresets
{
    private const string FileName = "HextechMod.prices.presets.txt";

    /// <summary>自动备份用的名字前缀，后面跟序号：自定义价格表1、自定义价格表2…</summary>
    private const string AutoNamePrefix = "自定义价格表";

    /// <summary>记忆段的段名：以 <c>#</c> 开头，和真正的预设区分开。</summary>
    private const string MetaSection = "#备份";

    private const string KeyBackupVersion = "版本";
    private const string KeyMultiplier = "倍率";
    private const string KeyPremium = "溢价";
    private const string KeyTokens = "代币";
    private const string KeyPrice = "价";

    /// <summary>名字长度上限（字符数）：列表那一行放得下，也不至于把文件写花。</summary>
    private const int MaxNameLength = 16;

    // 读回来的数值兜一下上下界：手改过的文件里可能是 99999，直接灌进配置会把商店整条刷爆。
    private const float MaxMultiplier = 20f;
    private const float MaxPremium = 2f;
    private const float MaxTokens = 120f;

    private static readonly List<PricePreset> Presets = new();

    /// <summary>「已经备份过更新前的价格」记在这（值 = 备份时的模组版本号）；空 = 还没备份过。</summary>
    private static string _backupVersion = string.Empty;

    private static bool _loaded;

    /// <summary>所有预设，按存进来的先后顺序（新加的排在最后）。</summary>
    public static IReadOnlyList<PricePreset> All
    {
        get
        {
            EnsureLoaded();
            return Presets;
        }
    }

    public static int Count
    {
        get
        {
            EnsureLoaded();
            return Presets.Count;
        }
    }

    private static string FilePath
    {
        get
        {
            var directory = Paths.ConfigPath;

            if (string.IsNullOrEmpty(directory))
            {
                directory = Path.Combine(AppContext.BaseDirectory, "config");
            }

            return Path.Combine(directory, FileName);
        }
    }

    /// <summary>找一个预设；找不着返回 null。</summary>
    public static PricePreset? Find(string name)
    {
        EnsureLoaded();

        var index = Presets.FindIndex(preset => string.Equals(preset.Name, name, StringComparison.Ordinal));
        return index < 0 ? null : Presets[index];
    }

    public static bool Exists(string name)
    {
        return Find(name) != null;
    }

    /// <summary>
    /// 取一个没被占用的自动名字：自定义价格表1、自定义价格表2…
    /// <para>
    /// 故意从 1 开始重新数，而不是在「自定义价格表1」后面粘个 2（那会变成「自定义价格表12」）——
    /// 玩家自己起名也常撞上这个前缀，拼字符串看着像 bug。
    /// </para>
    /// </summary>
    public static string NextAutoName()
    {
        EnsureLoaded();

        for (var index = 1; index < 1000; index++)
        {
            var name = AutoNamePrefix + index.ToString(CultureInfo.InvariantCulture);

            if (!Exists(name))
            {
                return name;
            }
        }

        return AutoNamePrefix + DateTime.Now.Ticks.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 把当前价格设置存成一份预设（同名就整份覆盖）。返回真表示覆盖了已有预设。
    /// </summary>
    public static bool Store(string name)
    {
        EnsureLoaded();

        name = SanitizeName(name);

        if (name.Length == 0)
        {
            name = NextAutoName();
        }

        var preset = Capture(name);
        var index = Presets.FindIndex(item => string.Equals(item.Name, name, StringComparison.Ordinal));
        var replaced = index >= 0;

        if (replaced)
        {
            Presets[index] = preset;
        }
        else
        {
            Presets.Add(preset);
        }

        Save();
        HextechPlugin.Log.LogInfo($"价格预设「{name}」已保存（{(replaced ? "覆盖" : "新建")}，{preset.Prices.Count} 件调价）。");
        return replaced;
    }

    /// <summary>删掉一份预设。返回真表示确实删掉了。</summary>
    public static bool Remove(string name)
    {
        EnsureLoaded();

        var index = Presets.FindIndex(preset => string.Equals(preset.Name, name, StringComparison.Ordinal));

        if (index < 0)
        {
            return false;
        }

        Presets.RemoveAt(index);
        Save();
        return true;
    }

    /// <summary>把当前设置拍成一份快照（不落盘）。</summary>
    public static PricePreset Capture(string name)
    {
        var preset = new PricePreset { Name = SanitizeName(name) };

        var (ids, prices) = ShopPricing.Snapshot();

        for (var index = 0; index < ids.Length && index < prices.Length; index++)
        {
            if (!string.IsNullOrEmpty(ids[index]))
            {
                preset.Prices[ids[index]] = prices[index];
            }
        }

        preset.Multiplier = ModConfig.ShopPriceMultiplier.Value;
        preset.Premium = ModConfig.ShopFunctionPremium.Value;
        preset.Tokens = ModConfig.TokensPerMinute.Value;
        return preset;
    }

    /// <summary>把一份预设写回去：逐件调价整表替换 + 三个数值项，全部落盘。</summary>
    public static void Apply(PricePreset preset, HextechState? state)
    {
        if (preset == null)
        {
            return;
        }

        ShopPricing.Replace(preset.Prices, state);

        ModConfig.ShopPriceMultiplier.Value = Mathf.Clamp(preset.Multiplier, 0f, MaxMultiplier);
        ModConfig.ShopFunctionPremium.Value = Mathf.Clamp(preset.Premium, 0f, MaxPremium);
        ModConfig.TokensPerMinute.Value = Mathf.Clamp(preset.Tokens, 0f, MaxTokens);
        ModConfig.Save();

        HextechPlugin.Log.LogInfo($"已应用价格预设「{preset.Name}」（{preset.Prices.Count} 件调价）。");
    }

    /// <summary>切回默认配置：调价全部清掉，三个数值项回模组默认值。</summary>
    public static void ApplyDefault(HextechState? state)
    {
        ShopPricing.Replace(null, state);

        ModConfig.ShopPriceMultiplier.Value = (float)ModConfig.ShopPriceMultiplier.DefaultValue;
        ModConfig.ShopFunctionPremium.Value = (float)ModConfig.ShopFunctionPremium.DefaultValue;
        ModConfig.TokensPerMinute.Value = (float)ModConfig.TokensPerMinute.DefaultValue;
        ModConfig.Save();
    }

    /// <summary>
    /// 模组更新后第一次启动时，把玩家更新前的价格设置备份成一份预设。
    /// <para>
    /// 必须在 <see cref="ModConfig"/> 那个「配置版本对不上就刷默认值」的覆盖动作<b>之前</b>调用，
    /// 否则备份到的已经是刷完的默认值、等于没备份。只在第一次做（做完写一行记忆进文件），
    /// 之后每次更新都不会再打扰玩家 —— 想要新的一份，在弹层里点「保存当前为新预设」自己存就行。
    /// </para>
    /// <para>
    /// 全新玩家（没调过价、三个数值也全是默认）没有东西可救，只写记忆不建预设。
    /// 整个过程包在 try 里：备份失败最坏是玩家少一份预设，绝不能让插件启动不了。
    /// </para>
    /// </summary>
    public static void BackupOnUpdate()
    {
        try
        {
            EnsureLoaded();

            if (_backupVersion.Length > 0)
            {
                return;
            }

            if (!HasAnythingToBackup())
            {
                _backupVersion = HextechPlugin.Version;
                Save();
                return;
            }

            var name = NextAutoName();
            var preset = Capture(name);
            Presets.Add(preset);
            _backupVersion = HextechPlugin.Version;
            Save();

            HextechPlugin.Log.LogInfo(
                $"更新前的价格设置已备份成预设「{name}」（{preset.Prices.Count} 件调价）—— "
                + "想换回来在商店调价模式（按 T）或海克斯设置面板的「商店调价」区里选。");
        }
        catch (Exception exception)
        {
            HextechPlugin.Log.LogWarning($"备份价格设置失败（不影响游戏，只是少了这份预设）：{exception.Message}");
        }
    }

    /// <summary>玩家有没有「值得备份」的东西：改过逐件价，或者动过三个价格数值。</summary>
    private static bool HasAnythingToBackup()
    {
        if (ShopPricing.Count > 0)
        {
            return true;
        }

        return Changed(ModConfig.ShopPriceMultiplier, ModConfig.ShopPriceMultiplier.Value)
               || Changed(ModConfig.ShopFunctionPremium, ModConfig.ShopFunctionPremium.Value)
               || Changed(ModConfig.TokensPerMinute, ModConfig.TokensPerMinute.Value);
    }

    private static bool Changed<T>(BepInEx.Configuration.ConfigEntry<T> entry, T value) where T : IConvertible
    {
        return Math.Abs(Convert.ToDouble(entry.DefaultValue, CultureInfo.InvariantCulture)
                        - Convert.ToDouble(value, CultureInfo.InvariantCulture)) > 0.0001d;
    }

    /// <summary>
    /// 洗一下玩家起的名字：去掉首尾空白与会破坏文件格式的字符（方括号是段头、制表符是分隔符、
    /// 换行会把一段劈成两段），再截到长度上限。洗完可能是空串，调用方自己兜底。
    /// </summary>
    internal static string SanitizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(MaxNameLength);

        foreach (var character in name)
        {
            if (character < ' ' || character == '[' || character == ']' || character == '\t')
            {
                continue;
            }

            builder.Append(character);

            if (builder.Length >= MaxNameLength)
            {
                break;
            }
        }

        return builder.ToString().Trim();
    }

    private static void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        Load();
    }

    private static void Load()
    {
        try
        {
            var path = FilePath;

            if (!File.Exists(path))
            {
                return;
            }

            PricePreset? current = null;
            var meta = false;

            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();

                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                if (line[0] == '[' && line[^1] == ']')
                {
                    var name = line.Substring(1, line.Length - 2).Trim();

                    // 「#备份」那种段是文件自己的记忆，不是预设。
                    meta = name.StartsWith("#", StringComparison.Ordinal);
                    current = null;

                    if (!meta && name.Length > 0)
                    {
                        current = new PricePreset { Name = name };
                        Presets.Add(current);
                    }

                    continue;
                }

                var parts = line.Split('\t');

                if (parts.Length < 2)
                {
                    continue;
                }

                var key = parts[0].Trim();
                var value = parts[1].Trim();

                if (meta)
                {
                    if (key == KeyBackupVersion)
                    {
                        _backupVersion = value;
                    }

                    continue;
                }

                if (current == null)
                {
                    continue;
                }

                switch (key)
                {
                    case KeyMultiplier:
                        if (TryParse(value, out var multiplier))
                        {
                            current.Multiplier = Mathf.Clamp(multiplier, 0f, MaxMultiplier);
                        }

                        break;

                    case KeyPremium:
                        if (TryParse(value, out var premium))
                        {
                            current.Premium = Mathf.Clamp(premium, 0f, MaxPremium);
                        }

                        break;

                    case KeyTokens:
                        if (TryParse(value, out var tokens))
                        {
                            current.Tokens = Mathf.Clamp(tokens, 0f, MaxTokens);
                        }

                        break;

                    case KeyPrice:
                        // 价 这一行有三列：价 / 商品 id / 价格。
                        if (parts.Length >= 3
                            && value.Length > 0
                            && int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var price))
                        {
                            current.Prices[value] = price;
                        }

                        break;
                }
            }

            if (Presets.Count > 0)
            {
                HextechPlugin.Log.LogInfo($"已读取 {Presets.Count} 份价格预设。");
            }
        }
        catch (Exception exception)
        {
            // 读不出来最多是预设列表空着，不该影响进游戏。
            HextechPlugin.Log.LogWarning($"读取价格预设表失败（这次当作没有预设）：{exception.Message}");
        }
    }

    private static bool TryParse(string text, out float value)
    {
        return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static void Save()
    {
        try
        {
            var path = FilePath;
            var directory = Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var builder = new StringBuilder();
            builder.Append("# 海克斯 mod 的价格预设（模组自己维护，手改格式会让预设读不出来）\n");
            builder.Append("# 每一段是一个预设：[名字]；段内每行「键 <制表符> 值」，键有 倍率 / 溢价 / 代币 / 价\n");
            builder.Append("# 「价」这一行是三列：价 <制表符> 商品 id <制表符> 价格\n");

            if (_backupVersion.Length > 0)
            {
                builder.Append('\n').Append('[').Append(MetaSection).Append("]\n");
                builder.Append(KeyBackupVersion).Append('\t').Append(_backupVersion).Append('\n');
            }

            foreach (var preset in Presets)
            {
                builder.Append('\n').Append('[').Append(SanitizeName(preset.Name)).Append("]\n");
                AppendNumber(builder, KeyMultiplier, preset.Multiplier);
                AppendNumber(builder, KeyPremium, preset.Premium);
                AppendNumber(builder, KeyTokens, preset.Tokens);

                // 排一下序只是让文件整齐，方便肉眼对比两份预设差在哪。
                var ids = new List<string>(preset.Prices.Keys);
                ids.Sort(StringComparer.Ordinal);

                foreach (var id in ids)
                {
                    builder.Append(KeyPrice).Append('\t').Append(id).Append('\t')
                        .Append(preset.Prices[id].ToString(CultureInfo.InvariantCulture)).Append('\n');
                }
            }

            File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
        }
        catch (Exception exception)
        {
            HextechPlugin.Log.LogWarning($"保存价格预设失败（这次的改动重启后会丢）：{exception.Message}");
        }
    }

    private static void AppendNumber(StringBuilder builder, string key, float value)
    {
        builder.Append(key).Append('\t')
            .Append(value.ToString("0.###", CultureInfo.InvariantCulture))
            .Append('\n');
    }
}
