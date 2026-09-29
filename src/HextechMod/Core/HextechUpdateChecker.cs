using System;
using System.Collections;
using UnityEngine;

namespace PeakModder.HextechMod;

/// <summary>
/// 版本检测：进入菜单侧场景（主菜单 MainMenu / 机场大厅 Airport）时查一次更新服务器，
/// 有新版本就把提示框推出来。
/// <para>
/// 触发点认场景沿：上一帧不在菜单侧、这一帧在菜单侧，才发起一次检测。注意必须是
/// <see cref="HextechScene.InMenu"/> 而不是只有 Airport —— PEAK 的主菜单是独立的 MainMenu 场景，
/// 玩家打开游戏停在主菜单时也要能收到提示（只认 Airport 的话不点「开始游戏」就永远不弹，
/// 2026-09-16 就是栽在这上面）。局内退回大厅、换房间都会再次触发。
/// 查失败（断网、服务器挂了）静默跳过，绝不影响正常进游戏。
/// 已经被玩家「忽略此版本」的版本号不再提示（强制更新除外）。
/// </para>
/// </summary>
public sealed class HextechUpdateChecker : MonoBehaviour
{
    private bool _wasInMenu;
    private bool _checking;

    private void Update()
    {
        var inMenu = ModConfig.Enabled.Value && HextechScene.InMenu;

        if (inMenu && !_wasInMenu && !_checking)
        {
            StartCoroutine(Run());
        }

        _wasInMenu = inMenu;
    }

    private IEnumerator Run()
    {
        _checking = true;

        try
        {
            UpdateInfo? info = null;

            yield return UpdateFeed.Fetch(i => info = i);

            if (info == null || !UpdateFeed.IsNewer(info.Version, HextechPlugin.Version))
            {
                yield break;
            }

            if (!info.Force
                && string.Equals(ModConfig.IgnoredUpdateVersion.Value.Trim(), info.Version, StringComparison.Ordinal))
            {
                HextechPlugin.Log.LogInfo($"[更新] v{info.Version} 已被玩家忽略，不再提示。");
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
        finally
        {
            _checking = false;
        }
    }
}
