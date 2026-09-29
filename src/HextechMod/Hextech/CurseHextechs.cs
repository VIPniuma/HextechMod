using System;
using System.Collections.Generic;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace PeakModder.HextechMod;

/// <summary>
/// 第三批词条：四条「明摆着坑你」的负面（代价）词条 +「纯氧」这条会改造三选一的特殊机制。
/// <para>
/// 全部只从「诅咒固定栏位 / 行李箱抽奖」来（<c>negative: true</c>），
/// 阶段三选一原本不给负面 —— 只有拿到「纯氧」之后三选一才会掺负面（见 <see cref="RollChoiceCards"/>）。
/// </para>
/// <para>
/// 效果一律在**本地玩家自己的客户端**结算，和前面两批口径一致。
/// </para>
/// </summary>
internal static class CurseHextechs
{
    public const string HeavenForsakenId = "heaven_forsaken";
    public const string AcuteIronPoisoningId = "acute_iron_poisoning";
    public const string BlondeAdmirerId = "blonde_admirer";
    public const string PureOxygenId = "pure_oxygen";
    public const string LeftForDeadId = "left_for_dead";

    // ── 天之弃子 ────────────────────────────────────────────────
    /// <summary>抽到时加的诅咒值（百分点，10 = 10%）。</summary>
    public static float HeavenForsakenCurse = 10f;

    /// <summary>抽到时加的石化值（点；石化上限是 100 点，50 就是半条）。</summary>
    public static int HeavenForsakenPetrify = 50;

    // ── 急性铁中毒 ──────────────────────────────────────────────
    private const string IronArrowTimerKey = "iron_arrow";

    /// <summary>隔多久检查一次「箭还在不在」（不用每帧查，拔掉到补上差这点时间无所谓）。</summary>
    public static float IronArrowRecheckSeconds = 1.5f;

    // ── 黄毛追求者 ──────────────────────────────────────────────
    private const string AdmirerTimerKey = "blonde_admirer";

    /// <summary>离所有队友都超过这么远，才算「落单」。</summary>
    public static float AdmirerDistance = 50f;

    /// <summary>落单要持续这么久才招来僵尸（2 分钟）。</summary>
    public static float AdmirerDelaySeconds = 120f;

    // ── 纯氧 ────────────────────────────────────────────────────
    private const string OxygenTimerKey = "pure_oxygen";

    /// <summary>体力消耗 ×0.5。</summary>
    public static float OxygenStaminaMultiplier = 0.5f;

    /// <summary>每隔这么久加一次毒与睡眠（秒）。</summary>
    public static float OxygenStatusIntervalSeconds = 90f;

    /// <summary>每次加多少（状态条 0~100，1 = 1%）。</summary>
    public static float OxygenStatusPerTick = 1f;

    /// <summary>装死时每秒涨多少寒冷。</summary>
    public static float OxygenColdPerSecond = 2f;

    /// <summary>装死时每秒消多少睡眠（快速消退）。</summary>
    public static float OxygenDrowsyClearPerSecond = 25f;

    /// <summary>
    /// 站在这个半径内的光源旁边（点着的篝火 / 点着的提灯），毒与睡眠会加快消退。
    /// 拿在手上的提灯距离接近 0，所以也算子在内。
    /// </summary>
    public static float OxygenLightRadius = 6f;

    /// <summary>在光源旁每秒消退多少点（毒与睡眠各算各的）。</summary>
    public static float OxygenLightClearPerSecond = 10f;

    // ── 求生之路 ────────────────────────────────────────────────
    private const string LeftForDeadCountdown = "left_for_dead_countdown";

    /// <summary>抽到后隔多久刷怪（给玩家一点反应时间，3 秒）。</summary>
    public static float LeftForDeadDelaySeconds = 3f;

    /// <summary>刷几只蘑菇僵尸。</summary>
    public static int LeftForDeadZombieCount = 5;

    /// <summary>
    /// 刷在玩家身边几米。**就贴脸刷**（玩家 2026-09-23 明确要求：不要跑到场景里的刷新点去，
    /// 也不要隔 25 米，就 5 米）—— 刷新点现在只用来取僵尸预制体。
    /// </summary>
    public static float LeftForDeadSpawnDistance = 5f;

    /// <summary>移速加成（乘法，1.2 = +20%）。</summary>
    public static float LeftForDeadMoveSpeedPerStack = 1.2f;

    /// <summary>三选一变成几选几（纯氧：三选二）。</summary>
    public const int OxygenChoiceCount = 2;

    /// <summary>纯氧下，每张牌出负面的概率。</summary>
    public const float OxygenNegativeChance = 0.30f;

