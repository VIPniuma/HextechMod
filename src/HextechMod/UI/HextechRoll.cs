using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PeakModder.HextechMod;

/// <summary>抽奖轨道上的一个候选格。</summary>
internal sealed class RollSlot
{
    public RollSlot(string title, string tag, Color accent, Texture2D? icon = null, string? detail = null)
    {
        Title = title;
        Tag = tag;
        Accent = accent;
        Icon = icon;
        Detail = detail ?? string.Empty;
    }

    public string Title { get; }

    public string Tag { get; }

    public Color Accent { get; }

    public Texture2D? Icon { get; }

    /// <summary>
    /// 具体效果。轨道上放不下，只有停在中间的结果条会显示，
    /// 让玩家抽完立刻看到「这是什么 + 它到底干嘛」，而不是只有一个名字。
    /// </summary>
    public string Detail { get; }

    /// <summary>没有图标时用名字首字当占位符。</summary>
    public string Glyph => string.IsNullOrEmpty(Title) ? "?" : Title.Substring(0, 1);
}

/// <summary>一次抽奖的展示数据：陪跑的池子 + 真正抽到的结果。</summary>
internal sealed class RollShow
{
    public RollShow(string header, IReadOnlyList<RollSlot> pool, RollSlot result)
    {
        Header = header;
        Pool = pool;
        Result = result;
    }

    public string Header { get; }

    /// <summary>只用来填充陪跑格子，纯装饰。</summary>
    public IReadOnlyList<RollSlot> Pool { get; }

    public RollSlot Result { get; }
}

/// <summary>
/// CS2 开箱那种横向滚动抽奖：轨道高速掠过、缓慢减速停在中间指针处，
/// 然后高亮结果。<para>
/// 结果在调用方那边早就结算完了，这里纯粹是演出，所以联机时不会因为动画而不同步。
/// 播放期间按 空格 / 回车 / 鼠标左键 可以跳过。
/// </para>
/// </summary>
internal sealed class HextechRoll : MonoBehaviour
{
    private const int SlotCount = 42;

    /// <summary>中奖格固定放在这个位置，后面留几格陪着减速，看起来才像随机停的。</summary>
    private const int TargetIndex = 35;

    private const float SlotWidth = 176f;
    private const float SlotHeight = 176f;
    private const float SlotGap = 16f;

    // 总时长从 5.4 秒一路压到现在的 2.6 秒左右：一次三连礼包要连着播 3 遍，
    // 滚动条再慢慢转下去就只剩烦了。
    private const float SpinSeconds = 1.5f;
    private const float SettleSeconds = 0.28f;
    private const float HoldSeconds = 0.85f;

    private enum Phase
    {
        Idle,
        Spin,
        Settle,
        Hold,
    }

    private sealed class SlotView
    {
        public RectTransform Root = null!;
        public Image Frame = null!;
        public Image Edge = null!;
        public Image Glow = null!;
        public RawImage Icon = null!;
        public TextMeshProUGUI Glyph = null!;
        public TextMeshProUGUI Title = null!;
        public TextMeshProUGUI Tag = null!;
    }

    public static HextechRoll? Instance { get; private set; }

    private readonly Queue<RollShow> _queue = new();
    private readonly List<SlotView> _slots = new();

    private Canvas _canvas = null!;
    private RectTransform _canvasRect = null!;
    private CanvasGroup _group = null!;
    private CanvasGroup _resultGroup = null!;
    private RectTransform _track = null!;
    private TextMeshProUGUI _header = null!;
    private TextMeshProUGUI _resultTitle = null!;
    private TextMeshProUGUI _resultTag = null!;
    private TextMeshProUGUI _resultDetail = null!;
    private Image _resultPanel = null!;
    private Image _pointer = null!;

    private Phase _phase = Phase.Idle;
    private RollSlot? _currentResult;
    private Action? _onFinish;
    private float _elapsed;
    private float _startX;
    private float _endX;
    private float _inputBlockedUntil;

    public bool IsPlaying => _phase != Phase.Idle;

