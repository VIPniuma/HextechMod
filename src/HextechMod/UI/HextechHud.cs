using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PeakModder.HextechMod;

/// <summary>
/// HUD：能量条、当前技能、已获得词条，以及短暂的操作提示。
/// 已获得词条在右侧竖排，一条一行，名字后面直接跟着它实际加成的效果说明。
/// </summary>
public sealed class HextechHud : MonoBehaviour
{
    public static HextechHud? Instance { get; private set; }

    private const float PanelWidth = 560f;

    /// <summary>面板贴右边缘，这里是离右边界的距离（放左边会挡住准星附近的画面）。</summary>
    private const float PanelRightMargin = 36f;

    /// <summary>整块面板比原来往上挪了一点，但没顶到右上角那排原生 UI。</summary>
    private const float PanelY = -176f;

    /// <summary>收起后那颗「小角落提示」的尺寸：只够放三行 —— 开菜单 / 开商店 / 代币数。</summary>
    private const float MiniWidth = 236f;
    private const float MiniHeight = 104f;

    /// <summary>面板顶部那条「代币 + 商店」横条的高度、四周留白。</summary>
    private const float WalletHeight = 56f;
    private const float WalletMargin = 16f;

    /// <summary>面板顶部到词条列表顶部的距离（上方留给钱包条、标题、能量条和技能）。</summary>
    private const float ListTopOffset = 200f;
    private const float ListBottomPadding = 18f;
    private const float RowHeight = 64f;
    private const float MinRowHeight = 48f;
    private const float MaxPanelHeight = 840f;
    private const float NameLineHeight = 32f;
    private const float MinPanelHeight = 240f;

    /// <summary>一页最多显示几条词条。超过就分页，由 <see cref="TickPaging"/> 自动轮播。</summary>
    private const int RowsPerPage = 7;

    /// <summary>自动翻页的间隔（秒）。故意定得慢，免得玩家还没看完就被换走。</summary>
    private const float PageIntervalSeconds = 10f;

    private Canvas _canvas = null!;
    private Image _panel = null!;
    private RectTransform _ownedList = null!;
    private TextMeshProUGUI _toast = null!;

    /// <summary>「本局还没禁用海克斯」的常驻提醒，见 <see cref="RefreshBanPrompt"/>。</summary>
    private TextMeshProUGUI _banPrompt = null!;
    private string _banPromptState = string.Empty;

    private TextMeshProUGUI _skillLabel = null!;
    private TextMeshProUGUI _tokenLabel = null!;
    private TextMeshProUGUI _shopLabel = null!;
    private Image _shopButton = null!;
    private TextMeshProUGUI _emptyLabel = null!;
    private TextMeshProUGUI _headerLabel = null!;
    private TextMeshProUGUI _collapseLabel = null!;
    private Image _energyFill = null!;
    private Image _mini = null!;
    private TextMeshProUGUI _miniLabel = null!;

    // 代币现在是带一位小数的连续值，所以比的是「十分位」这个整数（TokensTenths），
    // 单个小数位真的变了才重写标签 —— -1 = 还没写过。
    private int _shownTokens = -1;
    private string _shownShopKey = string.Empty;
    private string _shownHeader = string.Empty;
    private string _shownMini = string.Empty;
    private string _shownCollapseKey = string.Empty;

    /// <summary>
    /// 玩家是否把右侧 HUD 收起了。收起时整块面板藏起来，只在右边留一颗小提示，
    /// 免得挡住画面；状态记在实例上，展开前一直生效。
    /// </summary>
    private bool _collapsed;

    /// <summary>
    /// 玩家是否长按把整个 HUD 藏起来了。这时连收起后那颗小提示和顶部提示条都不画，
    /// 屏幕上一点 mod 的痕迹都没有。
    /// </summary>
    private bool _hidden;

    /// <summary>藏起来之前是展开还是收起，再长按一次原样还原。</summary>
    private bool _collapsedBeforeHide;

    private readonly List<OwnedRow> _rows = new();
    private readonly StringBuilder _builder = new();
    private string _ownedSignature = string.Empty;
    private float _toastUntil;

