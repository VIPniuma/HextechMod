using System;
using System.Collections.Generic;

namespace PeakModder.HextechMod;

/// <summary>
/// 「诅咒」固定栏位：登岛时自动抽一个负面（代价）词条扣在身上，
/// 之后每次点燃阶段篝火、三选一**选完**，就把旧的摘掉、重新抽一个。
/// <para>
/// 和普通海克斯的区别：它占的是**固定栏位**（<see cref="HextechState.Curse"/>）——
/// 不占 4 个普通词条的名额，也不进替换队列（换不掉），只能由下一次抽诅咒时整体换掉。
/// </para>
/// <para>
/// 结果和行李箱抽奖一样**先结算、再演动画**：动画只是演出，玩家中途关掉也不影响已经生效的代价。
/// 抽咒是每个人各抽各的（和三选一一样），所以不需要联机同步。
/// </para>
/// </summary>
internal static class CurseDraw
{
    private static readonly System.Random Rng = new();

    /// <summary>抽一条代价挂上固定栏位（旧的自动摘掉），播一次抽奖动画。</summary>
    public static void Draw(HextechState state, string header)
    {
        if (state == null || state.Character == null || !state.Character.IsLocal)
        {
            return;
        }

        var entry = Pick(state);

        if (entry == null)
        {
            // 负面池被禁空、或者全被自己拿过了：这次就没有代价（旧的已经在下一步之前摘掉了）。
            HextechPlugin.Log.LogInfo("[海克斯] 负面池里没有能抽的代价，这次不挂诅咒。");
            return;
        }

        state.SetCurse(entry);

        var stacks = state.StackOf(entry);
        HextechHud.Toast(Localization.T("代价缠身：{0} · {1}", entry.Title, entry.Summary(stacks)));

        // 动画只是演出：结果上面已经结算完了。
        var rollUi = HextechRoll.Instance;

        rollUi?.Play(new RollShow(header, BuildPool(), ToSlot(entry, stacks)));
    }

    /// <summary>
    /// 挑一条代价。**优先避开自己已经持有的** —— 摘诅咒是把整条词条移除，
    /// 要是挑中一条本来就有的，玩家自己攒的层数会被一起带走。
    /// <para>
    /// 另外「倒吊人」**不在诅咒固定栏位里出现**（2026-09-20 用户要求）：一次性事件词条
    /// 被抽成固定栏位等于上来就被打残，太惩罚 —— 它仍然可以从行李箱抽奖里出。
    /// </para>
    /// </summary>
    private static HextechEntry? Pick(HextechState state)
    {
        var exclude = new List<HextechEntry>(state.Owned);

        var hangedMan = HextechRegistry.Find(DefaultHextechs.HangedManId);

        if (hangedMan != null)
        {
            exclude.Add(hangedMan);
        }

        // 两次尝试都带着同一份排除表：倒吊人彻底不进诅咒池。
        return HextechRegistry.RollOne(state, Rng, allowNegative: true, negative: true, exclude: exclude)
               ?? HextechRegistry.RollOne(state, Rng, allowNegative: true, negative: true, exclude: exclude);
    }

    /// <summary>陪跑轨道只用负面词条：滚动时一眼就知道这是在抽「代价」。倒吊人同样不出现。</summary>
    private static List<RollSlot> BuildPool()
    {
        var pool = new List<RollSlot>();

        foreach (var entry in HextechRegistry.All)
        {
            if (entry.Negative && !string.Equals(entry.Id, DefaultHextechs.HangedManId, StringComparison.Ordinal))
            {
                pool.Add(ToSlot(entry));
            }
        }

        return pool;
    }

    private static RollSlot ToSlot(HextechEntry entry, int stacks = 0)
    {
        var detail = stacks > 0 ? entry.Summary(stacks) : null;

        return new RollSlot(entry.Title, Localization.T("代价"), UiFactory.Danger, null, detail);
    }
}
