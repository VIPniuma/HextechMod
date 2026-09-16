using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx.Configuration;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PeakModder.HextechMod;

/// <summary>
/// 游戏内的模组配置面板：把 <see cref="ModConfig"/> 里的配置项直接列出来改，
/// 玩家不用去翻 BepInEx 的 .cfg 文件。
/// <para>
/// 配置项是用反射遍历出来的，所以以后新加 <c>ConfigEntry</c> 不用回来改这里 ——
/// 认得 bool（开关）、数字（点右边输入框敲数字）、按键（点一下重绑）、以及带可选值列表（左右切换）这几种。
/// 面板按配置的 Section 分组、顺序跟声明顺序一致：开关 / 按键 / 列表排成两列，
/// 数值项要用输入框（点一下直接敲数字、敲完就存），所以独占一整行（一排只放一个）。
/// 配置区最后还挂了一行「数值恢复默认」：只把数值项调回默认值，开关和按键不动。
/// 行上只写配置项的名字，完整说明在被指到那一行时显示在面板底部，列表才扫得动。
/// </para>
/// </summary>
public sealed class HextechConfigPanel : MonoBehaviour
{
    private const float CardWidth = 1280f;
    private const float CardHeight = 880f;

    private const float ViewportTop = 150f;
    private const float ViewportBottom = 150f;
    private const float ViewportHeight = CardHeight - ViewportTop - ViewportBottom;

    private const float SidePadding = 40f;
    private const float RowHeight = 56f;
    private const float RowGap = 8f;
    private const float SectionHeight = 48f;

    private const float ScrollStep = 110f;

    /// <summary>
    /// 开关 / 按键 / 可选值列表这类「点一下就行」的行，一行摆几件。
    /// 数值项不受它管 —— 数值行独占一整行，一排只放一个。
    /// </summary>
    private const int Columns = 2;

    private const float ColumnGap = 16f;
    private const float ContentWidth = CardWidth - (SidePadding * 2);
    private const float CellWidth = (ContentWidth - (ColumnGap * (Columns - 1))) / Columns;

    /// <summary>右侧那块「值」的宽度：配置项只要放得下一个「开」字，价格行还要塞下 − / ＋。</summary>
    private const float ValueWidth = 132f;
    private const float PriceValueWidth = 240f;

    /// <summary>「日志上报」那一节最多列几条本机上传记录（再多就只剩编号，翻着也没意义）。</summary>
    private const int ReportRows = 6;

    // ── 数值输入框（数值项） ────────────────────────────────────
    /// <summary>数值行左边名字占的宽度（定死，各行的输入框才能左右对齐）。</summary>
    private const float NumberLabelWidth = 320f;

    /// <summary>右边输入框的宽度（没有范围提示了，框加宽一点，长数字也看得全）。</summary>
    private const float FieldWidth = 480f;

    /// <summary>一行配置（或者一个分组标题、一行商店调价）。</summary>
    private sealed class Row
    {
        public RectTransform Rect = null!;
        public Image Background = null!;
        public ConfigEntryBase? Entry;
        public bool IsSection;

        /// <summary>这一行属于第几页（每个配置分组、日志上报、商店调价各算一页）。</summary>
        public int Page;

        /// <summary>这一行在内容区里的顶边（内容坐标系，向下为正），翻页时用来算每页的滚动范围。</summary>
        public float TopY;

        /// <summary>这一行的高度，配合 <see cref="TopY"/> 算每页的滚动范围。</summary>
        public float Height;

        /// <summary>小标题（如物资分类）不算新的一页，跟在它所属的大分组里。</summary>
        public bool IsSubHeader;

        /// <summary>「商店调价」那个标题行：文案要跟着「是不是房主」变。</summary>
        public bool IsPriceSection;

        /// <summary>商店里的某一件商品（价格行的主角）。</summary>
        public ShopOffer? Offer;

        /// <summary>「重置全部调价」那一行。</summary>
        public bool IsPriceReset;

        /// <summary>「保存为预设」那一行。</summary>
        public bool IsPresetSave;

        /// <summary>「选择预设」那一行。</summary>
        public bool IsPresetPick;

        /// <summary>配置区最后那行「数值恢复默认」。</summary>
        public bool IsConfigReset;

        /// <summary>「日志上报」那一节里的一条本机上传记录（整行点一下就把编号复制走）。</summary>
        public bool IsReportRow;

        /// <summary>记录行指向 <see cref="ReportHistory.All"/> 里的第几条（0 = 最新）。</summary>
        public int ReportIndex;

        /// <summary>记录行左边那串「编号 · 时间 · 大小」；只有记录行有。</summary>
        public TextMeshProUGUI? ReportText;

        /// <summary>「日志上报」那一节末尾那行说明（还没传过 / 只列了前几条 / 怎么用，都写在这一行）。</summary>
        public bool IsReportNote;

        /// <summary>价格行左边那颗「−」/「＋」。</summary>
        public RectTransform MinusRect = null!;
        public RectTransform PlusRect = null!;

        /// <summary>右侧的控制区：显示当前值，点它来改。</summary>
        public TextMeshProUGUI Value = null!;
        public Image ValueBackground = null!;
        public RectTransform ValueRect = null!;

        /// <summary>这一行左半边（说明文字）占的矩形，用来判断点的是不是值那一块。</summary>
        public RectTransform ValueHitArea = null!;

        /// <summary>数值行上的输入框；不是数值行为 null。</summary>
        public NumberField? Field;

        public string LastText = string.Empty;
        public bool LastHighlight;
        public bool LastEnabled;
    }

    /// <summary>
    /// 数值行上的输入框。自己搭而不是用 UGUI 的 <see cref="InputField"/> / TMP 的 <c>TMP_InputField</c>：
    /// 这个面板全程手算鼠标位置、没接 EventSystem，那两个组件的选中 / 焦点 / 光标全靠 EventSystem 驱动，
    /// 在这里根本跑不起来。所以这里只管「点一下进入编辑、敲键盘改字符、回车或点别处提交」。
    /// </summary>
    private sealed class NumberField
    {
        /// <summary>点这一块进入编辑（也是输入框的可见范围）。</summary>
        public RectTransform Hit = null!;

        /// <summary>输入框底色：平时暗一档（像个凹槽），编辑时换成亮一档。</summary>
        public Image Background = null!;

        /// <summary>框里的文本：平时是当前值，编辑时是输入缓冲 + 闪烁光标。</summary>
        public TextMeshProUGUI Text = null!;

        public string LastText = string.Empty;
        public bool LastEditing;
    }

    public static HextechConfigPanel? Instance { get; private set; }

    private Canvas _canvas = null!;
    private CanvasGroup _group = null!;
    private RectTransform _viewport = null!;
    private RectTransform _content = null!;
    private TextMeshProUGUI _hint = null!;

    /// <summary>没停在任何一行上时底部那行提示。</summary>
    private string _defaultHint = string.Empty;

    private readonly List<Row> _rows = new();

    private bool _built;
    private int _hover = -1;

    /// <summary>正在输入数值的那一行；null 表示此刻没在输。</summary>
    private Row? _editing;

    /// <summary>输入缓冲：玩家敲进去的原文，回车 / 点别处时才解析成数字写进配置。</summary>
    private string _editBuffer = string.Empty;

    private float _scroll;

    /// <summary>当前停在哪一页（每个配置分组 / 日志上报 / 商店调价各一页，0 基）。</summary>
    private int _page;

    /// <summary>总页数。</summary>
    private int _pageCount;

    /// <summary>翻页时重算的每页滚动范围（与 <see cref="_page"/> 对应）。</summary>
    private float[] _pageMin = Array.Empty<float>();
    private float[] _pageMax = Array.Empty<float>();

    /// <summary>翻页导航条：上一页 / 下一页的点击区域，以及中间的「第 X/Y 页 · 分类名」。</summary>
    private RectTransform _prevRect = null!;
    private RectTransform _nextRect = null!;
    private TextMeshProUGUI _pageTitle = null!;

    /// <summary>往下列表的游标（从 0 往下走，所以是负数）；布局和追加新格都靠它。</summary>
    private float _cursor;

    /// <summary>当前这一行已经放了几格：满了就换行，整行块会把这里清 0。</summary>
    private int _cellsInRow;

    /// <summary>重算分页时用来给每一行编页码的游标。</summary>
    private int _currentPage;

    /// <summary>「商店调价」的表头和重置行建过没有。</summary>
    private bool _priceSectionBuilt;

    /// <summary>调价行已经列到第几类商品（0 = 只列了海克斯抽奖券）。</summary>
    private int _priceTabsBuilt;

    /// <summary>正在等待玩家按键的那一行；null 表示没在重绑。</summary>
    private ConfigEntryBase? _rebinding;

    public bool IsOpen { get; private set; }

    public static HextechConfigPanel Create(Transform parent)
    {
        var go = new GameObject("HextechConfigPanel");
        go.transform.SetParent(parent, false);

        var panel = go.AddComponent<HextechConfigPanel>();
        panel.Build();

        Instance = panel;
        return panel;
    }

