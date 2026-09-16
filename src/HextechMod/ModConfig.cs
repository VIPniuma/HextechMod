using System;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;

namespace PeakModder.HextechMod;

/// <summary>
/// 插件配置项集中定义处。
/// <para>
/// 分组名和条目名都写成中文。mod 管理器（BepInEx ConfigurationManager）的配置页
/// 显示的是「分组 → 条目 → 当前值」，描述要鼠标悬停才看得到 —— 所以光把描述写中文，
/// 玩家打开配置页看到的仍然是一片英文。
/// </para>
/// </summary>
internal static class ModConfig
{
    public static ConfigEntry<bool> Enabled = null!;
    public static ConfigEntry<KeyCode> SkillKey = null!;
    public static ConfigEntry<KeyCode> CycleSkillKey = null!;
    public static ConfigEntry<KeyCode> ShopKey = null!;
    public static ConfigEntry<bool> ShopEnabled = null!;
    public static ConfigEntry<float> TokensPerMinute = null!;
    public static ConfigEntry<float> ShopPriceMultiplier = null!;
    public static ConfigEntry<float> ShopFunctionPremium = null!;
    public static ConfigEntry<float> LuggagePositiveChance = null!;
    public static ConfigEntry<float> LuggageNegativeChance = null!;
    public static ConfigEntry<float> LuggageTokenChance = null!;
    public static ConfigEntry<int> LuggageTokenMax = null!;
    public static ConfigEntry<bool> CampfireAfkGuard = null!;
    public static ConfigEntry<bool> CarryGuard = null!;
    public static ConfigEntry<float> CampfireAfkRadius = null!;
    public static ConfigEntry<int> LegendaryWeight = null!;
    public static ConfigEntry<KeyCode> ResurrectKey = null!;
    public static ConfigEntry<KeyCode> HudToggleKey = null!;
    public static ConfigEntry<KeyCode> BanPanelKey = null!;
    public static ConfigEntry<KeyCode> ChoiceKey = null!;
    public static ConfigEntry<KeyCode> ConfigPanelKey = null!;
    public static ConfigEntry<KeyCode> ReportKey = null!;
    public static ConfigEntry<bool> SharedTokens = null!;
    public static ConfigEntry<bool> CheckUpdateOnStart = null!;
    public static ConfigEntry<string> UpdateFeedUrl = null!;
    public static ConfigEntry<string> IgnoredUpdateVersion = null!;

    /// <summary>
    /// 「这份 .cfg 是按哪个模组版本写的」。更新后第一次启动靠它判断要不要把配置刷成本版默认值。
    /// <para>
    /// 故意不是 public：F9 配置面板只反射 public 字段，这样它不会出现在玩家能点的列表里
    /// （BepInEx 的 ConfigurationManager 里还是看得到，说明文字写明了别动）。
    /// </para>
    /// </summary>
    internal static ConfigEntry<string> ConfigVersion = null!;

    /// <summary>
    /// 这份配置的 <see cref="ConfigFile"/>，给价格预设那边写盘用（见 <see cref="Save"/>）。
    /// </summary>
    internal static ConfigFile File = null!;

    /// <summary>
    /// 发一枚商店代币的间隔（秒），由「每分钟几枚」换算而来；
    /// 返回值 &lt;= 0 表示这局不发代币。
    /// </summary>
    public static float TokenIntervalSeconds =>
        TokensPerMinute.Value <= 0.01f ? 0f : 60f / TokensPerMinute.Value;

