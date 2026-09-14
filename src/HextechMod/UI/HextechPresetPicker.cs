using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PeakModder.HextechMod;

/// <summary>
/// 「价格预设」弹层：选一份预设换上去，或者把当前价格存成一份新的。
/// <para>
/// 为什么自己搭这一层、不用 UGUI 的 Dropdown / TMP_InputField：整个模组的界面都不接 EventSystem
/// （原因见 <see cref="HextechConfigPanel"/> 里 NumberField 的说明），那两个组件的展开、焦点、
/// 光标全靠 EventSystem 驱动，在这里根本跑不起来。所以列表、按钮、文本输入全部手算鼠标位置，
/// 文字直接读 <c>Input.inputString</c>。
/// </para>
/// <para>
/// 它不自己 Push 窗口 —— 只可能从商店（调价模式）或海克斯设置面板里打开，宿主界面已经把窗口压住了。
/// 需要宿主配合的是一件事：打开期间宿主必须跳过自己的输入处理，否则同一次点击会穿透到下面的
/// 卡片 / 按钮上（两边都在自己的 Update 里读 <c>Input.GetMouseButtonDown</c>）。宿主每帧看
/// <see cref="BlocksInput"/> 即可，它连「刚关掉的那一帧」也算进去，避免关闭用的那次点击 / ESC 顺手把宿主也关了。
/// </para>
/// </summary>
internal sealed class HextechPresetPicker : MonoBehaviour
{
    private const float CardWidth = 820f;
    private const float CardHeight = 640f;
    private const float RowHeight = 58f;
    private const float RowGap = 8f;

    /// <summary>列表视口距卡片顶 / 底的距离；视口高度就是卡片高减去这两个。</summary>
    private const float ListTop = 128f;
    private const float ListBottom = 108f;
    private const float ListHeight = CardHeight - ListTop - ListBottom;

    /// <summary>一格滚轮滚多远。列表一屏能放 6 行，滚得比配置面板慢一点更好瞄。</summary>
    private const float ScrollStep = 90f;

    /// <summary>名字长度上限（字符）：输入框放得下，也不至于把列表那一行撑爆。</summary>
    private const int MaxNameLength = 12;

    private enum Mode
    {
        Hidden,
        Pick,
        Name,
    }

    /// <summary>列表里的一行。第 0 行是「默认配置」，它没有删除按钮。</summary>
    private sealed class ItemView
    {
        public RectTransform Root = null!;
        public Image Background = null!;
        public TextMeshProUGUI Label = null!;
        public TextMeshProUGUI Detail = null!;
        public RectTransform? RemoveRect;
        public Image? RemoveBackground;
        public TextMeshProUGUI? RemoveLabel;
    }

    public static HextechPresetPicker? Instance { get; private set; }

    /// <summary>弹层关掉的那一帧也记一下，见 <see cref="BlocksInput"/>。</summary>
    private static int _closedFrame = -1;

    /// <summary>
    /// 打开弹层的那一帧（在 <see cref="Show"/> 里记）。那一帧的鼠标按下属于宿主的按钮 ——
    /// 商店右下角的「选择预设 ▾」或者设置面板里那一行 —— 弹层必须装作没看见，原因见 <see cref="Update"/>。
    /// </summary>
    private int _openedFrame = -1;

    /// <summary>
    /// 宿主界面每帧看它：真 = 这一帧的输入归弹层，宿主别处理。
    /// 含「刚关掉的那一帧」是关键 —— 否则玩家用一次点击关掉弹层，同一次点击还会落到下面的按钮上。
    /// </summary>
    public static bool BlocksInput =>
        Instance != null && (Instance._visible || _closedFrame == Time.frameCount);

    private Canvas _canvas = null!;
    private CanvasGroup _group = null!;
    private RectTransform _card = null!;
    private TextMeshProUGUI _subtitle = null!;
    private RectTransform _viewport = null!;
    private RectTransform _content = null!;
    private RectTransform _nameRoot = null!;
    private Image _nameBox = null!;
    private TextMeshProUGUI _nameText = null!;
    private RectTransform _pickFooter = null!;
    private RectTransform _nameFooter = null!;
    private RectTransform _saveNewButton = null!;
    private Image _saveNewBackground = null!;
    private RectTransform _confirmButton = null!;
    private Image _confirmBackground = null!;
    private RectTransform _cancelButton = null!;
    private Image _cancelBackground = null!;

    private readonly List<ItemView> _items = new();

