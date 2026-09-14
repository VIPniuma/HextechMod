using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PeakModder.HextechMod;

/// <summary>
/// 在游戏原生的「设置」页里插一行「海克斯模组设置」，点它打开 <see cref="HextechConfigPanel"/>。
/// <para>
/// 入口挂在设置页自己身上，而不是 ESC 主菜单上：ESC → 设置、主菜单 → 设置 用的是同一个
/// <see cref="SharedSettingsMenu"/>，所以两处都能看到这一行，位置也和原生设置项排在一起。
/// </para>
/// <para>
/// 做法是照着一行原生设置格（<c>SettingsUICell</c>）克隆一份，把标题换成我们的、把右侧那个
/// 控件区换成「点击打开」按钮 —— 字号、行高、配色、缩进、手柄可选中性全都跟着原生走，
/// 不用自己猜这套 UI 的参数。插在列表最上面：设置列表一屏放不下，排在末尾玩家很可能看不到。
/// </para>
/// <para>
/// 挂在 <c>SharedSettingsMenu.ShowSettings</c> 之后：那个方法每次都会先销毁旧格子、再按分类重建，
/// 我们跟着重建重插一次（上一次那行由我们自己销毁 —— 它不在游戏的格子表里，游戏不认它）。
/// 整段包在 try 里，认不出原生结构就只记一条日志：最坏是设置页里少这一行，
/// 玩家还能用快捷键 <see cref="ModConfig.ConfigPanelKey"/> 打开面板，原生设置不受影响。
/// </para>
/// </summary>
internal static class HextechSettingsMenuEntry
{
    private const string CellName = "HextechSettingsCell";

    private const string EntryTitle = "海克斯模组设置";

    private const string OpenLabel = "点击打开";

    /// <summary>控件区量不出宽度时，按钮按这个宽度报到排版里。</summary>
    private const float FallbackButtonWidth = 220f;

    /// <summary>我们插进去的那一行。它不在游戏的格子表里，得自己清理。</summary>
    private static GameObject? _injected;

    private static bool _loggedFailure;
    private static bool _loggedUnknownCell;

    [HarmonyPatch(typeof(SharedSettingsMenu), "ShowSettings")]
    internal static class SettingsShownPatch
    {
        [HarmonyPostfix]
        private static void Postfix(SharedSettingsMenu __instance)
        {
            try
            {
                Inject(__instance);
            }
            catch (Exception exception)
            {
                // 只报一次：切一次标签页就会走到这里，别把日志刷满。
                if (_loggedFailure)
                {
                    return;
                }

                _loggedFailure = true;
                HextechPlugin.Log.LogWarning($"设置页里的海克斯入口没能插上（之后不再提示）：{exception.Message}");
            }
        }
    }

    private static void Inject(SharedSettingsMenu menu)
    {
        // 上一次插的那行先拆掉再决定要不要重插：玩家把开关关掉之后也该立刻消失。
        if (_injected != null)
        {
            UnityEngine.Object.Destroy(_injected);
            _injected = null;
        }

        if (!ModConfig.AddSettingsMenuEntry.Value)
        {
            return;
        }

        var parent = menu.m_settingsContentParent;
        var prefab = menu.m_settingsCellPrefab;

        if (parent == null || prefab == null)
        {
            return;
        }

        var clone = UnityEngine.Object.Instantiate(prefab, parent);
        clone.name = CellName;
        clone.transform.SetAsFirstSibling();

        _injected = clone;

        // 原生格子是淡入的（Setup 里先把 CanvasGroup 置 0，再由协程拉起来）。
        // 我们这行不在游戏的淡入名单里，所以自己给个不透明，否则会一直看不见。
        var group = clone.GetComponent<CanvasGroup>();

        if (group != null)
        {
            group.alpha = 1f;
        }

        var cell = FindCellComponent(clone);

        if (cell == null)
        {
            // 认不出格子结构就别乱动它，撤掉克隆体，交给快捷键。
            UnityEngine.Object.Destroy(clone);
            _injected = null;

            if (!_loggedUnknownCell)
            {
                _loggedUnknownCell = true;
                HextechPlugin.Log.LogWarning("设置页里那行入口认不出原生设置格的字段，已跳过（用快捷键打开配置面板）。");
            }

            return;
        }

        Retitle(clone);
        BuildOpenButton(cell, clone);
    }

    /// <summary>
    /// 把标题改成「海克斯模组设置」。
    /// 原生标题是本地化组件按 key 写的，所以先在它自己的文本上写我们的字，再把本地化组件关掉 ——
    /// 不关的话它下次启用时会按 key 覆盖回去。
    /// </summary>
    private static void Retitle(GameObject clone)
    {
        var localized = clone.GetComponentInChildren<LocalizedText>(true);

        if (localized == null)
        {
            SetFirstText(clone);
            return;
        }

        if (localized.tmp != null)
        {
            localized.tmp.text = EntryTitle;
        }
        else
        {
            SetFirstText(clone);
        }

        localized.enabled = false;
    }

