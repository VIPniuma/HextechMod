using System.Collections.Generic;
using HarmonyLib;
using Peak.Afflictions;
using UnityEngine;

namespace PeakModder.HextechMod;

/// <summary>
/// 第二批词条：说不成「按层乘一个系数」、要盯着游戏事件的特殊机制词条。
/// <para>
/// 共同点：效果全部在「本地玩家自己的客户端」上结算 —— 每台机器只负责自己的角色，
/// 需要知道别人有什么词条时就读同步过来的 <see cref="HextechState"/>（没有装 mod 的队友不参与）。
/// </para>
/// </summary>
internal static class AdvancedHextechs
{
    public const string LeaderId = "leader";
    public const string GrabHandId = "grab_hand";
    public const string ChefId = "chef";
    public const string BackpackerId = "backpacker";
    public const string StillLightId = "still_light";
    public const string HotHandsId = "hot_hands";
    public const string MoraleBoostId = "morale_boost";
    public const string AllForOneId = "all_for_one";
    public const string ShopHalfPriceId = "shop_half_price";
    public const string SporeImmunityId = "spore_immunity";
    public const string FiresideChatId = "fireside_chat";
    public const string CollectorId = "collector";
    public const string PalanquinBearerId = "palanquin_bearer";
    public const string UndertakerId = "undertaker";
    public const string CapitalistId = "capitalist";
    public const string OsteoporosisId = "osteoporosis";
    public const string ResurrectId = "resurrect";
    public const string ButterfingersId = "butterfingers";
    public const string DeadweightId = "deadweight";
    public const string NightBlindId = "night_blind";

    /// <summary>收藏家：每层给商店代币积累速度的加成（0.5 = 一层 +50%）。</summary>
    public static float CollectorTokenBonusPerStack = 0.5f;

    /// <summary>
    /// 资本家：每层每分钟额外发的商店代币数。这是「绝对收入」，
    /// 不随配置里「每分钟发几枚」缩放 —— 所以说不走品质表。
    /// </summary>
    public static float CapitalistTokensPerMinute = 0.5f;

    /// <summary>
    /// 围炉谈心：算「抱团」的水平距离 —— 大约两个身位，人得真的挨在一起才算。
    /// 忽略高度差 —— 上下两层贴着站也算抱在一起，不然在吊桥 / 台阶上明明肩并肩却回不了体力。
    /// </summary>
    public const float FiresideGroupRadius = 3f;

    /// <summary>围炉谈心：每隔这么多秒结算一次（回 1 点受伤值）。</summary>
    public static float FiresideIntervalSeconds = 10f;

    /// <summary>送葬人：判定「附近」的半径。</summary>
    public static float UndertakerRadius = 10f;

    /// <summary>送葬人：一次回复的体力。</summary>
    public static float UndertakerStamina = 20f;

    /// <summary>送葬人：回复之后加速的时长。</summary>
    public static float UndertakerBuffSeconds = 4f;

    /// <summary>拉拉手：拉手交互距离的倍数（+50%）。</summary>
    public static float GrabHandDistanceMultiplier = 1.5f;

    /// <summary>拉拉手：一次成功拉手回复的体力。</summary>
    public static float GrabHandStamina = 20f;

    /// <summary>背包客：额外给的背包格数（只给滑稽背包，见 <see cref="BackpackCapacity"/>）。</summary>
    public static int BackpackerExtraSlots = 2;

    /// <summary>我还有光：每层每秒转化掉多少「石化上限」的石化值。</summary>
    public static float StillLightConvertPerSecond = 0.04f;

    /// <summary>
    /// 我还有光：石化值超过「石化上限的多少」之后才开始转化。
    /// 不到这条线就原样留着 —— 该来的石化压力还是实打实的，只有攒过半之后才慢慢泄掉。
    /// </summary>
    public static float StillLightConvertThreshold = 0.5f;

    /// <summary>士气大提升：离开篝火后加速与无限体力的时长。</summary>
    public static float MoraleBoostSeconds = 16f;

    /// <summary>人人为我：每层补充的额外体力。</summary>
    public static float AllForOneExtraStamina = 60f;

