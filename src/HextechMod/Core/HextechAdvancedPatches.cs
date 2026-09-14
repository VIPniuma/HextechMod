using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Peak.Afflictions;
using Photon.Pun;
using UnityEngine;

namespace PeakModder.HextechMod;

/// <summary>
/// 特殊机制词条要用的 Harmony 补丁。
/// <para>
/// 两个原则：只处理「本地玩家自己的角色」（每台机器只负责自己的角色），
/// 以及每个补丁体都包 try/catch —— 词条算错最多是这条词条不生效，不能把游戏搞崩。
/// </para>
/// </summary>
internal static class HextechAdvancedPatches
{
    // ── 领头羊 ───────────────────────────────────────────────────
    /// <summary>
    /// 高度低于持有者的队友，体力消耗降低。
    /// 谁被带谁结算：被带的那台客户端自己算（队友有没有「领头羊」是同步过来的）。
    /// </summary>
    [HarmonyPatch(typeof(Character), "UseStamina")]
    internal static class LeaderAura
    {
        private static float _nextRebuild;
        private static float _cachedFactor = 1f;

        [HarmonyPrefix]
        private static void Prefix(Character __instance, ref float __0)
        {
            try
            {
                if (__0 <= 0f || __instance == null || !__instance.IsLocal)
                {
                    return;
                }

                // 体力消耗每帧都在调用，缓存 0.25 秒足够了。
                if (Time.unscaledTime >= _nextRebuild)
                {
                    _nextRebuild = Time.unscaledTime + 0.25f;
                    _cachedFactor = ComputeFactor(__instance);
                }

                if (_cachedFactor < 1f)
                {
                    __0 *= _cachedFactor;
                }
            }
            catch (Exception exception)
            {
                HextechPlugin.Log.LogWarning($"领头羊结算失败：{exception.Message}");
            }
        }

        private static float ComputeFactor(Character target)
        {
            var characters = Character.AllCharacters;

            if (characters == null)
            {
                return 1f;
            }

            var factor = 1f;
            var targetHeight = target.Center.y;

            foreach (var other in characters)
            {
                var data = other.data;

                if (other == target || data == null || data.dead)
                {
                    continue;
                }

                // 只算高度低于我的队友（「我」是那个领队）。
                if (other.Center.y >= targetHeight - 0.5f)
                {
                    continue;
                }

                var stacks = HextechState.Get(other)?.StackOfId(AdvancedHextechs.LeaderId) ?? 0;

                if (stacks <= 0)
                {
                    continue;
                }

                factor *= AdvancedHextechs.LeaderAuraFactor(stacks);
            }

            return Mathf.Clamp(factor, 0.2f, 1f);
        }
    }

    // ── 拖后腿 ───────────────────────────────────────────────────
    /// <summary>
    /// 「领头羊」的镜像：自己持有时什么都不做，代价落在身边队友身上 ——
    /// 挨着「拖后腿」的人，体力消耗变高。同样由「被拖累的那台客户端」自己结算。
    /// </summary>
    [HarmonyPatch(typeof(Character), "UseStamina")]
    internal static class Deadweight
    {
        private static float _nextRebuild;
        private static float _cachedFactor = 1f;

        [HarmonyPrefix]
        private static void Prefix(Character __instance, ref float __0)
        {
            try
            {
                if (__0 <= 0f || __instance == null || !__instance.IsLocal)
                {
                    return;
                }

                if (Time.unscaledTime >= _nextRebuild)
                {
                    _nextRebuild = Time.unscaledTime + 0.25f;
                    _cachedFactor = ComputeFactor(__instance);
                }

                if (_cachedFactor > 1f)
                {
                    __0 *= _cachedFactor;
                }
            }
            catch (Exception exception)
            {
                HextechPlugin.Log.LogWarning($"拖后腿结算失败：{exception.Message}");
            }
        }

        private static float ComputeFactor(Character target)
        {
            var characters = Character.AllCharacters;

            if (characters == null)
            {
                return 1f;
            }

            var factor = 1f;
            var center = target.Center;

            foreach (var other in characters)
            {
                if (other == null || other == target)
                {
                    continue;
                }

                var data = other.data;

                if (data == null || data.dead)
                {
                    continue;
                }

                if (Vector3.Distance(other.Center, center) > AdvancedHextechs.DeadweightRadius)
                {
                    continue;
                }

                var stacks = HextechState.Get(other)?.StackOfId(AdvancedHextechs.DeadweightId) ?? 0;

                if (stacks <= 0)
                {
                    continue;
                }

                factor *= AdvancedHextechs.DeadweightFactor(stacks);
            }

            return Mathf.Clamp(factor, 1f, AdvancedHextechs.DeadweightMaxFactor);
        }
    }

