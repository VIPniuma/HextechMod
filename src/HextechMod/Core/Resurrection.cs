using Photon.Pun;
using UnityEngine;

namespace PeakModder.HextechMod;

/// <summary>
/// 「死而复生」的复活逻辑：把一名已经死掉（或完全昏迷）的队友拉回到使用者面前。
/// <para>
/// 用的是游戏自己的复活入口 <c>Character.RPCA_ReviveAtPosition(position, applyStatus, statueSegment)</c>
/// —— 和「骸骨之书」在 <c>Skelleton.Interact_CastFinished</c> 里复活队友走的是同一个 RPC，
/// 所以原版的复活动作、血量状态、队友视角这些东西都会照常更新，我们不用自己造一套。
/// </para>
/// <para>
/// 两个参数特意和骸骨之书不一样：
/// <list type="bullet">
/// <item><c>applyStatus</c> 传 false —— 不吃「复活诅咒」，也不加那 30% 饥饿；</item>
/// <item><c>statueSegment</c> 传 -1 —— 不写「最后一个复活点」，也不会把生物群系标成已跳过。</item>
/// </list>
/// </para>
/// </summary>
internal static class Resurrection
{
    /// <summary>
    /// 复活的 RPC 名。<c>Character.RPCA_ReviveAtPosition</c> 是 protected，
    /// 跨程序集拿不到 <c>nameof</c>，只能写字符串（Photon 本身也是按名字找方法的）。
    /// </summary>
    private const string ReviveRpc = "RPCA_ReviveAtPosition";

    /// <summary>复活点放在使用者正前方多远。</summary>
    private const float FrontDistance = 2f;

    /// <summary>往地面探射线时的起始高度，避免贴着地形时起点已经在地面之下。</summary>
    private const float GroundProbeHeight = 2f;

    /// <summary>
    /// 试着复活一名队友。成功时 <paramref name="targetName"/> 是被救的人，
    /// 失败时 <paramref name="failure"/> 是给玩家看的一句话。
    /// </summary>
    public static bool TryRevive(Character reviver, out string targetName, out string failure)
    {
        targetName = string.Empty;
        failure = string.Empty;

        if (reviver == null || !reviver.IsLocal)
        {
            failure = "「死而复生」只能由本人使用";
            return false;
        }

        // 自定义难度里可以关掉「允许复活死人」（跑图设置 900），关了就别用这条词条硬绕过去。
        if (!Ascents.canReviveDead)
        {
            failure = "这一局的规则不接受复活队友";
            return false;
        }

        var target = FindTarget(reviver);

        if (target == null)
        {
            failure = "现在没有已经死掉或完全昏迷的队友";
            return false;
        }

        var view = target.refs == null ? null : target.refs.view;

        if (view == null)
        {
            failure = "找不到目标的网络视图，复活没能发出去";
            return false;
        }

        targetName = target.characterName;
        view.RPC(ReviveRpc, RpcTarget.All, SpawnPoint(reviver), false, -1);
        return true;
    }

    /// <summary>挑最近的一个「已经死掉 / 完全昏迷」的队友，自己不算。</summary>
    private static Character? FindTarget(Character reviver)
    {
        var all = Character.AllCharacters;
        Character? best = null;
        var bestDistance = float.MaxValue;

        for (var i = 0; i < all.Count; i++)
        {
            var candidate = all[i];

            if (candidate == null || candidate == reviver || candidate.data == null)
            {
                continue;
            }

            if (!candidate.data.dead && !candidate.data.fullyPassedOut)
            {
                continue;
            }

            var distance = Vector3.Distance(reviver.transform.position, candidate.transform.position);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// 复活点：使用者正前方 2 米，高度贴着地面（斜坡 / 台阶上也不会把人塞进地形里）。
    /// 探不到地面时退回到和使用者同高。
    /// </summary>
    private static Vector3 SpawnPoint(Character reviver)
    {
        var origin = reviver.transform.position;
        var forward = reviver.transform.forward;
        forward.y = 0f;

        if (forward.sqrMagnitude < 0.0001f)
        {
            forward = Vector3.forward;
        }

        var point = origin + (forward.normalized * FrontDistance);
        point.y = origin.y;

        if (Physics.Raycast(
                point + (Vector3.up * GroundProbeHeight),
                Vector3.down,
                out var hit,
                6f,
                ~0,
                QueryTriggerInteraction.Ignore))
        {
            point = hit.point;
        }

        return point;
    }
}
