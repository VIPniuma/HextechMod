using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PeakModder.HextechMod;

/// <summary>
/// 「发现新版本」提示框：显示版本号 + 更新公告，并指路到安装器。
///
/// 不做进程内自动更新是刻意的：插件 dll 正被游戏加载着，文件锁死、进程里也换不掉自己。
/// 唯一可行的玩法是「先把新版下到旁边，等游戏退出再替换」，绕一大圈还不一定成功 ——
/// 游戏装在 Program Files 下要有管理员权限才写得动，静默替换游戏目录里的 dll 容易被杀软拦，
/// 替换失败还会留下半新半旧的 dll。安装器干同一件事稳得多（游戏关着、内置模组本体、
/// 顺带把 BepInEx 框架补齐），所以这里只负责提醒一声，并说清楚去哪更新。
/// </summary>
public sealed class HextechUpdatePrompt : MonoBehaviour
{
    private const float CardWidth = 840f;
    private const float CardHeight = 580f;
    private const float ButtonWidth = 226f;
    private const float ButtonHeight = 62f;
    private const float ButtonGap = 22f;

    /// <summary>安装器 exe 的文件名，指路文案里要用。改文件名记得一并改这里。</summary>
    private const string InstallerFileName = "HextechModInstaller.exe";

    /// <summary>按钮文案。「立即更新」已经拿掉 —— 更新只能去安装器做，这里不提供入口。</summary>
    private static readonly string[] ButtonLabels = { "忽略此版本", "稍后再说" };

    private sealed class ButtonView
    {
        public RectTransform Rect = null!;
        public Image Background = null!;
        public TextMeshProUGUI Label = null!;
        public Color BaseColor;
    }

    public static HextechUpdatePrompt? Instance { get; private set; }

    private Canvas _canvas = null!;
    private CanvasGroup _group = null!;
    private TextMeshProUGUI _title = null!;
    private TextMeshProUGUI _versionLine = null!;
    private TextMeshProUGUI _notesHeading = null!;
    private TextMeshProUGUI _notes = null!;
    private TextMeshProUGUI _guide = null!;

    private readonly ButtonView[] _buttons = new ButtonView[ButtonLabels.Length];

    private UpdateInfo? _info;
    private int _hover = -1;
    private bool _visible;

    public bool IsOpen => _visible;

    public static HextechUpdatePrompt Create(Transform parent)
    {
        var go = new GameObject("HextechUpdatePrompt");
        go.transform.SetParent(parent, false);
        var prompt = go.AddComponent<HextechUpdatePrompt>();
        prompt.Build();
        Instance = prompt;
        return prompt;
    }

