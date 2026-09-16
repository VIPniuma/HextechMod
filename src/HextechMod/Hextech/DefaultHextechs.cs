using System;
using Peak.Afflictions;
using UnityEngine;

namespace PeakModder.HextechMod;

/// <summary>
/// 初始词条表。所有效果都只作用于持有者自己的角色，
/// 由玩家自己的客户端应用（游戏自带的 AddAffliction 会再同步给其他客户端）。
/// <para>
/// 加成数值由品质决定：青铜 8% / 白银 12% / 黄金 16%（<see cref="HextechQualityMath"/>，2026-09-13 全量削弱）。
/// 说明里写 <c>{0}</c> 的词条，数字会按品质自动填进去，面板上还会按当前层数显示累计值 ——
/// 重复获得同一条时是乘法叠加（12% 后再得 12% → 13.44%），不是再加一遍品质数值。
/// </para>
/// <para>
/// 定级只看「这条词条能把一局游戏变轻松多少」，不看实现复杂度：
/// 青铜 = 单属性小加成、一次性小补给、情境型机制；
/// 白银 = 核心资源（耐力 / 伤势 / 寒冷 / 负面状态）削减、有时限的强 buff、主动技能解锁；
/// 黄金 = 改变玩法的永久机制（免疫、额外跳跃、低重力、无敌），一律附带一条
/// <see cref="HextechDrawback"/>「代价」来平衡。
/// </para>
/// <para>
/// 不走品质表的只有三类：固定值/持续时间的词条、镜像成对的「状态积累」词条
/// （<see cref="ToughBodyId"/> 与 <see cref="GlassyId"/> 数值必须一正一负对应）、
/// 以及代价型负面词条（它们的数值是「代价」，故意比品质数值重）。
/// </para>
/// </summary>
internal static class DefaultHextechs
{
    public const string ToughBodyId = "tough_body";
    public const string GlassyId = "glassy";
    public const string LuckyLuggageId = "lucky_luggage";
    public const string SnackId = "snack";
    public const string TarzanId = "tarzan";
    public const string ThickHideId = "thick_hide";
    public const string AntidoteBodyId = "antidote_body";
    public const string HeatShieldId = "heat_shield";
    public const string CurseBreakerId = "curse_breaker";
    public const string ColdBloodedId = "cold_blooded";
    public const string ScavengerId = "scavenger";
    public const string ThrowMasterId = "throw_master";
    public const string TumblerId = "tumbler";
    public const string PetrifyWardId = "petrify_ward";
    public const string GamblerId = "gambler";
    public const string PhoenixId = "phoenix";
    public const string HangedManId = "hanged_man";
    public const string FeatherFallId = "feather_fall";
    public const string SpiderManId = "spiderman";
    public const string RefinedStomachId = "refined_stomach";

    /// <summary>
    /// 羽落：每层把「摔落造成的伤势」乘以该系数（0.4 = 摔伤减 60%，「免疫部分摔落伤害」）。
    /// 判定写在 <see cref="HextechPatches"/> 的 AddStatus 前缀里，每次摔落都生效 ——
    /// 原版 <c>CharacterMovement.CapFallDamage</c> 是「一次性窗口」（爆炸击飞专用），拿来当持续减免只会护住第一次。
    /// 2026-09-14 用户拍板：从 0.02（-98%，几乎完全免疫）削到 0.4（-60%）。
    /// </summary>
    public static float FeatherFallFactorPerStack = 0.4f;

    /// <summary>长跑运动员：每层把冲刺耐力消耗乘以该系数。</summary>
    public static float MarathonerSprintFactorPerStack = 0.8f;

    /// <summary>投掷专家：每层把投掷蓄力（也就是力度）乘以该系数。</summary>
    public static float ThrowMasterFactorPerStack = 1.2f;

    /// <summary>不倒翁：每层把摔倒 / 被击飞后的硬直时间乘以该系数。</summary>
    public static float TumblerStunFactorPerStack = 0.65f;

    /// <summary>
    /// 不死鸟：摔落时至少给留这么多伤势量（20 点，见 <see cref="StatusPoint"/>），
    /// 也就是「摔得再狠也给你留 20 点伤势」—— 且**不限次数**（2026-09-14 用户拍板，
    /// 原来是「每局 1 次、留 1 点」）。
    /// </summary>
    public static float PhoenixMinInjuryLeft = 20f * StatusPoint;

    /// <summary>
    /// 倒吊人：抽到后把伤势填到「上限的 <c>0.99</c>」——
    /// 只留 1% 的余量到倒地线，所以不会当场昏迷，但一点伤就会倒。
    /// </summary>
    public static float HangedManInjuryRatio = 0.99f;

    /// <summary>
    /// 机能零食：每件物品把「每种负面状态」按各自当前值削掉的**单层**比例。
    /// 是相对削减（0.05 = 每种各 -5%，条越长扣得越多），不是扣整条状态条的 5%。
    /// </summary>
    public static float SnackReduceRatioPerStack = 0.05f;

    /// <summary>
    /// 机能零食叠了 <paramref name="stacks"/> 层时的累计削减比例 —— 与品质词条同一套乘法口径：
    /// 5% 后再得 5% → 5% + 5%×5% = 5.25%，即 0.05 × 1.05^(n-1)。
    /// （2026-09-13 之前是每层线性再叠一份 5%，三层 15% 偏超标。）
    /// </summary>
    public static float SnackTotalRatio(int stacks)
    {
        return stacks <= 0
            ? 0f
            : SnackReduceRatioPerStack * Mathf.Pow(1f + SnackReduceRatioPerStack, stacks - 1);
    }

    /// <summary>
    /// 说明里「一点」的口径：状态条内部值的 1%。
    /// <para>
    /// 饥饿 / 伤势 / 寒冷这些负面状态在游戏里都是 0~1 上下的小数（HUD 满条 ≈ 1），
    /// 所以「1 点」= <c>0.01</c>（游戏自带测试指令 <c>AddHunger()</c> 的 <c>+0.2</c> 就是 20 点）。
    /// 「每 N 秒消一点」的词条（饱腹 / 自我修复 / 温暖之躯）都用它，层数直接乘上去。
    /// </para>
    /// </summary>
    public const float StatusPoint = 0.01f;

    /// <summary>
    /// 冷血体质：每层把受到的灼热（晒伤）积累乘以该系数 —— 这条负面词条捎带的半个好处。
    /// 实际削减由 <see cref="HextechPatches"/> 在 <c>CharacterAfflictions.AddStatus</c>
    /// 的前缀里按层数取幂（和四条专项抗性走同一套）。
    /// </summary>
    public static float ColdBloodedHeatResistPerStack = 0.5f;

