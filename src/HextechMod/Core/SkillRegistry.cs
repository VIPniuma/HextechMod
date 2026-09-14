using System;
using System.Collections.Generic;
using Peak.Afflictions;

namespace PeakModder.HextechMod;

public enum SkillId
{
    SuperJump = 0,
    Sprint = 1,
    Cleanse = 2,
    Heal = 3,
    Adrenaline = 4,
    Invincible = 5,
    MechanicalHand = 6,
}

public sealed class SkillDefinition
{
    public SkillId Id { get; }

    public string Title { get; }

    public string Description { get; }

    public float EnergyCost { get; }

    public float Cooldown { get; }

    /// <summary>
    /// 释放时**不**立刻进冷却，改由技能自己在「用掉」的那一刻调
    /// <see cref="HextechState.StartCooldown"/>。目前只有机械手是这种（交互之后才冷却）。
    /// </summary>
    public bool CooldownOnConsume { get; }

    /// <summary>前置判定没过时给玩家的一句提示（空字符串 = 静默当作没按）。</summary>
    public string BlockedHint { get; }

    private readonly Action<Character> _cast;
    private readonly Func<Character, bool>? _canCast;

    public SkillDefinition(
        SkillId id,
        string title,
        string description,
        float energyCost,
        float cooldown,
        Action<Character> cast,
        bool cooldownOnConsume = false,
        Func<Character, bool>? canCast = null,
        string blockedHint = "")
    {
        Id = id;
        Title = title;
        Description = description;
        EnergyCost = energyCost;
        Cooldown = cooldown;
        _cast = cast;
        CooldownOnConsume = cooldownOnConsume;
        _canCast = canCast;
        BlockedHint = blockedHint;
    }

    /// <summary>现在能不能放。没写前置判定的一律能放。</summary>
    public bool CanCast(Character character)
    {
        return _canCast == null || _canCast(character);
    }

    public void Cast(Character character)
    {
        _cast(character);
    }
}

/// <summary>
/// 海克斯主动技能表。全部只作用于本地玩家。
/// <para>
/// 所有数值（能量 / 冷却 / 时长 / 代价）都是 <c>public static</c> 字段 —— 服务器平衡配置
/// （<see cref="BalanceConfig"/>）会在技能注册**之前**按「类名.字段名」反射覆盖，
/// 所以微调数值不用发新版。下面的说明文字全部用插值引用同一份字段，配置生效后描述自动跟着变。
/// </para>
/// </summary>
public static class SkillRegistry
{
    // ── 能量 / 冷却（肾上腺素 2026-09-14：22 → 60 秒，用户定的）──
    public static float SuperJumpEnergy = 30f;
    public static float SuperJumpCooldown = 8f;
    public static float SprintEnergy = 35f;
    public static float SprintCooldown = 30f;
    public static float CleanseEnergy = 60f;
    public static float CleanseCooldown = 60f;
    public static float HealEnergy = 45f;
    public static float HealCooldown = 120f;
    public static float AdrenalineEnergy = 40f;
    public static float AdrenalineCooldown = 60f;
    public static float InvincibleEnergy = 80f;
    public static float InvincibleCooldown = 45f;
    public static float MechanicalHandEnergy = 45f;
    public static float MechanicalHandCooldown = 60f;

    // ── 数值 / 代价（饥饿 / 石化代价一律用「点」，1 点 = 0.01 条；使用处自己乘 StatusPoint）──
    public static float SprintHungerCost = 10f;

    public static float SprintDurationSeconds = 5f;

    public static float SuperJumpHungerCost = 10f;

    /// <summary>净化：每个负面状态清除当前值的比例（2026-09-14 用户定的：5%）。</summary>
    public static float CleansePercentPerStatus = 0.05f;

    public static float HealInjuryPoints = 15f;

    public static float HealPetrifyCostPoints = 5f;

    public static float AdrenalineDurationSeconds = 12f;

    public static float AdrenalineMoveSpeedMod = 0.5f;

    public static float AdrenalineHungerCost = 15f;

    public static float InvincibleDurationSeconds = 6f;

    public static float InvinciblePetrifyCost = 5f;

