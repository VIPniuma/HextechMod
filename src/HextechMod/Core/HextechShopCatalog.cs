using System;
using System.Collections.Generic;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace PeakModder.HextechMod;

/// <summary>商店里的一件商品。</summary>
internal sealed class ShopOffer
{
    private readonly Func<HextechState, List<RollShow>?> _purchase;
    private readonly Func<HextechState, bool>? _available;
    private readonly string? _unavailableHint;
    private readonly string? _unavailableToast;

    public ShopOffer(
        string title,
        string description,
        string tag,
        int baseCost,
        Color accent,
        Func<HextechState, List<RollShow>?> purchase,
        Texture2D? icon = null,
        Func<HextechState, bool>? available = null,
        string? glyph = null,
        int maxPerRun = 0,
        string? unavailableHint = null,
        string? unavailableToast = null,
        string? id = null,
        string? englishTitle = null)
    {
        // 调价表的 key。物资用 prefab 名，抽奖券用写死的短 id ——
        // 都刻意避开显示名：显示名会跟着游戏语言变，价格不该跟着语言一起丢。
        Id = string.IsNullOrEmpty(id) ? title : id;
        Title = title;
        EnglishTitle = englishTitle;
        Description = description;
        Tag = tag;
        BaseCost = baseCost;
        Accent = accent;
        Icon = icon;
        Glyph = string.IsNullOrEmpty(glyph)
            ? (string.IsNullOrEmpty(tag) ? "?" : tag.Substring(0, 1))
            : glyph;
        MaxPerRun = maxPerRun;
        _purchase = purchase;
        _available = available;
        _unavailableHint = unavailableHint;
        _unavailableToast = unavailableToast;
    }

    /// <summary>调价表里的唯一键（不随界面语言变）。</summary>
    public string Id { get; }

    public string Title { get; }

    /// <summary>PEAK 官方本地化数据里的英文名（见 <see cref="TryGetEnglishItemName"/>）；没有就 null。</summary>
    public string? EnglishTitle { get; }

    /// <summary>
    /// 界面上显示的名字：英文模式且有官方英文名时用英文名，否则中文名。
    /// 三重兜底（英文名 → 中文名 → 调价 key），保证**永远不会是空白**（2026-09-18 反馈的「商品没有名字」）。
    /// </summary>
    public string DisplayTitle
    {
        get
        {
            if (Localization.IsEnglish && !string.IsNullOrWhiteSpace(EnglishTitle))
            {
                return EnglishTitle!;
            }

            if (!string.IsNullOrWhiteSpace(Title))
            {
                return Title;
            }

            return Id;
        }
    }

    public string Description { get; }

    /// <summary>卡片角上的小标签（品质 / 档位）。</summary>
    public string Tag { get; }

    /// <summary>没乘任何倍率的原始价，只用来排序 / 做倍率基数。</summary>
    public int BaseCost { get; }

    /// <summary>
    /// 实际售价：（房主改过的价 ?? 基础价）× 配置倍率 × 玩家自己的折扣。
    /// <para>
    /// 折扣可能来自「讲价高手」这类按持有状态生效的词条，所以价钱只能在
    /// 「显示 / 结账的那一刻」算，不能在建表的时候写死。
    /// </para>
    /// </summary>
    public int CostFor(HextechState? state)
    {
        return HextechShopCatalog.Price(ShopPricing.OverrideFor(Id) ?? BaseCost, state);
    }

    public Color Accent { get; }

    /// <summary>游戏物品自带的图标，抽奖券没有图标（用符号代替）。</summary>
    public Texture2D? Icon { get; }

    /// <summary>没有图标时卡片上显示的符号。</summary>
    public string Glyph { get; }

    /// <summary>整局限购几次，0 表示不限购。计数按 <see cref="Title"/> 记在 <see cref="HextechState"/> 里。</summary>
    public int MaxPerRun { get; }

    /// <summary>本局已经买过几次。</summary>
    public int PurchasedCount(HextechState? state)
    {
        return state == null ? 0 : state.PurchaseCount(Title);
    }

    /// <summary>本局已经卖完（限购次数用完）。</summary>
    public bool SoldOut(HextechState? state)
    {
        return MaxPerRun > 0 && PurchasedCount(state) >= MaxPerRun;
    }

    /// <summary>卖完了、或者池子抽空之类的情况返回 false，商店会把卡片置灰。</summary>
    public bool Available(HextechState state)
    {
        return !SoldOut(state) && (_available == null || _available(state));
    }