    /// <summary>当前页的行高（每页行数固定，所以只在重建时算一次，翻页直接复用）。</summary>
    private float _rowHeight = RowHeight;

    /// <summary>当前显示第几页（从 0 开始），以及距离下一次自动翻页还剩多久。</summary>
    private int _pageIndex;
    private float _pageTimer;

    /// <summary>一条已获得词条的显示部件：品质圆点 + 名字 + 效果说明。</summary>
    private sealed class OwnedRow
    {
        public RectTransform Rect = null!;
        public Image Dot = null!;
        public TextMeshProUGUI Name = null!;
        public TextMeshProUGUI Effect = null!;
    }

    public static HextechHud Create(Transform parent)
    {
        var go = new GameObject("HextechHud");
        go.transform.SetParent(parent, false);
        var hud = go.AddComponent<HextechHud>();
        hud.Build();
        Instance = hud;
        return hud;
    }

    /// <summary>默认提示停留多久（秒）。</summary>
    private const float ToastSeconds = 3.5f;

    public static void Toast(string message)
    {
        Toast(message, ToastSeconds);
    }

    /// <summary>
    /// 需要玩家看清楚、甚至要抄下来的提示（比如日志上报回来的取件编号）走这个重载，
    /// 给一段更长的停留时间。
    /// </summary>
    public static void Toast(string message, float seconds)
    {
        if (Instance == null)
        {
            return;
        }

        Instance._toast.text = message;
        Instance._toast.color = new Color(UiFactory.Accent.r, UiFactory.Accent.g, UiFactory.Accent.b, 1f);
        Instance._toastUntil = Time.time + seconds;
    }

    /// <summary>HUD 是否处于收起状态（右侧只剩一颗小提示）。</summary>
    public static bool IsCollapsed => Instance != null && Instance._collapsed;

    /// <summary>展开 / 收起右侧 HUD。收起后画面不再被词条面板挡住。</summary>
    public static void ToggleCollapsed()
    {
        // 长按藏起来的时候什么都不显示，这时短按没有意义，等长按来恢复就行。
        if (Instance == null || Instance._hidden)
        {
            return;
        }

        Instance._collapsed = !Instance._collapsed;
        Instance.ApplyCollapsedLayout();

        if (Instance._collapsed)
        {
            var key = ModConfig.HudToggleKey.Value;
            var hint = key == KeyCode.None ? string.Empty : $" · 短按 {key} 展开，长按整个藏起来";
            Toast($"海克斯 HUD 已收起{hint}");
        }
        else
        {
            Toast("海克斯 HUD 已展开");
        }
    }

    /// <summary>HUD 是否被长按整个藏起来了。</summary>
    public static bool IsHidden => Instance != null && Instance._hidden;

    /// <summary>
    /// 长按面板键：把所有 UI 整个藏起来 / 恢复。
    /// <para>
    /// 和「收起」的区别：收起只藏掉右边那块大面板，角落那颗小提示还留着；
    /// 这里是连小提示和顶部提示条一起不画，屏幕上一点 mod 的东西都没有。
    /// 再长按一次回到藏起来之前的样子（之前是收起的就还是收起，之前展开的就还是展开）。
    /// </para>
    /// </summary>
    public static void ToggleHidden()
    {
        if (Instance == null)
        {
            return;
        }

        var hud = Instance;
        hud._hidden = !hud._hidden;

        if (hud._hidden)
        {
            hud._collapsedBeforeHide = hud._collapsed;

            // 连正在淡出的那条提示也一起抹掉，不然恢复之前它可能还挂在屏幕上。
            hud._toastUntil = 0f;
            hud._toast.text = string.Empty;
            hud._canvas.enabled = false;
            return;
        }

        hud._collapsed = hud._collapsedBeforeHide;
        hud._canvas.enabled = true;
        hud.ApplyCollapsedLayout();
        Toast("海克斯 UI 已恢复");
    }