    /// <summary>
    /// 专项状态抗性：每层把该状态的积累乘以这个系数（固定值，不走品质表）。
    /// 只管一种状态，所以比通用版「坚韧之躯」的 25% 重一档。
    /// </summary>
    public static float ThickHidePerStack = 0.75f;

    public static float AntidoteBodyPerStack = 0.75f;
    public static float HeatShieldPerStack = 0.8f;
    public static float CurseBreakerPerStack = 0.8f;

    private const string BreatheTimerKey = "breathe_timer";

    /// <summary>吐纳：每隔多少秒回一次体力。</summary>
    private const float BreatheInterval = 1f;

    /// <summary>
    /// 吐纳：每层、每秒恢复的体力。**体力是 0~1 的小数**（<c>Character.AddStamina</c> 加完会
    /// <c>ClampStamina</c> 夹回 1），所以 0.01 = 满条的 1%。
    /// <para>
    /// 以前这里写的是 2f：加完直接夹回 1，等于每秒把体力条灌满（挂在墙上永远不掉体力）。
    /// 2026-09-12 按玩家反馈削到「每秒 1%」。
    /// </para>
    /// </summary>
    private const float BreatheStaminaPerStack = 0.01f;

    private const string LightBodyTimerKey = "light_body_check";

    /// <summary>空灵之体：每隔多少秒自查一次低重力还在不在（别人的浮空道具会把它顶掉）。</summary>
    private const float LightBodyCheckInterval = 1f;

    /// <summary>空灵之体：低重力的强度（游戏自带低重力 1 档）。</summary>
    private const int LightBodyAmount = 1;

    /// <summary>空灵之体：低重力的时长，够长就等于永久。</summary>
    private const float LightBodyDuration = 999999f;

    /// <summary>
    /// 石化抗性：受到的石化值积累按这个比例削减（1/3：本该 +30 只会 +20）。
    /// <para>
    /// 2026-09-13 重做（用户定调）：原来的「外界石化值一点都攒不上来 + 自己每分钟涨 1%」
    /// 等于让所有「副作用是加石化值」的东西全部白给（机械手的 +5 代价、爬石化石壁、护符……），
    /// 太超标。现在改成按比例削减 —— 石化压力还在，只是慢三分之一；
    /// 机械手那 5 点代价会真的掉 3 点血（1/3 削减后拦 1~2 点）。
    /// </para>
    /// <para>
    /// 游戏里石化值的权威口径是 <c>CharacterData.petrifyAmount</c>（<c>int</c>，0~100，100 = 被石化）。
    /// 削减点在 <see cref="HextechPatches.PetrifyWard"/>（<c>AddPetrify(int)</c> 前缀）——
    /// 状态条路 <c>AddStatus(Petrify)</c> 内部最后也走 AddPetrify，所以两条来路都在这一个点被削减，
    /// 不会双重打折。小数「省下的点数」进 <see cref="PetrifyWardBankKey"/> 攒整，长期期望正好 -1/3。
    /// </para>
    /// </summary>
    public static float PetrifyWardReduction = 1f / 3f;

    /// <summary>石化抗性：攒「省下来的石化点数」的小数余额（计时器不会自己走，在补丁里读写）。</summary>
    internal const string PetrifyWardBankKey = "petrify_ward_bank";

    public static void Register()
    {
        RegisterBronze();
        RegisterSilver();
        RegisterGold();
        RegisterLegendary();

        // 只削减一种状态的专抗，和「坚韧之躯」共用同一套 AddStatus 前缀。
        RegisterStatusResist();
        RegisterNegative();

        // 需要盯游戏事件的特殊机制词条（领头羊 / 拉拉手 / 厨师 / 背包客 / 我还有光 …）
        AdvancedHextechs.Register();
    }

    // ── 青铜（8%）───────────────────────────────────────────────
    /// <summary>单属性小加成与一次性小补给：抽到不会兴奋，但永远不亏。</summary>
    private static void RegisterBronze()
    {
        var quality = HextechQuality.Bronze;

        Register(new ActionHextech(
            "strong_legs",
            "强健双腿",
            "跳跃高度 +{0}。",
            quality,
            (state, stacks) => state.Character.refs.movement.jumpImpulse *= quality.GainFactor(stacks),
            stackable: true,
            maxStacks: 3));

        Register(new ActionHextech(
            "sprinter",
            "疾跑者",
            "冲刺速度 +{0}，冲刺耐力消耗 -{0}。",
            quality,
            (state, stacks) =>
            {
                state.Character.refs.movement.sprintMultiplier *= quality.GainFactor(stacks);
                state.Character.refs.movement.sprintStaminaUsage *= quality.ReduceFactor(stacks);
            },
            stackable: true,
            maxStacks: 3));

        Register(new ActionHextech(
            "swift",
            "轻装疾行",
            "地面移动速度 +{0}。",
            quality,
            (state, stacks) => state.Character.refs.movement.movementForce *= quality.GainFactor(stacks),
            stackable: true,
            maxStacks: 3));

        Register(new ActionHextech(
            "thrifty",
            "节能食客",
            "饥饿积累速度 -{0}。",
            quality,
            (state, stacks) => state.Character.refs.afflictions.hungerPerSecond *= quality.ReduceFactor(stacks)));

        // 「拾荒者」：每件物品第一次被拾起时给 0.3 枚代币（小数累计）。
        // 2026-09-16 从 +1 削弱到 +0.3：PEAK 里散落物品极多，+1/件等于白嫖几十枚、商店变免费。
        // 去重和「烫手 / 机能零食」共用 TryMarkPickupSettled（按物品实例记，
        // 丢了再捡起来不会重复给）。不设每局上限（2026-09-12 按玩家要求去掉原来的 20 枚上限）。
        Register(new ActionHextech(
            ScavengerId,
            "拾荒者",
            "每件物品第一次被你拾起时 +0.3 枚商店代币（小数累计、不设上限，捡多少给多少；丢了再捡不会重复给）。",
            quality));

        // 每件物品第一次被拾起时吃一口：身上「每种负面状态」各按自己当前值 -5%。
        // 是相对削减，条越长扣得越多、永远扣不到 0；同一件物品拿进拿出、丢了再捡都不会再来一次。
        // 叠层走乘法口径（5% → 5.25% → 5.51%），见 SnackTotalRatio —— 2026-09-13 从「每层再叠一份」的线性叠加改过来。
        Register(new ActionHextech(
            SnackId,
            "机能零食",
            "每件物品第一次被你拾起时，身上每种负面状态各减少当前值的 5%（每多一层按乘法再叠一点，同一件物品不会重复生效）。",
            quality,
            stackable: true,
            maxStacks: 3));

        Register(new ActionHextech(
            "climber",
            "攀爬健将",
            "攀爬速度 +{0}。",
            quality,
            (state, stacks) => state.Character.refs.climbing.climbSpeed *= quality.GainFactor(stacks),
            stackable: true,
            maxStacks: 3));

        Register(new ActionHextech(
            "iron_stomach",
            "铁胃",
            "中毒消退速度 +{0}。",
            quality,
            (state, stacks) => state.Character.refs.afflictions.poisonReductionPerSecond *= quality.GainFactor(stacks),
            stackable: true,
            maxStacks: 3));
    }

