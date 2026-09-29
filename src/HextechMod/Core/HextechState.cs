using System;
using System.Collections.Generic;
using System.Globalization;
using Peak.Afflictions;
using Photon.Pun;
using UnityEngine;

namespace PeakModder.HextechMod;

/// <summary>
/// 挂在每个 <see cref="Character"/> 上的海克斯状态：已获得的词条、层数与技能。
/// 词条效果由「角色拥有者自己的客户端」应用，其他客户端只保存一份名册用于显示。
/// 开局什么都不带，所有词条与主动技能都只能靠点燃阶段篝火抽海克斯拿到。
/// </summary>
public sealed class HextechState : MonoBehaviour
{
    private static readonly List<HextechState> _instances = new();

    public static IReadOnlyList<HextechState> Instances => _instances;

    public Character Character { get; private set; } = null!;

    private readonly List<HextechEntry> _owned = new();
    private readonly List<int> _stacks = new();
    private readonly List<SkillId> _skills = new();
    private readonly Dictionary<string, float> _timers = new();
    /// <summary>
    /// 本局已经给过「登岛 / 篝火」奖励的 segment 集合，避免同一段重复弹三选一。
    /// <para>
    /// 2026-09-16 改：以前是「每个角色一个 HashSet」，但 PEAK 复活会重建角色（新 HextechState），
    /// 标记就空了 —— 于是死亡复活后再落地 / 再点篝火，登岛强化会重新弹出来（用户反馈的「死了要重新抽海克斯」）。
    /// 现在按「稳定玩家身份」（actor 号，回机场前不会变）存进静态字典，复活重建角色也不丢，
    /// 一回机场（<see cref="ResetForNewRun"/>）整体清空。
    /// </para>
    /// </summary>
    private static readonly Dictionary<int, HashSet<int>> ClaimedRewardsByActor = new();

    // 「拾取结算」去重：键是物品实例 GUID（随槽位数据走，切手 / 切背包都不会变）。
    // 读不到 GUID 时**不结算**、也绝不退回物体身份（网络 ID / 实例 ID 每切一次都变，
    // 那正是「在物品栏来回切就能一直刷」的根源），交给 PickupSettlement 等数据到位再补。
    private readonly HashSet<Guid> _settledItemInstances = new();
    private readonly Dictionary<string, int> _purchases = new();
    private readonly List<Affliction> _appliedAfflictions = new();

    // 「这条词条本局到底跑没跑」的计量，**和 _owned 按下标一一对应**（报告用，见 CollectUsage）：
    // 前两张是「效果真的落地了几次 / 累计影响了多少」，后两张是「被动在场的时长与帧数」。
    // 只有本机角色的那份状态会有数 —— 别人的词条效果在对方机器上结算，这台机器上只能是 0。
    private readonly List<int> _effectCounts = new();
    private readonly List<float> _effectAmounts = new();
    private readonly List<float> _activeSeconds = new();
    private readonly List<int> _activeFrames = new();

    private int _skillIndex;

    /// <summary>
    /// 「固定栏位」上的诅咒：登岛时自动抽到的负面（代价）词条，之后每次篝火三选一选完重抽一个。
    /// <para>
    /// 它**不占** 4 个普通词条的名额（<see cref="IsAtCap"/> 跳过它），也**不进替换队列**
    /// （换不掉），只能由下一次抽诅咒时整体换掉 —— 这就是「固定栏位」的意思。
    /// </para>
    /// </summary>
    private HextechEntry? _curse;

    // 商店代币是**连续累积**的浮点数，不再是「每满一个间隔 +1」的整数计数器：
    // 速率 1.5/分 就等于每秒 +0.025，HUD 显示到一位小数，玩家能看见它在涨。
    // 小数部分只是「还没攒够一整枚」，消费仍按整数价扣，扣完剩下的零头留在账上。
    private float _tokens;
    private bool _baselineCaptured;

    /// <summary>
    /// 按「稳定玩家身份」（actor 号，与 <see cref="ClaimedRewardsByActor"/> 同口径）记的代币账本。
    /// <para>
    /// 这就是「死亡后代币没了」的根源修复：PEAK 里死亡 / 复活会**销毁并重建角色对象**，
    /// 新挂上来的 <see cref="HextechState"/> 是全新的、_tokens 从 0 开始 —— 代币原来只活在
    /// 角色组件字段上，死一次就跟着组件一起没了（多人模式有 RunRoster 档案兜底还原，
    /// 单人模式连档案都没有，必丢）。这份账本每帧落一笔，重建出的新实例首帧从里面把余额读回来。
    /// </para>
    /// 只活在内存里、回机场（<see cref="HextechManager.ResetRun"/>）整体清空，明确不做跨局保留。
    /// </summary>
    private static readonly Dictionary<int, float> TokensByActor = new();

    /// <summary>本实例是否已经从账本里恢复过余额（每个实例一次，首帧做 —— Awake 时 PhotonView 的 Owner 可能还没挂上）。</summary>
    private bool _tokensRestored;

    /// <summary>这个角色实例已经试过「把掉线前的东西还回来」了（每个实例只还原一次）。</summary>
    private bool _progressRestored;

    /// <summary>
    /// 正在跑「掉线重连还原」（逐条重放词条）。
    /// 一次性副作用（加诅咒值、扎箭这类）要在这期间让开 —— 重放一遍就等于再吃一次。
    /// </summary>
    internal static bool RestoringProgress { get; private set; }

    private float _baseJumpImpulse;
    private float _baseSprintMultiplier;
    private float _baseSprintStaminaUsage;
    private float _baseMovementForce;
    private float _baseMaxGravity;
    private float _baseTurnSpeed;
    private float _baseAirTurnSpeed;
    private float _baseHungerPerSecond;
    private float _baseNightColdPerSecond;
    private float _baseClimbStaminaUsage;
    private float _baseOutOfStamClimbTime;
    private float _baseClimbSpeed;
    private float _baseVineStaminaUsage;
    private float _baseVineClimbSpeed;
    private float _basePoisonReductionPerSecond;
    private float _baseHotReductionPerSecond;
    private float _baseSporesReductionPerSecond;
    private float _baseThornsReductionPerSecond;
    private float _baseDrowsyReductionPerSecond;
    private float _baseGrabFriendDistance;

    public float CooldownRemaining { get; private set; }

    /// <summary>商店代币。局内连续累积（带小数），回机场清零。共享代币模式下改从共享池读。</summary>
    public float Tokens => ModConfig.SharedTokens.Value ? SharedTokenPool.Balance : _tokens;

    /// <summary>代币 ×10 向下取整 —— UI 用它判断「显示的那一位小数变没变」，免得每帧重写字符串。</summary>
    public int TokensTenths => ModConfig.SharedTokens.Value ? Mathf.FloorToInt(SharedTokenPool.Balance * 10f) : Mathf.FloorToInt(_tokens * 10f);

    /// <summary>
    /// 代币的显示口径：**向下**取一位小数（不四舍五入）。
    /// 向下取是为了「显示的数一定买得起」—— 要是 3.97 显示成 4.0，玩家点那颗 4 枚的东西却买不了，很坑。
    /// </summary>
    public static string FormatTokens(float value) =>
        (Mathf.Floor(value * 10f) / 10f).ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>「死而复生」剩下的复活次数：抽到词条给 1 次，用掉归零，回机场也清零。</summary>
    public int ReviveCharges { get; private set; }

    public IReadOnlyList<HextechEntry> Owned => _owned;

    public IReadOnlyList<SkillId> Skills => _skills;

    /// <summary>开局一个技能都没有，想放技能必须先去抽海克斯。</summary>
    public bool HasSkill => _skills.Count > 0;

    /// <summary>固定栏位上挂着的那条代价（没有则为 null）。</summary>
    public HextechEntry? Curse => _curse;

    public bool IsCursed => _curse != null;

