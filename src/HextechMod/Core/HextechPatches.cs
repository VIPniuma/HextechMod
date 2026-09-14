using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Peak.Afflictions;
using Photon.Pun;
using UnityEngine;

namespace PeakModder.HextechMod;

/// <summary>
/// 少数无法只靠改数值实现的海克斯效果，用 Harmony 挂钩游戏逻辑。
/// 所有补丁默认都是「不满足条件就直接返回」，对原版行为零影响。
/// </summary>
internal static class HextechPatches
{
    /// <summary>「坚韧之躯」每层把负面状态积累乘以该系数。</summary>
    private const float ToughBodyPerStack = 0.75f;

    /// <summary>「玻璃体质」每层把负面状态积累乘以该系数。</summary>
    private const float GlassyPerStack = 1.25f;

    /// <summary>「百毒不侵」把孢子压到的僵尸化阈值比例（留一点余量，别贴着转化线待机）。</summary>
    private const float SporeImmunityCapRatio = 0.9f;

    /// <summary>
    /// 原版骷髅的伤势倍率：<c>CharacterAfflictions.AddStatus</c> 里写死的
    /// <c>if (isSkeleton &amp;&amp; type == 0 /* Injury */) amount *= 8f</c>（STATUSTYPE.Injury 就是第 0 号）。
    /// <para>
    /// 我们的补丁都跑在原版这一步**之前**，所以凡是往 AddStatus 里塞**绝对量**的设计，都要按这个倍率折回 1/8，
    /// 否则会被原版放大 8 倍（「不死鸟」的「距倒地线还差 1 点」就属于这种）。
    /// 比例型减免（「羽落」）不要用它 —— 线性缩放乘在前还是乘在后，减的都是同一份「×8 之后的总伤」。
    /// </para>
    /// </summary>
    private const float SkeletonInjuryMultiplier = 8f;

    /// <summary>「不死鸟」已改成不限次数（2026-09-14），旧的一次性开关已删。</summary>

    /// <summary>
    /// 「蜘蛛侠」：方向输入小于这个幅度就算「一动不动」。
    /// 攀爬时的输入是一根 0~1 的摇杆量，手柄会有轻微漂移，0.05 足够把它滤掉，
    /// 又不会把「轻轻推着往上爬」误判成静止。
    /// </summary>
    private const float SpiderManStillThreshold = 0.05f;

    /// <summary>
    /// 「不死鸟」用：刚刚上报摔落伤害的那个 <see cref="CharacterMovement"/>。
    /// 摔落伤害的落点是 <c>CharacterMovement.CheckFallDamage</c> 里紧挨着的一次
    /// <c>AddStatus(Injury, …)</c>，中间不会再插别的角色事件，
    /// 所以用一个「是谁摔了」的标记把两者对上 —— 比猜「这一大坨伤势是不是摔出来的」靠谱。
    /// </summary>
    private static CharacterMovement? _fallingMovement;

    /// <summary>
    /// 专项状态抗性：只削减一种状态，系数写死在词条那边（<see cref="DefaultHextechs"/>），
    /// 这里只负责按层数取幂。单项专抗比通用的 25% 重一档 —— 拿它就是为了专门扛住那一种。
    /// <para>
    /// 最后一条是「冷血体质」捎带的半个好处：它本身是负面词条（夜间寒冷积累 +20%），
    /// 但顺带把灼热（晒伤）积累砍掉一半，所以和四条专抗一起走这套削减。
    /// </para>
    /// </summary>
    private static readonly (string Id, CharacterAfflictions.STATUSTYPE Status, float PerStack)[] StatusResists =
    {
        (DefaultHextechs.ThickHideId, CharacterAfflictions.STATUSTYPE.Injury, DefaultHextechs.ThickHidePerStack),
        (DefaultHextechs.AntidoteBodyId, CharacterAfflictions.STATUSTYPE.Poison, DefaultHextechs.AntidoteBodyPerStack),
        (DefaultHextechs.HeatShieldId, CharacterAfflictions.STATUSTYPE.Hot, DefaultHextechs.HeatShieldPerStack),
        (DefaultHextechs.CurseBreakerId, CharacterAfflictions.STATUSTYPE.Curse, DefaultHextechs.CurseBreakerPerStack),
        (DefaultHextechs.ColdBloodedId, CharacterAfflictions.STATUSTYPE.Hot, DefaultHextechs.ColdBloodedHeatResistPerStack),
    };