    private void Build()
    {
        _canvas = UiFactory.CreateCanvas("HextechConfigCanvas", 254);

        var root = UiFactory.Stretch(UiFactory.Node("Root", _canvas.transform));
        _group = UiFactory.Group(root);

        var backdrop = UiFactory.Solid(root, UiFactory.Backdrop);
        UiFactory.Stretch(backdrop.rectTransform);

        var card = UiFactory.Panel(root, UiFactory.PanelBackground, 26);
        card.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
        card.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        card.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        card.rectTransform.sizeDelta = new Vector2(CardWidth, CardHeight);

        var title = UiFactory.Label(card.transform, "海克斯设置", 40f, TextAlignmentOptions.Center, UiFactory.TextPrimary);
        var titleRect = title.rectTransform;
        titleRect.anchorMin = new Vector2(0f, 1f);
        titleRect.anchorMax = new Vector2(1f, 1f);
        titleRect.pivot = new Vector2(0.5f, 1f);
        titleRect.offsetMin = new Vector2(SidePadding, -74f);
        titleRect.offsetMax = new Vector2(-SidePadding, -26f);

        var subtitle = UiFactory.Label(
            card.transform,
            "数值点右边的框直接输数字（回车生效）· 开关点一下切换、按键点一下再按新键 · ◀▶ 翻页看分类 · 商品调价在列表最下面",
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
        _viewport.gameObject.AddComponent<RectMask2D>();

        _content = UiFactory.Node("Content", _viewport);
        _content.anchorMin = new Vector2(0f, 1f);
        _content.anchorMax = new Vector2(1f, 1f);
        _content.pivot = new Vector2(0.5f, 1f);
        _content.anchoredPosition = Vector2.zero;
        _content.sizeDelta = Vector2.zero;

        // 面板底部这一条兼作「说明区」：鼠标停在某一行上就显示那一项的完整说明，平时显示操作提示。
        // 说明文字不塞进列表里，列表才能一眼扫完。
        _hint = UiFactory.Label(card.transform, string.Empty, 19f, TextAlignmentOptions.Center, UiFactory.TextMuted);
        var hintRect = _hint.rectTransform;
        hintRect.anchorMin = new Vector2(0f, 0f);
        hintRect.anchorMax = new Vector2(1f, 0f);
        hintRect.pivot = new Vector2(0.5f, 0f);
        hintRect.offsetMin = new Vector2(SidePadding, 76f);
        hintRect.offsetMax = new Vector2(-SidePadding, 132f);

        // 翻页导航条：左下「上一页」、右下「下一页」、中间「第 X/Y 页 · 分类名」。
        _prevRect = BuildNavButton(card.transform, SidePadding, "◀ 上一页", false);
        _nextRect = BuildNavButton(card.transform, -SidePadding, "下一页 ▶", true);

        _pageTitle = UiFactory.Label(card.transform, string.Empty, 22f, TextAlignmentOptions.Center, UiFactory.Accent);
        var pageTitleRect = _pageTitle.rectTransform;
        pageTitleRect.anchorMin = new Vector2(0.5f, 0f);
        pageTitleRect.anchorMax = new Vector2(0.5f, 0f);
        pageTitleRect.pivot = new Vector2(0.5f, 0f);
        pageTitleRect.offsetMin = new Vector2(-220f, 12f);
        pageTitleRect.offsetMax = new Vector2(220f, 66f);

        // 说明有长有短（最长的一百多字），挤不下就自动缩一点，别把字裁掉。
        _hint.enableAutoSizing = true;
        _hint.fontSizeMin = 15f;
        _hint.fontSizeMax = 19f;

        _canvas.enabled = false;
    }

    /// <summary>翻页导航条上的一颗按钮（左下 / 右下）。</summary>
    private static RectTransform BuildNavButton(Transform parent, float xEdge, string text, bool right)
    {
        var rect = UiFactory.Node(right ? "NextPage" : "PrevPage", parent);
        rect.anchorMin = new Vector2(0f, 0f);
        rect.anchorMax = new Vector2(0f, 0f);
        rect.pivot = new Vector2(0f, 0f);

        var width = 168f;

        if (right)
        {
            rect.offsetMin = new Vector2(xEdge - width, 12f);
            rect.offsetMax = new Vector2(xEdge, 66f);
        }
        else
        {
            rect.offsetMin = new Vector2(xEdge, 12f);
            rect.offsetMax = new Vector2(xEdge + width, 66f);
        }

        var background = UiFactory.Rounded(rect, UiFactory.PanelHighlight, 10);
        UiFactory.Stretch(background.rectTransform);

        var label = UiFactory.Label(rect, text, 22f, TextAlignmentOptions.Center, UiFactory.TextPrimary);
        UiFactory.Stretch(label.rectTransform);

        return rect;
    }

    public void Show()
    {
        if (IsOpen)
        {
            return;
        }

        EnsureBuilt();

        // 服务器到点就删，本机这本账也照着删一遍：列表里留着「剩 0 小时」的编号只会让人白试一次。
        ReportHistory.PruneExpired();

        _hover = -1;
        _scroll = 0f;
        _rebinding = null;
        ApplyPage();
        _group.alpha = 0f;

        var closeHint = ModConfig.ConfigPanelKey.Value == KeyCode.None
            ? "ESC"
            : $"{ModConfig.ConfigPanelKey.Value} 或 ESC";
        _defaultHint = $"滚轮浏览 · 数值点右边输入框直接敲（回车生效）· 开关、按键点一下 · 按 {closeHint} 关闭";
        _hint.text = _defaultHint;

        _canvas.enabled = true;
        IsOpen = true;

        Refresh();
        HextechWindowHost.Push();
    }

    public void Hide()
    {
        if (!IsOpen)
        {
            return;
        }

        // 面板自己关掉时把价格预设弹层一起收起来，别让它留在屏幕上挡住后面的输入。
        HextechPresetPicker.ForceClose();

        _canvas.enabled = false;
        IsOpen = false;
        _hover = -1;
        _rebinding = null;

        // 关面板时把还没提交的输入存下来 —— 输入框没有「保存」按钮，输完就是要生效。
        CommitEditing();

        HextechWindowHost.Sync();
    }

    private void Update()
    {
        if (!IsOpen)
        {
            return;
        }

        if (!HextechWindowHost.IsOpen)
        {
            Hide();
            return;
        }

        // 价格预设弹层盖在上面时，输入全归它（连刚关掉的那一帧也算）—— 不拦的话同一次点击
        // 会同时落到弹层和下面的行上，弹层里按 ESC 还会顺手把整个面板关掉。
        if (HextechPresetPicker.BlocksInput)
        {
            return;
        }

        // 「日志已上传」的编号浮窗盖在上面时同理：这一帧的鼠标归它（多半正在点「复制 / 确定」），
        // 不拦的话点「确定」的那一下会同时穿到下面的行上，顺手把某个配置项改了。
        if (HextechReportCodeHud.BlocksInput)
        {
            return;
        }

        // 重绑期间 ESC 用来取消，所以取消态要排在关面板前面判断。
        if (_rebinding != null)
        {
            HandleRebind();
            Refresh();
            return;
        }

        // 输入框也是一样：编辑期间 Esc / 回车归它管，别顺手把面板也关了。
        if (_editing != null && HandleEditKeys())
        {
            Refresh();
            return;
        }

        if (Input.GetKeyDown(KeyCode.Escape))
        {
            Hide();
            return;
        }

        _group.alpha = Mathf.MoveTowards(_group.alpha, 1f, Time.deltaTime * 8f);

        HandleScroll();
        HandlePointer();

        // 物资那几百行要等游戏把物品表扫出来才存在（面板也可能是在主菜单打开的），
        // 所以每次刷新都顺手补一次：没扫出来时这里只是两次布尔判断，不花钱。
        EnsurePriceRows();
        Refresh();
    }

    private void HandleRebind()
    {
        if (Input.GetKeyDown(KeyCode.Escape) || Input.GetMouseButtonDown(1))
        {
            _rebinding = null;
            return;
        }

        // 逐个键试。只在重绑期间跑，平时不花这个开销。
        foreach (KeyCode code in Enum.GetValues(typeof(KeyCode)))
        {
            if (code == KeyCode.None || !Input.GetKeyDown(code))
            {
                continue;
            }

            _rebinding!.BoxedValue = code;
            _rebinding = null;
            return;
        }
    }

    /// <summary>
    /// 输入框的键盘操作：数字 / 小数点 / 负号进缓冲，退格删字，回车提交，Esc 放弃这次输入。
    /// 返回真表示这一帧按的是 Esc —— 调用方别再把这次 Esc 拿去关面板。
    /// </summary>
    private bool HandleEditKeys()
    {
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            _editing = null;
            return true;
        }

        if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
        {
            CommitEditing();
            return false;
        }

        if (Input.GetKeyDown(KeyCode.Backspace) && _editBuffer.Length > 0)
        {
            _editBuffer = _editBuffer.Substring(0, _editBuffer.Length - 1);
        }

        // 只收数字 / 小数点 / 负号：输入框是拿来填数值的，字母之类敲了也当没看见。
        var typed = Input.inputString;

        for (var i = 0; i < typed.Length; i++)
        {
            var character = typed[i];

            if ((character >= '0' && character <= '9') || character == '.' || character == '-')
            {
                _editBuffer += character;
            }
        }

        return false;
    }

