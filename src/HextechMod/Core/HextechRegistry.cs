using System;
using System.Collections.Generic;

namespace PeakModder.HextechMod;

/// <summary>所有海克斯词条的注册表，负责抽取 3 选 1 的候选。</summary>
public static class HextechRegistry
{
    private static readonly List<HextechEntry> _all = new();
    private static readonly Dictionary<string, HextechEntry> _byId = new(StringComparer.Ordinal);

    public static IReadOnlyList<HextechEntry> All => _all;

    static HextechRegistry()
    {
        DefaultHextechs.Register();
    }

    public static void Register(HextechEntry entry)
    {
        if (entry == null)
        {
            return;
        }

        _all.Add(entry);
        _byId[entry.Id] = entry;
    }

    public static HextechEntry? Find(string id)
    {
        return _byId.TryGetValue(id, out var entry) ? entry : null;
    }

    /// <summary>
    /// 按品质权重抽取不重复的若干词条。
    /// <paramref name="allowNegative"/> 为 false 时只会抽到正面词条（阶段三选一、商店都是这样）。
    /// <paramref name="exclude"/> 里的词条不会被抽到（刷新三选一时用来排掉当前已经摆出来的几张）。
    /// <paramref name="minQuality"/> 用来做「保底白银」这类抽奖，只从该品质及以上的词条里抽。
    /// <paramref name="maxQuality"/> 反过来封顶：商店的抽奖券开到黄金为止，传说只能自然抽到。
    /// <paramref name="negative"/> 只抽半边：true = 只要负面，false = 只要正面，null = 正负面一起抽。
    /// 行李箱抽奖把正面 / 负面的概率拆成了两个配置项，所以要先在这里把池子分成两半。
    /// </summary>
    public static List<HextechEntry> Roll(
        HextechState state,
        int count,
        Random random,
        bool allowNegative = false,
        ICollection<HextechEntry>? exclude = null,
        HextechQuality? minQuality = null,
        HextechQuality? maxQuality = null,
        bool? negative = null)
    {
        var pool = new List<HextechEntry>();
        var weights = new List<int>();

        foreach (var entry in _all)
        {
            // 三选一与商店只给「永久收益」：负面词条和限时爆发都留在行李箱抽奖里。
            if (!allowNegative && (entry.Negative || entry.LuggageOnly))
            {
                continue;
            }

            // 行李箱抽奖：正面 / 负面各自成池，只抽对上的那一半。
            if (negative.HasValue && entry.Negative != negative.Value)
            {
                continue;
            }

            // 机场里被任何人禁掉的词条，本局全房间都不再刷新。
            // 三选一、商店、行李箱抽奖都走这里，所以一处过滤就全覆盖了。
            if (HextechBans.IsBanned(entry.Id))
            {
                continue;
            }

            if (exclude != null && exclude.Contains(entry))
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

            if (!state.CanAcquire(entry))
            {
                continue;
            }

            pool.Add(entry);
            weights.Add(WeightOf(entry.Quality));
        }

        var result = new List<HextechEntry>();

        while (result.Count < count && pool.Count > 0)
        {
            var total = 0;
            foreach (var weight in weights)
            {
                total += weight;
            }

            if (total <= 0)
            {
                break;
            }

            var roll = random.Next(total);
            var index = 0;

            for (; index < weights.Count; index++)
            {
                roll -= weights[index];
                if (roll < 0)
                {
                    break;
                }
            }

            if (index >= pool.Count)
            {
                index = pool.Count - 1;
            }

            result.Add(pool[index]);
            pool.RemoveAt(index);
            weights.RemoveAt(index);
        }

        return result;
    }

    /// <summary>只抽一个，用于开箱抽奖、商店与刷新三选一。</summary>
    public static HextechEntry? RollOne(
        HextechState state,
        Random random,
        bool allowNegative,
        ICollection<HextechEntry>? exclude = null,
        HextechQuality? minQuality = null,
        HextechQuality? maxQuality = null,
        bool? negative = null)
    {
        var result = Roll(state, 1, random, allowNegative, exclude, minQuality, maxQuality, negative);
        return result.Count > 0 ? result[0] : null;
    }

    /// <summary>池子里还有没有能给的正面词条，商店用它决定抽奖券能不能买。</summary>
    public static bool HasAny(
        HextechState state,
        HextechQuality? minQuality = null,
        HextechQuality? maxQuality = null)
    {
        foreach (var entry in _all)
        {
            if (entry.Negative || entry.LuggageOnly)
            {
                continue;
            }

            // 和 Roll 保持一致：全被禁掉时这里也得说「没得抽了」，
            // 否则商店会卖给你一张必定抽空的抽奖券。
            if (HextechBans.IsBanned(entry.Id))
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

            if (state.CanAcquire(entry))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 品质权重：青铜 60 / 白银 22 / 黄金 18 / 传说 1（可用 <see cref="ModConfig.LegendaryWeight"/> 调，0 = 抽不到）。
    /// 白银原来是 30，压到 22 是为了让白银别那么满街都是。
    /// 黄金从 10 抬到 18 是为了配平「正面概率砍到 2.5%」：权重不动的话黄金在「抽到的海克斯」里只剩 4%，
    /// 现在约 7%。青铜 / 传说的权重一个没动，多出来的份额是靠挤掉白银的占比让出来的。
    /// <para>
    /// 负面（代价）词条和同品质的正面词条一样重 ——「开箱开出代价」的概率不再靠这里打折，
    /// 而是由 <see cref="ModConfig.LuggageNegativeChance"/> 直接给：
    /// 行李箱先判正面 / 负面，再在各自那半边池子里按这里的权重挑。
    /// 所以这个权重只决定「池子内部各品质怎么分」。
    /// </para>
    /// </summary>
    private static int WeightOf(HextechQuality quality)
    {
        return quality switch
        {
            HextechQuality.Legendary => Math.Max(0, ModConfig.LegendaryWeight.Value),
            HextechQuality.Gold => 18,
            HextechQuality.Silver => 22,
            _ => 60,
        };
    }
}