    private Mode _mode = Mode.Hidden;
    private bool _visible;

    private Action<PricePreset?>? _pickCallback;
    private Action<string>? _savedCallback;
    private bool _cameFromPick;

    private int _hover = -1;
    private int _hoverRemove = -1;
    private bool _hoverSaveNew;
    private bool _hoverConfirm;
    private bool _hoverCancel;

    /// <summary>已经点过一次「删除」、等第二次确认的那一行（-1 = 没有）。</summary>
    private int _pendingDelete = -1;

    private string _nameBuffer = string.Empty;
    private float _scroll;
    private float _maxScroll;

    public static HextechPresetPicker Create(Transform parent)
    {
        var go = new GameObject("HextechPresetPicker");
        go.transform.SetParent(parent, false);

        var picker = go.AddComponent<HextechPresetPicker>();
        picker.Build();

        Instance = picker;
        return picker;
    }

    /// <summary>打开「选预设」列表。<paramref name="saved"/> 是列表底部「保存当前为新预设」用的回调。</summary>
    public static bool OpenPick(Action<PricePreset?> picked, Action<string> saved)
    {
        if (Instance == null)
        {
            return false;
        }

        Instance.Show(Mode.Pick, picked, saved, false);
        return true;
    }

    /// <summary>直接打开「起名字」那一屏（商店里的「保存为预设」按钮走这条路）。</summary>
    public static bool OpenName(Action<string> saved)
    {
        if (Instance == null)
        {
            return false;
        }

        Instance.Show(Mode.Name, null, saved, false);
        return true;
    }

    /// <summary>
    /// 强制收起来。宿主界面自己关掉（被别的路径 Hide 掉）时一定要调 ——
    /// 否则弹层会孤零零留在屏幕上，而它开着的时候又让所有界面跳过输入，玩家会觉得游戏坏了。
    /// </summary>
    public static void ForceClose()
    {
        Instance?.Hide();
    }

    private void Build()
    {
        _canvas = UiFactory.CreateCanvas("HextechPresetCanvas", 268);
        _canvas.transform.SetParent(transform, false);

        var root = UiFactory.Stretch(UiFactory.Node("Root", _canvas.transform));
        _group = UiFactory.Group(root);

        var backdrop = UiFactory.Solid(root, UiFactory.Backdrop);
        UiFactory.Stretch(backdrop.rectTransform);

        _card = UiFactory.Node("Card", root);
        _card.anchorMin = new Vector2(0.5f, 0.5f);
        _card.anchorMax = new Vector2(0.5f, 0.5f);
        _card.pivot = new Vector2(0.5f, 0.5f);
        _card.sizeDelta = new Vector2(CardWidth, CardHeight);

        var background = UiFactory.Rounded(_card, UiFactory.PanelBackground, 22);
        UiFactory.Stretch(background.rectTransform);

        var strip = UiFactory.Rounded(_card, UiFactory.Accent, 5);
        strip.rectTransform.anchorMin = new Vector2(0f, 1f);
        strip.rectTransform.anchorMax = new Vector2(1f, 1f);
        strip.rectTransform.pivot = new Vector2(0.5f, 1f);
        strip.rectTransform.offsetMin = new Vector2(60f, -14f);
        strip.rectTransform.offsetMax = new Vector2(-60f, -6f);

        var title = UiFactory.Label(_card, "价格预设", 36f, TextAlignmentOptions.Center, UiFactory.TextPrimary);
        TopRow(title.rectTransform, -26f, 46f);

        _subtitle = UiFactory.Label(_card, string.Empty, 21f, TextAlignmentOptions.Center, UiFactory.TextMuted);
        TopRow(_subtitle.rectTransform, -84f, 40f);

        _viewport = UiFactory.Node("Viewport", _card);
        _viewport.anchorMin = Vector2.zero;
        _viewport.anchorMax = Vector2.one;
        _viewport.offsetMin = new Vector2(34f, ListBottom);
        _viewport.offsetMax = new Vector2(-34f, -ListTop);
        _viewport.gameObject.AddComponent<RectMask2D>();

        _content = UiFactory.Node("Content", _viewport);
        _content.anchorMin = new Vector2(0f, 1f);
        _content.anchorMax = new Vector2(1f, 1f);
        _content.pivot = new Vector2(0.5f, 1f);
        _content.anchoredPosition = Vector2.zero;
        _content.sizeDelta = Vector2.zero;

        BuildNameRoot();
        BuildFooters();

        _canvas.enabled = false;
    }