    public static HextechRoll Create(Transform parent)
    {
        var go = new GameObject("HextechRoll");
        go.transform.SetParent(parent, false);
        var roll = go.AddComponent<HextechRoll>();
        roll.Build();
        Instance = roll;
        return roll;
    }

    // ── 构建 ─────────────────────────────────────────────────────
    private void Build()
    {
        _canvas = UiFactory.CreateCanvas("HextechRollCanvas", 270);
        _canvas.transform.SetParent(transform, false);
        _canvasRect = _canvas.GetComponent<RectTransform>();

        var root = UiFactory.Stretch(UiFactory.Node("Root", _canvas.transform));
        _group = UiFactory.Group(root);

        var backdrop = UiFactory.Solid(root, new Color(0.035f, 0.030f, 0.024f, 0.86f));
        UiFactory.Stretch(backdrop.rectTransform);

        var vignette = UiFactory.Glow(root, new Color(0.90f, 0.64f, 0.20f, 0.12f));
        vignette.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
        vignette.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        vignette.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        vignette.rectTransform.sizeDelta = new Vector2(1700f, 1000f);

        _header = UiFactory.Label(root, string.Empty, 40f, TextAlignmentOptions.Center, UiFactory.TextPrimary);
        _header.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
        _header.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        _header.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        _header.rectTransform.anchoredPosition = new Vector2(0f, 250f);
        _header.rectTransform.sizeDelta = new Vector2(1500f, 60f);

        var maskRect = UiFactory.Node("Mask", root);
        maskRect.anchorMin = new Vector2(0f, 0.5f);
        maskRect.anchorMax = new Vector2(1f, 0.5f);
        maskRect.pivot = new Vector2(0.5f, 0.5f);
        maskRect.offsetMin = new Vector2(0f, -SlotHeight * 0.5f - 40f);
        maskRect.offsetMax = new Vector2(0f, SlotHeight * 0.5f + 40f);
        maskRect.gameObject.AddComponent<RectMask2D>();

        _track = UiFactory.Node("Track", maskRect);
        _track.anchorMin = new Vector2(0f, 0.5f);
        _track.anchorMax = new Vector2(0f, 0.5f);
        _track.pivot = new Vector2(0f, 0.5f);
        _track.sizeDelta = new Vector2((SlotCount * SlotWidth) + ((SlotCount - 1) * SlotGap), SlotHeight);

        for (var i = 0; i < SlotCount; i++)
        {
            _slots.Add(BuildSlot(i));
        }

        // 中间的指针：一条竖直亮线，两侧各一条渐隐的暗色遮罩，把视觉焦点压在中心。
        var pointerGlow = UiFactory.Glow(maskRect, new Color(1f, 0.86f, 0.45f, 0.30f));
        pointerGlow.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
        pointerGlow.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        pointerGlow.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        pointerGlow.rectTransform.sizeDelta = new Vector2(300f, SlotHeight + 120f);

        _pointer = UiFactory.Solid(maskRect, UiFactory.Warning);
        _pointer.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
        _pointer.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        _pointer.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        _pointer.rectTransform.sizeDelta = new Vector2(5f, SlotHeight + 34f);

        // 结果条：名字 + 品质标签 + 效果说明，三行都留着，抽完不用再去翻 HUD。
        var resultPanel = UiFactory.Panel(root, UiFactory.PanelBackground, 20);
        resultPanel.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
        resultPanel.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        resultPanel.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        resultPanel.rectTransform.anchoredPosition = new Vector2(0f, -235f);
        resultPanel.rectTransform.sizeDelta = new Vector2(900f, 190f);
        _resultPanel = resultPanel;
        _resultGroup = UiFactory.Group(resultPanel.rectTransform);
        _resultGroup.alpha = 0f;

        _resultTitle = UiFactory.Label(resultPanel.transform, string.Empty, 36f, TextAlignmentOptions.Center, UiFactory.TextPrimary);
        _resultTitle.rectTransform.anchorMin = new Vector2(0f, 1f);
        _resultTitle.rectTransform.anchorMax = new Vector2(1f, 1f);
        _resultTitle.rectTransform.pivot = new Vector2(0.5f, 1f);
        _resultTitle.rectTransform.offsetMin = new Vector2(24f, -70f);
        _resultTitle.rectTransform.offsetMax = new Vector2(-24f, -12f);

        _resultTag = UiFactory.Label(resultPanel.transform, string.Empty, 22f, TextAlignmentOptions.Center, UiFactory.TextMuted);
        _resultTag.rectTransform.anchorMin = new Vector2(0f, 1f);
        _resultTag.rectTransform.anchorMax = new Vector2(1f, 1f);
        _resultTag.rectTransform.pivot = new Vector2(0.5f, 1f);
        _resultTag.rectTransform.offsetMin = new Vector2(24f, -100f);
        _resultTag.rectTransform.offsetMax = new Vector2(-24f, -72f);

        _resultDetail = UiFactory.Label(resultPanel.transform, string.Empty, 19f, TextAlignmentOptions.Top, UiFactory.TextPrimary);
        _resultDetail.rectTransform.anchorMin = new Vector2(0f, 0f);
        _resultDetail.rectTransform.anchorMax = new Vector2(1f, 1f);
        _resultDetail.rectTransform.offsetMin = new Vector2(28f, 14f);
        _resultDetail.rectTransform.offsetMax = new Vector2(-28f, -106f);
        _resultDetail.textWrappingMode = TextWrappingModes.Normal;

        var hint = UiFactory.Label(
            root,
            "空格 / 回车 / 鼠标左键 跳过",
            20f,
            TextAlignmentOptions.Center,
            new Color(UiFactory.TextMuted.r, UiFactory.TextMuted.g, UiFactory.TextMuted.b, 0.75f));
        hint.rectTransform.anchorMin = new Vector2(0.5f, 0f);
        hint.rectTransform.anchorMax = new Vector2(0.5f, 0f);
        hint.rectTransform.pivot = new Vector2(0.5f, 0f);
        hint.rectTransform.anchoredPosition = new Vector2(0f, 90f);
        hint.rectTransform.sizeDelta = new Vector2(900f, 30f);

        _canvas.enabled = false;
    }