    /// <summary>
    /// 手滑：每层把「投掷蓄力」乘以该系数 —— 蓄满力也只有七成，扔得又近又软。
    /// 改的是 RPC 收到的蓄力值，所以每台客户端算出来的初速度一致，不会各算各的。
    /// </summary>
    public static float ButterfingersThrowFactorPerStack = 0.7f;

    /// <summary>拖后腿：判定「附近」的半径（与「围炉谈心」的抱团距离同量级）。</summary>
    public const float DeadweightRadius = 6f;

    /// <summary>拖后腿：每层让附近队友的体力消耗乘以该系数。</summary>
    public static float DeadweightStaminaPerStack = 1.12f;

    /// <summary>拖后腿：体力消耗最多被放大到几倍（两边都叠满也不至于走两步就趴下）。</summary>
    public static float DeadweightMaxFactor = 2f;

    /// <summary>夜盲：一层时隔多少秒失明一次（层数越高间隔越短）。</summary>
    public const float NightBlindIntervalSeconds = 90f;

    /// <summary>夜盲：一次失明的时长。</summary>
    public const float NightBlindDurationSeconds = 4f;

    private const string GrabHandTimerLast = "grab_hand_last";
    private const string StillLightTimerCarry = "still_light_carry";
    private const string MoraleTimerWasInside = "morale_was_inside";
    private const string FiresideTimerKey = "fireside_timer";
    private const string PalanquinTimerApplied = "palanquin_applied";
    private const string NightBlindTimerKey = "night_blind_timer";

    private static List<Campfire>? _campfires;
    private static float _campfireRefreshTime;

    public static void Register()
    {
        RegisterLeader();
        RegisterDeadweight();
        RegisterGrabHand();
        RegisterChef();
        RegisterBackpacker();
        RegisterStillLight();
        RegisterHotHands();
        RegisterMoraleBoost();
        RegisterAllForOne();
        RegisterShopHalfPrice();
        RegisterSporeImmunity();
        RegisterFiresideChat();
        RegisterCollector();
        RegisterCapitalist();
        RegisterPalanquinBearer();
        RegisterUndertaker();
        RegisterOsteoporosis();
        RegisterResurrect();
        RegisterButterfingers();
        RegisterNightBlind();
    }

    /// <summary>
    /// 单名领队（一层）下方队友的体力消耗系数，给 <see cref="HextechAdvancedPatches.LeaderAura"/> 用。
    /// </summary>
    public static float LeaderAuraFactor(int stacks)
    {
        return 1f - HextechQuality.Silver.TotalValue(stacks);
    }

    // ⚠️ 铁律（2026-09-13 用户定的）：除主动技能外，任何词条都**没有冷却** —— 光环类词条（领头羊 / 拖后腿）
    // 的 ComputeFactor 里那 0.25 秒是「缓存刷新节流」，不是冷却；领队 / 送葬人 / 人人为我里的 2 秒窗口是
    // 「同一次死亡 / 倒地会走两条 RPC」的去重，也不是冷却 —— 别把这几处当 CD 删掉，也别再给词条加 CD。

    /// <summary>
    /// 「拖后腿」叠了 N 层时，附近队友的体力消耗系数（> 1），给
    /// <see cref="HextechAdvancedPatches.Deadweight"/> 用。
    /// </summary>
    public static float DeadweightFactor(int stacks)
    {
        return Mathf.Min(DeadweightMaxFactor, Mathf.Pow(DeadweightStaminaPerStack, Mathf.Max(1, stacks)));
    }

    /// <summary>
    /// 「手滑」叠了 N 层时的投掷力度系数（< 1），给
    /// <see cref="HextechAdvancedPatches.Butterfingers"/> 用。
    /// </summary>
    public static float ButterfingersFactor(int stacks)
    {
        return Mathf.Pow(ButterfingersThrowFactorPerStack, Mathf.Max(1, stacks));
    }

    /// <summary>
    /// 厨师：从固定的几项增益里随机挑一项。
    /// 只用游戏自带的 Affliction —— 自造状态同步不出去。
    /// </summary>
    public static Affliction RollChefBuff()
    {
        return UnityEngine.Random.Range(0, 5) switch
        {
            0 => new Affliction_AddBonusStamina { totalTime = 1f, staminaAmount = 30f },
            1 => new Affliction_HealAll { totalTime = 1f, maxHealing = 25f },
            2 => new Affliction_BingBongShield { totalTime = 20f },
            3 => new Affliction_FasterBoi { totalTime = 30f, moveSpeedMod = 0.2f, climbSpeedMod = 0.25f },
            _ => new Affliction_ClimbingChalk { totalTime = 60f, climbStaminaMultiplier = 0.8f },
        };
    }