    private static void TopRow(RectTransform rect, float top, float height)
    {
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.offsetMin = new Vector2(40f, top - height);
        rect.offsetMax = new Vector2(-40f, top);
    }

    /// <summary>「起名字」那一屏：一个输入框 + 一行说明，位置和列表区重合。</summary>
    private void BuildNameRoot()
    {
        _nameRoot = UiFactory.Node("NameRoot", _card);
        _nameRoot.anchorMin = Vector2.zero;
        _nameRoot.anchorMax = Vector2.one;
        _nameRoot.offsetMin = new Vector2(34f, ListBottom);
        _nameRoot.offsetMax = new Vector2(-34f, -ListTop);

        var field = UiFactory.Node("NameField", _nameRoot);
        field.anchorMin = new Vector2(0.5f, 1f);
        field.anchorMax = new Vector2(0.5f, 1f);
        field.pivot = new Vector2(0.5f, 1f);
        field.anchoredPosition = new Vector2(0f, -150f);
        field.sizeDelta = new Vector2(560f, 68f);

        _nameBox = UiFactory.Rounded(field, UiFactory.PanelBackground, 12);
        UiFactory.Stretch(_nameBox.rectTransform);

        _nameText = UiFactory.Label(field, string.Empty, 26f, TextAlignmentOptions.Center, UiFactory.Warning);
        UiFactory.Stretch(_nameText.rectTransform, 14f, 0f, 14f, 0f);

        var hint = UiFactory.Label(
            _nameRoot,
            $"最多 {MaxNameLength} 个字 · 回车保存 · ESC 返回上一步\n"
            + "（游戏里输不了中文，想要中文名可以事后改 config 目录里的预设文件）",
            20f,
            TextAlignmentOptions.Center,
            UiFactory.TextMuted);
        var hintRect = hint.rectTransform;
        hintRect.anchorMin = new Vector2(0f, 1f);
        hintRect.anchorMax = new Vector2(1f, 1f);
        hintRect.pivot = new Vector2(0.5f, 1f);
        hintRect.offsetMin = new Vector2(20f, -270f);
        hintRect.offsetMax = new Vector2(-20f, -232f);

        _nameRoot.gameObject.SetActive(false);
    }

    private void BuildFooters()
    {
        _pickFooter = BuildFooterArea();
        _saveNewButton = BuildButton(_pickFooter, "保存当前为新预设…", 380f, UiFactory.Accent, out _saveNewBackground);
        _saveNewButton.anchoredPosition = Vector2.zero;

        _nameFooter = BuildFooterArea();
        _confirmButton = BuildButton(_nameFooter, "保存", 210f, UiFactory.Accent, out _confirmBackground);
        _confirmButton.anchoredPosition = new Vector2(-116f, 0f);

        _cancelButton = BuildButton(_nameFooter, "取消", 170f, UiFactory.PanelBackgroundLight, out _cancelBackground);
        _cancelButton.anchoredPosition = new Vector2(112f, 0f);

        _nameFooter.gameObject.SetActive(false);
    }

    private RectTransform BuildFooterArea()
    {
        var area = UiFactory.Node("Footer", _card);
        area.anchorMin = new Vector2(0f, 0f);
        area.anchorMax = new Vector2(1f, 0f);
        area.pivot = new Vector2(0.5f, 0f);
        area.offsetMin = new Vector2(34f, 26f);
        area.offsetMax = new Vector2(-34f, 86f);
        return area;
    }

    private static RectTransform BuildButton(RectTransform parent, string text, float width, Color color, out Image background)
    {
        var rect = UiFactory.Node("Button", parent);
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(width, 56f);

        background = UiFactory.Rounded(rect, color, 12);
        UiFactory.Stretch(background.rectTransform);

        var label = UiFactory.Label(rect, text, 23f, TextAlignmentOptions.Center, UiFactory.TextPrimary);
        UiFactory.Stretch(label.rectTransform);

        return rect;
    }