    // ── 白银（12%）───────────────────────────────────────────────
    /// <summary>
    /// 核心资源削减、有时限的强 buff、主动技能解锁 —— 一局里能实打实省事的那一档。
    /// 纯收益但不改变玩法规则的机制（幸运旅客、厨师之类）也留在这一档。
    /// </summary>
    private static void RegisterSilver()
    {
        var quality = HextechQuality.Silver;

        // 从青铜升上来：攀爬耐力是这游戏最硬的资源，-12% 已经不是「小加成」了。
        Register(new ActionHextech(
            "stamina_reserve",
            "耐力储备",
            "攀爬耐力消耗 -{0}。",
            quality,
            (state, stacks) => state.Character.refs.climbing.maxStaminaUsage *= quality.ReduceFactor(stacks),
            stackable: true,
            maxStacks: 3));

        Register(new ActionHextech(
            "moon_gravity",
            "月面重力",
            "最大下落速度 -{0}，滞空时间更长。",
            quality,
            (state, stacks) => state.Character.refs.movement.maxGravity *= quality.ReduceFactor(stacks),
            stackable: true,
            maxStacks: 2));

        Register(new ActionHextech(
            "recovery",
            "自我修复",
            "每 15 秒自动恢复 1 点伤势。",
            quality,
            onTick: (state, stacks, dt) => TickStatus(
                state, "recovery", "recovery", dt, 15f, CharacterAfflictions.STATUSTYPE.Injury, StatusPoint * stacks),
            stackable: true,
            maxStacks: 3));

        Register(new ActionHextech(
            "warm_body",
            "温暖之躯",
            "每 5 秒自动驱散 1 点寒冷。",
            quality,
            onTick: (state, stacks, dt) => TickStatus(
                state, "warm_body", "warm_body", dt, 5f, CharacterAfflictions.STATUSTYPE.Cold, StatusPoint * stacks),
            stackable: true,
            maxStacks: 3));

        // 从青铜升上来：从「每 2 秒削一点」改成「每 60 秒削 1 点」。
        // 间隔拉长到整分钟是为了手感 —— 饥饿条本来就涨得慢，一分钟结算一次足够压住，
        // 也不会再出现条子一直在动的画面。
        Register(new ActionHextech(
            "full_belly",
            "饱腹",
            "每 60 秒自动缓解 1 点饥饿。",
            quality,
            onTick: (state, stacks, dt) => TickStatus(
                state,
                "full_belly",
                "full_belly",
                dt,
                60f,
                CharacterAfflictions.STATUSTYPE.Hunger,
                StatusPoint * stacks),
            stackable: true,
            maxStacks: 3));

        // 精致胃袋（2026-09-14 用户加的）：吃法规则类词条，结算在 HextechAdvancedPatches.RefinedStomach ——
        // 野生（浆果 / 蘑菇标签）效果减半，包装（袋装食品标签：棉花糖 / 烤肠 / 坚果等）效果翻倍；
        // 运动饮料 / 能量饮料 / 棒棒糖按物品名排除。不可叠加 —— 规则类词条，层数没有意义。
        Register(new ActionHextech(
            RefinedStomachId,
            "精致胃袋",
            "吃野生食物（浆果、蘑菇等）效果减半；吃包装食物（棉花糖、烤肠、坚果等）效果翻倍。"
            + "运动饮料、能量饮料和棒棒糖不受影响。",
            HextechQuality.Silver));

        // 「挣脱」（每 2 秒自动挣脱蛛网与捕蝇草）已于 2026-09-14 按用户要求删除：
        // 游戏里实际不可用（蛛网/捕蝇草的挣脱不走这条状态削减路径），词条等于白板。

        // 从「3 分钟免晒伤」改成永久：灼热（晒伤）自己退得更快。
        Register(new ActionHextech(
            "sunscreen",
            "防晒霜",
            "晒伤（灼热）消退速度 +{0}。",
            quality,
            (state, stacks) => state.Character.refs.afflictions.hotReductionPerSecond *= quality.GainFactor(stacks),
            stackable: true,
            maxStacks: 3));

        // 从「45 秒 buff」改成永久：直接常驻小幅加速。
        // 2026-09-13 用户拍板：上限 3→2（移速词条叠乘太猛）。
        Register(new ActionHextech(
            "energy_drink",
            "能量饮料",
            "移动速度 +{0}，攀爬速度 +{0}。",
            quality,
            (state, stacks) =>
            {
                var factor = quality.GainFactor(stacks);
                state.Character.refs.movement.movementForce *= factor;
                state.Character.refs.climbing.climbSpeed *= factor;
            },
            stackable: true,
            maxStacks: 2));

        // 「一次性爆发」全部丢进行李箱抽奖（luggageOnly）：三选一与商店只给永久收益。
        // 2026-09-13 用户拍板：五条一次性 buff 全体微削（30/25/80/15/20 → 24/20/64/12/16）。
        Register(new ActionHextech(
            "sugar_rush",
            "甜食冲刺",
            "立即获得 24 秒无限耐力。",
            quality,
            (state, _) => AddAffliction(state, new Affliction_InfiniteStamina(24f)),
            luggageOnly: true));

        Register(new ActionHextech(
            "guardian_shield",
            "守护者护盾",
            "获得 20 秒的护盾保护。",
            quality,
            (state, _) => AddAffliction(state, new Affliction_BingBongShield { totalTime = 20f }),
            luggageOnly: true));

        Register(new ActionHextech(
            "full_heal",
            "全能治疗",
            "立即恢复最多 64 点负面积累。",
            quality,
            (state, _) => AddAffliction(state, new Affliction_HealAll { maxHealing = 64f }),
            luggageOnly: true));

        // 从黄金降下来：和「耐力储备」是同类效果，只是多一条力竭续攀。
        Register(new ActionHextech(
            "stamina_overflow",
            "极限攀爬",
            "攀爬耐力消耗 -{0}，力竭后还能多攀 {0} 的时间。",
            quality,
            (state, stacks) =>
            {
                state.Character.refs.climbing.maxStaminaUsage *= quality.ReduceFactor(stacks);
                state.Character.refs.climbing.maxOutOfStamClimbTimeOnStartClimb *= quality.GainFactor(stacks);
            },
            stackable: true,
            maxStacks: 3));

        // 只在丛林藤蔓上有用：情境型机制，离了藤蔓完全没用，
        // 所以数值（-25% / +30%）虽然比白银档重，定级仍然留在白银，而且不走品质表。
        Register(new ActionHextech(
            TarzanId,
            "人猿泰山",
            "在藤蔓上体力消耗 -25%，藤蔓攀爬速度 +30%。",
            quality,
            (state, _) =>
            {
                var vine = state.Character.refs.vineClimbing;

                if (vine == null)
                {
                    return;
                }

                vine.staminaUsage *= 0.75f;
                vine.climbSpeed *= 1.3f;
            }));

        // 与「玻璃体质」是一对镜像词条，数值必须一正一负对应，所以不走品质表。
        Register(new ActionHextech(
            ToughBodyId,
            "坚韧之躯",
            "受到的负面状态积累 -25%。",
            quality,
            stackable: true,
            maxStacks: 2));

        Register(new ActionHextech(
            LuckyLuggageId,
            "幸运旅客",
            "你打开的行李箱每层多抽 1 次奖。",
            quality,
            stackable: true,
            maxStacks: 3));

        // 三条新的永久收益：负面状态退得更快、慢慢回体力。
        Register(new ActionHextech(
            "thick_skin",
            "硬皮",
            "孢子与尖刺的消退速度 +{0}。",
            quality,
            (state, stacks) =>
            {
                var factor = quality.GainFactor(stacks);
                state.Character.refs.afflictions.sporesReductionPerSecond *= factor;
                state.Character.refs.afflictions.thornsReductionPerSecond *= factor;
            },
            stackable: true,
            maxStacks: 2));

        Register(new ActionHextech(
            "clear_mind",
            "清醒",
            "每 2 秒自动驱散一点困倦。",
            quality,
            onTick: (state, stacks, dt) => TickStatus(
                state, "clear_mind", "clear_mind", dt, 2f, CharacterAfflictions.STATUSTYPE.Drowsy, StatusPoint * stacks),
            stackable: true,
            maxStacks: 2));

        // 原来是青铜的「热血体质」（-10%、不可叠）：和「温室体质」是同一件事，
        // 两条几乎一样的词条留在池子里只会互相稀释，所以合并成这一条，
        // id 保持不变（老存档里存的就是这个 id，抽到过的人只会发现它变强了）。
        Register(new ActionHextech(
            "warm_blood",
            "温室体质",
            "夜间寒冷积累速度 -{0}。",
            quality,
            (state, stacks) => state.Character.refs.afflictions.nightColdPerSecond *= quality.ReduceFactor(stacks),
            stackable: true,
            maxStacks: 3));

        // 与负面词条「手滑」正好是一对：一个把投掷蓄力打折，一个把蓄力放大。
        // 和手滑一样改的是 DropItemRpc 收到的蓄力参数，见
        // HextechAdvancedPatches.ThrowForce（同一个补丁同时管这两条）。
        Register(new ActionHextech(
            ThrowMasterId,
            "投掷专家",
            $"投掷力度 +{(ThrowMasterFactorPerStack - 1f) * 100f:0}%（每层再乘 {ThrowMasterFactorPerStack}）。",
            quality,
            stackable: true,
            maxStacks: 2));

        // 「疾跑者」也省冲刺耐力，但那条是「提速 + 省力」各占一半；
        // 这条只管省力、省得更狠，专门给爱跑的人。
        Register(new ActionHextech(
            "marathoner",
            "长跑运动员",
            $"冲刺耐力消耗 -{(1f - MarathonerSprintFactorPerStack) * 100f:0}%（每层再乘 {MarathonerSprintFactorPerStack}）。",
            quality,
            (state, stacks) => state.Character.refs.movement.sprintStaminaUsage *=
                Mathf.Pow(MarathonerSprintFactorPerStack, stacks),
            stackable: true,
            maxStacks: 2));

        // 摔懵 / 被击飞的硬直时间由 Character::Fall 的 seconds 决定（写进 data.fallSeconds），
        // 这里只登记词条，改值在 HextechAdvancedPatches.Tumbler 里 —— 所以要能叠。
        Register(new ActionHextech(
            TumblerId,
            "不倒翁",
            $"摔倒、被击飞后的硬直时间 -{(1f - TumblerStunFactorPerStack) * 100f:0}%（每层再乘 {TumblerStunFactorPerStack}）。",
            quality,
            stackable: true,
            maxStacks: 2));

        Register(new ActionHextech(
            "breathe",
            "吐纳",
            "每秒恢复 1% 体力（每层再叠 1%，最多 2 层）；攀爬时不恢复。",
            quality,
            onTick: (state, stacks, dt) =>
            {
                // 攀爬中不回体力（2026-09-15 用户要求）：不然挂在墙上也能一直回体力，
                // 等于把「攀爬耐力」这条最硬的资源白送，攀爬类词条（耐力储备 / 极限攀爬）也失去意义。
                // 判定沿用「蜘蛛侠」那套（HextechPatches.SpiderManPatch）：
                // 墙上 / 绳梯 / 藤蔓（isClimbingAnything），或抓着边缘 / 钉子的把手（currentClimbHandle）。
                // 放在计时之前 return：攀爬期间连计时都不走，松手后要重新攒满 1 秒才回，不会「存着一次性补」。
                var data = state.Character.data;

                if (data != null && (data.isClimbingAnything || data.currentClimbHandle != null))
                {
                    return;
                }

                var timer = state.GetTimer(BreatheTimerKey) + dt;

                if (timer < BreatheInterval)
                {
                    state.SetTimer(BreatheTimerKey, timer);
                    return;
                }

                state.SetTimer(BreatheTimerKey, 0f);
                state.Character.AddStamina(BreatheStaminaPerStack * stacks);
                state.RecordEffect("breathe", BreatheStaminaPerStack * stacks);
            },
            stackable: true,
            maxStacks: 2));

        // 技能本质是「进入低重力漂浮、朝视线方向轻盈飘起」，不是击飞/弹射（原名叫「海克斯：跳跃」）。
        // 技能词条的说明统一带上「技能介绍 + 效果 + 能量 / 冷却」（2026-09-14 用户要求），
        // 全部插值引用 SkillRegistry 的静态字段 —— 服务器平衡配置改了数值，这里自动跟上。
        RegisterSkillUnlock("unlock_super_jump", "海克斯：漂浮",
            $"解锁主动技能「海克斯漂浮」：短暂进入低重力漂浮状态，朝视线方向轻盈飘起；"
            + $"代价是使用后立刻 +{SkillRegistry.SuperJumpHungerCost:0} 点饥饿值"
            + $"（{SkillRegistry.SuperJumpEnergy:0} 能量 · {SkillRegistry.SuperJumpCooldown:0} 秒冷却）。",
            quality, SkillId.SuperJump);
        RegisterSkillUnlock("unlock_dash", "海克斯：疾风步",
            $"解锁主动技能「疾风步」：{SkillRegistry.SprintDurationSeconds:0} 秒内耐力无限，奔跑与攀爬不再耗体力；"
            + $"用完立刻涨一截饥饿（{SkillRegistry.SprintEnergy:0} 能量 · {SkillRegistry.SprintCooldown:0} 秒冷却）。",
            quality, SkillId.Sprint);
        RegisterSkillUnlock("unlock_cleanse", "海克斯：净化",
            $"解锁主动技能「净化」：清除自身每个负面状态当前值的 {SkillRegistry.CleansePercentPerStatus * 100f:0.#}%（寒冷额外清除 {SkillRegistry.CleanseColdPercent * 100f:0.#}%）；"
            + $"实际消除多少就转成多少饥饿值（{SkillRegistry.CleanseEnergy:0} 能量 · {SkillRegistry.CleanseCooldown:0} 秒冷却）。",
            quality, SkillId.Cleanse);
        RegisterSkillUnlock("unlock_heal", "海克斯：治疗波",
            $"解锁主动技能「治疗波」：立即恢复 {SkillRegistry.HealInjuryPoints:0} 点受伤值（只回伤势，不治疗其他负面状态）；"
            + $"代价是立刻 +{SkillRegistry.HealPetrifyCostPoints:0} 点石化值"
            + $"（{SkillRegistry.HealEnergy:0} 能量 · {SkillRegistry.HealCooldown:0} 秒冷却）。",
            quality, SkillId.Heal);
        RegisterSkillUnlock("unlock_adrenaline", "海克斯：肾上腺素",
            $"解锁主动技能「肾上腺素」：{SkillRegistry.AdrenalineDurationSeconds:0} 秒内大幅提升移动与攀爬速度，且不会犯困；"
            + $"代价是使用后立刻透支 {SkillRegistry.AdrenalineHungerCost:0} 点饥饿值"
            + $"（{SkillRegistry.AdrenalineEnergy:0} 能量 · {SkillRegistry.AdrenalineCooldown:0} 秒冷却）。",
            quality, SkillId.Adrenaline);
    }