    /// <summary>纯氧下，一次最多刷出几个负面。</summary>
    public const int OxygenMaxNegatives = 2;

    /// <summary>一次表情动作算「正在演」的时长（用它判断是不是正躺着装死）。</summary>
    public static float EmoteActiveSeconds = 3f;

    /// <summary>
    /// 「装死」表情的真实名字（2026-09-20 从日志里实测到的：玩家做一次动作会打出
    /// <c>[海克斯] 播放表情：A_Scout_Emote_PlayDead</c>）。名字写在游戏资源里、代码读不到清单，
    /// 所以这里写死；关键字匹配只作兜底。
    /// </summary>
    private const string PlayDeadEmoteName = "A_Scout_Emote_PlayDead";

    // ── 表情动作（装死）────────────────────────────────────────
    /// <summary>最近一次播放的表情名（本地玩家自己的）。</summary>
    private static string? _lastEmoteName;

    private static float _lastEmoteAt = -999f;

    /// <summary>
    /// 一局结束（回机场）：清掉跨局残留的表情状态。
    /// 不清的话上一局最后做的动作会留着，新局头几秒身上有纯氧时会被误判成「正在装死」。
    /// </summary>
    public static void ResetRunState()
    {
        _lastEmoteName = null;
        _lastEmoteAt = -999f;
    }

    public static void Register()
    {
        RegisterHeavenForsaken();
        RegisterAcuteIronPoisoning();
        RegisterBlondeAdmirer();
        RegisterPureOxygen();
        RegisterLeftForDead();
    }

    // ── 天之弃子 ────────────────────────────────────────────────
    private static void RegisterHeavenForsaken()
    {
        Register(new ActionHextech(
            HeavenForsakenId,
            "天之弃子",
            $"抽到时立刻加 {HeavenForsakenPetrify} 点石化值与 {HeavenForsakenCurse:0}% 诅咒值。",
            HextechQuality.Bronze,
            ApplyHeavenForsaken,
            stackable: true,
            maxStacks: 1,
            negative: true));
    }

    private static void ApplyHeavenForsaken(HextechState state, int stacks)
    {
        // 掉线重连是「逐条重放词条」，而这是一次性副作用 ——
        // 重放时再吃一次就等于每掉一次线多加 50 点诅咒（2026-09-20 排查发现）。
        if (HextechState.RestoringProgress)
        {
            return;
        }

        AddStatus(state, CharacterAfflictions.STATUSTYPE.Curse, HeavenForsakenCurse);

        // 石化走整数入口（AddPetrify 收的就是 0~100 的点数），比用状态条换算更直白。
        var afflictions = state.Character.refs?.afflictions;

        if (afflictions != null)
        {
            afflictions.AddPetrify(HeavenForsakenPetrify);
        }
    }

    // ── 急性铁中毒 ──────────────────────────────────────────────
    /// <summary>
    /// 脑袋上扎一根箭（游戏自带的 AddArrow），拔掉就立刻再扎 ——
    /// 判定靠「身上还有没有 ArrowOnMe 组件」，没有就补一根。
    /// </summary>
    private static void RegisterAcuteIronPoisoning()
    {
        Register(new ActionHextech(
            AcuteIronPoisoningId,
            "急性铁中毒",
            "脑袋上自动扎一根箭；拔掉就立刻再扎一根。",
            HextechQuality.Bronze,
            (state, _) => StickArrow(state),
            onTick: TickIronArrow,
            stackable: true,
            maxStacks: 1,
            negative: true));
    }

    private static void TickIronArrow(HextechState state, int stacks, float deltaTime)
    {
        var character = state.Character;

        if (!character.IsLocal || character.data == null || character.data.dead)
        {
            return;
        }

        var timer = state.GetTimer(IronArrowTimerKey) + deltaTime;

        if (timer < IronArrowRecheckSeconds)
        {
            state.SetTimer(IronArrowTimerKey, timer);
            return;
        }

        // 只扣掉一个周期、余数留着，长局里不会越跑越偏。
        state.SetTimer(IronArrowTimerKey, timer - IronArrowRecheckSeconds);

        if (character.GetComponentsInChildren<Peak.ArrowOnMe>().Length == 0)
        {
            StickArrow(state);
        }
    }