    /// <summary>兜底：认不出本地化组件时，直接改格子上第一个文本。</summary>
    private static void SetFirstText(GameObject clone)
    {
        var text = clone.GetComponentInChildren<TextMeshProUGUI>(true);

        if (text != null)
        {
            text.text = EntryTitle;
        }
    }

    /// <summary>
    /// 在格子的「控件区」（原生的滑块 / 下拉框就是摆在这里）放一个按钮。
    /// 位置和尺寸跟着原生控件容器走，所以看上去就是原生的一行设置。
    /// </summary>
    private static void BuildOpenButton(MonoBehaviour cell, GameObject clone)
    {
        if (ReadField(cell, "m_settingsContentParent") is not Transform content)
        {
            return;
        }

        // 克隆体上可能带着原生的控件（比如克隆的是已经搭好的格子），先清掉免得和按钮叠在一起。
        for (var i = content.childCount - 1; i >= 0; i--)
        {
            UnityEngine.Object.Destroy(content.GetChild(i).gameObject);
        }

        var holder = UiFactory.Node("Open", content);
        UiFactory.Stretch(holder);

        // 控件区是「由里面的控件撑开」的那种的话，现在里面空着可能还是零尺寸，
        // 光靠 Stretch 会缩成一个点。所以顺手报一个首选尺寸：排版组会照着它把这一块撑开。
        var layout = holder.gameObject.AddComponent<LayoutElement>();
        layout.preferredWidth = FallbackButtonWidth;
        layout.preferredHeight = RowHeight(clone);
        layout.flexibleWidth = 1f;

        var background = UiFactory.Rounded(holder, UiFactory.PanelBackgroundLight, 10);
        UiFactory.Stretch(background.rectTransform);

        var label = UiFactory.Label(holder, OpenLabel, LabelSize(clone), TextAlignmentOptions.Center, UiFactory.TextPrimary);
        UiFactory.Stretch(label.rectTransform);

        var button = holder.gameObject.AddComponent<Button>();
        button.targetGraphic = background;
        button.onClick.AddListener(OpenConfigPanel);
    }

    /// <summary>按钮文字用原生标题的字号，拿不到时才退回一个看着差不多的值。</summary>
    private static float LabelSize(GameObject clone)
    {
        var text = clone.GetComponentInChildren<TextMeshProUGUI>(true);

        return text != null && text.fontSize > 1f ? text.fontSize * 0.9f : 22f;
    }

    /// <summary>
    /// 一行有多高。取的是克隆体的矩形（这一份是从原生格子上抄下来的，所以就是原生行高），
    /// 量不出来时给个常见的行高。
    /// </summary>
    private static float RowHeight(GameObject clone)
    {
        var rect = clone.transform as RectTransform;

        return rect != null && rect.rect.height > 10f
            ? Mathf.Clamp(rect.rect.height - 6f, 28f, 64f)
            : 44f;
    }

    /// <summary>
    /// 找格子上的原生设置格组件。按类型名认而不是直接引用类型：那个类带泛型参数，
    /// 字段又大多是私有的，反射取更不容易被游戏版本改动打翻。
    /// </summary>
    private static MonoBehaviour? FindCellComponent(GameObject clone)
    {
        foreach (var behaviour in clone.GetComponents<MonoBehaviour>())
        {
            if (behaviour != null && behaviour.GetType().Name.StartsWith("SettingsUICell", StringComparison.Ordinal))
            {
                return behaviour;
            }
        }

        return null;
    }

    /// <summary>按名字取字段值（含私有字段），找不到返回 null。</summary>
    private static object? ReadField(object target, string name)
    {
        var type = target.GetType();

        while (type != null)
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (field != null)
            {
                return field.GetValue(target);
            }

            type = type.BaseType;
        }

        return null;
    }

    private static void OpenConfigPanel()
    {
        CloseOtherWindows();
        HextechConfigPanel.Instance?.Show();
    }

    /// <summary>
    /// 先把游戏原生开着的窗口（点入口时就是「设置」所在的那个菜单窗口）关掉，再打开我们的面板。
    /// <para>
    /// 不关的话，游戏会在暂停 / 取消的收尾里把刚打开的窗口直接关掉 ——
    /// Player.log 里的表现就是 <c>opening window</c> 紧跟着 <c>HextechWindow closing.</c>，
    /// 玩家看到的是「点了没反应 / 海克斯设置打不开」。关掉菜单窗口也顺便让游戏从暂停里恢复正常。
    /// </para>
    /// </summary>
    internal static void CloseOtherWindows()
    {
        var windows = MenuWindow.AllActiveWindows;

        if (windows == null)
        {
            return;
        }

        // 复制一份再遍历：Close 会把自己从这张表里摘掉，边遍历边改会抛异常。
        foreach (var window in new List<MenuWindow>(windows))
        {
            if (window == null || window == HextechWindowHost.Window)
            {
                continue;
            }

            try
            {
                window.Close();
            }
            catch (Exception)
            {
                // 关不掉最多是那个窗口留着，不影响面板打开。
            }
        }
    }
}
