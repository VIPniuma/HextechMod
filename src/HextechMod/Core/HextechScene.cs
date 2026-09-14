using System;
using UnityEngine.SceneManagement;

namespace PeakModder.HextechMod;

/// <summary>
/// 「现在是不是在机场」的唯一判据。
/// <para>
/// 机场既是主菜单、也是组队大厅，还是唯一允许禁用海克斯的地方。这里直接用当前场景名判断 ——
/// 和游戏自己（<c>GameUtils.Awake</c> 里就是比对 <c>gameObject.scene.name == "Airport"</c>）
/// 以及本 mod「回到机场就清空这一局」用的口径完全一致。
/// </para>
/// <para>
/// 以前这里图省事，用「<c>MapHandler</c> 存不存在」反推：没有 <c>MapHandler</c> 就当在机场。
/// 但机场场景里本来就带着 <c>Map</c> 那套物体，<c>MapHandler</c> 也可能存在，
/// 于是禁用面板会在机场被拦下来（弹「只能回到机场再禁用词条」），怎么按都开不出来。
/// 换成场景名之后就不会再误判了。
/// </para>
/// </summary>
internal static class HextechScene
{
    private const string AirportSceneName = "Airport";

    /// <summary>当前活动场景是不是机场。</summary>
    public static bool InAirport => IsAirportScene(SceneManager.GetActiveScene().name);

    /// <summary>
    /// 指定名字的场景是不是机场。给「拿不到活动场景」的地方用 —— 比如要判断**某个角色自己**在哪张图上
    /// （<c>character.gameObject.scene</c>），那时活动场景可能是另一张（机场与地图共存时）。
    /// </summary>
    public static bool IsAirportScene(string? sceneName) =>
        string.Equals(sceneName, AirportSceneName, StringComparison.OrdinalIgnoreCase);
}