    private static void StickArrow(HextechState state)
    {
        var character = state.Character;

        if (!character.IsLocal || character.refs?.afflictions == null)
        {
            return;
        }

        // 重连还原时先不扎：TickIronArrow 下一轮检查发现身上没箭，会自己补上（同一个入口）。
        if (HextechState.RestoringProgress)
        {
            return;
        }

        // 2026-09-20 修位置（玩家反馈）：原来「中心 + 0.55 米向上」是猜的 —— 姿势一变就会
        // 扎到屁股上；落在头上时箭杆又正对镜头挡视野。现在用真实骨骼锚在**肩胛骨之间**
        // （胸口偏上、脖子下方），箭杆朝后上方斜着出去：看得见、不糊脸、也不会跑偏。
        var refs = character.refs;
        var hip = refs.hip != null ? refs.hip.transform.position : character.Center;
        var headPos = refs.head != null ? refs.head.transform.position : hip + Vector3.up * 0.8f;
        var anchor = Vector3.Lerp(hip, headPos, 0.8f);

        // 朝向：角色面朝方向的反向（往后背出去）再抬一点；根物体朝向拿不到就退回竖直向上。
        var forward = character.transform != null ? character.transform.forward : Vector3.zero;
        var direction = forward.sqrMagnitude > 0.01f ? (Vector3.up * 0.5f - forward).normalized : Vector3.up;

        character.refs.afflictions.AddArrow(anchor, direction);
    }

    // ── 黄毛追求者 ──────────────────────────────────────────────
    /// <summary>
    /// 离所有队友都超过 50 米、且持续 2 分钟 → 招一只蘑菇僵尸。
    /// 同一时刻只保持一只：刷新点上还挂着活的就不重复刷；那只没了（被杀 / 消失）才可能再刷。
    /// </summary>
    private static void RegisterBlondeAdmirer()
    {
        Register(new ActionHextech(
            BlondeAdmirerId,
            "黄毛追求者",
            $"离队友 {AdmirerDistance:0} 米以外持续 {AdmirerDelaySeconds / 60f:0} 分钟，会招来一只蘑菇僵尸（同时最多一只；它死了才会再招）。",
            HextechQuality.Bronze,
            onTick: TickBlondeAdmirer,
            stackable: true,
            maxStacks: 1,
            negative: true));
    }

    private static void TickBlondeAdmirer(HextechState state, int stacks, float deltaTime)
    {
        var character = state.Character;

        if (!character.IsLocal || character.data == null || character.data.dead || HextechScene.InAirport)
        {
            state.SetTimer(AdmirerTimerKey, 0f);
            return;
        }

        // 单人游玩不刷：没进房间时场上只有自己，永远「离队友 50 米外」，
        // 不挡住的话每隔 2 分钟就会凭空冒一只僵尸（2026-09-20）。
        if (!PhotonNetwork.InRoom)
        {
            state.SetTimer(AdmirerTimerKey, 0f);
            return;
        }

        var alone = true;

        foreach (var other in Character.AllCharacters)
        {
            if (other == null || other == character || other.data == null || other.data.dead)
            {
                continue;
            }

            if (Vector3.Distance(character.Center, other.Center) <= AdmirerDistance)
            {
                alone = false;
                break;
            }
        }

        // 有人陪着就重新计时：必须「连续」落单满 2 分钟。
        if (!alone)
        {
            state.SetTimer(AdmirerTimerKey, 0f);
            return;
        }

        var timer = state.GetTimer(AdmirerTimerKey) + deltaTime;

        if (timer < AdmirerDelaySeconds)
        {
            state.SetTimer(AdmirerTimerKey, timer);
            return;
        }

        state.SetTimer(AdmirerTimerKey, 0f);
        SpawnAdmirerZombie(state);
    }

    /// <summary>
    /// 招蘑菇僵尸。僵尸是**网络对象**，必须由房主生成大家才都看得到 ——
    /// 非房主把请求转给房主（和「物资生成」「商店发货」同一套做法）。
    /// </summary>
    private static void SpawnAdmirerZombie(HextechState state)
    {
        if (PhotonNetwork.InRoom && !PhotonNetwork.IsMasterClient)
        {
            var view = state.Character?.refs?.view;

            if (view != null && view.ViewID > 0)
            {
                view.RPC(nameof(HextechState.HextechRPC_RequestAdmirerZombie), RpcTarget.MasterClient, view.ViewID);
            }

            return;
        }

        SpawnAdmirerZombieLocal();
    }

