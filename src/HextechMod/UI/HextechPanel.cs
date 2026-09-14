using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PeakModder.HextechMod;

/// <summary>
/// 海克斯三选一面板。不使用 EventSystem，直接做矩形命中检测，
/// 这样无论游戏的输入模块怎么配置都能点击。
/// <para>
/// 每张卡片下面带一个「刷新」按钮：<b>每张卡每次机会各可以刷一次</b>。
/// 「每次机会」而不是「每次弹出」：玩家按 ESC 把面板收起来、安全了再按重开键打开，
/// 看到的还是原来那几张（哪张刷过、刷成了什么都在），不会重新抽一副牌 ——
/// 否则收起来再打开就成了无限刷新（详见 <see cref="Show"/>）。
/// </para>
/// </summary>
public sealed class HextechPanel : MonoBehaviour
{
    /// <summary>
    /// 刷新一张卡。给出被刷的词条和当前摆着的全部词条，返回替换后的新词条；
    /// 返回 null 表示没有别的可以换了（此时不消耗刷新次数）。
    /// </summary>
    public delegate HextechEntry? RerollHandler(HextechEntry current, IReadOnlyList<HextechEntry> shown);

    private const float CardWidth = 460f;
    private const float CardHeight = 520f;
    private const float CardGap = 70f;

    /// <summary>卡片行能用的宽度（和 <c>_cardRow.sizeDelta.x</c> 对齐）：摆 4 张时整行按它缩。</summary>
    private const float RowWidth = 1520f;

    private const float IntroDuration = 0.34f;
    private const float IntroStagger = 0.08f;

    private sealed class CardView
    {
        public RectTransform Root = null!;
        public CanvasGroup Group = null!;
        public Image Background = null!;
        public Image Strip = null!;
        public Image Sheen = null!;
        public Image BadgeGlow = null!;
        public Image BadgeCircle = null!;
        public Image BadgeRing = null!;
        public TextMeshProUGUI BadgeGlyph = null!;
        public TextMeshProUGUI Title = null!;
        public TextMeshProUGUI Rarity = null!;
        public Image Divider = null!;
        public TextMeshProUGUI Description = null!;
        public RectTransform RerollRect = null!;
        public Image RerollBackground = null!;
        public RawImage RerollIcon = null!;
        public Vector2 BasePosition;
        public float Hover;
        public bool RerollUsed;
    }

    public static HextechPanel? Instance { get; private set; }

    private Canvas _canvas = null!;
    private CanvasGroup _rootGroup = null!;
    private TextMeshProUGUI _title = null!;
    private TextMeshProUGUI _hint = null!;
    private RectTransform _cardRow = null!;

    private readonly List<CardView> _cards = new();
    private readonly List<HextechEntry> _entries = new();

    /// <summary>
    /// 现在摆着的是哪一次机会（调用方给的编号，<c>-1</c> = 没摆着）。
    /// 编号没变就说明还是同一次：收起来再打开要原样摆回，不能重新抽（见 <see cref="Show"/>）。
    /// </summary>
    private int _offerKey = -1;

    private RerollHandler? _onReroll;
    private Action<HextechEntry>? _onPick;
    private Action? _onDismiss;
    private int _hoverCard = -1;
    private int _hoverReroll = -1;
    private float _introTime = 10f;

    public bool IsOpen { get; private set; }

    public static HextechPanel Create(Transform parent)
    {
        var go = new GameObject("HextechPanel");
        go.transform.SetParent(parent, false);
        var panel = go.AddComponent<HextechPanel>();
        panel.Build();
        Instance = panel;
        return panel;
    }