    // ── 投掷力度（手滑 / 投掷专家）────────────────────────────────
    /// <summary>
    /// 投掷时按词条把「蓄力」打折或者放大。
    /// <para>
    /// <c>DropItemRpc</c> 是 <c>RpcTarget.All</c>：每台客户端拿同一份参数各自算初速度
    /// （<c>力 = min + (max - min) × 蓄力</c>，再乘 <c>Item.throwForceMultiplier</c>）。
    /// 所以只能在「参数」上动手脚 —— 本地偷偷改自己的 <c>minThrowForce</c>，
    /// 会让各端算出来的轨迹不一样，扔出去就拉丝。
    /// 谁持有词条走的是同步过的词条列表，各端结论一致。
    /// </para>
    /// </summary>
    [HarmonyPatch(typeof(CharacterItems), "DropItemRpc")]
    internal static class ThrowForce
    {
        private static readonly FieldInfo? CharacterField = AccessTools.Field(typeof(CharacterItems), "character");

        [HarmonyPrefix]
        private static void Prefix(CharacterItems __instance, ref float __0)
        {
            try
            {
                // 蓄力为 0 是普通「放下 / 丢弃」，不该被这些词条影响。
                if (__0 <= 0f || __instance == null)
                {
                    return;
                }

                var thrower = CharacterField?.GetValue(__instance) as Character;

                if (thrower == null)
                {
                    return;
                }

                var state = HextechState.Get(thrower);

                if (state == null)
                {
                    return;
                }

                // 一条一条乘过去，顺手把「这条让力度变了多少」记到它自己头上（报告里要能看出是哪条在起作用）。
                var value = __0;

                // 手滑（负面词条）：蓄满力也只有七成。
                var slippery = state.StackOfId(AdvancedHextechs.ButterfingersId);

                if (slippery > 0)
                {
                    var scaled = value * AdvancedHextechs.ButterfingersFactor(slippery);
                    state.RecordEffect(AdvancedHextechs.ButterfingersId, value - scaled);
                    value = scaled;
                }

                // 投掷专家：反过来放大；两条同时持有就相乘（自己抵消自己）。
                var master = state.StackOfId(DefaultHextechs.ThrowMasterId);

                if (master > 0)
                {
                    var scaled = value * Mathf.Pow(DefaultHextechs.ThrowMasterFactorPerStack, master);
                    state.RecordEffect(DefaultHextechs.ThrowMasterId, scaled - value);
                    value = scaled;
                }

                if (!Mathf.Approximately(value, __0))
                {
                    __0 = value;
                }
            }
            catch (Exception exception)
            {
                HextechPlugin.Log.LogWarning($"投掷力度结算失败：{exception.Message}");
            }
        }
    }

    // ── 不倒翁 ───────────────────────────────────────────────────
    /// <summary>
    /// 摔倒 / 被击飞后的硬直时间（<c>CharacterData.fallSeconds</c>）。
    /// <para>
    /// <c>Character::Fall(seconds, shake)</c> 自己不做别的，只把这个 seconds 通过
    /// <c>RPCA_Fall</c> 发出去、「谁发的」得是本机（<c>view.IsMine</c>），
    /// 所以在前缀里把参数缩一下就行 —— 各端收到的还是同一个（缩小后的）时长，不会各摔各的。
    /// </para>
    /// </summary>
    [HarmonyPatch(typeof(Character), "Fall")]
    internal static class Tumbler
    {
        [HarmonyPrefix]
        private static void Prefix(Character __instance, ref float __0)
        {
            try
            {
                if (__0 <= 0f || __instance == null)
                {
                    return;
                }

                var state = HextechState.Get(__instance);
                var stacks = state?.StackOfId(DefaultHextechs.TumblerId) ?? 0;

                if (state == null || stacks <= 0)
                {
                    return;
                }

                var before = __0;
                __0 *= Mathf.Pow(DefaultHextechs.TumblerStunFactorPerStack, stacks);
                state.RecordEffect(DefaultHextechs.TumblerId, before - __0);
            }
            catch (Exception exception)
            {
                HextechPlugin.Log.LogWarning($"不倒翁结算失败：{exception.Message}");
            }
        }
    }

    // ── 机械手（原「磁力手」）────────────────────────────────────
    /// <summary>
    /// 主动技能「机械手」：把本地玩家的交互距离临时拉长 5 米。
    /// <para>
    /// 交互距离就是 <c>Interaction.distance</c>：<c>Interaction.LateUpdate</c> 每帧拿它做
    /// <c>LineCheckAll</c>（一条射线 + 一段球形扫描）来找「现在手能够到什么」，
    /// 所以只改这一个字段，拾取物品、开门、拉行李箱会一起变长 —— 不用去认每一种可交互物。
    /// </para>
    /// <para>
    /// 这 5 米是**一次性**的：成功交互一次就立刻缩回去（<see cref="Consume"/>），
    /// 技能也从那一刻才开始冷却 —— 也就是「先按 `G` 把手伸着，走到够不着的地方点一下，
    /// 然后等 60 秒」。代价（+5 点石化值）在按下技能的那一刻就结算，交互没发生也不退。
    /// </para>
    /// </summary>
    internal static class MechanicalHand
    {
        /// <summary>交互距离拉长多少米。</summary>
        internal static float ExtraDistance = 5f;

