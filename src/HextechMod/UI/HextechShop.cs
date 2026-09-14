using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PeakModder.HextechMod;

/// <summary>
/// 海克斯商店。顶部是分类，下面是**可滚动的商品网格**：所有商品一次铺开，
/// 滚轮下滑或拖动右侧滑块浏览，不再翻页。
///
/// 视觉对齐游戏原生的暖色纸质感（深棕底 + 橙色高亮 + 圆角块）。
/// 和 <see cref="HextechPanel"/> 一样不使用 EventSystem：命中检测和滚动全部
/// 按屏幕坐标自己算，所以不依赖游戏当前挂的是哪套输入模块。
/// </summary>
public sealed class HextechShop : MonoBehaviour
{
    private const int Columns = 3;

    private const float PanelWidth = 1560f;
    private const float PanelHeight = 900f;
    private const float PanelPadding = 36f;
    private const float HeaderHeight = 96f;
    private const float TabBarHeight = 58f;
    private const float FooterHeight = 46f;

    private const float CardHeight = 200f;
    private const float CardGapX = 16f;
    private const float CardGapY = 16f;
    private const float ContentPadY = 6f;

    private const float ScrollBarWidth = 14f;
    private const float ThumbWidth = 8f;
    private const float ScrollBarGap = 8f;

    /// <summary>布局没算出来时的兜底宽度 / 高度（正常情况下用实际 rect）。</summary>
    private const float FallbackBodyWidth = PanelWidth - (2f * PanelPadding) - ScrollBarWidth - ScrollBarGap;
    private const float FallbackBodyHeight = PanelHeight - (HeaderHeight + TabBarHeight + 16f) - (FooterHeight + 12f);

    /// <summary>一格滚轮滚动的距离。</summary>
    private const float WheelStep = 130f;

    /// <summary>滚动的平滑系数，越大越跟手。</summary>
    private const float ScrollLerp = 16f;

    private const float IntroDuration = 0.26f;
    private const float IntroStagger = 0.02f;

    private sealed class CardView
    {
        public RectTransform Root = null!;
        public CanvasGroup Group = null!;
        public Image Background = null!;
        public Image Strip = null!;
        public Image IconFrame = null!;
        public RawImage Icon = null!;
        public TextMeshProUGUI Glyph = null!;
        public TextMeshProUGUI Title = null!;
        public TextMeshProUGUI Tag = null!;
        public TextMeshProUGUI Description = null!;
        public Image CostBackground = null!;
        public TextMeshProUGUI Cost = null!;
        public TextMeshProUGUI Hint = null!;

        /// <summary>调价模式下价格左边那颗「−」（点一下降 1，按住 Shift 降 5）。</summary>
        public Image MinusBackground = null!;
        public RectTransform MinusArea = null!;
        public Image PlusBackground = null!;
        public RectTransform PlusArea = null!;

        public Vector2 BasePosition;
        public float Hover;
    }

    public static HextechShop? Instance { get; private set; }

    private Canvas _canvas = null!;
    private CanvasGroup _rootGroup = null!;
    private RectTransform _panelRect = null!;
    private RectTransform _body = null!;
    private RectTransform _viewport = null!;
    private RectTransform _content = null!;
    private RectTransform _scrollTrack = null!;
    private RectTransform _scrollThumb = null!;

    private TextMeshProUGUI _tokens = null!;
    private TextMeshProUGUI _hint = null!;
    private TextMeshProUGUI _empty = null!;
    private Image _tokenBadge = null!;

    /// <summary>调价模式左下角那颗「重置全部调价」。</summary>
    private Image _resetBackground = null!;
    private RectTransform _resetButton = null!;

    /// <summary>调价模式右下角的「保存为预设」（把现在这一版价格存起来）。</summary>
    private Image _presetSaveBackground = null!;
    private RectTransform _presetSaveButton = null!;

    /// <summary>调价模式右下角的「选择预设 ▾」（换一份存好的价格）。</summary>
    private Image _presetPickBackground = null!;
    private RectTransform _presetPickButton = null!;

    private RectTransform _tabRoot = null!;

    private readonly List<RectTransform> _tabRects = new();
    private readonly List<Image> _tabImages = new();
    private readonly List<Image> _tabUnderlines = new();
    private readonly List<TextMeshProUGUI> _tabLabels = new();
    private readonly List<CardView> _cards = new();
    private readonly List<ShopOffer> _visible = new();

    private HextechState? _state;
    private int _tab;

    /// <summary>调价模式：卡片上多出「− / ＋」，点卡片不再是购买。</summary>
    private bool _editingPrices;

    private int _hoverTab = -1;
    private int _hoverCard = -1;
    private float _introTime = 10f;

    // ── 滚动状态 ────────────────────────────────────────────────
    private float _scroll;
    private float _scrollTarget;
    private float _maxScroll;
    private float _cardWidth = 480f;

    private bool _scrollDragging;
    private float _dragStartMouseY;
    private float _dragStartScroll;

    public bool IsOpen { get; private set; }

    public static HextechShop Create(Transform parent)
    {
        var go = new GameObject("HextechShop");
        go.transform.SetParent(parent, false);
        var shop = go.AddComponent<HextechShop>();
        shop.Build();
        Instance = shop;
        return shop;
    }