    // ── 领头羊 ───────────────────────────────────────────────────
    /// <summary>
    /// 这条词条本身不给持有者任何加成 —— 拿到它的人是「领队」，
    /// 效果落在高度比他低的队友身上，由那名队友的客户端结算（见补丁）。
    /// </summary>
    private static void RegisterLeader()
    {
        Register(new ActionHextech(
            LeaderId,
            "领头羊",
            "高度低于你的队友，体力消耗 -12%。",
            HextechQuality.Silver,
            stackable: true,
            maxStacks: 2));
    }

    // ── 拉拉手 ───────────────────────────────────────────────────
    /// <summary>
    /// 拉手距离按层乘上去（+50% 再加一层就是 +125%，和品质叠加规则一致）；
    /// 成功拉手靠 <c>sinceGrabFriend</c> 被清零来判断，所以需要自己那一侧才算得到。
    /// <para>⚠️ 没有 CD —— 用户定的铁律：除主动技能外词条一律没有冷却（2026-09-13 把这里原来的 6 秒冷却删了）。</para>
    /// </summary>
    private static void RegisterGrabHand()
    {
        Register(new ActionHextech(
            GrabHandId,
            "拉拉手",
            "拉手（扶起队友）的交互距离 +50%；每次成功拉手立刻回复 20 点体力。",
            HextechQuality.Silver,
            (state, _) =>
            {
                var data = state.Character.data;

                if (data != null)
                {
                    data.grabFriendDistance *= GrabHandDistanceMultiplier;
                }
            },
            onTick: TickGrabHand,
            stackable: true,
            maxStacks: 2));
    }

    private static void TickGrabHand(HextechState state, int stacks, float deltaTime)
    {
        var character = state.Character;
        var data = character.data;

        if (!character.IsLocal || data == null)
        {
            return;
        }

        var now = data.sinceGrabFriend;
        var last = state.GetTimer(GrabHandTimerLast);

        // 成功拉手会把 sinceGrabFriend 清零，计时器突然回退就是「刚刚拉上手」。
        if (last > 0.25f && now < last - 0.05f)
        {
            character.AddStamina(GrabHandStamina);
        }

        state.SetTimer(GrabHandTimerLast, now);
    }

    // ── 厨师 ─────────────────────────────────────────────────────
    /// <summary>烤好的食物随机附带一项增益，由补丁挂在食物上，吃到嘴里时结算。</summary>
    private static void RegisterChef()
    {
        Register(new ActionHextech(
            ChefId,
            "厨师",
            "你在篝火上烤好的食物会随机附带一项增益：回体 / 治伤 / 护盾 / 加速 / 攀爬省力。",
            HextechQuality.Silver));
    }

    // ── 背包客 ───────────────────────────────────────────────────
    /// <summary>
    /// 背包容量由补丁扩大，这里只负责登记词条。
    /// <para>
    /// 只有「滑稽背包」算数：普通背包原版就 4 格（扩了也看不出多出来），喷气背包 / 火箭背包
    /// 本身没有物品格，火箭背包背上之后按住 E 是点火，动它的格子会出怪状态，所以都没算。
    /// </para>
    /// </summary>
    private static void RegisterBackpacker()
    {
        Register(new ActionHextech(
            BackpackerId,
            "背包客",
            "你背的滑稽背包可以额外放入 2 件物品（普通背包、喷气背包、火箭背包不算）。",
            HextechQuality.Silver));
    }