    private void HandleScroll()
    {
        var min = _pageCount > 0 ? _pageMin[_page] : 0f;
        var max = _pageCount > 0 ? _pageMax[_page] : 0f;

        // 正在输数值时不滚动：滚了以后输入框会从鼠标底下跑掉。
        if (_editing != null || max <= min + 0.01f)
        {
            _scroll = Mathf.Clamp(_scroll, min, max);
            _content.anchoredPosition = new Vector2(0f, _scroll);
            return;
        }

        var wheel = Input.mouseScrollDelta.y;

        if (Mathf.Abs(wheel) > 0.01f)
        {
            _scroll = Mathf.Clamp(_scroll - (wheel * ScrollStep), min, max);
        }

        _content.anchoredPosition = new Vector2(0f, _scroll);
    }

    private void HandlePointer()
    {
        var mouse = (Vector2)Input.mousePosition;

        // 翻页按钮：在视口外面，先拦下来，免得被下面的行命中逻辑吃掉。
        if (Input.GetMouseButtonDown(0))
        {
            if (Inside(_prevRect, mouse)) { PagePrev(); return; }
            if (Inside(_nextRect, mouse)) { PageNext(); return; }
        }

        // 输入框先看：编辑期间这一帧的鼠标归它管（点框里接着输，点别处先提交、这次点击照常生效）。
        if (HandleNumberField(mouse))
        {
            return;
        }

        _hover = -1;

        if (RectTransformUtility.RectangleContainsScreenPoint(_viewport, mouse, null))
        {
            for (var i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];

                // 收起来的行（上传记录不够 6 条时余下的那几行）不能算命中：
                // 它们的 RectTransform 还留在原地，鼠标划过那片空白会被当成点了一条不存在的记录。
                if (!row.IsSection
                    && row.Rect != null
                    && row.Rect.gameObject.activeInHierarchy
                    && RectTransformUtility.RectangleContainsScreenPoint(row.Rect, mouse, null))
                {
                    _hover = i;
                    break;
                }
            }
        }

        var leftClick = Input.GetMouseButtonDown(0);
        var rightClick = Input.GetMouseButtonDown(1);

        if ((!leftClick && !rightClick) || _hover < 0)
        {
            return;
        }

        var target = _rows[_hover];

        // 「日志上报」的记录行：整行都是「复制编号」，点哪儿都算 —— 不挑右边那颗按钮，
        // 因为它的按钮只是个「点这里」的提示，玩家多半会直接点上面那串编号。
        if (target.IsReportRow)
        {
            if (leftClick)
            {
                CopyReportCode(target.ReportIndex);
            }

            return;
        }

        // 价格行自带「− / ＋」两颗按钮，方向写在按钮上，不看左右键；预设那两行也走这里。
        if (target.Offer != null || target.IsPriceReset || target.IsPresetSave || target.IsPresetPick)
        {
            HandlePriceClick(target, mouse);
            return;
        }

        // 「数值恢复默认」那一行：只有点在右边按钮上才算（点左边的说明文字不做事）。
        if (target.IsConfigReset)
        {
            if (leftClick && Inside(target.ValueHitArea, mouse))
            {
                ResetNumberEntries();
            }

            return;
        }

        var entry = target.Entry;

        if (entry == null)
        {
            return;
        }

        // 点值那一块才改；点左半边名字不做事，免得误触。（数值行不走这里，它们在滑块那边。）
        if (!RectTransformUtility.RectangleContainsScreenPoint(target.ValueHitArea, mouse, null))
        {
            return;
        }