    private void Build()
    {
        _canvas = UiFactory.CreateCanvas("HextechShopCanvas", 255);
        _canvas.transform.SetParent(transform, false);

        var root = UiFactory.Stretch(UiFactory.Node("Root", _canvas.transform));
        _rootGroup = UiFactory.Group(root);

        var backdrop = UiFactory.Solid(root, UiFactory.Backdrop);
        UiFactory.Stretch(backdrop.rectTransform);

        var ambiance = UiFactory.Glow(root, new Color(0.90f, 0.64f, 0.20f, 0.10f));
        ambiance.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
        ambiance.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        ambiance.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        ambiance.rectTransform.sizeDelta = new Vector2(2200f, 1500f);

        var panel = UiFactory.Panel(root, UiFactory.PanelBackground, 22);
        panel.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
        panel.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        panel.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        panel.rectTransform.anchoredPosition = Vector2.zero;
        panel.rectTransform.sizeDelta = new Vector2(PanelWidth, PanelHeight);
        _panelRect = panel.rectTransform;

        BuildHeader(panel.rectTransform);
        BuildTabBar(panel.rectTransform);
        BuildBody(panel.rectTransform);
        BuildFooter(panel.rectTransform);

        _canvas.enabled = false;
    }

    // ── 顶部标题栏 ──────────────────────────────────────────────
    private void BuildHeader(RectTransform panel)
    {
        var header = UiFactory.Rounded(panel, UiFactory.HeaderBackground, 18);
        header.rectTransform.anchorMin = new Vector2(0f, 1f);
        header.rectTransform.anchorMax = new Vector2(1f, 1f);
        header.rectTransform.pivot = new Vector2(0.5f, 1f);
        header.rectTransform.offsetMin = new Vector2(0f, -HeaderHeight);
        header.rectTransform.offsetMax = new Vector2(0f, 0f);

        var title = UiFactory.Label(header.transform, "海克斯商店", 38f, TextAlignmentOptions.Left, UiFactory.TextPrimary);
        AnchorTopLeft(title.rectTransform, PanelPadding, -16f, 720f, 48f);

        var subtitle = UiFactory.Label(
            header.transform,
            $"局内每分钟获得 {ModConfig.TokensPerMinute.Value:0.##} 枚代币 · 抽奖券开出的都是本局强化",
            17f,
            TextAlignmentOptions.Left,
            UiFactory.TextMuted);
        AnchorTopLeft(subtitle.rectTransform, PanelPadding + 2f, -62f, 980f, 26f);

        _tokenBadge = UiFactory.Rounded(header.transform, UiFactory.PanelHighlight, 14);
        AnchorTopRight(_tokenBadge.rectTransform, -PanelPadding, -18f, 220f, 60f);

        _tokens = UiFactory.Label(_tokenBadge.transform, string.Empty, 30f, TextAlignmentOptions.Center, UiFactory.Warning);
        UiFactory.Stretch(_tokens.rectTransform, 0f, 0f, 0f, 0f);
    }

