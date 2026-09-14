using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace PeakModder.HextechMod;

/// <summary>
/// 物资定价：稀有度基准价，外加一条「功能底线价」。
/// <para>
/// 稀有度只说明「这东西刷出来的概率」，说不清「这东西能干什么」——
/// 一捆绳子能让整队翻过一面墙、一株踏板菇能凭空造出一块落脚点，
/// 而它们的稀有度可能还不如一件没用的衣服。所以反过来看道具身上挂了哪些游戏组件：
/// 挂 <c>ShelfShroom</c> 的就是那块能踩的菇，挂 <c>Peak.AmuletBase</c> 的就是护符。
/// </para>
/// <para>
/// 底线只补差价、不是整店涨价：只有「按稀有度算下来比底线还便宜」的道具会被抬到底线，
/// 本来就比底线贵的（那些荒诞稀有度的位移道具）一分不涨 —— 涨价只针对「作用很大、
/// 价格却低得离谱」的那一批，免得整家店一起变贵。
/// </para>
/// <para>
/// 反方向也留了口子：个别道具（滑翔翼、一束气球）和整类补给（食物）会被**按住顶价** ——
/// 稀有度算出来的价钱明显配不上它的实际作用时，宁可降价也不让玩家觉得「这件东西不值这个价」。
/// 顶价和底线吃同一个 <see cref="ModConfig.ShopFunctionPremium"/>：0 = 两边都关掉，只按稀有度定价。
/// </para>
/// <para>
/// 三条计算里没有随机数：价格是全房间统一的事（见 <see cref="ShopPricing"/>），
/// 同一个 prefab 名在每个客户端上必须算出同一个价，「个体微调」用的是名字哈希而不是随机数。
/// </para>
/// </summary>
internal static class ItemPricing
{
    /// <summary>
    /// 稀有度基准价，下标就是 <see cref="Rarity"/> 的值：普通 → 荒诞。
    /// <para>
    /// 和最初那一版（4 / 7 / 12 / 18 / 28 / 45 / 70）比：底下两档各抬 1 枚
    /// （普通物资以前 4 枚就能买一件，比一次抽奖还便宜，商店等于白送），
    /// 稀有及以上整体下调 —— 一局也就几十枚代币，70 枚一件的价签等于「买不到」。
    /// 除了「很有用却很便宜」的功能道具（见下面 <see cref="Rules"/>），整店只降不涨。
    /// </para>
    /// </summary>
    private static readonly int[] RarityBase = { 5, 8, 12, 16, 20, 24, 30 };

    /// <summary>没有特殊功能的食物按基准价的这个比例卖：最常见、也最不缺的补给。</summary>
    private const float FoodDiscount = 0.8f;

    /// <summary>
    /// 纯补给（蘑菇 / 浆果 / 袋装食品，且没有别的功能）的顶价。
    /// 稀有度只说明「这东西刷出来的概率」，说不清「它能顶多少饥饿」——
    /// 一颗传说档的果子按稀有度算下来比一捆绳子还贵，谁看了都觉得虚高，所以一律按住。
    /// </summary>
    private const int FoodCap = 12;

    /// <summary>个体微调的上下限（按 prefab 名算，稳定、各端一致）。</summary>
    private const float TweakMin = 0.88f;
    private const float TweakMax = 1.24f;

    /// <summary>价格硬边界，和逐件调价表（见 <see cref="ShopPricing"/>）保持一致。</summary>
    private const int MinPrice = 1;
    private const int MaxPrice = 999;

    /// <summary>一条功能规则：这件道具的底线价与顶价，以及它算哪一类。</summary>
    private sealed class Rule
    {
        private readonly Func<Item, bool> _matches;

        public Rule(string category, int floor, int cap, Func<Item, bool> matches)
        {
            Category = category;
            Floor = floor;
            Cap = cap;
            _matches = matches;
        }

        /// <summary>日志里显示的归类名。</summary>
        public string Category { get; }

        /// <summary>功能底线价：按稀有度算下来比它便宜的补到这个价，比它贵的原样不动。0 = 不管这头。</summary>
        public int Floor { get; }

        /// <summary>功能顶价：按稀有度算下来比它贵的压到这个价，比它便宜的原样不动。0 = 不管这头。</summary>
        public int Cap { get; }

        public bool Matches(Item item)
        {
            return _matches(item);
        }
    }