    private void Build()
    {
        _canvas = UiFactory.CreateCanvas("HextechUpdateCanvas", 270);
        _canvas.transform.SetParent(transform, false);

        var root = UiFactory.Stretch(UiFactory.Node("Root", _canvas.transform));
        _group = UiFactory.Group(root);

        var backdrop = UiFactory.Solid(root, UiFactory.Backdrop);
        UiFactory.Stretch(backdrop.rectTransform);

        var card = UiFactory.Node("Card", root);
        card.anchorMin = new Vector2(0.5f, 0.5f);
        card.anchorMax = new Vector2(0.5f, 0.5f);
        card.pivot = new Vector2(0.5f, 0.5f);
        card.sizeDelta = new Vector2(CardWidth, CardHeight);

        var cardBackground = UiFactory.Rounded(card, UiFactory.PanelBackground, 22);
        UiFactory.Stretch(cardBackground.rectTransform);

        var topSheen = UiFactory.TopSheen(card, new Color(0.96f, 0.78f, 0.42f, 0.14f));
        topSheen.rectTransform.anchorMin = new Vector2(0f, 1f);
        topSheen.rectTransform.anchorMax = new Vector2(1f, 1f);
        topSheen.rectTransform.pivot = new Vector2(0.5f, 1f);
        topSheen.rectTransform.offsetMin = new Vector2(3f, -230f);
        topSheen.rectTransform.offsetMax = new Vector2(-3f, -3f);

        var strip = UiFactory.Rounded(card, UiFactory.Accent, 5);
        strip.rectTransform.anchorMin = new Vector2(0f, 1f);
        strip.rectTransform.anchorMax = new Vector2(1f, 1f);
        strip.rectTransform.pivot = new Vector2(0.5f, 1f);
        strip.rectTransform.offsetMin = new Vector2(60f, -14f);
        strip.rectTransform.offsetMax = new Vector2(-60f, -6f);

        _title = UiFactory.Label(card, "发现新版本", 42f, TextAlignmentOptions.Center, UiFactory.TextPrimary);
        _title.rectTransform.anchorMin = new Vector2(0f, 1f);
        _title.rectTransform.anchorMax = new Vector2(1f, 1f);
        _title.rectTransform.pivot = new Vector2(0.5f, 1f);
        _title.rectTransform.offsetMin = new Vector2(40f, -86f);
        _title.rectTransform.offsetMax = new Vector2(-40f, -34f);

        _versionLine = UiFactory.Label(card, string.Empty, 23f, TextAlignmentOptions.Center, UiFactory.TextMuted);
        _versionLine.rectTransform.anchorMin = new Vector2(0f, 1f);
        _versionLine.rectTransform.anchorMax = new Vector2(1f, 1f);
        _versionLine.rectTransform.pivot = new Vector2(0.5f, 1f);
        _versionLine.rectTransform.offsetMin = new Vector2(40f, -126f);
        _versionLine.rectTransform.offsetMax = new Vector2(-40f, -90f);

        var divider = UiFactory.Rounded(card, new Color(1f, 1f, 1f, 0.16f), 2);
        divider.rectTransform.anchorMin = new Vector2(0f, 1f);
        divider.rectTransform.anchorMax = new Vector2(1f, 1f);
        divider.rectTransform.pivot = new Vector2(0.5f, 1f);
        divider.rectTransform.offsetMin = new Vector2(56f, -146f);
        divider.rectTransform.offsetMax = new Vector2(-56f, -144f);

        _notesHeading = UiFactory.Label(card, "更新内容", 24f, TextAlignmentOptions.Left, UiFactory.Accent);
        _notesHeading.rectTransform.anchorMin = new Vector2(0f, 1f);
        _notesHeading.rectTransform.anchorMax = new Vector2(1f, 1f);
        _notesHeading.rectTransform.pivot = new Vector2(0.5f, 1f);
        _notesHeading.rectTransform.offsetMin = new Vector2(56f, -190f);
        _notesHeading.rectTransform.offsetMax = new Vector2(-56f, -154f);

        _notes = UiFactory.Label(card, string.Empty, 24f, TextAlignmentOptions.TopLeft, UiFactory.TextMuted);
        _notes.rectTransform.anchorMin = new Vector2(0f, 0f);
        _notes.rectTransform.anchorMax = new Vector2(1f, 1f);
        _notes.rectTransform.offsetMin = new Vector2(56f, 152f);
        _notes.rectTransform.offsetMax = new Vector2(-56f, -196f);

        // 公告可能很长，挤不下就自动缩小，别把字切掉。
        _notes.enableAutoSizing = true;
        _notes.fontSizeMin = 15f;
        _notes.fontSizeMax = 24f;

        // 「去哪更新」这行一直看得见 —— 它是玩家唯一能走的路，不再像以前那样被拿去显示下载进度。
        // 文案是两行，所以这块按两行 22pt 的高度留（按钮顶边在 88，公告底边在 152，这里占 90~146）。
        _guide = UiFactory.Label(card, string.Empty, 22f, TextAlignmentOptions.Center, UiFactory.Accent);
        _guide.rectTransform.anchorMin = new Vector2(0f, 0f);
        _guide.rectTransform.anchorMax = new Vector2(1f, 0f);
        _guide.rectTransform.pivot = new Vector2(0.5f, 0f);
        _guide.rectTransform.offsetMin = new Vector2(40f, 90f);
        _guide.rectTransform.offsetMax = new Vector2(-40f, 146f);

        var totalWidth = (ButtonLabels.Length * ButtonWidth) + ((ButtonLabels.Length - 1) * ButtonGap);
        var startX = (-totalWidth / 2f) + (ButtonWidth / 2f);

        // 配色跟着语义走：没有「立即更新」之后，主推的动作是「稍后再说」（先关掉弹窗、玩家自己去更新），
        // 所以只有它用高亮色；「忽略此版本」是次要且带破坏性的选择，用普通底色，免得看起来像推荐操作。
        var colors = new[] { UiFactory.PanelBackgroundLight, UiFactory.PanelHighlight };

        for (var i = 0; i < ButtonLabels.Length; i++)
        {
            _buttons[i] = BuildButton(
                card,
                ButtonLabels[i],
                startX + (i * (ButtonWidth + ButtonGap)),
                colors[i]);
        }

        _canvas.enabled = false;
    }