    private void Show(Mode mode, Action<PricePreset?>? picked, Action<string>? saved, bool cameFromPick)
    {
        _pickCallback = picked;
        _savedCallback = saved;
        _cameFromPick = cameFromPick;
        _pendingDelete = -1;
        _hover = -1;
        _hoverRemove = -1;
        _hoverSaveNew = false;
        _hoverConfirm = false;
        _hoverCancel = false;
        _scroll = 0f;

        if (mode == Mode.Name)
        {
            _nameBuffer = PricePresets.NextAutoName();
        }

        ApplyMode(mode);

        _canvas.enabled = true;
        _visible = true;
        _group.alpha = 0f;

        // 打开它的那一次点击是宿主按钮的：这一帧 Update 会跳过输入处理（见那边的说明）。
        _openedFrame = Time.frameCount;
    }

    private void Hide()
    {
        if (!_visible)
        {
            return;
        }

        _canvas.enabled = false;
        _visible = false;
        _mode = Mode.Hidden;
        _pickCallback = null;
        _savedCallback = null;
        _closedFrame = Time.frameCount;
    }

    private void ApplyMode(Mode mode)
    {
        _mode = mode;

        var pick = mode == Mode.Pick;

        _viewport.gameObject.SetActive(pick);
        _nameRoot.gameObject.SetActive(!pick);
        _pickFooter.gameObject.SetActive(pick);
        _nameFooter.gameObject.SetActive(!pick);

        _subtitle.text = pick
            ? "点一份就能换上 · 调好价之后可以「保存当前为新预设」"
            : "给这份价格起个名字（同名会整份覆盖）";

        if (pick)
        {
            RebuildItems();
            RefreshItemVisuals();
        }
        else
        {
            RefreshNameText();
        }

        RefreshFooterVisuals();
    }

    private void Update()
    {
        if (!_visible)
        {
            return;
        }

        _group.alpha = Mathf.MoveTowards(_group.alpha, 1f, Time.deltaTime * 10f);

        // 打开弹层的那一帧不收输入。那次鼠标按下属于宿主的按钮，而弹层的 Update 比宿主晚跑
        // （组件是后创建的），于是这里会看到「正按着 + 鼠标既不在列表行上、也不在卡片里」，
        // 被下面的「点卡片外面 = 取消」分支当场关掉 —— 玩家看到的就是「点了选择预设没反应」。
        if (Time.frameCount == _openedFrame)
        {
            return;
        }

        if (_mode == Mode.Name)
        {
            UpdateNameMode();
            return;
        }

        UpdatePickMode();
    }

    // ── 选预设 ──────────────────────────────────────────────────

    private void UpdatePickMode()
    {
        var mouse = (Vector2)Input.mousePosition;

        // 只有鼠标在列表上时滚轮才动列表，在卡片别处滚不该带走它。
        if (_maxScroll > 0.01f && RectTransformUtility.RectangleContainsScreenPoint(_viewport, mouse, null))
        {
            var wheel = Input.mouseScrollDelta.y;

            if (Mathf.Abs(wheel) > 0.01f)
            {
                _scroll = Mathf.Clamp(_scroll - (wheel * ScrollStep), 0f, _maxScroll);
                _content.anchoredPosition = new Vector2(0f, _scroll);
            }
        }

        _hover = -1;
        _hoverRemove = -1;

        // 行会随滚动跑到视口外，而 RectMask2D 只裁渲染、不裁命中判定 —— 不先卡住视口的话，
        // 点标题区、页脚那圈空白也可能选中一行。
        var overList = RectTransformUtility.RectangleContainsScreenPoint(_viewport, mouse, null);

        for (var i = 0; overList && i < _items.Count; i++)
        {
            if (!RectTransformUtility.RectangleContainsScreenPoint(_items[i].Root, mouse, null))
            {
                continue;
            }

            _hover = i;

            if (_items[i].RemoveRect != null
                && RectTransformUtility.RectangleContainsScreenPoint(_items[i].RemoveRect, mouse, null))
            {
                _hoverRemove = i;
            }

            break;
        }

        _hoverSaveNew = RectTransformUtility.RectangleContainsScreenPoint(_saveNewButton, mouse, null);

        RefreshItemVisuals();
        RefreshFooterVisuals();

        if (Input.GetKeyDown(KeyCode.Escape))
        {
            Hide();
            return;
        }

        if (!Input.GetMouseButtonDown(0))
        {
            return;
        }

        if (_hoverSaveNew)
        {
            _pendingDelete = -1;
            _cameFromPick = true;
            _nameBuffer = PricePresets.NextAutoName();
            ApplyMode(Mode.Name);
            return;
        }

        if (_hover < 0)
        {
            // 点卡片外面 = 取消；点卡片里的空白不关，免得手滑点到空处就把这一屏丢了。
            if (!RectTransformUtility.RectangleContainsScreenPoint(_card, mouse, null))
            {
                Hide();
            }

            return;
        }

        // 删除要点两次：第一次点只是把那一行变成「确认」，点别处就撤销。
        // 预设是玩家自己一份份存出来的，误删了没法找回来。
        if (_hoverRemove == _hover)
        {
            if (_pendingDelete == _hover)
            {
                DeleteAt(_hover);
            }
            else
            {
                _pendingDelete = _hover;
                RefreshItemVisuals();
            }

            return;
        }

        _pendingDelete = -1;
        Pick();
    }