    public static void Bind(ConfigFile config)
    {
        File = config;

        Enabled = config.Bind(
            "常规",
            "启用模组",
            true,
            "是否启用海克斯 mod。");

        SkillKey = config.Bind(
            "技能",
            "释放技能",
            KeyCode.G,
            "释放当前海克斯技能的按键。");

        CycleSkillKey = config.Bind(
            "技能",
            "切换技能",
            KeyCode.H,
            "在已解锁的海克斯技能之间切换的按键。");

        ResurrectKey = config.Bind(
            "技能",
            "复活队友",
            KeyCode.N,
            "「死而复生」用来复活队友的按键。没抽到这条词条时按它不会有任何效果。");

        HudToggleKey = config.Bind(
            "界面",
            "打开海克斯页面",
            KeyCode.K,
            "按住这个键查看「海克斯页面」，松开就关。页面里用方块卡片陈列本局持有的技能（左侧）与海克斯（右侧最多 4 个），"
            + "每张显示名字、介绍与效果（技能卡另带冷却 / 耗能）。");

        BanPanelKey = config.Bind(
            "界面",
            "禁用海克斯",
            KeyCode.M,
            "在机场打开「禁用海克斯」面板的按键（默认 M）。每位玩家可以禁掉一个词条，"
            + "被任何人禁掉的词条本局全房间都不会再刷出来；一回机场自动解除，可以重新选。");

        ConfigPanelKey = config.Bind(
            "界面",
            "打开海克斯设置",
            KeyCode.F9,
            "随时打开模组配置面板的快捷键（默认 F9，里面能直接改这份配置里的每一项）。"
            + "ESC → 设置（主菜单 → 设置也一样）里最上面那行「海克斯模组设置」是同一个面板的另一个入口。");

        ReportKey = config.Bind(
            "界面",
            "上传日志",
            KeyCode.F10,
            "遇到 bug 时按这个键（默认 F10）：把「本机状态快照 + 最近 10 分钟的 BepInEx 日志与游戏日志」"
            + "打个包上传到作者的服务器，成功后屏幕上会显示一个 6 位编号（并自动复制到剪贴板）——"
            + "把这个编号和问题描述一起发给作者，作者就能照着编号取到你的日志。"
            + "上传全程屏幕上方会有提示，最多等二十几秒；万一两条通道都不通，报告会存成 BepInEx 目录下的 "
            + "HextechMod-report.txt，直接把那个文件发给作者效果一样。"
            + "只有你自己按下这个键才会传，不会自动上传；包里含你的游戏昵称、房间人数、本局词条与这份配置，"
            + "觉得不方便就别按。");

        ChoiceKey = config.Bind(
            "界面",
            "重新打开三选一",
            KeyCode.Y,
            "把还没选的海克斯三选一面板重新弹出来的按键。"
            + "三选一面板按 ESC 只是「先收起来」，那次机会不会丢 —— 一直留着，按这个键随时能继续选。"
            + "跳过几次就会攒几次，下一次点燃篝火时会连同新的一次一起弹出来。");

        ShopKey = config.Bind(
            "商店",
            "打开商店",
            KeyCode.L,
            "打开海克斯商店的按键。");

        ShopEnabled = config.Bind(
            "游戏玩法",
            "启用商店",
            true,
            "关掉后商店打不开，代币也不再积累（自动连带关闭代币功能）。觉得商店太超标的玩家可以整体关掉。"
            + "随时能切，不要求只在机场设置。");

        SharedTokens = config.Bind(
            "游戏玩法",
            "共享代币",
            false,
            "开启后代币变成全房间共享：只在房主侧按「房间人数」的速率累积，任何人花钱都从同一个池子扣。"
            + "获取速率 = 每分钟 2 枚，每多 2 名玩家再 +1 枚/分。"
            + "只能在机场开关 —— 进局之后再想改就改不了了（避免半路把别人的池子改没了）。");

        TokensPerMinute = config.Bind(
            "游戏玩法",
            "每分钟代币",
            1.5f,
            "局内每分钟发多少枚商店代币（连着涨，HUD 显示到一位小数）。默认 1.5 = 40 秒攒出 1 枚。0 = 不发。");

        ShopPriceMultiplier = config.Bind(
            "游戏玩法",
            "物价倍率",
            1f,
            "商店物价倍率。商品基础价 × 这个值 = 实际售价（向上取整，最低 1）。调低会让游戏简单很多。");

        ShopFunctionPremium = config.Bind(
            "游戏玩法",
            "功能溢价",
            1f,
            new ConfigDescription(
                "功能性定价的强度：只改「作用」和「按稀有度算出来的价」明显对不上的那批 —— "
                + "位移类（绳索 / 踏板菇 / 飞行 / 可放置）补到 30 枚，护符与救援钩 30 枚，"
                + "背包与续航 25 枚，光源与破坏 20 枚；反方向的顶价：滑翔翼 22 枚、一束气球 15 枚、"
                + "纯补给（蘑菇 / 浆果 / 袋装食品）×0.8 且不超过 12 枚。"
                + "底线只补差价、顶价只压高价，两边都不动本来就在区间里的道具。"
                + "1 = 默认（补到 / 压到上面这些价），0 = 完全不管、只按稀有度定价，2 = 把这份差距再翻一倍。",
                new AcceptableValueRange<float>(0f, 2f)));

        LuggagePositiveChance = config.Bind(
            "行李箱",
            "正面海克斯概率",
            0.025f,
            "打开行李箱时，每个抽奖位抽出「正面」海克斯的概率。0.025 = 2.5%。"
            + "只给永久收益，抽到什么就是什么、不给你选。");

        LuggageNegativeChance = config.Bind(
            "行李箱",
            "负面海克斯概率",
            0.015f,
            "打开行李箱时，每个抽奖位抽出「负面（代价）」海克斯的概率。0.015 = 1.5%，0 = 完全抽不到。"
            + "正面和负面是两次独立判定，两个加起来才是「开箱出海克斯」的总概率（默认 2.5% + 1.5% = 4%）。");

        LuggageTokenChance = config.Bind(
            "行李箱",
            "出代币概率",
            0.1f,
            "打开行李箱时，每个抽奖位抽出商店代币（而不是物资）的概率。0.1 = 10%。"
            + "先判正面海克斯、再判负面海克斯、再判代币，剩下的才给物资。");

        LuggageTokenMax = config.Bind(
            "行李箱",
            "代币上限",
            20,
            "行李箱抽奖一次最多能抽到多少枚代币（在 1 ~ 这个值之间随机）。"
            + "硬上限 20，往大了调不会生效。");

        CampfireAfkGuard = config.Bind(
            "防挂机",
            "篝火内不积累代币",
            true,
            "在点着的篝火附近（见「篝火范围」）站着时，不再积累商店代币 —— 这就是新版的「挂机惩罚」。"
            + "以前的「站太久叫一只蘑菇僵尸来咬你」已经整个移除（那个判定在房主侧，反而容易误伤认真在篝火边做饭 / 恢复的玩家）。"
            + "判定由本地做（每个玩家自己不算自己的代币），所以联机时每个人各自生效。");

        CampfireAfkRadius = config.Bind(
            "防挂机",
            "篝火范围（米）",
            25f,
            "离点着的篝火多近算「待在篝火边」。默认 25 米。"
            + "没点着的篝火不算 —— 不点火的篝火旁没有安全区，正常也不会有人在那儿停下。");

        CarryGuard = config.Bind(
            "修复",
            "扛人兜底",
            true,
            "原版只能扛「完全昏迷」的队友，但他一旦醒了，原版既不会自动把人放下，也很难手动放下 ——"
            + "扛起时被扛者的碰撞体被关掉了，交互射线扫不到他，只有切到背上那个槽才选得中，"
            + "于是人一直挂在肩上、按什么都放不下来。"
            + "打开这个开关：本地玩家肩上的人一旦不再完全昏迷（或已经死亡），就自动用原版自己的放下逻辑把人放下。"
            + "放下这条 RPC 只能由扛人的那一方发，所以联机时「扛人的那位玩家」也得装这个模组才会生效。");

        LegendaryWeight = config.Bind(
            "海克斯",
            "传说词条权重",
            1,
            "「传说」品质词条的抽取权重。其它档位固定为 青铜 60 / 白银 22 / 黄金 10，"
            + "所以 1 = 极稀有（每格约 0.1%），调大更容易抽到，0 = 完全抽不到。"
            + "商店的抽奖券开到黄金为止，改这个只会影响自然三选一与开行李箱。");

        CheckUpdateOnStart = config.Bind(
            "更新",
            "启动时检查更新",
            true,
            "每次打开游戏、刚进主菜单（还没选在线 / 单人）时检查一次新版本。"
            + "有新版本会弹提示，指路到安装器（HextechModInstaller.exe）更新，也可以选「忽略此版本」。"
            + "检查失败（断网、服务器维护）会静默跳过，不影响正常玩。");

        // 默认留空，不把内置地址写进玩家的 .cfg —— 那等于把服务器地址抄一份到磁盘上，
        // 玩家求助贴配置时容易跟着出去。留空时用构建时注入的内置地址（见 UpdateFeed）。
        UpdateFeedUrl = config.Bind(
            "更新",
            "更新源地址",
            string.Empty,
            "版本清单（version.json）的地址。留空 = 用内置地址。");

        IgnoredUpdateVersion = config.Bind(
            "更新",
            "已忽略版本",
            string.Empty,
            "被忽略的版本号。和远端版本一致时不再提示；留空 = 没有忽略任何版本。");

        // 「强制三选一」那个调试入口已经整个拿掉了：它能凭空弹一次三选一，
        // 发出去的版本不该留这种能绕过正常流程的开关。老 .cfg 里那两条留着也没用，顺手清掉。
        RemoveLegacy(config, "调试", "强制三选一按键");
        RemoveLegacy(config, "Debug", "DebugChoiceKey");

        // 记账用：上次运行的是哪个版本。这一项必须在「覆盖」之前绑好。
        ConfigVersion = config.Bind(
            "内部",
            "配置版本",
            string.Empty,
            "上次运行模组的版本号，由模组自己维护，不要手动改。"
            + "装上新版本之后第一次启动会用新版本的默认值覆盖这份配置里的数值项"
            + "（按键、开关、更新源这些保留）；同一个版本内你自己调过的值不会被覆盖。");

        // 老键迁移要在覆盖之前做：按键 / 更新源那几项是「保留项」，
        // 覆盖逻辑会跳过它们，所以迁移结果留得下来。
        AdoptLegacyKeys(config);

        // 价格预设的自备份也必须排在覆盖之前：覆盖会把物价倍率 / 功能溢价 / 每分钟代币刷成本版默认值，
        // 刷完再备份就等于备份了个寂寞。只有「装上带预设功能的版本后第一次启动」才会真的备份，
        // 之后靠预设文件里那行记忆跳过（见 PricePresets.BackupOnUpdate）。
        PricePresets.BackupOnUpdate();

        ApplyVersionDefaults(config);
    }