    /// <summary>
    /// 功能价格表。<b>从上往下第一条命中的生效</b>（不叠加，也不取最高）——
    /// 所以「点名的那几件」必须排在它们所属的大类前面，不然会被大类价盖掉。
    /// 同时挂两种功能的道具很少（绳子兼照明之类），叠加出来的价格既没法解释也没法调。
    /// </summary>
    private static readonly Rule[] Rules =
    {
        // ── 点名单价：作用配不上稀有度价的几件 ───────────────────────

        // 滑翔翼：能飞，但多数时候没有绳子 / 踏板菇好使，按稀有度算下来 30 往上，玩家反馈虚高。
        new Rule(
            "位移·滑翔翼",
            0,
            22,
            item => Has<Glider>(item)),

        // 一束气球：一次性载具，按住位移类的一半。
        new Rule(
            "位移·气球",
            0,
            15,
            item => Has<Balloon>(item)),

        // ── 大类 ─────────────────────────────────────────────────────

        // 位移：绳子 / 枪绳 / 藤枪 / 气球 / 魔豆 / 踏板菇 / 滑翔翼 / 喷气・火箭背包 /
        // 降落伞 / 攀爬钉 / 可放置类（链子、检查点旗）。这一段过不过得去往往就看有没有它。
        // 气球与滑翔翼已经被上面两条按住，这里保留它们只是为了把「这一类包含什么」写全。
        new Rule(
            "位移",
            30,
            0,
            item => Has<RopeSpool>(item)
                || Has<RopeTier>(item)
                || Has<RopeShooter>(item)
                || Has<VineShooter>(item)
                || Has<Balloon>(item)
                || Has<MagicBean>(item)
                || Has<ShelfShroom>(item)
                || Has<Glider>(item)
                || Has<JetpackItem>(item)
                || Has<Rocketpack>(item)
                || Has<Peak.ParachuteItem>(item)
                || Has<ClimbingSpikeComponent>(item)
                || Has<Constructable>(item)),

        // 救援：护符（大治疗 / 无限体力 / 二段跳 / 复制）与救援钩 ——
        // 一次就能把整队从团灭边缘拉回来。
        new Rule(
            "救援",
            30,
            0,
            item => Has<Peak.AmuletBase>(item) || Has<RescueHook>(item)),

        // 容量与续航：多背一件东西、多一截体力，整局都在吃收益。
        new Rule(
            "容量",
            25,
            0,
            item => item is Backpack
                || Has<MagicBugle>(item)
                || Has<BingBongShieldWhileHolding>(item)),

        // 光源与破坏：黑的地方能看见路、挡路的东西能拆开。
        new Rule(
            "光源",
            20,
            0,
            item => Has<Lantern>(item)
                || Has<Flare>(item)
                || Has<Candle>(item)
                || Has<ItemTorch>(item)
                || Has<Dynamite>(item)
                || Has<Beehive>(item)
                || Has<Snowball>(item)
                || Has<KnockOutPlayerOnImpact>(item)
                || Has<Mandrake>(item)),
    };

    /// <summary>建表时攒一份明细，建完由 <see cref="LogSummary"/> 打到日志里，方便回头核对分类。</summary>
    private static readonly List<Entry> Pricings = new();

    private readonly struct Entry
    {
        public Entry(string category, string name, int price)
        {
            Category = category;
            Name = name;
            Price = price;
        }

        public string Category { get; }

        public string Name { get; }

        public int Price { get; }
    }

    /// <summary>一件物资的默认基础价（还没乘全局物价倍率与玩家自己的折扣）。</summary>
    public static int BasePrice(Item item, Rarity rarity, string prefabName)
    {
        var index = (int)rarity;
        var price = index >= 0 && index < RarityBase.Length ? RarityBase[index] : RarityBase[0];

        var strength = Mathf.Clamp(ModConfig.ShopFunctionPremium.Value, 0f, 2f);
        var category = Find(item, out var floor, out var cap, out var food);

        if (food)
        {
            // 纯补给：先按稀有度打个折，再按住顶价 —— 稀有度只说明刷出来的概率，
            // 一株高稀有度的蘑菇不该比一捆绳子还贵。
            price = Mathf.RoundToInt(price * (1f - ((1f - FoodDiscount) * strength)));

            if (price > FoodCap)
            {
                price = Mathf.RoundToInt(price - ((price - FoodCap) * strength));
            }
        }
        else if (floor > price)
        {
            // 只补差价：0 = 不补（完全按稀有度），1 = 补到功能底线，2 = 把这份差价再翻一倍。
            price = Mathf.RoundToInt(price + ((floor - price) * strength));
        }
        else if (cap > 0 && price > cap)
        {
            // 顶价方向相反，比例一样：0 = 不压，1 = 压到顶价，2 = 压得更狠一点。
            price = Mathf.RoundToInt(price - ((price - cap) * strength));
        }

        price = Mathf.Clamp(Mathf.RoundToInt(price * Tweak(prefabName)), MinPrice, MaxPrice);

        if (Pricings.Count < 4000)
        {
            Pricings.Add(new Entry(category, prefabName, price));
        }

        return price;
    }