    private static readonly HashSet<CharacterAfflictions.STATUSTYPE> HarmfulStatuses = new()
    {
        CharacterAfflictions.STATUSTYPE.Injury,
        CharacterAfflictions.STATUSTYPE.Poison,
        CharacterAfflictions.STATUSTYPE.Spores,
        CharacterAfflictions.STATUSTYPE.Cold,
        CharacterAfflictions.STATUSTYPE.Hot,
        CharacterAfflictions.STATUSTYPE.Drowsy,
        CharacterAfflictions.STATUSTYPE.Hunger,
        CharacterAfflictions.STATUSTYPE.Curse,
        CharacterAfflictions.STATUSTYPE.Thorns,
        CharacterAfflictions.STATUSTYPE.Crab,
    };

    /// <summary>
    /// 「机能零食」用：把身上每种负面状态各按「自己当前值」的比例削掉 <paramref name="ratio"/>。
    /// 传 0.05 就是「每种各 -5%」，条越长扣掉的绝对值越多，永远扣不到 0（是相对削减，不是扣整条的 5%）。
    /// <para>
    /// 只走上面那张表和「坚韧之躯」同一套口径 —— 石化 / 蛛网 / 捕蝇草不算日常负面状态，
    /// 它们各自由自己的词条处理（比如「我还有光」管石化）。
    /// </para>
    /// </summary>
    internal static float ReduceHarmfulStatusesByRatio(CharacterAfflictions afflictions, float ratio)
    {
        if (afflictions == null || ratio <= 0f)
        {
            return 0f;
        }

        var total = 0f;

        foreach (var statusType in HarmfulStatuses)
        {
            var current = afflictions.GetCurrentStatus(statusType);

            if (current <= 0f)
            {
                continue;
            }

            var cut = current * ratio;
            afflictions.SubtractStatus(statusType, cut);
            total += cut;
        }

        return total;
    }

    /// <summary>
    /// 本地角色受到的负面状态积累按词条增减：
    /// 「坚韧之躯」每层 ×0.75，「玻璃体质」（负面词条）每层 ×1.25。
    /// 只在「本地自己的客户端」上改动，游戏自身会把结果同步给其他人，不会重复生效。
    /// </summary>
    [HarmonyPatch(typeof(CharacterAfflictions), nameof(CharacterAfflictions.AddStatus))]
    private static class AddStatusPatch
    {
        [HarmonyPrefix]
        private static void Prefix(
            CharacterAfflictions __instance,
            CharacterAfflictions.STATUSTYPE statusType,
            ref float amount,
            bool fromRPC)
        {
            if (fromRPC || amount <= 0f)
            {
                return;
            }

            // 石化（Petrify）不在 HarmfulStatuses 表里：它是「死亡机制」不是日常负面状态，
            // 而且它的削减统一在下面的 PetrifyWard（AddPetrify 前缀）做 —— AddStatus 对 Petrify
            // 内部就是「×100 取整 → AddPetrify」，在这里再动一手会变成双重打折（2026-09-13）。
            if (!HarmfulStatuses.Contains(statusType))
            {
                return;
            }

            var local = Character.localCharacter;

            if (local == null || local.refs == null || local.refs.afflictions != __instance)
            {
                return;
            }

            var state = HextechState.Get(local);

            if (state == null)
            {
                return;
            }

            // ① 先让「坚韧之躯 / 玻璃体质 / 专项抗性」把这一笔积累按系数缩放完 ——
            // 不死鸟留的是「距上限 20 点」的**绝对余量**，必须排在系数之后：
            // 排在前面的话，玻璃体质（+25%/层）会把兜完底的量再放大一遍，安全线就守不住了（2026-09-14 修）。
            var multiplier = MultiplierOf(state, statusType, amount);

            if (!Mathf.Approximately(multiplier, 1f))
            {
                amount *= multiplier;
            }

            // 「骨质疏松」：骷髅只对诅咒 / 蛛网 / 石化 / 捕蝇草开门，诅咒是唯一还能攒的日常状态，
            // 所以这条词条直接把诅咒封死 —— 骷髅形态要做到「没有诅咒」全靠这里。
            if (statusType == CharacterAfflictions.STATUSTYPE.Curse
                && state.StackOfId(AdvancedHextechs.OsteoporosisId) > 0)
            {
                state.RecordEffect(AdvancedHextechs.OsteoporosisId, amount);
                amount = 0f;
                return;
            }

            // ② 摔落造成的伤势：「羽落」按比例减免，「不死鸟」再按绝对量兜底。
            // 标记由 FallDamageMarker 打，这里消费掉（用完就清，免得误伤同帧的其它伤势）。
            if (statusType == CharacterAfflictions.STATUSTYPE.Injury && _fallingMovement != null)
            {
                var fromFall = local.refs.movement != null && _fallingMovement == local.refs.movement;
                _fallingMovement = null;

                if (fromFall)
                {
                    // 先减免后兜底：羽落把量压小之后，不死鸟只为真正越线的摔落出手。
                    ApplyFeatherFall(state, ref amount);
                    ApplyPhoenix(__instance, state, local, ref amount);
                }
            }

            if (statusType == CharacterAfflictions.STATUSTYPE.Spores)
            {
                ClampSpores(__instance, state, ref amount);
            }
        }