    /// <summary>
    /// 把这份配置写盘。换价格预设时要显式调一次 —— 那几个价格数值是绕过面板直接写进 ConfigEntry 的，
    /// 而玩家可能下一次改动才触发 BepInEx 自己落盘，中间被游戏崩掉就白改了。
    /// </summary>
    internal static void Save()
    {
        try
        {
            File?.Save();
        }
        catch (Exception exception)
        {
            HextechPlugin.Log.LogWarning($"保存配置失败（这次改动重启后会丢）：{exception.Message}");
        }
    }

    /// <summary>
    /// 更新时保留、不接受新默认值覆盖的配置项：按键、开关、更新源这类跟「玩家的手 /
    /// 玩家的机器 / 更新服务器」绑定的东西。
    /// <para>
    /// 数值类（代币、物价、开箱概率、抽取权重）不在里面 —— 那些正是「以新版本为主」要覆盖的目标。
    /// </para>
    /// </summary>
    private static bool IsPreservedAcrossUpdates(ConfigEntryBase entry)
    {
        return entry == Enabled
            || entry == SkillKey
            || entry == CycleSkillKey
            || entry == ResurrectKey
            || entry == ShopKey
            || entry == HudToggleKey
            || entry == BanPanelKey
            || entry == ChoiceKey
            || entry == ConfigPanelKey
            || entry == ReportKey
            || entry == SharedTokens
            || entry == ShopEnabled
            || entry == CampfireAfkGuard
            || entry == CarryGuard
            || entry == CheckUpdateOnStart
            || entry == UpdateFeedUrl
            || entry == IgnoredUpdateVersion;
    }