        Apply(entry, rightClick ? -1 : 1);
    }

    /// <summary>
    /// 数值输入框的鼠标操作，分两种情况：
    /// <list type="bullet">
    /// <item>正在输：点框里就接着输；点别处 = 输完了，先提交存盘，<b>这次点击照常往下走</b> ——
    /// 玩家点开关能一次点上，不用点两下。</item>
    /// <item>没在输：左键点到哪个框就进哪个框，缓冲预填当前值（想整个换掉就退格删干净）。</item>
    /// </list>
    /// 返回真表示这一帧的鼠标归输入框管（编辑期间），调用方不再当普通点击处理。
    /// </summary>
    private bool HandleNumberField(Vector2 mouse)
    {
        if (_editing != null)
        {
            if (!Input.GetMouseButtonDown(0) && !Input.GetMouseButtonDown(1))
            {
                return true;
            }

            if (Inside(_editing.Field!.Hit, mouse))
            {
                return true;
            }

            CommitEditing();
            return false;
        }

        if (!Input.GetMouseButtonDown(0) || !RectTransformUtility.RectangleContainsScreenPoint(_viewport, mouse, null))
        {
            return false;
        }

        foreach (var row in _rows)
        {
            var field = row.Field;

            if (field == null || !Inside(field.Hit, mouse))
            {
                continue;
            }

            var entry = row.Entry!;

            // 关店时「物价倍率 / 功能溢价」不能改（调价跟着商店一起关）：吃下这次点击、给个提示，不进编辑。
            if (PriceLockedByShopOff(entry))
            {
                if (Input.GetMouseButtonDown(0))
                {
                    HextechHud.Toast("商店已关闭，调价不可用");
                }

                return true;
            }

            _editing = row;
            _editBuffer = FormatNumber(
                Convert.ToDouble(entry.BoxedValue, CultureInfo.InvariantCulture),
                entry.SettingType == typeof(int));
            return true;
        }

        return false;
    }

    /// <summary>
    /// 把输入缓冲解析成数字、写进配置 —— 这就是「自动保存」那一脚：回车、点别处、关面板都会走到这儿，
    /// 所以面板上没有保存按钮。
    /// <para>
    /// 空缓冲当没改过；解析不了就弹提示、值原样不动。<b>数值不设上下限</b>：填多少存多少，
    /// 只按显示精度留三位小数（跟框里显示的对齐，免得存进去的和看见的不一样）。
    /// </para>
    /// </summary>
    private void CommitEditing()
    {
        var row = _editing;
        _editing = null;

        var entry = row?.Entry;

        if (entry == null)
        {
            return;
        }

        var text = _editBuffer.Trim();

        if (text.Length == 0)
        {
            return;
        }

        if (!double.TryParse(
                text,
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var value))
        {
            HextechHud.Toast($"「{text}」不是数字，这项没改");
            return;
        }

        StoreNumber(entry, Math.Round(value, 3));
    }

    /// <summary>Shift 有没有被按住（左右都算）。</summary>
    private static bool ShiftHeld()
    {
        return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
    }

    /// <summary>
    /// 按配置项的类型决定点一下干什么，<paramref name="direction"/> 为负表示这次是「往回走一格」。
    /// <para>
    /// 数值项（int / float / double）不从这里走 —— 它们在 <see cref="HandleNumberField"/> 里直接输数字。
    /// 这里管的是开关、按键、以及带可选值列表的单选：左键 = 加 / 下一个，右键 = 减 / 上一个。
    /// </para>
    /// </summary>
    // ── 分页 ────────────────────────────────────────────────────
    //
    // 配置项按分组（Section）分页：每个分组、日志上报、商店调价各占一页，底部用 ◀▶ 翻。
    // 不再把所有配置堆在一页里上下滚。分页信息在行建好之后一次性算出来，翻页只切可见性 + 滚动范围。

    private void RecomputePages()
    {
        if (_rows.Count == 0)
        {
            return;
        }

        _currentPage = -1;

        foreach (var row in _rows)
        {
            // 每个大分组（IsSection 且不是物资分类那种小标题）开一页；小标题跟在所属分组里。
            if (row.IsSection && !row.IsSubHeader)
            {
                _currentPage++;
            }

            row.Page = _currentPage;
            row.TopY = -row.Rect.anchoredPosition.y;
            row.Height = row.Rect.sizeDelta.y;
        }

        _pageCount = _currentPage + 1;
        _pageMin = new float[_pageCount];
        _pageMax = new float[_pageCount];

        var cur = -1;
        var top = float.MaxValue;
        var bottom = float.MinValue;

        foreach (var row in _rows)
        {
            if (row.Page != cur)
            {
                if (cur >= 0)
                {
                    CommitPage(cur, top, bottom);
                }

                cur = row.Page;
                top = float.MaxValue;
                bottom = float.MinValue;
            }

            top = Mathf.Min(top, row.TopY);
            bottom = Mathf.Max(bottom, row.TopY + row.Height);
        }

        if (cur >= 0)
        {
            CommitPage(cur, top, bottom);
        }

        if (_page >= _pageCount)
        {
            _page = _pageCount - 1;
        }

        if (_page < 0)
        {
            _page = 0;
        }

        if (_pageTitle != null)
        {
            _pageTitle.text = $"{PageName(_page)} · 第 {_page + 1}/{_pageCount} 页";
        }
    }

    private void CommitPage(int page, float top, float bottom)
    {
        var height = bottom - top;
        _pageMax[page] = Mathf.Max(0f, top);
        _pageMin[page] = Mathf.Max(0f, top + height - ViewportHeight);
    }

    /// <summary>只切可见性（不碰滚动位置）：翻页、或商店调价页追加新行时用。</summary>
    private void ApplyVisibility()
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];

            if (row.Rect != null)
            {
                row.Rect.gameObject.SetActive(row.Page == _page);
            }
        }
    }

    private void ApplyPage()
    {
        if (_pageCount == 0)
        {
            return;
        }

        ApplyVisibility();
        _scroll = _pageMax[_page];
        _content.anchoredPosition = new Vector2(0f, _scroll);

        if (_pageTitle != null)
        {
            _pageTitle.text = $"{PageName(_page)} · 第 {_page + 1}/{_pageCount} 页";
        }

        Refresh();
    }

    private string PageName(int page)
    {
        foreach (var row in _rows)
        {
            if (row.Page == page && row.IsSection)
            {
                return row.Value.text;
            }
        }

        return $"第 {page + 1} 页";
    }

    private void PagePrev()
    {
        if (_page <= 0)
        {
            return;
        }

        _page--;
        CommitEditing();
        _rebinding = null;
        ApplyPage();
    }

    private void PageNext()
    {
        if (_page >= _pageCount - 1)
        {
            return;
        }

        _page++;
        CommitEditing();
        _rebinding = null;
        ApplyPage();
    }

    private void Apply(ConfigEntryBase entry, int direction)
    {
        // 共享代币只能在机场开关：进局之后想改就改不了了（避免半路把别人的池子改没）。
        if (entry == ModConfig.SharedTokens && !HextechScene.InAirport)
        {
            HextechHud.Toast("共享代币只能在机场设置");
            return;
        }

        var type = entry.SettingType;

        if (type == typeof(bool))
        {
            entry.BoxedValue = !(bool)entry.BoxedValue;
            return;
        }

        // 按键行只认左键：重绑期间右键是「取消」，两个动作别混在一起。
        if (type == typeof(KeyCode))
        {
            if (direction > 0)
            {
                _rebinding = entry;
            }

            return;
        }

        // 配了「只能从这几个里挑」的，左键往下一个候选跳、右键往回跳。
        if (TryCycleList(entry, direction))
        {
            return;
        }

        // 整数候选列表（配置里暂时没有这种项，留个兜底）：左键下一个、右键上一个。
        if (type == typeof(int)
            && entry.Description.AcceptableValues is AcceptableValueList<int> values
            && values.AcceptableValues.Length > 0)
        {
            var current = Convert.ToDouble(entry.BoxedValue, CultureInfo.InvariantCulture);
            StepThroughList(entry, values.AcceptableValues, current, direction);
        }
    }

    private static bool TryCycleList(ConfigEntryBase entry, int direction)
    {
        if (entry.Description.AcceptableValues is not AcceptableValueList<string> list || list.AcceptableValues.Length == 0)
        {
            return false;
        }

        var current = entry.BoxedValue as string;
        var index = Array.IndexOf(list.AcceptableValues, current);

        if (index < 0)
        {
            index = 0;
        }

        // 往回走一格就是「最后一个」（负数取模不好看，直接减长度再取模）。
        var offset = direction < 0 ? list.AcceptableValues.Length - 1 : 1;

        entry.BoxedValue = list.AcceptableValues[(index + offset) % list.AcceptableValues.Length];
        return true;
    }

    /// <summary>
    /// 写数值。<b>不做玩法上的范围限制</b>（玩家填多少就存多少），只兜一下类型边界 ——
    /// 填得超出 <c>int</c> / <c>float</c> 能表示的范围时，转型出来会变成乱七八糟的值。
    /// </summary>
    private static void StoreNumber(ConfigEntryBase entry, double value)
    {
        if (entry.SettingType == typeof(int))
        {
            entry.BoxedValue = (int)Math.Clamp(Math.Round(value), int.MinValue, int.MaxValue);
        }
        else if (entry.SettingType == typeof(double))
        {
            entry.BoxedValue = value;
        }
        else
        {
            entry.BoxedValue = (float)Math.Clamp(value, -float.MaxValue, float.MaxValue);
        }
    }

    private static void StepThroughList(ConfigEntryBase entry, int[] values, double current, int direction)
    {
        var index = Array.IndexOf(values, (int)Math.Round(current));

        if (index < 0)
        {
            index = 0;
        }

        var offset = direction < 0 ? values.Length - 1 : 1;
        entry.BoxedValue = values[(index + offset) % values.Length];
    }

    /// <summary>
    /// 把面板里的数值项（滑块项）全部恢复默认值 —— 倍率 / 概率 / 速率 / 代币上限这些。
    /// <para>
    /// 只写「真的和默认值不一样」的那些：每写一项 BepInEx 就落一次盘，全都重写一遍是白写文件。
    /// 开关与按键不在内 —— 玩家想要的通常是「把数值调回来」，不是把快捷键一起清了。
    /// </para>
    /// </summary>
    private void ResetNumberEntries()
    {
        var changed = 0;

        foreach (var row in _rows)
        {
            var entry = row.Entry;

            if (entry == null || row.Field == null || Equals(entry.BoxedValue, entry.DefaultValue))
            {
                continue;
            }

            entry.BoxedValue = entry.DefaultValue;
            changed++;
        }

        HextechHud.Toast(changed == 0 ? "数值本来就都是默认值" : $"已把 {changed} 项数值恢复默认");
    }

    private void Refresh()
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];

            // 记录行要带上自己在列表里的位置：高亮跟着 _hover 走（刷新时才知道鼠标停在第几行）。
            if (row.IsReportRow)
            {
                RefreshReportRow(row, i);
                continue;
            }

            if (row.IsReportNote)
            {
                RefreshReportNote(row);
                continue;
            }

            if (row.Offer != null)
            {
                RefreshPrice(row);
                continue;
            }

            if (row.IsPriceSection)
            {
                RefreshPriceHeader(row);
                continue;
            }

            if (row.IsPriceReset)
            {
                RefreshResetRow(row);
                continue;
            }

            if (row.Entry == null)
            {
                continue;
            }

            if (row.Field != null)
            {
                RefreshNumberField(row);
                continue;
            }

            var entry = row.Entry;
            var text = ValueText(entry, row);

            // 共享代币只能机场开关：不在机场时标注「仅机场」（点击会在 Apply 里被挡下）。
            if (entry == ModConfig.SharedTokens && !HextechScene.InAirport)
            {
                text += "（仅机场）";
            }

            if (row.LastText != text)
            {
                row.LastText = text;
                row.Value.text = text;
            }

            var highlight = _rebinding == entry;

            if (row.LastHighlight != highlight)
            {
                row.LastHighlight = highlight;
                row.ValueBackground.color = highlight ? UiFactory.Accent : UiFactory.PanelBackgroundLight;
                row.Value.color = highlight ? UiFactory.PanelBackground : UiFactory.TextPrimary;
            }

            // 共享代币不在机场时整体置灰，提示改不了。
            if (entry == ModConfig.SharedTokens && !HextechScene.InAirport)
            {
                row.Value.color = UiFactory.TextMuted;
            }
        }

        RefreshHint();
    }

    /// <summary>
    /// 数值行：把当前值写进输入框（编辑中就显示输入缓冲 + 闪烁光标）。
    /// 这个方法每帧都会被走到，所以文本与状态没变就一个对象都不动。
    /// </summary>
    private void RefreshNumberField(Row row)
    {
        var field = row.Field!;
        var entry = row.Entry!;
        var editing = _editing == row;
        var locked = PriceLockedByShopOff(entry);

        var text = editing
            ? _editBuffer + (Time.unscaledTime % 1f < 0.5f ? "|" : " ")
            : FormatNumber(
                Convert.ToDouble(entry.BoxedValue, CultureInfo.InvariantCulture),
                entry.SettingType == typeof(int));

        if (field.LastText != text)
        {
            field.LastText = text;
            field.Text.text = text;
        }

        if (field.LastEditing != editing)
        {
            field.LastEditing = editing;

            // 编辑时把框点亮：玩家一眼能看出这会儿敲的数字会落在哪一行。
            field.Background.color = editing ? UiFactory.PanelHighlight : UiFactory.PanelBackground;
            field.Text.color = editing ? UiFactory.TextPrimary : UiFactory.Warning;
        }

        // 关店时把「物价倍率 / 功能溢价」这两个调价项钉成置灰，提示改不了（2026-09-16）。
        // 钉在编辑态判断之外：开着店切到关店这种跨帧的状态切换，编辑态没变也需要每帧重涂一次。
        if (locked)
        {
            field.Background.color = UiFactory.PanelBackground;
            field.Text.color = UiFactory.TextMuted;
        }
    }

    /// <summary>
    /// 「物价倍率」「功能溢价」是调价的一部分：商店关掉时一起锁死，点也点不进输入框（2026-09-16）。
    /// </summary>
    private static bool PriceLockedByShopOff(ConfigEntryBase entry)
    {
        return !ModConfig.ShopEnabled.Value
            && (entry == ModConfig.ShopPriceMultiplier || entry == ModConfig.ShopFunctionPremium);
    }

    private static string FormatNumber(double value, bool integer)
    {
        return value.ToString(integer ? "0" : "0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 面板底部那一行：默认是操作提示，鼠标停在某一行上时换成那一项的完整说明。
    /// 列表里只写配置项的名字，说明挤在这一行 —— 这样列表能一屏看到底。
    /// </summary>
    private void RefreshHint()
    {
        var text = _defaultHint;

        if (_rebinding != null)
        {
            text = "现在按一个想绑定的键 · Esc 或右键取消";
        }
        else if (_editing != null)
        {
            // 编辑中：数值不设上下限，这里只说操作方式。
            text = "输入中 · 填多少存多少（没有上下限）· 退格删字、回车保存、Esc 放弃（点别处或关面板也会保存）";
        }
        else if (_hover >= 0 && _hover < _rows.Count && _rows[_hover].Entry != null)
        {
            var row = _rows[_hover];
            var description = row.Entry!.Description?.Description ?? _defaultHint;

            // 数值行多说一句怎么改：面板上没按钮，值是在框里敲出来的。
            text = row.Field != null
                ? $"{description} · 点右边输入框直接敲数字，回车保存"
                : description;
        }
        else if (_hover >= 0 && _hover < _rows.Count && _rows[_hover].Offer != null)
        {
            text = "点 − / ＋ 改这件商品的基础价（按住 Shift 一次 5 枚）· 点中间的数字恢复默认价";
        }
        else if (_hover >= 0 && _hover < _rows.Count && _rows[_hover].IsConfigReset)
        {
            text = "把上面那些数值项（倍率 / 概率 / 速率 / 上限）恢复成默认值 · 开关与按键不受影响";
        }
        else if (_hover >= 0 && _hover < _rows.Count && _rows[_hover].IsReportRow)
        {
            text = $"点这一行把编号复制回剪贴板，发给我就能取这份日志 · 服务器只留 {ReportHistory.KeepHours} 小时";
        }

        if (_hint.text != text)
        {
            _hint.text = text;
        }
    }

    private static string ValueText(ConfigEntryBase entry, Row row)
    {
        if (row.LastHighlight)
        {
            return "按键中…";
        }

        var value = entry.BoxedValue;

        return value switch
        {
            bool flag => flag ? "开" : "关",
            KeyCode key => key == KeyCode.None ? "未绑定" : key.ToString(),
            // 三位小数：概率那类默认值是 0.025，只显示两位的话玩家看到的和点出来的对不上。
            float number => number.ToString("0.###", CultureInfo.InvariantCulture),
            double number => number.ToString("0.###", CultureInfo.InvariantCulture),
            null => "—",
            _ => value.ToString() ?? "—",
        };
    }

    /// <summary>第一次打开时把行建出来。配置项是固定的，建一次就够。</summary>
    private void EnsureBuilt()
    {
        if (_built)
        {
            return;
        }

        _built = true;

        var fields = typeof(ModConfig).GetFields(BindingFlags.Public | BindingFlags.Static);
        var entries = new List<ConfigEntryBase>();
        var sections = new List<string>();

        foreach (var field in fields)
        {
            if (!typeof(ConfigEntryBase).IsAssignableFrom(field.FieldType))
            {
                continue;
            }

            if (field.GetValue(null) is not ConfigEntryBase entry)
            {
                continue;
            }

            var section = entry.Definition.Section;

            if (!sections.Contains(section))
            {
                sections.Add(section);
            }

            entries.Add(entry);
        }

        // 按 Section 分组摆：反射给的是声明顺序，所以顺序和源码里一致。
        var ordered = new List<ConfigEntryBase>();

        foreach (var section in sections)
        {
            ordered.AddRange(entries.FindAll(entry => entry.Definition.Section == section));
        }

        _cursor = 0f;
        _cellsInRow = 0;
        var lastSection = string.Empty;

        foreach (var entry in ordered)
        {
            var section = entry.Definition.Section;

            if (section != lastSection)
            {
                lastSection = section;
                _rows.Add(SectionRow(section));
            }

            // 数值项要写范围提示 + 输入框，独占一整行（一排只放一个）；其余的还是两列，省一半列表长度。
            if (IsNumberEntry(entry))
            {
                _rows.Add(BuildNumberRow(entry));
                continue;
            }

            var rect = NextCell("ConfigRow", RowHeight);

            var background = UiFactory.Rounded(rect, UiFactory.PanelBackgroundLight, 10);
            UiFactory.Stretch(background.rectTransform);

            // 行里只写配置项的名字，整句说明挪到面板底部的说明行（鼠标停在行上才显示）：
            // 每行都挂一整句解释，这份列表就变成一堵字墙了。
            var label = UiFactory.Label(rect, entry.Definition.Key, 20f, TextAlignmentOptions.Left, UiFactory.TextPrimary);
            var labelRect = label.rectTransform;
            labelRect.anchorMin = new Vector2(0f, 0f);
            labelRect.anchorMax = new Vector2(1f, 1f);
            labelRect.offsetMin = new Vector2(14f, 0f);
            labelRect.offsetMax = new Vector2(-(ValueWidth + 10f), 0f);

            var valueHit = UiFactory.Node("ValueHit", rect);
            valueHit.anchorMin = new Vector2(1f, 0f);
            valueHit.anchorMax = new Vector2(1f, 1f);
            valueHit.pivot = new Vector2(1f, 0.5f);
            valueHit.offsetMin = new Vector2(-ValueWidth, 8f);
            valueHit.offsetMax = new Vector2(-12f, -8f);

            var valueBackground = UiFactory.Rounded(valueHit, UiFactory.PanelBackgroundLight, 8);
            UiFactory.Stretch(valueBackground.rectTransform);

            var value = UiFactory.Label(valueHit, string.Empty, 20f, TextAlignmentOptions.Center, UiFactory.TextPrimary);
            UiFactory.Stretch(value.rectTransform);

            _rows.Add(new Row
            {
                Entry = entry,
                Rect = rect,
                Background = background,
                Value = value,
                ValueBackground = valueBackground,
                ValueHitArea = valueHit,
                ValueRect = valueHit,
            });
        }

        // 配置区收尾：一行「数值恢复默认」，拖花了不用一个个拖回去。
        AppendConfigResetRow();

        // 上传记录排在配置区之后、商店调价之前：它是「出问题时」才来翻的地方，
        // 混在配置项里会让人以为那也是几个开关。
        AppendReportRows();
        FlushLayout();

        // 把所有行按分组编成几页，并切到第一页。
        RecomputePages();
        ApplyPage();
    }

    /// <summary>数值项（int / float / double）用输入框；开关、按键、单选列表仍然是点一下切换。</summary>
    private static bool IsNumberEntry(ConfigEntryBase entry)
    {
        var type = entry.SettingType;

        if (type != typeof(int) && type != typeof(float) && type != typeof(double))
        {
            return false;
        }

        // 配了「只能从这几个数里挑」的用点按逐个跳，输入框填不进不在表里的值。
        return entry.Description.AcceptableValues is not AcceptableValueList<int>;
    }

    /// <summary>
    /// 数值行：左边名字、右边输入框（点一下就能敲数字）。整行独占 —— 一排只放一个，框才够长。
    /// </summary>
    private Row BuildNumberRow(ConfigEntryBase entry)
    {
        var rect = FullRow("ConfigRow", RowHeight);

        var background = UiFactory.Rounded(rect, UiFactory.PanelBackgroundLight, 10);
        UiFactory.Stretch(background.rectTransform);

        var label = UiFactory.Label(rect, entry.Definition.Key, 20f, TextAlignmentOptions.Left, UiFactory.TextPrimary);
        var labelRect = label.rectTransform;
        labelRect.anchorMin = new Vector2(0f, 0f);
        labelRect.anchorMax = new Vector2(0f, 1f);
        labelRect.pivot = new Vector2(0f, 0.5f);
        labelRect.offsetMin = new Vector2(14f, 0f);
        labelRect.offsetMax = new Vector2(14f + NumberLabelWidth, 0f);

        var hit = UiFactory.Node("NumberField", rect);
        hit.anchorMin = new Vector2(1f, 0f);
        hit.anchorMax = new Vector2(1f, 1f);
        hit.pivot = new Vector2(1f, 0.5f);
        hit.offsetMin = new Vector2(-(14f + FieldWidth), 8f);
        hit.offsetMax = new Vector2(-14f, -8f);

        // 平时暗一档（像个凹槽），编辑时会被 RefreshNumberField 提亮。
        var box = UiFactory.Rounded(hit, UiFactory.PanelBackground, 8);
        UiFactory.Stretch(box.rectTransform);

        var text = UiFactory.Label(hit, string.Empty, 22f, TextAlignmentOptions.Center, UiFactory.Warning);
        UiFactory.Stretch(text.rectTransform, 12f, 0f, 12f, 0f);

        return new Row
        {
            Entry = entry,
            Rect = rect,
            Background = background,
            Value = text,
            ValueBackground = box,
            ValueHitArea = hit,
            ValueRect = hit,
            Field = new NumberField
            {
                Hit = hit,
                Background = box,
                Text = text,
            },
        };
    }

    // ── 网格 ────────────────────────────────────────────────────

    /// <summary>
    /// 取网格里的下一格：上一行摆满了就自动换行。格子都一样高，所以换行只是把游标再往下推一行。
    /// </summary>
    private RectTransform NextCell(string name, float height)
    {
        if (_cellsInRow == 0)
        {
            _cursor -= height + RowGap;
        }

        var column = _cellsInRow;
        _cellsInRow = (_cellsInRow + 1) % Columns;

        var rect = UiFactory.Node(name, _content);
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.sizeDelta = new Vector2(CellWidth, height);
        rect.anchoredPosition = new Vector2(column * (CellWidth + ColumnGap), _cursor + height);
        return rect;
    }

    /// <summary>占满一整行的块（分组标题、调价表头、重置行）—— 它会先把当前这一行断掉。</summary>
    private RectTransform FullRow(string name, float height)
    {
        _cellsInRow = 0;
        _cursor -= height + RowGap;

        var rect = UiFactory.Node(name, _content);
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.offsetMin = new Vector2(0f, _cursor);
        rect.offsetMax = new Vector2(0f, _cursor + height);
        return rect;
    }

    /// <summary>配置分组的标题行。</summary>
    private Row SectionRow(string text)
    {
        var rect = FullRow("Section", SectionHeight);

        var header = UiFactory.Label(rect, text, 24f, TextAlignmentOptions.Left, UiFactory.Accent);
        UiFactory.Stretch(header.rectTransform, 4f, 0f, 4f, 0f);

        return new Row
        {
            IsSection = true,
            Rect = rect,
            Entry = null,
            Value = header,
            ValueBackground = null!,
            ValueHitArea = rect,
            ValueRect = rect,
        };
    }

    /// <summary>
    /// 配置区最后那行「数值恢复默认」：倍率 / 概率这些拖花了以后一键全调回去。
    /// 按钮样式和调价段的「重置全部」保持一致（右边一颗按钮，点它才算数）。
    /// </summary>
    private void AppendConfigResetRow()
    {
        var rect = FullRow("ConfigResetRow", RowHeight);

        var background = UiFactory.Rounded(rect, UiFactory.PanelBackgroundLight, 10);
        UiFactory.Stretch(background.rectTransform);

        var label = UiFactory.Label(
            rect,
            "倍率 / 概率 / 速率这些数值一键调回默认（开关与按键不动）",
            19f,
            TextAlignmentOptions.Left,
            UiFactory.TextMuted);
        UiFactory.Stretch(label.rectTransform, 16f, 0f, PriceValueWidth + 16f, 0f);

        var valueHit = UiFactory.Node("ConfigResetHit", rect);
        valueHit.anchorMin = new Vector2(1f, 0f);
        valueHit.anchorMax = new Vector2(1f, 1f);
        valueHit.pivot = new Vector2(1f, 0.5f);
        valueHit.offsetMin = new Vector2(-PriceValueWidth, 8f);
        valueHit.offsetMax = new Vector2(-12f, -8f);

        var valueBackground = UiFactory.Rounded(valueHit, UiFactory.Accent, 8);
        UiFactory.Stretch(valueBackground.rectTransform);

        var value = UiFactory.Label(valueHit, "恢复默认", 20f, TextAlignmentOptions.Center, UiFactory.PanelBackground);
        UiFactory.Stretch(value.rectTransform);

        _rows.Add(new Row
        {
            IsConfigReset = true,
            Rect = rect,
            Background = background,
            Value = value,
            ValueBackground = valueBackground,
            ValueHitArea = valueHit,
            ValueRect = valueHit,
        });
    }

    // ── 日志上报记录 ────────────────────────────────────────────
    //
    // 上传日志换来的那个编号本来只在「日志已上传」浮窗里显示一次，关掉之后就只能靠剪贴板 ——
    // 而剪贴板会被别的东西顶掉。玩家第二天跑来说「编号我找不着了」时，
    // 作者能让他自己在这里把编号翻出来，而不用重新传一份。

    /// <summary>
    /// 「日志上报」那一节：本机传过的日志按时间倒序列出来，点一行就把编号复制回剪贴板。
    /// <para>
    /// 留的时长和服务端一致（<see cref="ReportHistory.KeepHours"/> 小时）：服务端那份到点就删了，
    /// 本机这个编号已经没处可取了，留在列表里只会让人白试一次。
    /// </para>
    /// </summary>
    private void AppendReportRows()
    {
        AppendHeader($"日志上报 · 本机上传记录（服务器只留 {ReportHistory.KeepHours} 小时）", false);

        for (var i = 0; i < ReportRows; i++)
        {
            AppendReportRow(i);
        }

        AppendReportNote();
    }

    /// <summary>一条上传记录。整行都可点（复制编号），右边那颗按钮只是告诉玩家「点哪儿」。</summary>
    private void AppendReportRow(int index)
    {
        var rect = FullRow("ReportRow", RowHeight);

        var background = UiFactory.Rounded(rect, UiFactory.PanelBackgroundLight, 10);
        UiFactory.Stretch(background.rectTransform);

        var text = UiFactory.Label(rect, string.Empty, 19f, TextAlignmentOptions.Left, UiFactory.TextPrimary);
        UiFactory.Stretch(text.rectTransform, 16f, 0f, PriceValueWidth + 16f, 0f);

        // 和「重置全部调价」「保存为预设」共用右侧按钮的尺寸与位置，扫下来是一条线。
        var hit = UiFactory.Node("ReportCopyHit", rect);
        hit.anchorMin = new Vector2(1f, 0f);
        hit.anchorMax = new Vector2(1f, 1f);
        hit.pivot = new Vector2(1f, 0.5f);
        hit.offsetMin = new Vector2(-PriceValueWidth, 8f);
        hit.offsetMax = new Vector2(-12f, -8f);

        var hitBackground = UiFactory.Rounded(hit, UiFactory.PanelHighlight, 8);
        UiFactory.Stretch(hitBackground.rectTransform);

        var hitLabel = UiFactory.Label(hit, "复制编号", 20f, TextAlignmentOptions.Center, UiFactory.TextPrimary);
        UiFactory.Stretch(hitLabel.rectTransform);

        _rows.Add(new Row
        {
            IsReportRow = true,
            ReportIndex = index,
            Rect = rect,
            Background = background,
            ReportText = text,
            Value = hitLabel,
            ValueBackground = hitBackground,
            ValueHitArea = hit,
            ValueRect = hit,
        });
    }

    /// <summary>记录区末尾那行说明：「还没传过 / 还有几条没列出来 / 怎么用」共用这一个位置。</summary>
    private void AppendReportNote()
    {
        var rect = FullRow("ReportNote", RowHeight);
        var label = UiFactory.Label(rect, string.Empty, 18f, TextAlignmentOptions.Left, UiFactory.TextMuted);
        UiFactory.Stretch(label.rectTransform, 16f, 0f, 16f, 0f);

        _rows.Add(new Row
        {
            IsReportNote = true,
            Rect = rect,
            Value = label,
            ValueBackground = null!,
            ValueHitArea = rect,
            ValueRect = rect,
        });
    }

    /// <summary>
    /// 刷新一条上传记录。行不够的那些直接藏起来（列表短的时候不留空档）；
    /// 这个方法每帧都会被走到，所以文本和颜色都是「真的变了才动」。
    /// </summary>
    private void RefreshReportRow(Row row, int index)
    {
        var records = ReportHistory.All;
        var record = row.ReportIndex >= 0 && row.ReportIndex < records.Count ? records[row.ReportIndex] : null;
        var hasRecord = record != null;

        if (row.Rect.gameObject.activeSelf != hasRecord)
        {
            row.Rect.gameObject.SetActive(hasRecord);
        }

        if (record == null)
        {
            return;
        }

        var left = ReportHistory.Remaining(record);
        var text = $"{record.Code} · {record.Time:MM-dd HH:mm} · {record.Kilobytes} KB · {record.Scene}"
            + $" · 剩 {(int)Math.Ceiling(left.TotalHours)} 小时";

        if (row.LastText != text)
        {
            row.LastText = text;
            row.ReportText!.text = text;
        }

        var highlight = _hover == index;

        if (row.LastHighlight == highlight)
        {
            return;
        }

        row.LastHighlight = highlight;
        row.Background.color = highlight ? UiFactory.PanelHighlight : UiFactory.PanelBackgroundLight;
        row.ValueBackground.color = highlight ? UiFactory.Accent : UiFactory.PanelHighlight;
        row.Value.color = highlight ? UiFactory.PanelBackground : UiFactory.TextPrimary;
    }

    private void RefreshReportNote(Row row)
    {
        var records = ReportHistory.All;
        string text;

        if (records.Count == 0)
        {
            var key = ModConfig.ReportKey.Value;
            text = key == KeyCode.None
                ? "本机还没传过日志 · 在「界面」里给「上传日志」绑一个键，进游戏按一下就能把日志发过来"
                : $"本机还没传过日志 · 进游戏按 {key} 就能把日志发过来（会给一个取件编号）";
        }
        else if (records.Count > ReportRows)
        {
            text = $"点某一行把它的编号复制回剪贴板 · 另有 {records.Count - ReportRows} 条更早的没列出来";
        }
        else
        {
            text = $"点某一行把它的编号复制回剪贴板 · 服务器只留 {ReportHistory.KeepHours} 小时，过期就取不到了";
        }

        if (row.LastText != text)
        {
            row.LastText = text;
            row.Value.text = text;
        }
    }

    /// <summary>把某一条记录的编号再复制一次（这份列表就是为「编号找不着了」准备的）。</summary>
    private static void CopyReportCode(int index)
    {
        var records = ReportHistory.All;

        if (index < 0 || index >= records.Count)
        {
            HextechHud.Toast("这条记录已经不在了，重新打开面板看看");
            return;
        }

        var record = records[index];
        ReportHistory.CopyToClipboard(record.Code);
        HextechHud.Toast($"编号 {record.Code} 已复制 · 尽快发给我（服务器只留 {ReportHistory.KeepHours} 小时）");
    }

    // ── 商店调价 ────────────────────────────────────────────────
    //
    // 单品调价放在这个面板里（而不是只在商店里），是想让「改价」和「改配置」在同一个地方：
    // 打开就能看见现在什么价、改完立刻存盘。商店里按 T 的那个快捷入口还在，两边改的是同一张表。

    /// <summary>
    /// 把「商店调价」那一段（表头 + 重置行 + 每件商品一行）接在配置列表最后面。
    /// <para>
    /// 物资那几百件要等游戏把物品表扫出来才存在，而面板可能是在主菜单打开的，
    /// 所以分成两次：抽奖券（静态就有）先建，物资等 <see cref="HextechShopCatalog.ItemsReady"/> 之后再补。
    /// </para>
    /// </summary>
    private void EnsurePriceRows()
    {
        var appended = false;

        if (!_priceSectionBuilt)
        {
            _priceSectionBuilt = true;
            AppendHeader(ShopPricing.CanEdit ? "商店调价" : "商店调价 · 联机时只有房主能改", true);
            AppendResetRow();
            AppendPresetRows();
            AppendOffers(0);
            _priceTabsBuilt = 1;
            appended = true;
        }

        if (HextechShopCatalog.ItemsReady)
        {
            for (var tab = _priceTabsBuilt; tab < HextechShopCatalog.TabCount; tab++)
            {
                AppendSubHeader(HextechShopCatalog.TabTitle(tab));
                AppendOffers(tab);
                _priceTabsBuilt = tab + 1;
                appended = true;
            }
        }

        if (appended)
        {
            FlushLayout();

            // 价格页是动态长出来的（物品表扫出来才有一格格），每长出一段就重算分页 + 刷新可见性，
            // 免得翻到这一页时新长出来的行还顶着「可见」的默认状态、或卡在别的页看不见。
            RecomputePages();
            ApplyVisibility();
        }
    }

    private void AppendOffers(int tab)
    {
        var offers = tab <= 0 ? HextechShopCatalog.TicketOffers : HextechShopCatalog.Offers(tab);

        // 上面的小标题已经写着这是哪一类了，每一行再重复一遍类别纯属占位置。
        foreach (var offer in offers)
        {
            AppendPriceRow(offer);
        }
    }

    private void AppendHeader(string text, bool priceSection)
    {
        var rect = FullRow("PriceHeader", SectionHeight);

        var header = UiFactory.Label(rect, text, 24f, TextAlignmentOptions.Left, UiFactory.Accent);
        UiFactory.Stretch(header.rectTransform, 4f, 0f, 4f, 0f);

        _rows.Add(new Row
        {
            IsSection = true,
            IsPriceSection = priceSection,
            Rect = rect,
            Value = header,
            ValueBackground = null!,
            ValueHitArea = rect,
            ValueRect = rect,
        });
    }

    /// <summary>物资分类之间的小标题，比配置分组标题矮一点、暗一点。</summary>
    private void AppendSubHeader(string text)
    {
        var rect = FullRow("SubHeader", SectionHeight * 0.7f);

        var header = UiFactory.Label(rect, text, 21f, TextAlignmentOptions.Left, UiFactory.TextMuted);
        UiFactory.Stretch(header.rectTransform, 4f, 0f, 4f, 0f);

        _rows.Add(new Row
        {
            IsSection = true,
            IsSubHeader = true,
            Rect = rect,
            Value = header,
            ValueBackground = null!,
            ValueHitArea = rect,
            ValueRect = rect,
        });
    }

    /// <summary>「重置全部调价」那一行：右边那颗按钮点一下就全恢复默认。</summary>
    private void AppendResetRow()
    {
        var rect = FullRow("PriceResetRow", RowHeight);

        var background = UiFactory.Rounded(rect, UiFactory.PanelBackgroundLight, 10);
        UiFactory.Stretch(background.rectTransform);

        var label = UiFactory.Label(
            rect,
            "改过的价一键恢复默认（怎么改见下面的说明）",
            19f,
            TextAlignmentOptions.Left,
            UiFactory.TextMuted);
        UiFactory.Stretch(label.rectTransform, 16f, 0f, PriceValueWidth + 16f, 0f);

        var valueHit = UiFactory.Node("PriceResetHit", rect);
        valueHit.anchorMin = new Vector2(1f, 0f);
        valueHit.anchorMax = new Vector2(1f, 1f);
        valueHit.pivot = new Vector2(1f, 0.5f);
        valueHit.offsetMin = new Vector2(-PriceValueWidth, 8f);
        valueHit.offsetMax = new Vector2(-12f, -8f);

        var valueBackground = UiFactory.Rounded(valueHit, UiFactory.Accent, 8);
        UiFactory.Stretch(valueBackground.rectTransform);

        var value = UiFactory.Label(valueHit, "重置全部", 20f, TextAlignmentOptions.Center, UiFactory.PanelBackground);
        UiFactory.Stretch(value.rectTransform);

        _rows.Add(new Row
        {
            IsPriceReset = true,
            Rect = rect,
            Background = background,
            Value = value,
            ValueBackground = valueBackground,
            ValueHitArea = valueHit,
            ValueRect = valueHit,
        });
    }

    /// <summary>
    /// 价格预设那两行：「保存为预设」（把现在这一版价格存起来）和「选择预设 ▾」（换一份存好的）。
    /// <para>
    /// 和「重置全部调价」不一样，这两行<b>不</b>管 <see cref="ShopPricing.CanEdit"/>：预设是本机的
    /// 文本文件，联机时照样能存、能换（换完要等自己当房主才生效，所以应用时会补一句提示），不必置灰。
    /// </para>
    /// </summary>
    private void AppendPresetRows()
    {
        AppendPresetRow("PricePresetSave", "把现在这一版价格存起来，起个名字以后随时换回来", "保存为预设", true);
        AppendPresetRow("PricePresetPick", "换一份存好的价格，或者一键回到默认配置", "选择预设 ▾", false);
    }

    private void AppendPresetRow(string name, string description, string buttonText, bool save)
    {
        var rect = FullRow(name, RowHeight);

        var background = UiFactory.Rounded(rect, UiFactory.PanelBackgroundLight, 10);
        UiFactory.Stretch(background.rectTransform);

        var label = UiFactory.Label(rect, description, 19f, TextAlignmentOptions.Left, UiFactory.TextMuted);
        UiFactory.Stretch(label.rectTransform, 16f, 0f, PriceValueWidth + 16f, 0f);

        // 复用「重置全部」那套右侧按钮的尺寸与位置，玩家扫一眼就知道是一列操作。
        var valueHit = UiFactory.Node($"{name}Hit", rect);
        valueHit.anchorMin = new Vector2(1f, 0f);
        valueHit.anchorMax = new Vector2(1f, 1f);
        valueHit.pivot = new Vector2(1f, 0.5f);
        valueHit.offsetMin = new Vector2(-PriceValueWidth, 8f);
        valueHit.offsetMax = new Vector2(-12f, -8f);

        var valueBackground = UiFactory.Rounded(valueHit, save ? UiFactory.Accent : UiFactory.PanelHighlight, 8);
        UiFactory.Stretch(valueBackground.rectTransform);

        var value = UiFactory.Label(
            valueHit,
            buttonText,
            20f,
            TextAlignmentOptions.Center,
            save ? UiFactory.PanelBackground : UiFactory.TextPrimary);
        UiFactory.Stretch(value.rectTransform);

        _rows.Add(new Row
        {
            IsPresetSave = save,
            IsPresetPick = !save,
            Rect = rect,
            Background = background,
            Value = value,
            ValueBackground = valueBackground,
            ValueHitArea = valueHit,
            ValueRect = valueHit,
        });
    }

    /// <summary>一件商品一格：左边名字 + 默认价，右边「− 现价 ＋」。</summary>
    private void AppendPriceRow(ShopOffer offer)
    {
        var rect = NextCell("PriceRow", RowHeight);

        var background = UiFactory.Rounded(rect, UiFactory.PanelBackgroundLight, 10);
        UiFactory.Stretch(background.rectTransform);

        // 默认价写死在左边：它不随玩家改价变，所以一次性写上就行。
        var label = UiFactory.Label(
            rect,
            $"{offer.Title} · 默认 {offer.BaseCost}",
            19f,
            TextAlignmentOptions.Left,
            UiFactory.TextPrimary);
        var labelRect = label.rectTransform;
        labelRect.anchorMin = new Vector2(0f, 0f);
        labelRect.anchorMax = new Vector2(1f, 1f);
        labelRect.offsetMin = new Vector2(14f, 0f);
        labelRect.offsetMax = new Vector2(-(PriceValueWidth + 14f), 0f);

        var valueHit = UiFactory.Node("PriceHit", rect);
        valueHit.anchorMin = new Vector2(1f, 0f);
        valueHit.anchorMax = new Vector2(1f, 1f);
        valueHit.pivot = new Vector2(1f, 0.5f);
        valueHit.offsetMin = new Vector2(-PriceValueWidth, 8f);
        valueHit.offsetMax = new Vector2(-12f, -8f);

        var valueBackground = UiFactory.Rounded(valueHit, UiFactory.PanelBackgroundLight, 8);
        UiFactory.Stretch(valueBackground.rectTransform);

        var minusRect = StepButton(valueHit, "−", false);
        var plusRect = StepButton(valueHit, "＋", true);

        var value = UiFactory.Label(valueHit, string.Empty, 20f, TextAlignmentOptions.Center, UiFactory.Warning);
        var valueRect = value.rectTransform;
        valueRect.anchorMin = new Vector2(0f, 0f);
        valueRect.anchorMax = new Vector2(1f, 1f);
        valueRect.offsetMin = new Vector2(58f, 0f);
        valueRect.offsetMax = new Vector2(-58f, 0f);

        _rows.Add(new Row
        {
            Offer = offer,
            Rect = rect,
            Background = background,
            Value = value,
            ValueBackground = valueBackground,

            // 中间那串数字既是显示、也是「恢复这一件默认价」的按钮。
            ValueHitArea = valueRect,
            ValueRect = valueRect,
            MinusRect = minusRect,
            PlusRect = plusRect,
        });
    }

    /// <summary>价格左右那两颗「− / ＋」。</summary>
    private static RectTransform StepButton(RectTransform parent, string text, bool right)
    {
        var background = UiFactory.Rounded(parent, UiFactory.PanelHighlight, 8);
        var rect = background.rectTransform;
        rect.anchorMin = new Vector2(right ? 1f : 0f, 0f);
        rect.anchorMax = new Vector2(right ? 1f : 0f, 1f);
        rect.pivot = new Vector2(right ? 1f : 0f, 0.5f);
        rect.offsetMin = new Vector2(right ? -56f : 0f, 4f);
        rect.offsetMax = new Vector2(right ? 0f : 56f, -4f);

        var label = UiFactory.Label(rect, text, 24f, TextAlignmentOptions.Center, UiFactory.TextPrimary);
        UiFactory.Stretch(label.rectTransform);

        return rect;
    }

    /// <summary>调价行上的点击：− / ＋ 改价，中间那串数字恢复这一件的默认价。</summary>
    private void HandlePriceClick(Row row, Vector2 mouse)
    {
        if (row.IsPriceReset)
        {
            if (!Inside(row.ValueHitArea, mouse))
            {
                return;
            }

            if (!ShopPricing.CanEdit)
            {
                HextechHud.Toast(!ModConfig.ShopEnabled.Value ? "商店已关闭，无法调价" : "联机时只有房主能调整商店价格");
                return;
            }

            ShopPricing.ResetAll(LocalState());
            HextechHud.Toast("商店价格已全部恢复默认");
            return;
        }

        if (row.IsPresetSave)
        {
            if (Inside(row.ValueHitArea, mouse))
            {
                HextechPresetPicker.OpenName(PricePresetActions.Save);
            }

            return;
        }

        if (row.IsPresetPick)
        {
            if (Inside(row.ValueHitArea, mouse))
            {
                // 应用完不用手动刷面板：Update 末尾每帧都会 Refresh 一遍价格行。
                HextechPresetPicker.OpenPick(
                    preset => PricePresetActions.Apply(preset, LocalState()),
                    PricePresetActions.Save);
            }

            return;
        }

        var offer = row.Offer!;

        if (!ShopPricing.CanEdit)
        {
            HextechHud.Toast(!ModConfig.ShopEnabled.Value ? "商店已关闭，无法调价" : "联机时只有房主能调整商店价格");
            return;
        }

        var step = ShiftHeld() ? 5 : 1;

        if (Inside(row.MinusRect, mouse))
        {
            StepPrice(offer, -step);
        }
        else if (Inside(row.PlusRect, mouse))
        {
            StepPrice(offer, step);
        }
        else if (Inside(row.ValueRect, mouse))
        {
            ShopPricing.Reset(offer.Id, LocalState());
            HextechHud.Toast($"{offer.Title}：已恢复默认价");
        }
    }

    /// <summary>改价以「基础价」为单位：没改过就从默认基础价开始加减。</summary>
    private static void StepPrice(ShopOffer offer, int delta)
    {
        var current = ShopPricing.OverrideFor(offer.Id) ?? offer.BaseCost;
        ShopPricing.Set(offer.Id, current + delta, LocalState());
    }

    private static bool Inside(RectTransform? rect, Vector2 mouse)
    {
        return rect != null && RectTransformUtility.RectangleContainsScreenPoint(rect, mouse, null);
    }

    /// <summary>本机玩家的海克斯状态；拿不到就是 null（单人时价格只写盘，不需要广播）。</summary>
    private static HextechState? LocalState()
    {
        var local = Character.localCharacter;
        return local == null ? null : HextechState.Get(local);
    }

    /// <summary>价格行：现价 + 有没有被改过（改过的用主题色标出来）。</summary>
    private static void RefreshPrice(Row row)
    {
        var offer = row.Offer!;
        var overridden = ShopPricing.OverrideFor(offer.Id);
        var text = $"◈ {overridden ?? offer.BaseCost}";

        if (row.LastText != text)
        {
            row.LastText = text;
            row.Value.text = text;
        }

        var changed = overridden.HasValue;

        if (row.LastHighlight == changed)
        {
            return;
        }

        row.LastHighlight = changed;
        row.Value.color = changed ? UiFactory.Accent : UiFactory.Warning;
        row.ValueBackground.color = changed ? UiFactory.PanelBackground : UiFactory.PanelBackgroundLight;
    }

    /// <summary>表头：房主（或单人）才是「商店调价」，客户端的标题上直接写明白改不了；商店关了则写「商店已关闭」。</summary>
    private static void RefreshPriceHeader(Row row)
    {
        var text = !ModConfig.ShopEnabled.Value
            ? "商店调价 · 商店已关闭"
            : (ShopPricing.CanEdit ? "商店调价" : "商店调价 · 联机时只有房主能改");

        if (row.LastText == text)
        {
            return;
        }

        row.LastText = text;
        row.Value.text = text;
    }

    /// <summary>重置行：客户端的按钮置灰（点了只会弹一句提示）。</summary>
    private static void RefreshResetRow(Row row)
    {
        var canEdit = ShopPricing.CanEdit;

        if (row.LastEnabled == canEdit)
        {
            return;
        }

        row.LastEnabled = canEdit;
        row.ValueBackground.color = canEdit ? UiFactory.Accent : UiFactory.PanelBackgroundLight;
        row.Value.color = canEdit ? UiFactory.PanelBackground : UiFactory.TextMuted;
    }

    /// <summary>行建完（或者又追加上一段）之后，把内容高度和滚动上限重算一遍。</summary>
    private void FlushLayout()
    {
        // 最后一行的 RowGap 不算进高度里，不然列表底部会多出一小段空白。
        var height = Mathf.Max(0f, -_cursor - RowGap);

        _content.sizeDelta = new Vector2(0f, height);

        // 滚动范围按「当页」算（见 HandleScroll），这里只把内容高度落好、滚动归零。
        _scroll = 0f;
        _content.anchoredPosition = new Vector2(0f, _scroll);
    }
}