    /// <summary>卡片上那行短提示（置灰时显示）。</summary>
    public string UnavailableHint => _unavailableHint ?? Localization.T("暂时不可用");

    /// <summary>点已置灰的卡片时弹出的说明。</summary>
    public string UnavailableToast => _unavailableToast ?? Localization.T("暂时没有可以给的强化了");

    /// <summary>
    /// 买下这件商品。代币不够时返回 false 且什么都不做。
    /// <paramref name="rolls"/> 不为空表示需要播抽奖动画（结果已经在里面了）。
    /// <para>
    /// ⚠ 扣钱**之前**再查一次可用性（700629）：UI 置灰只是展示层，这里不拦的话
    /// 「置灰的原因在发货时才暴露」就会变成钱扣了货没影 —— 雕像那次就是这样。
    /// </para>
    /// </summary>
    public bool TryBuy(HextechState state, out List<RollShow>? rolls)
    {
        rolls = null;

        if (SoldOut(state) || (_available != null && !_available(state)) || !state.TrySpendTokens(CostFor(state)))
        {
            return false;
        }

        rolls = _purchase(state);

        if (MaxPerRun > 0)
        {
            state.RecordPurchase(Title);
        }

        return true;
    }
}

/// <summary>
/// 海克斯商店的商品表。第 0 类是按品质分档的海克斯抽奖，其余四类按游戏自己的稀有度
/// 把「几乎所有能刷出来的物资」分好类，卡片直接用物品自带的图标。
/// <para>
/// 物资的价钱不按稀有度一条直线走：具体规则在 <see cref="ItemPricing"/>（稀有度基准 +
/// 功能性溢价 + 个体微调），这里只负责把商品卡片建出来。
/// </para>
/// </summary>
internal static class HextechShopCatalog
{
    public const int TabCount = 5;

    private static readonly string[] TabTitles = { "海克斯", "普通物资", "精良物资", "稀有物资", "传说物资" };

    private static readonly List<ShopOffer>[] Tabs;

    private static readonly System.Random Rng = new();

    private static bool _itemsBuilt;

    static HextechShopCatalog()
    {
        Tabs = new List<ShopOffer>[TabCount];

        for (var i = 0; i < TabCount; i++)
        {
            Tabs[i] = new List<ShopOffer>();
        }

        BuildHextechTab();

        // 童军雕像：不在物资池里（是场景物件不是物品），静态挂进「传说物资」页，
        // 200 枚一口价。和物资一样机场里禁售 —— 在机场生成的物件上岛后就落在错误的坐标上。
        // ⚠ 描述里必须写明「不能复活死人」：游戏侧 PetrifiedScout 只有「生成 / 打碎」两个功能，
        // 全程序集没有任何复活 API（2026-09-22 实读确认）。玩家老把它当复活道具买，买完发现
        // 「没反应」（雕像只会在买家面前生成、绑定买家自己）就来反馈。
        Tabs[4].Add(new ShopOffer(
            "童军雕像",
            "一座石化童军雕像，照买家形象定制，生成在你面前（联机由房主代为生成）。只是个撞狠了会碎的物理物件 —— 它不能复活死人。",
            "特殊",
            200,
            RarityColor(Rarity.Legendary),
            DropScoutStatue,
            icon: ScoutStatueIcon(),
            glyph: "▲",
            available: _ => !HextechScene.InAirport && ScoutStatueLoadablePath() != null,
            unavailableHint: "上岛之后再买",
            unavailableToast: "机场里生成的雕像上岛后会落在错误的坐标上，等于白买 —— 上岛之后再买",
            id: "statue.scout",
            englishTitle: "Scout Statue"));
    }

    public static string TabTitle(int tab)
    {
        var title = tab >= 0 && tab < TabTitles.Length ? TabTitles[tab] : TabTitles[0];
        return Localization.T(title);
    }

    // ── 物品英文名（PEAK 官方资产数据）──────────────────────────
    //
    // 英文名**不是我们翻的**：Item.UIData.itemName 是游戏资产里写的原始名字（英文原文），
    // 这正是 PEAK 自己给这件物品起的官方英文名。第一版去翻 LocalizedText 的文本表，
    // 表结构对不上导致一堆卡片名字空白（2026-09-18 反馈），换成直接读 itemName。
    private static Dictionary<string, string>? _englishNames;
    private static bool _englishNamesBroken;