    /// <summary>房主侧（或单机）实际执行：全场扫一遍刷新点，确认没有活着的僵尸才招。</summary>
    internal static void SpawnAdmirerZombieLocal()
    {
        var spawners = UnityEngine.Object.FindObjectsByType<MushroomZombieSpawner>(FindObjectsSortMode.None);

        HextechPlugin.Log.LogInfo($"[海克斯] 黄毛追求者：场景里的蘑菇僵尸刷新点 = {spawners.Length} 个");

        if (spawners == null || spawners.Length == 0)
        {
            HextechPlugin.Log.LogWarning("[海克斯] 黄毛追求者：当前场景里没有蘑菇僵尸刷新点，这次没招成。");
            return;
        }

        // 「最多一只」是**全局**的，不是「每个刷新点一只」：
        // 先整场扫一遍，只要还有一只活着的就直接收手 ——
        // 否则 A 点还挂着、就会去 B 点再刷一只，长局里越叠越多（2026-09-20 排查发现）。
        foreach (var spawner in spawners)
        {
            if (spawner != null && spawner.spawnedZombie != null)
            {
                HextechPlugin.Log.LogInfo("[海克斯] 黄毛追求者：已有存活的僵尸，这次不招。");
                return;
            }
        }

        foreach (var spawner in spawners)
        {
            if (spawner == null)
            {
                continue;
            }

            if (!TrySpawnZombie(spawner))
            {
                continue;
            }

            HextechHud.Toast(Localization.T("黄毛追求者：蘑菇僵尸找上你了"));
            return;
        }
    }

