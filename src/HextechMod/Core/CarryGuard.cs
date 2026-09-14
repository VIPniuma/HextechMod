using System;
using UnityEngine;

namespace PeakModder.HextechMod;

/// <summary>
/// 「背了人放不下来」的兜底。
/// <para>
/// 原版能扛的只有<b>完全昏迷</b>的队友（<c>CharacterInteractible.CanBeCarried</c> 要求 <c>fullyPassedOut</c>、
/// 没死、且还没人扛）。可一旦被扛的人醒了，原版<b>什么都不做</b>：
/// <c>Character.UnPassOutDone</c> 只清 <c>fullyPassedOut</c> / <c>passedOut</c>，完全不碰扛人关系；
/// <c>CharacterCarrying.Update</c> 只在<b>扛人者自己死亡</b>时才自动放下；
/// 而手动放下只能靠 <c>Interaction.DoInteractableRaycasts</c> 里那条「扛着人 + 当前选中槽为背上那个槽」的特殊分支
/// —— 扛起时被扛者的碰撞体已被 <c>CharacterRagdoll.ToggleCollision(false)</c> 全部关掉，普通射线扫不到他。
/// 于是队友醒了之后人就一直挂在肩上，玩家怎么按都放不下来。
/// </para>
/// <para>
/// 这里只补原版缺的那半：本地玩家肩上的人一旦不再完全昏迷（或已经死亡），就用原版自己的
/// <c>Character.BreakCharacterCarrying(true)</c> 广播放下，各端表现与手动放下完全一致。
/// 放下这条 RPC 必须由扛人者的客户端发出，所以只有「扛人的那一方」装了这个模组才生效。
/// </para>
/// </summary>
internal static class CarryGuard
{
    /// <summary>同一个目标两次尝试之间的最小间隔：RPC 是异步的，太密集只会刷屏。</summary>
    private const float RetrySeconds = 3f;

    /// <summary>上一次尝试放下的目标（网络 ViewID）与时间。</summary>
    private static int _lastAttemptViewId;
    private static float _lastAttemptTime;

    /// <summary>由 <see cref="HextechManager"/> 每帧调用。</summary>
    public static void Tick()
    {
        if (!ModConfig.CarryGuard.Value)
        {
            return;
        }

        var local = Character.localCharacter;

        if (local == null || local.data == null || local.refs == null || local.refs.view == null)
        {
            return;
        }

        // 只有扛人的那一方发得出这条 RPC（RPCA_Drop 打在扛人者的 PhotonView 上）。
        if (!local.refs.view.IsMine)
        {
            return;
        }

        var carried = local.data.carriedPlayer;

        if (carried == null || carried.data == null)
        {
            _lastAttemptViewId = 0;
            return;
        }

        // 还处在「可以被扛」的状态（活着 + 完全昏迷）就照旧扛着，这是原版的正常玩法。
        if (!carried.data.dead && carried.data.fullyPassedOut)
        {
            _lastAttemptViewId = 0;
            return;
        }

        // 目标的网络视图没了就别自己发 RPC —— RPCA_Drop 会拿它 GetComponent，null 会炸。
        if (carried.refs == null || carried.refs.view == null)
        {
            return;
        }

        var viewId = carried.refs.view.ViewID;

        if (viewId == _lastAttemptViewId && Time.time - _lastAttemptTime < RetrySeconds)
        {
            return;
        }

        _lastAttemptViewId = viewId;
        _lastAttemptTime = Time.time;

        try
        {
            local.BreakCharacterCarrying(true);
        }
        catch (Exception exception)
        {
            HextechPlugin.Log.LogWarning($"扛人兜底：放下 {carried.characterName} 失败（{exception.Message}）");
        }
    }
}
