using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PeakModder.HextechMod;

/// <summary>
/// 「发现新版本」提示框：显示版本号 + 更新公告，三颗按钮 —— 忽略此版本 / 立即更新 / 关闭。
/// <para>
/// 「立即更新」走游戏内自更新：下载新版 dll、按清单校验 SHA256，写到当前 dll 旁边，
/// 拉起一个等游戏退出的替换脚本（见 <see cref="HextechSelfUpdater"/>），然后自动退出游戏 ——
/// 脚本会原地换上新 dll 并把游戏重新打开。下载中允许「关闭」取消；一旦替换脚本就绪，
/// 弹窗就进入「即将退出重启」的终态，谁也关不掉，几秒后游戏自动退出重启。
/// </para>
/// </summary>
public sealed class HextechUpdatePrompt : MonoBehaviour
{
    private enum UpdateState
    {
        /// <summary>还没开始更新：三颗按钮各在其位。</summary>
        Idle,

        /// <summary>正在下载新版 dll：「关闭」变成取消，其余按钮禁用。</summary>
        Downloading,

        /// <summary>替换脚本已就绪：游戏即将自动退出重启，弹窗锁死不再响应任何输入。</summary>
        Ready,
    }

    private const float CardWidth = 840f;
    private const float CardHeight = 580f;
    private const float ButtonWidth = 226f;
    private const float ButtonHeight = 62f;
    private const float ButtonGap = 22f;

    /// <summary>没在更新时状态行显示的说明。</summary>
    private const string IdleStatusText =
        "点「立即更新」自动完成：下载新版 → 自动退出游戏 → 替换并重新打开。下载中点「关闭」可取消。";

    private static readonly string[] ButtonLabels = { "忽略此版本", "立即更新", "关闭" };

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
    private TextMeshProUGUI _status = null!;
    private Image _progressTrack = null!;
    private Image _progressFill = null!;

    private readonly ButtonView[] _buttons = new ButtonView[ButtonLabels.Length];

    private UpdateInfo? _info;
    private int _hover = -1;
    private bool _visible;
    private UpdateState _state = UpdateState.Idle;
    private Coroutine? _downloading;

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

        // 进度条（平时藏着，点「立即更新」才现身）+ 状态行：游戏内更新的全部反馈都在这一小块。
        _progressTrack = UiFactory.Rounded(card, UiFactory.PanelBackgroundLight, 6);
        _progressTrack.rectTransform.anchorMin = new Vector2(0f, 1f);
        _progressTrack.rectTransform.anchorMax = new Vector2(1f, 1f);
        _progressTrack.rectTransform.pivot = new Vector2(0.5f, 1f);
        _progressTrack.rectTransform.offsetMin = new Vector2(56f, -(CardHeight - 110f));
        _progressTrack.rectTransform.offsetMax = new Vector2(-56f, -(CardHeight - 94f));

        _progressFill = UiFactory.Rounded(_progressTrack.rectTransform, UiFactory.Accent, 6);
        var fillRect = _progressFill.rectTransform;
        fillRect.anchorMin = new Vector2(0f, 0f);
        fillRect.anchorMax = new Vector2(0f, 1f);
        fillRect.pivot = new Vector2(0f, 0.5f);
        fillRect.offsetMin = new Vector2(0f, 0f);
        fillRect.offsetMax = new Vector2(0f, 0f);

        _status = UiFactory.Label(card, string.Empty, 20f, TextAlignmentOptions.Center, UiFactory.TextMuted);
        _status.rectTransform.anchorMin = new Vector2(0f, 1f);
        _status.rectTransform.anchorMax = new Vector2(1f, 1f);
        _status.rectTransform.pivot = new Vector2(0.5f, 1f);
        _status.rectTransform.offsetMin = new Vector2(40f, -(CardHeight - 146f));
        _status.rectTransform.offsetMax = new Vector2(-40f, -(CardHeight - 118f));
        _status.enableAutoSizing = true;
        _status.fontSizeMin = 15f;
        _status.fontSizeMax = 20f;

        var totalWidth = (ButtonLabels.Length * ButtonWidth) + ((ButtonLabels.Length - 1) * ButtonGap);
        var startX = (-totalWidth / 2f) + (ButtonWidth / 2f);

        // 配色跟着语义走：「立即更新」是主推动作用高亮色；「忽略此版本」带破坏性（这版再也不提示）、
        // 「关闭」是中性动作，都用普通底色，免得看起来像推荐操作。
        var colors = new[] { UiFactory.PanelBackgroundLight, UiFactory.Accent, UiFactory.PanelBackgroundLight };

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
        _state = UpdateState.Idle;

        _title.text = Localization.T(info.Force ? "发现新版本（建议更新）" : "发现新版本");
        _versionLine.text = Localization.T("当前 v{0} → 最新 v{1}", HextechPlugin.Version, info.Version)
                            + (string.IsNullOrWhiteSpace(info.Published) ? string.Empty : $" · {info.Published}");
        _notes.text = string.IsNullOrWhiteSpace(info.Notes)
            ? "（这次的更新公告是空的，具体改动见发布页。）"
            : info.Notes;
        _status.text = IdleStatusText;

        HideProgress();
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

        // 就绪态不可关：替换脚本已经在等游戏退出，这会儿关掉弹窗没有任何意义。
        if (_state == UpdateState.Ready)
        {
            return;
        }

        if (_downloading != null)
        {
            StopCoroutine(_downloading);
            _downloading = null;
        }

        _canvas.enabled = false;
        _visible = false;
        _info = null;
        _state = UpdateState.Idle;