    // ── 黄金（20%，一律带「代价」）────────────────────────────────
    /// <summary>
    /// 改变玩法的永久机制。为了不让它们变成无脑最优解，每条都挂一条
    /// <see cref="HextechDrawback"/>：数值故意比品质数值小，但拿一层就要还一次。
    /// </summary>
    private static void RegisterGold()
    {
        var quality = HextechQuality.Gold;

        // 从白银升上来：摔落伤害几乎免疫直接抹掉了这游戏最大的一条死因。
        // 没有 onAcquired：减免由 HextechPatches.AddStatus 前缀在每次摔落时乘
        // FeatherFallFactorPerStack 完成（原版的 CapFallDamage 是一次性窗口，撑不住一整局）。
        // 2026-09-14 用户拍板：从 -98%（几乎完全免疫）削到 -60%（落地伤害只剩 40%）。
        Register(new ActionHextech(
            FeatherFallId,
            "羽落",
            "摔落造成的伤势 -60%（落地伤害只剩 40%）。",
            quality,
            stackable: false,
            maxStacks: 1,
            drawback: Cost(
                "落地太轻，蹬地也使不上劲：地面移动速度 -8%。",
                state => state.Character.refs.movement.movementForce *= 0.92f)));

        // 从青铜升上来：多一次空中跳跃是这游戏最值钱的位移手段。
        // 2026-09-13 用户拍板：改成不可叠加 —— 只给一次额外空中跳（原来可叠 3 层 = 3 次额外跳，位移超标）。
        Register(new ActionHextech(
            "extra_jump",
            "二段跳",
            "获得一次额外的空中跳跃（不可叠加）。",
            quality,
            (state, _) => AddAffliction(state, new Affliction_DoubleJumpAmulet()),
            drawback: Cost(
                "空中全靠手臂硬拉：攀爬耐力消耗 +12%。",
                state => state.Character.refs.climbing.maxStaminaUsage *= 1.12f)));

        Register(new ActionHextech(
            "spring_legs",
            "弹簧腿",
            "跳跃高度 +{0}，并额外获得一次空中跳跃。",
            quality,
            (state, stacks) =>
            {
                state.Character.refs.movement.jumpImpulse *= quality.GainFactor(stacks);
                AddAffliction(state, new Affliction_DoubleJumpAmulet());
            },
            drawback: Cost(
                "起跳那一下会抽走冲刺的力气：冲刺耐力消耗 +15%。",
                state => state.Character.refs.movement.sprintStaminaUsage *= 1.15f)));

        // 空灵之体：低重力必须能自我修复 —— 别的浮空道具会走游戏自己的 Stack，
        // 把这条状态的时长顶成**它自己的**时长，等它走完整条状态就被移除，我们原来挂的那次是唯一的，
        // 丢了就再也回不来（玩家反馈的「被其他给浮空的道具刷掉」）。所以除了第一次挂上去，
        // 还要每秒自查一次，见 EnsureLightBody 的注释。
        Register(new ActionHextech(
            "light_body",
            "空灵之体",
            "永久处于低重力状态。",
            quality,
            (state, _) =>
            {
                // 回机场清空本局成长时要按类型摘掉它，记一份就够（游戏自己会 Copy 一份进它的表）。
                state.TrackAffliction(LightBodyAffliction);
                EnsureLightBody(state);
            },
            onTick: TickLightBody,
            drawback: Cost(
                "身体太轻，蹬地也没劲：跳跃高度 -8%。",
                state => state.Character.refs.movement.jumpImpulse *= 0.92f)));

        Register(new ActionHextech(
            "second_wind",
            "回光返照",
            "12 秒内免疫一切伤害与负面状态。",
            quality,
            (state, _) => AddAffliction(state, new Affliction_Invincibility
            {
                totalTime = 12f,
                isFromMilk = true,
            }),
            luggageOnly: true,
            drawback: Cost(
                "烧的是自己的储备：饥饿积累速度 +20%。",
                state => state.Character.refs.afflictions.hungerPerSecond *= 1.2f)));

        RegisterSkillUnlock(
            "unlock_invincible",
            "海克斯：无敌",
            $"解锁主动技能「无敌」：{SkillRegistry.InvincibleDurationSeconds:0} 秒内免疫一切伤害与负面状态；"
            + $"代价是使用时立刻 +{SkillRegistry.InvinciblePetrifyCost:0} 点石化值"
            + $"（{SkillRegistry.InvincibleEnergy:0} 能量 · {SkillRegistry.InvincibleCooldown:0} 秒冷却）。",
            quality,
            SkillId.Invincible,
            Cost(
                "技能同样吃体力：饥饿积累速度 +25%。",
                state => state.Character.refs.afflictions.hungerPerSecond *= 1.25f));

        // 蜘蛛侠（原叫「倒吊人」）：挂在边缘 / 绳梯 / 藤蔓上一动不动时不再掉体力。
        // 实现在 HextechPatches.SpiderManPatch：三条攀爬路径的消耗最后都汇到 Character.UseStamina，
        // 由那里在「挂在攀爬物上 + 没有方向输入」时把这一笔消耗归零（详见补丁里的注释）。
        Register(new ActionHextech(
            SpiderManId,
            "蜘蛛侠",
            "挂在边缘、绳梯、藤蔓上静止不动时不再消耗体力。",
            quality,
            drawback: Cost(
                "手脚都黏在墙上，走路反而不利索：地面移动速度 -12%。",
                state => state.Character.refs.movement.movementForce *= 0.88f)));

        // 石化抗性：石化值有两条来路 —— 状态累积（AddStatus(Petrify)）与整数镜像（AddPetrify / SetPetrify），
        // 但 AddStatus 对 Petrify 最后也是 ×100 取整调 AddPetrify，所以两条路都汇进
        // HextechPatches.PetrifyWard（AddPetrify(int) 前缀）这一个点 —— 在那里按 1/3 削减即可，不会双重打折。
        // 2026-09-13 再改口径：之前的「外界一点都攒不上来 + 自己每分钟 1%」等于让所有
        // 「副作用是加石化值」的东西全部白给（机械手的 +5 代价、爬石化石壁、护符……），
        // 太超标 —— 现在改成「受到的石化值积累 -1/3」（本该 +30 只会 +20），压力还在、只是变慢。
        Register(new ActionHextech(
            PetrifyWardId,
            "石化抗性",
            "受到的石化值积累 -33%（比如本该加 30 点石化值，现在只加 20 点）。"
            + "石化压力不会消失，只是来得更慢。",
            quality));

        // 赌徒（原传说档，按玩家要求改到黄金档）：三选一变四选一、多摆一张牌，
        // 见 HextechManager.OfferChoices 与 HextechPanel。
        // 黄金档一般是「带代价的机制」，这条特意不加 —— 效果本身没动，只是稀有度改到黄金。
        Register(new ActionHextech(
            GamblerId,
            "赌徒",
            "每次海克斯三选一都变成四选一：多摆一张牌给你挑。",
            quality));
    }