    // ── 我还有光 ─────────────────────────────────────────────────
    /// <summary>
    /// 石化值过半（上限的 50%）之后才开始转化：先补「额外体力」，额外体力满了之后才积累到体力上。
    /// 一半以内原样留着，所以该被石化的压力还在；转化是连续的小额扣除（按石化上限的百分比），
    /// 石化值涨得太快照样会被石化。
    /// </summary>
    private static void RegisterStillLight()
    {
        Register(new ActionHextech(
            StillLightId,
            "我还有光",
            "石化值攒到上限的一半之后，超出的部分会先替你补满「额外体力」，额外体力满了之后转化为体力（每层每秒转化 4% 石化上限）。",
            HextechQuality.Gold,
            onTick: TickStillLight,
            drawback: new HextechDrawback(
                "石化转化的代价是体温：夜间寒冷积累速度 +20%。",
                (state, _) => state.Character.refs.afflictions.nightColdPerSecond *= 1.2f),
            stackable: true,
            maxStacks: 3));
    }

    private static void TickStillLight(HextechState state, int stacks, float deltaTime)
    {
        var character = state.Character;
        var data = character.data;

        if (!character.IsLocal || data == null)
        {
            return;
        }

        var afflictions = character.refs.afflictions;
        var petrify = afflictions.GetCurrentStatus(CharacterAfflictions.STATUSTYPE.Petrify);

        if (petrify <= 0.01f)
        {
            return;
        }

        var cap = afflictions.GetStatusCap(CharacterAfflictions.STATUSTYPE.Petrify);

        if (cap <= 0.01f)
        {
            return;
        }

        // 过半才开始泄：超出一半的那部分才是可以被转走的量，一半以内原样留着。
        var excess = petrify - (cap * StillLightConvertThreshold);

        if (excess <= 0.01f)
        {
            return;
        }

        var amount = Mathf.Min(excess, cap * StillLightConvertPerSecond * stacks * deltaTime);

        if (amount <= 0f)
        {
            return;
        }

        // 先填「额外体力」，填满之后才积累到体力上。
        if (data.extraStamina < character.GetMaxStamina())
        {
            character.AddExtraStamina(amount);
        }
        else
        {
            character.AddStamina(amount);
        }

        afflictions.SubtractStatus(CharacterAfflictions.STATUSTYPE.Petrify, amount, fromRPC: false, decreasedNaturally: true);

        // 石化值还有一份整数镜像（决定会不会被石化），攒够 1 点就把那份也压下去。
        var carry = state.GetTimer(StillLightTimerCarry) + amount;
        var whole = Mathf.Floor(carry);
        state.SetTimer(StillLightTimerCarry, carry - whole);

        if (whole >= 1f && data.petrifyAmount > 0)
        {
            data.SetPetrify(Mathf.Max(0, data.petrifyAmount - Mathf.RoundToInt(whole)));
        }
    }

    // ── 烫手 ─────────────────────────────────────────────────────
    /// <summary>第一次拾起「生的食物」就烤一次，交给补丁处理。</summary>
    private static void RegisterHotHands()
    {
        Register(new ActionHextech(
            HotHandsId,
            "烫手",
            "每当第一次拾起食物时，立刻把它烤熟（只对食物生效，每件只烤一次，已烹饪过的不再动）。",
            HextechQuality.Bronze));
    }

    // ── 士气大提升 ───────────────────────────────────────────────
    /// <summary>
    /// 只有「从点着的篝火旁边走出去」那一下才会触发；篝火烧完了不算离开。
    /// 篝火列表每 2 秒刷一次，避免每帧全场搜索。
    /// </summary>
    private static void RegisterMoraleBoost()
    {
        Register(new ActionHextech(
            MoraleBoostId,
            "士气大提升",
            "离开点着的篝火后，获得 16 秒的加速与无限体力。",
            HextechQuality.Gold,
            onTick: TickMoraleBoost,
            luggageOnly: true,
            drawback: new HextechDrawback(
                "篝火狂欢之后的疲软：饥饿积累速度 +15%。",
                (state, _) => state.Character.refs.afflictions.hungerPerSecond *= 1.15f)));
    }

    private static void TickMoraleBoost(HextechState state, int stacks, float deltaTime)
    {
        var character = state.Character;

        if (!character.IsLocal)
        {
            return;
        }

        var inside = IsInsideLitCampfire(character);
        var wasInside = state.GetTimer(MoraleTimerWasInside) > 0.5f;
        state.SetTimer(MoraleTimerWasInside, inside ? 1f : 0f);

        if (!wasInside || inside)
        {
            return;
        }

        AddAffliction(state, new Affliction_FasterBoi
        {
            totalTime = MoraleBoostSeconds,
            moveSpeedMod = 0.25f,
            climbSpeedMod = 0.3f,
        });

        AddAffliction(state, new Affliction_InfiniteStamina { totalTime = MoraleBoostSeconds });
    }