        /// <summary>使用代价：多少点石化值（1 点 = <see cref="DefaultHextechs.StatusPoint"/>）。</summary>
        internal static float PetrifyCostPoints = 5f;

        /// <summary>上面那个「点」换算成状态条上的量。</summary>
        internal static float PetrifyCost = PetrifyCostPoints * DefaultHextechs.StatusPoint;

        private static Interaction? _interaction;
        private static float _originalDistance;
        private static bool _armed;

        /// <summary>现在处于「已经伸着、还没用掉」的状态。</summary>
        internal static bool IsArmed => _armed;

        /// <summary>技能释放：把手伸长，并立刻结算石化代价。</summary>
        internal static void Arm(Character character)
        {
            var interaction = Interaction.instance;

            if (character == null
                || character.refs == null
                || character.refs.afflictions == null
                || interaction == null)
            {
                return;
            }

            // 防御：正常路径下「已经伸着」会被技能的前置判定挡住，走不到这里。
            Restore();

            _interaction = interaction;
            _originalDistance = interaction.distance;
            interaction.distance = _originalDistance + ExtraDistance;
            _armed = true;

            // 代价照抄游戏自己加状态时的那次 AddStatus（和疾风步的饥饿代价同一套）：
            // fromRPC=false 才会同步给别人，playEffects / notify 让条子与音效正常播。
            // 所以「无敌」期间会被原版逻辑拦下来；「石化抗性」持有者会少掉其中 1/3
            // （2026-09-13 起石化抗性改成按比例削减，机械手的代价不再白给，但也没了）。
            character.refs.afflictions.AddStatus(
                CharacterAfflictions.STATUSTYPE.Petrify,
                PetrifyCost,
                false,
                true,
                true,
                false);

            HextechHud.Toast(
                $"机械手：交互距离 +{ExtraDistance:0} 米（{_originalDistance:0.#} → {interaction.distance:0.#}）· 交互一次后复原");
        }

        /// <summary>成功交互一次：手缩回去，冷却从这一刻开始算。</summary>
        internal static void Consume()
        {
            if (!_armed)
            {
                return;
            }

            Restore();

            var cooldown = SkillRegistry.Get(SkillId.MechanicalHand).Cooldown;
            HextechState.Get(Character.localCharacter)?.StartCooldown(cooldown);

            HextechHud.Toast($"机械手：交互距离复原 · 冷却 {cooldown:0} 秒");
        }

        /// <summary>把手缩回原来的长度（没交互就回机场、或者模组被关掉时兜底）。</summary>
        internal static void Restore()
        {
            if (!_armed)
            {
                return;
            }

            if (_interaction != null)
            {
                _interaction.distance = _originalDistance;
            }

            _interaction = null;
            _armed = false;
        }
    }

    /// <summary>
    /// 「机械手用掉了没有」的判定点。
    /// <para>
    /// <c>Interaction.DoInteraction</c> 是每帧都被调用的，里面按「交互键状态 + <c>readyToInteract</c>」
    /// 决定这一帧要不要真的发起一次交互。所以判定条件就是：进方法前「已上膛」
    /// （<c>readyToInteract</c> 为真、且面前有目标），出来之后它被清成假 ——
    /// 那说明这次真的交互了（长按型的会先进入 held 状态，同样算用掉）。
    /// </para>
    /// </summary>
    [HarmonyPatch(typeof(Interaction), "DoInteraction")]
    internal static class MechanicalHandInteraction
    {
        [HarmonyPrefix]
        private static void Prefix(Interaction __instance, IInteractible? __0, out bool __state)
        {
            __state = __instance != null && __instance.readyToInteract && __0 != null;
        }

        [HarmonyPostfix]
        private static void Postfix(Interaction __instance, bool __state)
        {
            try
            {
                if (!__state || __instance == null || __instance.readyToInteract || !MechanicalHand.IsArmed)
                {
                    return;
                }

                MechanicalHand.Consume();
            }
            catch (Exception exception)
            {
                HextechPlugin.Log.LogWarning($"机械手结算失败：{exception.Message}");
            }
        }
    }

