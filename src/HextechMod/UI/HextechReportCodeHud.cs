using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PeakModder.HextechMod;

/// <summary>
/// 「日志已上传」的编号浮窗：把 6 位取件编号写得很大，配一颗「复制编号」和一颗「确定」。
/// <para>
/// 为什么单独做一块，而不是像原来那样只弹一条吐司：编号是玩家唯一要转达给作者的东西，
/// 而吐司十秒就没了、又只是屏幕顶上的一行小字，玩家只能凭记忆手打 6 位数字 ——
/// 打错一位作者就白跑一趟。这里停到玩家自己点「确定」为止，中间随时能把编号再复制一次。
/// </para>
/// <para>
/// 点得动的前提是鼠标要解锁，而光标归游戏原生的窗口系统管（见 <see cref="HextechWindowHost"/>），
/// 所以它自己 <see cref="HextechWindowHost.Push"/>。代价是这期间玩家动不了 ——
/// 本来就是「看一眼编号、复制走、关掉」的窗口，可以接受。
/// </para>
/// <para>
/// 不接 EventSystem、鼠标全靠手算（同 <see cref="HextechConfigPanel"/> 里 NumberField 的说明）；
/// 也别把「停多久自动关」加回来：玩家可能就是要去群里发消息，回来发现编号没了更麻烦。
/// </para>
/// </summary>
public sealed class HextechReportCodeHud : MonoBehaviour
{
    private const float CardWidth = 720f;
    private const float CardHeight = 420f;
    private const float ButtonWidth = 240f;
    private const float ButtonHeight = 62f;
    private const float ButtonGap = 24f;

    /// <summary>关掉的那一帧也记一下，见 <see cref="BlocksInput"/>。</summary>
    private static int _closedFrame = -1;

    private sealed class ButtonView
    {
        public RectTransform Rect = null!;
        public Image Background = null!;
        public TextMeshProUGUI Label = null!;
        public Color BaseColor;
    }

    public static HextechReportCodeHud? Instance { get; private set; }

    /// <summary>
    /// 别的界面每帧看它：真 = 这一帧的输入归本浮窗。含「刚关掉的那一帧」——
    /// 否则玩家点「确定」的那一下会同时穿到底下的设置面板行上（两边都在自己的 Update 里读鼠标）。
    /// </summary>
    public static bool BlocksInput =>
        Instance != null && (Instance._visible || _closedFrame == Time.frameCount);

    private Canvas _canvas = null!;
    private CanvasGroup _group = null!;
    private TextMeshProUGUI _code = null!;
    private TextMeshProUGUI _detail = null!;

    private readonly ButtonView[] _buttons = new ButtonView[2];
    private int _hover = -1;
    private bool _visible;
    private bool _copied;

    public bool IsOpen => _visible;

    public static HextechReportCodeHud Create(Transform parent)
    {
        var go = new GameObject("HextechReportCodeHud");
        go.transform.SetParent(parent, false);
        var hud = go.AddComponent<HextechReportCodeHud>();
        hud.Build();
        Instance = hud;
        return hud;
    }

    /// <summary>两颗按钮的文案：左边复制、右边确定（顺序和更新提示一致，玩家不用重新找）。</summary>
    private static readonly string[] ButtonLabels = { "复制编号", "确定" };

