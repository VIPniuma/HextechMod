using System;
using System.Collections.Generic;
using Photon.Pun;

namespace PeakModder.HextechMod;

/// <summary>
/// 全房间共用的「禁用海克斯」名单。
/// <para>
/// 玩法：在机场（还没进图的时候）每位玩家可以禁掉一个海克斯，被任何人禁掉的词条
/// 全房间都抽不到 —— 三选一、商店、行李箱都不会再刷出来。一局结束回到机场自动全部解除，
/// 下一局重新选。
/// </para>
/// <para>
/// 名单本质就是「谁禁了什么」，所以这里只记每个人那一票，不用另外记一份合并结果 ——
/// 面板上也就能一眼看出某个词条是队友禁的、还是自己禁的。
/// 同步走 Photon RPC：谁改了就广播全房间；中途进房间的人向房主问一次当前名单。
/// 单人游戏（不在房间里）就只在本地生效。
/// </para>
/// </summary>
public static class HextechBans
{
    /// <summary>不在房间里时用的假编号，这样单人游戏也能用同一套逻辑。</summary>
    private const int OfflineActor = 0;

    /// <summary>玩家编号 → 他禁掉的词条 Id。</summary>
    private static readonly Dictionary<int, string> Votes = new();

    /// <summary>本机玩家禁掉的那一个；没禁是 null。</summary>
    public static string? Mine { get; private set; }

    /// <summary>当前生效的全部投票。</summary>
    public static IReadOnlyDictionary<int, string> All => Votes;

    /// <summary>这个词条是不是被（任何人）禁掉了。</summary>
    public static bool IsBanned(string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return false;
        }

        foreach (var vote in Votes.Values)
        {
            if (string.Equals(vote, id, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>这个词条是不是本机玩家自己禁的。</summary>
    public static bool IsMine(string id)
    {
        return Mine != null && string.Equals(Mine, id, StringComparison.Ordinal);
    }

    /// <summary>这个词条是被队友禁的（不是自己禁的）。</summary>
    public static bool IsBannedByOthers(string id)
    {
        return IsBanned(id) && !IsMine(id);
    }

    /// <summary>
    /// 投出自己那一票，传 null 表示撤销。每人只有一票，改投别的会顶掉原来那一票。
    /// 返回是否真的改了 —— 没变就不广播，免得白刷网络。
    /// </summary>
    public static bool Vote(string? hextechId)
    {
        var normalized = string.IsNullOrWhiteSpace(hextechId) ? null : hextechId;

        if (string.Equals(normalized, Mine, StringComparison.Ordinal))
        {
            return false;
        }

        Mine = normalized;

        var actor = LocalActor;

        if (normalized == null)
        {
            Votes.Remove(actor);
        }
        else
        {
            Votes[actor] = normalized;
        }

        // 广播给全房间。不在房间 / 状态还没挂上来的时候没有接收方，静默跳过。
        var owner = Character.localCharacter;

        if (owner != null)
        {
            HextechState.Get(owner)?.BroadcastBan(normalized ?? string.Empty);
        }

        return true;
    }

    /// <summary>收到别人的一票；房主补发整份名单时也逐条走这里。</summary>
    public static void ApplyRemote(int actorNumber, string hextechId)
    {
        if (string.IsNullOrEmpty(hextechId))
        {
            Votes.Remove(actorNumber);
            return;
        }

        Votes[actorNumber] = hextechId;
    }

    /// <summary>
    /// 房主补发的整份名单：先清空再照抄，免得本地留着房主那边已经没有的旧票。
    /// 自己那一票以本地为准（本地才是这个玩家自己点的），其他人照抄。
    /// </summary>
    public static void ReplaceAll(int[] actorNumbers, string[] hextechIds)
    {
        if (actorNumbers == null || hextechIds == null)
        {
            return;
        }

        Votes.Clear();

        var count = Math.Min(actorNumbers.Length, hextechIds.Length);

        for (var i = 0; i < count; i++)
        {
            ApplyRemote(actorNumbers[i], hextechIds[i]);
        }

        var actor = LocalActor;

        if (Mine == null)
        {
            Votes.Remove(actor);
        }
        else
        {
            Votes[actor] = Mine;
        }
    }

    /// <summary>
    /// 回机场 / 换局：全部解除。
    /// 所有客户端都在同一个机场场景里走到这里，所以这一步不需要额外的网络同步。
    /// </summary>
    public static void Clear()
    {
        Votes.Clear();
        Mine = null;
    }

    private static int LocalActor =>
        PhotonNetwork.InRoom && PhotonNetwork.LocalPlayer != null
            ? PhotonNetwork.LocalPlayer.ActorNumber
            : OfflineActor;
}