        HextechWindowHost.Sync();
    }

    private void Update()
    {
        if (!_visible)
        {
            return;
        }

        // 就绪态：弹窗锁死，输入全忽略，等协程把游戏退出去（脚本接管重启）。
        if (_state == UpdateState.Ready)
        {
            _group.alpha = Mathf.MoveTowards(_group.alpha, 1f, Time.deltaTime * 8f);
            return;
        }

        if (!HextechWindowHost.IsOpen)
        {
            Hide();
            return;
        }

        _group.alpha = Mathf.MoveTowards(_group.alpha, 1f, Time.deltaTime * 8f);

        if (Input.GetKeyDown(KeyCode.Escape))
        {
            if (_state == UpdateState.Downloading)
            {
                CancelDownload();
            }
            else
            {
                Hide();
            }

            return;
        }

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

        switch (_hover)
        {
            case 0:
                IgnoreVersion();
                break;
            case 1:
                BeginUpdate();
                break;
            case 2:
                if (_state == UpdateState.Downloading)
                {
                    CancelDownload();
                }
                else
                {
                    Hide();
                }

                break;
        }
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

            // 下载中只留「关闭」当取消用；就绪后全部锁死；平时只有强制更新会让「忽略」不可点。
            var disabled = _state switch
            {
                UpdateState.Downloading => i != 2,
                UpdateState.Ready => true,
                _ => i == 0 && _info != null && _info.Force,
            };

            // 强制更新时把主按钮的文案说重一点：这版忽略不掉，只能更。
            var labelOverride = i == 1 && _info != null && _info.Force ? "必须更新" : null;

            if (disabled)
            {
                button.Background.color = UiFactory.PanelBackground;
                button.Label.color = UiFactory.TextMuted;
            }
            else
            {
                button.Background.color = i == _hover
                    ? Color.Lerp(button.BaseColor, Color.white, 0.25f)
                    : button.BaseColor;
                button.Label.color = i == 1 ? UiFactory.PanelBackground : UiFactory.TextPrimary;
            }

            button.Label.text = labelOverride ?? ButtonLabels[i];
        }
    }

    private void BeginUpdate()
    {
        if (_state != UpdateState.Idle || _info == null)
        {
            return;
        }

        _state = UpdateState.Downloading;
        SetProgress(0f);
        _status.text = Localization.T("正在下载新版本…");
        RefreshVisuals();

        _downloading = StartCoroutine(DownloadRoutine(_info));
    }

    private IEnumerator DownloadRoutine(UpdateInfo info)
    {
        byte[]? data = null;

        yield return UpdateFeed.DownloadDll(info, SetProgress, result => data = result);

        _downloading = null;

        // 下载途中弹窗被关掉（取消更新）了：别再往下走。
        if (_state != UpdateState.Downloading)
        {
            yield break;
        }

        if (data == null)
        {
            Fail("更新失败：下载没成功（断网或服务器没响应），游戏保持原样。");
            yield break;
        }

        _status.text = Localization.T("下载完成，正在准备替换…");

        if (!HextechSelfUpdater.StageAndArm(data, out var error))
        {
            Fail($"更新失败：{error}。可以关掉游戏后用安装器更新。");
            yield break;
        }

        _state = UpdateState.Ready;
        SetProgress(1f);
        _status.text = Localization.T("v{0} 已就绪 · 游戏即将退出并自动重启；若没有自动重开，手动启动即可（更新已完成）", info.Version);

        HextechPlugin.Log.LogInfo($"[更新] v{info.Version} 替换脚本已就绪，游戏即将退出并自动重启。");

        RefreshVisuals();

        yield return new WaitForSecondsRealtime(1.6f);
        Application.Quit();
    }

    private void CancelDownload()
    {
        if (_downloading != null)
        {
            StopCoroutine(_downloading);
            _downloading = null;
        }

        _state = UpdateState.Idle;
        HideProgress();
        _status.text = Localization.T("已取消更新，可以继续游戏");
        RefreshVisuals();

        HextechHud.Toast("已取消更新，可以继续游戏");
    }

    /// <summary>更新流程走到死路时回到初始态：按钮恢复、进度条藏起来、状态行说明原因
    /// （Toast 的画布层级在这层弹窗下面，会被盖住，所以失败原因必须写进状态行）。</summary>
    private void Fail(string message)
    {
        _state = UpdateState.Idle;
        HideProgress();
        _status.text = message;
        RefreshVisuals();

        HextechPlugin.Log.LogWarning($"[更新] {message}");
        HextechHud.Toast(message);
    }

    private void IgnoreVersion()
    {
        if (_info == null || _info.Force)
        {
            return;
        }

        ModConfig.IgnoredUpdateVersion.Value = _info.Version;
        HextechHud.Toast(Localization.T("已忽略 v{0} · 想更新的话清掉配置里的「更新.已忽略版本」", _info.Version));
        Hide();
    }

    private void SetProgress(float value)
    {
        if (_progressTrack == null)
        {
            return;
        }

        if (!_progressTrack.enabled)
        {
            _progressTrack.enabled = true;
            _progressFill.enabled = true;
        }

        var width = _progressTrack.rectTransform.rect.width;
        _progressFill.rectTransform.offsetMax = new Vector2(width * Mathf.Clamp01(value), 0f);
    }

    private void HideProgress()
    {
        if (_progressTrack != null && _progressTrack.enabled)
        {
            _progressTrack.enabled = false;
            _progressFill.enabled = false;
        }
    }
}