    // ── 拾取结算（拾荒者 / 机能零食 / 烫手）──────────────────────
    /// <summary>
    /// 「一件物品第一次被你拾起」的结算：拾荒者发代币、机能零食削负面状态、烫手把食物烤熟。
    /// <para>
    /// 物品到手上有两条路，两条都得结算 —— 少了哪条，那种捡法下词条会整体不生效：
    /// </para>
    /// <list type="bullet">
    /// <item><description>**进手**：<c>CharacterItems.Equip</c> → <c>SetState(Held, 角色)</c>，
    /// 物品物体就在手上，走 <see cref="Settle"/>；</description></item>
    /// <item><description>**手满了，物品直接落进背包槽**：房主 <c>BackpackVisuals.RefreshVisuals</c>
    /// 为这个槽位生成视觉体时发出 <c>Item.PutInBackpackRPC</c> → <c>SetState(InBackpack, null)</c>
    /// （character 是 null），走 <see cref="SettleInBackpack"/>。</description></item>
    /// </list>
    /// <para>
    /// 注意 <c>PutInBackpackRPC</c> 并**不是**「把东西放进背包」这个动作本身 —— 从手上塞进背包、
    /// 甚至背包视觉重刷都会让它再跑一遍，而且那时物品物体是新造的、实例数据还没下发。
    /// 所以那条路只认槽位数据里的实例 GUID，拿不到就不结算；要是按物体身份去记，
    /// 就变成「在物品栏来回切一直刷代币」（2026-09-12 玩家反馈）。
    /// </para>
    /// <para>
    /// 去重共用 <see cref="HextechState.TryMarkPickupSettled(Item)"/>（按物品实例 GUID 记）：
    /// 同一件物品拿进拿出、丢了再捡都只结算一次，不然食物会被反复烤到 <c>COOKING_MAX</c> 烤糊。
    /// </para>
    /// </summary>
    internal static class PickupSettlement
    {
        /// <summary>进手路径：物品物体在手、实例数据齐全，三个效果都能上。</summary>
        internal static void Settle(Character character, Item item)
        {
            try
            {
                if (character == null || item == null)
                {
                    return;
                }

                var state = HextechState.Get(character);

                if (state == null)
                {
                    return;
                }

                // 实例数据还没下发（少见，手↔背包切换时可能差一帧）→ 挂起来，等它到了再结算。
                // 绝不能退回物体身份当键：那正是「来回切一直刷」的根源。
                if (!HasInstanceGuid(item))
                {
                    Defer(character, item);
                    return;
                }

                if (!state.TryMarkPickupSettled(item))
                {
                    return;
                }

                ApplyAwards(state, character);

                if (state.StackOfId(AdvancedHextechs.HotHandsId) > 0 && CookInstantly(item))
                {
                    state.RecordEffect(AdvancedHextechs.HotHandsId, 1f);
                }
            }
            catch (Exception exception)
            {
                HextechPlugin.Log.LogWarning($"拾取结算失败：{exception.Message}");
            }
        }

        /// <summary>
        /// 背包路径（手满了，物品直接落进自己背着的背包槽）：此刻物品物体是房主刚生成的视觉体，
        /// 实例数据还没下发，读不到 <c>Item.data</c>；但槽位数据里有同一个 GUID，就用它去重。
        /// <para>
        /// 只做「拾荒者 / 机能零食」这两个纯角色侧的效果，**不烤**：<c>FinishCooking</c> 只有物品
        /// 控制器（房主）调得动，非房主在这里烤等于白调，还会把 GUID 提前占掉、物品真正进手时
        /// 就再也烤不上了。烤制留给它进手那一刻。
        /// </para>
        /// </summary>
        internal static void SettleInBackpack(Character character, Guid instanceGuid)
        {
            try
            {
                if (character == null)
                {
                    return;
                }

                var state = HextechState.Get(character);

                if (state == null || !state.TryMarkPickupSettled(instanceGuid))
                {
                    return;
                }

                ApplyAwards(state, character);
            }
            catch (Exception exception)
            {
                HextechPlugin.Log.LogWarning($"拾取结算（背包）失败：{exception.Message}");
            }
        }

        /// <summary>「拾荒者」发代币 + 「机能零食」按当前值削一层负面状态。</summary>
        private static void ApplyAwards(HextechState state, Character character)
        {
            // 「拾荒者」：每件物品第一次被拾起时发 1 枚商店代币。不设每局上限。
            if (state.StackOfId(DefaultHextechs.ScavengerId) > 0)
            {
                state.AwardScavengerToken();
            }

            var snack = state.StackOfId(DefaultHextechs.SnackId);

            if (snack > 0 && character.refs != null)
            {
                // 每种负面状态各按「自己当前值」削 5%（叠层走乘法口径：5% → 5.25% → 5.51%，
                // 见 DefaultHextechs.SnackTotalRatio —— 2026-09-13 之前是每层线性再叠一份）。
                // 以前这里挂的是游戏自带的 Affliction_HealAll，等于捡一次东西白送一次「治疗全部状态」；
                // 再后来只压伤势与中毒，现在按词条说明改成整张负面状态表逐项相对削减。
                var reduced = HextechPatches.ReduceHarmfulStatusesByRatio(
                    character.refs.afflictions,
                    DefaultHextechs.SnackTotalRatio(snack));

                state.RecordEffect(DefaultHextechs.SnackId, reduced);
            }
        }