    private void Build()
    {
        _canvas = UiFactory.CreateCanvas("HextechHudCanvas", 120);
        _canvas.transform.SetParent(transform, false);

        var root = UiFactory.Stretch(UiFactory.Node("Root", _canvas.transform));

        _toast = UiFactory.Label(root, string.Empty, 30f, TextAlignmentOptions.Center, UiFactory.Accent);
        _toast.rectTransform.anchorMin = new Vector2(0.5f, 1f);
        _toast.rectTransform.anchorMax = new Vector2(0.5f, 1f);
        _toast.rectTransform.pivot = new Vector2(0.5f, 1f);
        _toast.rectTransform.anchoredPosition = new Vector2(0f, -120f);
        _toast.rectTransform.sizeDelta = new Vector2(1200f, 46f);

        // 「本局还没禁用海克斯」的常驻提醒，挂在顶部提示条下面一行。
        _banPrompt = UiFactory.Label(root, string.Empty, 24f, TextAlignmentOptions.Center, UiFactory.Warning);
        _banPrompt.textWrappingMode = TextWrappingModes.NoWrap;
        _banPrompt.rectTransform.anchorMin = new Vector2(0.5f, 1f);
        _banPrompt.rectTransform.anchorMax = new Vector2(0.5f, 1f);
        _banPrompt.rectTransform.pivot = new Vector2(0.5f, 1f);
        _banPrompt.rectTransform.anchoredPosition = new Vector2(0f, -170f);
        _banPrompt.rectTransform.sizeDelta = new Vector2(1200f, 40f);

        _panel = UiFactory.Panel(root, UiFactory.PanelBackground);
        var panel = _panel;
        panel.rectTransform.anchorMin = new Vector2(1f, 1f);
        panel.rectTransform.anchorMax = new Vector2(1f, 1f);
        panel.rectTransform.pivot = new Vector2(1f, 1f);
        panel.rectTransform.anchoredPosition = new Vector2(-PanelRightMargin, PanelY);
        panel.rectTransform.sizeDelta = new Vector2(PanelWidth, MinPanelHeight);

        // 钱包条：代币和「按什么键开商店」单独拎出来做成一条按钮，
        // 之前只是在角落写一句小字，别人根本不知道商店怎么开。
        var wallet = UiFactory.Rounded(panel.transform, UiFactory.PanelBackgroundLight, 18);
        wallet.rectTransform.anchorMin = new Vector2(0f, 1f);
        wallet.rectTransform.anchorMax = new Vector2(1f, 1f);
        wallet.rectTransform.offsetMin = new Vector2(WalletMargin, -(WalletMargin + WalletHeight));
        wallet.rectTransform.offsetMax = new Vector2(-WalletMargin, -WalletMargin);

        _tokenLabel = UiFactory.Label(wallet.transform, string.Empty, 30f, TextAlignmentOptions.Left, UiFactory.Warning);
        _tokenLabel.textWrappingMode = TextWrappingModes.NoWrap;
        _tokenLabel.rectTransform.anchorMin = new Vector2(0f, 0f);
        _tokenLabel.rectTransform.anchorMax = new Vector2(0f, 1f);
        _tokenLabel.rectTransform.offsetMin = new Vector2(20f, 0f);
        _tokenLabel.rectTransform.offsetMax = new Vector2(240f, 0f);

        _shopButton = UiFactory.Rounded(wallet.transform, UiFactory.PanelHighlight, 14);
        _shopButton.rectTransform.anchorMin = new Vector2(1f, 0.5f);
        _shopButton.rectTransform.anchorMax = new Vector2(1f, 0.5f);
        _shopButton.rectTransform.pivot = new Vector2(1f, 0.5f);
        _shopButton.rectTransform.anchoredPosition = new Vector2(-14f, 0f);
        _shopButton.rectTransform.sizeDelta = new Vector2(272f, 40f);

        _shopLabel = UiFactory.Label(_shopButton.transform, string.Empty, 20f, TextAlignmentOptions.Center, UiFactory.Warning);
        _shopLabel.textWrappingMode = TextWrappingModes.NoWrap;
        UiFactory.Stretch(_shopLabel.rectTransform);

        _headerLabel = UiFactory.Label(panel.transform, "海克斯 HEX", 22f, TextAlignmentOptions.Left, UiFactory.TextMuted);
        _headerLabel.textWrappingMode = TextWrappingModes.NoWrap;
        _headerLabel.rectTransform.anchorMin = new Vector2(0f, 1f);
        _headerLabel.rectTransform.anchorMax = new Vector2(0f, 1f);
        _headerLabel.rectTransform.pivot = new Vector2(0f, 1f);
        _headerLabel.rectTransform.anchoredPosition = new Vector2(24f, -84f);
        _headerLabel.rectTransform.sizeDelta = new Vector2(320f, 38f);

        // 面板展开时也得告诉玩家怎么把它收起来，不然「能收起」这件事只有看过说明的人知道。
        _collapseLabel = UiFactory.Label(panel.transform, string.Empty, 20f, TextAlignmentOptions.Right, UiFactory.TextMuted);
        _collapseLabel.textWrappingMode = TextWrappingModes.NoWrap;
        _collapseLabel.rectTransform.anchorMin = new Vector2(1f, 1f);
        _collapseLabel.rectTransform.anchorMax = new Vector2(1f, 1f);
        _collapseLabel.rectTransform.pivot = new Vector2(1f, 1f);
        _collapseLabel.rectTransform.anchoredPosition = new Vector2(-24f, -84f);
        _collapseLabel.rectTransform.sizeDelta = new Vector2(260f, 38f);

        var barBackground = UiFactory.Panel(panel.transform, new Color(0f, 0f, 0f, 0.55f));
        barBackground.rectTransform.anchorMin = new Vector2(0f, 1f);
        barBackground.rectTransform.anchorMax = new Vector2(1f, 1f);
        barBackground.rectTransform.pivot = new Vector2(0.5f, 1f);
        barBackground.rectTransform.offsetMin = new Vector2(24f, -152f);
        barBackground.rectTransform.offsetMax = new Vector2(-24f, -124f);

        var fillRect = UiFactory.Node("EnergyFill", barBackground.transform);
        UiFactory.Stretch(fillRect, 2f, 2f, 2f, 2f);
        _energyFill = fillRect.gameObject.AddComponent<Image>();
        _energyFill.sprite = UiFactory.RoundedSprite();
        _energyFill.type = Image.Type.Filled;
        _energyFill.fillMethod = Image.FillMethod.Horizontal;
        _energyFill.fillOrigin = 0;
        _energyFill.color = UiFactory.Accent;

        _skillLabel = UiFactory.Label(panel.transform, string.Empty, 21f, TextAlignmentOptions.Left, UiFactory.TextPrimary);
        _skillLabel.rectTransform.anchorMin = new Vector2(0f, 1f);
        _skillLabel.rectTransform.anchorMax = new Vector2(1f, 1f);
        _skillLabel.rectTransform.pivot = new Vector2(0.5f, 1f);
        _skillLabel.rectTransform.offsetMin = new Vector2(24f, -188f);
        _skillLabel.rectTransform.offsetMax = new Vector2(-24f, -158f);

        _ownedList = UiFactory.Node("OwnedList", panel.transform);
        _ownedList.anchorMin = new Vector2(0f, 0f);
        _ownedList.anchorMax = new Vector2(1f, 1f);
        _ownedList.pivot = new Vector2(0.5f, 1f);
        _ownedList.offsetMin = new Vector2(24f, ListBottomPadding);
        _ownedList.offsetMax = new Vector2(-24f, -ListTopOffset);

        _emptyLabel = UiFactory.Label(_ownedList, "本局还没有强化 · 登岛、点燃篝火、开行李箱都能抽到", 19f, TextAlignmentOptions.TopLeft, UiFactory.TextMuted);
        _emptyLabel.rectTransform.anchorMin = new Vector2(0f, 1f);
        _emptyLabel.rectTransform.anchorMax = new Vector2(1f, 1f);
        _emptyLabel.rectTransform.pivot = new Vector2(0.5f, 1f);
        _emptyLabel.rectTransform.offsetMin = new Vector2(0f, -70f);
        _emptyLabel.rectTransform.offsetMax = new Vector2(0f, 0f);

        // 收起状态的小角落提示：只报「按什么键开菜单 / 开商店」和代币数，
        // 不列词条，占的地方很小，不会再挡住右边的画面。
        _mini = UiFactory.Rounded(root, UiFactory.PanelBackground, 16);
        _mini.rectTransform.anchorMin = new Vector2(1f, 1f);
        _mini.rectTransform.anchorMax = new Vector2(1f, 1f);
        _mini.rectTransform.pivot = new Vector2(1f, 1f);
        _mini.rectTransform.anchoredPosition = new Vector2(-PanelRightMargin, PanelY);
        _mini.rectTransform.sizeDelta = new Vector2(MiniWidth, MiniHeight);

        var miniDot = UiFactory.Circle(_mini.transform, UiFactory.Accent);
        miniDot.rectTransform.anchorMin = new Vector2(0f, 0.5f);
        miniDot.rectTransform.anchorMax = new Vector2(0f, 0.5f);
        miniDot.rectTransform.pivot = new Vector2(0f, 0.5f);
        miniDot.rectTransform.anchoredPosition = new Vector2(14f, 0f);
        miniDot.rectTransform.sizeDelta = new Vector2(12f, 12f);

        _miniLabel = UiFactory.Label(_mini.transform, string.Empty, 19f, TextAlignmentOptions.Left, UiFactory.TextPrimary);
        _miniLabel.textWrappingMode = TextWrappingModes.NoWrap;
        _miniLabel.rectTransform.anchorMin = new Vector2(0f, 0f);
        _miniLabel.rectTransform.anchorMax = new Vector2(1f, 1f);
        _miniLabel.rectTransform.offsetMin = new Vector2(36f, 12f);
        _miniLabel.rectTransform.offsetMax = new Vector2(-12f, -12f);
        _mini.gameObject.SetActive(false);

        _canvas.enabled = false;
    }