    private void Build()
    {
        // 画布是独立的根对象（和别的面板一样），显隐由 _canvas.enabled 控制。
        _canvas = UiFactory.CreateCanvas("HextechReportCodeCanvas", 280);

        var root = UiFactory.Stretch(UiFactory.Node("Root", _canvas.transform));
        _group = UiFactory.Group(root);

        var backdrop = UiFactory.Solid(root, UiFactory.Backdrop);
        UiFactory.Stretch(backdrop.rectTransform);

        var card = UiFactory.Node("Card", root);
        card.anchorMin = new Vector2(0.5f, 0.5f);
        card.anchorMax = new Vector2(0.5f, 0.5f);
        card.pivot = new Vector2(0.5f, 0.5f);
        card.sizeDelta = new Vector2(CardWidth, CardHeight);

        var background = UiFactory.Rounded(card, UiFactory.PanelBackground, 22);
        UiFactory.Stretch(background.rectTransform);

        var strip = UiFactory.Rounded(card, UiFactory.Accent, 5);
        strip.rectTransform.anchorMin = new Vector2(0f, 1f);
        strip.rectTransform.anchorMax = new Vector2(1f, 1f);
        strip.rectTransform.pivot = new Vector2(0.5f, 1f);
        strip.rectTransform.offsetMin = new Vector2(60f, -14f);
        strip.rectTransform.offsetMax = new Vector2(-60f, -6f);

        var title = UiFactory.Label(card, "日志已上传", 34f, TextAlignmentOptions.Center, UiFactory.TextPrimary);
        title.rectTransform.anchorMin = new Vector2(0f, 1f);
        title.rectTransform.anchorMax = new Vector2(1f, 1f);
        title.rectTransform.pivot = new Vector2(0.5f, 1f);
        title.rectTransform.offsetMin = new Vector2(40f, -82f);
        title.rectTransform.offsetMax = new Vector2(-40f, -30f);

        // 编号是这块窗口存在的唯一理由，所以它最大、用主题色：玩家抄的时候不用凑近看。
        _code = UiFactory.Label(card, string.Empty, 62f, TextAlignmentOptions.Center, UiFactory.Accent);
        _code.characterSpacing = 14f;
        _code.rectTransform.anchorMin = new Vector2(0f, 1f);
        _code.rectTransform.anchorMax = new Vector2(1f, 1f);
        _code.rectTransform.pivot = new Vector2(0.5f, 1f);
        _code.rectTransform.offsetMin = new Vector2(40f, -164f);
        _code.rectTransform.offsetMax = new Vector2(-40f, -94f);

        var divider = UiFactory.Rounded(card, new Color(1f, 1f, 1f, 0.16f), 2);
        divider.rectTransform.anchorMin = new Vector2(0f, 1f);
        divider.rectTransform.anchorMax = new Vector2(1f, 1f);
        divider.rectTransform.pivot = new Vector2(0.5f, 1f);
        divider.rectTransform.offsetMin = new Vector2(60f, -180f);
        divider.rectTransform.offsetMax = new Vector2(-60f, -178f);

        _detail = UiFactory.Label(card, string.Empty, 21f, TextAlignmentOptions.Center, UiFactory.TextMuted);
        _detail.rectTransform.anchorMin = new Vector2(0f, 1f);
        _detail.rectTransform.anchorMax = new Vector2(1f, 1f);
        _detail.rectTransform.pivot = new Vector2(0.5f, 1f);
        _detail.rectTransform.offsetMin = new Vector2(46f, -276f);
        _detail.rectTransform.offsetMax = new Vector2(-46f, -190f);

        var totalWidth = (ButtonLabels.Length * ButtonWidth) + ((ButtonLabels.Length - 1) * ButtonGap);
        var startX = (-totalWidth / 2f) + (ButtonWidth / 2f);

        // 左边复制用普通底色、右边确定用主题色：这是「看完了，走吧」，别让复制看着像主操作。
        var colors = new[] { UiFactory.PanelHighlight, UiFactory.Accent };

        for (var i = 0; i < ButtonLabels.Length; i++)
        {
            _buttons[i] = BuildButton(card, ButtonLabels[i], startX + (i * (ButtonWidth + ButtonGap)), colors[i]);
        }

        // 「确定」上的字用深色（压在主题色上），不然亮底亮字看不出写的是什么。
        _buttons[1].Label.color = UiFactory.PanelBackground;

        _canvas.enabled = false;
    }

