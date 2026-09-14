using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PeakModder.HextechMod;

/// <summary>
/// 机场里的「禁用海克斯」面板。
/// <para>
/// 每位玩家在这里禁掉一个词条，被禁的词条本局全房间都不会刷出来（三选一、商店、行李箱都算）。
/// 每人只有一票：点别的卡片就是改投，再点自己已经禁掉的那张就是取消。
/// 一局结束回到机场，禁用名单自动清空，可以重新选。
/// </para>
/// <para>
/// 列表一排一个词条：徽标 · 名字 · 效果说明 · 我禁的 / 队友禁的，滚轮上下翻。
/// 点击沿用 mod 里其它面板的做法 ——
/// 自己用 <see cref="RectTransformUtility.RectangleContainsScreenPoint"/> 做命中检测，
/// 不依赖 UGUI 的 EventSystem。
/// </para>
/// </summary>
public sealed class HextechBanPanel : MonoBehaviour
{
    private const float CardWidth = 1280f;
    private const float CardHeight = 860f;

    /// <summary>列表可视区相对卡片上下边的留白，<see cref="ViewportHeight"/> 由它们算出来。</summary>
    private const float ViewportTop = 150f;
    private const float ViewportBottom = 130f;
    private const float ViewportHeight = CardHeight - ViewportTop - ViewportBottom;

    /// <summary>一排一个词条：名字在前、效果在后，长名字和长效果都读得下。</summary>
    private const int Columns = 1;
    private const float EntryGap = 10f;
    private const float EntryHeight = 64f;
    private const float SidePadding = 40f;

    /// <summary>品质徽标那一列占的宽度。</summary>
    private const float GlyphColumn = 50f;

    /// <summary>名字列占的宽度，效果说明紧接着它往右排。</summary>
    private const float TitleColumn = 300f;

    /// <summary>最右侧「我禁的 / 队友禁的」占的宽度。</summary>
    private const float StateColumn = 96f;

    /// <summary>一格滚轮翻多远。</summary>
    private const float ScrollStep = 110f;

    /// <summary>
    /// 机场里预热时每帧建几行。每行都要顺手把 TMP 的网格算出来（<see cref="BuildRow"/> 末尾），
    /// 所以这个数不能大 —— 一次铺太多，等于把卡顿从「打开那一刻」提前到「进机场那一刻」而已。
    /// </summary>
    private const int PrewarmRowsPerFrame = 4;

    /// <summary>可视区之外再多建几行当缓冲，滚起来不容易看到还没建出来的空行。</summary>
    private const int VisibleBufferRows = 4;

    /// <summary>面板开着时一帧最多补多少行（滚太快也不能一次补满，那又回到掉帧的老路）。</summary>
    private const int CatchUpRowsPerFrame = 12;

    private sealed class EntryView
    {
        public HextechEntry Entry = null!;
        public RectTransform Rect = null!;
        public Image Background = null!;
        public TextMeshProUGUI Title = null!;
        public TextMeshProUGUI Description = null!;
        public TextMeshProUGUI State = null!;
        public string LastState = string.Empty;
    }

    public static HextechBanPanel? Instance { get; private set; }

    private Canvas _canvas = null!;
    private CanvasGroup _group = null!;
    private TextMeshProUGUI _summary = null!;
    private TextMeshProUGUI _hint = null!;
    private RectTransform _viewport = null!;
    private RectTransform _content = null!;

    private readonly List<EntryView> _entries = new();

    /// <summary>排好序的词条表（传说在前）。布局算好后就固定了。</summary>
    private readonly List<HextechEntry> _ordered = new();

    /// <summary>布局（内容高度、每行宽高、最大滚动量）是否已经算过。</summary>
    private bool _layoutReady;

    /// <summary>已经建到第几行 —— 列表是分帧铺出来的，见 <see cref="TickPrewarm"/>。</summary>
    private int _builtRows;