        /// <summary>
        /// 通用版（坚韧之躯 / 玻璃体质）作用于全部负面状态，专项版只作用于一种，两者相乘。
        /// <para>
        /// 一边乘一边把「这一条削掉了多少」记到它自己头上（记的是它作用在当前值上的那一份，
        /// 多条同时持有时各记各的），这样报告里能看出是哪条在起作用、一共削了多少。
        /// </para>
        /// </summary>
        private static float MultiplierOf(HextechState state, CharacterAfflictions.STATUSTYPE statusType, float amount)
        {
            var value = amount;

            var tough = state.StackOfId(DefaultHextechs.ToughBodyId);

            if (tough > 0)
            {
                value = ApplyFactor(state, DefaultHextechs.ToughBodyId, value, Mathf.Pow(ToughBodyPerStack, tough));
            }

            var glassy = state.StackOfId(DefaultHextechs.GlassyId);

            if (glassy > 0)
            {
                value = ApplyFactor(state, DefaultHextechs.GlassyId, value, Mathf.Pow(GlassyPerStack, glassy));
            }

            for (var i = 0; i < StatusResists.Length; i++)
            {
                var resist = StatusResists[i];

                if (resist.Status != statusType)
                {
                    continue;
                }

                var stacks = state.StackOfId(resist.Id);

                if (stacks > 0)
                {
                    value = ApplyFactor(state, resist.Id, value, Mathf.Pow(resist.PerStack, stacks));
                }
            }

            return amount <= 0f ? 1f : value / amount;
        }

        /// <summary>
        /// 按系数缩放这一笔状态积累，并把「被改掉的那一份」记到 <paramref name="id"/> 头上
        /// （系数小于 1 是削减、大于 1 是放大，都记正数，见 <see cref="HextechState.RecordEffect"/>）。
        /// </summary>
        private static float ApplyFactor(HextechState state, string id, float value, float factor)
        {
            var scaled = value * factor;
            state.RecordEffect(id, Mathf.Abs(scaled - value));
            return scaled;
        }

        /// <summary>
        /// 「百毒不侵」：孢子最多涨到僵尸化阈值的九成，永远够不到转化线；
        /// 拿到词条之前就已经快到顶的，也按回安全线（多出来的部分靠自身消退慢慢降）。
        /// </summary>
        private static void ClampSpores(
            CharacterAfflictions afflictions,
            HextechState state,
            ref float amount)
        {
            if (state.StackOfId(AdvancedHextechs.SporeImmunityId) <= 0)
            {
                return;
            }

            var threshold = CharacterAfflictions.ZOMBIFICATION_SPORES_THRESHOLD;

            if (threshold <= 0.01f)
            {
                return;
            }

            var room = (threshold * SporeImmunityCapRatio)
                - afflictions.GetCurrentStatus(CharacterAfflictions.STATUSTYPE.Spores);

            if (room <= 0f)
            {
                state.RecordEffect(AdvancedHextechs.SporeImmunityId, amount);
                amount = 0f;
                return;
            }

            if (amount > room)
            {
                state.RecordEffect(AdvancedHextechs.SporeImmunityId, amount - room);
                amount = room;
            }
        }