    private void Build()
    {
        _canvas = UiFactory.CreateCanvas("HextechPanelCanvas", 250);
        _canvas.transform.SetParent(transform, false);

        var root = UiFactory.Stretch(UiFactory.Node("Root", _canvas.transform));
        _rootGroup = UiFactory.Group(root);

        var backdrop = UiFactory.Solid(root, UiFactory.Backdrop);
        UiFactory.Stretch(backdrop.rectTransform);

        var ambiance = UiFactory.Glow(root, new Color(0.90f, 0.64f, 0.20f, 0.12f));
        ambiance.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
        ambiance.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        ambiance.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        ambiance.rectTransform.sizeDelta = new Vector2(2200f, 1400f);

        _title = UiFactory.Label(root, string.Empty, 48f, TextAlignmentOptions.Center, UiFactory.TextPrimary);
        _title.rectTransform.anchorMin = new Vector2(0.5f, 1f);
        _title.rectTransform.anchorMax = new Vector2(0.5f, 1f);
        _title.rectTransform.pivot = new Vector2(0.5f, 1f);
        _title.rectTransform.anchoredPosition = new Vector2(0f, -128f);
        _title.rectTransform.sizeDelta = new Vector2(1600f, 70f);

        var titleGlow = UiFactory.Glow(root, new Color(0.96f, 0.78f, 0.42f, 0.18f));
        titleGlow.rectTransform.anchorMin = new Vector2(0.5f, 1f);
        titleGlow.rectTransform.anchorMax = new Vector2(0.5f, 1f);
        titleGlow.rectTransform.pivot = new Vector2(0.5f, 1f);
        titleGlow.rectTransform.anchoredPosition = new Vector2(0f, -60f);
        titleGlow.rectTransform.sizeDelta = new Vector2(1100f, 320f);

        _hint = UiFactory.Label(
            root,
            BuildHint(),
            24f,
            TextAlignmentOptions.Center,
            UiFactory.TextMuted);
        _hint.rectTransform.anchorMin = new Vector2(0.5f, 1f);
        _hint.rectTransform.anchorMax = new Vector2(0.5f, 1f);
        _hint.rectTransform.pivot = new Vector2(0.5f, 1f);
        _hint.rectTransform.anchoredPosition = new Vector2(0f, -206f);
        _hint.rectTransform.sizeDelta = new Vector2(1600f, 40f);

        _cardRow = UiFactory.Node("Cards", root);
        _cardRow.anchorMin = new Vector2(0.5f, 0.5f);
        _cardRow.anchorMax = new Vector2(0.5f, 0.5f);
        _cardRow.pivot = new Vector2(0.5f, 0.5f);
        // 卡片整体上移一点：画面更居中，也给卡片下方的刷新按钮留出距离。
        _cardRow.anchoredPosition = new Vector2(0f, -32f);
        _cardRow.sizeDelta = new Vector2(1520f, CardHeight);

        _canvas.enabled = false;
    }

    /// <summary>
    /// 摆出一次三选一。
    /// <para>
    /// <paramref name="offerKey"/> 是这次机会的编号（调用方给，同一次机会反复打开要给同一个数）：
    /// 编号没变就<b>原样摆回上次那几张</b>（连「哪张刷过、刷成了什么」一起），而且<b>不调用</b>
    /// <paramref name="rollOptions"/> —— 玩家按 ESC 先收起来、安全了再按重开键继续选是正当操作，
    /// 但「再打开」不能等于「重新抽一副牌」，否则 ESC + 重开键就成了无限刷新。
    /// </para>
    /// <paramref name="rollOptions"/> 只在真正是「新的一次机会」时调用一次；返回空表示池子空了，那就什么也不摆。
    /// </summary>
    public void Show(
        int offerKey,
        Func<IReadOnlyList<HextechEntry>> rollOptions,
        string title,
        Action<HextechEntry> onPick,
        RerollHandler? onReroll = null,
        Action? onDismiss = null)
    {
        if (IsOpen)
        {
            return;
        }

        // 卡片齐（没被别的 Show 重建过）才敢原样摆回；少一张都说明对不上了，老老实实重抽。
        var resume = _offerKey == offerKey && _entries.Count > 0 && _cards.Count == _entries.Count;

        if (!resume)
        {
            var options = rollOptions();

            if (options == null || options.Count == 0)
            {
                return;
            }

            _entries.Clear();
            _entries.AddRange(options);
            _offerKey = offerKey;

            RebuildCards();
        }

        _onPick = onPick;
        _onReroll = onReroll;
        _onDismiss = onDismiss;
        _title.text = title;
        _hint.text = BuildHint();
        _hoverCard = -1;
        _hoverReroll = -1;
        _introTime = 0f;
        _rootGroup.alpha = 0f;

        _canvas.enabled = true;
        IsOpen = true;

        HextechWindowHost.Push();
    }