    /// <summary>建完之后重新算过网格的行数，见 <see cref="RepolishRows"/>。</summary>
    private int _repolishRows;

    /// <summary>重算网格用的字体，用来发现「算完之后字体又换了」。</summary>
    private TMP_FontAsset? _prewarmFont;

    private float _entryWidth;
    private float _totalWidth;
    private int _hover = -1;
    private float _scroll;
    private float _maxScroll;

    public bool IsOpen { get; private set; }

    public static HextechBanPanel Create(Transform parent)
    {
        var go = new GameObject("HextechBanPanel");
        go.transform.SetParent(parent, false);

        var panel = go.AddComponent<HextechBanPanel>();
        panel.Build();

        Instance = panel;
        return panel;
    }

    private void Build()
    {
        // CreateCanvas 出来的 Canvas 自带 DontDestroyOnLoad，这里不要再挂到别的对象下面：
        // 挂错了父节点，换场景时会被连根销毁。
        _canvas = UiFactory.CreateCanvas("HextechBanCanvas", 252);

        var root = UiFactory.Stretch(UiFactory.Node("Root", _canvas.transform));
        _group = UiFactory.Group(root);

        var backdrop = UiFactory.Solid(root, UiFactory.Backdrop);
        UiFactory.Stretch(backdrop.rectTransform);

        var card = UiFactory.Panel(root, UiFactory.PanelBackground, 26);
        card.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
        card.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        card.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        card.rectTransform.sizeDelta = new Vector2(CardWidth, CardHeight);

        var title = UiFactory.Label(card.transform, "禁用海克斯", 40f, TextAlignmentOptions.Center, UiFactory.TextPrimary);
        var titleRect = title.rectTransform;
        titleRect.anchorMin = new Vector2(0f, 1f);
        titleRect.anchorMax = new Vector2(1f, 1f);
        titleRect.pivot = new Vector2(0.5f, 1f);
        titleRect.offsetMin = new Vector2(SidePadding, -74f);
        titleRect.offsetMax = new Vector2(-SidePadding, -26f);

        var subtitle = UiFactory.Label(
            card.transform,
            "每人禁掉一个词条 · 被禁的词条本局全房间都不会刷出 · 回机场后自动解除",
            22f,
            TextAlignmentOptions.Center,
            UiFactory.TextMuted);
        var subtitleRect = subtitle.rectTransform;
        subtitleRect.anchorMin = new Vector2(0f, 1f);
        subtitleRect.anchorMax = new Vector2(1f, 1f);
        subtitleRect.pivot = new Vector2(0.5f, 1f);
        subtitleRect.offsetMin = new Vector2(SidePadding, -142f);
        subtitleRect.offsetMax = new Vector2(-SidePadding, -80f);

        _viewport = UiFactory.Node("Viewport", card.transform);
        _viewport.anchorMin = new Vector2(0f, 0f);
        _viewport.anchorMax = new Vector2(1f, 1f);
        _viewport.offsetMin = new Vector2(SidePadding, ViewportBottom);
        _viewport.offsetMax = new Vector2(-SidePadding, -ViewportTop);

        // 滚出可视区的卡片必须裁掉，不然会画到面板外面去。
        _viewport.gameObject.AddComponent<RectMask2D>();

        _content = UiFactory.Node("Content", _viewport);
        _content.anchorMin = new Vector2(0f, 1f);
        _content.anchorMax = new Vector2(1f, 1f);
        _content.pivot = new Vector2(0.5f, 1f);
        _content.anchoredPosition = Vector2.zero;
        _content.sizeDelta = Vector2.zero;

        _summary = BottomLabel(card.transform, 88f, 40f, 24f, UiFactory.Accent);
        _hint = BottomLabel(card.transform, 34f, 36f, 20f, UiFactory.TextMuted);

        _canvas.enabled = false;
    }