    internal static string? TryGetEnglishItemName(Item item)
    {
        if (!Localization.IsEnglish || _englishNamesBroken || item == null)
        {
            return null;
        }

        try
        {
            var data = item.UIData;
            var name = data != null ? data.itemName : null;

            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            _englishNames ??= new Dictionary<string, string>(StringComparer.Ordinal);
            _englishNames[name] = name;
            return name;
        }
        catch (Exception)
        {
            // 读不到就算了：商店宁可用中文名，也不能因为取英文名挂掉。
            _englishNamesBroken = true;
            return null;
        }
    }

    /// <summary>取某一类的商品。物资分类是第一次打开商店时扫描游戏物品表得到的。</summary>
    public static IReadOnlyList<ShopOffer> Offers(int tab)
    {
        EnsureItems();

        return tab <= 0 || tab >= TabCount ? Tabs[0] : Tabs[tab];
    }

    /// <summary>
    /// 海克斯抽奖那几档券：静态就有，不需要扫物资表，所以配置面板任何时候都能列出来调价。
    /// </summary>
    public static IReadOnlyList<ShopOffer> TicketOffers => Tabs[0];

    /// <summary>
    /// 物资表扫出来没有。配置面板靠它决定要不要列物资的调价行 ——
    /// 面板不该替商店决定「什么时候扫」（扫描失败会被商店记成「这次不卖物资」）。
    /// </summary>
    public static bool ItemsReady => _itemsBuilt;

    /// <summary>
    /// 基础价 × 配置倍率 × 玩家折扣 = 实际售价。
    /// 调低倍率会让游戏简单很多，所以默认 1.0；折扣是「讲价高手」那类词条给的。
    /// </summary>
    internal static int Price(int baseCost, HextechState? state)
    {
        var multiplier = ModConfig.ShopPriceMultiplier.Value;

        if (multiplier < 0.1f)
        {
            multiplier = 0.1f;
        }

        multiplier *= DiscountOf(state);
        return Mathf.Max(1, Mathf.RoundToInt(baseCost * multiplier));
    }

    /// <summary>
    /// 「讲价高手」给的折扣：一层就是五折，两层四分之一，以此类推。
    /// 没拿到就是 1（原价）。
    /// </summary>
    private static float DiscountOf(HextechState? state)
    {
        if (state == null)
        {
            return 1f;
        }

        var stacks = state.StackOfId(AdvancedHextechs.ShopHalfPriceId);
        return stacks <= 0 ? 1f : Mathf.Pow(0.5f, stacks);
    }

    // ── 海克斯抽奖 ───────────────────────────────────────────────
    /// <summary>
    /// 商店只卖「抽奖」，不再直接卖道具。价格故意定得不便宜——代币是每分钟 1.5 枚的，
    /// 一局下来也就够抽一两次，抽奖是这一局的额外惊喜而不是常规补给。
    /// </summary>
    private static void BuildHextechTab()
    {
        // 抽奖券整局每档只能买一次，价格也比最早那版贵 1.5 倍：
        // 代币是局内按分钟发的，一局下来只够抽一两次，不能让玩家蹲在商店里把池子刷空。
        Tabs[0].Add(new ShopOffer(
            "海克斯抽奖 · 青铜",
            "随机 1 项正面强化，以青铜为主，小概率直接开出白银或黄金。整局限购 1 次。",
            "青铜",
            72,
            UiFactory.QualityColor(HextechQuality.Bronze),
            state => DrawOne(state, null, "海克斯抽奖 · 青铜"),
            available: state => HextechRegistry.HasAny(state, maxQuality: HextechQuality.Gold),
            glyph: "◈",
            maxPerRun: 1,
            id: "hextech.bronze"));

        Tabs[0].Add(new ShopOffer(
            "海克斯抽奖 · 白银",
            "保底白银，有一定概率直接开出黄金。整局限购 1 次。",
            "白银",
            180,
            UiFactory.QualityColor(HextechQuality.Silver),
            state => DrawOne(state, HextechQuality.Silver, "海克斯抽奖 · 白银"),
            available: state => HextechRegistry.HasAny(state, HextechQuality.Silver, HextechQuality.Gold),
            glyph: "◆",
            maxPerRun: 1,
            id: "hextech.silver"));

        Tabs[0].Add(new ShopOffer(
            "海克斯抽奖 · 黄金",
            "必定开出 1 项黄金强化。整局限购 1 次。",
            "黄金",
            360,
            UiFactory.QualityColor(HextechQuality.Gold),
            state => DrawOne(state, HextechQuality.Gold, "海克斯抽奖 · 黄金"),
            available: state => HextechRegistry.HasAny(state, HextechQuality.Gold, HextechQuality.Gold),
            glyph: "★",
            maxPerRun: 1,
            id: "hextech.gold"));

        Tabs[0].Add(new ShopOffer(
            "强化礼包 · 三连",
            "一次开出 3 项互不重复的正面强化，比单抽划算。整局限购 1 次。",
            "组合",
            220,
            UiFactory.Warning,
            DrawBundle,
            available: state => HextechRegistry.HasAny(state),
            // 这里原本用的是「✦」(U+2726)。游戏那套字体（DarumaDropOne / 中文回退字体）
            // 都没有这个 Dingbats 区的字形，TMP 找不到就退成「□」，
            // 日志里能看到 "Unicode value \u2726 (✦) ... replaced by \u25A1 (□)"。
            // 换成 U+25CF 这个「实心圆」，它在中文回退字体里必定存在（三选一徽章、HUD 都在用）。
            glyph: "●",
            maxPerRun: 1,
            id: "hextech.bundle"));
    }