    /// <summary>
    /// 「以新版本为主」：模组更新之后第一次启动，把配置整体刷成本版默认值。
    /// <para>
    /// 背景：BepInEx 只在「键不存在」时才写默认值，所以老玩家的 .cfg 里一直留着旧数值 ——
    /// 光在代码里改默认值，改完只有新玩家吃得到，老玩家那边毫无变化（这次改
    /// 「负面词条权重 0.35 → 0.4」就是撞上这件事才加的机制）。
    /// 这里用 <see cref="ConfigVersion"/> 记账：账对不上（第一次装、更新、降级回退）
    /// 就覆盖一遍，再把当前版本记回去；账对得上（同一个版本内）就完全不动，
    /// 玩家自己在 F9 面板里调的值照旧保留。
    /// </para>
    /// <para>
    /// 覆盖规则是「默认全部覆盖」，只有 <see cref="IsPreservedAcrossUpdates"/> 点名的几项例外，
    /// 所以以后新加的数值项不用再单独登记，自动跟随新版本的默认值。
    /// </para>
    /// </summary>
    private static void ApplyVersionDefaults(ConfigFile config)
    {
        var current = HextechPlugin.Version;

        if (string.Equals(ConfigVersion.Value, current, StringComparison.Ordinal))
        {
            return;
        }

        var previous = ConfigVersion.Value;
        var overwritten = 0;

        foreach (var field in typeof(ModConfig).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not ConfigEntryBase entry || IsPreservedAcrossUpdates(entry))
            {
                continue;
            }

            // 本来就是默认值的不用动，省下没必要的写盘与日志噪音。
            if (Equals(entry.BoxedValue, entry.DefaultValue))
            {
                continue;
            }

            entry.BoxedValue = entry.DefaultValue;
            overwritten++;
        }

        ConfigVersion.Value = current;
        config.Save();

