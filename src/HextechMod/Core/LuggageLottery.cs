using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace PeakModder.HextechMod;

/// <summary>
/// 行李箱抽奖。原版开箱会按箱子的出生点刷出 N 件物资，
/// 这里改成「抽 N 次奖」，每次要么拿到原本该刷的那件物资，要么随机到一个海克斯。
/// <para>
/// 抽奖由开箱者本人判定（所以联机时各抽各的海克斯），
/// 抽到的物资则交给房主在同样的位置生成，大家都能看到、都能捡。
/// 开箱抽到的海克斯是「抽到什么就是什么」，可能带负面。
/// </para>
/// <para>
/// 只有海克斯会播抽奖动画：物资和代币当场结算、当场到手，不用等滚动条走完
/// （一箱动不动抽十来件，件件都看一遍动画太拖时间）。
/// </para>
/// </summary>
internal static class LuggageLottery
{
    private static readonly MethodInfo? GetSpawnSpotsMethod =
        AccessTools.Method(typeof(Spawner), "GetSpawnSpots");

    private static readonly MethodInfo? GetObjectsToSpawnMethod =
        AccessTools.Method(typeof(Spawner), "GetObjectsToSpawn");

    private static readonly System.Random Rng = new();

    /// <summary>一次抽奖最多给多少枚代币。写死上限，配置项只能往小调。</summary>
    private const int MaxTokenReward = 20;

    /// <summary>已经抽过奖的箱子（按 PhotonView ID），防止同一次开箱被重复判定。</summary>
    private static readonly HashSet<int> Rolled = new();

    /// <summary>一局结束时清掉记录，避免下一局复用到同一个 ViewID。</summary>
    public static void Reset()
    {
        Rolled.Clear();
    }

    /// <summary>由 <see cref="HextechPatches"/> 在开箱者本地调用。</summary>
    public static void Run(Luggage luggage, Character opener)
    {
        if (luggage == null || opener == null || !opener.IsLocal)
        {
            return;
        }

        var state = HextechState.Get(opener);

        if (state == null)
        {
            return;
        }

        var luggageView = luggage.GetComponent<PhotonView>();
        var key = luggageView == null ? luggage.GetInstanceID() : luggageView.ViewID;

        if (!Rolled.Add(key))
        {
            return;
        }

        var spots = GetSpawnSpots(luggage);
        var draws = spots.Count + state.StackOfId(DefaultHextechs.LuckyLuggageId);

        if (draws <= 0 || spots.Count == 0)
        {
            return;
        }

        var prefabs = GetObjectsToSpawn(luggage, draws);
        var positiveChance = Mathf.Clamp01(ModConfig.LuggagePositiveChance.Value);
        var negativeChance = Mathf.Clamp01(ModConfig.LuggageNegativeChance.Value);
        var hextechChance = positiveChance + negativeChance;
        var tokenChance = Mathf.Clamp01(ModConfig.LuggageTokenChance.Value);
        var tokenMax = Mathf.Clamp(ModConfig.LuggageTokenMax.Value, 1, MaxTokenReward);

        var names = new List<string>();
        var positions = new List<Vector3>();
        var gained = new List<string>();
        var shows = new List<RollShow>();
        var hextechCount = 0;
        var tokenTotal = 0;

        // 只有海克斯会播动画，所以陪跑的池子也只建这一个。
        var hextechPool = BuildHextechPool();

        for (var i = 0; i < draws; i++)
        {
            var header = $"行李箱抽奖 · 第 {i + 1} / {draws} 次";
            var roll = Rng.NextDouble();

            // 正面 / 负面是两次独立判定（概率各由一个配置项给），抽中的那一个再按品质权重从自己那半边池子里挑。
            // 负面池被禁空或抽满时退一格给正面，不至于让这几次直接掉进物资里。
            HextechEntry? entry = null;

            if (roll < positiveChance)
            {
                entry = HextechRegistry.RollOne(state, Rng, allowNegative: true, negative: false);
            }
            else if (roll < hextechChance)
            {
                entry = HextechRegistry.RollOne(state, Rng, allowNegative: true, negative: true)
                        ?? HextechRegistry.RollOne(state, Rng, allowNegative: true, negative: false);
            }

            if (entry != null)
            {
                // 结果先结算，动画只是演出，玩家中途关掉也不影响实际收益。
                // 撞上拿取上限时转替换队列，抽卡动画结束后弹替换面板（2026-09-14）。
                HextechManager.Instance?.AcquireOrQueueReplacement(state, entry);
                hextechCount++;
                gained.Add(entry.Negative ? $"{entry.Title}（负面）" : entry.Title);
                shows.Add(new RollShow(header, hextechPool, ToSlot(entry, state.StackOf(entry))));
                continue;
            }

            // 代币不播动画：先结算，最后跟物资一起写进那条播报里。
            if (roll < hextechChance + tokenChance)
            {
                var amount = Rng.Next(1, tokenMax + 1);
                state.AddTokens(amount);
                tokenTotal += amount;
                gained.Add($"代币 ×{amount}");
                continue;
            }

            var prefab = i < prefabs.Count ? prefabs[i] : null;

            if (prefab == null)
            {
                continue;
            }

            // 物资也不播动画：一箱动不动抽十来件，件件都看一遍滚动条太熬人，
            // 而且东西就掉在箱子原位、抬头就看见，播动画纯属拖时间。
            names.Add(prefab.name);
            positions.Add(spots[i % spots.Count].position);
        }

        if (names.Count > 0)
        {
            HextechSpawning.RequestSpawn(luggage, names.ToArray(), positions.ToArray(), luggage.isKinematic);
        }

        var rollUi = HextechRoll.Instance;

        if (rollUi != null && shows.Count > 0)
        {
            rollUi.PlayQueue(shows, () => Announce(draws, names.Count, hextechCount, tokenTotal, gained));
            return;
        }

        Announce(draws, names.Count, hextechCount, tokenTotal, gained);
    }