    private static List<RollShow>? DrawOne(HextechState state, HextechQuality? minQuality, string header)
    {
        // 商店的券开到黄金为止：传说档只能靠自然三选一 / 开行李箱碰运气，不能拿代币硬刷。
        var entry = HextechRegistry.RollOne(
            state,
            Rng,
            allowNegative: false,
            exclude: null,
            minQuality: minQuality,
            maxQuality: HextechQuality.Gold);

        if (entry == null)
        {
            return null;
        }

        // 统一授权入口：撞上拿取上限时转替换面板（普通 4 条 / 技能 1 条，2026-09-14）。
        HextechManager.Instance?.AcquireOrQueueReplacement(state, entry);

        return new List<RollShow>
        {
            new(header, BuildPool(minQuality, HextechQuality.Gold), ToSlot(entry, state.StackOf(entry))),
        };
    }

    private static List<RollShow>? DrawBundle(HextechState state)
    {
        var entries = HextechRegistry.Roll(
            state, 3, Rng, allowNegative: false, maxQuality: HextechQuality.Gold);

        if (entries.Count == 0)
        {
            return null;
        }

        var pool = BuildPool(null, HextechQuality.Gold);
        var shows = new List<RollShow>();

        foreach (var entry in entries)
        {
            HextechManager.Instance?.AcquireOrQueueReplacement(state, entry);
            shows.Add(new RollShow("强化礼包 · 三连", pool, ToSlot(entry, state.StackOf(entry))));
        }

        HextechHud.Toast(Localization.T("礼包开出：{0}", string.Join("、", entries.ConvertAll(entry => entry.Title))));
        return shows;
    }

    /// <summary>抽奖动画里陪跑的格子。纯装饰，跟着这一档抽奖能开出的范围走。</summary>
    private static List<RollSlot> BuildPool(HextechQuality? minQuality, HextechQuality? maxQuality = null)
    {
        var pool = new List<RollSlot>();

        foreach (var entry in HextechRegistry.All)
        {
            if (entry.Negative)
            {
                continue;
            }

            if (minQuality.HasValue && entry.Quality < minQuality.Value)
            {
                continue;
            }

            if (maxQuality.HasValue && entry.Quality > maxQuality.Value)
            {
                continue;
            }

            pool.Add(ToSlot(entry));
        }

        if (pool.Count == 0)
        {
            foreach (var entry in HextechRegistry.All)
            {
                pool.Add(ToSlot(entry));
            }
        }

        return pool;
    }

    /// <summary>
    /// <paramref name="stacks"/> 大于 0 时把「当前层数下的实际效果」一起带上，
    /// 抽奖结果条会显示，光看名字也能知道抽到的东西到底干什么。
    /// 陪跑的装饰格子传 0，省掉没必要的字符串拼接。
    /// </summary>
    private static RollSlot ToSlot(HextechEntry entry, int stacks = 0)
    {
        return new RollSlot(
            entry.Title,
            UiFactory.QualityName(entry.Quality),
            UiFactory.QualityColor(entry.Quality),
            null,
            stacks > 0 ? entry.Summary(stacks) : null);
    }

