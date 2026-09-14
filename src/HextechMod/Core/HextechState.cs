using System;
using System.Collections.Generic;
using System.Globalization;
using Peak.Afflictions;
using Photon.Pun;
using UnityEngine;

namespace PeakModder.HextechMod;

/// <summary>
/// 挂在每个 <see cref="Character"/> 上的海克斯状态：已获得的词条、层数、能量与技能。
/// 词条效果由「角色拥有者自己的客户端」应用，其他客户端只保存一份名册用于显示。
/// 开局什么都不带，所有词条与主动技能都只能靠点燃阶段篝火抽海克斯拿到。
/// </summary>
public sealed class HextechState : MonoBehaviour
{
    public const float MaxEnergy = 100f;
    public const float EnergyRegenPerSecond = 7f;

    private static readonly List<HextechState> _instances = new();

    public static IReadOnlyList<HextechState> Instances => _instances;

    public Character Character { get; private set; } = null!;

    private readonly List<HextechEntry> _owned = new();
    private readonly List<int> _stacks = new();
    private readonly List<SkillId> _skills = new();
    private readonly Dictionary<string, float> _timers = new();
    private readonly HashSet<int> _rewardedCampfires = new();

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

    // 商店代币是**连续累积**的浮点数，不再是「每满一个间隔 +1」的整数计数器：
    // 速率 1.5/分 就等于每秒 +0.025，HUD 显示到一位小数，玩家能看见它在涨。
    // 小数部分只是「还没攒够一整枚」，消费仍按整数价扣，扣完剩下的零头留在账上。
    private float _tokens;
    private bool _baselineCaptured;

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

    public float Energy { get; private set; } = MaxEnergy;

    public float CooldownRemaining { get; private set; }

    public float EnergyRatio => Mathf.Clamp01(Energy / MaxEnergy);

    /// <summary>商店代币。局内连续累积（带小数），回机场清零。</summary>
    public float Tokens => _tokens;

    /// <summary>代币 ×10 向下取整 —— UI 用它判断「显示的那一位小数变没变」，免得每帧重写字符串。</summary>
    public int TokensTenths => Mathf.FloorToInt(_tokens * 10f);

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

    public SkillId? CurrentSkill =>
        _skills.Count == 0 ? null : _skills[_skillIndex % _skills.Count];

    private void Awake()
    {
        Character = GetComponent<Character>();
        _instances.Add(this);
    }

    private void OnDestroy()
    {
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
        return _rewardedCampfires.Add(segment);
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
        if (cost <= 0 || _tokens < cost)
        {
            return false;
        }

        _tokens -= cost;
        return true;
    }

    /// <summary>发代币（拾荒者 +1、行李箱抽到、中途加入补足都走这里）。<paramref name="amount"/> 可以带小数。</summary>
    public void AddTokens(float amount)
    {
        _tokens = Mathf.Max(0f, _tokens + amount);
    }

    /// <summary>
    /// 「拾荒者」本局靠捡东西发出的代币数（回机场清零），**只用于提示，不封顶**。
    /// 同一件物品被丢掉再捡起来不会重复发 —— 去重和「烫手 / 机能零食」
    /// 共用 <see cref="TryMarkPickupSettled(Item)"/>（按物品实例 GUID 记），这里只管发币。
    /// </summary>
    public int ScavengerTokensEarned { get; private set; }

    /// <summary>「拾荒者」：发一枚代币并提示。不设每局上限，捡多少给多少。</summary>
    public void AwardScavengerToken()
    {
        ScavengerTokensEarned++;
        AddTokens(1);
        RecordEffect(DefaultHextechs.ScavengerId, 1f);

        HextechHud.Toast($"拾荒者：+1 商店代币（本局 {ScavengerTokensEarned} 枚）");
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

    public void RefillEnergy()
    {
        Energy = MaxEnergy;
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

        if (_skills.Count > 0)
        {
            Energy = Mathf.Min(MaxEnergy, Energy + EnergyRegenPerSecond * deltaTime);
        }

        AccrueTokens(deltaTime);

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
            HextechHud.Toast($"商店代币 +{whole - before}（共 {whole} 枚）· 按 {ModConfig.ShopKey.Value} 打开商店");
        }
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
        HextechHud.Toast($"死而复生：把 {targetName} 拉到了你面前（剩余 {ReviveCharges} 次）");
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
        // 不然会白扣一次能量和石化，手还是原来那只。
        if (!definition.CanCast(Character))
        {
            if (!string.IsNullOrEmpty(definition.BlockedHint))
            {
                HextechHud.Toast(definition.BlockedHint);
            }

            return false;
        }

        if (Energy < definition.EnergyCost)
        {
            return false;
        }

        Energy -= definition.EnergyCost;

        // 延后冷却的技能由它自己在「用掉」的那一刻调 StartCooldown。
        if (!definition.CooldownOnConsume)
        {
            CooldownRemaining = definition.Cooldown;
        }

        definition.Cast(Character);
        return true;
    }

    /// <summary>
    /// 一局结束（回到机场）时清空：词条、层数、技能、能量全部还原，
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
        _rewardedCampfires.Clear();
        _settledItemInstances.Clear();
        _purchases.Clear();
        _skillIndex = 0;
        Energy = MaxEnergy;
        CooldownRemaining = 0f;
        _tokens = 0f;
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

    public void PushState()
    {
        var view = Character == null ? null : Character.refs.view;

        if (view == null || !view.IsMine || !PhotonNetwork.InRoom)
        {
            return;
        }

        var ids = new string[_owned.Count];
        var stacks = new int[_owned.Count];

        for (var i = 0; i < _owned.Count; i++)
        {
            ids[i] = _owned[i].Id;
            stacks[i] = _stacks[i];
        }

        view.RPC(nameof(HextechRPC_SyncOwned), RpcTarget.Others, ids, stacks);
    }

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
    public void HextechRPC_SyncOwned(string[] ids, int[] stacks)
    {
        _owned.Clear();
        _stacks.Clear();
        _effectCounts.Clear();
        _effectAmounts.Clear();
        _activeSeconds.Clear();
        _activeFrames.Clear();

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
    }
}