    private void Pick()
    {
        var callback = _pickCallback;

        // 第 0 行是「默认配置」，它不对应任何预设 —— 回调收 null 就是「切回默认」。
        if (_hover == 0)
        {
            Hide();
            callback?.Invoke(null);
            return;
        }

        var presets = PricePresets.All;
        var index = _hover - 1;

        if (index < 0 || index >= presets.Count)
        {
            return;
        }

        var preset = presets[index];
        Hide();
        callback?.Invoke(preset);
    }

    private void DeleteAt(int row)
    {
        var presets = PricePresets.All;
        var index = row - 1;

        if (index < 0 || index >= presets.Count)
        {
            return;
        }

        var name = presets[index].Name;

        if (PricePresets.Remove(name))
        {
            HextechHud.Toast($"已删除预设「{name}」");
        }

        _pendingDelete = -1;
        _hover = -1;
        _hoverRemove = -1;
        RebuildItems();
        RefreshItemVisuals();
    }

    // ── 起名字 ──────────────────────────────────────────────────

    private void UpdateNameMode()
    {
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            Back();
            return;
        }

        if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
        {
            CommitName();
            return;
        }

        if (Input.GetKeyDown(KeyCode.Backspace) && _nameBuffer.Length > 0)
        {
            _nameBuffer = _nameBuffer.Substring(0, _nameBuffer.Length - 1);
        }

        // Input.inputString 给的是「这一帧敲进去的字符」，中文要靠输入法提交（多数情况下游戏里拿不到）。
        var typed = Input.inputString;

        for (var i = 0; i < typed.Length; i++)
        {
            var character = typed[i];

            // 控制字符不要（回车 / 制表符都在里面），方括号会把预设文件的段头写坏。
            if (character < ' ' || character == '[' || character == ']')
            {
                continue;
            }

            if (_nameBuffer.Length >= MaxNameLength)
            {
                break;
            }

            _nameBuffer += character;
        }

        var mouse = (Vector2)Input.mousePosition;
        _hoverConfirm = RectTransformUtility.RectangleContainsScreenPoint(_confirmButton, mouse, null);
        _hoverCancel = RectTransformUtility.RectangleContainsScreenPoint(_cancelButton, mouse, null);

        RefreshNameText();
        RefreshFooterVisuals();

        if (!Input.GetMouseButtonDown(0))
        {
            return;
        }