    public void Show()
    {
        if (IsOpen)
        {
            return;
        }

        _hover = -1;
        _scroll = 0f;
        _group.alpha = 0f;

        // 先把看得见的那几行就地建出来（玩家已经在看了，不能有洞），
        // 剩下的行交给 TickPrewarm 每帧接着补。
        EnsureVisibleRows();

        var key = ModConfig.BanPanelKey.Value;
        var closeHint = key == KeyCode.None ? "ESC" : $"{key} 或 ESC";
        _hint.text = $"滚轮浏览全部词条 · 点击一行禁用 / 取消 · 按 {closeHint} 关闭";

        _canvas.enabled = true;
        IsOpen = true;

        // 打开时顺手问一次房主当前的名单：别人可能是在自己进房间之前就禁好了。
        var owner = Character.localCharacter;

        if (owner != null)
        {
            HextechState.Get(owner)?.RequestBans();
        }

        RefreshVisuals();

        // 这两行是打开时才写进去的，顺手把网格算掉，省得再占一帧。
        _hint.ForceMeshUpdate();
        _summary.ForceMeshUpdate();

        HextechWindowHost.Push();
    }

    public void Hide()
    {
        if (!IsOpen)
        {
            return;
        }

        _canvas.enabled = false;
        IsOpen = false;
        _hover = -1;

        HextechWindowHost.Sync();
    }

    private void Update()
    {
        // 这一句要在 IsOpen 之前：机场里就把列表一行行建起来，玩家按键那一刻才不卡。
        TickPrewarm();

        if (!IsOpen)
        {
            return;
        }

        // 游戏窗口被 ESC 关掉时跟着收起来；后半句是兜底，防止窗口状态和面板脱节。
        if (!HextechWindowHost.IsOpen || Input.GetKeyDown(KeyCode.Escape))
        {
            Hide();
            return;
        }

        // 禁用是给下一局准备的：一离开机场就自动收起来，
        // 免得玩家开着面板被队友拖进图、鼠标还锁在界面上。
        if (!HextechScene.InAirport)
        {
            Hide();
            return;
        }

        _group.alpha = Mathf.MoveTowards(_group.alpha, 1f, Time.deltaTime * 8f);

        HandleScroll();
        HandlePointer();
        RefreshVisuals();
    }

    private void HandleScroll()
    {
        if (_maxScroll <= 0.01f)
        {
            _scroll = 0f;
            return;
        }

        var wheel = Input.mouseScrollDelta.y;

        if (Mathf.Abs(wheel) > 0.01f)
        {
            _scroll = Mathf.Clamp(_scroll - (wheel * ScrollStep), 0f, _maxScroll);
        }

        _content.anchoredPosition = new Vector2(0f, _scroll);
    }

    private void HandlePointer()
    {
        var mouse = (Vector2)Input.mousePosition;
        _hover = -1;

        // 先看鼠标在不在可视区里 —— 滚出去的卡片按屏幕坐标算还在屏幕上，
        // 但已经是被裁掉的部分，不该响应点击。
        if (RectTransformUtility.RectangleContainsScreenPoint(_viewport, mouse, null))
        {
            for (var i = 0; i < _entries.Count; i++)
            {
                var rect = _entries[i].Rect;

                if (rect != null && RectTransformUtility.RectangleContainsScreenPoint(rect, mouse, null))
                {
                    _hover = i;
                    break;
                }
            }
        }

        if (!Input.GetMouseButtonDown(0) || _hover < 0)
        {
            return;
        }

        var id = _entries[_hover].Entry.Id;

        // 点自己已经禁掉的那张 = 取消；点别的 = 把这一票改投过去。
        HextechBans.Vote(HextechBans.IsMine(id) ? null : id);
    }