    private SlotView BuildSlot(int index)
    {
        var root = UiFactory.Node($"Slot{index}", _track);
        root.anchorMin = new Vector2(0f, 0.5f);
        root.anchorMax = new Vector2(0f, 0.5f);
        root.pivot = new Vector2(0f, 0.5f);
        root.anchoredPosition = new Vector2(index * (SlotWidth + SlotGap), 0f);
        root.sizeDelta = new Vector2(SlotWidth, SlotHeight);

        var frame = UiFactory.Rounded(root, UiFactory.PanelBackgroundLight, 14);
        UiFactory.Stretch(frame.rectTransform);

        var glow = UiFactory.Glow(root, new Color(1f, 1f, 1f, 0f));
        glow.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
        glow.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        glow.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        glow.rectTransform.sizeDelta = new Vector2(SlotWidth * 1.7f, SlotHeight * 1.7f);

        var edge = UiFactory.Rounded(root, UiFactory.Accent, 4);
        edge.rectTransform.anchorMin = new Vector2(0f, 1f);
        edge.rectTransform.anchorMax = new Vector2(1f, 1f);
        edge.rectTransform.pivot = new Vector2(0.5f, 1f);
        edge.rectTransform.offsetMin = new Vector2(18f, -16f);
        edge.rectTransform.offsetMax = new Vector2(-18f, -8f);

        var icon = UiFactory.Icon(root, null);
        icon.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
        icon.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        icon.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        icon.rectTransform.anchoredPosition = new Vector2(0f, 14f);
        icon.rectTransform.sizeDelta = new Vector2(88f, 88f);

        var glyph = UiFactory.Label(root, "?", 52f, TextAlignmentOptions.Center, UiFactory.Accent);
        glyph.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
        glyph.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        glyph.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        glyph.rectTransform.anchoredPosition = new Vector2(0f, 16f);
        glyph.rectTransform.sizeDelta = new Vector2(120f, 70f);

        var title = UiFactory.Label(root, string.Empty, 19f, TextAlignmentOptions.Center, UiFactory.TextPrimary);
        title.rectTransform.anchorMin = new Vector2(0f, 0f);
        title.rectTransform.anchorMax = new Vector2(1f, 0f);
        title.rectTransform.pivot = new Vector2(0.5f, 0f);
        title.rectTransform.offsetMin = new Vector2(12f, 12f);
        title.rectTransform.offsetMax = new Vector2(-12f, 46f);

        var tag = UiFactory.Label(root, string.Empty, 16f, TextAlignmentOptions.Right, UiFactory.TextMuted);
        tag.rectTransform.anchorMin = new Vector2(1f, 1f);
        tag.rectTransform.anchorMax = new Vector2(1f, 1f);
        tag.rectTransform.pivot = new Vector2(1f, 1f);
        tag.rectTransform.anchoredPosition = new Vector2(-14f, -22f);
        tag.rectTransform.sizeDelta = new Vector2(120f, 26f);

        return new SlotView
        {
            Root = root,
            Frame = frame,
            Edge = edge,
            Glow = glow,
            Icon = icon,
            Glyph = glyph,
            Title = title,
            Tag = tag,
        };
    }

