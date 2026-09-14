using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PeakModder.HextechMod;

/// <summary>
/// 「海克斯页面」：按 K（界面.打开海克斯页面）打开的本局持有总览。
/// <para>
/// 以三选一那种方块卡片的方式陈列：左侧第一张是已解锁的<b>技能</b>，
/// 右侧最多四张是已获得的<b>海克斯</b>（普通词条，最多 4 条）。
/// 每张卡给出：名字、介绍、效果（技能卡额外显示冷却与耗能）。
/// 玩家替换海克斯 / 技能后，面板在下一帧就把卡片内容刷新，不会停留在旧数据上。
/// </para>
/// </summary>
public sealed class HextechCodex : MonoBehaviour
{
    private const float CardWidth = 340f;
    private const float CardHeight = 470f;
    private const float CardGap = 36f;
    private const float RowWidth = 1844f; // 5 张卡排开时整行按它缩，避免最外侧跑到屏幕外。

    private const float FadeSpeed = 8f;

    private enum CardKind
    {
        Skill,
        Hextech,
    }

    private sealed class CodexCard
    {
        public RectTransform Root = null!;
        public CanvasGroup Group = null!;
        public Image Background = null!;
        public Image Strip = null!;
        public Image BadgeCircle = null!;
        public Image BadgeRing = null!;
        public Image BadgeGlow = null!;
        public RawImage Icon = null!;
        public TextMeshProUGUI BadgeGlyph = null!;
        public TextMeshProUGUI Title = null!;
        public TextMeshProUGUI Rarity = null!;
        public TextMeshProUGUI Description = null!;
        public TextMeshProUGUI Effect = null!;
        public CardKind Kind;
        public SkillId Skill;
        public HextechEntry? Entry;
    }

    public static HextechCodex? Instance { get; private set; }

    private Canvas _canvas = null!;
    private CanvasGroup _rootGroup = null!;
    private TextMeshProUGUI _title = null!;
    private TextMeshProUGUI _hint = null!;
    private TextMeshProUGUI _empty = null!;
    private RectTransform _cardRow = null!;

    private readonly List<CodexCard> _cards = new();
    private readonly StringBuilder _builder = new();
    private string _signature = string.Empty;
    private float _alpha;

    public bool IsOpen { get; private set; }

    public static HextechCodex Create(Transform parent)
    {
        var go = new GameObject("HextechCodex");
        go.transform.SetParent(parent, false);
        var panel = go.AddComponent<HextechCodex>();
        panel.Build();
        Instance = panel;
        return panel;
    }

    public void Toggle()
    {
        if (IsOpen)
        {
            Close();
        }
        else
        {
            Open();
        }
    }

    private void Open()
    {
        IsOpen = true;
        _canvas.enabled = true;
        _alpha = 0f;
        _signature = string.Empty; // 强制重建卡片
        _rootGroup.alpha = 0f;
    }

    private void Close()
    {
        IsOpen = false;
        _canvas.enabled = false;
    }

    private void Build()
    {
        _canvas = UiFactory.CreateCanvas("HextechCodexCanvas", 245);
        _canvas.transform.SetParent(transform, false);

        var root = UiFactory.Stretch(UiFactory.Node("Root", _canvas.transform));
        _rootGroup = UiFactory.Group(root);

        var backdrop = UiFactory.Solid(root, UiFactory.Backdrop);
        UiFactory.Stretch(backdrop.rectTransform);

        var ambiance = UiFactory.Glow(root, new Color(0.90f, 0.64f, 0.20f, 0.10f));
        ambiance.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
        ambiance.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        ambiance.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        ambiance.rectTransform.sizeDelta = new Vector2(2200f, 1400f);

        _title = UiFactory.Label(root, "海克斯 · 本局持有", 46f, TextAlignmentOptions.Center, UiFactory.TextPrimary);
        _title.rectTransform.anchorMin = new Vector2(0.5f, 1f);
        _title.rectTransform.anchorMax = new Vector2(0.5f, 1f);
        _title.rectTransform.pivot = new Vector2(0.5f, 1f);
        _title.rectTransform.anchoredPosition = new Vector2(0f, -110f);
        _title.rectTransform.sizeDelta = new Vector2(1600f, 70f);

        _hint = UiFactory.Label(
            root,
            "按 K 或 ESC 关闭",
            22f,
            TextAlignmentOptions.Center,
            UiFactory.TextMuted);
        _hint.rectTransform.anchorMin = new Vector2(0.5f, 1f);
        _hint.rectTransform.anchorMax = new Vector2(0.5f, 1f);
        _hint.rectTransform.pivot = new Vector2(0.5f, 1f);
        _hint.rectTransform.anchoredPosition = new Vector2(0f, -188f);
        _hint.rectTransform.sizeDelta = new Vector2(1600f, 36f);

        _empty = UiFactory.Label(
            root,
            "本局还没有强化 · 登岛、点燃阶段篝火、开行李箱都能抽到",
            24f,
            TextAlignmentOptions.Center,
            UiFactory.TextMuted);
        _empty.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
        _empty.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        _empty.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        _empty.rectTransform.anchoredPosition = new Vector2(0f, 0f);
        _empty.rectTransform.sizeDelta = new Vector2(1400f, 60f);
        _empty.gameObject.SetActive(false);

        _cardRow = UiFactory.Node("Cards", root);
        _cardRow.anchorMin = new Vector2(0.5f, 0.5f);
        _cardRow.anchorMax = new Vector2(0.5f, 0.5f);
        _cardRow.pivot = new Vector2(0.5f, 0.5f);
        _cardRow.anchoredPosition = new Vector2(0f, 20f);
        _cardRow.sizeDelta = new Vector2(RowWidth, CardHeight);

        _canvas.enabled = false;
    }