        /// <summary>
        /// 「羽落」：摔落造成的伤势按层数乘 <see cref="DefaultHextechs.FeatherFallFactorPerStack"/>。
        /// <para>
        /// 原版 <c>CharacterMovement.CapFallDamage(max, time)</c> 只护住「time 秒内的下一次」——
        /// 用掉就把窗口关掉（<c>fallDamageCapEndTime = Time.time</c>），原版只有 <c>AOE.Explode</c> 拿它兜爆炸击飞。
        /// 想要持续减免只能自己乘，所以这里乘在 AddStatus 之前，每次摔落都生效。
        /// </para>
        /// <para>
        /// ⚠ 骷髅**不要**再把原版的 ×8 除回来（见 <see cref="SkeletonInjuryMultiplier"/>）：这是**比例**减免，
        /// 乘在前还是乘在后等价，减的就是「放大 8 倍之后的那份总伤」—— 同样一摔：人形剩 0.02 份，骷髅剩 0.16 份。
        /// 骷髅照旧比人形疼 8 倍，那是「骨质疏松」写明的代价，词条不替它免掉
        /// （只有留**绝对量**的「不死鸟」才折回 1/8）。
        /// </para>
        /// </summary>
        private static void ApplyFeatherFall(HextechState state, ref float amount)
        {
            var stacks = state.StackOfId(DefaultHextechs.FeatherFallId);

            if (stacks <= 0)
            {
                return;
            }

            var before = amount;
            amount *= Mathf.Pow(DefaultHextechs.FeatherFallFactorPerStack, stacks);
            state.RecordEffect(DefaultHextechs.FeatherFallId, before - amount);
        }

        /// <summary>
        /// 「不死鸟」：摔落伤害永远不会把本地角色摔到完全昏迷 ——
        /// 最多把伤势填到距上限还差 <see cref="DefaultHextechs.PhoenixMinInjuryLeft"/>（20 点），**不限次数**
        /// （2026-09-14 用户拍板，原来是「每局 1 次、留 1 点」）。
        /// 只有「这一摔本来会越过 20 点安全线」时才压量，小摔照常结算。
        /// </summary>
        private static void ApplyPhoenix(
            CharacterAfflictions afflictions,
            HextechState state,
            Character local,
            ref float amount)
        {
            if (state.StackOfId(DefaultHextechs.PhoenixId) <= 0)
            {
                return;
            }

            var cap = afflictions.GetStatusCap(CharacterAfflictions.STATUSTYPE.Injury);
            var current = afflictions.GetCurrentStatus(CharacterAfflictions.STATUSTYPE.Injury);
            var room = cap - DefaultHextechs.PhoenixMinInjuryLeft - current;

            // 骷髅在原版加算里还会把伤势 ×8（见 SkeletonInjuryMultiplier），我们是在那之前动手的，
            // 所以「留给它的余量」要先折回 1/8，不然设进去的 room 会被放大 8 倍直接顶到满。
            if (local.data != null && local.data.isSkeleton)
            {
                room /= SkeletonInjuryMultiplier;
            }

            // room <= 0：人已经贴在安全线上了，这一摔不欠它什么。
            if (room <= 0f || amount <= room)
            {
                return;
            }

            state.RecordEffect(DefaultHextechs.PhoenixId, amount - room);
            amount = room;
            HextechHud.Toast("不死鸟：这一摔没能把你带走");
        }
    }

    /// <summary>
    /// 「羽落」/「不死鸟」的配对补丁：把「正在结算摔落伤害的是谁」记下来，
    /// 给 <see cref="AddStatusPatch"/> 认领（见 <see cref="_fallingMovement"/>）。
    /// </summary>
    [HarmonyPatch(typeof(CharacterMovement), "CheckFallDamage")]
    private static class FallDamageMarker
    {
        [HarmonyPrefix]
        private static void Prefix(CharacterMovement __instance)
        {
            _fallingMovement = __instance;
        }
    }