    /// <summary>
    /// 在一个刷新点上试着刷一只僵尸。**坏点防护**（315514 日志）：预制体没配的刷新点会刷出
    /// 缺胳膊少腿的僵尸（游戏内部的 AwakeRoutine 空引用就是从坏点上来的），直接跳过；
    /// Spawn 本身抛异常也吞掉换下一个点，别让一只僵尸把整波怪带崩。
    /// </summary>
    private static bool TrySpawnZombie(MushroomZombieSpawner spawner)
    {
        if (spawner.mushroomZombiePrefab == null)
        {
            HextechPlugin.Log.LogInfo($"[海克斯] 跳过没配预制体的蘑菇僵尸刷新点：{spawner.name}");
            return false;
        }

        try
        {
            spawner.Spawn();

            var zombie = spawner.spawnedZombie;

            if (zombie == null)
            {
                HextechPlugin.Log.LogWarning($"[海克斯] 刷新点 {spawner.name} 调了 Spawn 但没有产出僵尸（点不 Ready？）");
                return false;
            }

            // 关键：刷出来不等于场上看得见。游戏靠 ZombieManager 的 EnableZombie 协程
            // （联机再配 RPC_EnableZombie）才把僵尸真正激活；少了这一步就是
            // 「日志说刷了、场上一只都没有」（2026-09-23「你跑不过我」反馈）。
            ActivateZombie(zombie);
            HextechPlugin.Log.LogInfo($"[海克斯] 已在刷新点 {spawner.name}（{spawner.transform.position}）刷出蘑菇僵尸");
            return true;
        }
        catch (Exception exception)
        {
            HextechPlugin.Log.LogWarning($"[海克斯] 刷新点 {spawner.name} 生成僵尸失败：{exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// 把一只已经实例化出来的僵尸真正放上场：登记进 <see cref="ZombieManager"/>，
    /// 没激活的就走游戏自己的 <c>EnableZombie</c> 协程激活（它内部负责联机同步）。
    /// 重复登记会污染 <c>zombies</c> 列表，所以先查一遍。
    /// </summary>
    private static void ActivateZombie(MushroomZombie zombie)
    {
        try
        {
            var manager = ZombieManager.Instance;

            if (manager == null)
            {
                HextechPlugin.Log.LogWarning("[海克斯] ZombieManager 还没起来，僵尸只能就地放着。");
                return;
            }

            var list = manager.zombies;

            if (list != null && !list.Contains(zombie))
            {
                manager.RegisterZombie(zombie);
            }

            HextechPlugin.Log.LogInfo(
                $"[海克斯] 僵尸登记完成（场上 {list?.Count ?? 0} / 上限 {manager.maxActiveZombies}），激活状态 = {zombie.gameObject.activeSelf}");

            // 已经激活的就不重复走一遍协程，免得把游戏自己的激活流程跑两遍。
            if (!zombie.gameObject.activeSelf)
            {
                manager.StartCoroutine(manager.EnableZombie(zombie));
            }
        }
        catch (Exception exception)
        {
            HextechPlugin.Log.LogWarning($"[海克斯] 激活僵尸失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 兜底：场景里没有可用刷新点时，直接按玩家的方位放几只（保证这条词条说到做到）。
    /// 僵尸的联网同步和上面一样靠 RegisterZombie + EnableZombie 协程。
    /// </summary>
    private static int SpawnZombiesNearPlayer(MushroomZombie prefab, int count)
    {
        var local = Character.localCharacter;

        if (local == null)
        {
            return 0;
        }

        var made = 0;

        for (var i = 0; i < count; i++)
        {
            try
            {
                var angle = (i / (float)count) * Mathf.PI * 2f;

                // 就在身边：5 米一整圈铺开，奇偶错开 1 米免得几只叠在一个点上。
                var distance = LeftForDeadSpawnDistance + (i % 2 == 0 ? 0f : 1f);
                var offset = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * distance;
                var at = local.transform.position + offset + (Vector3.up * 0.5f);

                var zombie = UnityEngine.Object.Instantiate(prefab, at, Quaternion.identity);

                if (zombie == null)
                {
                    continue;
                }

                ActivateZombie(zombie);
                made++;
            }
            catch (Exception exception)
            {
                HextechPlugin.Log.LogWarning($"[海克斯] 兜底放置第 {i + 1} 只僵尸失败：{exception.Message}");
            }
        }

        return made;
    }

    // ── 纯氧 ────────────────────────────────────────────────────
    /// <summary>
    /// 体力消耗减半；每 60 秒 +1% 毒 +1% 睡眠，且这两项**不再自然消退**；
    /// 篝火 / 灯笼能消睡眠；装死可以快速消睡眠，代价是每秒 +2 寒冷。
    /// 另外拿到它之后，三选一变成**三选二**，牌池掺进负面（每张 30%，一次最多 2 个）。
    /// </summary>
    private static void RegisterPureOxygen()
    {
        Register(new ActionHextech(
            PureOxygenId,
            "纯氧",
            $"体力消耗 -50%，但每 {OxygenStatusIntervalSeconds:0} 秒 +1% 毒与 +1% 睡眠（毒会正常消退，睡眠不会；"
            + "篝火 / 灯笼能消睡眠，装死消得快但每秒 +2 寒冷）。拿到后三选一变三选二，牌池里会掺负面。",
            HextechQuality.Bronze,
            (state, _) => ApplyOxygenStamina(state),
            onTick: TickPureOxygen,
            stackable: true,
            maxStacks: 1,
            negative: true));
    }

    private static void ApplyOxygenStamina(HextechState state)
    {
        // 和本文件其它入口一致：只动本地自己的角色，别把队友的数值也乘一遍。
        if (!state.Character.IsLocal)
        {
            return;
        }

        var refs = state.Character.refs;

        if (refs == null)
        {
            return;
        }

        if (refs.movement != null)
        {
            refs.movement.sprintStaminaUsage *= OxygenStaminaMultiplier;
        }

        if (refs.climbing != null)
        {
            refs.climbing.maxStaminaUsage *= OxygenStaminaMultiplier;
        }

        if (refs.vineClimbing != null)
        {
            refs.vineClimbing.staminaUsage *= OxygenStaminaMultiplier;
        }
    }

    private static void TickPureOxygen(HextechState state, int stacks, float deltaTime)
    {
        var character = state.Character;

        if (!character.IsLocal || character.refs?.afflictions == null || character.data == null || character.data.dead)
        {
            return;
        }

        var afflictions = character.refs.afflictions;
        var playingDead = IsPlayingDeadEmote();

        // 光源旁边（点着的篝火 / 提灯）：纯氧攒下的毒与睡眠都会加快消退。
        var nearLight = IsNearLightSource(character);

        // 睡眠默认**不自然消退**（纯氧的代价，0 = 不消退）。
        // 装死、或者站在光源旁，才把消退速度临时拉高 —— 走游戏自己的消退通道，
        // 比往 AddStatus 里塞负数稳（负数实测没生效）。
        var drowsyDecay = 0f;

        if (playingDead)
        {
            drowsyDecay = Points(OxygenDrowsyClearPerSecond);
        }
        else if (nearLight)
        {
            drowsyDecay = Points(OxygenLightClearPerSecond);
        }

        afflictions.drowsyReductionPerSecond = drowsyDecay;

        // 中毒平时按正常速度消退（玩家要求），只有站在光源旁才加快。
        if (nearLight)
        {
            afflictions.poisonReductionPerSecond = Points(OxygenLightClearPerSecond);
        }

        var timer = state.GetTimer(OxygenTimerKey) + deltaTime;

        if (timer < OxygenStatusIntervalSeconds)
        {
            state.SetTimer(OxygenTimerKey, timer);
        }
        else
        {
            state.SetTimer(OxygenTimerKey, timer - OxygenStatusIntervalSeconds);
            AddStatus(state, CharacterAfflictions.STATUSTYPE.Poison, OxygenStatusPerTick);
            AddStatus(state, CharacterAfflictions.STATUSTYPE.Drowsy, OxygenStatusPerTick);
        }

        // 装死的代价：寒冷每秒 +2 点。
        if (playingDead)
        {
            AddStatus(state, CharacterAfflictions.STATUSTYPE.Cold, OxygenColdPerSecond * deltaTime);
        }
    }

    /// <summary>
    /// 身边有没有光源：点着的篝火，或者点着的提灯（拿在手上也算，那时距离接近 0）。
    /// </summary>
    private static bool IsNearLightSource(Character character)
    {
        // 篝火那边 AdvancedHextechs 已经做好了 2 秒缓存，直接用，别再自己全扫一遍。
        if (AdvancedHextechs.IsNearLitCampfire(character, OxygenLightRadius))
        {
            return true;
        }

        foreach (var lantern in Lanterns())
        {
            if (lantern == null || !lantern.lit || lantern.gameObject.scene != character.gameObject.scene)
            {
                continue;
            }

            if (Vector3.Distance(character.Center, lantern.transform.position) <= OxygenLightRadius)
            {
                return true;
            }
        }

        return false;
    }

    private static Lantern[] _lanterns = Array.Empty<Lantern>();
    private static float _lanternsRefreshedAt = -999f;

    /// <summary>
    /// 场景里的提灯列表。找提灯要全场扫一遍，而 Tick 是每帧跑的 —— 缓存 2 秒（和篝火那份同口径）。
    /// </summary>
    private static Lantern[] Lanterns()
    {
        if (_lanterns.Length == 0 || Time.unscaledTime > _lanternsRefreshedAt)
        {
            _lanternsRefreshedAt = Time.unscaledTime + 2f;
            _lanterns = UnityEngine.Object.FindObjectsByType<Lantern>(FindObjectsSortMode.None);
        }

        return _lanterns;
    }

    /// <summary>是不是正在演「装死」这个表情。</summary>
    private static bool IsPlayingDeadEmote()
    {
        if (Time.time - _lastEmoteAt > EmoteActiveSeconds || string.IsNullOrEmpty(_lastEmoteName))
        {
            return false;
        }

        var name = _lastEmoteName!;

        return string.Equals(name, PlayDeadEmoteName, StringComparison.OrdinalIgnoreCase)
               || name.IndexOf("dead", StringComparison.OrdinalIgnoreCase) >= 0
               || name.IndexOf("装死", StringComparison.Ordinal) >= 0;
    }

    // ── 求生之路 ────────────────────────────────────────────────
    /// <summary>
    /// 「移速 +20%」换「3 秒后 5 只蘑菇僵尸 + 1 个童子军领队」。
    /// 僵尸和领队都是网络对象，和黄毛追求者一样由房主生成。
    /// </summary>
    private static void RegisterLeftForDead()
    {
        Register(new ActionHextech(
            LeftForDeadId,
            "你跑不过我你信不信？",
            $"移速 +20%。抽到 {LeftForDeadDelaySeconds:0} 秒后，在你身边刷出 {LeftForDeadZombieCount} 只蘑菇僵尸和 1 个童子军领队。",
            HextechQuality.Bronze,
            ApplyLeftForDead,
            onTick: TickLeftForDead,
            stackable: true,
            maxStacks: 1,
            negative: true));
    }

    private static void ApplyLeftForDead(HextechState state, int stacks)
    {
        var character = state.Character;

        if (!character.IsLocal || character.refs?.movement == null)
        {
            return;
        }

        // 移速是乘法改基准，重连还原时照常重放 —— 这部分要回来。
        character.refs.movement.movementForce *= LeftForDeadMoveSpeedPerStack;

        // 刷怪是一次性副作用，重连还原时不能再来一轮（重放期间不开始倒计时）。
        if (HextechState.RestoringProgress)
        {
            return;
        }

        // 计时开始。0.0001 与「没抽到时的 0」「刷完后的 -1」区分开。
        state.SetTimer(LeftForDeadCountdown, 0.0001f);
    }

    private static void TickLeftForDead(HextechState state, int stacks, float deltaTime)
    {
        var character = state.Character;

        if (!character.IsLocal || character.data == null || character.data.dead || HextechScene.InAirport)
        {
            return;
        }

        var timer = state.GetTimer(LeftForDeadCountdown);

        // 0 = 没抽到这条；负数 = 这一局已经刷过了。
        if (timer <= 0f)
        {
            return;
        }

        timer += deltaTime;

        if (timer < LeftForDeadDelaySeconds)
        {
            state.SetTimer(LeftForDeadCountdown, timer);
            return;
        }

        state.SetTimer(LeftForDeadCountdown, -1f);
        SpawnLeftForDeadHorde(state);
    }

    private static void SpawnLeftForDeadHorde(HextechState state)
    {
        if (PhotonNetwork.InRoom && !PhotonNetwork.IsMasterClient)
        {
            var view = state.Character?.refs?.view;

            if (view != null && view.ViewID > 0)
            {
                view.RPC(nameof(HextechState.HextechRPC_RequestLeftForDeadHorde), RpcTarget.MasterClient, view.ViewID);
            }

            return;
        }

        SpawnLeftForDeadHordeLocal();
    }

    /// <summary>房主侧（或单机）实际执行：5 只蘑菇僵尸 + 召唤童子军领队。</summary>
    internal static void SpawnLeftForDeadHordeLocal()
    {
        var spawners = UnityEngine.Object.FindObjectsByType<MushroomZombieSpawner>(FindObjectsSortMode.None);

        // 刷新点只在特定场景里才有 —— 没有的话一只都刷不出来，必须能从日志看出来。
        HextechPlugin.Log.LogInfo($"[海克斯] 求生之路：场景里的蘑菇僵尸刷新点 = {spawners?.Length ?? 0} 个");

        var spawned = 0;
        MushroomZombie? prefab = null;

        if (spawners != null)
        {
            foreach (var spawner in spawners)
            {
                if (spawner != null && spawner.mushroomZombiePrefab != null)
                {
                    // 刷新点在这里**只当僵尸预制体的来源**：玩家要的是刷在身边，不是刷在场景点上。
                    prefab = spawner.mushroomZombiePrefab;
                    break;
                }
            }
        }

        // 主路径：**直接刷在玩家身边**（5 米一圈），场景刷新点一概不管。
        if (prefab != null)
        {
            var made = SpawnZombiesNearPlayer(prefab, LeftForDeadZombieCount);
            HextechPlugin.Log.LogInfo($"[海克斯] 求生之路：在玩家身边 {LeftForDeadSpawnDistance:0} 米刷出 {made} 只。");
            spawned += made;
        }

        // 身边放不出来（没拿到预制体 / 实例化失败）才退回场景刷新点，总比一只都没有强。
        if (spawned == 0 && spawners != null)
        {
            foreach (var spawner in spawners)
            {
                if (spawner == null)
                {
                    continue;
                }

                if (spawned >= LeftForDeadZombieCount)
                {
                    break;
                }

                if (spawner.spawnedZombie != null)
                {
                    // 这个点上已经有僵尸，换下一个点（同一点重复 Spawn 的行为未知，不赌）。
                    continue;
                }

                if (spawner.mushroomZombiePrefab == null)
                {
                    HextechPlugin.Log.LogInfo($"[海克斯] 求生之路：跳过没配预制体的刷新点 {spawner.name}");
                    continue;
                }

                if (!spawner.gameObject.activeInHierarchy)
                {
                    HextechPlugin.Log.LogInfo($"[海克斯] 求生之路：跳过未启用的刷新点 {spawner.name}（可能被随机剔除）");
                    continue;
                }

                // 游戏自己的前置判断：不 Ready 的点硬刷会静默失败 / 刷出残缺僵尸（315514 那类空引用）。
                if (!spawner.ReadyToSpawn())
                {
                    HextechPlugin.Log.LogInfo($"[海克斯] 求生之路：刷新点 {spawner.name} ReadyToSpawn() = false，跳过");
                    continue;
                }

                if (!TrySpawnZombie(spawner))
                {
                    continue;
                }

                spawned++;
            }
        }

        HextechPlugin.Log.LogInfo($"[海克斯] 求生之路：实际刷出 {spawned} 只蘑菇僵尸。");

        HextechHud.Toast(spawned > 0
            ? Localization.T("你跑不过我你信不信？：{0} 只蘑菇僵尸与童子军领队找上了你", spawned)
            : Localization.T("你跑不过我你信不信？：这个场景刷不出僵尸，只来了童子军领队"));

        SummonScoutmaster();
    }

    /// <summary>
    /// 召唤童子军领队。走游戏自己的 <see cref="ScoutmasterSpawner.SpawnScoutmaster"/>（「掉队惩罚」的正规入口）。
    /// <para>
    /// 原来「凭空造 <see cref="Action_CallScoutmaster"/> 再 RunAction」**必炸**（315514 日志实锤）：
    /// 动作基类的 character 属性从 item 上取，凭空的实例没有 item，直接空引用。
    /// Spawner 找不到时优雅降级 —— 只少一个领队，五只僵尸照刷。
    /// </para>
    /// </summary>
    private static void SummonScoutmaster()
    {
        try
        {
            var spawner = UnityEngine.Object.FindFirstObjectByType<ScoutmasterSpawner>();

            if (spawner == null)
            {
                HextechPlugin.Log.LogWarning("[海克斯] 场景里没有 ScoutmasterSpawner，领队没招出来（僵尸照刷）");
                return;
            }

            spawner.SpawnScoutmaster();
            HextechPlugin.Log.LogInfo("[海克斯] 已通过 ScoutmasterSpawner 召唤童子军领队");
        }
        catch (Exception exception)
        {
            HextechPlugin.Log.LogWarning($"[海克斯] 召唤童子军领队失败（僵尸照刷）：{exception.Message}");
        }
    }

    // ── 纯氧的三选二（牌池掺负面）──────────────────────────────
    /// <summary>
    /// 拿到「纯氧」时用它替代普通的三选一抽牌：变成 2 张，每张有 30% 概率是负面，一次最多 2 个负面。
    /// 没有纯氧返回 null，调用方照旧走普通抽牌。
    /// </summary>
    public static List<HextechEntry>? RollChoiceCards(HextechState state, System.Random random)
    {
        if (state.StackOfId(PureOxygenId) <= 0)
        {
            return null;
        }

        var cards = new List<HextechEntry>();
        var negatives = 0;

        for (var i = 0; i < OxygenChoiceCount; i++)
        {
            var entry = RollOneChoiceCard(state, random, cards, ref negatives);

            if (entry != null)
            {
                cards.Add(entry);
            }
        }

        return cards.Count > 0 ? cards : null;
    }

    /// <summary>刷新单张牌（面板里点刷新）时用，同样遵守「30% 负面、最多 2 个」。</summary>
    public static HextechEntry? RollOneChoiceCard(
        HextechState state,
        System.Random random,
        ICollection<HextechEntry> exclude)
    {
        if (state.StackOfId(PureOxygenId) <= 0)
        {
            return null;
        }

        var negatives = CountNegatives(state);
        return RollOneChoiceCard(state, random, exclude, ref negatives);
    }

    private static HextechEntry? RollOneChoiceCard(
        HextechState state,
        System.Random random,
        ICollection<HextechEntry> exclude,
        ref int negatives)
    {
        var wantNegative = negatives < OxygenMaxNegatives && random.NextDouble() < OxygenNegativeChance;

        var entry = HextechRegistry.RollOne(state, random, allowNegative: true, exclude: exclude, negative: wantNegative);

        // 想要的那一半被禁空 / 抽满了，就退到另一半，别让这张牌空着。
        if (entry == null)
        {
            entry = HextechRegistry.RollOne(state, random, allowNegative: true, exclude: exclude, negative: !wantNegative);
        }

        if (entry != null && entry.Negative)
        {
            negatives++;
        }

        return entry;
    }

    private static int CountNegatives(HextechState state)
    {
        var count = 0;

        for (var i = 0; i < state.Owned.Count; i++)
        {
            if (state.Owned[i].Negative)
            {
                count++;
            }
        }

        return count;
    }

    // ── 辅助 ────────────────────────────────────────────────────
    /// <summary>
    /// 状态条的口径是 **0~100 点**，而 <c>CharacterAfflictions.AddStatus</c> 收的是 **0~1 的小数**
    /// （原版对 Petrify 就是「×100 取整」，1.0 = 100 点 = 满）。
    /// 所以外面一律按「点」写，进 AddStatus 之前用它换算 —— 传 1 会被当成 100。
    /// </summary>
    private static float Points(float points) => points / 100f;

    /// <summary>往状态条上加 <paramref name="points"/> 点（内部换算成 0~1）。</summary>
    private static void AddStatus(HextechState state, CharacterAfflictions.STATUSTYPE type, float points)
    {
        AddStatusFraction(state, type, Points(points));
    }

    private static void AddStatusFraction(HextechState state, CharacterAfflictions.STATUSTYPE type, float fraction)
    {
        var character = state.Character;

        if (!character.IsLocal || character.refs?.afflictions == null)
        {
            return;
        }

        character.refs.afflictions.AddStatus(type, fraction);
    }

    private static void Register(HextechEntry entry) => HextechRegistry.Register(entry);

    /// <summary>
    /// 记下最近一次播放的表情名 —— 「纯氧」要靠它判断玩家是不是在装死。
    /// 顺手把名字打进日志：表情名写在资源里、代码读不到清单，
    /// 玩家做一次动作就能从 BepInEx 日志里看到真实名字，方便对不上时改判定。
    /// </summary>
    [HarmonyPatch(typeof(CharacterAnimations), nameof(CharacterAnimations.PlayEmote))]
    private static class PlayEmotePatch
    {
        [HarmonyPostfix]
        private static void Postfix(string emoteName)
        {
            _lastEmoteName = emoteName;
            _lastEmoteAt = Time.time;

            HextechPlugin.Log.LogInfo($"[海克斯] 播放表情：{emoteName}");
        }
    }
}