        if (_hoverConfirm)
        {
            CommitName();
        }
        else if (_hoverCancel)
        {
            Back();
        }
    }

    /// <summary>取消：从列表点进来的就退回列表，从外面直接点「保存为预设」进来的就整个关掉。</summary>
    private void Back()
    {
        if (_cameFromPick)
        {
            ApplyMode(Mode.Pick);
            return;
        }

        Hide();
    }

    private void CommitName()
    {
        var name = PricePresets.SanitizeName(_nameBuffer);

        if (name.Length == 0)
        {
            name = PricePresets.NextAutoName();
        }

        var callback = _savedCallback;
        Hide();
        callback?.Invoke(name);
    }

    // ── 刷新 ────────────────────────────────────────────────────

    private void RebuildItems()
    {
        foreach (var item in _items)
        {
            if (item.Root != null)
            {
                item.Root.gameObject.SetActive(false);
                Destroy(item.Root.gameObject);
            }
        }

        _items.Clear();

        var presets = PricePresets.All;
        var count = presets.Count + 1;
        var height = (count * RowHeight) + ((count - 1) * RowGap);

        _content.sizeDelta = new Vector2(0f, height);
        _maxScroll = Mathf.Max(0f, height - ListHeight);
        _scroll = Mathf.Clamp(_scroll, 0f, _maxScroll);
        _content.anchoredPosition = new Vector2(0f, _scroll);

        for (var i = 0; i < count; i++)
        {
            _items.Add(i == 0 ? BuildItem(0, null) : BuildItem(i, presets[i - 1]));
        }
    }

    private ItemView BuildItem(int index, PricePreset? preset)
    {
        var isDefault = preset == null;

        var rect = UiFactory.Node($"Item{index}", _content);
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.sizeDelta = new Vector2(0f, RowHeight);
        rect.anchoredPosition = new Vector2(0f, -(index * (RowHeight + RowGap)));

        var background = UiFactory.Rounded(rect, UiFactory.PanelBackgroundLight, 10);
        UiFactory.Stretch(background.rectTransform);

        var label = UiFactory.Label(
            rect,
            isDefault ? "默认配置" : preset!.Name,
            24f,
            TextAlignmentOptions.Left,
            UiFactory.TextPrimary);
        var labelRect = label.rectTransform;
        labelRect.anchorMin = new Vector2(0f, 0f);
        labelRect.anchorMax = new Vector2(0f, 1f);
        labelRect.pivot = new Vector2(0f, 0.5f);
        labelRect.offsetMin = new Vector2(18f, 0f);
        labelRect.offsetMax = new Vector2(18f + 300f, 0f);

        var detail = UiFactory.Label(
            rect,
            isDefault ? "清掉所有调价 · 倍率等数值回模组默认" : preset!.Summary,
            19f,
            TextAlignmentOptions.Left,
            UiFactory.TextMuted);
        var detailRect = detail.rectTransform;
        detailRect.anchorMin = new Vector2(0f, 0f);
        detailRect.anchorMax = new Vector2(1f, 1f);
        detailRect.offsetMin = new Vector2(330f, 0f);
        detailRect.offsetMax = new Vector2(-88f, 0f);

        var item = new ItemView
        {
            Root = rect,
            Background = background,
            Label = label,
            Detail = detail,
        };

        if (isDefault)
        {
            return item;
        }

        var removeBackground = UiFactory.Rounded(rect, UiFactory.PanelBackground, 8);
        var removeRect = removeBackground.rectTransform;
        removeRect.anchorMin = new Vector2(1f, 0.5f);
        removeRect.anchorMax = new Vector2(1f, 0.5f);
        removeRect.pivot = new Vector2(1f, 0.5f);
        removeRect.anchoredPosition = new Vector2(-12f, 0f);
        removeRect.sizeDelta = new Vector2(64f, 40f);

        var removeLabel = UiFactory.Label(removeRect, "删除", 19f, TextAlignmentOptions.Center, UiFactory.TextMuted);
        UiFactory.Stretch(removeLabel.rectTransform);

        item.RemoveRect = removeRect;
        item.RemoveBackground = removeBackground;
        item.RemoveLabel = removeLabel;
        return item;
    }

    private void RefreshItemVisuals()
    {
        for (var i = 0; i < _items.Count; i++)
        {
            var item = _items[i];

            item.Background.color = i == _hover
                ? Color.Lerp(UiFactory.PanelBackgroundLight, UiFactory.Accent, 0.22f)
                : UiFactory.PanelBackgroundLight;

            if (item.RemoveRect == null || item.RemoveLabel == null || item.RemoveBackground == null)
            {
                continue;
            }

            var pending = i == _pendingDelete;

            item.RemoveLabel.text = pending ? "确认" : "删除";
            item.RemoveLabel.color = pending
                ? UiFactory.PanelBackground
                : (i == _hoverRemove ? UiFactory.TextPrimary : UiFactory.TextMuted);
            item.RemoveBackground.color = pending
                ? UiFactory.Danger
                : (i == _hoverRemove ? UiFactory.PanelHighlight : UiFactory.PanelBackground);
        }
    }

    private void RefreshNameText()
    {
        var text = _nameBuffer + (Time.unscaledTime % 1f < 0.5f ? "|" : " ");

        if (_nameText.text != text)
        {
            _nameText.text = text;
        }
    }

    private void RefreshFooterVisuals()
    {
        _saveNewBackground.color = _hoverSaveNew
            ? Color.Lerp(UiFactory.Accent, UiFactory.TextPrimary, 0.30f)
            : UiFactory.Accent;

        _confirmBackground.color = _hoverConfirm
            ? Color.Lerp(UiFactory.Accent, UiFactory.TextPrimary, 0.30f)
            : UiFactory.Accent;

        _cancelBackground.color = _hoverCancel ? UiFactory.PanelHighlight : UiFactory.PanelBackgroundLight;
    }
}