    /// <summary>收起面板，当作这次机会已经用掉（玩家选了一项之后走这里）。</summary>
    public void Hide()
    {
        DiscardOffer();
        Close();
    }

    /// <summary>
    /// 玩家没选就把面板关掉了（按 ESC，或者游戏把窗口关了）。
    ///
    /// 和 <see cref="Hide"/> 的区别是它会通知调用方「这次没选」——
    /// 调用方据此把这次机会留在账上，而不是当成已经用掉。
    /// 面板弹出来时可能正被怪追着打，玩家想先收起来、安全了再选，这是完全合理的操作。
    /// </summary>
    public void Dismiss()
    {
        var dismiss = _onDismiss;
        Close();
        dismiss?.Invoke();
    }

    /// <summary>
    /// 只把界面收起来。这次机会还留在账上，所以摆过的牌与「刷过没刷过」一并留着 ——
    /// 重新打开时 <see cref="Show"/> 按编号把它们原样摆回来。
    /// </summary>
    private void Close()
    {
        if (!IsOpen)
        {
            return;
        }

        _canvas.enabled = false;
        IsOpen = false;
        _onPick = null;
        _onReroll = null;
        _onDismiss = null;

        HextechWindowHost.Sync();
    }

    /// <summary>这次机会彻底没了（玩家已经选了一项）：摆过的牌和编号一起作废。</summary>
    private void DiscardOffer()
    {
        _offerKey = -1;
        _entries.Clear();
    }

    /// <summary>
    /// 底部那行操作提示。重新打开用的按键是可配置的，所以每次弹出时现算，
    /// 免得玩家改完键之后提示里还写着旧键。
    /// </summary>
    private string BuildHint()
    {
        var reopen = ModConfig.ChoiceKey.Value == KeyCode.None
            ? "ESC 先收起来（这次不会丢）"
            : $"ESC 先收起来（这次不会丢，按 {ModConfig.ChoiceKey.Value} 再打开）";

        // 「赌徒」会给到第四张，所以按键提示按实际张数给。
        var keys = _entries.Count > 3 ? "1 / 2 / 3 / 4" : "1 / 2 / 3";

        return "点击卡片选择 · 点卡片下方刷新图标换一个（每张限 1 次）"
            + $" · 也可以按 {keys} · {reopen}";
    }

    private void Update()
    {
        if (!IsOpen)
        {
            return;
        }

        // ESC（游戏原生的取消键）会先被窗口吃掉并关掉窗口，这里负责把界面一起收起来；
        // 后面那半句是兜底：万一下手时游戏没把 ESC 接到窗口取消上，也能自己关掉。
        if (!HextechWindowHost.IsOpen || Input.GetKeyDown(KeyCode.Escape))
        {
            Dismiss();
            return;
        }

        // 抽奖动画盖在上面时，面板不接受输入。
        if (HextechRoll.Instance != null && HextechRoll.Instance.IsPlaying)
        {
            return;
        }

        _introTime += Time.deltaTime;

        var mouse = (Vector2)Input.mousePosition;
        var hoverCard = -1;
        var hoverReroll = -1;

        for (var i = 0; i < _cards.Count; i++)
        {
            // 刷新按钮在卡片外部下方，仍然先判定它，避免被整张卡的「选择」吞掉。
            if (RectTransformUtility.RectangleContainsScreenPoint(_cards[i].RerollRect, mouse, null))
            {
                hoverReroll = i;
            }

            if (RectTransformUtility.RectangleContainsScreenPoint(_cards[i].Root, mouse, null))
            {
                hoverCard = i;
            }
        }

        _hoverCard = hoverCard;
        _hoverReroll = hoverReroll;

        Animate();
        RefreshVisuals();

        // 最多 4 张（「赌徒」的四选一），键位跟着 1 / 2 / 3 / 4 走。
        var count = Mathf.Min(_entries.Count, 4);

        for (var i = 0; i < count; i++)
        {
            if (Input.GetKeyDown(KeyCode.Alpha1 + i))
            {
                Pick(i);
                return;
            }
        }

        if (!Input.GetMouseButtonDown(0))
        {
            return;
        }

        if (hoverReroll >= 0)
        {
            TryReroll(hoverReroll);
            return;
        }

        if (hoverCard >= 0)
        {
            Pick(hoverCard);
        }
    }