    private void Update()
    {
        // 「启用模组」关掉之后 HUD 必须跟着消失：开关关了、面板还挂在屏幕上，
        // 看起来就像开关没生效。这里每帧兜底，不依赖谁去通知。
        if (!ModConfig.Enabled.Value)
        {
            if (_canvas.enabled)
            {
                _canvas.enabled = false;
            }

            return;
        }

        if (_toastUntil > 0f)
        {
            var remaining = _toastUntil - Time.time;

            if (remaining <= 0f)
            {
                _toastUntil = 0f;
                _toast.text = string.Empty;
            }
            else
            {
                var color = _toast.color;
                color.a = Mathf.Clamp01(remaining);
                _toast.color = color;
            }
        }

        var state = HextechState.Get(Character.localCharacter);

        if (state == null)
        {
            if (_canvas.enabled)
            {
                _canvas.enabled = false;
            }

            return;
        }

        // 长按藏起来时整块 Canvas 都关掉：大面板、角落小提示、顶部提示条一起不画。
        if (_hidden)
        {
            if (_canvas.enabled)
            {
                _canvas.enabled = false;
            }

            return;
        }

        if (!_canvas.enabled)
        {
            _canvas.enabled = true;
            ApplyCollapsedLayout();
        }

        _energyFill.fillAmount = state.EnergyRatio;
        RefreshWallet(state);
        RefreshMini(state);

        // 放在收起判断之前：收起状态下这条提醒同样要能看到。
        RefreshBanPrompt();

        if (_collapsed)
        {
            // 收起时完整面板整个藏着，词条列表 / 技能行不用重建，分页计时也停下来。
            return;
        }

        var skill = state.CurrentSkill;

        // 「死而复生」是独立按键，不占技能位，所以单独挂在技能提示后面。
        var revive = state.ReviveCharges > 0
            ? $" · {ModConfig.ResurrectKey.Value} 死而复生×{state.ReviveCharges}"
            : string.Empty;

        if (skill.HasValue)
        {
            var definition = SkillRegistry.Get(skill.Value);
            var cooldown = state.CooldownRemaining > 0.05f ? $" · 冷却{state.CooldownRemaining:0.0}s" : string.Empty;
            _skillLabel.color = UiFactory.TextPrimary;
            _skillLabel.text =
                $"{ModConfig.SkillKey.Value} {definition.Title} ({definition.EnergyCost:0}能量){cooldown} · {ModConfig.CycleSkillKey.Value}切换{revive}";
        }
        else if (state.ReviveCharges > 0)
        {
            _skillLabel.color = UiFactory.TextPrimary;
            _skillLabel.text =
                $"{ModConfig.ResurrectKey.Value} 死而复生×{state.ReviveCharges} · 把一名死掉的队友拉回来";
        }
        else
        {
            _skillLabel.color = UiFactory.TextMuted;
            _skillLabel.text = "尚无技能 · 点燃阶段篝火或开箱抽取";
        }

        RefreshOwned(state);
        TickPaging(state);
    }