    private ButtonView BuildButton(RectTransform parent, string text, float centerX, Color background)
    {
        var rect = UiFactory.Node("Button", parent);
        rect.anchorMin = new Vector2(0.5f, 0f);
        rect.anchorMax = new Vector2(0.5f, 0f);
        rect.pivot = new Vector2(0.5f, 0f);
        rect.anchoredPosition = new Vector2(centerX, 30f);
        rect.sizeDelta = new Vector2(ButtonWidth, ButtonHeight);

        var image = UiFactory.Rounded(rect, background, 14);
        UiFactory.Stretch(image.rectTransform);

        var label = UiFactory.Label(rect, text, 26f, TextAlignmentOptions.Center, UiFactory.TextPrimary);
        UiFactory.Stretch(label.rectTransform);

        return new ButtonView
        {
            Rect = rect,
            Background = image,
            Label = label,
            BaseColor = background,
        };
    }

    /// <summary>弹出编号浮窗。已经开着就直接把编号换掉（玩家在面板开着时又传了一次）。</summary>
    public void Show(string code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return;
        }

        // 顺手先复制一次：多数人下一步就是把它粘给作者。
        ReportHistory.CopyToClipboard(code);
        _copied = false;

        _code.text = code;
        RefreshDetail();
        _buttons[0].Label.text = ButtonLabels[0];

        if (_visible)
        {
            return;
        }

        _hover = -1;
        _group.alpha = 0f;
        _canvas.enabled = true;
        _visible = true;

        HextechWindowHost.Push();
    }

    public void Hide()
    {
        if (!_visible)
        {
            return;
        }

        _canvas.enabled = false;
        _visible = false;
        _closedFrame = Time.frameCount;

        HextechWindowHost.Sync();
    }

    private void Update()
    {
        if (!_visible)
        {
            return;
        }

        // 窗口被游戏关掉（ESC 归它管）之后浮窗不该留在屏幕上：那时鼠标已经收回，
        // 点不动任何按钮，玩家会以为游戏卡住了。
        if (!HextechWindowHost.IsOpen || Input.GetKeyDown(KeyCode.Escape))
        {
            Hide();
            return;
        }

        _group.alpha = Mathf.MoveTowards(_group.alpha, 1f, Time.deltaTime * 8f);

        var mouse = (Vector2)Input.mousePosition;
        _hover = -1;

        for (var i = 0; i < _buttons.Length; i++)
        {
            if (_buttons[i] != null && RectTransformUtility.RectangleContainsScreenPoint(_buttons[i].Rect, mouse, null))
            {
                _hover = i;
            }
        }

        RefreshVisuals();

        if (!Input.GetMouseButtonDown(0) || _hover < 0)
        {
            return;
        }

        if (_hover == 0)
        {
            CopyAgain();
            return;
        }

        Hide();
    }

    /// <summary>再复制一次：剪贴板被别的东西顶掉之后，玩家不用去别处翻这个编号。</summary>
    private void CopyAgain()
    {
        if (_copied)
        {
            return;
        }

        ReportHistory.CopyToClipboard(_code.text);
        _copied = true;
        _buttons[0].Label.text = "已复制";
        RefreshDetail();

        HextechHud.Toast($"编号 {_code.text} 已复制 · 发给作者就能取这份报告", 6f);
    }

    private void RefreshDetail()
    {
        _detail.text = _copied
            ? $"把这个编号发给作者，他按编号取这份报告\n服务器只留 {ReportHistory.KeepHours} 小时 · 编号已复制，粘贴即可"
            : $"把这个编号发给作者，他按编号取这份报告\n服务器只留 {ReportHistory.KeepHours} 小时 · 已经自动复制，也可以点左边的按钮";
    }

    private void RefreshVisuals()
    {
        for (var i = 0; i < _buttons.Length; i++)
        {
            var button = _buttons[i];

            if (button == null)
            {
                continue;
            }

            // 「已复制」之后那颗按钮不再高亮：它已经没有可做的事了。
            var disabled = i == 0 && _copied;

            button.Background.color = disabled
                ? UiFactory.PanelBackgroundLight
                : i == _hover
                    ? Color.Lerp(button.BaseColor, UiFactory.Accent, 0.45f)
                    : button.BaseColor;

            button.Label.color = i == 1
                ? UiFactory.PanelBackground
                : disabled
                    ? UiFactory.TextMuted
                    : UiFactory.TextPrimary;
        }
    }
}