    // ── 分类标签 ────────────────────────────────────────────────
    private void BuildTabBar(RectTransform panel)
    {
        _tabRoot = UiFactory.Node("Tabs", panel);
        _tabRoot.anchorMin = new Vector2(0f, 1f);
        _tabRoot.anchorMax = new Vector2(1f, 1f);
        _tabRoot.pivot = new Vector2(0.5f, 1f);
        _tabRoot.offsetMin = new Vector2(PanelPadding, -(HeaderHeight + TabBarHeight + 8f));
        _tabRoot.offsetMax = new Vector2(-PanelPadding, -(HeaderHeight + 8f));

        var count = HextechShopCatalog.TabCount;
        var total = FallbackBodyWidth + ScrollBarWidth + ScrollBarGap;
        var gap = 10f;
        var width = (total - ((count - 1) * gap)) / count;

        for (var i = 0; i < count; i++)
        {
            var rect = UiFactory.Node($"Tab{i}", _tabRoot);
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 0.5f);
            rect.anchoredPosition = new Vector2(i * (width + gap), 0f);
            rect.sizeDelta = new Vector2(width, 0f);

            var image = rect.gameObject.AddComponent<Image>();
            image.sprite = UiFactory.RoundedSprite(14);
            image.type = Image.Type.Sliced;
            image.color = UiFactory.PanelHighlight;

            var label = UiFactory.Label(rect, HextechShopCatalog.TabTitle(i), 24f, TextAlignmentOptions.Center, UiFactory.TextMuted);
            UiFactory.Stretch(label.rectTransform);

            // 选中态底部的橙色亮条。
            var underline = UiFactory.Rounded(rect, UiFactory.Accent, 3);
            underline.rectTransform.anchorMin = new Vector2(0f, 0f);
            underline.rectTransform.anchorMax = new Vector2(1f, 0f);
            underline.rectTransform.pivot = new Vector2(0.5f, 0f);
            underline.rectTransform.offsetMin = new Vector2(20f, 4f);
            underline.rectTransform.offsetMax = new Vector2(-20f, 8f);
            underline.color = new Color(UiFactory.Accent.r, UiFactory.Accent.g, UiFactory.Accent.b, 0f);

            _tabRects.Add(rect);
            _tabImages.Add(image);
            _tabUnderlines.Add(underline);
            _tabLabels.Add(label);
        }
    }

    // ── 滚动区 ──────────────────────────────────────────────────
    private void BuildBody(RectTransform panel)
    {
        _body = UiFactory.Node("Body", panel);
        _body.anchorMin = new Vector2(0f, 0f);
        _body.anchorMax = new Vector2(1f, 1f);
        _body.offsetMin = new Vector2(PanelPadding, FooterHeight + 12f);
        _body.offsetMax = new Vector2(-PanelPadding, -(HeaderHeight + TabBarHeight + 16f));

        _viewport = UiFactory.Node("Viewport", _body);
        _viewport.anchorMin = Vector2.zero;
        _viewport.anchorMax = Vector2.one;
        _viewport.offsetMin = Vector2.zero;
        _viewport.offsetMax = new Vector2(-(ScrollBarWidth + ScrollBarGap), 0f);
        _viewport.gameObject.AddComponent<RectMask2D>();

        _content = UiFactory.Node("Content", _viewport);
        _content.anchorMin = new Vector2(0f, 1f);
        _content.anchorMax = new Vector2(1f, 1f);
        _content.pivot = new Vector2(0.5f, 1f);
        _content.anchoredPosition = Vector2.zero;
        _content.sizeDelta = Vector2.zero;

        BuildScrollBar();

        _empty = UiFactory.Label(_viewport, string.Empty, 26f, TextAlignmentOptions.Center, UiFactory.TextMuted);
        _empty.rectTransform.anchorMin = new Vector2(0f, 1f);
        _empty.rectTransform.anchorMax = new Vector2(1f, 1f);
        _empty.rectTransform.pivot = new Vector2(0.5f, 1f);
        _empty.rectTransform.offsetMin = new Vector2(0f, -140f);
        _empty.rectTransform.offsetMax = new Vector2(0f, -40f);
        _empty.gameObject.SetActive(false);
    }

    private void BuildScrollBar()
    {
        _scrollTrack = UiFactory.Node("ScrollTrack", _body);
        _scrollTrack.anchorMin = new Vector2(1f, 0f);
        _scrollTrack.anchorMax = new Vector2(1f, 1f);
        _scrollTrack.pivot = new Vector2(1f, 0.5f);
        _scrollTrack.offsetMin = new Vector2(-ScrollBarWidth, 0f);
        _scrollTrack.offsetMax = new Vector2(0f, 0f);

        var track = _scrollTrack.gameObject.AddComponent<Image>();
        track.sprite = UiFactory.RoundedSprite(7);
        track.type = Image.Type.Sliced;
        track.color = new Color(1f, 1f, 1f, 0.06f);

        var thumb = UiFactory.Node("Thumb", _scrollTrack);
        thumb.anchorMin = new Vector2(0.5f, 1f);
        thumb.anchorMax = new Vector2(0.5f, 1f);
        thumb.pivot = new Vector2(0.5f, 1f);
        thumb.sizeDelta = new Vector2(ThumbWidth, 120f);
        _scrollThumb = thumb;

        var thumbImage = thumb.gameObject.AddComponent<Image>();
        thumbImage.sprite = UiFactory.RoundedSprite(4);
        thumbImage.type = Image.Type.Sliced;
        thumbImage.color = new Color(UiFactory.Accent.r, UiFactory.Accent.g, UiFactory.Accent.b, 0.75f);
    }

    private void BuildFooter(RectTransform panel)
    {
        _hint = UiFactory.Label(panel, string.Empty, 19f, TextAlignmentOptions.Center, UiFactory.TextMuted);
        _hint.rectTransform.anchorMin = new Vector2(0f, 0f);
        _hint.rectTransform.anchorMax = new Vector2(1f, 0f);
        _hint.rectTransform.pivot = new Vector2(0.5f, 0f);
        _hint.rectTransform.offsetMin = new Vector2(PanelPadding, 12f);
        _hint.rectTransform.offsetMax = new Vector2(-PanelPadding, 12f + FooterHeight - 12f);

        // 只有调价模式才露出来，所以它平时不会和中间那行提示打架。
        _resetBackground = UiFactory.Rounded(panel, UiFactory.PanelHighlight, 10);
        _resetButton = _resetBackground.rectTransform;
        _resetButton.anchorMin = new Vector2(0f, 0f);
        _resetButton.anchorMax = new Vector2(0f, 0f);
        _resetButton.pivot = new Vector2(0f, 0f);
        _resetButton.anchoredPosition = new Vector2(PanelPadding, 10f);
        _resetButton.sizeDelta = new Vector2(176f, 36f);
        _resetButton.gameObject.SetActive(false);

        var resetLabel = UiFactory.Label(_resetButton, "重置全部调价", 18f, TextAlignmentOptions.Center, UiFactory.TextPrimary);
        UiFactory.Stretch(resetLabel.rectTransform);

        // 右下角这两颗也只有调价模式才露出来：调价的人才需要「存一份 / 换一份」。
        // 从右往左排：最右是「保存为预设」（调完价的下一步动作），它左边是「选择预设 ▾」。
        _presetSaveButton = BuildFooterButton(
            panel, "保存为预设", 200f, -PanelPadding, UiFactory.Accent, out _presetSaveBackground);
        _presetPickButton = BuildFooterButton(
            panel, "选择预设 ▾", 240f, -PanelPadding - 208f, UiFactory.PanelHighlight, out _presetPickBackground);
    }

    /// <summary>底栏右下角那种小按钮：锚在右下、默认藏起来，布局和「重置全部调价」同一行。</summary>
    private static RectTransform BuildFooterButton(
        RectTransform panel, string text, float width, float x, Color color, out Image background)
    {
        background = UiFactory.Rounded(panel, color, 10);

        var rect = background.rectTransform;
        rect.anchorMin = new Vector2(1f, 0f);
        rect.anchorMax = new Vector2(1f, 0f);
        rect.pivot = new Vector2(1f, 0f);
        rect.anchoredPosition = new Vector2(x, 10f);
        rect.sizeDelta = new Vector2(width, 36f);
        rect.gameObject.SetActive(false);

        var label = UiFactory.Label(rect, text, 18f, TextAlignmentOptions.Center, UiFactory.TextPrimary);
        UiFactory.Stretch(label.rectTransform);
        return rect;
    }

    /// <summary>调价模式那几颗底栏按钮一起显隐（重置全部调价 / 保存为预设 / 选择预设）。</summary>
    private void SetPriceEditingButtons(bool on)
    {
        _resetButton.gameObject.SetActive(on);
        _presetSaveButton.gameObject.SetActive(on);
        _presetPickButton.gameObject.SetActive(on);
    }

    private static void AnchorTopLeft(RectTransform rect, float x, float y, float width, float height)
    {
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = new Vector2(x, y);
        rect.sizeDelta = new Vector2(width, height);
    }

    private static void AnchorTopRight(RectTransform rect, float x, float y, float width, float height)
    {
        rect.anchorMin = new Vector2(1f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(1f, 1f);
        rect.anchoredPosition = new Vector2(x, y);
        rect.sizeDelta = new Vector2(width, height);
    }

    // ── 显隐 ────────────────────────────────────────────────────
    public void Show(HextechState state)
    {
        if (IsOpen || state == null)
        {
            return;
        }

        _state = state;
        _hoverTab = -1;
        _hoverCard = -1;
        _scrollDragging = false;

        // 先开画布，RectTransform 的尺寸才是最新的，后面算布局靠它。
        _canvas.enabled = true;
        Refresh();

        _introTime = 0f;
        _rootGroup.alpha = 0f;

        _editingPrices = false;
        SetPriceEditingButtons(false);
        RefreshHint();
        IsOpen = true;

        HextechWindowHost.Push();
    }

    public void Hide()
    {
        if (!IsOpen)
        {
            return;
        }

        // 商店自己关掉时把价格预设弹层一起收起来，别让它留在屏幕上挡住后面的输入。
        HextechPresetPicker.ForceClose();

        _canvas.enabled = false;
        IsOpen = false;
        _state = null;
        _hoverTab = -1;
        _hoverCard = -1;
        _scrollDragging = false;

        // 调价模式是「这一次开商店」的事，关掉就退出，免得下次进来一点到卡片就以为是购买。
        _editingPrices = false;
        SetPriceEditingButtons(false);

        HextechWindowHost.Sync();
    }

    /// <summary>底部那行提示：分「买」和「调价」两种口吻，联机时还要说明只有房主能改。</summary>
    private void RefreshHint()
    {
        if (_editingPrices)
        {
            _hint.text = ShopPricing.CanEdit
                ? "调价模式 · 点 − / ＋ 改这一件的基础价（按住 Shift 一次 5 枚）· 右下可存成预设 / 换预设 · 再按 T 退出"
                : "联机时只有房主能调价";
            return;
        }

        _hint.text = $"滚轮下滑浏览全部商品 · 点击卡片购买 · 按 {ModConfig.ShopKey.Value} 或 ESC 关闭"
                     + (ShopPricing.CanEdit ? " · 按 T 调价" : string.Empty);
    }

    private void SetPriceEditing(bool on)
    {
        if (on && !ShopPricing.CanEdit)
        {
            HextechHud.Toast("联机时只有房主能调整商店价格");
            return;
        }

        if (_editingPrices == on)
        {
            return;
        }

        _editingPrices = on;
        SetPriceEditingButtons(on);
        RefreshHint();
        RefreshVisuals();
    }

    /// <summary>调价模式下的点击：先看「重置全部」，再看卡片上的 − / ＋。</summary>
    private bool TryPriceClick(Vector2 mouse, int hoverCard)
    {
        if (!ShopPricing.CanEdit)
        {
            return false;
        }

        if (RectTransformUtility.RectangleContainsScreenPoint(_resetButton, mouse, null))
        {
            ShopPricing.ResetAll(_state);
            HextechHud.Toast("商店价格已全部恢复默认");
            return true;
        }

        // 这两颗走弹层（自己搭的一层，见 HextechPresetPicker）——它是独立画布，点完回来这里接着跑。
        if (RectTransformUtility.RectangleContainsScreenPoint(_presetSaveButton, mouse, null))
        {
            HextechPresetPicker.OpenName(PricePresetActions.Save);
            return true;
        }

        if (RectTransformUtility.RectangleContainsScreenPoint(_presetPickButton, mouse, null))
        {
            HextechPresetPicker.OpenPick(ApplyPricePreset, PricePresetActions.Save);
            return true;
        }

        if (hoverCard < 0 || hoverCard >= _cards.Count || hoverCard >= _visible.Count)
        {
            return false;
        }

        var card = _cards[hoverCard];
        var offer = _visible[hoverCard];
        var step = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) ? 5 : 1;

        if (RectTransformUtility.RectangleContainsScreenPoint(card.MinusArea, mouse, null))
        {
            AdjustPrice(offer, -step);
            return true;
        }

        if (RectTransformUtility.RectangleContainsScreenPoint(card.PlusArea, mouse, null))
        {
            AdjustPrice(offer, step);
            return true;
        }

        return false;
    }

    /// <summary>换上一份价格预设（或默认配置），然后刷新整屏 —— 价格、可买状态都会跟着变。</summary>
    private void ApplyPricePreset(PricePreset? preset)
    {
        PricePresetActions.Apply(preset, _state);
        RefreshVisuals();
    }

    /// <summary>右下角两颗预设按钮的悬停高亮（和界面里其它按钮一样手算，不接 EventSystem）。</summary>
    private void RefreshPresetButtonColors(Vector2 mouse)
    {
        var hoverSave = RectTransformUtility.RectangleContainsScreenPoint(_presetSaveButton, mouse, null);
        var hoverPick = RectTransformUtility.RectangleContainsScreenPoint(_presetPickButton, mouse, null);

        _presetSaveBackground.color = hoverSave
            ? Color.Lerp(UiFactory.Accent, UiFactory.TextPrimary, 0.30f)
            : UiFactory.Accent;
        _presetPickBackground.color = hoverPick
            ? Color.Lerp(UiFactory.PanelHighlight, UiFactory.Accent, 0.45f)
            : UiFactory.PanelHighlight;
    }

    /// <summary>改价以「基础价」为单位：没改过就从默认基础价开始加减。</summary>
    private void AdjustPrice(ShopOffer offer, int delta)
    {
        var current = ShopPricing.OverrideFor(offer.Id) ?? offer.BaseCost;
        ShopPricing.Set(offer.Id, current + delta, _state);
    }

    private void Update()
    {
        if (!IsOpen)
        {
            return;
        }

        // 价格预设弹层盖在上面时，这一帧的输入全归它：不跳过去的话，同一次点击会同时落到弹层和
        // 底下的按钮上（两边都在自己的 Update 里读 GetMouseButtonDown），刚关掉弹层那一下也会穿过来。
        if (HextechPresetPicker.BlocksInput)
        {
            return;
        }

        // ESC（游戏原生的取消键）会先被窗口吃掉并关掉窗口，这里负责把界面一起收起来；
        // 后面那半句是兜底：万一下手时游戏没把 ESC 接到窗口取消上，也能自己关掉。
        if (!HextechWindowHost.IsOpen || Input.GetKeyDown(KeyCode.Escape))
        {
            Hide();
            return;
        }

        // 抽奖动画在演的时候，商店整体不接受输入。
        if (HextechRoll.Instance != null && HextechRoll.Instance.IsPlaying)
        {
            return;
        }

        _introTime += Time.deltaTime;

        var mouse = (Vector2)Input.mousePosition;
        var overBody = RectTransformUtility.RectangleContainsScreenPoint(_viewport, mouse, null);

        var hoverTab = -1;

        for (var i = 0; i < _tabRects.Count; i++)
        {
            if (RectTransformUtility.RectangleContainsScreenPoint(_tabRects[i], mouse, null))
            {
                hoverTab = i;
                break;
            }
        }

        // 卡片可能滚出可视区，所以除了命中卡片本身，还要求鼠标在滚动区内。
        var hoverCard = -1;

        if (overBody)
        {
            for (var i = 0; i < _cards.Count; i++)
            {
                if (RectTransformUtility.RectangleContainsScreenPoint(_cards[i].Root, mouse, null))
                {
                    hoverCard = i;
                    break;
                }
            }
        }

        _hoverTab = hoverTab;
        _hoverCard = hoverCard;

        HandleScroll(mouse, overBody);
        Animate();
        RefreshVisuals();

        // T 在「购买」和「调价」之间切换（只有单人 / 房主能进调价模式）。
        if (Input.GetKeyDown(KeyCode.T))
        {
            SetPriceEditing(!_editingPrices);
        }

        if (_editingPrices)
        {
            RefreshPresetButtonColors(mouse);

            // 调价模式下只认分类标签和 − / ＋ / 重置，点卡片本体不再购买 —— 免得手一抖买错东西。
            if (Input.GetMouseButtonDown(0))
            {
                if (hoverTab >= 0)
                {
                    SetTab(hoverTab);
                }
                else
                {
                    TryPriceClick(mouse, hoverCard);
                }
            }

            return;
        }

        var quickCount = Mathf.Min(_visible.Count, 9);

        for (var i = 0; i < quickCount; i++)
        {
            if (Input.GetKeyDown(KeyCode.Alpha1 + i))
            {
                Buy(i);
                return;
            }
        }

        if (!Input.GetMouseButtonDown(0))
        {
            return;
        }

        if (hoverTab >= 0)
        {
            SetTab(hoverTab);
            return;
        }

        if (hoverCard >= 0)
        {
            Buy(hoverCard);
        }
    }

    /// <summary>
    /// 滚动：滚轮一格走固定距离，另外可以按住右侧滑块拖。
    /// 拖拽状态优先于点击，避免拖滑块时误买到旁边的卡片。
    /// </summary>
    private void HandleScroll(Vector2 mouse, bool overBody)
    {
        if (_maxScroll <= 0.01f)
        {
            _scroll = 0f;
            _scrollTarget = 0f;
            _scrollDragging = false;
            return;
        }

        if (_scrollDragging)
        {
            if (!Input.GetMouseButton(0))
            {
                _scrollDragging = false;
            }
            else
            {
                var travel = _viewport.rect.height - _scrollThumb.sizeDelta.y;

                // 鼠标位移是屏幕像素，滑块行程是 UI 单位，中间要过一遍缩放系数，
                // 否则不同分辨率下拖动手感会差很多。
                var scale = _canvas.scaleFactor <= 0f ? 1f : _canvas.scaleFactor;
                var delta = (mouse.y - _dragStartMouseY) / scale;

                if (travel > 1f)
                {
                    _scrollTarget = Mathf.Clamp(
                        _dragStartScroll - (delta * (_maxScroll / travel)),
                        0f,
                        _maxScroll);
                }

                _scroll = _scrollTarget;
                return;
            }
        }
        else if (Input.GetMouseButtonDown(0)
                 && RectTransformUtility.RectangleContainsScreenPoint(_scrollTrack, mouse, null))
        {
            _scrollDragging = true;
            _dragStartMouseY = mouse.y;
            _dragStartScroll = _scrollTarget;
            return;
        }

        if (overBody)
        {
            var wheel = Input.mouseScrollDelta.y;

            if (Mathf.Abs(wheel) > 0.01f)
            {
                _scrollTarget = Mathf.Clamp(_scrollTarget - (wheel * WheelStep), 0f, _maxScroll);
            }
        }

        _scroll = Mathf.Lerp(_scroll, _scrollTarget, Mathf.Min(1f, Time.deltaTime * ScrollLerp));

        if (Mathf.Abs(_scroll - _scrollTarget) < 0.4f)
        {
            _scroll = _scrollTarget;
        }

        _content.anchoredPosition = new Vector2(0f, _scroll);
    }

    /// <summary>入场淡入 + 悬停放大/上浮，全部用时间驱动，不用任何动画组件。</summary>
    private void Animate()
    {
        _rootGroup.alpha = Mathf.MoveTowards(_rootGroup.alpha, 1f, Time.deltaTime * 8f);

        var panelT = UiFactory.EaseOutCubic(Mathf.Clamp01(_introTime / 0.22f));
        _panelRect.localScale = Vector3.one * Mathf.Lerp(0.97f, 1f, panelT);

        for (var i = 0; i < _cards.Count; i++)
        {
            var card = _cards[i];
            var targetHover = i == _hoverCard ? 1f : 0f;
            card.Hover = Mathf.MoveTowards(card.Hover, targetHover, Time.deltaTime * 7f);

            // 只给前面一排做错落入场，后面的商品滑下去时早就到终点了。
            var delay = Mathf.Min(i, 9) * IntroStagger;
            var t = UiFactory.EaseOutCubic(Mathf.Clamp01((_introTime - delay) / IntroDuration));

            card.Group.alpha = t;

            var scale = Mathf.Lerp(0.94f, 1f, t) * Mathf.Lerp(1f, 1.02f, card.Hover);
            card.Root.localScale = new Vector3(scale, scale, 1f);
            card.Root.anchoredPosition = card.BasePosition + new Vector2(0f, Mathf.Lerp(-14f, 0f, t) + (4f * card.Hover));
        }
    }

    private void SetTab(int tab)
    {
        if (tab == _tab || tab < 0 || tab >= HextechShopCatalog.TabCount)
        {
            return;
        }

        _tab = tab;
        Refresh();
    }

    private void Refresh()
    {
        var offers = HextechShopCatalog.Offers(_tab);

        _visible.Clear();

        for (var i = 0; i < offers.Count; i++)
        {
            _visible.Add(offers[i]);
        }

        RebuildCards();

        _empty.gameObject.SetActive(_visible.Count == 0);
        _empty.text = "这一类暂时没有商品";

        RefreshVisuals();
    }

    /// <summary>买完东西只刷新数值和颜色，不重建卡片（重建会让悬停状态跳一下）。</summary>
    private void RefreshAffordable()
    {
        RefreshVisuals();
    }

    private void RefreshVisuals()
    {
        var tokens = _state?.Tokens ?? 0f;

        _tokens.text = $"◈ {HextechState.FormatTokens(tokens)}";
        _tokens.color = tokens > 0 ? UiFactory.Warning : UiFactory.TextMuted;
        _tokenBadge.color = tokens > 0 ? UiFactory.PanelHighlight : UiFactory.PanelBackgroundLight;

        var editing = _editingPrices && ShopPricing.CanEdit;

        for (var i = 0; i < _cards.Count; i++)
        {
            var offer = _visible[i];
            var card = _cards[i];
            var soldOut = _state != null && offer.SoldOut(_state);
            var available = _state != null && offer.Available(_state);
            var overridePrice = ShopPricing.OverrideFor(offer.Id);
            var cost = editing ? (overridePrice ?? offer.BaseCost) : offer.CostFor(_state);
            var affordable = tokens >= cost;
            var interactive = available && affordable;

            // 调价模式下所有卡片都亮着：机场里买不了的物资也得能改价。
            var lit = available || editing;

            card.MinusArea.gameObject.SetActive(editing);
            card.PlusArea.gameObject.SetActive(editing);

            card.Cost.text = $"◈ {cost}";

            if (editing)
            {
                // 这里的数字是基础价（物价倍率另算），改成别的数就直接覆盖它。
                card.Cost.color = overridePrice.HasValue ? UiFactory.Accent : UiFactory.Warning;
                card.CostBackground.color = overridePrice.HasValue
                    ? Color.Lerp(UiFactory.PanelHighlight, UiFactory.Accent, 0.30f + (0.35f * card.Hover))
                    : UiFactory.PanelHighlight;

                card.Hint.text = overridePrice.HasValue ? $"已改价 · 默认 {offer.BaseCost}" : "默认价";
                card.Hint.color = UiFactory.TextMuted;

                var stepColor = overridePrice.HasValue ? UiFactory.Accent : UiFactory.PanelBackgroundLight;
                card.MinusBackground.color = stepColor;
                card.PlusBackground.color = stepColor;
            }
            else
            {
                card.Cost.color = !available
                    ? UiFactory.TextMuted
                    : (affordable ? UiFactory.Warning : UiFactory.Danger);

                // 限购卖完和「池子抽空」都是置灰，但提示词分开，不然玩家不知道是自己买过了。
                card.Hint.text = soldOut
                    ? "本局限购，已买过"
                    : (!available
                        ? offer.UnavailableHint
                        : (affordable ? (i == _hoverCard ? "点击购买" : (i < 9 ? $"[{i + 1}]" : string.Empty)) : "代币不足"));
                card.Hint.color = interactive ? UiFactory.TextMuted : UiFactory.Danger;

                card.CostBackground.color = interactive
                    ? Color.Lerp(UiFactory.PanelHighlight, offer.Accent, 0.30f + (0.35f * card.Hover))
                    : new Color(0.12f, 0.11f, 0.10f, 0.9f);
            }

            card.Background.color = lit
                ? Color.Lerp(UiFactory.PanelBackgroundLight, offer.Accent, 0.26f * card.Hover)
                : UiFactory.PanelBackground;

            card.Strip.color = lit ? offer.Accent : UiFactory.TextMuted;
            card.IconFrame.color = lit
                ? new Color(0f, 0f, 0f, 0.32f)
                : new Color(0f, 0f, 0f, 0.45f);
            card.Title.color = lit ? UiFactory.TextPrimary : UiFactory.TextMuted;
            card.Tag.color = lit ? offer.Accent : UiFactory.TextMuted;

            // 买不起的图标压暗，一眼就能看出来；调价模式下不压暗，图标得看得清。
            var dim = lit && (editing || interactive) ? 1f : 0.42f;
            card.Icon.color = new Color(dim, dim, dim, 1f);
            card.Glyph.color = lit ? offer.Accent : UiFactory.TextMuted;
        }

        for (var i = 0; i < _tabImages.Count; i++)
        {
            var active = i == _tab;

            _tabImages[i].color = active
                ? Color.Lerp(UiFactory.PanelHighlight, UiFactory.Accent, 0.55f)
                : (i == _hoverTab ? UiFactory.PanelBackgroundLight : UiFactory.PanelHighlight);

            _tabLabels[i].color = active
                ? new Color(0.086f, 0.078f, 0.067f, 1f)
                : (i == _hoverTab ? UiFactory.TextPrimary : UiFactory.TextMuted);

            var underline = UiFactory.Accent;
            underline.a = active || i == _hoverTab ? 1f : 0f;
            _tabUnderlines[i].color = underline;
        }

        UpdateScrollBar();
    }

    /// <summary>把滚动位置同步到滑块：内容不够长时整条隐藏。</summary>
    private void UpdateScrollBar()
    {
        if (_maxScroll <= 0.01f)
        {
            _scrollTrack.gameObject.SetActive(false);
            return;
        }

        _scrollTrack.gameObject.SetActive(true);

        _scrollThumb.sizeDelta = new Vector2(
            ThumbWidth,
            Mathf.Max(56f, _viewport.rect.height * (_viewport.rect.height / _content.sizeDelta.y)));

        var travel = _viewport.rect.height - _scrollThumb.sizeDelta.y;
        var t = Mathf.Clamp01(_scroll / _maxScroll);
        _scrollThumb.anchoredPosition = new Vector2(0f, -t * travel);
    }

    // ── 卡片构建 ────────────────────────────────────────────────
    private void RebuildCards()
    {
        for (var i = _content.childCount - 1; i >= 0; i--)
        {
            var child = _content.GetChild(i).gameObject;
            child.SetActive(false);
            Destroy(child);
        }

        _cards.Clear();
        _introTime = 0f;
        _scroll = 0f;
        _scrollTarget = 0f;
        _content.anchoredPosition = Vector2.zero;

        var viewWidth = _viewport.rect.width;

        if (viewWidth < 100f)
        {
            viewWidth = FallbackBodyWidth;
        }

        _cardWidth = Mathf.Floor((viewWidth - ((Columns - 1) * CardGapX)) / Columns);

        var rows = Mathf.Max(1, Mathf.CeilToInt(_visible.Count / (float)Columns));
        var contentHeight = (rows * CardHeight) + ((rows - 1) * CardGapY) + (ContentPadY * 2f);

        _content.sizeDelta = new Vector2(0f, contentHeight);

        var viewHeight = _viewport.rect.height;

        if (viewHeight < 100f)
        {
            viewHeight = FallbackBodyHeight;
        }

        _maxScroll = Mathf.Max(0f, contentHeight - viewHeight);

        var totalWidth = (Columns * _cardWidth) + ((Columns - 1) * CardGapX);
        var left = Mathf.Max(0f, (viewWidth - totalWidth) * 0.5f);

        for (var i = 0; i < _visible.Count; i++)
        {
            var column = i % Columns;
            var row = i / Columns;
            var position = new Vector2(
                left + (column * (_cardWidth + CardGapX)),
                -(ContentPadY + (row * (CardHeight + CardGapY))));

            _cards.Add(BuildCard(_visible[i], i, position));
        }

        UpdateScrollBar();
    }

    private CardView BuildCard(ShopOffer offer, int index, Vector2 position)
    {
        var card = UiFactory.Node($"Card{index}", _content);
        card.anchorMin = new Vector2(0f, 1f);
        card.anchorMax = new Vector2(0f, 1f);
        card.pivot = new Vector2(0f, 1f);
        card.anchoredPosition = position;
        card.sizeDelta = new Vector2(_cardWidth, CardHeight);
        card.localScale = Vector3.one * 0.94f;

        var background = UiFactory.Rounded(card, UiFactory.PanelBackgroundLight, 16);
        UiFactory.Stretch(background.rectTransform);

        var group = UiFactory.Group(card);
        group.alpha = 0f;

        // 左侧那条稀有度色带。
        var strip = UiFactory.Rounded(card, offer.Accent, 3);
        strip.rectTransform.anchorMin = new Vector2(0f, 0f);
        strip.rectTransform.anchorMax = new Vector2(0f, 1f);
        strip.rectTransform.pivot = new Vector2(0f, 0.5f);
        strip.rectTransform.offsetMin = new Vector2(0f, 12f);
        strip.rectTransform.offsetMax = new Vector2(6f, -12f);

        var iconFrame = UiFactory.Rounded(card, new Color(0f, 0f, 0f, 0.32f), 14);
        AnchorTopLeft(iconFrame.rectTransform, 18f, -18f, 100f, 100f);
        // 卡片竖向编排：左上是图标，右上是标题/标签/说明，底部一行放提示和价格。
        // 说明的右边界要留出卡片内边距，下边界要停在底部价格行之上，避免压字。

        var icon = UiFactory.Icon(iconFrame.transform, offer.Icon);
        icon.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
        icon.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        icon.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        icon.rectTransform.anchoredPosition = Vector2.zero;
        icon.rectTransform.sizeDelta = new Vector2(76f, 76f);
        icon.gameObject.SetActive(offer.Icon != null);

        var glyph = UiFactory.Label(iconFrame.transform, offer.Glyph, 44f, TextAlignmentOptions.Center, offer.Accent);
        UiFactory.Stretch(glyph.rectTransform);
        glyph.gameObject.SetActive(offer.Icon == null);

        var title = UiFactory.Label(card, offer.Title, 24f, TextAlignmentOptions.Left, UiFactory.TextPrimary);
        title.textWrappingMode = TextWrappingModes.NoWrap;
        title.overflowMode = TextOverflowModes.Ellipsis;
        title.rectTransform.anchorMin = new Vector2(0f, 1f);
        title.rectTransform.anchorMax = new Vector2(1f, 1f);
        title.rectTransform.pivot = new Vector2(0.5f, 1f);
        title.rectTransform.offsetMin = new Vector2(132f, -48f);
        title.rectTransform.offsetMax = new Vector2(-18f, -18f);

        var tag = UiFactory.Label(card, offer.Tag, 17f, TextAlignmentOptions.Left, offer.Accent);
        tag.textWrappingMode = TextWrappingModes.NoWrap;
        tag.overflowMode = TextOverflowModes.Ellipsis;
        tag.rectTransform.anchorMin = new Vector2(0f, 1f);
        tag.rectTransform.anchorMax = new Vector2(1f, 1f);
        tag.rectTransform.pivot = new Vector2(0.5f, 1f);
        tag.rectTransform.offsetMin = new Vector2(132f, -72f);
        tag.rectTransform.offsetMax = new Vector2(-18f, -50f);

        var description = UiFactory.Label(card, offer.Description, 16f, TextAlignmentOptions.TopLeft, UiFactory.TextMuted);
        description.rectTransform.anchorMin = new Vector2(0f, 1f);
        description.rectTransform.anchorMax = new Vector2(1f, 1f);
        description.rectTransform.pivot = new Vector2(0.5f, 1f);
        description.rectTransform.offsetMin = new Vector2(132f, -136f);
        description.rectTransform.offsetMax = new Vector2(-18f, -78f);
        description.overflowMode = TextOverflowModes.Ellipsis;

        var costBackground = UiFactory.Rounded(card, UiFactory.PanelHighlight, 12);
        costBackground.rectTransform.anchorMin = new Vector2(1f, 0f);
        costBackground.rectTransform.anchorMax = new Vector2(1f, 0f);
        costBackground.rectTransform.pivot = new Vector2(1f, 0f);
        costBackground.rectTransform.anchoredPosition = new Vector2(-18f, 14f);
        costBackground.rectTransform.sizeDelta = new Vector2(150f, 42f);

        var cost = UiFactory.Label(costBackground.transform, string.Empty, 23f, TextAlignmentOptions.Center, UiFactory.Warning);
        UiFactory.Stretch(cost.rectTransform);

        var hint = UiFactory.Label(card, string.Empty, 17f, TextAlignmentOptions.Left, UiFactory.TextMuted);
        hint.rectTransform.anchorMin = new Vector2(0f, 0f);
        hint.rectTransform.anchorMax = new Vector2(0f, 0f);
        hint.rectTransform.pivot = new Vector2(0f, 0f);
        hint.rectTransform.anchoredPosition = new Vector2(20f, 14f);
        hint.rectTransform.sizeDelta = new Vector2(170f, 42f);
        hint.alignment = TextAlignmentOptions.Left;

        // 调价用的两颗小按钮：紧挨着右下角的价格块排（卡片宽 478，价格块占右侧 168）。
        var minusBackground = UiFactory.Rounded(card, UiFactory.PanelHighlight, 10);
        var minusArea = minusBackground.rectTransform;
        minusArea.anchorMin = new Vector2(1f, 0f);
        minusArea.anchorMax = new Vector2(1f, 0f);
        minusArea.pivot = new Vector2(1f, 0f);
        minusArea.anchoredPosition = new Vector2(-222f, 14f);
        minusArea.sizeDelta = new Vector2(40f, 42f);
        minusArea.gameObject.SetActive(false);

        var minusLabel = UiFactory.Label(minusArea, "−", 24f, TextAlignmentOptions.Center, UiFactory.TextPrimary);
        UiFactory.Stretch(minusLabel.rectTransform);

        var plusBackground = UiFactory.Rounded(card, UiFactory.PanelHighlight, 10);
        var plusArea = plusBackground.rectTransform;
        plusArea.anchorMin = new Vector2(1f, 0f);
        plusArea.anchorMax = new Vector2(1f, 0f);
        plusArea.pivot = new Vector2(1f, 0f);
        plusArea.anchoredPosition = new Vector2(-176f, 14f);
        plusArea.sizeDelta = new Vector2(40f, 42f);
        plusArea.gameObject.SetActive(false);

        var plusLabel = UiFactory.Label(plusArea, "＋", 24f, TextAlignmentOptions.Center, UiFactory.TextPrimary);
        UiFactory.Stretch(plusLabel.rectTransform);

        return new CardView
        {
            Root = card,
            Group = group,
            Background = background,
            Strip = strip,
            IconFrame = iconFrame,
            Icon = icon,
            Glyph = glyph,
            Title = title,
            Tag = tag,
            Description = description,
            CostBackground = costBackground,
            Cost = cost,
            Hint = hint,
            MinusBackground = minusBackground,
            MinusArea = minusArea,
            PlusBackground = plusBackground,
            PlusArea = plusArea,
            BasePosition = position,
        };
    }

    private void Buy(int index)
    {
        if (_state == null || index < 0 || index >= _visible.Count)
        {
            return;
        }

        var offer = _visible[index];

        if (offer.SoldOut(_state))
        {
            HextechHud.Toast($"{offer.Title}：整局只能买 {offer.MaxPerRun} 次，本局已经买过了");
            return;
        }

        if (!offer.Available(_state))
        {
            HextechHud.Toast($"{offer.Title}：{offer.UnavailableToast}");
            return;
        }

        if (!offer.TryBuy(_state, out var rolls))
        {
            HextechHud.Toast($"代币不足：{offer.Title} 需要 {offer.CostFor(_state)} 枚，当前 {HextechState.FormatTokens(_state.Tokens)} 枚");
            return;
        }

        HextechHud.Toast($"已购买 {offer.Title}（-{offer.CostFor(_state)} 代币，剩 {HextechState.FormatTokens(_state.Tokens)}）");

        if (rolls != null && rolls.Count > 0 && HextechRoll.Instance != null)
        {
            HextechRoll.Instance.PlayQueue(rolls, RefreshAffordable);
            return;
        }

        RefreshAffordable();
    }
}