    /// <summary>
    /// 钱包条：左边代币数，右边「按 X 开商店」的按钮。
    /// 只在数字 / 按键真的变了才写字符串，省掉每帧的拼接开销。
    /// </summary>
    private void RefreshWallet(HextechState state)
    {
        var tenths = state.TokensTenths;

        if (_shownTokens != tenths)
        {
            _shownTokens = tenths;
            _tokenLabel.text = $"◈ {HextechState.FormatTokens(state.Tokens)} 代币";
            _tokenLabel.color = state.Tokens > 0f ? UiFactory.Warning : UiFactory.TextMuted;
            _shopButton.color = state.Tokens > 0f ? UiFactory.PanelHighlight : UiFactory.PanelBackgroundLight;
        }

        var key = ModConfig.ShopKey.Value.ToString();

        if (_shownShopKey != key)
        {
            _shownShopKey = key;
            _shopLabel.text = $"按 {key} 打开商店";
        }

        // 同一帧顺手把「按 X 收起面板」也对一遍，按键改了立刻跟上。
        var toggleKey = ModConfig.HudToggleKey.Value.ToString();

        if (_shownCollapseKey != toggleKey)
        {
            _shownCollapseKey = toggleKey;
            _collapseLabel.text = ModConfig.HudToggleKey.Value == KeyCode.None
                ? string.Empty
                : $"按 {toggleKey} 收起面板";
        }
    }

