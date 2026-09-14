using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PeakModder.HextechMod;

/// <summary>
/// 屏幕正下方的技能 HUD：已解锁技能排成一排，显示 CD、能量消耗与当前选中技能。
/// </summary>
public sealed class HextechSkillHud : MonoBehaviour
{
    public static HextechSkillHud? Instance { get; private set; }

    private const int SortOrder = 121;
    private const float SlotSize = 76f;
    private const float SlotSpacing = 10f;
    private const float BarPadding = 16f;
    private const float IconSize = 50f;
    private const float CostHeight = 22f;
    private const float BottomOffset = 26f;

    private static readonly Color DimIcon = new(1f, 1f, 1f, 0.35f);
    private static readonly Color NormalIcon = new(1f, 1f, 1f, 1f);
    private static readonly Color VeilColor = new(0.04f, 0.03f, 0.02f, 0.75f);

    private Canvas _canvas = null!;
    private RectTransform _bar = null!;
    private TextMeshProUGUI _titleLabel = null!;
    private readonly List<Slot> _slots = new();

    private int _shownSkillCount = -1;
    private float _lastCooldown = -1f;
    private SkillId? _lastCastSkill;

    public static HextechSkillHud Create(Transform parent)
    {
        var go = new GameObject("HextechSkillHud");
        go.transform.SetParent(parent, false);
        var hud = go.AddComponent<HextechSkillHud>();
        hud.Build();
        Instance = hud;
        return hud;
    }

    private void Build()
    {
        _canvas = UiFactory.CreateCanvas("HextechSkillHudCanvas", SortOrder);
        _canvas.transform.SetParent(transform, false);
        _canvas.enabled = false;

        var root = UiFactory.Stretch(UiFactory.Node("Root", _canvas.transform));

        _bar = UiFactory.Rounded(root, new Color(
            UiFactory.PanelBackground.r,
            UiFactory.PanelBackground.g,
            UiFactory.PanelBackground.b,
            0.92f), 18).rectTransform;
        _bar.anchorMin = new Vector2(0.5f, 0f);
        _bar.anchorMax = new Vector2(0.5f, 0f);
        _bar.pivot = new Vector2(0.5f, 0f);
        _bar.anchoredPosition = new Vector2(0f, BottomOffset);

        _titleLabel = UiFactory.Label(root, string.Empty, 20f, TextAlignmentOptions.Center, UiFactory.TextMuted);
        _titleLabel.textWrappingMode = TextWrappingModes.NoWrap;
        _titleLabel.rectTransform.anchorMin = new Vector2(0.5f, 0f);
        _titleLabel.rectTransform.anchorMax = new Vector2(0.5f, 0f);
        _titleLabel.rectTransform.pivot = new Vector2(0.5f, 0f);
        _titleLabel.rectTransform.anchoredPosition = new Vector2(0f, BottomOffset);
        _titleLabel.rectTransform.sizeDelta = new Vector2(800f, 26f);
    }

    private void Update()
    {
        var state = HextechState.Get(Character.localCharacter);

        // 模组被关掉时技能条也要跟着消失（和右侧 HUD 一样，每帧兜底）。
        if (state == null || HextechHud.IsHidden || !ModConfig.Enabled.Value)
        {
            if (_canvas.enabled)
            {
                _canvas.enabled = false;
            }

            return;
        }

        var skills = state.Skills;
        var count = skills.Count;
        if (count == 0)
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

        var current = state.CurrentSkill;
        var cooldown = state.CooldownRemaining;

        if (cooldown > 0.05f && _lastCooldown <= 0.05f && current.HasValue)
        {
            _lastCastSkill = current.Value;
        }

        _lastCooldown = cooldown;

        if (_shownSkillCount != count)
        {
            _shownSkillCount = count;
            RebuildSlots(skills);
        }

        var castId = cooldown > 0.05f ? _lastCastSkill : null;
        var barWidth = count * SlotSize + Mathf.Max(0, count - 1) * SlotSpacing + BarPadding * 2f;
        var barHeight = SlotSize + CostHeight + BarPadding * 2f;
        _bar.sizeDelta = new Vector2(barWidth, barHeight);

        var titleY = BottomOffset + barHeight + 4f;
        _titleLabel.rectTransform.anchoredPosition = new Vector2(0f, titleY);

        if (current.HasValue)
        {
            var definition = SkillRegistry.Get(current.Value);
            _titleLabel.text =
                $"{definition.Title} · 按 {ModConfig.SkillKey.Value} 释放 · 按 {ModConfig.CycleSkillKey.Value} 切换";
            _titleLabel.color = UiFactory.TextPrimary;
        }
        else
        {
            _titleLabel.text = "尚未选择技能";
            _titleLabel.color = UiFactory.TextMuted;
        }

        for (var i = 0; i < _slots.Count; i++)
        {
            var slot = _slots[i];
            var id = skills[i];
            var definition = SkillRegistry.Get(id);
            var isCurrent = current.HasValue && current.Value == id;
            var isCasting = castId.HasValue && castId.Value == id;
            var canAfford = state.Energy >= definition.EnergyCost;

            UpdateSlot(slot, definition, isCurrent, isCasting, cooldown, canAfford);
        }
    }