    /// <summary>
    /// 当前场景里的篝火列表（2 秒缓存 —— 找篝火要翻静态表或全场扫一遍，经不起每帧来一次）。
    /// 「篝火挂机惩罚」也吃这份缓存，所以别再自己扫一遍。
    /// </summary>
    internal static List<Campfire> Campfires()
    {
        if (_campfires == null || Time.unscaledTime > _campfireRefreshTime)
        {
            _campfireRefreshTime = Time.unscaledTime + 2f;
            _campfires = CollectCampfires();
        }

        return _campfires;
    }

    internal static bool IsInsideLitCampfire(Character character)
    {
        foreach (var campfire in Campfires())
        {
            if (campfire == null || !campfire.Lit)
            {
                continue;
            }

            var inside = campfire.PlayerCharactersInRadius(campfire.moraleBoostRadius);

            if (inside != null && inside.Contains(character))
            {
                return true;
            }
        }

        return false;
    }

    private static List<Campfire> CollectCampfires()
    {
        var field = AccessTools.Field(typeof(Campfire), "ALL_CAMPFIRES");

        if (field?.GetValue(null) is List<Campfire> list && list.Count > 0)
        {
            return list;
        }

        return new List<Campfire>(Object.FindObjectsByType<Campfire>(FindObjectsSortMode.None));
    }

    // ── 人人为我 ─────────────────────────────────────────────────
    /// <summary>队友死亡时补额外体力，由 Character.RPCA_Die 与石化侦察兵的 RPC 触发。</summary>
    private static void RegisterAllForOne()
    {
        Register(new ActionHextech(
            AllForOneId,
            "人人为我",
            "有队友死亡时，补充 60 点额外体力。",
            HextechQuality.Silver,
            stackable: true,
            maxStacks: 2));
    }

    // ── 讲价高手 ─────────────────────────────────────────────────
    /// <summary>
    /// 效果不落在角色属性上，而是被 <see cref="HextechShopCatalog"/> 在算价钱时读到。
    /// 商店的售价是「查价那一刻」算的，所以拿到词条之后马上回到商店就是半价。
    /// <para>
    /// 描述里不写 <c>{0}</c>：这个词条的折扣是固定的 50%，跟品质数值没关系。
    /// </para>
    /// </summary>
    private static void RegisterShopHalfPrice()
    {
        Register(new ActionHextech(
            ShopHalfPriceId,
            "讲价高手",
            "海克斯商店里所有商品价格减半。",
            HextechQuality.Silver));
    }

    // ── 百毒不侵 ─────────────────────────────────────────────────
    /// <summary>
    /// 孢子积累由 Harmony 前缀封顶在僵尸化阈值之下（见 <see cref="HextechPatches"/>），
    /// 代价是「身体跟着僵化」。
    /// </summary>
    private static void RegisterSporeImmunity()
    {
        Register(new ActionHextech(
            SporeImmunityId,
            "百毒不侵",
            "孢子积累得再高也不会让你变成僵尸。",
            HextechQuality.Gold,
            drawback: new HextechDrawback(
                "身体跟着僵化：地面移动速度 -10%。",
                (state, _) => state.Character.refs.movement.movementForce *= 0.9f)));
    }

    // ── 围炉谈心 ─────────────────────────────────────────────────
    /// <summary>
    /// 和队友挨在一起（两个身位内）时，每 10 秒恢复 1 点受伤值（伤势条 1%）。
    /// 不再要求站在篝火旁，「围炉」靠的是人挨人；人数不再分档，多一个人也不会更快。
    /// <para>2026-09-14 用户拍板：从「每 8 秒回 6% 体力」改成「每 10 秒回 1 点受伤值」——
    /// 回的是伤势不是体力，且必须真的挨在一起才结算。</para>
    /// </summary>
    private static void RegisterFiresideChat()
    {
        Register(new ActionHextech(
            FiresideChatId,
            "围炉谈心",
            "与队友挨在一起（两个身位内）时，每 10 秒恢复 1 点受伤值；中途有人走远就中断重算。",
            HextechQuality.Bronze,
            onTick: TickFiresideChat));
    }