    private void Update()
    {
        if (!IsOpen)
        {
            return;
        }

        if (Input.GetKeyDown(KeyCode.Escape))
        {
            Close();
            return;
        }

        _alpha = Mathf.MoveTowards(_alpha, 1f, Time.deltaTime * FadeSpeed);
        _rootGroup.alpha = _alpha;

        var state = HextechState.Get(Character.localCharacter);

        if (state == null)
        {
            _empty.gameObject.SetActive(true);
            EnsureCardCount(0);
            return;
        }

        _empty.gameObject.SetActive(false);

        var signature = ComputeSignature(state);

        if (signature != _signature)
        {
            _signature = signature;
            RebuildCards(state);
        }

        RefreshCards(state);
    }

    /// <summary>技能（最多 1 个，排在左边）+ 普通海克斯（最多 4 个，排在右边）拼成一行签名。</summary>
    private string ComputeSignature(HextechState state)
    {
        _builder.Clear();

        if (state.Skills.Count > 0)
        {
            _builder.Append("S:").Append(state.Skills[0]).Append('|');
        }

        var hextechs = 0;

        for (var i = 0; i < state.Owned.Count && hextechs < HextechState.MaxNormalHextechs; i++)
        {
            var entry = state.Owned[i];

            if (entry.UnlocksSkill.HasValue)
            {
                continue;
            }

            _builder.Append("H:").Append(entry.Id).Append(':').Append(state.StackOf(entry)).Append('|');
            hextechs++;
        }

        return _builder.ToString();
    }

    private void EnsureCardCount(int count)
    {
        while (_cards.Count > count)
        {
            var card = _cards[_cards.Count - 1];
            Destroy(card.Root.gameObject);
            _cards.RemoveAt(_cards.Count - 1);
        }

        while (_cards.Count < count)
        {
            _cards.Add(BuildCard(_cards.Count));
        }
    }

    private void RebuildCards(HextechState state)
    {
        // 收集这一次要展示的卡片：先技能，后普通海克斯。
        var skills = state.Skills;
        var hextechs = new List<HextechEntry>();

        for (var i = 0; i < state.Owned.Count && hextechs.Count < HextechState.MaxNormalHextechs; i++)
        {
            var entry = state.Owned[i];

            if (!entry.UnlocksSkill.HasValue)
            {
                hextechs.Add(entry);
            }
        }

        var total = skills.Count + hextechs.Count;

        EnsureCardCount(total);

        var totalWidth = (total * CardWidth) + (Mathf.Max(0, total - 1) * CardGap);
        _cardRow.localScale = Vector3.one * Mathf.Min(1f, RowWidth / Mathf.Max(1f, totalWidth));

        var startX = (-totalWidth / 2f) + (CardWidth / 2f);
        var index = 0;

        for (var i = 0; i < skills.Count; i++, index++)
        {
            var card = _cards[index];
            card.Kind = CardKind.Skill;
            card.Skill = skills[i];
            card.Entry = null;
            card.Root.anchoredPosition = new Vector2(startX + (index * (CardWidth + CardGap)), 0f);
            card.Root.gameObject.SetActive(true);
        }

        for (var i = 0; i < hextechs.Count; i++, index++)
        {
            var card = _cards[index];
            card.Kind = CardKind.Hextech;
            card.Skill = default;
            card.Entry = hextechs[i];
            card.Root.anchoredPosition = new Vector2(startX + (index * (CardWidth + CardGap)), 0f);
            card.Root.gameObject.SetActive(true);
        }

        for (var i = index; i < _cards.Count; i++)
        {
            _cards[i].Root.gameObject.SetActive(false);
        }
    }