    // ── 物资扫描 ─────────────────────────────────────────────────
    private static void EnsureItems()
    {
        if (_itemsBuilt)
        {
            return;
        }

        _itemsBuilt = true;

        try
        {
            LootData.PopulateLootData();
        }
        catch (Exception exception)
        {
            HextechPlugin.Log.LogWarning($"扫描物资池失败，商店这次不卖物资：{exception.Message}");
            return;
        }

        var pools = LootData.AllSpawnWeightData;

        if (pools == null)
        {
            HextechPlugin.Log.LogWarning("物资池还是空的，商店这次不卖物资。");
            return;
        }

        var seen = new HashSet<ushort>();
        var added = 0;

        foreach (var pool in pools.Values)
        {
            foreach (var id in pool.Keys)
            {
                if (!seen.Add(id))
                {
                    continue;
                }

                if (!ItemDatabase.TryGetItem(id, out var item) || item == null)
                {
                    continue;
                }

                added += AddItem(item) ? 1 : 0;
            }
        }

        for (var tab = 1; tab < TabCount; tab++)
        {
            Tabs[tab].Sort((left, right) =>
            {
                var byCost = left.BaseCost.CompareTo(right.BaseCost);
                return byCost != 0 ? byCost : string.CompareOrdinal(left.Title, right.Title);
            });
        }

        HextechPlugin.Log.LogInfo($"商店物资表已生成，共 {added} 件。");
        ItemPricing.LogSummary();
    }

    // 2026-09-16 应反馈下架：潘多拉魔盒、诅咒之书（玩家觉得商店这两件超标）。
    // 显示名是商店里实际看到的中文名（最准），prefab 名作兜底（防止 GetName 取不到时漏网）。
    private static readonly HashSet<string> BannedShopItemNames = new()
    {
        "潘多拉魔盒",
        "诅咒之书",
    };

    private static readonly string[] BannedShopItemPrefabHints =
    {
        "pandora", "cursedtome", "bookofcurse", "cursesbook",
    };