        /// <summary>
        /// 把「还没烹饪过的食物」直接顶到熟一次。
        /// <para>
        /// 只认食物：<c>Item.itemTags</c> 里带 PackagedFood / Berry / Mushroom 才算。
        /// 以前只检查 <c>canBeCooked</c>，而带 <c>ItemCooking</c> 的不全是食物 ——
        /// 于是火把、木头这类也会被「烤熟」（有的还因为 <c>wreckWhenCooked</c> 直接烤坏）。
        /// </para>
        /// <para>
        /// 而且只烤一次：已经烹饪过的（自己在篝火上烤过的、出生就带 <c>preCooked</c> 的）
        /// 一律不再动。以前只拦「烤到 <c>COOKING_MAX</c>」，半熟的东西会被反复烤到糊 / 炸掉。
        /// </para>
        /// </summary>
        private static bool CookInstantly(Item item)
        {
            var cooking = item.GetComponent<ItemCooking>();

            if (cooking == null || !cooking.canBeCooked)
            {
                return false;
            }

            if (!IsFood(item) || cooking.preCooked > 0 || cooking.timesCookedLocal > 0)
            {
                return false;
            }

            // 走到这里物品就在手上（背包那条路不烤，见 SettleInBackpack 的说明），
            // 用游戏自己的「烤好一次」入口，烹饪进度会照常同步给其他客户端。
            cooking.FinishCooking();
            return true;
        }

        /// <summary>游戏自己的食物标签：袋装食品、浆果、蘑菇。</summary>
        private static bool IsFood(Item item)
        {
            const Item.ItemTags food =
                Item.ItemTags.PackagedFood | Item.ItemTags.Berry | Item.ItemTags.Mushroom;

            return (item.itemTags & food) != Item.ItemTags.None;
        }

        // ── 实例数据晚一帧到位的拾取 ──────────────────────────────
        /// <summary>挂起来等实例数据下发的一次拾取。</summary>
        private struct PendingPickup
        {
            public Character Character;
            public Item Item;

            /// <summary>还能等多久（秒），到点就丢掉。</summary>
            public float Remaining;
        }

        /// <summary>等实例数据的上限。正常下一帧就到，等到这一点还没有就说明这件物品没有实例数据。</summary>
        private const float PendingTimeout = 1f;

        /// <summary>同时挂起的上限，纯防御（正常最多一两件）。</summary>
        private const int PendingCapacity = 16;

        private static readonly List<PendingPickup> _pending = new List<PendingPickup>();

        /// <summary>
        /// 把「拿不到实例 GUID」的拾取挂起来，等数据到位再按 GUID 结算。
        /// <para>
        /// **这是替代「按物体身份兜底」的那条路**：物体身份（网络 ID / 实例 ID）手↔背包每切一次
        /// 都会变，拿它当键会让每次切换都重新结算（玩家反馈的「一直切换物品就可以一直回」）。
        /// 这里宁可等一秒、甚至这一单不要，也绝不按物体身份记。
        /// </para>
        /// </summary>
        private static void Defer(Character character, Item item)
        {
            if (character == null || item == null)
            {
                return;
            }

            for (var i = 0; i < _pending.Count; i++)
            {
                if (_pending[i].Item == item)
                {
                    return;
                }
            }

            if (_pending.Count >= PendingCapacity)
            {
                _pending.RemoveAt(0);
            }

            _pending.Add(new PendingPickup
            {
                Character = character,
                Item = item,
                Remaining = PendingTimeout,
            });
        }

        /// <summary>
        /// 每帧由 <see cref="HextechManager"/> 调用：把挂着的拾取补结算掉。
        /// 物品已经不在那个人手上（丢下 / 收回背包 / 换了别的东西）就直接放弃。
        /// </summary>
        internal static void TickPending(float deltaTime)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            for (var i = _pending.Count - 1; i >= 0; i--)
            {
                var pending = _pending[i];
                var item = pending.Item;

                if (item == null || item.holderCharacter != pending.Character || item.itemState != ItemState.Held)
                {
                    _pending.RemoveAt(i);
                    continue;
                }

                if (HasInstanceGuid(item))
                {
                    _pending.RemoveAt(i);

                    // 回来这里时数据已经在物体上了，TryMarkPickupSettled 能正常按 GUID 去重；
                    // 中途被别人（背包路径）先记过也不会重复发。
                    Settle(pending.Character, item);
                    continue;
                }

                pending.Remaining -= deltaTime;

                if (pending.Remaining <= 0f)
                {
                    _pending.RemoveAt(i);
                    continue;
                }

                _pending[i] = pending;
            }
        }

        /// <summary>这块物品物体上有没有能用的实例 GUID（数据没下发时字段是 null 或空 GUID）。</summary>
        private static bool HasInstanceGuid(Item item)
        {
            var data = item != null ? item.data : null;

            return data != null && data.guid != Guid.Empty;
        }