    /// <summary>
    /// 物资表建完后往日志里打一句总结：功能性定价到底认出了什么、最贵的是哪几件。
    /// <para>
    /// 归类是照组件猜的（游戏没给道具标「功能」这种属性），改完价格想知道
    /// 「踏板菇到底被算成了哪一类」时，看一眼日志比进游戏一件件翻商店快得多。
    /// </para>
    /// </summary>
    public static void LogSummary()
    {
        if (Pricings.Count == 0)
        {
            return;
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var plain = 0;

        foreach (var entry in Pricings)
        {
            if (entry.Category.Length == 0)
            {
                plain++;
            }
            else
            {
                counts.TryGetValue(entry.Category, out var count);
                counts[entry.Category] = count + 1;
            }
        }

        var categories = new List<string>(counts.Keys);
        categories.Sort(StringComparer.Ordinal);

        var builder = new StringBuilder();
        builder.Append($"商店定价：共 {Pricings.Count} 件，{Pricings.Count - plain} 件按功能改过价（");

        for (var i = 0; i < categories.Count; i++)
        {
            if (i > 0)
            {
                builder.Append('、');
            }

            builder.Append(categories[i]).Append(' ').Append(counts[categories[i]]);
        }

        builder.Append("）；最贵：");

        var top = new List<Entry>(Pricings);
        top.Sort((left, right) => right.Price.CompareTo(left.Price));

        var shown = Math.Min(10, top.Count);

        for (var i = 0; i < shown; i++)
        {
            if (i > 0)
            {
                builder.Append('、');
            }

            builder.Append(top[i].Name).Append(' ').Append(top[i].Price);
        }

        HextechPlugin.Log.LogInfo(builder.ToString());
        Pricings.Clear();
    }

    /// <summary>这件道具的归类、功能底线价与顶价，以及它是不是「没有别的功能」的食物。</summary>
    private static string Find(Item item, out int floor, out int cap, out bool food)
    {
        foreach (var rule in Rules)
        {
            if (!rule.Matches(item))
            {
                continue;
            }

            floor = rule.Floor;
            cap = rule.Cap;
            food = false;
            return rule.Category;
        }

        floor = 0;
        cap = 0;
        food = IsFood(item);
        return food ? "食物" : string.Empty;
    }

    /// <summary>道具身上挂没挂这个组件 —— 功能性就藏在组件里。子物体一起找：有的组件不在根节点上。</summary>
    private static bool Has<T>(Item item)
        where T : Component
    {
        return item.GetComponentInChildren<T>(true) != null;
    }

    /// <summary>游戏自己的食物标签：袋装食品、浆果、蘑菇。</summary>
    private static bool IsFood(Item item)
    {
        const Item.ItemTags food =
            Item.ItemTags.PackagedFood | Item.ItemTags.Berry | Item.ItemTags.Mushroom;

        return (item.itemTags & food) != Item.ItemTags.None;
    }

    /// <summary>
    /// 同一档里的东西不再都是一口价：按 prefab 名算一个上下 15% 左右的稳定浮动。
    /// <para>
    /// 用名字的哈希而不是随机数 —— 价格要在房主和每个客户端上算出同一个数，
    /// 重启游戏之后也不能变，否则调价面板里的「默认价」每次开机都不一样。
    /// </para>
    /// </summary>
    private static float Tweak(string prefabName)
    {
        if (string.IsNullOrEmpty(prefabName))
        {
            return 1f;
        }

        unchecked
        {
            var hash = 2166136261u;

            for (var i = 0; i < prefabName.Length; i++)
            {
                hash = (hash ^ prefabName[i]) * 16777619u;
            }

            var unit = (hash % 1024u) / 1023f;
            return TweakMin + ((TweakMax - TweakMin) * unit);
        }
    }
}