        var from = string.IsNullOrEmpty(previous) ? "首次运行" : $"v{previous}";
        HextechPlugin.Log.LogInfo(
            $"{HextechPlugin.Name}：{from} → v{current}，配置已按本版默认值刷新"
            + $"（{overwritten} 项被改回默认值；按键 / 开关 / 更新源保持不变）。");
    }

    /// <summary>
    /// 把英文键名时代留下的老配置接到中文键上。
    /// <para>
    /// 分组名和条目名同时也是 .cfg 文件里的键，直接改名等于把所有老玩家的配置丢掉 ——
    /// 按键绑定、更新源、被忽略的版本这些会被悄悄重置回默认值，而玩家通常过很久才发现。
    /// 这里逐条对一遍：老键还在就把值接过来，然后把老键从文件里删掉，不留下满文件废键。
    /// </para>
    /// </summary>
    private static void AdoptLegacyKeys(ConfigFile config)
    {
        Adopt(config, Enabled, "General", "Enabled");
        Adopt(config, SkillKey, "Skill", "SkillKey");
        Adopt(config, CycleSkillKey, "Skill", "CycleSkillKey");
        Adopt(config, ResurrectKey, "Skill", "ResurrectKey");
        Adopt(config, HudToggleKey, "UI", "HudToggleKey");
        Adopt(config, ShopKey, "Shop", "ShopKey");
        Adopt(config, TokensPerMinute, "Shop", "TokensPerMinute");
        Adopt(config, ShopPriceMultiplier, "Shop", "ShopPriceMultiplier");
        Adopt(config, LuggageTokenChance, "Luggage", "LuggageTokenChance");
        Adopt(config, LuggageTokenMax, "Luggage", "LuggageTokenMax");
        Adopt(config, LegendaryWeight, "Hextech", "LegendaryWeight");
        Adopt(config, CheckUpdateOnStart, "Update", "CheckUpdateOnStart");
        Adopt(config, UpdateFeedUrl, "Update", "UpdateFeedUrl");
        Adopt(config, IgnoredUpdateVersion, "Update", "IgnoredUpdateVersion");

        // 旧版本把内置更新源当成默认值绑定，BepInEx 会把它抄进 .cfg，有些玩家还手动填过一遍。
        // 值等于内置地址就清掉：地址本来就由程序集里注入的常量提供，清掉不损失任何功能，
        // 而玩家贴配置 / 日志求助时就不会顺带把服务器地址发出去。
        if (string.Equals(UpdateFeedUrl.Value.Trim(), UpdateFeed.DefaultManifestUrl, StringComparison.OrdinalIgnoreCase))
        {
            UpdateFeedUrl.Value = string.Empty;
        }

        // 禁用面板的默认键从 B 换成了 M：老配置里存着旧默认值 B，不迁的话玩家会一直按 B、
        // 然后以为「禁用面板坏了」。只迁「正好等于旧默认值」的那种，玩家自己特意设成 B 的情况极少，
        // 真被改到了，在设置面板里点一下就能改回来。
        if (BanPanelKey.Value == KeyCode.B)
        {
            BanPanelKey.Value = KeyCode.M;
        }

        // 配置面板的默认键从 F1 换成了 F9：F1 在原版 / 部分机器上会被别的程序先吃掉。
        // 同样只迁「正好等于旧默认值」的那种，玩家自己设过的键不动。
        if (ConfigPanelKey.Value == KeyCode.F1)
        {
            ConfigPanelKey.Value = KeyCode.F9;
        }

        // 开箱概率原来只有「出海克斯概率」一项（4%），现在拆成正面 2.5% / 负面 1.5%（合计不变）。
        // BepInEx 首启就会把老默认值写进 .cfg，所以不主动迁的话老玩家那边会一直留着一项废键。
        // 老键只删不搬：新默认值合起来正好还是 4%，搬过去反而会跟「以新版本为主」的刷默认值打架。
        RemoveLegacy(config, "行李箱", "出海克斯概率");
        RemoveLegacy(config, "行李箱", "负面词条权重");
    }

    /// <summary>把已经被删掉的配置条目从 .cfg 里清掉（留着只是几行废键）。</summary>
    private static void RemoveLegacy(ConfigFile config, string section, string key)
    {
        try
        {
            config.Remove(new ConfigDefinition(section, key));
        }
        catch (Exception)
        {
            // 删不掉最多是 .cfg 里留一行废键，不影响使用。
        }
    }

    private static void Adopt<T>(ConfigFile config, ConfigEntry<T> entry, string section, string key)
    {
        var legacy = new ConfigDefinition(section, key);

        if (!config.TryGetEntry<T>(legacy, out var old) || old == null)
        {
            return;
        }

        entry.Value = old.Value;

        try
        {
            config.Remove(legacy);
        }
        catch (Exception)
        {
            // 删不掉最多是 .cfg 里留几行废键，不影响使用。
        }
    }
}