    /// <summary>
    /// 「本局还没有禁用海克斯」的顶部提醒。
    /// <para>
    /// 只在机场、而且只在自己这一票还没投的时候显示 —— 出了机场就禁不了，
    /// 已经禁过的人也不必再看到。按「显示与否 + 当前按键」算成一个签名，变了才重写字符串。
    /// </para>
    /// </summary>
    private void RefreshBanPrompt()
    {
        var show = HextechScene.InAirport && HextechBans.Mine == null;
        var signature = show ? ModConfig.BanPanelKey.Value.ToString() : string.Empty;

        if (_banPromptState == signature)
        {
            return;
        }

        _banPromptState = signature;

        if (!show)
        {
            _banPrompt.text = string.Empty;
            return;
        }

        _banPrompt.text = ModConfig.BanPanelKey.Value == KeyCode.None
            ? "本局还没有禁用海克斯 · 在机场打开禁用面板可以禁掉一个"
            : $"本局还没有禁用海克斯 · 按 {ModConfig.BanPanelKey.Value} 打开禁用面板";
    }

    /// <summary>按收起状态切换「完整面板」与「小角落提示」的显隐。</summary>
    private void ApplyCollapsedLayout()
    {
        if (_panel.gameObject.activeSelf == _collapsed)
        {
            _panel.gameObject.SetActive(!_collapsed);
        }

        if (_mini.gameObject.activeSelf != _collapsed)
        {
            _mini.gameObject.SetActive(_collapsed);
        }
    }

    /// <summary>
    /// 收起后小角落提示的三行：开菜单按键 / 开商店按键 / 代币数。
    /// 按键和代币数都没变就不重写字符串。
    /// </summary>
    private void RefreshMini(HextechState state)
    {
        var menuKey = ModConfig.HudToggleKey.Value == KeyCode.None
            ? "打开菜单"
            : $"{ModConfig.HudToggleKey.Value} 打开菜单";

        var shopKey = ModConfig.ShopKey.Value == KeyCode.None
            ? "打开商店"
            : $"{ModConfig.ShopKey.Value} 打开商店";

        var signature = $"{menuKey}|{shopKey}|{state.TokensTenths}";

        if (signature == _shownMini)
        {
            return;
        }

        _shownMini = signature;
        var tokenHex = ColorUtility.ToHtmlStringRGB(state.Tokens > 0f ? UiFactory.Warning : UiFactory.TextMuted);
        _miniLabel.text =
            $"{menuKey}\n{shopKey}\n<color=#{tokenHex}>◈ {HextechState.FormatTokens(state.Tokens)} 代币</color>";
    }

