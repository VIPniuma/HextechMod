using System;
using System.Collections;
using UnityEngine;

namespace PeakModder.HextechMod;

/// <summary>
/// 游戏刚打开（插件一加载，此时正好在主菜单）就查一次版本，有新版本就把更新提示推出来。
///
/// 不再等具体场景名：以前卡在「场景名 == Airport」才查，可机场就是主菜单，
/// 一旦 PEAK 改了主菜单的场景名或加载时序，检测就整段失灵（玩家打开游戏也收不到提示）。
/// 现在直接在插件启动后等菜单入场动画落定就查 —— 插件只在游戏启动时加载一次，
/// 这一刻就是主菜单，等于「打开游戏就检测」，不用再进到机场。
/// 只查一次（每次启动游戏算一次），失败就算了 —— 断网、服务器挂了都不该影响正常玩。
/// </summary>
public sealed class HextechUpdateChecker : MonoBehaviour
{
    /// <summary>启动后缓一下，让主菜单自己的入场动画先落定，别两层动画叠在一起。</summary>
    private const float MenuSettleSeconds = 2f;

    private void Start()
    {
        StartCoroutine(Run());
    }

    private IEnumerator Run()
    {
        if (!ModConfig.Enabled.Value || !ModConfig.CheckUpdateOnStart.Value)
        {
            yield break;
        }

        yield return new WaitForSecondsRealtime(MenuSettleSeconds);

        UpdateInfo? info = null;

        yield return UpdateFeed.Fetch(i => info = i);

        if (info == null || !UpdateFeed.IsNewer(info.Version, HextechPlugin.Version))
        {
            yield break;
        }

        if (!info.Force
            && string.Equals(ModConfig.IgnoredUpdateVersion.Value.Trim(), info.Version, StringComparison.Ordinal))
        {
            HextechPlugin.Log.LogInfo($"[更新] v{info.Version} 已被玩家忽略，本次不再提示。");
            yield break;
        }

        var prompt = HextechUpdatePrompt.Instance;

        if (prompt == null)
        {
            yield break;
        }

        HextechPlugin.Log.LogInfo($"[更新] 发现新版本 v{info.Version}（当前 v{HextechPlugin.Version}）。");
        prompt.Show(info);
    }
}
