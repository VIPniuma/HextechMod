using System;
using System.Collections.Generic;
using Photon.Pun;

namespace PeakModder.HextechMod;

/// <summary>
/// 本局「成员成长档案」：每个玩家的词条 / 层数 / 代币 / 复活次数。
/// <para>
/// 只为解决一件事：**掉线重连（以及中途加入）别把人这一局攒的东西弄丢**。
/// 现在这些只活在各个 <see cref="HextechState"/> 组件上，而 Photon 断线会把角色对象销毁重建，
/// 新挂上去的组件是空的 —— 于是「重连回来海克斯和代币全没了」。
/// </para>
/// <para>
/// 做法是**每台机器都记一份**：自己那份靠 <see cref="RecordLocal"/> 记，
/// 别人的靠他们广播名册时顺手记下来（见 <c>HextechRPC_SyncOwned</c>）。
/// 重连时先用自己的那份还原（游戏没重启的话它最全、还不依赖网络），
/// 自己那份丢了（游戏整个重启过）再向房主要 —— 房主那份是全的。
/// </para>
/// <para>
/// ⚠️ **只活在内存里、一回机场就整体清掉**：用户明确不做跨局保留，
/// 所以这里不落盘、也不按「存档」分键（见 <see cref="HextechManager.ResetRun"/>）。
/// </para>
/// </summary>
internal static class RunRoster
{
    private sealed class Progress
    {
        public string[] Ids = Array.Empty<string>();
        public int[] Stacks = Array.Empty<int>();
        public float Tokens;
        public int ReviveCharges;

        /// <summary>固定栏位上那条诅咒的词条 id（还原时要把它重新标成诅咒，不然会变成普通词条）。</summary>
        public string? CurseId;
    }

    /// <summary>按 Photon 的 UserId 存 —— 昵称会改、会重名，UserId 才是稳定的身份。</summary>
    private static readonly Dictionary<string, Progress> ByUser = new(StringComparer.Ordinal);

    public static string? LocalUserId => PhotonNetwork.LocalPlayer?.UserId;

    public static void Record(string? userId, string[]? ids, int[]? stacks, float tokens, int reviveCharges, string? curseId)
    {
        if (string.IsNullOrEmpty(userId))
        {
            return;
        }

        ByUser[userId!] = new Progress
        {
            Ids = ids ?? Array.Empty<string>(),
            Stacks = stacks ?? Array.Empty<int>(),
            Tokens = tokens,
            ReviveCharges = reviveCharges,
            CurseId = string.IsNullOrEmpty(curseId) ? null : curseId,
        };
    }

    /// <summary>
    /// 记下本机玩家自己的成长。广播是发给「别人」的，自己收不到自己的那份，所以自己单独记一次。
    /// </summary>
    public static void RecordLocal(HextechState? state)
    {
        if (state == null || !PhotonNetwork.InRoom)
        {
            return;
        }

        var player = PhotonNetwork.LocalPlayer;

        if (player == null)
        {
            return;
        }

        var (ids, stacks) = state.OwnedSnapshot();
        Record(player.UserId, ids, stacks, state.Tokens, state.ReviveCharges, state.Curse?.Id);
    }

    /// <summary>取某个玩家的成长；没记过、或者确实什么都没有（刚进局还没抽）时返回 false。</summary>
    public static bool TryGet(
        string? userId,
        out string[] ids,
        out int[] stacks,
        out float tokens,
        out int reviveCharges,
        out string? curseId)
    {
        ids = Array.Empty<string>();
        stacks = Array.Empty<int>();
        tokens = 0f;
        reviveCharges = 0;
        curseId = null;

        if (string.IsNullOrEmpty(userId) || !ByUser.TryGetValue(userId!, out var progress) || progress == null)
        {
            return false;
        }

        // 一条词条都没有、代币也是 0 —— 那就是「还没开始」，没什么可还原的。
        if (progress.Ids.Length == 0 && progress.Tokens <= 0f)
        {
            return false;
        }

        ids = progress.Ids;
        stacks = progress.Stacks;
        tokens = progress.Tokens;
        reviveCharges = progress.ReviveCharges;
        curseId = progress.CurseId;
        return true;
    }

    /// <summary>回机场（一局结束）：档案整体清掉 —— 明确不做跨局保留。</summary>
    public static void Clear() => ByUser.Clear();
}