    private static void TickFiresideChat(HextechState state, int stacks, float deltaTime)
    {
        var character = state.Character;

        if (!character.IsLocal)
        {
            return;
        }

        if (NearbyTeammateCount(character) <= 0)
        {
            // 人一散开就中断：没攒满的那几秒直接清零，重新挨到一起再从零开始数。
            state.SetTimer(FiresideTimerKey, 0f);
            return;
        }

        var afflictions = character.refs.afflictions;

        // 没受伤就没东西可回，计时也不往前走（别攒着等受伤了一次吐出来）。
        if (afflictions.GetCurrentStatus(CharacterAfflictions.STATUSTYPE.Injury) <= 0f)
        {
            state.SetTimer(FiresideTimerKey, 0f);
            return;
        }

        var timer = state.GetTimer(FiresideTimerKey) + deltaTime;

        if (timer < FiresideIntervalSeconds)
        {
            state.SetTimer(FiresideTimerKey, timer);
            return;
        }

        // 只扣掉一个周期、把余数留着，人一直贴着站也不会把多出来的那点吞掉。
        state.SetTimer(FiresideTimerKey, timer - FiresideIntervalSeconds);

        afflictions.SubtractStatus(CharacterAfflictions.STATUSTYPE.Injury, DefaultHextechs.StatusPoint);
        state.RecordEffect(FiresideChatId, DefaultHextechs.StatusPoint);
    }

    /// <summary>
    /// 身边还能站着的队友数量（不含自己，也不算已经死掉 / 完全昏迷的）——
    /// 「围炉谈心」只关心有没有人（> 0），人数再多也不会回得更快。
    /// </summary>
    internal static int NearbyTeammateCount(Character character)
    {
        var all = Character.AllCharacters;

        if (all == null)
        {
            return 0;
        }

        var origin = character.transform.position;
        var count = 0;

        for (var i = 0; i < all.Count; i++)
        {
            var other = all[i];

            if (other == null || other == character || other.data == null)
            {
                continue;
            }

            // 倒地的队友抱不了团，别把「守着尸体」也算成回体力。
            if (other.data.dead || other.data.fullyPassedOut)
            {
                continue;
            }

            var delta = other.transform.position - origin;
            delta.y = 0f;

            if (delta.sqrMagnitude <= FiresideGroupRadius * FiresideGroupRadius)
            {
                count++;
            }
        }

        return count;
    }

    // ── 收藏家 ───────────────────────────────────────────────────
    /// <summary>
    /// 效果不落在角色数值上，而是在 <see cref="HextechState.AccrueTokens"/> 里给代币积累加速，
    /// 所以这里只登记词条（2026-09-13 用户拍板：上限 2→1 —— 两层 ×2 的代币速度把商店经济冲垮了）。
    /// </summary>
    private static void RegisterCollector()
    {
        Register(new ActionHextech(
            CollectorId,
            "收藏家",
            "商店代币的积累速度 +50%。",
            HextechQuality.Silver,
            stackable: true,
            maxStacks: 1));
    }

    // ── 资本家 ───────────────────────────────────────────────────
    /// <summary>
    /// 和「收藏家」一样在 <see cref="HextechState.AccrueTokens"/> 里结算，区别是这条走绝对收入：
    /// 每层固定多 0.5 枚 / 分钟，不随配置里「每分钟发几枚」缩放，也不能用品质百分比表示，
    /// 所以说明里不写 <c>{0}</c>，「每层」两个字顶掉了叠加说明。
    /// </summary>
    private static void RegisterCapitalist()
    {
        Register(new ActionHextech(
            CapitalistId,
            "资本家",
            "每层每分钟额外获得 0.5 枚商店代币。",
            HextechQuality.Silver,
            stackable: true,
            maxStacks: 2));
    }