    // ── 传说（不走品质表，一律不带代价）──────────────────────────
    /// <summary>
    /// 头奖档：稀有度全靠权重压（<c>传说权重</c>，默认 1），
    /// 所以效果本身不再附加代价 —— 抽到就是赚，这就是它们稀有的理由。
    /// </summary>
    private static void RegisterLegendary()
    {
        // 摔落保命：判定在 HextechPatches.Phoenix 里
        // （CharacterMovement.CheckFallDamage 打标记 + AddStatus 前缀里压伤势量）。
        // 2026-09-14 用户拍板：不限次数，最多留 20 点伤势。
        Register(new ActionHextech(
            PhoenixId,
            "不死鸟",
            "摔落伤害不会把你摔到完全昏迷，最多留 20 点伤势（不限次数）。",
            HextechQuality.Legendary));

        // 主动技能「机械手」（原「磁力手」）：按下把手伸长（交互距离 +5 米），
        // 成功交互一次后复原并开始 60 秒冷却，代价是按下那一刻 +5 点石化值。
        // id 保持 unlock_magnet 不动 —— 机场禁用面板里存的就是这个键。
        RegisterSkillUnlock(
            "unlock_magnet",
            "海克斯：机械手",
            "解锁主动技能「机械手」：立刻把交互距离拉长 5 米，成功交互一次后自动复原并开始冷却；"
            + "代价是按下那一刻 +5 点石化值（45 能量 · 交互后才计冷却）。",
            HextechQuality.Legendary,
            SkillId.MechanicalHand);
    }