    private ButtonView BuildButton(RectTransform parent, string text, float centerX, Color background)
    {
        var rect = UiFactory.Node("Button", parent);
        rect.anchorMin = new Vector2(0.5f, 0f);
        rect.anchorMax = new Vector2(0.5f, 0f);
        rect.pivot = new Vector2(0.5f, 0f);
        rect.anchoredPosition = new Vector2(centerX, 26f);
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

    public void Show(UpdateInfo info)
    {
        if (_visible)
        {
            return;
        }

        _info = info;
        _hover = -1;

        _title.text = info.Force ? "发现新版本（建议更新）" : "发现新版本";
        _versionLine.text = $"当前 v{HextechPlugin.Version}  →  最新 v{info.Version}"
                            + (string.IsNullOrWhiteSpace(info.Published) ? string.Empty : $"　·　{info.Published}");
        _notes.text = string.IsNullOrWhiteSpace(info.Notes)
            ? "（这次的更新公告是空的，具体改动见发布页。）"
            : info.Notes;

        // 这里原来打的是完整的更新源地址。玩家看到自己的服务器地址没有任何用处，
        // 反而等于把地址摆在界面上，所以不再显示。
        _guide.text =
            $"更新方法：退出游戏，运行安装器 {InstallerFileName}\n"
            + "选中同一个游戏目录，选「一键安装」即可";

        RefreshVisuals();

        _canvas.enabled = true;
        _visible = true;
        _group.alpha = 0f;

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
        _info = null;

        HextechWindowHost.Sync();
    }

    private void Update()
    {
        if (!_visible)
        {
            return;
        }

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
            IgnoreVersion();
            return;
        }

        HextechHud.Toast("已跳过 · 下次进游戏还会提醒");
        Hide();
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

            // 强制更新时「忽略此版本」不给点（点了也没意义）；「稍后再说」永远可以关掉弹窗 ——
            // 游戏里本来也更新不了，把人锁在弹窗上毫无用处。
            var disabled = i == 0 && _info != null && _info.Force;

            if (disabled)
            {
                button.Background.color = UiFactory.PanelBackground;
                button.Label.color = UiFactory.TextMuted;
                continue;
            }

            button.Background.color = i == _hover
                ? Color.Lerp(button.BaseColor, UiFactory.Accent, 0.45f)
                : button.BaseColor;
            button.Label.color = UiFactory.TextPrimary;
        }

        // 强制更新时「忽略」没意义，直接把文案说清楚。
        if (_buttons[0] != null)
        {
            _buttons[0].Label.text = _info != null && _info.Force ? "必须更新" : ButtonLabels[0];
        }
    }

    private void IgnoreVersion()
    {
        if (_info == null || _info.Force)
        {
            return;
        }

        ModConfig.IgnoredUpdateVersion.Value = _info.Version;
        HextechHud.Toast($"已忽略 v{_info.Version} · 想更新的话清掉配置里的「更新.已忽略版本」");
        Hide();
    }
}