    // ── 抬轿人 ───────────────────────────────────────────────────
    /// <summary>
    /// 扛着倒地的队友时给自己加速。原版没有「扛人减速系数」这种能直接改的字段
    /// （扛人是抓着人形布娃娃的物理拖拽），所以等价地做成「扛人时加速」：
    /// 扛起 / 放下的那两帧各乘一次，乘了多少记在 state 的计时器里，
    /// 多拿一层时先把旧系数除掉再乘新的，不会叠出重复的倍数。
    /// </summary>
    private static void RegisterPalanquinBearer()
    {
        var quality = HextechQuality.Silver;

        Register(new ActionHextech(
            PalanquinBearerId,
            "抬轿人",
            "扛着倒地的队友时，你的移动速度 +{0}。",
            quality,
            onTick: (state, stacks, dt) => TickPalanquinBearer(state, stacks, dt, quality),
            stackable: true,
            maxStacks: 2));
    }

    private static void TickPalanquinBearer(
        HextechState state,
        int stacks,
        float deltaTime,
        HextechQuality quality)
    {
        var character = state.Character;

        if (!character.IsLocal)
        {
            return;
        }

        var movement = character.refs.movement;
        var data = character.data;

        if (movement == null || data == null)
        {
            return;
        }

        var want = data.IsCarryingCharacter ? quality.GainFactor(stacks) : 1f;
        var applied = state.GetTimer(PalanquinTimerApplied);

        if (applied <= 0.001f)
        {
            applied = 1f;
        }

        if (Mathf.Approximately(applied, want))
        {
            return;
        }

        movement.movementForce *= want / applied;
        state.SetTimer(PalanquinTimerApplied, want);
    }

    // ── 送葬人 ───────────────────────────────────────────────────
    /// <summary>
    /// 倒地事件写在 <see cref="HextechAdvancedPatches.Undertaker"/> 里，这里只登记词条。
    /// ⚠️ 没有冷却 —— 补丁里的 2 秒窗口只是「同一次倒地走两条 RPC」的去重，不是 CD。
    /// </summary>
    private static void RegisterUndertaker()
    {
        Register(new ActionHextech(
            UndertakerId,
            "送葬人",
            "附近 10 米内有队友倒地时，立刻回复 20 点体力并获得 4 秒加速。",
            HextechQuality.Silver));
    }

    // ── 骨质疏松 ─────────────────────────────────────────────────
    /// <summary>
    /// 拿到的瞬间就变成骷髅，走的是「骸骨之书」同一个入口 <c>CharacterData.SetSkeleton</c>
    /// —— 那个方法自己会发 PunRPC，所以队友那边看到的也是骷髅，不用我们自己同步。
    /// <para>
    /// 骷髅形态在原版里几乎全免疫：<c>CharacterAfflictions.AddStatus</c> 会被
    /// <c>StatusAffectsSkeleton</c> 拦住，只放行伤势 / 诅咒 / 蛛网 / 石化 / 捕蝇草。
    /// 也就是说诅咒是唯一还能攒的日常状态，所以这里先清掉已攒的诅咒，
    /// 再由 <see cref="HextechPatches"/> 的 AddStatus 前缀把之后的诅咒也封死在 0。
    /// </para>
    /// <para>
    /// ⚠ 放行的这几种里「伤势」是被原版硬编码放大 8 倍的（<c>AddStatus</c> 里
    /// <c>isSkeleton &amp;&amp; Injury → amount *= 8f</c>，见 <see cref="HextechPatches"/> 的 SkeletonInjuryMultiplier），
    /// 所以骷髅比人形脆得多 —— 这是原版代价，不能靠「免疫绝大多数状态」抵掉。
    /// </para>
    /// <para>
    /// 代价还有骷髅死亡不会留下骸骨、队友没法把你复活 —— 也是骷髅状态自带的，
    /// 所以没有再挂一条品质代价。
    /// </para>
    /// </summary>
    private static void RegisterOsteoporosis()
    {
        Register(new ActionHextech(
            OsteoporosisId,
            "骨质疏松",
            "立刻变成骷髅（同用掉骸骨之书）：免疫绝大部分负面积累、诅咒也不再积累；"
            + "代价是受到的伤势 ×8、死亡后不会留下骸骨，无法被复活。",
            HextechQuality.Legendary,
            (state, _) =>
            {
                var character = state.Character;

                if (character == null || character.refs == null
                    || character.refs.afflictions == null || character.data == null)
                {
                    return;
                }

                character.refs.afflictions.SetStatus(CharacterAfflictions.STATUSTYPE.Curse, 0f, true);
                character.data.SetSkeleton(true);
            },
            maxHoldersPerRun: 1));
    }