    private static bool IsBannedShopItem(Item item, string prefabName)
    {
        var name = ResolveName(item, prefabName);

        if (BannedShopItemNames.Contains(name))
        {
            return true;
        }

        var lower = prefabName.ToLowerInvariant();

        foreach (var hint in BannedShopItemPrefabHints)
        {
            if (lower.Contains(hint))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>把一件物资塞进对应分类。返回 false 表示这东西不该卖（图鉴、石头、怪物道具、成就收集品等）。</summary>
    private static bool AddItem(Item item)
    {
        var loot = item.GetComponent<LootData>();

        if (loot == null)
        {
            return false;
        }

        if (item is Guidebook || item is Stone || item is MobItem)
        {
            return false;
        }

        // 金神像之类的成就收集品不卖，免得花点代币就把成就拿了。
        if ((item.itemTags & Item.ItemTags.GoldenIdol) != 0)
        {
            return false;
        }

        // 单人局刷不出来（需要多人配合触发）的东西也不卖。
        if (loot.banInSolo && (!PhotonNetwork.InRoom || PhotonNetwork.CurrentRoom == null || PhotonNetwork.CurrentRoom.PlayerCount <= 1))
        {
            return false;
        }

        var prefabName = item.gameObject.name;

        if (string.IsNullOrEmpty(prefabName))
        {
            return false;
        }

        // 2026-09-16 应反馈下架：潘多拉魔盒、诅咒之书（玩家觉得商店这两件超标）。
        if (IsBannedShopItem(item, prefabName))
        {
            return false;
        }

        var rarity = loot.Rarity;

        Tabs[TabOf(rarity)].Add(new ShopOffer(
            ResolveName(item, prefabName),
            "购买后直接进你的物品栏（满了才会掉在面前）。",
            RarityName(rarity),
            ItemPricing.BasePrice(item, rarity, prefabName),
            RarityColor(rarity),
            state => DropItem(state, prefabName),
            ResolveIcon(item),
            // 物资是「房间物件」，机场场景里掉出来的那些上岛之后不会被清掉，
            // 但会留在机场那套世界坐标上 —— 和岛屿地形完全对不上，玩家根本捡不到。
            // 所以机场里直接把这几个分类置灰，别让玩家白花代币。
            available: _ => !HextechScene.InAirport,
            unavailableHint: "上岛之后再买",
            unavailableToast: "机场里掉出来的物资上岛后会落在错误的坐标上，等于白买 —— 上岛之后再买",
            id: prefabName,
            englishTitle: TryGetEnglishItemName(item)));

        return true;
    }

    /// <summary>取物品的显示名。行李箱抽奖的动画也会用到，所以是 internal。</summary>
    internal static string ResolveName(Item item, string prefabName)
    {
        try
        {
            var name = item.GetName();

            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }
        catch (Exception)
        {
            // 有些物品没有 UI 数据，取名字会炸，退回内部名字。
        }

        return prefabName;
    }

    /// <summary>直接用物品自带的图标；不走 GetIcon()，那个会读设置管理器，早期可能为空。</summary>
    internal static Texture2D? ResolveIcon(Item item)
    {
        var data = item.UIData;

        if (data == null)
        {
            return null;
        }

        if (data.icon != null)
        {
            return data.icon;
        }

        return data.hasAltIcon ? data.altIcon : null;
    }

    private static int TabOf(Rarity rarity)
    {
        return rarity switch
        {
            Rarity.Uncommon or Rarity.Rare => 2,
            Rarity.Epic or Rarity.Legendary => 3,
            Rarity.Mythic or Rarity.RidiculouslyRare => 4,
            _ => 1,
        };
    }

    private static string RarityName(Rarity rarity)
    {
        return Localization.T(rarity switch
        {
            Rarity.Uncommon => "精良",
            Rarity.Rare => "稀有",
            Rarity.Epic => "史诗",
            Rarity.Legendary => "传奇",
            Rarity.Mythic => "神话",
            Rarity.RidiculouslyRare => "荒诞",
            _ => "普通",
        });
    }

    private static Color RarityColor(Rarity rarity)
    {
        return rarity switch
        {
            Rarity.Uncommon => new Color(0.45f, 0.85f, 0.45f, 1f),
            Rarity.Rare => new Color(0.35f, 0.72f, 1f, 1f),
            Rarity.Epic => new Color(0.78f, 0.45f, 1f, 1f),
            Rarity.Legendary => new Color(1f, 0.70f, 0.25f, 1f),
            Rarity.Mythic => new Color(1f, 0.35f, 0.50f, 1f),
            Rarity.RidiculouslyRare => new Color(1f, 0.95f, 0.40f, 1f),
            _ => new Color(0.72f, 0.76f, 0.82f, 1f),
        };
    }

    // ── 发放 ─────────────────────────────────────────────────────
    private static List<RollShow>? DropItem(HextechState state, string prefabName)
    {
        var character = state.Character;

        if (character == null)
        {
            return null;
        }

        // 物品仍然要实例化一次（网络物件必须由房主生成），生成完房主会直接把它交给买家：
        // 走原版 Item.RequestPickup，结果和玩家自己捡起来一样 —— 进物品栏，而不是躺在地上。
        // 物品栏满了才会留在原地，玩家自己走过去捡。
        var origin = Origin(character);

        HextechSpawning.RequestSpawn(null, new[] { prefabName }, new[] { origin }, kinematic: false, giveTo: character);
        return null;
    }

    private static Vector3 Origin(Character character)
    {
        return character.transform.position
               + (character.transform.forward * 2f)
               + (Vector3.up * 1.5f);
    }

    // ── 童军雕像 ─────────────────────────────────────────────────
    /// <summary>
    /// 雕像的 prefab 名是游戏自己的常量（<c>Character.PETRIFIED_PREFAB</c>），不随语言和版本变；
    /// 发货走物资同一条通道（非房主转给房主生成），只是它不是 Item，房主侧会走「场景物件」分支
    /// 并用原版 RPC 把雕像绑定成买家的石化形态（见 <see cref="HextechSpawning.Spawn"/>）。
    /// </summary>
    private static List<RollShow>? DropScoutStatue(HextechState state)
    {
        var character = state.Character;

        if (character == null)
        {
            return null;
        }

        string? prefabName;

        try
        {
            prefabName = AccessTools.Field(typeof(Character), "PETRIFIED_PREFAB")?.GetValue(null) as string;
        }
        catch (Exception)
        {
            prefabName = null;
        }

        if (string.IsNullOrWhiteSpace(prefabName))
        {
            HextechPlugin.Log.LogWarning("童军雕像生成失败：游戏没有提供 PETRIFIED_PREFAB 预制体名。");
            HextechHud.Toast("雕像暂时生成不了");
            return null;
        }

        // 700629：Photon 的 DefaultPool 只会 Resources.Load，路径不对就静默返回 null ——
        // 而购买流程先扣钱后发货，玩家白扣 200 枚。发货前先确认这条路径真的能加载。
        var loadablePath = ScoutStatueLoadablePath();

        if (loadablePath == null)
        {
            HextechPlugin.Log.LogWarning(
                $"[海克斯] 童军雕像生成失败：Resources 里找不到 {prefabName}（试过裸名 / 0_Items/ / Items/）。");
            HextechHud.Toast("雕像暂时生成不了");
            return null;
        }

        HextechSpawning.RequestSpawn(null, new[] { loadablePath }, new[] { Origin(character) }, kinematic: false, giveTo: character);
        return null;
    }

    /// <summary>
    /// 探测雕像预制体真正能被 Photon 加载的 Resources 路径，找不到返回 null。
    /// PETRIFIED_PREFAB 是裸名「PetrifiedScout」，但游戏版本之间资源位置不一定一致 ——
    /// 挨个候选路径试（裸名 / 0_Items/ / Items/），找到哪个能 Load 就用哪个发货。
    /// </summary>
    internal static string? ScoutStatueLoadablePath()
    {
        string? prefabName;

        try
        {
            prefabName = AccessTools.Field(typeof(Character), "PETRIFIED_PREFAB")?.GetValue(null) as string;
        }
        catch (Exception)
        {
            prefabName = null;
        }

        if (string.IsNullOrWhiteSpace(prefabName))
        {
            return null;
        }

        foreach (var candidate in new[] { prefabName, "0_Items/" + prefabName, "Items/" + prefabName })
        {
            if (Resources.Load<GameObject>(candidate) != null)
            {
                return candidate;
            }
        }

        return null;
    }

    private static Texture2D? _scoutStatueIcon;

    /// <summary>
    /// 从游戏自带的 PetrifiedScout 预制体里抠出图标：优先 SpriteRenderer（UI 图标），
    /// 没有就退而取 MeshRenderer 的材质贴图（石像真实纹理）。都拿不到返回 null，UI 回退到符号。
    /// </summary>
    private static Texture2D? ScoutStatueIcon()
    {
        if (_scoutStatueIcon != null)
        {
            return _scoutStatueIcon;
        }

        string? prefabName = null;
        try
        {
            prefabName = AccessTools.Field(typeof(Character), "PETRIFIED_PREFAB")?.GetValue(null) as string;
        }
        catch (Exception)
        {
            prefabName = null;
        }

        if (string.IsNullOrWhiteSpace(prefabName))
        {
            return null;
        }

        // 生成走 Photon 的 0_Items/ 路径（见 HextechSpawning.Spawn 的报错），多试几个前缀兜底。
        GameObject? prefab = null;
        foreach (var path in new[] { prefabName, "0_Items/" + prefabName, "Items/" + prefabName })
        {
            prefab = Resources.Load<GameObject>(path);
            if (prefab != null)
            {
                break;
            }
        }

        if (prefab == null)
        {
            HextechPlugin.Log.LogWarning($"童军雕像图标抓取失败：Resources 里找不到预制体 \"{prefabName}\"。");
            return null;
        }

        foreach (var sr in prefab.GetComponentsInChildren<SpriteRenderer>(true))
        {
            if (sr.sprite != null && sr.sprite.texture != null)
            {
                _scoutStatueIcon = sr.sprite.texture;
                HextechPlugin.Log.LogInfo($"童军雕像图标来自 SpriteRenderer：{sr.sprite.name}（{_scoutStatueIcon.width}x{_scoutStatueIcon.height}）");
                return _scoutStatueIcon;
            }
        }

        foreach (var mr in prefab.GetComponentsInChildren<MeshRenderer>(true))
        {
            var mat = mr.sharedMaterial;
            if (mat != null && mat.mainTexture is Texture2D tex && tex.width > 1 && tex.height > 1)
            {
                _scoutStatueIcon = tex;
                HextechPlugin.Log.LogInfo($"童军雕像图标来自 MeshRenderer 材质：{tex.name}（{tex.width}x{tex.height}）");
                return _scoutStatueIcon;
            }
        }

        HextechPlugin.Log.LogWarning("童军雕像预制体上既没有 SpriteRenderer 也没有可用的材质贴图，图标回退到符号。");
        return null;
    }
}