    // ── 专项状态抗性（不走品质表）────────────────────────────────
    /// <summary>
    /// 走的是「坚韧之躯」同一套机制（Harmony 前缀挂 <c>CharacterAfflictions.AddStatus</c>），
    /// 但只削减一种状态 —— 因为只管一种，每层的系数比通用版的 25% 重一档：
    /// 拿它就是为了专门扛住那一种，代价是别的状态一点忙都不帮。
    /// 数值是「每层乘一个固定系数」，不走品质表，所以说明里也不写 <c>{0}</c>。
    /// </summary>
    private static void RegisterStatusResist()
    {
        // 伤势是最硬的资源（会直接压到倒地线上），给白银档。
        // 2026-09-13 用户拍板：0.7 → 0.75/层（3 层 -65.7% → -57.8%），抗毒体质同步。
        Register(new ActionHextech(
            ThickHideId,
            "皮糙肉厚",
            "受到的伤势积累 -25%。",
            HextechQuality.Silver,
            stackable: true,
            maxStacks: 3));

        Register(new ActionHextech(
            AntidoteBodyId,
            "抗毒体质",
            "受到的中毒积累 -25%。",
            HextechQuality.Silver,
            stackable: true,
            maxStacks: 3));

        // 灼热（日照）与诅咒（仪式）都是情境型的，给青铜档。
        Register(new ActionHextech(
            HeatShieldId,
            "隔热层",
            "受到的灼热（晒伤）积累 -20%。",
            HextechQuality.Bronze,
            stackable: true,
            maxStacks: 3));

        Register(new ActionHextech(
            CurseBreakerId,
            "破咒者",
            "受到的诅咒积累 -20%。",
            HextechQuality.Bronze,
            stackable: true,
            maxStacks: 2));
    }