    /// <summary>这条词条是不是固定栏位上的诅咒（替换面板靠它把诅咒排除在候选之外）。</summary>
    public bool IsCurse(HextechEntry? entry) => entry != null && ReferenceEquals(_curse, entry);

    /// <summary>把一条代价挂上固定栏位：先摘掉旧的，再挂新的（效果一起重放）。</summary>
    public void SetCurse(HextechEntry entry)
    {
        RemoveCurse();
        Acquire(entry);
        _curse = entry;
    }

    /// <summary>
    /// 只给已有的一条词条**补上诅咒标记**（重连还原用：词条已经逐条还原过了，这里不能再拿一次）。
    /// </summary>
    public void MarkCurse(HextechEntry entry) => _curse = entry;

    /// <summary>摘掉固定栏位上的诅咒（连效果一起清）。</summary>
    public void RemoveCurse()
    {
        var curse = _curse;
        _curse = null;

        if (curse != null)
        {
            RemoveEntry(curse);
        }
    }

    public SkillId? CurrentSkill =>
        _skills.Count == 0 ? null : _skills[_skillIndex % _skills.Count];

    private void Awake()
    {
        Character = GetComponent<Character>();
        _instances.Add(this);

        // 不在这里恢复代币：Awake 时 PhotonView 的 Owner 未必已赋值，actor 号拿不准。
        // 推迟到首帧 Tick 里做（见 RestoreTokensOnce）。
        _tokensRestored = false;
    }

    private void OnDestroy()
    {
        // 角色销毁前把余额最后落一次账：死亡重建的新实例首帧要从账本里读回这笔钱。
        // 机场里不落（见 PersistTokens 的闸），免得一局结束的销毁把旧账带进下一局。
        PersistTokens();
        _instances.Remove(this);
    }

    public static HextechState? Get(Character? character)
    {
        return character == null ? null : character.GetComponent<HextechState>();
    }

    /// <summary>给场上所有角色补齐 <see cref="HextechState"/> 组件。</summary>
    public static void EnsureForAll()
    {
        var all = Character.AllCharacters;

        for (var i = 0; i < all.Count; i++)
        {
            var character = all[i];

            if (character == null || character.GetComponent<HextechState>() != null)
            {
                continue;
            }

            character.gameObject.AddComponent<HextechState>();

            // 组件是运行时挂上去的，必须让 PhotonView 重新扫描一遍 [PunRPC]，
            // 否则对面发来的 RPC 会因为「方法未缓存」而被丢弃。
            var view = character.GetComponent<PhotonView>();

            if (view != null)
            {
                view.RefreshRpcMonoBehaviourCache();
            }
        }
    }

    public bool CanAcquire(HextechEntry entry)
    {
        // 「本局全队最多只能有几个人拿到」这类限制（目前只有传说档的骨质疏松用）：
        // 自己手里的份数 + 队友同步过来的名册一起数，名额满了就在造池子时直接跳过这条。
        if (entry.MaxHoldersPerRun > 0 && CountHolders(entry.Id) >= entry.MaxHoldersPerRun)
        {
            return false;
        }

        var index = _owned.IndexOf(entry);

        if (index < 0)
        {
            return true;
        }

        return entry.Stackable && _stacks[index] < entry.MaxStacks;
    }

    // ── 拿取上限（2026-09-14）─────────────────────────────────────
    /// <summary>普通词条最多同时持有几条（不同词条计 1 条，同词条叠层不重复占位）。可被服务器配置覆盖。</summary>
    public static int MaxNormalHextechs = 4;

    /// <summary>技能解锁词条最多同时持有几条（2026-09-14 用户定的：只能拿 1 个技能）。</summary>
    public static int MaxSkillHextechs = 1;

    /// <summary>某一类（普通 / 技能）词条是否已经到了持有上限。同一条词条叠层不算「新拿」。</summary>
    public bool IsAtCap(bool skillCategory)
    {
        var count = 0;

        for (var i = 0; i < _owned.Count; i++)
        {
            // 固定栏位上的诅咒不占普通词条的名额。
            if (ReferenceEquals(_owned[i], _curse))
            {
                continue;
            }

            if (_owned[i].UnlocksSkill.HasValue == skillCategory)
            {
                count++;
            }
        }

        return count >= (skillCategory ? MaxSkillHextechs : MaxNormalHextechs);
    }

    /// <summary>
    /// 移除一条词条并**连效果一起清除**（2026-09-14 换词条机制的核心）：
    /// 摘掉这条词条挂上去的所有状态 → 把角色数值还原到开局基准 →
    /// 把剩下的词条按各自当前层数重放一遍（重放会重新挂它们的状态与数值）→ 同步名册。
    /// <para>
    /// 为什么用「重放」而不是给每条词条写撤销闭包：所有数值效果都是乘法改基准值，
    /// 基准还原 + 重放的结果与「逐条精确撤销」等价，而不用维护几十份撤销代码。
    /// </para>
    /// </summary>
    public void RemoveEntry(HextechEntry entry, bool broadcast = true)
    {
        var index = _owned.IndexOf(entry);

        if (index < 0)
        {
            return;
        }

        RemoveOwnedAt(index);

        if (entry.UnlocksSkill.HasValue)
        {
            _skills.Remove(entry.UnlocksSkill.Value);

            if (_skillIndex >= _skills.Count)
            {
                _skillIndex = 0;
            }
        }

        RebuildEffects();

        if (broadcast)
        {
            PushState();
        }
    }

    private void RemoveOwnedAt(int index)
    {
        _owned.RemoveAt(index);
        _stacks.RemoveAt(index);
        _effectCounts.RemoveAt(index);
        _effectAmounts.RemoveAt(index);
        _activeSeconds.RemoveAt(index);
        _activeFrames.RemoveAt(index);
    }

    /// <summary>摘状态 → 还原基准 → 重放剩余词条。词条移除 / 未来任何「局内清效果」都走这一个入口。</summary>
    private void RebuildEffects()
    {
        RemoveAppliedAfflictions();
        RestoreBaseline();

        for (var i = 0; i < _owned.Count; i++)
        {
            for (var stack = 1; stack <= _stacks[i]; stack++)
            {
                _owned[i].OnAcquired(this, stack);
                _owned[i].Drawback?.Apply(this, stack);
            }
        }
    }

    /// <summary>
    /// 数「本局全队有几个人已经拿到这条词条」。远程角色的 <see cref="HextechState"/> 是靠
    /// <c>HextechRPC_SyncOwned</c> 同步名册的，所以联机时这个数来自全队；
    /// 只是队友如果这一局还没拿过任何词条，我们这边就还没有他的名册。
    /// </summary>
    private static int CountHolders(string id)
    {
        var count = 0;

        for (var i = 0; i < _instances.Count; i++)
        {
            var state = _instances[i];

            if (state != null && state.Character != null && state.StackOfId(id) > 0)
            {
                count++;
            }
        }

        return count;
    }

    public int StackOf(HextechEntry entry)
    {
        var index = _owned.IndexOf(entry);
        return index < 0 ? 0 : _stacks[index];
    }

    /// <summary>按 id 查询层数，未拥有返回 0。给 Harmony 补丁用。</summary>
    public int StackOfId(string id)
    {
        var index = IndexOfId(id);
        return index < 0 ? 0 : _stacks[index];
    }