    // ── 播放 ─────────────────────────────────────────────────────
    public void Play(RollShow show, Action? onFinish = null)
    {
        PlayQueue(new[] { show }, onFinish);
    }

    /// <summary>
    /// 连续播放多次抽奖（开行李箱一次可能抽好几个）。
    /// <para>
    /// 正在播的时候**不打断**当前这次：新抽奖直接接到队尾，当前这次演完自然接着播。
    /// 所以抽奖动画还没结束时又去开了一个行李箱，后开的那些一样会按顺序演完，不会被吞掉。
    /// </para>
    /// </summary>
    public void PlayQueue(IReadOnlyList<RollShow> shows, Action? onFinish = null)
    {
        if (shows.Count == 0)
        {
            onFinish?.Invoke();
            return;
        }

        for (var i = 0; i < shows.Count; i++)
        {
            _queue.Enqueue(shows[i]);
        }

        // 已经在播了：队列和收尾回调都保留，只把新的那一段接到后面。
        if (IsPlaying)
        {
            _onFinish = CombineCallbacks(_onFinish, onFinish);
            return;
        }

        _onFinish = onFinish;
        Advance();
    }

    /// <summary>两段收尾回调串成一段（一次排队里可能攒了好几个行李箱的开奖播报）。</summary>
    private static Action? CombineCallbacks(Action? first, Action? second)
    {
        if (first == null)
        {
            return second;
        }

        if (second == null)
        {
            return first;
        }

        return () =>
        {
            first();
            second();
        };
    }

    private void Advance()
    {
        if (_queue.Count == 0)
        {
            FinishAll();
            return;
        }

        Present(_queue.Dequeue());
    }

    private void Present(RollShow show)
    {
        _header.text = show.Header;
        _currentResult = show.Result;
        FillSlots(show);
        ResetHighlight();

        // 位置要等布局算完，所以放在这里而不是 Build 里。
        var centerInTrack = (TargetIndex * (SlotWidth + SlotGap)) + (SlotWidth * 0.5f);
        _endX = (_canvasRect.rect.width * 0.5f) - centerInTrack;
        _startX = _endX + ((SlotCount * (SlotWidth + SlotGap)) * 0.62f);

        _track.anchoredPosition = new Vector2(_startX, 0f);
        _pointer.color = UiFactory.Warning;

        _resultPanel.color = UiFactory.PanelBackground;
        _resultTitle.text = string.Empty;
        _resultTag.text = string.Empty;
        _resultDetail.text = string.Empty;
        _resultDetail.gameObject.SetActive(false);
        _resultGroup.alpha = 0f;

        _group.alpha = 0f;
        _canvas.enabled = true;
        _phase = Phase.Spin;
        _elapsed = 0f;

        // 触发抽奖的那一次点击不要立刻把动画跳过。
        _inputBlockedUntil = Time.time + 0.25f;
    }