        /// <summary>
        /// 回机场清空本局成长时把挂着的拾取一起丢掉：新一局的去重表已经清空，
        /// 再补结算上一局挂着的物品就会凭空多发一次。
        /// </summary>
        internal static void ClearPending()
        {
            _pending.Clear();
        }
    }

    /// <summary>
    /// 拾起物品**到手上**的入口（<c>CharacterItems.Equip</c> → <c>SetState(Held, 角色)</c>）。
    /// 只负责判断「这算不算一次拾取」，实际结算交给 <see cref="PickupSettlement"/>。
    /// </summary>
    [HarmonyPatch(typeof(Item), "SetState")]
    internal static class HotHands
    {
        /// <summary>进 <c>SetState</c> 之前这件物品的状态（Postfix 里已经换成新状态了）。</summary>
        [HarmonyPrefix]
        private static void Prefix(Item __instance, out ItemState __state)
        {
            __state = __instance == null ? ItemState.Ground : __instance.itemState;
        }

        [HarmonyPostfix]
        private static void Postfix(Item __instance, ItemState __0, Character __1, ItemState __state)
        {
            if (__instance == null || __1 == null || !__1.IsLocal)
            {
                return;
            }

            if (__0 != ItemState.Held && __0 != ItemState.InBackpack)
            {
                return;
            }

            // 只认「从外面拿到身上」。物品原本就在背包里（从背包拿在手上、在背包内挪格子）
            // 只是它在身上的位置变了，不算新拾取。以前只靠「每件物品结算一次」挡，
            // 一旦去重键失效，在物品栏来回切就会一直刷代币（2026-09-12 玩家反馈）。
            if (__state == ItemState.InBackpack)
            {
                return;
            }

            PickupSettlement.Settle(__1, __instance);
        }
    }

    /// <summary>
    /// 物品**直接落进背包槽**（手满了，不进手）时的结算入口 —— 这种情况物品物体是房主
    /// <c>BackpackVisuals.RefreshVisuals</c> 现造出来的，只有这一条路能被我们看到。
    /// <para>
    /// <c>Item.PutInBackpackRPC</c> 调的是 <c>SetState(InBackpack, null)</c>，character 是 null，
    /// 上面那个补丁认不出来（`__1 == null` 直接 return），所以这条路上词条会整体不生效。
    /// </para>
    /// <para>
    /// 但这条路同时也跑在别的场合（把手上东西塞进背包、背包视觉重刷），物品物体每次都是新的，
    /// **不能按物体身份结算**，否则「在物品栏来回切」就会一直刷代币。所以这里只认一样东西：
    /// 槽位数据里的物品实例 GUID（<c>ItemSlot.data.guid</c>）。拿不到 GUID 就什么都不做 ——
    /// 宁可不给，也不能误给。判定还用「放进去的是自己背着的背包」（<c>IsOnMyBack()</c>）
    /// 把别人的背包排除掉。
    /// </para>
    /// <para>
    /// 单独一个补丁类：万一 <c>BackpackReference</c> 这个结构体参数注入失败，
    /// 也只丢这一条路径，不会连「烫手 / 机能零食 / 拾荒者」一起失效。
    /// </para>
    /// </summary>
    [HarmonyPatch(typeof(Item), "PutInBackpackRPC")]
    internal static class BackpackPickup
    {
        /// <summary>进 RPC 之前这件物品的状态（Postfix 时已经被改成 InBackpack 了）。</summary>
        [HarmonyPrefix]
        private static void Prefix(Item __instance, out ItemState __state)
        {
            __state = __instance == null ? ItemState.InBackpack : __instance.itemState;
        }

        [HarmonyPostfix]
        private static void Postfix(Item __instance, byte __0, ref BackpackReference __1, ItemState __state)
        {
            try
            {
                if (__instance == null || __state == ItemState.InBackpack)
                {
                    return;
                }

                if (!__1.exists || !__1.IsOnMyBack())
                {
                    return;
                }

                var character = Character.localCharacter;

                if (character == null)
                {
                    return;
                }

                // 此刻物品物体刚被 instantiate、实例数据还没下发（RefreshVisuals 是先
                // PutItemInBackpack 再 SetItemInstanceDataRPC），所以只能从槽位数据里拿 GUID。
                var instanceGuid = ReadSlotInstanceGuid(ref __1, __0);

                if (instanceGuid != Guid.Empty)
                {
                    PickupSettlement.SettleInBackpack(character, instanceGuid);
                }
            }
            catch (Exception exception)
            {
                HextechPlugin.Log.LogWarning($"拾荒者（背包）结算失败：{exception.Message}");
            }
        }

        /// <summary>从背包槽位数据里读这件物品的实例 GUID，读不到就返回 <c>Guid.Empty</c>。</summary>
        private static Guid ReadSlotInstanceGuid(ref BackpackReference backpackReference, byte slotID)
        {
            var visuals = backpackReference.GetVisuals();
            var backpackData = visuals != null ? visuals.GetBackpackData() : null;
            var slots = backpackData != null ? backpackData.itemSlots : null;

            if (slots == null || slotID >= slots.Length)
            {
                return Guid.Empty;
            }

            var slot = slots[slotID];
            var data = slot != null ? slot.data : null;

            return data != null ? data.guid : Guid.Empty;
        }
    }

    // ── 厨师 ─────────────────────────────────────────────────────
    /// <summary>本地玩家烤好的食物，随机附带一项增益（吃的人才会吃到效果）。</summary>
    [HarmonyPatch(typeof(ItemCooking), "FinishCooking")]
    internal static class ChefSeasoning
    {
        [HarmonyPostfix]
        private static void Postfix(ItemCooking __instance)
        {
            try
            {
                var item = __instance != null ? __instance.item : null;
                var holder = item != null ? item.holderCharacter : null;

                if (item == null || holder == null || !holder.IsLocal)
                {
                    return;
                }

                var state = HextechState.Get(holder);

                if (state == null || state.StackOfId(AdvancedHextechs.ChefId) <= 0)
                {
                    return;
                }

                Seasoning.Attach(item);
            }
            catch (Exception exception)
            {
                HextechPlugin.Log.LogWarning($"厨师结算失败：{exception.Message}");
            }
        }
    }

    // ── 背包客 ───────────────────────────────────────────────────
    // 扩容补丁在 BackpackCapacity.cs：数据格子、轮盘切片、序列化循环、模型挂点四处必须一起改，
    // 只改其中一处就会让轮盘 slices[i + 1] 越界（按 E 开背包之后视角再也转不动）。

    // ── 人人为我 ─────────────────────────────────────────────────
    /// <summary>
    /// 任何队友死亡（不只是被石化）时，给拥有这条词条的人补额外体力。
    /// 一次死亡可能先后走「死亡 RPC」和「石化淘汰 RPC」两条路，所以两个都挂，
    /// 再用同一个 actor 的短窗口去重，保证一次死亡只补一次。
    /// </summary>
    internal static class AllForOne
    {
        /// <summary>同一次死亡的两条 RPC 之间的去重窗口。</summary>
        private const float DedupeWindow = 2f;

        private static int _lastActor = -1;
        private static float _lastGrantTime;

        [HarmonyPatch(typeof(Character), "RPCA_Die")]
        [HarmonyPostfix]
        private static void AfterDie(Character __instance)
        {
            var view = __instance != null ? __instance.refs?.view : null;
            Grant(__instance, view != null ? view.OwnerActorNr : -1);
        }

        [HarmonyPatch(typeof(Peak.PetrifiedScout), "RPC_SpawnPetrifiedScout")]
        [HarmonyPostfix]
        private static void AfterPetrified(int __0)
        {
            Grant(null, __0);
        }

        private static void Grant(Character? dead, int actorNumber)
        {
            try
            {
                var local = Character.localCharacter;

                if (local == null || dead == local)
                {
                    return;
                }

                // 自己死了不算「队友死亡」。
                var localView = local.refs?.view;

                if (localView != null && actorNumber >= 0 && localView.OwnerActorNr == actorNumber)
                {
                    return;
                }

                var state = HextechState.Get(local);
                var stacks = state?.StackOfId(AdvancedHextechs.AllForOneId) ?? 0;

                if (stacks <= 0)
                {
                    return;
                }

                if (actorNumber >= 0)
                {
                    if (_lastActor == actorNumber && Time.unscaledTime - _lastGrantTime < DedupeWindow)
                    {
                        return;
                    }

                    _lastActor = actorNumber;
                    _lastGrantTime = Time.unscaledTime;
                }

                local.AddExtraStamina(AdvancedHextechs.AllForOneExtraStamina * stacks);
            }
            catch (Exception exception)
            {
                HextechPlugin.Log.LogWarning($"人人为我结算失败：{exception.Message}");
            }
        }
    }

    // ── 精致胃袋 ─────────────────────────────────────────────────
    /// <summary>
    /// 吃食物时的效果缩放：野生（<c>ItemTags.Berry / Mushroom</c>）×0.5，
    /// 包装（<c>ItemTags.PackagedFood</c>：棉花糖 / 烤肠 / 坚果等）×2；
    /// 运动饮料 / 能量饮料 / 棒棒糖按物品名排除，其余物品 ×1。
    /// <para>
    /// 拦截点选 <c>Action_ModifyStatus.RunAction</c>（食物对状态条的所有加减 —— 回饥饿、
    /// 中毒、治伤 —— 都走它）：前缀把 <c>changeAmount</c> 按倍率临时改掉，finalizer 还原，
    /// **不拦不跳过** —— 游戏自己的联机同步、喂食统计、成就照常跑。
    /// buff 类效果（护盾 / 加速那类 ApplyAffliction）不缩放；烤过的野生食物标签不变，仍按野生算。
    /// </para>
    /// <para>
    /// ⚠️ 只有「吃的人」持有精致胃袋才生效（喂队友时按吃到的人算）。
    /// 排除名单按 Unity 物品名的小写包含匹配（sporty / energy / lollipop）——
    /// 游戏若改物品名要同步这份名单。
    /// </para>
    /// </summary>
    [HarmonyPatch(typeof(Action_ModifyStatus), "RunAction")]
    internal static class RefinedStomach
    {
        /// <summary>野生食物（浆果 / 蘑菇）效果倍率。</summary>
        internal static float WildFoodMultiplier = 0.5f;

        /// <summary>包装食物（袋装食品）效果倍率。</summary>
        internal static float PackagedFoodMultiplier = 2f;

        [HarmonyPrefix]
        private static void Prefix(Action_ModifyStatus __instance, Item ___item, out float __state)
        {
            __state = __instance.changeAmount;

            var multiplier = FoodMultiplier(__instance.character, ___item);

            if (!Mathf.Approximately(multiplier, 1f))
            {
                __instance.changeAmount *= multiplier;
            }
        }

        [HarmonyFinalizer]
        private static void Finalizer(Action_ModifyStatus __instance, float __state)
        {
            __instance.changeAmount = __state;
        }

        /// <summary>按「吃的人」的词条与物品标签算倍率；异常时按 1 处理，绝不挡原版吃饭。</summary>
        private static float FoodMultiplier(Character? eater, Item? item)
        {
            try
            {
                if (eater == null || item == null)
                {
                    return 1f;
                }

                var state = HextechState.Get(eater);

                if (state == null || state.StackOfId(DefaultHextechs.RefinedStomachId) <= 0)
                {
                    return 1f;
                }

                // 排除名单：这三样即使带包装标签也不算（2026-09-14 用户定的）。
                var name = item.name?.ToLowerInvariant() ?? string.Empty;

                if (name.Contains("sporty") || name.Contains("energy") || name.Contains("lollipop"))
                {
                    return 1f;
                }

                var tags = item.itemTags;

                if ((tags & Item.ItemTags.Berry) != Item.ItemTags.None
                    || (tags & Item.ItemTags.Mushroom) != Item.ItemTags.None)
                {
                    return WildFoodMultiplier;
                }

                if ((tags & Item.ItemTags.PackagedFood) != Item.ItemTags.None)
                {
                    return PackagedFoodMultiplier;
                }

                return 1f;
            }
            catch (Exception exception)
            {
                HextechPlugin.Log.LogWarning($"精致胃袋倍率计算失败：{exception.Message}");
                return 1f;
            }
        }
    }

    // ── 送葬人 ───────────────────────────────────────────────────
    /// <summary>
    /// 有队友倒地（不是死亡）而且就在附近时，给持有这条词条的本地玩家回体力与一段加速。
    /// 效果落在自己身上（加速没法只加给别人），由游戏负责同步。
    /// </summary>
    internal static class Undertaker
    {
        /// <summary>同一次倒地可能先后走两条 RPC，用短窗口去重。</summary>
        private const float DedupeWindow = 2f;

        private static int _lastActor = -1;
        private static float _lastGrantTime;

        [HarmonyPatch(typeof(Character), "RPCA_PassOut")]
        [HarmonyPostfix]
        private static void AfterPassOut(Character __instance)
        {
            try
            {
                var local = Character.localCharacter;

                if (__instance == null || local == null || __instance == local)
                {
                    return;
                }

                // 太远的队友倒地，跟没看见一样。
                if (Vector3.Distance(local.Center, __instance.Center) > AdvancedHextechs.UndertakerRadius)
                {
                    return;
                }

                var state = HextechState.Get(local);

                if (state == null || state.StackOfId(AdvancedHextechs.UndertakerId) <= 0)
                {
                    return;
                }

                var actorNumber = __instance.refs?.view != null ? __instance.refs.view.OwnerActorNr : -1;

                if (actorNumber >= 0)
                {
                    if (_lastActor == actorNumber && Time.unscaledTime - _lastGrantTime < DedupeWindow)
                    {
                        return;
                    }

                    _lastActor = actorNumber;
                    _lastGrantTime = Time.unscaledTime;
                }

                local.AddStamina(AdvancedHextechs.UndertakerStamina);
                state.ApplyAffliction(new Affliction_FasterBoi
                {
                    totalTime = AdvancedHextechs.UndertakerBuffSeconds,
                    moveSpeedMod = 0.25f,
                    climbSpeedMod = 0.2f,
                });
            }
            catch (Exception exception)
            {
                HextechPlugin.Log.LogWarning($"送葬人结算失败：{exception.Message}");
            }
        }
    }
}