    /// <summary>每帧刷新卡片文字与配色，保证替换海克斯 / 技能后立刻跟上。</summary>
    private void RefreshCards(HextechState state)
    {
        for (var i = 0; i < _cards.Count; i++)
        {
            var card = _cards[i];

            // 每张卡自带一层 CanvasGroup，初值被设成 0 做淡入；不跟着根面板一起还原就永远透明。
            card.Group.alpha = _alpha;

            if (!card.Root.gameObject.activeSelf)
            {
                continue;
            }

            if (card.Kind == CardKind.Skill)
            {
                var definition = SkillRegistry.Get(card.Skill);
                var color = UiFactory.Accent;
                var cd = state.CooldownRemaining;

                card.Title.text = definition.Title;
                card.Rarity.text = "主动技能";
                card.Rarity.color = color;
                card.Description.text = definition.Description;
                card.Effect.text = cd > 0.05f
                    ? $"冷却 {cd:0.0}s · 耗能 {definition.EnergyCost:0} · 按 {ModConfig.SkillKey.Value} 释放"
                    : $"就绪 · 耗能 {definition.EnergyCost:0} · 按 {ModConfig.SkillKey.Value} 释放";

                card.Strip.color = color;
                card.BadgeGlyph.gameObject.SetActive(false);
                card.Icon.gameObject.SetActive(true);
                card.Icon.texture = SkillIcons.Get(card.Skill);
                card.BadgeCircle.color = new Color(color.r, color.g, color.b, 0.16f);
                card.BadgeRing.color = new Color(color.r, color.g, color.b, 0.65f);
                card.BadgeGlow.color = new Color(color.r, color.g, color.b, 0.18f * _alpha);
            }
            else
            {
                var entry = card.Entry!;
                var stacks = state.StackOf(entry);
                var color = entry.Negative ? UiFactory.Danger : UiFactory.QualityColor(entry.Quality);

                card.Title.text = entry.Title;
                card.Rarity.text = UiFactory.QualityName(entry.Quality) + (stacks > 1 ? $" ×{stacks}" : "");
                card.Rarity.color = color;
                card.Description.text = entry.Description;
                card.Effect.text = "效果：" + entry.Summary(stacks);

                card.Strip.color = color;
                card.BadgeGlyph.gameObject.SetActive(true);
                card.BadgeGlyph.text = UiFactory.QualityGlyph(entry.Quality);
                card.BadgeGlyph.color = color;
                card.Icon.gameObject.SetActive(false);
                card.BadgeCircle.color = new Color(color.r, color.g, color.b, 0.16f);
                card.BadgeRing.color = new Color(color.r, color.g, color.b, 0.65f);
                card.BadgeGlow.color = new Color(color.r, color.g, color.b, 0.18f * _alpha);
            }

            card.Background.color = UiFactory.PanelBackground;
        }
    }