    // ── 代价型（负面）─────────────────────────────────────────────
    /// <summary>
    /// 这些词条只会从「开行李箱抽奖」里被随到，阶段三选一和商店永远不会给出。
    /// 数值就是「代价」，故意比同品质的正面加成重，所以不走品质表。
    /// 除「倒吊人」是一次性事件外，其余都用乘法，方便回机场时按基准一次性还原。
    /// </summary>
    private static void RegisterNegative()
    {
        var quality = HextechQuality.Bronze;

        Register(new ActionHextech(
            "heavy_bones",
            "沉重之躯",
            "地面移动速度 -10%。",
            quality,
            (state, _) => state.Character.refs.movement.movementForce *= 0.9f,
            stackable: true,
            maxStacks: 3,
            negative: true));

        Register(new ActionHextech(
            "weak_legs",
            "腿软",
            "跳跃高度 -12%。",
            quality,
            (state, _) => state.Character.refs.movement.jumpImpulse *= 0.88f,
            stackable: true,
            maxStacks: 3,
            negative: true));

        Register(new ActionHextech(
            "sluggish",
            "迟钝",
            "冲刺速度 -15%，冲刺耐力消耗 +25%。",
            quality,
            (state, _) =>
            {
                state.Character.refs.movement.sprintMultiplier *= 0.85f;
                state.Character.refs.movement.sprintStaminaUsage *= 1.25f;
            },
            stackable: true,
            maxStacks: 3,
            negative: true));

        Register(new ActionHextech(
            "ravenous",
            "大胃王",
            "饥饿积累速度 +25%。",
            quality,
            (state, _) => state.Character.refs.afflictions.hungerPerSecond *= 1.25f,
            stackable: true,
            maxStacks: 2,
            negative: true));

        // 半条负面、半个好处：代价是寒冷涨得更快，赔偿是灼热（晒伤）积累砍半。
        // 赔偿的那一半不在这里的动作里，而是挂在 AddStatus 前缀的抗性表上
        // （见 HextechPatches.StatusResists），这样层数由那边统一按幂算。
        Register(new ActionHextech(
            ColdBloodedId,
            "冷血体质",
            "夜间寒冷积累速度 +20%；受到的灼热（晒伤）积累 -50%。",
            quality,
            (state, _) => state.Character.refs.afflictions.nightColdPerSecond *= 1.2f,
            stackable: true,
            maxStacks: 2,
            negative: true));

        // 与「坚韧之躯」是一对镜像词条，数值必须一正一负对应，所以不走品质表。
        Register(new ActionHextech(
            GlassyId,
            "玻璃体质",
            "受到的负面状态积累 +25%。",
            quality,
            stackable: true,
            maxStacks: 3,
            negative: true));

        // 下面五条都是「少一个属性」，写法与上面几条一致：只用乘法改字段。
        // 新碰到的字段（转向速度、困倦消退）已经跟着补进 HextechState 的基准值，
        // 回机场时和原来那几条一起还原，不会残留到下一局。
        Register(new ActionHextech(
            "stiff",
            "僵直",
            "转向速度 -25%（转身、空中调整方向都变慢）。",
            quality,
            (state, _) =>
            {
                state.Character.refs.movement.movementTurnSpeed *= 0.75f;
                state.Character.refs.movement.airMovementTurnSpeed *= 0.75f;
            },
            stackable: true,
            maxStacks: 2,
            negative: true));

        // 「铁胃 / 硬皮」的反面：专管消退速度，所以碰的是三条消退速率。
        Register(new ActionHextech(
            "slow_recovery",
            "病去如抽丝",
            "中毒、孢子与尖刺的消退速度 -25%。",
            quality,
            (state, _) =>
            {
                var afflictions = state.Character.refs.afflictions;
                afflictions.poisonReductionPerSecond *= 0.75f;
                afflictions.sporesReductionPerSecond *= 0.75f;
                afflictions.thornsReductionPerSecond *= 0.75f;
            },
            stackable: true,
            maxStacks: 2,
            negative: true));

        // 「清醒」的反面：那条是「每 2 秒驱散一点困倦」，这条直接让困倦退不回去。
        Register(new ActionHextech(
            "never_awake",
            "睡不醒",
            "困倦消退速度 -30%。",
            quality,
            (state, _) => state.Character.refs.afflictions.drowsyReductionPerSecond *= 0.7f,
            stackable: true,
            maxStacks: 2,
            negative: true));

        // 「防晒霜 / 隔热层」的反面：晒伤自己退不掉，只能靠阴凉与篝火。
        Register(new ActionHextech(
            "fever",
            "高烧不退",
            "灼热（晒伤）消退速度 -30%。",
            quality,
            (state, _) => state.Character.refs.afflictions.hotReductionPerSecond *= 0.7f,
            stackable: true,
            maxStacks: 2,
            negative: true));

        // 「拉拉手」的反面：自己扶人的手感变差。
        Register(new ActionHextech(
            "clumsy_hands",
            "手残",
            "拉手（扶起队友）的交互距离 -25%。",
            quality,
            (state, _) =>
            {
                var data = state.Character.data;

                if (data != null)
                {
                    data.grabFriendDistance *= 0.75f;
                }
            },
            stackable: true,
            maxStacks: 2,
            negative: true));

        // 「倒吊人」：唯一一条「一次性」的负面词条（其余全是乘法系数）。
        // 看上去像救了你一命 —— 先把身上能清的负面全清掉，再把伤势直接顶到倒地线前 1%。
        // 名字沿用「倒吊人」这个提法（原来那条已改名「蜘蛛侠」）。
        Register(new ActionHextech(
            HangedManId,
            "倒吊人",
            $"抽到后立刻清除身上所有负面状态，代价是伤势被填到上限的 {HangedManInjuryRatio * 100f:0}%"
            + $"（只剩 {100f - HangedManInjuryRatio * 100f:0}% 就倒地）。",
            quality,
            (state, _) => ApplyHangedMan(state),
            negative: true));
    }