    private void RebuildSlots(IReadOnlyList<SkillId> skills)
    {
        foreach (var slot in _slots)
        {
            if (slot.Root != null)
            {
                Destroy(slot.Root.gameObject);
            }
        }

        _slots.Clear();

        for (var i = 0; i < skills.Count; i++)
        {
            var id = skills[i];
            var root = UiFactory.Node($"Slot_{id}", _bar);
            root.anchorMin = new Vector2(0f, 1f);
            root.anchorMax = new Vector2(0f, 1f);
            root.pivot = new Vector2(0f, 1f);
            root.sizeDelta = new Vector2(SlotSize, SlotSize + CostHeight);
            root.anchoredPosition = new Vector2(BarPadding + i * (SlotSize + SlotSpacing), -BarPadding);

            var bg = UiFactory.Rounded(root, UiFactory.PanelBackgroundLight, 14);
            UiFactory.Stretch(bg.rectTransform, 0f, 0f, 0f, 0f);

            var border = UiFactory.Ring(root, UiFactory.Accent);
            border.rectTransform.anchorMin = new Vector2(0f, 1f);
            border.rectTransform.anchorMax = new Vector2(0f, 1f);
            border.rectTransform.pivot = new Vector2(0f, 1f);
            border.rectTransform.anchoredPosition = new Vector2(-2f, 2f);
            border.rectTransform.sizeDelta = new Vector2(SlotSize + 4f, SlotSize + 4f);
            border.gameObject.SetActive(false);

            var iconArea = UiFactory.Node("IconArea", root);
            iconArea.anchorMin = new Vector2(0f, 1f);
            iconArea.anchorMax = new Vector2(0f, 1f);
            iconArea.pivot = new Vector2(0f, 1f);
            iconArea.offsetMin = new Vector2(0f, -SlotSize);
            iconArea.offsetMax = new Vector2(SlotSize, 0f);

            var icon = UiFactory.Icon(iconArea, SkillIcons.Get(id));
            icon.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
            icon.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            icon.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            icon.rectTransform.anchoredPosition = Vector2.zero;
            icon.rectTransform.sizeDelta = new Vector2(IconSize, IconSize);

            // 必须给它一个 sprite：Image 在 sprite 为空时会退化成画整块矩形，fillAmount 会被忽略。
            var veil = UiFactory.Rounded(iconArea, VeilColor, 2);
            UiFactory.Stretch(veil.rectTransform, 0f, 0f, 0f, 0f);
            veil.type = Image.Type.Filled;
            veil.fillMethod = Image.FillMethod.Vertical;
            veil.fillOrigin = 1;
            veil.fillAmount = 0f;

            var cdLabel = UiFactory.Label(iconArea, string.Empty, 26f, TextAlignmentOptions.Center, UiFactory.TextPrimary);
            cdLabel.textWrappingMode = TextWrappingModes.NoWrap;
            cdLabel.fontStyle = FontStyles.Bold;
            UiFactory.Stretch(cdLabel.rectTransform, 0f, 0f, 0f, 0f);

            var cost = UiFactory.Label(root, string.Empty, 16f, TextAlignmentOptions.Center, UiFactory.Accent);
            cost.textWrappingMode = TextWrappingModes.NoWrap;
            cost.rectTransform.anchorMin = new Vector2(0f, 0f);
            cost.rectTransform.anchorMax = new Vector2(0f, 0f);
            cost.rectTransform.pivot = new Vector2(0f, 0f);
            cost.rectTransform.offsetMin = new Vector2(0f, 0f);
            cost.rectTransform.offsetMax = new Vector2(SlotSize, CostHeight);

            _slots.Add(new Slot
            {
                Root = root,
                Border = border,
                Icon = icon,
                Veil = veil,
                CdLabel = cdLabel,
                CostLabel = cost,
            });
        }
    }

    private static void UpdateSlot(Slot slot, SkillDefinition definition, bool isCurrent, bool isCasting, float cooldown, bool canAfford)
    {
        slot.Border.gameObject.SetActive(isCurrent);

        slot.CostLabel.text = definition.EnergyCost.ToString("0");
        slot.CostLabel.color = canAfford ? UiFactory.Accent : UiFactory.Danger;

        if (isCasting)
        {
            var total = Mathf.Max(definition.Cooldown, 0.01f);
            var progress = Mathf.Clamp01(cooldown / total);
            slot.Veil.fillAmount = progress;
            slot.Veil.gameObject.SetActive(true);
            slot.CdLabel.text = cooldown > 0.95f ? cooldown.ToString("0") : cooldown.ToString("0.0");
            slot.CdLabel.gameObject.SetActive(true);
            slot.Icon.color = DimIcon;
        }
        else
        {
            slot.Veil.gameObject.SetActive(false);
            slot.CdLabel.gameObject.SetActive(false);
            slot.Icon.color = cooldown > 0.05f || !canAfford ? DimIcon : NormalIcon;
        }
    }

    private void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }
    }

    private sealed class Slot
    {
        public RectTransform Root = null!;
        public Image Border = null!;
        public RawImage Icon = null!;
        public Image Veil = null!;
        public TextMeshProUGUI CdLabel = null!;
        public TextMeshProUGUI CostLabel = null!;
    }
}