    /// <summary>净化会清的状态。⚠️ 不含饥饿（饥饿是净化的代价货币，不能自己清自己）也不含石化（死亡机制）。</summary>
    private static readonly CharacterAfflictions.STATUSTYPE[] CleanseStatuses =
    {
        CharacterAfflictions.STATUSTYPE.Injury,
        CharacterAfflictions.STATUSTYPE.Poison,
        CharacterAfflictions.STATUSTYPE.Spores,
        CharacterAfflictions.STATUSTYPE.Cold,
        CharacterAfflictions.STATUSTYPE.Hot,
        CharacterAfflictions.STATUSTYPE.Drowsy,
        CharacterAfflictions.STATUSTYPE.Curse,
        CharacterAfflictions.STATUSTYPE.Thorns,
        CharacterAfflictions.STATUSTYPE.Crab,
    };

    private static readonly Dictionary<SkillId, SkillDefinition> _definitions = new();

    public static IEnumerable<SkillDefinition> All => _definitions.Values;

    static SkillRegistry()
    {
        // 漂浮：代价 +10 饥饿（2026-09-14：全部主动技能都要有使用代价，不许白嫖）。
        Register(new SkillDefinition(
            SkillId.SuperJump,
            "海克斯漂浮",
            $"朝视线方向弹射出去，并短暂进入低重力漂浮状态；代价是使用后立刻增加 {SuperJumpHungerCost:0} 点饥饿值。",
            SuperJumpEnergy,
            SuperJumpCooldown,
            character =>
            {
                character.refs.movement.DoSuperJump(26f, 0.4f, 1, 1.2f);

                // 代价照抄疾风步的饥饿 AddStatus：fromRPC=false 同步给别人，无敌期会被原版拦下。
                character.refs.afflictions.AddStatus(
                    CharacterAfflictions.STATUSTYPE.Hunger,
                    SuperJumpHungerCost * DefaultHextechs.StatusPoint,
                    false,
                    true,
                    true,
                    false);
            }));

        Register(new SkillDefinition(
            SkillId.Sprint,
            "疾风步",
            $"{SprintDurationSeconds:0} 秒内耐力无限，奔跑与攀爬不再消耗体力；代价是使用后立刻涨一截饥饿。",
            SprintEnergy,
            SprintCooldown,
            character =>
            {
                character.refs.afflictions.AddAffliction(new Affliction_InfiniteStamina(SprintDurationSeconds));

                // 代价：立刻涨一截饥饿。参数照抄游戏自己积累饥饿时的那次 AddStatus 调用
                // （fromRPC=false 才会同步给别人，playEffects/notify 让条子和音效正常播），
                // 所以「无敌」期间会被原版逻辑拦下来，篝火那种「不饿」buff 也一样生效。
                character.refs.afflictions.AddStatus(
                    CharacterAfflictions.STATUSTYPE.Hunger,
                    SprintHungerCost * DefaultHextechs.StatusPoint,
                    false,
                    true,
                    true,
                    false);
            }));

        // 净化（2026-09-14 用户重做）：不再全清 —— 每个负面状态各清掉当前值的 5%，
        // 实际消除多少（换算成「点」）就原样转成饥饿值加上 —— 清得越多越饿，解药是要拿饭换的。
        Register(new SkillDefinition(
            SkillId.Cleanse,
            "净化",
            $"清除自身每个负面状态当前值的 {CleansePercentPerStatus * 100f:0.#}%；"
            + "实际消除多少，就立刻转成多少饥饿值（清得越多越饿）。",
            CleanseEnergy,
            CleanseCooldown,
            character =>
            {
                var afflictions = character.refs.afflictions;

                var removed = 0f;

                foreach (var statusType in CleanseStatuses)
                {
                    var current = afflictions.GetCurrentStatus(statusType);

                    if (current <= 0f)
                    {
                        continue;
                    }

                    var cut = current * CleansePercentPerStatus;
                    afflictions.SubtractStatus(statusType, cut);
                    removed += cut;
                }

                if (removed > 0f)
                {
                    // 代价：真实消除的量原样转成饥饿值（与状态条同刻度），结算路径与疾风步一致。
                    afflictions.AddStatus(
                        CharacterAfflictions.STATUSTYPE.Hunger,
                        removed,
                        false,
                        true,
                        true,
                        false);
                }
            }));

        // 治疗波（2026-09-14 用户重做口径）：只回 15 点受伤值、120 秒冷却、代价 +5 点石化值
        // （与机械手同一条 AddStatus 路径 ——「石化抗性」持有者少 1/3，「无敌」期间被原版逻辑拦下）。
        Register(new SkillDefinition(
            SkillId.Heal,
            "治疗波",
            $"立即恢复 {HealInjuryPoints:0} 点受伤值（只回伤势，不治疗其他负面状态）。"
            + $"代价：立刻增加 {HealPetrifyCostPoints:0} 点石化值。",
            HealEnergy,
            HealCooldown,
            character =>
            {
                var afflictions = character.refs.afflictions;

                // 只回固定点数的受伤值（1 点 = 0.01 条），其他负面一概不碰。
                afflictions.SubtractStatus(CharacterAfflictions.STATUSTYPE.Injury, HealInjuryPoints * DefaultHextechs.StatusPoint);

                // 石化代价照抄机械手的那次 AddStatus：fromRPC=false 会同步给别人，
                // playEffects / notify 让条子与音效正常播。
                afflictions.AddStatus(
                    CharacterAfflictions.STATUSTYPE.Petrify,
                    HealPetrifyCostPoints * DefaultHextechs.StatusPoint,
                    false,
                    true,
                    true,
                    false);
            }));

        // 肾上腺素：代价 +15 饥饿（2026-09-14：爆发速度算预支体力，用完得补）；冷却 22 → 60 秒。
        Register(new SkillDefinition(
            SkillId.Adrenaline,
            "肾上腺素",
            $"{AdrenalineDurationSeconds:0} 秒内大幅提升移动速度与攀爬速度，且不会犯困；"
            + $"代价是使用后立刻透支 {AdrenalineHungerCost / DefaultHextechs.StatusPoint:0} 点饥饿值。",
            AdrenalineEnergy,
            AdrenalineCooldown,
            character =>
            {
                character.refs.afflictions.AddAffliction(new Affliction_FasterBoi
                {
                    totalTime = AdrenalineDurationSeconds,
                    moveSpeedMod = AdrenalineMoveSpeedMod,
                    climbSpeedMod = 1f,
                });

                character.refs.afflictions.AddStatus(
                    CharacterAfflictions.STATUSTYPE.Hunger,
                    AdrenalineHungerCost * DefaultHextechs.StatusPoint,
                    false,
                    true,
                    true,
                    false);
            }));

        // 无敌：代价 +5 石化（2026-09-14：最强保命技，代价与机械手 / 治疗波同一条石化路径 ——
        // 「石化抗性」持有者少 1/3）。⚠️ 代价必须**先**结算再上无敌 buff ——
        // 无敌期间免疫一切负面积累，反过来的话代价会被无敌自己拦掉（永远白给）。
        Register(new SkillDefinition(
            SkillId.Invincible,
            "无敌",
            $"{InvincibleDurationSeconds:0} 秒内免疫一切伤害与负面状态；"
            + $"代价是立刻增加 {InvinciblePetrifyCost:0} 点石化值。",
            InvincibleEnergy,
            InvincibleCooldown,
            character =>
            {
                // 先付代价（此刻还没无敌，石化值能正常落账），再进入无敌。
                character.refs.afflictions.AddStatus(
                    CharacterAfflictions.STATUSTYPE.Petrify,
                    InvinciblePetrifyCost * DefaultHextechs.StatusPoint,
                    false,
                    true,
                    true,
                    false);

                character.refs.afflictions.AddAffliction(new Affliction_Invincibility
                {
                    totalTime = InvincibleDurationSeconds,
                    isFromMilk = true,
                });
            }));

        // 机械手（原「磁力手」）：唯一一个「延后冷却」的技能 ——
        // 按下先把手伸长，成功交互一次才复原并开始冷却，所以用 canCast 挡住就绪期间的第二次按下，
        // 免得白扣能量和石化。数值见 HextechAdvancedPatches.MechanicalHand。
        Register(new SkillDefinition(
            SkillId.MechanicalHand,
            "机械手",
            $"立刻把交互距离拉长 {HextechAdvancedPatches.MechanicalHand.ExtraDistance:0} 米，"
            + "成功交互一次后自动复原并开始冷却。"
            + $"代价：立刻增加 {HextechAdvancedPatches.MechanicalHand.PetrifyCostPoints:0} 点石化值。",
            MechanicalHandEnergy,
            MechanicalHandCooldown,
            character => HextechAdvancedPatches.MechanicalHand.Arm(character),
            cooldownOnConsume: true,
            canCast: _ => Interaction.instance != null && !HextechAdvancedPatches.MechanicalHand.IsArmed,
            blockedHint: "机械手已经伸着了 · 先用一次交互再按"));
    }

    public static void Register(SkillDefinition definition)
    {
        _definitions[definition.Id] = definition;
    }

    public static SkillDefinition Get(SkillId id)
    {
        return _definitions.TryGetValue(id, out var definition)
            ? definition
            : _definitions[SkillId.SuperJump];
    }
}