    private void RefreshVisuals()
    {
        for (var i = 0; i < _entries.Count; i++)
        {
            var view = _entries[i];
            var mine = HextechBans.IsMine(view.Entry.Id);
            var banned = HextechBans.IsBannedByOthers(view.Entry.Id);

            var state = mine ? "我禁的" : banned ? "队友禁的" : string.Empty;

            if (view.LastState != state)
            {
                view.LastState = state;
                view.State.text = state;
            }

            var background = UiFactory.PanelBackgroundLight;

            if (mine)
            {
                background = Color.Lerp(UiFactory.PanelHighlight, UiFactory.Accent, 0.40f);
            }
            else if (banned)
            {
                background = Color.Lerp(UiFactory.PanelBackgroundLight, UiFactory.Danger, 0.35f);
            }

            if (i == _hover)
            {
                background = Color.Lerp(background, UiFactory.Accent, 0.35f);
            }

            view.Background.color = background;
            view.Title.color = banned ? UiFactory.TextMuted : UiFactory.TextPrimary;
            view.State.color = mine ? UiFactory.Accent : UiFactory.Danger;
        }

        var mineEntry = HextechBans.Mine == null ? null : HextechRegistry.Find(HextechBans.Mine);

        _summary.text = mineEntry == null
            ? "你还没有禁用任何词条 —— 点击一行禁用它"
            : $"你禁用了「{mineEntry.Title}」—— 再点一次可以取消";
        _summary.color = mineEntry == null ? UiFactory.TextMuted : UiFactory.Accent;
    }

    /// <summary>
    /// 算好列表内容与布局：排序、内容区高度、最大滚动量、每行宽度。
    /// 真正建行由 <see cref="BuildRow"/> 一行行来（见 <see cref="TickPrewarm"/>）。
    /// </summary>
    private void PrepareLayout()
    {
        if (_layoutReady)
        {
            return;
        }

        _layoutReady = true;

        _ordered.AddRange(HextechRegistry.All);

        // 传说排最前面：会想禁的通常就是那些一出现就决定整局走向的强词条。
        _ordered.Sort((left, right) =>
        {
            var byQuality = ((int)right.Quality).CompareTo((int)left.Quality);
            return byQuality != 0 ? byQuality : string.CompareOrdinal(left.Title, right.Title);
        });

        var rows = (_ordered.Count + Columns - 1) / Columns;
        var contentHeight = (rows * EntryHeight) + (Mathf.Max(0, rows - 1) * EntryGap);

        _content.sizeDelta = new Vector2(0f, contentHeight);
        _maxScroll = Mathf.Max(0f, contentHeight - ViewportHeight);

        var available = CardWidth - (SidePadding * 2f);
        _entryWidth = (available - ((Columns - 1) * EntryGap)) / Columns;
        _totalWidth = (Columns * _entryWidth) + ((Columns - 1) * EntryGap);
    }

    /// <summary>
    /// 预热：机场里就把列表一行行铺出来，而不是等玩家按下按键那一帧才建。
    /// <para>
    /// 82 行 × (底图 + 4 个文字) 一次建完会把按键那一帧卡住好几秒 —— 中文还得现塞进字体图集，
    /// 图集一扩容就是重建纹理 + 全部文本重算 mesh。摊到每帧几行，在机场里掉一两帧没人会察觉。
    /// </para>
    /// </summary>
    private void TickPrewarm()
    {
        if (_layoutReady && _builtRows >= _ordered.Count && !RepolishPending())
        {
            return;
        }

        // 只在「面板开着」或「人在机场（可能马上要开）」时铺。进图之后停手，下次回机场接着铺。
        if (!IsOpen && !HextechScene.InAirport)
        {
            return;
        }

        PrepareLayout();

        // 阶段一：把行铺出来。面板开着的话优先保证「看得见的 + 一点缓冲」建满，别让玩家滚出空白行；
        // 只是机场里预热的话，按固定节奏慢慢铺就行。两种都收个上限：一帧建太多照样会卡。
        if (_builtRows < _ordered.Count)
        {
            var budget = IsOpen
                ? Mathf.Clamp(VisibleRowBudget() - _builtRows, PrewarmRowsPerFrame, CatchUpRowsPerFrame)
                : PrewarmRowsPerFrame;

            BuildPendingRows(budget);
            return;
        }

        // 阶段二：铺完之后再把所有文本的网格重算一遍。
        if (_repolishRows == 0)
        {
            // 记下这一轮用的字体。中文字体 fallback 有可能比我们晚一步就绪，
            // 那样 TMP 会把已经算好的网格全作废，这时靠 RepolishPending 让整轮重来。
            _prewarmFont = UiFactory.ResolveFont();
        }

        RepolishRows(PrewarmRowsPerFrame);
    }