    private CodexCard BuildCard(int index)
    {
        var card = UiFactory.Node($"CodexCard{index}", _cardRow);
        card.anchorMin = new Vector2(0.5f, 0.5f);
        card.anchorMax = new Vector2(0.5f, 0.5f);
        card.pivot = new Vector2(0.5f, 0.5f);
        card.sizeDelta = new Vector2(CardWidth, CardHeight);

        var background = UiFactory.Rounded(card, UiFactory.PanelBackground, 20);
        UiFactory.Stretch(background.rectTransform);

        var group = UiFactory.Group(card);
        group.alpha = 0f;

        var sheen = UiFactory.TopSheen(card, new Color(1f, 1f, 1f, 0.08f));
        sheen.rectTransform.anchorMin = new Vector2(0f, 1f);
        sheen.rectTransform.anchorMax = new Vector2(1f, 1f);
        sheen.rectTransform.pivot = new Vector2(0.5f, 1f);
        sheen.rectTransform.offsetMin = new Vector2(3f, -200f);
        sheen.rectTransform.offsetMax = new Vector2(-3f, -3f);

        var strip = UiFactory.Rounded(card, UiFactory.Accent, 5);
        strip.rectTransform.anchorMin = new Vector2(0f, 1f);
        strip.rectTransform.anchorMax = new Vector2(1f, 1f);
        strip.rectTransform.pivot = new Vector2(0.5f, 1f);
        strip.rectTransform.offsetMin = new Vector2(26f, -14f);
        strip.rectTransform.offsetMax = new Vector2(-26f, -6f);

        var badgeGlow = UiFactory.Glow(card, new Color(1f, 1f, 1f, 0.2f));
        badgeGlow.rectTransform.anchorMin = new Vector2(0.5f, 1f);
        badgeGlow.rectTransform.anchorMax = new Vector2(0.5f, 1f);
        badgeGlow.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        badgeGlow.rectTransform.anchoredPosition = new Vector2(0f, -72f);
        badgeGlow.rectTransform.sizeDelta = new Vector2(230f, 230f);

        var badgeCircle = UiFactory.Circle(card, new Color(1f, 1f, 1f, 0.14f));
        badgeCircle.rectTransform.anchorMin = new Vector2(0.5f, 1f);
        badgeCircle.rectTransform.anchorMax = new Vector2(0.5f, 1f);
        badgeCircle.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        badgeCircle.rectTransform.anchoredPosition = new Vector2(0f, -72f);
        badgeCircle.rectTransform.sizeDelta = new Vector2(88f, 88f);

        var badgeRing = UiFactory.Ring(card, new Color(1f, 1f, 1f, 0.5f));
        badgeRing.rectTransform.anchorMin = new Vector2(0.5f, 1f);
        badgeRing.rectTransform.anchorMax = new Vector2(0.5f, 1f);
        badgeRing.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        badgeRing.rectTransform.anchoredPosition = new Vector2(0f, -72f);
        badgeRing.rectTransform.sizeDelta = new Vector2(88f, 88f);

        var icon = UiFactory.Icon(card, null);
        icon.rectTransform.anchorMin = new Vector2(0.5f, 1f);
        icon.rectTransform.anchorMax = new Vector2(0.5f, 1f);
        icon.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        icon.rectTransform.anchoredPosition = new Vector2(0f, -72f);
        icon.rectTransform.sizeDelta = new Vector2(64f, 64f);

        var badgeGlyph = UiFactory.Label(card, "●", 40f, TextAlignmentOptions.Center, UiFactory.Accent);
        badgeGlyph.rectTransform.anchorMin = new Vector2(0.5f, 1f);
        badgeGlyph.rectTransform.anchorMax = new Vector2(0.5f, 1f);
        badgeGlyph.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        badgeGlyph.rectTransform.anchoredPosition = new Vector2(0f, -72f);
        badgeGlyph.rectTransform.sizeDelta = new Vector2(88f, 88f);

        var title = UiFactory.Label(card, string.Empty, 30f, TextAlignmentOptions.Center, UiFactory.TextPrimary);
        title.rectTransform.anchorMin = new Vector2(0f, 1f);
        title.rectTransform.anchorMax = new Vector2(1f, 1f);
        title.rectTransform.pivot = new Vector2(0.5f, 1f);
        title.rectTransform.offsetMin = new Vector2(20f, -150f);
        title.rectTransform.offsetMax = new Vector2(-20f, -112f);

        var rarity = UiFactory.Label(card, string.Empty, 21f, TextAlignmentOptions.Center, UiFactory.TextMuted);
        rarity.rectTransform.anchorMin = new Vector2(0f, 1f);
        rarity.rectTransform.anchorMax = new Vector2(1f, 1f);
        rarity.rectTransform.pivot = new Vector2(0.5f, 1f);
        rarity.rectTransform.offsetMin = new Vector2(20f, -184f);
        rarity.rectTransform.offsetMax = new Vector2(-20f, -154f);

        var divider = UiFactory.Rounded(card, new Color(1f, 1f, 1f, 0.2f), 2);
        divider.rectTransform.anchorMin = new Vector2(0f, 1f);
        divider.rectTransform.anchorMax = new Vector2(1f, 1f);
        divider.rectTransform.pivot = new Vector2(0.5f, 1f);
        divider.rectTransform.offsetMin = new Vector2(48f, -202f);
        divider.rectTransform.offsetMax = new Vector2(-48f, -200f);

        var description = UiFactory.Label(card, string.Empty, 23f, TextAlignmentOptions.TopLeft, UiFactory.TextMuted);
        description.rectTransform.anchorMin = new Vector2(0f, 1f);
        description.rectTransform.anchorMax = new Vector2(1f, 1f);
        description.rectTransform.pivot = new Vector2(0.5f, 1f);
        description.rectTransform.offsetMin = new Vector2(28f, -228f);
        description.rectTransform.offsetMax = new Vector2(-28f, -360f);

        var effect = UiFactory.Label(card, string.Empty, 22f, TextAlignmentOptions.TopLeft, UiFactory.TextPrimary);
        effect.rectTransform.anchorMin = new Vector2(0f, 0f);
        effect.rectTransform.anchorMax = new Vector2(1f, 0f);
        effect.rectTransform.pivot = new Vector2(0.5f, 0f);
        effect.rectTransform.offsetMin = new Vector2(28f, 18f);
        effect.rectTransform.offsetMax = new Vector2(-28f, 130f);

        return new CodexCard
        {
            Root = card,
            Group = group,
            Background = background,
            Strip = strip,
            BadgeCircle = badgeCircle,
            BadgeRing = badgeRing,
            BadgeGlow = badgeGlow,
            Icon = icon,
            BadgeGlyph = badgeGlyph,
            Title = title,
            Rarity = rarity,
            Description = description,
            Effect = effect,
        };
    }
}