    /// <summary>
    /// 「蜘蛛侠」：挂在边缘 / 绳梯 / 藤蔓上一动不动时不再消耗体力。
    /// <para>
    /// 原版三条攀爬路径各算各的消耗，最后都汇到这一次 <c>Character.UseStamina</c>：
    /// 墙面与挂把手在 <c>CharacterClimbing.Update</c>（静止时被夹到 <c>minStaminaUsage</c>，不是 0），
    /// 绳梯在 <c>CharacterRopeHandling.Update</c>（静止时用 <c>staminaUsage</c>），
    /// 藤蔓在 <c>CharacterVineClimbing.Update</c>（静止时是写死的 0.005/秒）。
    /// 在消费端统一归零最省事，也不必分别去改它们各自的局部量。
    /// </para>
    /// <para>
    /// 判定「静止」用 <c>CharacterInput.movementInput</c> —— 攀爬时的方向输入就是它，
    /// 松开摇杆 / 键盘时接近 0。**攀爬跳**（<c>RPCA_ClimbJump</c> 里那次扣体力）用
    /// <c>jumpWasPressed</c> 排除掉，所以爬到一半原地起跳照旧消耗：
    /// 这条词条给的是「能安心挂着歇」，不是「攀爬免费」。
    /// </para>
    /// <para>
    /// 曾经用错过的字段：<c>CharacterData.staticClimbCost</c>。它由攀爬地形
    /// （<c>ClimbModifierSurface</c>）按地形来回写，管的是「跳过角度 / 地形修正」而不是
    /// 「不消耗」—— 挂边缘时游戏根本不碰它，所以怎么改都停不下体力，反而会和地形打架。
    /// </para>
    /// </summary>
    [HarmonyPatch(typeof(Character), "UseStamina")]
    private static class SpiderManPatch
    {
        [HarmonyPrefix]
        private static void Prefix(Character __instance, ref float __0, out bool __state)
        {
            __state = false;

            try
            {
                if (__0 <= 0f || __instance == null || !__instance.IsLocal)
                {
                    return;
                }

                var data = __instance.data;

                // 「挂在攀爬物上」= 墙上 / 绳梯 / 藤蔓（isClimbingAnything），或抓住边缘 / 钉子的把手
                // （currentClimbHandle）—— 后者正是玩家说的「挂在边缘」，它不属于 isClimbingAnything。
                if (data == null || (!data.isClimbingAnything && data.currentClimbHandle == null))
                {
                    return;
                }

                var input = __instance.input;

                if (input == null || input.jumpWasPressed)
                {
                    return;
                }

                var move = input.movementInput;

                if (move.x * move.x + move.y * move.y > SpiderManStillThreshold * SpiderManStillThreshold)
                {
                    return;
                }

                var state = HextechState.Get(__instance);

                if (state == null || state.StackOfId(DefaultHextechs.SpiderManId) <= 0)
                {
                    return;
                }

                state.RecordEffect(DefaultHextechs.SpiderManId, __0);
                __0 = 0f;
                __state = true;
            }
            catch (Exception exception)
            {
                HextechPlugin.Log.LogWarning($"蜘蛛侠结算失败：{exception.Message}");
            }
        }

        /// <summary>
        /// ⚠ <c>UseStamina</c> 开头就是 <c>if (usage == 0) return false;</c>，
        /// 而绳梯 / 藤蔓的 Update 把 false 当「体力见底」处理 —— 直接把人从绳子上踢下去。
        /// 所以归零之后还要把返回值扶成 true（＝「这次结算过了，人还挂得住」）。
        /// 只改我们动过 amount 的那一次（<c>__state</c>）。
        /// </summary>
        [HarmonyPostfix]
        private static void Postfix(Character __instance, ref bool __result, bool __state)
        {
            if (!__state)
            {
                return;
            }

            __result = true;

            // ⚠ 归零的消耗会让 UseStamina 在「usage == 0」就提前返回，
            // 走不到原版那句 data.sinceUseStamina = 0 —— 于是「多久没用体力」越涨越大，
            // 游戏的自然回体力趁虚而入：挂着挂着体力不降反升（2026-09-14 玩家反馈）。
            // 这里把计时按住 = 游戏以为你一直在用力，挂着不动时体力完全冻结（不掉也不涨）。
            var data = __instance != null ? __instance.data : null;

            if (data != null)
            {
                data.sinceUseStamina = 0f;
            }
        }
    }