    /// <summary>
    /// 词条列表只在「获得/叠加了新的词条」时重建，避免每帧重排文字。
    /// 用 id+层数拼一个签名来判定有没有变化。
    /// </summary>
    private void RefreshOwned(HextechState state)
    {
        _builder.Clear();

        for (var i = 0; i < state.Owned.Count; i++)
        {
            var entry = state.Owned[i];
            _builder.Append(entry.Id).Append(':').Append(state.StackOf(entry)).Append('|');
        }

        var signature = _builder.ToString();

        if (signature == _ownedSignature)
        {
            return;
        }

        _ownedSignature = signature;
        RebuildRows(state);
    }

    private void RebuildRows(HextechState state)
    {
        var count = state.Owned.Count;

        // 行高按「一页最多几条」算，而不是按词条总数算：
        // 这样面板高度在分页时保持稳定，翻页不会让整块面板忽大忽小。
        var rowsOnPage = Mathf.Clamp(count, 1, RowsPerPage);
        _rowHeight = ComputeRowHeight(rowsOnPage);

        EnsureRows(count);
        _emptyLabel.gameObject.SetActive(count == 0);

        // 新词条进来时从第一页看起，免得刚抽到的东西落在别的页上找不到。
        _pageIndex = 0;
        _pageTimer = 0f;

        ApplyPageLayout(state);

        var height = Mathf.Max(MinPanelHeight, ListTopOffset + (rowsOnPage * _rowHeight) + ListBottomPadding + 8f);
        _panel.rectTransform.sizeDelta = new Vector2(PanelWidth, height);
    }

    /// <summary>一页最多 <see cref="RowsPerPage"/> 条，多出来的排到后面的页。</summary>
    private static int PageCount(int count)
    {
        return Mathf.Max(1, (count + RowsPerPage - 1) / RowsPerPage);
    }

    /// <summary>
    /// 只按「当前页」摆放行：不属于这一页的行直接隐藏。
    /// 文字只在 <see cref="FillRow"/> 里写，翻页时对本页那几条重写一遍，开销可以忽略。
    /// </summary>
    private void ApplyPageLayout(HextechState state)
    {
        var count = state.Owned.Count;
        var pageCount = PageCount(count);

        _pageIndex %= pageCount;

        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];

            if (i >= count || (i / RowsPerPage) != _pageIndex)
            {
                row.Rect.gameObject.SetActive(false);
                continue;
            }

            var slot = i % RowsPerPage;
            row.Rect.gameObject.SetActive(true);
            row.Rect.offsetMin = new Vector2(0f, -(slot * _rowHeight) - _rowHeight);
            row.Rect.offsetMax = new Vector2(0f, -(slot * _rowHeight));