/// <summary>
/// 挂在被「厨师」加过料的食物上：吃到嘴里的那一下，把随机增益加到吃的人身上。
/// 只在本台客户端生效，所以队友咬一口是看不到这份加料的（除非他也装了 mod）。
/// </summary>
internal sealed class Seasoning : MonoBehaviour
{
    private Affliction? _buff;

    public static void Attach(Item item)
    {
        if (item == null || item.GetComponent<Seasoning>() != null)
        {
            return;
        }

        var component = item.gameObject.AddComponent<Seasoning>();
        component._buff = AdvancedHextechs.RollChefBuff();
        item.OnConsumed += component.OnConsumed;
    }

    private void OnConsumed()
    {
        try
        {
            var item = GetComponent<Item>();
            var eater = item != null ? item.holderCharacter : null;
            var buff = _buff;

            if (item == null || eater == null || buff == null || !eater.IsLocal)
            {
                return;
            }

            // 一份食物只加一次料。
            _buff = null;
            item.OnConsumed -= OnConsumed;

            HextechState.Get(eater)?.TrackAffliction(buff);
            eater.refs.afflictions.AddAffliction(buff);
        }
        catch (Exception exception)
        {
            HextechPlugin.Log.LogWarning($"食物增益结算失败：{exception.Message}");
        }
    }
}