    /// <summary>
    /// 「石化抗性」：受到的石化值按 <see cref="DefaultHextechs.PetrifyWardReduction"/>（1/3）削减。
    /// <para>
    /// 石化值的全部来路最后都汇进 <c>CharacterAfflictions.AddPetrify(int)</c>
    /// （状态条路 <c>AddStatus(Petrify)</c> 内部就是「×100 取整 → AddPetrify」），
    /// 所以只在这一个点削减，不会双重打折。削减是「把参数改小后照常放行」，不是拦掉 ——
    /// 石化压力还在，只是来得更慢（2026-09-13 用户定的口径，替代旧的「外界全拦 + 自己每分钟 1%」）。
    /// </para>
    /// <para>
    /// 要拦的是整点数，而 1/3 是小数：把「本该省下的点数」记进
    /// <see cref="DefaultHextechs.PetrifyWardBankKey"/> 攒整，凑满 1 点就真的少加 1 点 ——
    /// 单次 +30 会变成 +20；机械手那种 +5 变成 +4（省下的 0.67 存进银行）；
    /// 每秒 +1 的持续来源则表现为「约每三次拦一点」。长期期望正好 -1/3。
    /// </para>
    /// <para>
    /// 目标签名在这里写死成 <c>(int)</c>：游戏 2026-09-07 的更新往同一个类里又塞了一个
    /// 无参的 <c>static AddPetrify()</c>（测试用），只写方法名会让 Harmony 报
    /// 「Ambiguous match」，而补丁一抛异常整个模组都起不来。
    /// </para>
    /// </summary>
    [HarmonyPatch]
    private static class PetrifyWard
    {
        private static MethodBase? TargetMethod()
        {
            return AccessTools.Method(typeof(CharacterAfflictions), "AddPetrify", new[] { typeof(int) });
        }

        [HarmonyPrefix]
        private static bool Prefix(CharacterAfflictions __instance, ref int __0)
        {
            try
            {
                if (__0 <= 0 || __instance == null)
                {
                    return true;
                }

                var character = __instance.character;

                if (character == null || !character.IsLocal)
                {
                    return true;
                }

                var state = HextechState.Get(character);

                if (state == null || state.StackOfId(DefaultHextechs.PetrifyWardId) <= 0)
                {
                    return true;
                }

                // 理论上想省下的点数（可带小数）进银行，凑满 1 点就真的拦 1 点；
                // banked 每次扣掉整数部分，永远只攒下 < 1 的余数，不会越攒越多。
                var banked = state.GetTimer(DefaultHextechs.PetrifyWardBankKey)
                    + __0 * DefaultHextechs.PetrifyWardReduction;
                var block = Mathf.Min(Mathf.FloorToInt(banked), __0);
                state.SetTimer(DefaultHextechs.PetrifyWardBankKey, banked - block);

                if (block > 0)
                {
                    state.RecordEffect(DefaultHextechs.PetrifyWardId, block);
                    __0 -= block;
                }

                return true;
            }
            catch (Exception exception)
            {
                HextechPlugin.Log.LogWarning($"石化抗性结算失败：{exception.Message}");
                return true;
            }
        }
    }

    /// <summary>
    /// 无敌状态叠层时**只会变短**的修正。
    /// <para>
    /// 原版 <c>Affliction_Invincibility.Stack</c> 是 <c>totalTime = other.totalTime</c> + <c>timeElapsed = 0</c>
    /// —— 后来源的时长无条件顶掉当前的，于是「回光返照」那 15 秒中途用一次 6 秒的技能「无敌」
    /// （或捡到更短的无敌道具）就只剩 6 秒。
    /// </para>
    /// <para>
    /// 这里改成取「两者中更长的剩余时间」：既不缩短，也不凭空延长
    /// （叠层前先记下旧的剩余时间，原版叠完再取一次 max）。
    /// 不在叠层路径上的「第一次获得无敌」不经过 <c>Stack</c>，时长照常。
    /// </para>
    /// </summary>
    [HarmonyPatch]
    private static class InvincibilityStackPatch
    {
        private static MethodBase? TargetMethod()
        {
            return AccessTools.Method(typeof(Affliction_Invincibility), "Stack", new[] { typeof(Affliction) });
        }