    private void Update()
    {
        if (_phase == Phase.Idle)
        {
            return;
        }

        _group.alpha = Mathf.MoveTowards(_group.alpha, 1f, Time.deltaTime * 8f);

        var skip = Time.time >= _inputBlockedUntil && SkipPressed();

        switch (_phase)
        {
            case Phase.Spin:
            {
                _elapsed += Time.deltaTime;
                var t = Mathf.Clamp01(_elapsed / SpinSeconds);
                _track.anchoredPosition = new Vector2(Mathf.Lerp(_startX, _endX, UiFactory.EaseOutQuint(t)), 0f);

                if (skip || t >= 1f)
                {
                    _track.anchoredPosition = new Vector2(_endX, 0f);
                    _phase = Phase.Settle;
                    _elapsed = 0f;
                }

                break;
            }

            case Phase.Settle:
            {
                _elapsed += Time.deltaTime;
                var t = Mathf.Clamp01(_elapsed / SettleSeconds);
                HighlightWinning(t);

                if (skip || t >= 1f)
                {
                    HighlightWinning(1f);
                    ShowResult();
                    _phase = Phase.Hold;
                    _elapsed = 0f;
                }

                break;
            }

            case Phase.Hold:
            {
                _elapsed += Time.deltaTime;
                _resultGroup.alpha = Mathf.MoveTowards(_resultGroup.alpha, 1f, Time.deltaTime * 6f);

                if (skip || _elapsed >= HoldSeconds)
                {
                    Advance();
                }

                break;
            }
        }
    }

    private void FinishAll()
    {
        _phase = Phase.Idle;
        _canvas.enabled = false;

        var callback = _onFinish;
        _onFinish = null;
        callback?.Invoke();
    }

    private static bool SkipPressed()
    {
        return Input.GetMouseButtonDown(0)
               || Input.GetKeyDown(KeyCode.Space)
               || Input.GetKeyDown(KeyCode.Return)
               || Input.GetKeyDown(KeyCode.KeypadEnter);
    }

    private void FillSlots(RollShow show)
    {
        for (var i = 0; i < _slots.Count; i++)
        {
            var slot = i == TargetIndex ? show.Result : Pick(show);
            ApplySlot(_slots[i], slot);
        }
    }

    private static RollSlot Pick(RollShow show)
    {
        if (show.Pool.Count == 0)
        {
            return show.Result;
        }

        return show.Pool[UnityEngine.Random.Range(0, show.Pool.Count)];
    }

    private static void ApplySlot(SlotView view, RollSlot slot)
    {
        view.Frame.color = Color.Lerp(UiFactory.PanelBackgroundLight, slot.Accent, 0.22f);
        view.Edge.color = slot.Accent;
        view.Title.text = slot.Title;
        view.Tag.text = slot.Tag;
        view.Tag.color = slot.Accent;
        view.Icon.texture = slot.Icon;

        var hasIcon = slot.Icon != null;
        view.Icon.gameObject.SetActive(hasIcon);
        view.Glyph.gameObject.SetActive(!hasIcon);
        view.Glyph.text = slot.Glyph;
        view.Glyph.color = slot.Accent;
    }

    private void ResetHighlight()
    {
        for (var i = 0; i < _slots.Count; i++)
        {
            _slots[i].Root.localScale = Vector3.one;
            var glow = _slots[i].Glow.color;
            glow.a = 0f;
            _slots[i].Glow.color = glow;
        }
    }

    private void HighlightWinning(float amount)
    {
        var view = _slots[TargetIndex];
        var scale = Mathf.Lerp(1f, 1.10f, amount);
        view.Root.localScale = new Vector3(scale, scale, 1f);

        var accent = view.Edge.color;
        view.Glow.color = new Color(accent.r, accent.g, accent.b, 0.55f * amount);
    }

    private void ShowResult()
    {
        var result = _currentResult;

        if (result == null)
        {
            return;
        }

        _resultPanel.color = Color.Lerp(UiFactory.PanelBackground, result.Accent, 0.18f);
        _resultTitle.text = result.Title;
        _resultTitle.color = UiFactory.TextPrimary;
        _resultTag.text = result.Tag;
        _resultTag.color = result.Accent;

        var hasDetail = !string.IsNullOrEmpty(result.Detail);
        _resultDetail.text = result.Detail;
        _resultDetail.gameObject.SetActive(hasDetail);

        _resultGroup.alpha = 0f;
        _pointer.color = result.Accent;
    }
}