    /// <summary>卡片依次飞入 + 悬停放大/上浮。</summary>
    private void Animate()
    {
        _rootGroup.alpha = Mathf.MoveTowards(_rootGroup.alpha, 1f, Time.deltaTime * 8f);

        for (var i = 0; i < _cards.Count; i++)
        {
            var card = _cards[i];
            card.Hover = Mathf.MoveTowards(card.Hover, i == _hoverCard ? 1f : 0f, Time.deltaTime * 6f);

            var delay = i * IntroStagger;
            var t = UiFactory.EaseOutCubic(Mathf.Clamp01((_introTime - delay) / IntroDuration));

            card.Group.alpha = t;

            var scale = Mathf.Lerp(0.90f, 1f, t) * Mathf.Lerp(1f, 1.04f, card.Hover);
            card.Root.localScale = new Vector3(scale, scale, 1f);
            card.Root.anchoredPosition = card.BasePosition + new Vector2(0f, Mathf.Lerp(-36f, 0f, t) + (10f * card.Hover));
        }
    }

    private void Pick(int index)
    {
        if (index < 0 || index >= _entries.Count)
        {
            return;
        }

        var entry = _entries[index];
        var callback = _onPick;
        Hide();
        callback?.Invoke(entry);
    }

    private void TryReroll(int index)
    {
        if (index < 0 || index >= _cards.Count || index >= _entries.Count || _onReroll == null)
        {
            return;
        }

        var card = _cards[index];

        if (card.RerollUsed)
        {
            HextechHud.Toast("这张已经刷新过了");
            return;
        }

        var next = _onReroll(_entries[index], _entries);

        if (next == null)
        {
            HextechHud.Toast("已经没有别的强化可以换了");
            return;
        }

        card.RerollUsed = true;
        _entries[index] = next;

        ApplyEntry(card, next);

        // 换完给一下反馈：卡片闪一下。
        card.Hover = 0f;
        card.Root.localScale = new Vector3(1.07f, 1.07f, 1f);

        RefreshVisuals();
    }

    private void ApplyEntry(CardView card, HextechEntry entry)
    {
        var color = UiFactory.QualityColor(entry.Quality);

        card.Title.text = entry.Title;
        card.Description.text = entry.Description;
        card.Rarity.text = UiFactory.QualityName(entry.Quality);
        card.Rarity.color = color;
        card.Strip.color = color;
        card.Divider.color = new Color(color.r, color.g, color.b, 0.35f);
        card.BadgeGlyph.text = UiFactory.QualityGlyph(entry.Quality);
        card.BadgeGlyph.color = color;
        card.BadgeCircle.color = new Color(color.r, color.g, color.b, 0.16f);
        card.BadgeRing.color = new Color(color.r, color.g, color.b, 0.65f);
        card.Sheen.color = new Color(color.r, color.g, color.b, 0.10f);
    }

    private void RefreshVisuals()
    {
        for (var i = 0; i < _cards.Count; i++)
        {
            var card = _cards[i];
            var hovered = i == _hoverCard && i < _entries.Count;
            var accent = i < _entries.Count ? UiFactory.QualityColor(_entries[i].Quality) : UiFactory.TextMuted;

            card.Background.color = hovered
                ? Color.Lerp(UiFactory.PanelBackground, accent, 0.22f)
                : UiFactory.PanelBackground;

            card.BadgeGlow.color = new Color(accent.r, accent.g, accent.b, (0.18f + (0.22f * card.Hover)) * card.Group.alpha);

            if (card.RerollUsed)
            {
                card.RerollBackground.color = UiFactory.PanelBackgroundLight;
                card.RerollIcon.color = UiFactory.TextMuted;
            }
            else
            {
                card.RerollBackground.color = i == _hoverReroll
                    ? Color.Lerp(UiFactory.PanelBackgroundLight, accent, 0.45f)
                    : UiFactory.PanelBackgroundLight;
                card.RerollIcon.color = i == _hoverReroll
                    ? Color.Lerp(UiFactory.Accent, accent, 0.35f)
                    : UiFactory.Accent;
            }
        }
    }