            FillRow(row, state.Owned[i], state.StackOf(state.Owned[i]));
        }

        // 超过一页时在标题后面带上「第几页 / 共几页」，不然玩家不知道列表在自动翻。
        var header = pageCount <= 1 ? "海克斯 HEX" : $"海克斯 HEX · {_pageIndex + 1}/{pageCount}";

        if (_shownHeader != header)
        {
            _shownHeader = header;
            _headerLabel.text = header;
        }
    }

    /// <summary>把一条词条的名字（品质色 + 加粗）和当前层数的效果说明填进同一行。</summary>
    private void FillRow(OwnedRow row, HextechEntry entry, int stacks)
    {
        var accent = entry.Negative ? UiFactory.Danger : UiFactory.QualityColor(entry.Quality);
        row.Dot.color = accent;

        var compact = _rowHeight < 56f;
        var nameSize = compact ? 19f : 22f;
        var effectSize = compact ? 14f : 16f;
        var accentHex = ColorUtility.ToHtmlStringRGB(accent);

        // 名字和效果说明合并到同一个 Label 里分两行显示：
        // 之前分两个 Label 时，名字那一行在运行时渲染不出来。
        var titleLine = stacks > 1
            ? $"<size={nameSize}><b><color=#{accentHex}>{UiFactory.QualityGlyph(entry.Quality)} {entry.Title}</color></b> <size=75%><color=#6BC7FF>×{stacks}</color></size></size>"
            : $"<size={nameSize}><b><color=#{accentHex}>{UiFactory.QualityGlyph(entry.Quality)} {entry.Title}</color></b></size>";

        row.Effect.color = entry.Negative ? new Color(0.95f, 0.62f, 0.64f, 1f) : UiFactory.TextMuted;
        row.Effect.text = $"{titleLine}\n<size={effectSize}>{entry.Summary(stacks)}</size>";
    }

    /// <summary>一页装不下时，每 <see cref="PageIntervalSeconds"/> 秒自动翻到下一页（循环）。</summary>
    private void TickPaging(HextechState state)
    {
        var pageCount = PageCount(state.Owned.Count);

        if (pageCount <= 1)
        {
            _pageTimer = 0f;
            return;
        }

        _pageTimer += Time.deltaTime;

        if (_pageTimer < PageIntervalSeconds)
        {
            return;
        }

        _pageTimer = 0f;
        _pageIndex = (_pageIndex + 1) % pageCount;
        ApplyPageLayout(state);
    }

    /// <summary>词条太多时压缩行高，尽量让整块列表留在屏幕里。</summary>
    private static float ComputeRowHeight(int count)
    {
        if (count <= 0)
        {
            return RowHeight;
        }

        var available = MaxPanelHeight - ListTopOffset - ListBottomPadding - 8f;

        if (count * RowHeight <= available)
        {
            return RowHeight;
        }

        return Mathf.Max(MinRowHeight, available / count);
    }

    private void EnsureRows(int count)
    {
        while (_rows.Count < count)
        {
            _rows.Add(CreateRow());
        }
    }

    private OwnedRow CreateRow()
    {
        var rect = UiFactory.Node("OwnedRow", _ownedList);
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);

        var dot = UiFactory.Circle(rect, UiFactory.TextMuted);
        dot.rectTransform.anchorMin = new Vector2(0f, 1f);
        dot.rectTransform.anchorMax = new Vector2(0f, 1f);
        dot.rectTransform.pivot = new Vector2(0f, 1f);
        dot.rectTransform.anchoredPosition = new Vector2(4f, -25f);
        dot.rectTransform.sizeDelta = new Vector2(14f, 14f);

        var name = UiFactory.Label(rect, string.Empty, 22f, TextAlignmentOptions.Left, UiFactory.TextPrimary);
        name.textWrappingMode = TextWrappingModes.NoWrap;
        name.overflowMode = TextOverflowModes.Ellipsis;
        name.rectTransform.anchorMin = new Vector2(0f, 1f);
        name.rectTransform.anchorMax = new Vector2(1f, 1f);
        name.rectTransform.pivot = new Vector2(0.5f, 1f);
        name.rectTransform.offsetMin = new Vector2(26f, -NameLineHeight);
        name.rectTransform.offsetMax = new Vector2(0f, -4f);

        // 之前把名字和效果分两个 Label，结果名字那一行死活不渲染；
        // 现在把两行合并到同一个 Label 里，省掉一个出问题的渲染层级。
        name.gameObject.SetActive(false);

        var effect = UiFactory.Label(rect, string.Empty, 18f, TextAlignmentOptions.TopLeft, UiFactory.TextMuted);
        effect.textWrappingMode = TextWrappingModes.NoWrap;
        effect.overflowMode = TextOverflowModes.Overflow;
        effect.rectTransform.anchorMin = new Vector2(0f, 0f);
        effect.rectTransform.anchorMax = new Vector2(1f, 1f);
        effect.rectTransform.pivot = new Vector2(0.5f, 1f);
        effect.rectTransform.offsetMin = new Vector2(26f, 4f);
        effect.rectTransform.offsetMax = new Vector2(0f, -4f);

        return new OwnedRow
        {
            Rect = rect,
            Dot = dot,
            Name = name,
            Effect = effect,
        };
    }
}