    /// <summary>这条词条在本局名册里的下标，没拿到返回 -1。</summary>
    private int IndexOfId(string id)
    {
        for (var i = 0; i < _owned.Count; i++)
        {
            if (_owned[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// 记一次「这条词条的效果真的落地了」：次数 +1、累计影响加上 <paramref name="amount"/>。
    /// <para>
    /// 埋点写在效果真正生效的那一行（补丁里、onTick 里），**不在获得词条时记** ——
    /// 玩家说「某条词条没生效 / 数值不对」时要能看出「它本局跑了几次、一共影响了多少」，
    /// 光有一份「拥有名单」是答不了这个问题的（见 <see cref="CollectUsage"/> 与日志报告那一节）。
    /// </para>
    /// <para>
    /// <paramref name="amount"/> 一律记「这条词条实际影响到的量」（削减类记削减量、增益类记增益量，都是正数），
    /// 单位随词条（代币 / 状态点 / 秒 / 力度…），只跟同一条词条纵向比，跨词条相加没有意义。
    /// 没拥有这条词条时直接丢掉，不上账。
    /// </para>
    /// </summary>
    public void RecordEffect(string id, float amount = 0f)
    {
        var index = IndexOfId(id);

        if (index < 0)
        {
            return;
        }

        _effectCounts[index]++;
        _effectAmounts[index] += amount;
    }

    /// <summary>一条词条本局的用量：触发次数、累计影响、被动在场时长与帧数。</summary>
    public readonly struct HextechUsage
    {
        public HextechUsage(HextechEntry entry, int stacks, int triggers, float amount, float seconds, int frames)
        {
            Entry = entry;
            Stacks = stacks;
            Triggers = triggers;
            Amount = amount;
            Seconds = seconds;
            Frames = frames;
        }

        public HextechEntry Entry { get; }

        public int Stacks { get; }

        /// <summary>效果真的落地了几次。</summary>
        public int Triggers { get; }

        /// <summary>这些次数里累计影响到的量（口径见 <see cref="RecordEffect"/>）。</summary>
        public float Amount { get; }

        /// <summary>被动在场的秒数（<see cref="Tick"/> 里按帧累加）。</summary>
        public float Seconds { get; }

        public int Frames { get; }
    }

    /// <summary>把名册里每条词条的用量抄进 <paramref name="into"/>（报告用，不排序、不过滤）。</summary>
    public void CollectUsage(List<HextechUsage> into)
    {
        if (into == null)
        {
            return;
        }

        for (var i = 0; i < _owned.Count; i++)
        {
            into.Add(new HextechUsage(
                _owned[i],
                _stacks[i],
                _effectCounts[i],
                _effectAmounts[i],
                _activeSeconds[i],
                _activeFrames[i]));
        }
    }

    /// <summary>
    /// 记录「这一阶段的篝火已经发过奖」。返回 true 表示是第一次，
    /// 所以同一阶段重复点火、或者退回去再点一次，都不会重复拿。
    /// </summary>
    public bool TryClaimCampfireReward(int segment)
    {
        // 按稳定玩家身份记，复活重建角色也不丢（见 ClaimedRewardsByActor 的说明）。
        var actor = Character?.refs?.view?.OwnerActorNr ?? 0;

        if (!ClaimedRewardsByActor.TryGetValue(actor, out var set))
        {
            set = new HashSet<int>();
            ClaimedRewardsByActor[actor] = set;
        }

        return set.Add(segment);
    }

    /// <summary>
    /// 记下「这件物品的拾取效果已经结算过了」。返回 true 表示这次是第一次。
    /// <para>
    /// 键是物品实例数据里的 GUID（<c>Item.data.guid</c>）。**只有它是稳的**：
    /// 游戏把物品当成「槽位里的数据 + 一个临时物体」，手↔背包每切一次都会销毁旧物体、
    /// 重新 <c>PhotonNetwork.Instantiate</c> 一个新的（<c>EquipSlotRpc</c> 销毁旧手持物、
    /// <c>BackpackVisuals.RefreshVisuals</c> 重造背包视觉），所以本机实例 ID 既会变、
    /// 又会被 Unity 回收复用给别的物品，两个方向都会错：
    /// 新物品被误判成「已结算」（同类物品再也不给代币）、同一件物品被当成新的（来回切刷代币）。
    /// GUID 随槽位数据走，切手、切背包、丢了再捡都是同一个。
    /// </para>
    /// <para>
    /// 拿不到 GUID 时返回 false（= 这次先不结算），**不退回物体身份**：那等于每次切换都算
    /// 一次新拾取，正是「在物品栏来回切一直刷」的根源。调用方拿到 false 后会把这次拾取挂起来、
    /// 隔几帧等实例数据下发再补（见 <c>PickupSettlement.Defer</c>）；数据始终不到就当没捡到
    /// —— 宁可不给，也不能误给。
    /// </para>
    /// <para>记录是每局制的，回机场清空本局成长时一起清掉。</para>
    /// </summary>
    public bool TryMarkPickupSettled(Item item)
    {
        if (item == null)
        {
            return false;
        }

        var data = item.data;

        return data != null && data.guid != Guid.Empty && _settledItemInstances.Add(data.guid);
    }

    /// <summary>
    /// 同上，但拿到的只有物品实例 GUID（背包路径：物品本体是房主刚生成、还没下发实例数据的
    /// 视觉体，只有槽位数据里有 GUID）。**拿不到 GUID 时由调用方直接跳过** —— 宁可不给，
    /// 也不能按物体身份去记（那才会出现「来回切刷代币」）。
    /// </summary>
    public bool TryMarkPickupSettled(Guid instanceGuid)
    {
        return instanceGuid != Guid.Empty && _settledItemInstances.Add(instanceGuid);
    }

    /// <summary>记下我们自己挂上去的 affliction，回机场清空局内成长时要能摘掉它。</summary>
    public void TrackAffliction(Affliction affliction)
    {
        if (affliction != null)
        {
            _appliedAfflictions.Add(affliction);
        }
    }

    /// <summary>
    /// 花掉代币，代币不够时不扣钱并返回 false。
    /// 商品价格都是整数，所以只比「够不够这个整数」；扣完剩下的零头（0.4 之类）留在账上，不会被抹掉。
    /// </summary>
    public bool TrySpendTokens(int cost)
    {
        // 共享代币模式：扣费走共享池（房主权威，普通客户端乐观扣本地再请房主正式扣）。
        if (ModConfig.SharedTokens.Value)
        {
            return SharedTokenPool.TrySpend(cost);
        }

        if (cost <= 0 || _tokens < cost)
        {
            return false;
        }

        _tokens -= cost;
        PersistTokens();
        return true;
    }

    /// <summary>发代币（拾荒者 +1、行李箱抽到、中途加入补足都走这里）。<paramref name="amount"/> 可以带小数。</summary>
    public void AddTokens(float amount)
    {
        // 商店关了 = 代币功能整体关掉（2026-09-16）：所有「发币」入口统一在这里掐断。
        // 被动积累走 AccrueTokens（不进这个方法），那里另有一道同样的闸。
        if (!ModConfig.ShopEnabled.Value)
        {
            return;
        }

        _tokens = Mathf.Max(0f, _tokens + amount);
        PersistTokens();
    }

    /// <summary>
    /// 「拾荒者」本局靠捡东西发出的代币数（回机场清零），**只用于提示，不封顶**。
    /// 同一件物品被丢掉再捡起来不会重复发 —— 去重和「烫手」
    /// 共用 <see cref="TryMarkPickupSettled(Item)"/>（按物品实例 GUID 记），这里只管发币。
    /// </summary>
    /// <summary>「拾荒者」每捡起一件物品发放的代币数（小数）。2026-09-16 从 +1 改成 +0.3 做削弱：PEAK 里可捡物品极多，+1/件等于白嫖几十枚、商店变免费。</summary>
    public const float ScavengerTokenPerPickup = 0.3f;

    /// <summary>
    /// 「拾荒者」本局靠捡东西发出的代币数（回机场清零），**只用于提示，不封顶**。
    /// 同一件物品被丢掉再捡起来不会重复发 —— 去重和「烫手」
    /// 共用 <see cref="TryMarkPickupSettled(Item)"/>（按物品实例 GUID 记），这里只管发币。
    /// <para>2026-09-16 起改成小数累计（见 <see cref="ScavengerTokenPerPickup"/>）。</para>
    /// </summary>
    public float ScavengerTokensEarned { get; private set; }

    /// <summary>「拾荒者」：发 <see cref="ScavengerTokenPerPickup"/> 枚代币并提示。小数累计、不设每局上限，捡多少给多少。</summary>
    public void AwardScavengerToken()
    {
        ScavengerTokensEarned += ScavengerTokenPerPickup;
        AddTokens(ScavengerTokenPerPickup);
        RecordEffect(DefaultHextechs.ScavengerId, ScavengerTokenPerPickup);

        HextechHud.Toast(Localization.T("拾荒者：+{0:0.0} 商店代币（本局 {1:0.0} 枚）", ScavengerTokenPerPickup, ScavengerTokensEarned));
    }

    /// <summary>本局这件商品买过几次（商店限购用，回机场清零）。</summary>
    public int PurchaseCount(string title)
    {
        return _purchases.TryGetValue(title, out var count) ? count : 0;
    }

    /// <summary>记一次购买，和 <see cref="PurchaseCount"/> 配对。</summary>
    public void RecordPurchase(string title)
    {
        _purchases[title] = PurchaseCount(title) + 1;
    }

    public void ResetCooldown()
    {
        CooldownRemaining = 0f;
    }

    /// <summary>
    /// 从这一刻开始计冷却。给「释放时不进冷却、用掉之后才进」的技能用（目前只有机械手：
    /// 按下先把手伸长，交互掉之后才开始冷却）。
    /// </summary>
    public void StartCooldown(float seconds)
    {
        CooldownRemaining = Mathf.Max(CooldownRemaining, seconds);
    }

    /// <summary>给自己挂一个状态（商店买的东西也走这里，方便回机场时统一摘掉）。</summary>
    public void ApplyAffliction(Affliction affliction)
    {
        if (affliction == null || Character == null || Character.refs == null || Character.refs.afflictions == null)
        {
            return;
        }

        TrackAffliction(affliction);
        Character.refs.afflictions.AddAffliction(affliction);
    }

    public void Acquire(HextechEntry entry, bool broadcast = true)
    {
        var index = _owned.IndexOf(entry);

        if (index < 0)
        {
            AppendOwned(entry, 1);
        }
        else
        {
            _stacks[index] += 1;
        }

        if (entry.UnlocksSkill.HasValue)
        {
            UnlockSkill(entry.UnlocksSkill.Value);
        }

        var stacks = StackOf(entry);

        entry.OnAcquired(this, stacks);

        // 顶级词条的「代价」和正效果一起应用，层数也一起数。
        entry.Drawback?.Apply(this, stacks);

        if (broadcast)
        {
            PushState();
        }
    }

    /// <summary>
    /// 往名册尾部加一条，并把四张用量表一起补零 —— 它们和 <see cref="_owned"/> 是按下标对齐的，
    /// 漏一处下标就会串行（<see cref="_stacks"/> 同理），所以只走这一个入口。
    /// </summary>
    private void AppendOwned(HextechEntry entry, int stacks)
    {
        _owned.Add(entry);
        _stacks.Add(stacks);
        _effectCounts.Add(0);
        _effectAmounts.Add(0f);
        _activeSeconds.Add(0f);
        _activeFrames.Add(0);
    }

    public void UnlockSkill(SkillId skill)
    {
        if (!_skills.Contains(skill))
        {
            _skills.Add(skill);
        }
    }

    public void CycleSkill()
    {
        if (_skills.Count <= 1)
        {
            return;
        }

        _skillIndex = (_skillIndex + 1) % _skills.Count;
    }

    public void Tick(float deltaTime)
    {
        if (Character == null)
        {
            return;
        }

        CaptureBaseline();

        if (CooldownRemaining > 0f)
        {
            CooldownRemaining = Mathf.Max(0f, CooldownRemaining - deltaTime);
        }

        // 首帧先把账本里的余额救回来（死亡 / 复活重建角色时代币不再清零），再正常累积并落账。
        RestoreTokensOnce();
        AccrueTokens(deltaTime);
        PersistTokens();

        for (var i = 0; i < _owned.Count; i++)
        {
            // 被动在场时长：这条词条的 onTick 一共跑了多少帧、在场多久
            // —— 报告里用它答「这条词条到底有没有在跑」（数值型词条没有「触发」，只有这个）。
            _activeSeconds[i] += deltaTime;
            _activeFrames[i]++;

            _owned[i].OnTick(this, _stacks[i], deltaTime);
        }
    }

    /// <summary>局内按分钟发商店代币；在机场（主菜单 / 大厅）不累积。</summary>
    private void AccrueTokens(float deltaTime)
    {
        var interval = ModConfig.TokenIntervalSeconds;

        // 机场里不能发代币 —— 代币是局内的，站在机场等下去不该白嫖。
        if (interval <= 0f || HextechScene.InAirport)
        {
            return;
        }

        // 商店关了 = 代币功能整体关掉（2026-09-16）：被动积累停在这里。
        if (!ModConfig.ShopEnabled.Value)
        {
            return;
        }

        // 共享代币模式：代币由 SharedTokenPool 在房主侧统一累积，这里不再给个人加钱。
        if (ModConfig.SharedTokens.Value)
        {
            return;
        }

        // 鬼魂 / 死亡 / 完全昏迷：不积累代币（2026-09-16 用户反馈鬼魂还能累计）。
        // 注意这里用的是每个角色自己的状态：克隆体 / 幽灵 / 还没醒的队友都不该往池子里加钱。
        var ch = Character;

        if (ch != null && ch.data != null && (ch.data.dead || ch.data.fullyPassedOut || ch.IsGhost))
        {
            return;
        }

        // 挂机惩罚改版（2026-09-16）：站在点着的篝火 25 米范围内不再积累代币。
        // 这是「挂机惩罚」的新含义 —— 以前的「叫蘑菇僵尸」那段已经整个拿掉了（见 CampfireZombieGuard 的删除）。
        if (ModConfig.CampfireAfkGuard.Value && ch != null && AdvancedHextechs.IsNearLitCampfire(ch, ModConfig.CampfireAfkRadius.Value))
        {
            return;
        }

        // 「收藏家」按层数给代币积累速度加成（1 层 +50%，2 层 +100%）；
        // 「资本家」是按「每分钟多几枚」算的绝对收入，换算成速率后直接相加。
        var rate = 1f / interval;
        var collector = StackOfId(AdvancedHextechs.CollectorId);

        if (collector > 0)
        {
            rate *= 1f + (AdvancedHextechs.CollectorTokenBonusPerStack * collector);
        }

        var capitalist = StackOfId(AdvancedHextechs.CapitalistId);

        if (capitalist > 0)
        {
            rate += AdvancedHextechs.CapitalistTokensPerMinute * capitalist / 60f;
        }

        // 代币**按帧连续累加**（1.5/分 → 每秒 +0.025），不再是「每满一个间隔 +1」：
        // 这样非整数速率能如实体现出来，HUD 上那一位小数会一直往上爬。
        var before = Mathf.FloorToInt(_tokens);
        _tokens += rate * deltaTime;

        var whole = Mathf.FloorToInt(_tokens);

        if (whole > before)
        {
            // 攒够新的一整枚才提示一次 —— 和以前一样，只是现在「凑够一枚」的过程看得见了。
            HextechHud.Toast(Localization.T("商店代币 +{0}（共 {1} 枚）· 按 {2} 打开商店", whole - before, whole, ModConfig.ShopKey.Value));
        }
    }

    /// <summary>当前角色的稳定身份键（actor 号；单机 / 没进房时是 0，与 ClaimedRewardsByActor 同口径）。</summary>
    private int Actor =>
        Character != null && Character.refs != null && Character.refs.view != null
            ? Character.refs.view.OwnerActorNr
            : 0;

    /// <summary>
    /// 首帧把账本里的余额读回来 —— 死亡 / 复活重建出的新角色实例靠它拿回代币。
    /// <para>
    /// 取 max 而不是直接赋值：多人模式下稍后还会跑 RunRoster 档案还原（那份按上报间隔记账，
    /// 可能比账本旧一点点），别让档案把账本里更新的余额冲回去；单人模式没有档案，账本就是唯一来源。
    /// </para>
    /// </summary>
    private void RestoreTokensOnce()
    {
        if (_tokensRestored)
        {
            return;
        }

        _tokensRestored = true;

        // 机场 = 一局的间隙，账本应当是空的（ResetRun 刚清过）；就算残留也不往新局里带。
        if (HextechScene.InAirport || TokensByActor.Count == 0)
        {
            return;
        }

        if (TokensByActor.TryGetValue(Actor, out var saved) && saved > _tokens)
        {
            _tokens = saved;
        }
    }

    /// <summary>
    /// 把当前余额落进账本。机场里不记 —— 那是「一局已结束」，记了就会把旧账漏进下一局
    /// （回机场清空 + 这里双保险，销毁顺序在前后都安全）。
    /// </summary>
    private void PersistTokens()
    {
        if (HextechScene.InAirport)
        {
            return;
        }

        TokensByActor[Actor] = _tokens;
    }

    /// <summary>回机场（一局结束）清空代币账本，防止跨局残留。由 <see cref="HextechManager.ResetRun"/> 无条件调用 ——
    /// 和 <see cref="ResetClaimedRewards"/> 同一套理由：角色实例可能已经销毁，藏在实例重置里会漏清。</summary>
    internal static void ResetTokensByActor()
    {
        TokensByActor.Clear();
    }

    /// <summary>「死而复生」：抽到词条时发一次复活机会。</summary>
    public void GrantReviveCharge()
    {
        ReviveCharges++;
    }

    /// <summary>
    /// 按复活键时调用：把一名已经死掉 / 完全昏迷的队友拉到自己面前。
    /// 每局只有 1 次，所以没次数（或者压根没抽到词条）时只会弹一句提示。
    /// </summary>
    public bool TryResurrect()
    {
        if (Character == null || !Character.IsLocal)
        {
            return false;
        }

        if (ReviveCharges <= 0)
        {
            // 没抽到词条的人按这个键不该收到任何提示，免得以为漏了什么。
            if (StackOfId(AdvancedHextechs.ResurrectId) > 0)
            {
                HextechHud.Toast("「死而复生」这一局已经用掉了");
            }

            return false;
        }

        if (!Resurrection.TryRevive(Character, out var targetName, out var failure))
        {
            HextechHud.Toast(failure);
            return false;
        }

        ReviveCharges--;
        RecordEffect(AdvancedHextechs.ResurrectId, 1f);
        HextechHud.Toast(Localization.T("死而复生：把 {0} 拉到了你面前（剩余 {1} 次）", targetName, ReviveCharges));
        return true;
    }

    /// <summary>尝试释放当前技能，返回是否成功。</summary>
    public bool TryCastSkill()
    {
        if (Character == null || !Character.IsLocal || CooldownRemaining > 0f)
        {
            return false;
        }

        var skill = CurrentSkill;

        if (!skill.HasValue)
        {
            return false;
        }

        var definition = SkillRegistry.Get(skill.Value);

        // 「先激活、用掉才进冷却」的技能（机械手）在就绪期间不该再按一次 ——
        // 不然会白扣一次石化，手还是原来那只。
        if (!definition.CanCast(Character))
        {
            if (!string.IsNullOrEmpty(definition.BlockedHint))
            {
                HextechHud.Toast(definition.BlockedHint);
            }

            return false;
        }

        // 延后冷却的技能由它自己在「用掉」的那一刻调 StartCooldown。
        if (!definition.CooldownOnConsume)
        {
            CooldownRemaining = definition.Cooldown;
        }

        definition.Cast(Character);
        return true;
    }

    /// <summary>
    /// 清空「各玩家已领过的登岛 / 篝火奖励」标记。
    /// <para>
    /// ⚠️ 必须由 <see cref="HextechManager.ResetRun"/>（回机场/主菜单）在**任何时机**直接调用 ——
    /// 它以前藏在实例方法 <see cref="ResetForNewRun"/> 里，而那个方法只在「Instances 里有实例」时才会跑：
    /// 回机场的瞬间旧角色实例可能已销毁、新角色还没生成（或玩家从主菜单重开一局），遍历落空，
    /// 这份静态领取标记就跨局残留 —— 下一局登岛 / 篝火的 <see cref="TryClaimCampfireReward"/> 全部返回
    /// 「已领过」，三选一面板一次都不弹（2026-09-16 编号 753261 的反馈）。
    /// </para>
    /// </summary>
    internal static void ResetClaimedRewards()
    {
        ClaimedRewardsByActor.Clear();
    }

    /// <summary>
    /// 一局结束（回到机场）时清空：词条、层数、技能全部还原，
    /// 并把本局改过的角色数值和挂上去的状态恢复成开局的样子。
    /// </summary>
    public void ResetForNewRun()
    {
        // 「死而复生」的复活次数是每局制的。
        ReviveCharges = 0;

        // 「骨质疏松」把角色变成了骷髅，回机场时（如果还是我们变的那个状态）要变回人形，
        // 否则下一局开场顶着骷髅建模站在机场里。
        if (StackOfId(AdvancedHextechs.OsteoporosisId) > 0
            && Character != null
            && Character.refs != null
            && Character.data != null
            && Character.data.isSkeleton)
        {
            Character.data.SetSkeleton(false);
        }

        _owned.Clear();
        _stacks.Clear();
        _effectCounts.Clear();
        _effectAmounts.Clear();
        _activeSeconds.Clear();
        _activeFrames.Clear();
        _skills.Clear();
        _timers.Clear();
        _settledItemInstances.Clear();
        _purchases.Clear();
        _skillIndex = 0;
        _curse = null;

        // 和 _curse / _timers 一样是「每局状态」：角色实例要是跨局存活，
        // 不清的话下一局从第一帧就被 TrackProgressRestore 挡住，整局的掉线还原全部失效。
        _progressRestored = false;
        CooldownRemaining = 0f;
        _tokens = 0f;
        TokensByActor.Remove(Actor);
        ScavengerTokensEarned = 0;

        RemoveAppliedAfflictions();
        RestoreBaseline();

        // 「机械手」加长的是交互距离（挂在 Interaction 组件上，不属于角色数值基准）：
        // 没交互就回机场的话得手动缩回去，不然下一局一开局手就是长的。
        HextechAdvancedPatches.MechanicalHand.Restore();

        // 挂着等实例数据的拾取也一起丢掉（去重表刚清空，再补结算就会凭空多发一次）。
        HextechAdvancedPatches.PickupSettlement.ClearPending();

        // 数值已经还原成开局的样子，下一局重新取一次基准，
        // 免得把玩家在机场期间拿到的东西又按旧基准刷掉。
        _baselineCaptured = false;
    }

    public float GetTimer(string key)
    {
        return _timers.TryGetValue(key, out var value) ? value : 0f;
    }

    public void SetTimer(string key, float value)
    {
        _timers[key] = value;
    }

    /// <summary>记下角色在「还没被任何海克斯改过」之前的数值，重置时用它还原。</summary>
    private void CaptureBaseline()
    {
        if (_baselineCaptured || Character == null || Character.refs == null || Character.data == null)
        {
            return;
        }

        var movement = Character.refs.movement;
        var afflictions = Character.refs.afflictions;

        if (movement == null || afflictions == null)
        {
            return;
        }

        _baseJumpImpulse = movement.jumpImpulse;
        _baseSprintMultiplier = movement.sprintMultiplier;
        _baseSprintStaminaUsage = movement.sprintStaminaUsage;
        _baseMovementForce = movement.movementForce;
        _baseMaxGravity = movement.maxGravity;
        _baseTurnSpeed = movement.movementTurnSpeed;
        _baseAirTurnSpeed = movement.airMovementTurnSpeed;
        _baseHungerPerSecond = afflictions.hungerPerSecond;
        _baseNightColdPerSecond = afflictions.nightColdPerSecond;
        _basePoisonReductionPerSecond = afflictions.poisonReductionPerSecond;
        _baseHotReductionPerSecond = afflictions.hotReductionPerSecond;
        _baseSporesReductionPerSecond = afflictions.sporesReductionPerSecond;
        _baseThornsReductionPerSecond = afflictions.thornsReductionPerSecond;
        _baseDrowsyReductionPerSecond = afflictions.drowsyReductionPerSecond;

        var climbing = Character.refs.climbing;

        if (climbing != null)
        {
            _baseClimbStaminaUsage = climbing.maxStaminaUsage;
            _baseOutOfStamClimbTime = climbing.maxOutOfStamClimbTimeOnStartClimb;
            _baseClimbSpeed = climbing.climbSpeed;
        }

        // 「人猿泰山」改的是藤蔓（JungleVine）那一套，跟绳梯/墙壁的 climbing 是两套组件。
        var vine = Character.refs.vineClimbing;

        if (vine != null)
        {
            _baseVineStaminaUsage = vine.staminaUsage;
            _baseVineClimbSpeed = vine.climbSpeed;
        }

        // 「拉拉手」改的是交互距离，也得记下来，不然每局都会越拉越长。
        _baseGrabFriendDistance = Character.data.grabFriendDistance;

        _baselineCaptured = true;
    }

    private void RestoreBaseline()
    {
        if (!_baselineCaptured || Character == null || Character.refs == null)
        {
            return;
        }

        var movement = Character.refs.movement;

        if (movement != null)
        {
            movement.jumpImpulse = _baseJumpImpulse;
            movement.sprintMultiplier = _baseSprintMultiplier;
            movement.sprintStaminaUsage = _baseSprintStaminaUsage;
            movement.movementForce = _baseMovementForce;
            movement.maxGravity = _baseMaxGravity;

            // 「僵直」改的转向速度：2026-09-14 之前漏还原 —— 残缺值会被下一局的基准捕获当成新基准，
            // 每局再乘一次 0.75，跨局越转越慢（静态推演抓出来的）。
            movement.movementTurnSpeed = _baseTurnSpeed;
            movement.airMovementTurnSpeed = _baseAirTurnSpeed;
        }

        var afflictions = Character.refs.afflictions;

        if (afflictions != null)
        {
            afflictions.hungerPerSecond = _baseHungerPerSecond;
            afflictions.nightColdPerSecond = _baseNightColdPerSecond;
            afflictions.poisonReductionPerSecond = _basePoisonReductionPerSecond;
            afflictions.hotReductionPerSecond = _baseHotReductionPerSecond;
            afflictions.sporesReductionPerSecond = _baseSporesReductionPerSecond;
            afflictions.thornsReductionPerSecond = _baseThornsReductionPerSecond;

            // 「睡不醒」改的困倦消退：同上，漏还原会跨局叠加（2026-09-14 修）。
            afflictions.drowsyReductionPerSecond = _baseDrowsyReductionPerSecond;
        }

        var climbing = Character.refs.climbing;

        if (climbing != null)
        {
            climbing.maxStaminaUsage = _baseClimbStaminaUsage;
            climbing.maxOutOfStamClimbTimeOnStartClimb = _baseOutOfStamClimbTime;
            climbing.climbSpeed = _baseClimbSpeed;
        }

        var vine = Character.refs.vineClimbing;

        if (vine != null)
        {
            vine.staminaUsage = _baseVineStaminaUsage;
            vine.climbSpeed = _baseVineClimbSpeed;
        }

        var data = Character.data;

        if (data != null)
        {
            data.grabFriendDistance = _baseGrabFriendDistance;
        }
    }

    private void RemoveAppliedAfflictions()
    {
        if (Character == null || !Character.IsLocal || Character.refs == null)
        {
            _appliedAfflictions.Clear();
            return;
        }

        var afflictions = Character.refs.afflictions;

        if (afflictions != null)
        {
            for (var i = 0; i < _appliedAfflictions.Count; i++)
            {
                var affliction = _appliedAfflictions[i];

                if (affliction == null)
                {
                    continue;
                }

                // 必须按**类型**移除：AddAffliction 遇到同类会把内容 Stack 进表里那条
                // （我们手里的实例根本不进表），而 RemoveAffliction(实例) 走的是 List.Remove 的引用比较 ——
                // 传自己的实例在「表里已有同类」时会静默删不掉，本局成长就摘不干净了。
                afflictions.RemoveAffliction(affliction.GetAfflictionType());
            }

            // 「空灵之体」在挂上永久低重力时把旋风音效静音了（见 DefaultHextechs.EnsureLightBody），
            // 本局成长清干净之后要还回去 —— 此时人身上若还有别的浮空道具，它的风声应该照常响。
            var whirlwind = afflictions.whirlwindSFX;

            if (whirlwind != null)
            {
                whirlwind.mute = false;
            }
        }

        _appliedAfflictions.Clear();
    }

    /// <summary>把名册打包成（词条 id, 层数）两个数组。同步与「本局成员档案」共用同一份口径。</summary>
    public (string[] Ids, int[] Stacks) OwnedSnapshot()
    {
        var ids = new string[_owned.Count];
        var stacks = new int[_owned.Count];

        for (var i = 0; i < _owned.Count; i++)
        {
            ids[i] = _owned[i].Id;
            stacks[i] = _stacks[i];
        }

        return (ids, stacks);
    }

    public void PushState()
    {
        var view = Character == null ? null : Character.refs.view;

        if (view == null || !view.IsMine || !PhotonNetwork.InRoom)
        {
            return;
        }

        var (ids, stacks) = OwnedSnapshot();

        // 代币、复活次数、固定栏位上的诅咒 id 一起带走：房主那份成员档案（掉线重连还原）要靠它们，
        // 光有名册的话重连回来代币还是 0、诅咒也会退化成普通词条。
        // ⚠ 参数个数必须和 HextechRPC_SyncOwned 的签名一致（少一个整条 RPC 都会被丢弃）。
        view.RPC(nameof(HextechRPC_SyncOwned), RpcTarget.Others, ids, stacks, Tokens, ReviveCharges, Curse?.Id ?? string.Empty);
    }

    /// <summary>这个角色实例是不是已经处理过还原（不管是还原成功还是确认「本来就没东西」）。</summary>
    public bool ProgressRestored => _progressRestored;

    /// <summary>标明「这份状态本来就是完好的，不用还原」（避免每帧都去查档案）。</summary>
    public void MarkProgressIntact() => _progressRestored = true;

    /// <summary>
    /// 中途加入时向房主问一次「本局已经跑了多久」，用来补足商店代币。
    /// 房主没装 mod、或者答复在路上丢了，都会由 <see cref="HextechManager"/> 隔几秒重试。
    /// </summary>
    public void RequestRunElapsed()
    {
        var view = Character == null || Character.refs == null ? null : Character.refs.view;

        if (view == null || view.ViewID <= 0)
        {
            return;
        }

        view.RPC(nameof(HextechRPC_RequestRunElapsed), RpcTarget.MasterClient, view.ViewID);
    }

    /// <summary>房主收到询问：把「本局已经打了多久」回给问的人。只有主持者会答复。</summary>
    [PunRPC]
    public void HextechRPC_RequestRunElapsed(int requesterViewId)
    {
        if (!PhotonNetwork.IsMasterClient)
        {
            return;
        }

        var manager = HextechManager.Instance;
        var elapsed = manager == null ? 0f : manager.RunElapsedSeconds;
        var view = PhotonView.Find(requesterViewId);

        // 时长还没攒出来（比如主持者自己也刚进局）就先不答，让对方过几秒再问。
        if (view == null || view.Owner == null || elapsed < 1f)
        {
            return;
        }

        view.RPC(nameof(HextechRPC_ReceiveRunElapsed), view.Owner, elapsed);
    }

    /// <summary>收到房主答复的本局时长，按时间差补足自己错过的商店代币。</summary>
    [PunRPC]
    public void HextechRPC_ReceiveRunElapsed(float hostElapsedSeconds)
    {
        if (Character == null || !Character.IsLocal)
        {
            return;
        }

        HextechManager.Instance?.ApplyRunElapsedCatchUp(this, hostElapsedSeconds);
    }

    /// <summary>
    /// 把自己禁用的词条广播给全房间。传空字符串表示撤销自己那一票。
    /// 禁用是「全房间一起生效」的，所以这里发给所有人，而不是只发房主。
    /// </summary>
    public void BroadcastBan(string hextechId)
    {
        var view = Character == null || Character.refs == null ? null : Character.refs.view;

        if (view == null || !view.IsMine || !PhotonNetwork.InRoom || PhotonNetwork.LocalPlayer == null)
        {
            return;
        }

        view.RPC(nameof(HextechRPC_ApplyBan), RpcTarget.All, PhotonNetwork.LocalPlayer.ActorNumber, hextechId);
    }

    /// <summary>收到某个玩家改了票。</summary>
    [PunRPC]
    public void HextechRPC_ApplyBan(int actorNumber, string hextechId)
    {
        HextechBans.ApplyRemote(actorNumber, hextechId);
    }

    /// <summary>
    /// 向房主问一次当前的禁用名单。中途进房间的人靠它补齐 ——
    /// 别人禁词条的时候自己还没进来，广播是收不到的。
    /// </summary>
    public void RequestBans()
    {
        var view = Character == null || Character.refs == null ? null : Character.refs.view;

        if (view == null || view.ViewID <= 0)
        {
            return;
        }

        view.RPC(nameof(HextechRPC_RequestBans), RpcTarget.MasterClient, view.ViewID);
    }

    /// <summary>房主收到询问：把当前的禁用名单回给问的人。名单就几条，一次发完。</summary>
    [PunRPC]
    public void HextechRPC_RequestBans(int requesterViewId)
    {
        if (!PhotonNetwork.IsMasterClient)
        {
            return;
        }

        var view = PhotonView.Find(requesterViewId);

        // 问的人可能刚好掉线了，那就没什么好回的。
        if (view == null || view.Owner == null)
        {
            return;
        }

        var votes = HextechBans.All;
        var actors = new int[votes.Count];
        var ids = new string[votes.Count];
        var index = 0;

        foreach (var vote in votes)
        {
            actors[index] = vote.Key;
            ids[index] = vote.Value;
            index++;
        }

        view.RPC(nameof(HextechRPC_SyncBans), view.Owner, actors, ids);
    }

    /// <summary>收到房主补发的整份名单。</summary>
    [PunRPC]
    public void HextechRPC_SyncBans(int[] actorNumbers, string[] hextechIds)
    {
        HextechBans.ReplaceAll(actorNumbers, hextechIds);
    }

    /// <summary>
    /// 房主改完商店单品价之后把整张表广播给全房间。
    /// 只认房主发的 —— 客户端的商店价格必须和房主看到的一致，谁改都得走房主。
    /// </summary>
    public void BroadcastPrices()
    {
        var view = Character == null || Character.refs == null ? null : Character.refs.view;

        if (view == null || !view.IsMine || !PhotonNetwork.InRoom)
        {
            return;
        }

        if (!PhotonNetwork.IsMasterClient)
        {
            // 非房主改不动价格（界面也不会给他这个入口），这里再兜一道。
            return;
        }

        var (ids, prices) = ShopPricing.Snapshot();
        view.RPC(nameof(HextechRPC_SyncPrices), RpcTarget.All, ids, prices);
    }

    /// <summary>收到房主广播 / 补发的调价表。</summary>
    [PunRPC]
    public void HextechRPC_SyncPrices(string[] ids, int[] prices)
    {
        ShopPricing.ApplyRemote(ids, prices);
    }

    /// <summary>
    /// 向房主问一次当前的调价表。中途进房的人靠它补齐 ——
    /// 房主改价的时候自己还没进来，广播是收不到的。
    /// </summary>
    public void RequestPrices()
    {
        var view = Character == null || Character.refs == null ? null : Character.refs.view;

        if (view == null || view.ViewID <= 0 || PhotonNetwork.IsMasterClient)
        {
            return;
        }

        view.RPC(nameof(HextechRPC_RequestPrices), RpcTarget.MasterClient, view.ViewID);
    }

    /// <summary>房主收到询问：把当前的调价表回给问的人。</summary>
    [PunRPC]
    public void HextechRPC_RequestPrices(int requesterViewId)
    {
        if (!PhotonNetwork.IsMasterClient)
        {
            return;
        }

        var view = PhotonView.Find(requesterViewId);

        // 问的人可能刚好掉线了，那就没什么好回的。
        if (view == null || view.Owner == null)
        {
            return;
        }

        var (ids, prices) = ShopPricing.Snapshot();
        view.RPC(nameof(HextechRPC_SyncPrices), view.Owner, ids, prices);
    }

    /// <summary>
    /// 房主把自己的「全房间统一设置」广播给所有人（商店开关 / 代币速率 / 物价 / 开箱概率 / 传说权重 / 共享代币）。
    /// 这些项原本各读各的本地 .cfg —— 房主关掉商店后队友那边照样开着，所以现在一律以房主为准。
    /// </summary>
    public bool BroadcastHostSettings()
    {
        var view = Character == null || Character.refs == null ? null : Character.refs.view;

        if (view == null || !view.IsMine || !PhotonNetwork.InRoom || !PhotonNetwork.IsMasterClient)
        {
            return false;
        }

        var (flags, values, ints) = HostSettings.Snapshot();
        view.RPC(nameof(HextechRPC_SyncHostSettings), RpcTarget.All, flags, values, ints);
        return true;
    }

    /// <summary>向房主问一次当前的房间设置。中途进房 / 掉线重连的人靠它补齐。</summary>
    public void RequestHostSettings()
    {
        var view = Character == null || Character.refs == null ? null : Character.refs.view;

        if (view == null || view.ViewID <= 0 || PhotonNetwork.IsMasterClient)
        {
            return;
        }

        view.RPC(nameof(HextechRPC_RequestHostSettings), RpcTarget.MasterClient, view.ViewID);
    }

    /// <summary>房主收到询问：把当前的房间设置回给问的人。</summary>
    [PunRPC]
    public void HextechRPC_RequestHostSettings(int requesterViewId)
    {
        if (!PhotonNetwork.IsMasterClient)
        {
            return;
        }

        var view = PhotonView.Find(requesterViewId);

        // 问的人可能刚好掉线了，那就没什么好回的。
        if (view == null || view.Owner == null)
        {
            return;
        }

        var (flags, values, ints) = HostSettings.Snapshot();
        view.RPC(nameof(HextechRPC_SyncHostSettings), view.Owner, flags, values, ints);
    }

    /// <summary>收到房主广播 / 补发的房间设置。</summary>
    [PunRPC]
    public void HextechRPC_SyncHostSettings(int flags, float[] values, int[] ints)
    {
        HostSettings.ApplyRemote(flags, values, ints);
    }

    /// <summary>
    /// 房主收到「招蘑菇僵尸」的请求（黄毛追求者）。
    /// 僵尸是网络对象，必须由房主生成大家才都看得到；词条有没有由发起方自己保证（和其它转房主 RPC 同口径）。
    /// </summary>
    [PunRPC]
    public void HextechRPC_RequestAdmirerZombie(int requesterViewId)
    {
        if (!PhotonNetwork.IsMasterClient)
        {
            return;
        }

        var view = PhotonView.Find(requesterViewId);

        // 发起的人可能刚好掉线了，那就没什么好刷的。
        if (view == null || view.Owner == null)
        {
            return;
        }

        CurseHextechs.SpawnAdmirerZombieLocal();
    }

    /// <summary>
    /// 房主收到「求生之路」的刷怪请求（5 只蘑菇僵尸 + 童子军领队）。同上，由房主生成大家才都看得到。
    /// </summary>
    [PunRPC]
    public void HextechRPC_RequestLeftForDeadHorde(int requesterViewId)
    {
        if (!PhotonNetwork.IsMasterClient)
        {
            return;
        }

        var view = PhotonView.Find(requesterViewId);

        if (view == null || view.Owner == null)
        {
            return;
        }

        CurseHextechs.SpawnLeftForDeadHordeLocal();
    }

    /// <summary>
    /// 非房主的开箱者 / 商店买家把「生成物资」的请求转给房主。
    /// 只有房主能往世界里实例化物品，位置由请求方算好。
    /// <paramref name="receiverViewId"/> 是商店买家的角色视图（-1 表示直接掉在地上）。
    /// </summary>
    [PunRPC]
    public void HextechRPC_SpawnLoot(string[] names, Vector3[] positions, bool kinematic, int sourceViewId, int receiverViewId)
    {
        if (!PhotonNetwork.IsMasterClient || names == null || positions == null)
        {
            return;
        }

        Spawner? source = null;

        if (sourceViewId >= 0)
        {
            var sourceView = PhotonView.Find(sourceViewId);
            source = sourceView == null ? null : sourceView.GetComponent<Spawner>();
        }

        Character? receiver = null;

        if (receiverViewId >= 0)
        {
            var receiverView = PhotonView.Find(receiverViewId);
            receiver = receiverView == null ? null : receiverView.GetComponent<Character>();
        }

        HextechSpawning.Spawn(source, names, positions, kinematic, receiver);
    }

    [PunRPC]
    public void HextechRPC_SyncOwned(
        string[] ids,
        int[] stacks,
        float tokens,
        int reviveCharges,
        string curseId,
        PhotonMessageInfo info)
    {
        _owned.Clear();
        _stacks.Clear();
        _effectCounts.Clear();
        _effectAmounts.Clear();
        _activeSeconds.Clear();
        _activeFrames.Clear();

        _tokens = Mathf.Max(0f, tokens);
        ReviveCharges = Mathf.Max(0, reviveCharges);
        PersistTokens();

        for (var i = 0; i < ids.Length; i++)
        {
            var entry = HextechRegistry.Find(ids[i]);

            if (entry == null)
            {
                continue;
            }

            AppendOwned(entry, i < stacks.Length ? stacks[i] : 1);

            if (entry.UnlocksSkill.HasValue)
            {
                UnlockSkill(entry.UnlocksSkill.Value);
            }
        }

        // 顺手记进「本局成员档案」：掉线重连要靠它把人这一局攒的东西还回去（见 RunRoster）。
        RunRoster.Record(info.Sender?.UserId, ids, stacks, tokens, reviveCharges, curseId);
    }

    /// <summary>
    /// 向房主要一次自己这一局的成长。只有「自己那份档案也丢了」才需要走这一步 ——
    /// 通常是游戏整个重启过（内存里的档案自然没了）。
    /// </summary>
    public void RequestProgress()
    {
        var view = Character == null || Character.refs == null ? null : Character.refs.view;

        if (view == null || view.ViewID <= 0 || PhotonNetwork.IsMasterClient)
        {
            return;
        }

        view.RPC(nameof(HextechRPC_RequestProgress), RpcTarget.MasterClient, view.ViewID);
    }

    /// <summary>房主收到询问：把这位玩家本局的成长回给他。</summary>
    [PunRPC]
    public void HextechRPC_RequestProgress(int requesterViewId)
    {
        if (!PhotonNetwork.IsMasterClient)
        {
            return;
        }

        var view = PhotonView.Find(requesterViewId);

        // 问的人可能刚好掉线了，那就没什么好回的。
        if (view == null || view.Owner == null)
        {
            return;
        }

        if (!RunRoster.TryGet(view.Owner.UserId, out var ids, out var stacks, out var tokens, out var revive, out var curseId))
        {
            return;
        }

        view.RPC(nameof(HextechRPC_ApplyProgress), view.Owner, ids, stacks, tokens, revive, curseId ?? string.Empty);
    }

    /// <summary>收到房主补发的成长数据。</summary>
    [PunRPC]
    public void HextechRPC_ApplyProgress(string[] ids, int[] stacks, float tokens, int reviveCharges, string curseId)
    {
        RestoreProgress(ids, stacks, tokens, reviveCharges, curseId);
    }

    /// <summary>
    /// 把这一局攒的东西还回来（掉线重连 / 中途加入）。
    /// <para>
    /// 关键是**逐条重放效果**，而不是照 <see cref="HextechRPC_SyncOwned"/> 那样只填名册 ——
    /// 那只够「面板上显示有这个词条」，人身上没有数值、没有状态、技能也没解锁。
    /// 所以要按层数逐次走 <see cref="Acquire"/>，让 <c>OnAcquired</c> 与代价一并生效。
    /// </para>
    /// </summary>
    public void RestoreProgress(string[]? ids, int[]? stacks, float tokens, int reviveCharges, string? curseId = null)
    {
        if (Character == null || !Character.IsLocal || _progressRestored)
        {
            return;
        }

        _progressRestored = true;

        // 重放期间挂上标志：一次性副作用（加诅咒值、扎箭）看到它就让开，别跟着重放再吃一次。
        RestoringProgress = true;

        // 取 max：死亡重建时账本里可能已经有比档案更新的余额（RestoreTokensOnce 先救过一次），
        // 别让按上报间隔记账的档案把新鲜数字冲回去；游戏重启重连时账本是空的，档案值照常生效。
        _tokens = Mathf.Max(_tokens, Mathf.Max(0f, tokens));
        ReviveCharges = Mathf.Max(0, reviveCharges);
        PersistTokens();

        if (ids == null || ids.Length == 0)
        {
            // 没攒下词条（代币可能攒了一些），上面已经还过了。
            RestoringProgress = false;
            return;
        }

        // 先把基准取下来：重连后角色是新的、基准还没取，
        // 这时候直接应用词条会把加成烤进基准里 —— 之后一重放就会叠两次。
        CaptureBaseline();

        _owned.Clear();
        _stacks.Clear();
        _effectCounts.Clear();
        _effectAmounts.Clear();
        _activeSeconds.Clear();
        _activeFrames.Clear();
        _skills.Clear();
        _skillIndex = 0;

        for (var i = 0; i < ids.Length; i++)
        {
            var entry = HextechRegistry.Find(ids[i]);

            // 版本更新后这条词条可能已经改名 / 删掉了，跳过而不是让整份还原失败。
            if (entry == null)
            {
                continue;
            }

            var count = i < stacks!.Length ? Mathf.Clamp(stacks[i], 1, entry.MaxStacks) : 1;

            for (var stack = 0; stack < count; stack++)
            {
                // broadcast: false —— 一条一条发 RPC 太吵，最后统一推一次。
                Acquire(entry, broadcast: false);
            }
        }

        // 固定栏位上的诅咒：词条上面已经逐条还原了，这里只把标记补回去 ——
        // 不补的话它会变成一条普通词条（占名额、还能被替换掉），「固定栏位」就丢了。
        if (!string.IsNullOrEmpty(curseId))
        {
            for (var i = 0; i < _owned.Count; i++)
            {
                if (string.Equals(_owned[i].Id, curseId, StringComparison.Ordinal))
                {
                    MarkCurse(_owned[i]);
                    break;
                }
            }
        }

        RestoringProgress = false;

        HextechPlugin.Log.LogInfo(
            $"[海克斯] 已还原掉线前的成长：{_owned.Count} 条词条、{HextechState.FormatTokens(_tokens)} 枚代币。");

        // 让房主与队友那边的名册也更新成还原后的样子。
        PushState();
    }
}