    private void RebuildCards()
    {
        for (var i = _cardRow.childCount - 1; i >= 0; i--)
        {
            var child = _cardRow.GetChild(i).gameObject;
            child.SetActive(false);
            Destroy(child);
        }

        _cards.Clear();

        var totalWidth = (_entries.Count * CardWidth) + ((_entries.Count - 1) * CardGap);

        // 超过 3 张（「赌徒」的四选一）时把整行缩一点，
        // 免得最外侧两张跑到屏幕外面；缩放挂在行上，卡片的动画不用改。
        _cardRow.localScale = Vector3.one * Mathf.Min(1f, RowWidth / Mathf.Max(1f, totalWidth));

        var startX = (-totalWidth / 2f) + (CardWidth / 2f);

        for (var i = 0; i < _entries.Count; i++)
        {
            var position = new Vector2(startX + (i * (CardWidth + CardGap)), 0f);
            _cards.Add(BuildCard(i, position));
            ApplyEntry(_cards[i], _entries[i]);
        }

        RefreshVisuals();
    }

    private CardView BuildCard(int index, Vector2 position)
    {
        var card = UiFactory.Node($"Card{index}", _cardRow);
        card.anchorMin = new Vector2(0.5f, 0.5f);
        card.anchorMax = new Vector2(0.5f, 0.5f);
        card.pivot = new Vector2(0.5f, 0.5f);
        card.anchoredPosition = position;
        card.sizeDelta = new Vector2(CardWidth, CardHeight);
        card.localScale = Vector3.one * 0.9f;

        var background = UiFactory.Rounded(card, UiFactory.PanelBackground, 20);
        UiFactory.Stretch(background.rectTransform);

        var group = UiFactory.Group(card);
        group.alpha = 0f;

        // 顶部一层品质色的受光渐变。
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

        // 品质徽章
        var badgeGlow = UiFactory.Glow(card, new Color(1f, 1f, 1f, 0.2f));
        badgeGlow.rectTransform.anchorMin = new Vector2(0.5f, 1f);
        badgeGlow.rectTransform.anchorMax = new Vector2(0.5f, 1f);
        badgeGlow.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        badgeGlow.rectTransform.anchoredPosition = new Vector2(0f, -70f);
        badgeGlow.rectTransform.sizeDelta = new Vector2(230f, 230f);

        var badgeCircle = UiFactory.Circle(card, new Color(1f, 1f, 1f, 0.14f));
        badgeCircle.rectTransform.anchorMin = new Vector2(0.5f, 1f);
        badgeCircle.rectTransform.anchorMax = new Vector2(0.5f, 1f);
        badgeCircle.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        badgeCircle.rectTransform.anchoredPosition = new Vector2(0f, -70f);
        badgeCircle.rectTransform.sizeDelta = new Vector2(84f, 84f);

        var badgeRing = UiFactory.Ring(card, new Color(1f, 1f, 1f, 0.5f));
        badgeRing.rectTransform.anchorMin = new Vector2(0.5f, 1f);
        badgeRing.rectTransform.anchorMax = new Vector2(0.5f, 1f);
        badgeRing.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        badgeRing.rectTransform.anchoredPosition = new Vector2(0f, -70f);
        badgeRing.rectTransform.sizeDelta = new Vector2(84f, 84f);

        var badgeGlyph = UiFactory.Label(card, "●", 40f, TextAlignmentOptions.Center, UiFactory.Accent);
        badgeGlyph.rectTransform.anchorMin = new Vector2(0.5f, 1f);
        badgeGlyph.rectTransform.anchorMax = new Vector2(0.5f, 1f);
        badgeGlyph.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        badgeGlyph.rectTransform.anchoredPosition = new Vector2(0f, -70f);
        badgeGlyph.rectTransform.sizeDelta = new Vector2(84f, 84f);

        var title = UiFactory.Label(card, string.Empty, 36f, TextAlignmentOptions.Center, UiFactory.TextPrimary);
        title.rectTransform.anchorMin = new Vector2(0f, 1f);
        title.rectTransform.anchorMax = new Vector2(1f, 1f);
        title.rectTransform.pivot = new Vector2(0.5f, 1f);
        title.rectTransform.offsetMin = new Vector2(24f, -168f);
        title.rectTransform.offsetMax = new Vector2(-24f, -124f);

        var rarity = UiFactory.Label(card, string.Empty, 22f, TextAlignmentOptions.Center, UiFactory.TextMuted);
        rarity.rectTransform.anchorMin = new Vector2(0f, 1f);
        rarity.rectTransform.anchorMax = new Vector2(1f, 1f);
        rarity.rectTransform.pivot = new Vector2(0.5f, 1f);
        rarity.rectTransform.offsetMin = new Vector2(24f, -204f);
        rarity.rectTransform.offsetMax = new Vector2(-24f, -172f);

        var divider = UiFactory.Rounded(card, new Color(1f, 1f, 1f, 0.2f), 2);
        divider.rectTransform.anchorMin = new Vector2(0f, 1f);
        divider.rectTransform.anchorMax = new Vector2(1f, 1f);
        divider.rectTransform.pivot = new Vector2(0.5f, 1f);
        divider.rectTransform.offsetMin = new Vector2(48f, -220f);
        divider.rectTransform.offsetMax = new Vector2(-48f, -218f);

        // 卡里不再摆「[1] 选择」按钮（整张卡都能点，那只是多余的视觉引导），
        // 说明区顺势往下铺满。
        var description = UiFactory.Label(card, string.Empty, 25f, TextAlignmentOptions.TopLeft, UiFactory.TextMuted);
        description.rectTransform.anchorMin = new Vector2(0f, 0f);
        description.rectTransform.anchorMax = new Vector2(1f, 1f);
        description.rectTransform.offsetMin = new Vector2(32f, 44f);
        description.rectTransform.offsetMax = new Vector2(-32f, -234f);

        // 卡片下方的刷新图标按钮。离卡片底边 36px：悬停时卡片会放大上浮，
        // 留这点容错空间，手抖也不会点到刷新。
        var rerollRect = UiFactory.Node("Reroll", card);
        rerollRect.anchorMin = new Vector2(0.5f, 0f);
        rerollRect.anchorMax = new Vector2(0.5f, 0f);
        rerollRect.pivot = new Vector2(0.5f, 1f);
        rerollRect.anchoredPosition = new Vector2(0f, -36f);
        rerollRect.sizeDelta = new Vector2(72f, 72f);

        var rerollBackground = UiFactory.Rounded(rerollRect, UiFactory.PanelBackgroundLight, 36);
        UiFactory.Stretch(rerollBackground.rectTransform);

        var rerollIcon = UiFactory.Icon(rerollRect, SkillIcons.Get("refresh"));
        rerollIcon.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
        rerollIcon.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        rerollIcon.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        rerollIcon.rectTransform.anchoredPosition = Vector2.zero;
        rerollIcon.rectTransform.sizeDelta = new Vector2(52f, 52f);
        rerollIcon.color = UiFactory.Accent;

        return new CardView
        {
            Root = card,
            Group = group,
            Background = background,
            Strip = strip,
            Sheen = sheen,
            BadgeGlow = badgeGlow,
            BadgeCircle = badgeCircle,
            BadgeRing = badgeRing,
            BadgeGlyph = badgeGlyph,
            Title = title,
            Rarity = rarity,
            Divider = divider,
            Description = description,
            RerollRect = rerollRect,
            RerollBackground = rerollBackground,
            RerollIcon = rerollIcon,
            BasePosition = position,
        };
    }
}