    /// <summary>还有没有网格要重算：没算完，或者字体又换了（换了就得全部重算）。</summary>
    private bool RepolishPending()
    {
        if (_repolishRows < _ordered.Count)
        {
            return true;
        }

        var font = UiFactory.ResolveFont();

        if (font != null && font != _prewarmFont)
        {
            _repolishRows = 0;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 给已经建好的行重算一遍网格。
    /// <para>
    /// 预热的过程中字体图集很可能扩容过：扩容换了图集纹理，TMP 会要求所有用到该字体的文本重算 UV 与网格，
    /// 而这批重算同样会被「Canvas 关着」挡在门外，最后一起堆到打开的那一刻。这里分帧做掉，
    /// 打开时就只剩「把 Canvas 打开」这一件事了。
    /// </para>
    /// </summary>
    private void RepolishRows(int count)
    {
        var end = Mathf.Min(_repolishRows + count, _entries.Count);

        for (; _repolishRows < end; _repolishRows++)
        {
            var view = _entries[_repolishRows];

            view.Title.ForceMeshUpdate();
            view.Description.ForceMeshUpdate();
        }
    }

    /// <summary>
    /// 打开面板时用：先把看得见的那几行就地建出来（玩家已经在看了，不能有洞），
    /// 剩下的行交给 <see cref="TickPrewarm"/> 每帧接着补。
    /// </summary>
    private void EnsureVisibleRows()
    {
        PrepareLayout();
        BuildPendingRows(VisibleRowBudget());
    }

    /// <summary>按当前滚动位置算「至少该建到第几行」（可视行数 + 缓冲）。</summary>
    private int VisibleRowBudget()
    {
        var pitch = EntryHeight + EntryGap;
        return Mathf.CeilToInt((_scroll + ViewportHeight) / pitch) + VisibleBufferRows;
    }

    /// <summary>接着建 <paramref name="count"/> 行（<see cref="Columns"/> 是 1，一行就是一条词条）。</summary>
    private void BuildPendingRows(int count)
    {
        for (var built = 0; built < count && _builtRows < _ordered.Count; built++)
        {
            BuildRow(_builtRows);
            _builtRows++;
        }
    }

    /// <summary>建出第 <paramref name="index"/> 行。分帧调用，所以一次只干一行的活。</summary>
    private void BuildRow(int index)
    {
        var column = index % Columns;
        var row = index / Columns;

        var x = (-_totalWidth / 2f) + (_entryWidth / 2f) + (column * (_entryWidth + EntryGap));
        var y = -(EntryHeight / 2f) - (row * (EntryHeight + EntryGap));

        var rect = UiFactory.Node("BanEntry", _content);
        rect.anchorMin = new Vector2(0.5f, 1f);
        rect.anchorMax = new Vector2(0.5f, 1f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(_entryWidth, EntryHeight);
        rect.anchoredPosition = new Vector2(x, y);

        var background = UiFactory.Rounded(rect, UiFactory.PanelBackgroundLight, 12);
        UiFactory.Stretch(background.rectTransform);

        var glyph = UiFactory.Label(rect, UiFactory.QualityGlyph(_ordered[index].Quality), 24f, TextAlignmentOptions.Center, UiFactory.QualityColor(_ordered[index].Quality));
        var glyphRect = glyph.rectTransform;
        glyphRect.anchorMin = new Vector2(0f, 0.5f);
        glyphRect.anchorMax = new Vector2(0f, 0.5f);
        glyphRect.pivot = new Vector2(0.5f, 0.5f);
        glyphRect.sizeDelta = new Vector2(38f, EntryHeight);
        glyphRect.anchoredPosition = new Vector2(26f, 0f);

        // 名字列：定宽靠左，效果说明紧接着它往右排。
        var label = UiFactory.Label(rect, _ordered[index].Title, 24f, TextAlignmentOptions.Left, UiFactory.TextPrimary);
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.overflowMode = TextOverflowModes.Ellipsis;
        var labelRect = label.rectTransform;
        labelRect.anchorMin = new Vector2(0f, 0f);
        labelRect.anchorMax = new Vector2(0f, 1f);
        labelRect.pivot = new Vector2(0f, 0.5f);
        labelRect.sizeDelta = new Vector2(TitleColumn, 0f);
        labelRect.anchoredPosition = new Vector2(GlyphColumn, 0f);

        // 效果说明：占掉「名字右边到状态左边」的整段，太长就在行尾截断。
        var effect = UiFactory.Label(rect, _ordered[index].Description, 19f, TextAlignmentOptions.Left, UiFactory.TextMuted);
        effect.textWrappingMode = TextWrappingModes.NoWrap;
        effect.overflowMode = TextOverflowModes.Ellipsis;
        var effectRect = effect.rectTransform;
        effectRect.anchorMin = new Vector2(0f, 0f);
        effectRect.anchorMax = new Vector2(1f, 1f);
        effectRect.offsetMin = new Vector2(GlyphColumn + TitleColumn + 12f, 0f);
        effectRect.offsetMax = new Vector2(-StateColumn, 0f);

        var state = UiFactory.Label(rect, string.Empty, 19f, TextAlignmentOptions.Right, UiFactory.TextMuted);
        var stateRect = state.rectTransform;
        stateRect.anchorMin = new Vector2(1f, 0f);
        stateRect.anchorMax = new Vector2(1f, 1f);
        stateRect.offsetMin = new Vector2(-(StateColumn - 6f), 0f);
        stateRect.offsetMax = new Vector2(-12f, 0f);

        // 关键一步：光把对象建出来是不够的。TMP 的排版、字形写进图集、网格生成都发生在「渲染前」那一刻，
        // 而这个面板的 Canvas 平时是关着的 —— 不主动算一次，这些活会被一路拖到按下按键的那一帧才做，
        // 预热就白预了。ForceMeshUpdate 让 TMP 立刻算完，不看 Canvas 开没开。
        glyph.ForceMeshUpdate();
        label.ForceMeshUpdate();
        effect.ForceMeshUpdate();

        _entries.Add(new EntryView
        {
            Entry = _ordered[index],
            Rect = rect,
            Background = background,
            Title = label,
            Description = effect,
            State = state,
        });
    }

    /// <summary>
    /// 在卡片底部开一条横条，并在里面放一个占满的标签。
    /// 标签必须挂在横条底下 —— 直接挂卡片上的话锚点算的是卡片，位置会对不上。
    /// </summary>
    private static TextMeshProUGUI BottomLabel(Transform parent, float bottom, float height, float fontSize, Color color)
    {
        var strip = UiFactory.Node("BottomStrip", parent);
        strip.anchorMin = new Vector2(0f, 0f);
        strip.anchorMax = new Vector2(1f, 0f);
        strip.pivot = new Vector2(0.5f, 0f);
        strip.offsetMin = new Vector2(SidePadding, bottom);
        strip.offsetMax = new Vector2(-SidePadding, bottom + height);

        var label = UiFactory.Label(strip, string.Empty, fontSize, TextAlignmentOptions.Center, color);
        UiFactory.Stretch(label.rectTransform);
        return label;
    }
}
