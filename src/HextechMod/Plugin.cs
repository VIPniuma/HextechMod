using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace PeakModder.HextechMod;

/// <summary>
/// 海克斯 mod 插件入口。负责初始化 Harmony、配置项与各子系统。
/// </summary>
[BepInPlugin(Guid, Name, Version)]
public sealed class HextechPlugin : BaseUnityPlugin
{
    public const string Guid = "PeakModder.HextechMod";
    public const string Name = "HextechMod";
    public const string Version = "0.3.37";

    internal static HextechPlugin Instance = null!;
    internal static ManualLogSource Log = null!;

    /// <summary>补丁体检：成功打上补丁的补丁类数量（日志报告里会带上）。</summary>
    internal static int PatchedTypeCount { get; private set; }

    /// <summary>
    /// 补丁体检：没打上的补丁类（连同原因）。
    /// 「某个功能在游戏里一点反应都没有」这类反馈，答案基本都在这份清单里 —— 所以随日志一起上传。
    /// </summary>
    internal static readonly List<string> PatchFailures = new();

    private Harmony? _harmony;
    private GameObject? _root;

    private void Awake()
    {
        Instance = this;
        Log = Logger;

        ModConfig.Bind(Config);

        // 服务器平衡配置必须在一切注册之前生效（技能定义在注册那一刻就把数值拷进实例属性）。
        BalanceConfig.Load();

        _harmony = new Harmony(Guid);
        PatchAllSafe();

        _root = new GameObject("HextechMod");
        DontDestroyOnLoad(_root);

        _root.AddComponent<HextechManager>();
        HextechWindowHost.Create(_root.transform);
        HextechPanel.Create(_root.transform);
        HextechShop.Create(_root.transform);
        HextechRoll.Create(_root.transform);
        HextechHud.Create(_root.transform);
        HextechSkillHud.Create(_root.transform);
        HextechBanPanel.Create(_root.transform);
        HextechConfigPanel.Create(_root.transform);
        HextechPresetPicker.Create(_root.transform);
        HextechUpdatePrompt.Create(_root.transform);
        HextechReportCodeHud.Create(_root.transform);
        _root.AddComponent<HextechUpdateChecker>();

        Log.LogInfo(
            $"补丁 {PatchedTypeCount} 组"
            + (PatchFailures.Count == 0
                ? "全部打上。"
                : $"打上，另有 {PatchFailures.Count} 组失败（见上方 [补丁] 错误，这些功能不会生效）。"));

        Log.LogInfo(
            $"{Name} v{Version} 已加载。开局没有任何技能；登岛、点燃阶段篝火可以三选一（全是正面），" +
            $"开行李箱改成抽奖（可能抽到负面海克斯）。" +
            $"按 {ModConfig.SkillKey.Value} 释放技能，按 {ModConfig.CycleSkillKey.Value} 切换技能，" +
            $"按 {ModConfig.ShopKey.Value} 打开商店（每分钟发 {ModConfig.TokensPerMinute.Value:0.##} 枚代币），" +
            $"按 {ModConfig.HudToggleKey.Value} 展开 / 收起右侧面板（长按整个藏起来），" +
            $"按 {ModConfig.BanPanelKey.Value} 在机场禁用词条，" +
            $"按 {ModConfig.ChoiceKey.Value} 重新打开还没选的三选一，" +
            $"按 {ModConfig.ConfigPanelKey.Value} 打开海克斯设置（也能从 ESC → 设置 里那行「海克斯模组设置」进）。" +
            $"遇到 bug 可以按 {ModConfig.ReportKey.Value} 上传日志，会回一个编号给你。");
    }

    /// <summary>
    /// 逐个补丁类打补丁，替掉 <c>Harmony.PatchAll()</c>。
    ///
    /// PatchAll 是「全有或全无」：只要有一个目标解析不了（游戏更新删了方法，或者新增重载
    /// 让按名字查找变成歧义），它就抛异常 —— 而异常是在 Awake 里抛的，后面的 UI 创建全不执行，
    /// 整个模组在游戏里毫无痕迹；更坑的是这个异常由 Unity 吞掉，BepInEx 的 LogOutput.log
    /// 里一条线索都没有（只在 Unity 的 Player.log 里）。
    /// 逐个打的话，最坏只丢那一条补丁，而且失败会明确写进日志。
    /// </summary>
    private void PatchAllSafe()
    {
        foreach (var type in typeof(HextechPlugin).Assembly.GetTypes())
        {
            if (!HasPatchAttribute(type))
            {
                continue;
            }

            try
            {
                _harmony!.CreateClassProcessor(type).Patch();
                PatchedTypeCount++;
            }
            catch (Exception exception)
            {
                PatchFailures.Add($"{type.FullName}（{exception.Message}）");
                Log.LogError($"[补丁] {type.FullName} 打补丁失败（已跳过，其余补丁照常生效）：{exception.Message}");
            }
        }
    }

    /// <summary>这个类本身、或它的某个方法上挂没挂 HarmonyPatch 特性。</summary>
    private static bool HasPatchAttribute(Type type)
    {
        if (type.GetCustomAttributes(typeof(HarmonyPatch), true).Length > 0)
        {
            return true;
        }

        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                                   | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        foreach (var method in type.GetMethods(flags))
        {
            if (method.GetCustomAttributes(typeof(HarmonyPatch), true).Length > 0)
            {
                return true;
            }
        }

        return false;
    }

    private void OnDestroy()
    {
        _harmony?.UnpatchSelf();
        _harmony = null;

        if (_root != null)
        {
            Destroy(_root);
            _root = null;
        }
    }
}