    /// <summary>
    /// 「倒吊人」的效果：先用游戏自带的 <c>ClearAllStatus(false, false)</c> 清掉全部**可清除**的
    /// 负面状态（它会逐个走 <c>SetStatus(…, 0f, …)</c>，所以诅咒、石化、蛛网、捕蝇草都在内 ——
    /// 石化走的是整数镜像，<c>SetStatus</c> 内部会顺手把它归零），
    /// 再把伤势设到上限的 <see cref="HangedManInjuryRatio"/>。
    /// <para>
    /// 最后那次 <c>SetStatus</c> 带 <c>pushStatus: true</c>，会把上面清空的结果一并同步给其他客户端；
    /// 万一伤势上限取不到（cap &lt;= 0），就退化成单独推一次状态，免得清空只落在本地。
    /// </para>
    /// </summary>
    private static void ApplyHangedMan(HextechState state)
    {
        var character = state.Character;

        if (character == null || character.refs == null || character.refs.afflictions == null)
        {
            return;
        }

        var afflictions = character.refs.afflictions;
        afflictions.ClearAllStatus(false, false);

        var cap = afflictions.GetStatusCap(CharacterAfflictions.STATUSTYPE.Injury);

        if (cap <= 0f)
        {
            afflictions.PushStatuses(null);
            return;
        }

        afflictions.SetStatus(
            CharacterAfflictions.STATUSTYPE.Injury,
            cap * HangedManInjuryRatio,
            true);
    }

    // ── 辅助 ─────────────────────────────────────────────────────
    private static void Register(HextechEntry entry)
    {
        HextechRegistry.Register(entry);
    }

    private static void RegisterSkillUnlock(
        string id,
        string title,
        string description,
        HextechQuality quality,
        SkillId skill,
        HextechDrawback? drawback = null)
    {
        Register(new ActionHextech(id, title, description, quality, unlocksSkill: skill, drawback: drawback));
    }

    /// <summary>
    /// 顶级词条的「代价」：乘在基准值上的小幅度负面效果。
    /// 和代价型负面词条一样只用乘法，回机场按基准整体还原时不会有残留。
    /// </summary>
    private static HextechDrawback Cost(string description, Action<HextechState> apply)
    {
        return new HextechDrawback(description, (state, _) => apply(state));
    }

    private static void AddAffliction(HextechState state, Affliction affliction)
    {
        // 记一笔，回机场清空本局成长时要把它摘掉。
        state.TrackAffliction(affliction);
        state.Character.refs.afflictions.AddAffliction(affliction);
    }

    /// <summary>
    /// 空灵之体的低重力模板。游戏 <c>AddAffliction</c> 内部会 <c>Copy()</c> 一份进角色自己的表，
    /// 所以这个实例只是「内容模板」，反复用也不会被游戏改到（强度 1 档、时长等于永久）。
    /// </summary>
    private static readonly Affliction_LowGravity LightBodyAffliction =
        new Affliction_LowGravity(LightBodyAmount, LightBodyDuration);

    /// <summary>空灵之体：每秒自查一次低重力还在不在（不在就补挂、时长被顶掉就顶回来）。</summary>
    private static void TickLightBody(HextechState state, int stacks, float deltaTime)
    {
        var timer = state.GetTimer(LightBodyTimerKey) + deltaTime;

        if (timer < LightBodyCheckInterval)
        {
            state.SetTimer(LightBodyTimerKey, timer);
            return;
        }

        state.SetTimer(LightBodyTimerKey, 0f);
        EnsureLightBody(state);
    }

    /// <summary>
    /// 保证角色身上挂着「永久低重力」，**能自我修复**。
    /// <para>
    /// 游戏的 <c>CharacterAfflictions.AddAffliction</c> 遇到同类状态走的是 <c>Stack</c>，而
    /// <c>Affliction_LowGravity.Stack</c> 会把 <c>totalTime</c> 改成**后来那个**的时长：别的浮空道具
    /// （几分钟的低重力）一带上来，我们那 999999 秒就被顶成它的时长，等它走完这条状态被整条移除
    /// （<c>OnRemoved</c> → <c>RecalculateLowGrav</c>），空灵之体就彻底没了 —— 原来只在抽到时挂一次，
    /// 之后永远不会回来，这就是玩家反馈的「被其他给浮空的道具刷掉」。
    /// </para>
    /// <para>
    /// 再 <c>AddAffliction</c> 一次会走 Stack：时长顶回永久、<c>lowGravAmount</c> 取两者的较大值
    /// （不会把别人的更强效果改弱），<c>RecalculateLowGrav()</c> 重新算重力；状态已经不在时就重新挂上
    /// （<c>OnApplied</c> 同样会重算重力并起脚下的旋风特效）。每秒一次很便宜，也不会发网络包
    /// —— 只有「新加」那条路才会 <c>PushAfflictions</c>。
    /// </para>
    /// <para>
    /// 顺带把旋风音效静音：<c>Affliction_LowGravity.OnApplied</c> 会调
    /// <c>CharacterAfflictions.StartWhirlwind</c>，而 <c>SetWhirlwindStageRPC(Start)</c> 里那次
    /// <c>whirlwindSFX.Play()</c> 是循环播放的低重力风声 —— 临时浮空道具只响几十秒无所谓，
    /// 但「永久」低重力会把它变成整局的背景噪声（玩家反馈「有点吵」）。这里只关 AudioSource、
    /// 保留脚下的旋风粒子（那是低重力唯一的视觉提示，还能看见自己正飘着）。
    /// </para>
    /// </summary>
    private static void EnsureLightBody(HextechState state)
    {
        var character = state.Character;

        if (character == null || character.refs == null || character.refs.afflictions == null)
        {
            return;
        }

        var afflictions = character.refs.afflictions;

        afflictions.AddAffliction(LightBodyAffliction);

        var whirlwind = afflictions.whirlwindSFX;

        if (whirlwind != null)
        {
            whirlwind.mute = true;
        }
    }

    /// <summary>
    /// 每隔 interval 秒触发一次状态削减，计时器存放在 state 上。
    /// <paramref name="id"/> 是词条 id，只用来在日志报告里记一笔「这条削了多少」（见 <see cref="HextechState.RecordEffect"/>）。
    /// </summary>
    private static void TickStatus(
        HextechState state,
        string id,
        string key,
        float deltaTime,
        float interval,
        CharacterAfflictions.STATUSTYPE statusType,
        float amount)
    {
        if (state.Character.refs.afflictions.GetCurrentStatus(statusType) <= 0f)
        {
            return;
        }

        var timer = state.GetTimer(key) + deltaTime;

        if (timer < interval)
        {
            state.SetTimer(key, timer);
            return;
        }

        state.SetTimer(key, 0f);
        state.Character.refs.afflictions.SubtractStatus(statusType, amount);
        state.RecordEffect(id, amount);
    }
}