        [HarmonyPrefix]
        private static void Prefix(Affliction_Invincibility __instance, out float __state)
        {
            __state = Mathf.Max(0f, __instance.totalTime - __instance.timeElapsed);
        }

        [HarmonyPostfix]
        private static void Postfix(Affliction_Invincibility __instance, float __state)
        {
            if (__state > __instance.totalTime)
            {
                __instance.totalTime = __state;
            }
        }
    }

    /// <summary>
    /// 「百毒不侵」的第二道保险：即使孢子被别处（难度系数、回填的存档数据）推过了转化线，
    /// 也把「会不会变僵尸」这个判定按成 false。只对本地自己的角色生效。
    /// </summary>
    [HarmonyPatch(typeof(CharacterAfflictions), "willZombify", MethodType.Getter)]
    private static class WillZombifyPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(CharacterAfflictions __instance, ref bool __result)
        {
            try
            {
                var local = Character.localCharacter;

                if (local == null || local.refs == null || local.refs.afflictions != __instance)
                {
                    return true;
                }

                var state = HextechState.Get(local);

                if (state == null || state.StackOfId(AdvancedHextechs.SporeImmunityId) <= 0)
                {
                    return true;
                }

                __result = false;
                return false;
            }
            catch (Exception exception)
            {
                HextechPlugin.Log.LogWarning($"百毒不侵结算失败：{exception.Message}");
                return true;
            }
        }
    }

    /// <summary>
    /// 行李箱抽奖：开箱者本人的客户端接管这次开箱的奖励判定。
    /// 原版的「刷 N 件物资」已被下面的补丁整体关掉，这里改成抽 N 次奖。
    /// </summary>
    [HarmonyPatch(typeof(Luggage), nameof(Luggage.Interact_CastFinished))]
    private static class LuggageLotteryPatch
    {
        [HarmonyPostfix]
        private static void Postfix(Luggage __instance, Character interactor)
        {
            if (interactor == null || __instance == null)
            {
                return;
            }

            if (__instance is RespawnChest)
            {
                return;
            }

            LuggageLottery.Run(__instance, interactor);
        }
    }

    /// <summary>
    /// 关掉行李箱的原版掉落。物资改由 <see cref="LuggageLottery"/> 抽奖后统一发放，
    /// 否则每次开箱都会额外刷一遍原版物资，抽奖就白做了。
    /// 复活箱（RespawnChest）保留原版行为。
    /// </summary>
    [HarmonyPatch(typeof(Spawner), nameof(Spawner.SpawnItems))]
    private static class LuggageNoVanillaLootPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(Spawner __instance, ref List<PhotonView> __result)
        {
            if (!(__instance is Luggage) || __instance is RespawnChest)
            {
                return true;
            }

            __result = new List<PhotonView>();
            return false;
        }
    }

    /// <summary>
    /// 每个阶段的篝火被真正点燃时，给自己一次海克斯三选一。
    /// 只有会推进阶段的那次点火（updateSegment = true）算数：
    /// 新角色入队时游戏会补一次「同步点火」（updateSegment = false），那种不算，
    /// 否则换个人进队就能白拿一次。
    /// </summary>
    [HarmonyPatch(typeof(Campfire), "Light_Rpc")]
    private static class CampfireLitPatch
    {
        [HarmonyPostfix]
        private static void Postfix(Campfire __instance, bool updateSegment)
        {
            if (!updateSegment || __instance == null || !__instance.Lit)
            {
                return;
            }

            var local = Character.localCharacter;

            if (local == null || local.data == null)
            {
                return;
            }

            var state = HextechState.Get(local);

            // 同一个阶段只发一次奖；退回旧阶段再点一次也不会重复拿。
            if (state == null || !state.TryClaimCampfireReward((int)__instance.advanceToSegment))
            {
                return;
            }

            HextechManager.Instance?.QueueChoice("点燃篝火 · 选择一项海克斯强化", 3f);
        }
    }
}
