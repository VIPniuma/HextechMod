using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PeakModder.HextechMod;

/// <summary>
/// 轻量 HUD：只保留「顶部居中的操作提示」与「本局还没禁用海克斯」的常驻提醒。
/// <para>
/// 原先贴在右侧的「已获得词条」面板已移除（见 <see cref="HextechCodex"/>：改为按 K 打开的海克斯页面）。
/// 代币 / 能量 / 开商店提示改挂到技能 HUD 右侧（见 <see cref="HextechSkillHud"/>）。
/// 这里的 <see cref="Toast"/> 仍是全模组共用的浮层提示入口。
/// </para>
/// </summary>
public sealed class HextechHud : MonoBehaviour
{
    public static HextechHud? Instance { get; private set; }

    private const float ToastSeconds = 3.5f;

    private Canvas _canvas = null!;
    private TextMeshProUGUI _toast = null!;
    private TextMeshProUGUI _banPrompt = null!;
    private string _banPromptState = string.Empty;
    private float _toastUntil;

    public static HextechHud Create(Transform parent)
    {
        var go = new GameObject("HextechHud");
        go.transform.SetParent(parent, false);
        var hud = go.AddComponent<HextechHud>();
        hud.Build();
        Instance = hud;
        return hud;
    }

    public static void Toast(string message)
    {
        Toast(message, ToastSeconds);
    }

    /// <summary>
    /// 需要玩家看清楚、甚至要抄下来的提示（比如日志上报回来的取件编号）走这个重载，
    /// 给一段更长的停留时间。
    /// </summary>
    public static void Toast(string message, float seconds)
    {
        if (Instance == null)
        {
            return;
        }

        Instance._toast.text = message;
        Instance._toast.color = new Color(UiFactory.Accent.r, UiFactory.Accent.g, UiFactory.Accent.b, 1f);
        Instance._toastUntil = Time.time + seconds;
    }

    /// <summary>整个 UI 是否被长按藏起来（已不再提供长按隐藏，恒为 false，仅保留兼容）。</summary>
    public static bool IsHidden => false;

    /// <summary>右侧面板是否收起（已移除，恒为 false，仅保留兼容）。</summary>
    public static bool IsCollapsed => false;

    private void Build()
    {
        _canvas = UiFactory.CreateCanvas("HextechHudCanvas", 120);
        _canvas.transform.SetParent(transform, false);

        var root = UiFactory.Stretch(UiFactory.Node("Root", _canvas.transform));

        _toast = UiFactory.Label(root, string.Empty, 30f, TextAlignmentOptions.Center, UiFactory.Accent);
        _toast.rectTransform.anchorMin = new Vector2(0.5f, 1f);
        _toast.rectTransform.anchorMax = new Vector2(0.5f, 1f);
        _toast.rectTransform.pivot = new Vector2(0.5f, 1f);
        _toast.rectTransform.anchoredPosition = new Vector2(0f, -120f);
        _toast.rectTransform.sizeDelta = new Vector2(1200f, 46f);

        // 「本局还没禁用海克斯」的常驻提醒，挂在顶部提示条下面一行。
        _banPrompt = UiFactory.Label(root, string.Empty, 24f, TextAlignmentOptions.Center, UiFactory.Warning);
        _banPrompt.textWrappingMode = TextWrappingModes.NoWrap;
        _banPrompt.rectTransform.anchorMin = new Vector2(0.5f, 1f);
        _banPrompt.rectTransform.anchorMax = new Vector2(0.5f, 1f);
        _banPrompt.rectTransform.pivot = new Vector2(0.5f, 1f);
        _banPrompt.rectTransform.anchoredPosition = new Vector2(0f, -170f);
        _banPrompt.rectTransform.sizeDelta = new Vector2(1200f, 40f);

        _canvas.enabled = false;
    }

    private void Update()
    {
        if (!ModConfig.Enabled.Value)
        {
            if (_canvas.enabled)
            {
                _canvas.enabled = false;
            }

            return;
        }

        var state = HextechState.Get(Character.localCharacter);

        if (state == null)
        {
            if (_canvas.enabled)
            {
                _canvas.enabled = false;
            }

            return;
        }

        if (!_canvas.enabled)
        {
            _canvas.enabled = true;
        }

        if (_toastUntil > 0f)
        {
            var remaining = _toastUntil - Time.time;

            if (remaining <= 0f)
            {
                _toastUntil = 0f;
                _toast.text = string.Empty;
            }
            else
            {
                var color = _toast.color;
                color.a = Mathf.Clamp01(remaining);
                _toast.color = color;
            }
        }

        RefreshBanPrompt();
    }

    /// <summary>
    /// 「本局还没有禁用海克斯」的顶部提醒。
    /// 只在机场、而且只在自己这一票还没投的时候显示 —— 出了机场就禁不了，
    /// 已经禁过的人也不必再看到。按「显示与否 + 当前按键」算成一个签名，变了才重写字符串。
    /// </summary>
    private void RefreshBanPrompt()
    {
        var show = HextechScene.InAirport && HextechBans.Mine == null;
        var signature = show ? ModConfig.BanPanelKey.Value.ToString() : string.Empty;

        if (_banPromptState == signature)
        {
            return;
        }

        _banPromptState = signature;

        if (!show)
        {
            _banPrompt.text = string.Empty;
            return;
        }

        _banPrompt.text = ModConfig.BanPanelKey.Value == KeyCode.None
            ? "本局还没有禁用海克斯 · 在机场打开禁用面板可以禁掉一个"
            : $"本局还没有禁用海克斯 · 按 {ModConfig.BanPanelKey.Value} 打开禁用面板";
    }
}
