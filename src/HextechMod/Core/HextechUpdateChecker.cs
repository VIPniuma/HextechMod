using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PeakModder.HextechMod;

/// <summary>
/// 游戏刚打开时查一次版本，有新版本就把更新提示推出来。
///
/// 时机刻意卡在「主菜单刚出来、玩家还没选在线还是单人」这一步，而不是进了对局才查：
/// 那会儿玩家还没开始玩，打断一下不心疼，也不会跟登岛三选一抢屏幕。
/// 只查一次（每次启动游戏算一次），失败就算了 —— 断网、服务器挂了都不该影响正常玩。
/// </summary>
public sealed class HextechUpdateChecker : MonoBehaviour
{
    /// <summary>主菜单场景，游戏启动后落在这里，玩家在这儿选在线 / 单人。</summary>
    private const string MenuSceneName = "Airport";

    /// <summary>等主菜单场景出现。实在等不到（场景改名之类）就走超时照查，别把更新提示卡没。</summary>
    private const float MenuWaitTimeoutSeconds = 25f;

    /// <summary>主菜单出来后再缓一下，让菜单自己的入场动画先落定，别两层动画叠在一起。</summary>
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

        var deadline = Time.realtimeSinceStartup + MenuWaitTimeoutSeconds;

        while (!IsAtMainMenu() && Time.realtimeSinceStartup < deadline)
        {
            yield return null;
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

    private static bool IsAtMainMenu()
    {
        var scene = SceneManager.GetActiveScene();
        return scene.IsValid() && string.Equals(scene.name, MenuSceneName, StringComparison.OrdinalIgnoreCase);
    }
}