    /// <summary>
    /// 结果格子除了名字，还带上「当前层数下的实际效果」——
    /// 抽奖停下时一眼就能看出抽到了什么、它到底干什么，不用再去翻 HUD。
    /// 陪跑的装饰格子传 0，省掉没必要的字符串拼接。
    /// </summary>
    private static RollSlot ToSlot(HextechEntry entry, int stacks = 0)
    {
        var detail = stacks > 0 ? entry.Summary(stacks) : null;

        return entry.Negative
            ? new RollSlot(entry.Title, "代价", UiFactory.Danger, null, detail)
            : new RollSlot(entry.Title, UiFactory.QualityName(entry.Quality), UiFactory.QualityColor(entry.Quality), null, detail);
    }

    private static List<RollSlot> BuildHextechPool()
    {
        var pool = new List<RollSlot>();

        foreach (var entry in HextechRegistry.All)
        {
            pool.Add(ToSlot(entry));
        }

        return pool;
    }

    private static List<Transform> GetSpawnSpots(Spawner spawner)
    {
        if (GetSpawnSpotsMethod == null)
        {
            return new List<Transform>();
        }

        return GetSpawnSpotsMethod.Invoke(spawner, null) as List<Transform> ?? new List<Transform>();
    }

    private static List<GameObject> GetObjectsToSpawn(Spawner spawner, int count)
    {
        if (GetObjectsToSpawnMethod == null)
        {
            return new List<GameObject>();
        }

        return GetObjectsToSpawnMethod.Invoke(spawner, new object[] { count, spawner.canRepeatSpawns })
                   as List<GameObject>
               ?? new List<GameObject>();
    }

    private static void Announce(int draws, int items, int hextechs, int tokens, List<string> gained)
    {
        var parts = new List<string> { $"物资 {items} 件" };

        if (hextechs > 0)
        {
            parts.Add($"海克斯 {hextechs} 个");
        }

        if (tokens > 0)
        {
            parts.Add($"代币 {tokens} 枚");
        }

        var summary = $"开箱抽奖 {draws} 次：{string.Join(" · ", parts)}";

        HextechHud.Toast(gained.Count > 0 ? $"{summary} → {string.Join("、", gained)}" : summary);
    }
}