    // ── 死而复生 ─────────────────────────────────────────────────
    /// <summary>
    /// 传说档的「多一条命」：抽到就得到 1 次复活机会，
    /// 之后按 <see cref="ModConfig.ResurrectKey"/>（默认 N）把一名已经死掉 / 完全昏迷的队友
    /// 拉到自己面前，实际动作在 <see cref="Resurrection"/> 里，走的是原版自己的复活 RPC。
    /// <para>
    /// 说明里不写具体按键：说明文字是注册时定死的静态字符串（而且 <c>{0}</c> 已经被品质数值占了），
    /// 按键在 HUD 那行是运行时读的，改配置也不会显示过期值。
    /// </para>
    /// </summary>
    private static void RegisterResurrect()
    {
        Register(new ActionHextech(
            ResurrectId,
            "死而复生",
            "获得 1 次复活机会：队友死掉后按复活键，把他复活到你面前（每局 1 次，只能救一个人）。",
            HextechQuality.Legendary,
            (state, _) =>
            {
                state.GrantReviveCharge();
                HextechHud.Toast($"死而复生：获得 1 次复活机会 · 按 {ModConfig.ResurrectKey.Value} 复活队友");
            }));
    }

    // ── 手滑 ─────────────────────────────────────────────────────
    /// <summary>
    /// 没有 onAcquire：投掷时才会用到，效果在
    /// <see cref="HextechAdvancedPatches.Butterfingers"/> 里按层数现算。
    /// </summary>
    private static void RegisterButterfingers()
    {
        Register(new ActionHextech(
            ButterfingersId,
            "手滑",
            "投掷力度 -30%（蓄力按比例打折，蓄满力也只有七成）。",
            HextechQuality.Bronze,
            stackable: true,
            maxStacks: 2,
            negative: true));
    }

    // ── 夜盲 ─────────────────────────────────────────────────────
    /// <summary>
    /// 用游戏自带的「失明」状态（和黑布条同款），隔一阵糊一次屏幕。
    /// 层数越高间隔越短：一层 90 秒一次，两层 45 秒一次。
    /// </summary>
    private static void RegisterNightBlind()
    {
        Register(new ActionHextech(
            NightBlindId,
            "夜盲",
            "每 90 秒短暂失明 4 秒（每多一层，间隔减半）。",
            HextechQuality.Bronze,
            onTick: TickNightBlind,
            stackable: true,
            maxStacks: 2,
            negative: true));
    }

    private static void TickNightBlind(HextechState state, int stacks, float deltaTime)
    {
        var character = state.Character;

        if (!character.IsLocal || character.refs == null)
        {
            return;
        }

        var data = character.data;

        if (data == null || data.dead)
        {
            return;
        }

        var interval = NightBlindIntervalSeconds / Mathf.Max(1, stacks);
        var timer = state.GetTimer(NightBlindTimerKey) + deltaTime;

        if (timer < interval)
        {
            state.SetTimer(NightBlindTimerKey, timer);
            return;
        }

        // 只扣掉一个周期、余数留着，长局里不会越跑越偏。
        state.SetTimer(NightBlindTimerKey, timer - interval);
        AddAffliction(state, new Affliction_Blind { totalTime = NightBlindDurationSeconds });
    }

    // ── 拖后腿 ───────────────────────────────────────────────────
    /// <summary>
    /// 「领头羊」的反面，而且是唯一一条「效果长在别人身上」的词条：
    /// 持有者不需要做任何事，判定与加成都在
    /// <see cref="HextechAdvancedPatches.Deadweight"/> 里，由队友自己的客户端结算。
    /// </summary>
    private static void RegisterDeadweight()
    {
        Register(new ActionHextech(
            DeadweightId,
            "拖后腿",
            "你 6 米内的队友，体力消耗 +12%（每层再乘 1.12）。",
            HextechQuality.Bronze,
            stackable: true,
            maxStacks: 2,
            negative: true));
    }

    // ── 辅助 ─────────────────────────────────────────────────────
    private static void Register(HextechEntry entry)
    {
        HextechRegistry.Register(entry);
    }

    private static void AddAffliction(HextechState state, Affliction affliction)
    {
        state.TrackAffliction(affliction);
        state.Character.refs.afflictions.AddAffliction(affliction);
    }
}
