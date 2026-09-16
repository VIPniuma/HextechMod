using System;
using System.Collections;
using System.Collections.Generic;
using Photon.Pun;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PeakModder.HextechMod;

/// <summary>
/// 全局驱动器：补齐 <see cref="HextechState"/>、处理技能与商店输入、
/// 在登岛和点燃阶段篝火时发放三选一，并在回到机场（一局结束）时清空局内成长。
/// </summary>
public sealed class HextechManager : MonoBehaviour
{
    /// <summary>回到这个场景就代表这一局结束了，局内海克斯全部清空。</summary>
    private const string AirportSceneName = "Airport";

    /// <summary>「登岛奖励」用的假阶段编号。真实阶段都是 0 及以上，不会撞。</summary>
    private const int RunStartKey = -1;

    /// <summary>场景加载完成后还要静置这么久，才允许弹面板。</summary>
    private const float SceneSettleSeconds = 1.5f;

    /// <summary>没等到「先离地再落地」时，在海滩上待够这么久也当作已登岛。</summary>
    private const float RunStartFallbackSeconds = 45f;

    /// <summary>中途加入补足的提示先攒一下再发，免得几条 toast 互相顶掉。</summary>
    private const float CatchUpToastDelaySeconds = 1f;

    /// <summary>问房主「本局已经跑了多久」的重试间隔与次数上限（房主没装 mod 就永远等不到答复）。</summary>
    private const float RunElapsedAskIntervalSeconds = 3f;
    private const int RunElapsedAskAttempts = 8;

    /// <summary>
    /// 欠着好几次三选一时，两次面板之间留一点间隔 ——
    /// 否则选完一次下一帧又弹一个，玩家来不及看清刚拿到的是什么。
    /// </summary>
    private const float ConsecutiveChoiceGapSeconds = 1.2f;

    /// <summary>面板键按住超过这么久算长按（秒）。</summary>
    private const float HudLongPressSeconds = 0.5f;

    public static HextechManager? Instance { get; private set; }

    /// <summary>本局到目前为止打了多久（和中途加入判定用的是同一套计时）。</summary>
    public float RunElapsedSeconds => _runElapsedSeconds;

    private readonly Queue<PendingChoice> _pendingChoices = new();
    private readonly List<string> _catchUpNotes = new();

    /// <summary>下一次机会的编号（一直加，永远不给 0 / 负数：面板拿 -1 当「没摆着」）。</summary>
    private int _nextChoiceKey;

    private float _pendingChoiceDelay;

    /// <summary>
    /// 玩家按 ESC 把三选一面板收起来了。收起来的那几次不会自己再弹，
    /// 要等玩家按「重新打开三选一」的键，或者下一次点燃篝火（那时会连同新的一次一起弹）。
    /// </summary>
    private bool _pendingChoiceHidden;
    private float _sceneSettledAt;
    private float _runStartBeachSince = -1f;
    private bool _runStartAirborne;
    private float _catchUpFlushAt = -1f;
    private float _runElapsedSeconds;
    private float _runElapsedAskAt;
    private int _runElapsedAsks;
    private bool _runElapsedAnswered;

    /// <summary>这次进机场有没有已经清过一局。用来兜住「插件是在机场场景之后才装起来的」那种启动。</summary>
    private bool _airportResetDone;

    /// <summary>上一帧「启用模组」是不是开着的，用来抓「刚刚被关掉」的那一刻。</summary>
    private bool _modEnabled = true;

    private void Awake()
    {
        Instance = this;
        _sceneSettledAt = Time.time;
        SceneManager.sceneLoaded += OnSceneLoaded;
        PhotonNetwork.NetworkingClient.EventReceived += SharedTokenPool.OnEvent;
    }

    /// <summary>
    /// 启动时兜底清一次。
    /// <para>
    /// BepInEx 是在第一个场景加载完之后才把插件装起来的，启动游戏直接落在机场的话，
    /// <see cref="OnSceneLoaded"/> 那一枪早就放过了 —— 不补这一次，玩家永远看不到
    /// 「机场里可以按 B 禁用本局不想看到的词条」这句提示，也就不知道机场能禁用。
    /// </para>
    /// </summary>
    private void Start()
    {
        if (!_airportResetDone && HextechScene.InAirport)
        {
            _airportResetDone = true;
            ResetRun();
        }
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        PhotonNetwork.NetworkingClient.EventReceived -= SharedTokenPool.OnEvent;

        if (Instance == this)
        {
            Instance = null;
        }
    }

    /// <summary>
    /// 账上欠着的一次三选一：标题 + 编号。
    /// <para>
    /// 编号是给面板认「还是同一次机会」用的：玩家按 ESC 先收起来、过后再按重开键时，
    /// 面板得把原来那几张牌（含每张「刷过没刷过」）原样摆回来，而不是重新抽一副 ——
    /// 不然「收起来再打开」就成了无限刷新。
    /// </para>
    /// </summary>
    private sealed class PendingChoice
    {
        public string Title = string.Empty;
        public int Key;
    }

    /// <summary>
    /// 排队一次三选一。延迟是为了避开阶段切换时的场景加载。
    ///
    /// 这里会顺手把「玩家自己收起来了」的状态解开：新的一次奖励到手，
    /// 就该连着他之前跳过的那些一起弹出来 —— 跳过几次就叠几次，一次都不会少。
    /// </summary>
    public void QueueChoice(string title, float delay)
    {
        _nextChoiceKey = _nextChoiceKey >= int.MaxValue ? 1 : _nextChoiceKey + 1;

        _pendingChoices.Enqueue(new PendingChoice { Title = title, Key = _nextChoiceKey });
        _pendingChoiceDelay = Mathf.Max(_pendingChoiceDelay, delay);
        _pendingChoiceHidden = false;
    }

    /// <summary>一局结束（回到机场）：把这一局拿到的东西全部清干净。</summary>
    public void ResetRun()
    {
        _pendingChoices.Clear();
        _pendingChoiceDelay = 0f;
        _pendingChoiceHidden = false;
        _runStartBeachSince = -1f;
        _runStartAirborne = false;
        _catchUpNotes.Clear();
        _catchUpFlushAt = -1f;
        _runElapsedSeconds = 0f;
        _runElapsedAskAt = 0f;
        _runElapsedAsks = 0;
        _runElapsedAnswered = false;
        LuggageLottery.Reset();

        // 共享代币池一回机场清空（见 SharedTokenPool）。
        SharedTokenPool.Reset();

        // 禁用名单是一局一清的：回到机场就全部解除，下一局重新选。
        // 所有客户端都在自己的机场场景里走到这里，所以解除本身不需要再同步一轮。
        HextechBans.Clear();

        var instances = HextechState.Instances;
        var hadAnything = false;

        for (var i = 0; i < instances.Count; i++)
        {
            var state = instances[i];

            if (state.Owned.Count > 0 || state.HasSkill)
            {
                hadAnything = true;
            }

            state.ResetForNewRun();
        }

        // 只是启动游戏时加载机场的话，什么都没拿过，不用标题那句提示；
        // 禁用按键那句还是要说，不然没人知道机场能禁词条。
        var message = hadAnything ? "本局结束 · 海克斯强化已清空" : string.Empty;
        var banKey = ModConfig.BanPanelKey.Value;

        if (banKey != KeyCode.None)
        {
            var hint = $"机场里可以按 {banKey} 禁用本局不想看到的词条";
            message = string.IsNullOrEmpty(message) ? hint : $"{message} · {hint}";
        }

        if (!string.IsNullOrEmpty(message))
        {
            HextechHud.Toast(message);
        }

        // 顺手向房主问一次当前名单：别人可能是在自己进房间之前就禁好了。
        // 商店单品调价同理 —— 房主改过的价格不能在别人那边显示成原价。
        var local = Character.localCharacter;

        if (local != null)
        {
            var state = HextechState.Get(local);

            state?.RequestBans();
            state?.RequestPrices();
        }
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        _sceneSettledAt = Time.time;

        if (string.Equals(scene.name, AirportSceneName, StringComparison.OrdinalIgnoreCase))
        {
            _airportResetDone = true;
            ResetRun();
        }
        else
        {
            _airportResetDone = false;
        }
    }

    private void Update()
    {
        UiFactory.RefreshFonts();

        // 界面都收起来了却还挂着窗口的话，玩家会卡在「视角和移动都动不了」，
        // 这里每帧兜底校正一次。
        HextechWindowHost.Sync();

        if (!ModConfig.Enabled.Value)
        {
            HandleDisabled();
            return;
        }

        if (!_modEnabled)
        {
            _modEnabled = true;
            HextechHud.Toast("海克斯模组已启用");
        }

        HextechState.EnsureForAll();
        FlushCatchUpNotes();

        // 上报的按键与本机角色无关：卡在主菜单、加载界面、甚至根本没进得去游戏时照样得能传，
        // 所以放在下面那段「没有角色就先 return」之前。Tick 只是每 30 秒记一次两份日志的长度，
        // 给「最近十分钟」当尺子（见 LogReporter）。
        LogReporter.Tick();
        HandleReportInput();

        var local = Character.localCharacter;

        if (local == null || local.data == null)
        {
            // 还没有本地角色（比如刚进机场、角色还没生成）时，技能 / 三选一 / 商店都无从谈起，
            // 但「禁用海克斯」和「海克斯设置」开的是界面，不依赖角色 ——
            // 这两个入口单独放行，否则玩家一进机场按 B 会毫无反应。
            var busy = IsAnyUiOpen();

            HandleBanInput(busy);
            HandleConfigInput(busy);
            return;
        }

        var state = HextechState.Get(local);

        if (state == null)
        {
            return;
        }

        SharedTokenPool.Tick(Time.deltaTime);
        state.Tick(Time.deltaTime);

        // 扛人兜底：原版不管「被扛的人醒了 / 死了」，放不下就一直是幽灵状态（详见 CarryGuard）。
        CarryGuard.Tick();

        // 实例数据晚一帧到位的拾取（手↔背包切换时的少数情况）在这一帧补结算。
        HextechAdvancedPatches.PickupSettlement.TickPending(Time.deltaTime);

        TrackRunElapsed(state);

        HandleRunStartChoice(state);
        HandleLateJoinCatchUp(state);
        HandlePendingChoice(state);
        HandlePendingReplacements(state);

        var uiOpen = IsAnyUiOpen();

        HandleShopInput(state, uiOpen);

        // 禁用面板的开关放在 uiOpen 判断之外：面板自己开着的时候 uiOpen 是 true，
        // 但它得能用同一个键把自己收起来。
        HandleBanInput(uiOpen);
        HandleConfigInput(uiOpen);

        if (!uiOpen)
        {
            HandleHudInput();
            HandleSkillInput(state);
            HandleChoiceInput(state);
        }
    }

    private static bool IsAnyUiOpen()
    {
        var panel = HextechPanel.Instance;

        if (panel != null && panel.IsOpen)
        {
            return true;
        }

        var shop = HextechShop.Instance;

        if (shop != null && shop.IsOpen)
        {
            return true;
        }

        // 禁用面板也是界面：开着的时候不该弹三选一，也不该让面板键跟着乱动。
        var ban = HextechBanPanel.Instance;

        if (ban != null && ban.IsOpen)
        {
            return true;
        }

        var config = HextechConfigPanel.Instance;

        if (config != null && config.IsOpen)
        {
            return true;
        }

        // 更新提示也占着屏幕，这时候不该再弹三选一、让玩家还能放技能。
        var update = HextechUpdatePrompt.Instance;

        if (update != null && update.IsOpen)
        {
            return true;
        }

        // 「日志已上传」的编号浮窗占着屏幕：这时候按别的键不该再叠一层界面上去
        // （它要玩家点「确定」才走，叠上去的话那两颗按钮会被盖住）。
        var report = HextechReportCodeHud.Instance;

        if (report != null && report.IsOpen)
        {
            return true;
        }

        // 抽奖动画在演的时候也算 UI 打开，免得动画里还能放技能、关商店。
        var roll = HextechRoll.Instance;
        return roll != null && roll.IsPlaying;
    }

    /// <summary>
    /// 「启用模组」关掉之后每帧走这里，做两件事。
    /// <para>
    /// 一是把已经挂在屏幕上的界面收掉 —— 开关关掉了、HUD 还留在屏幕上，看起来就像开关没生效
    /// （HUD 那两个 Canvas 由它们自己的 <c>Update</c> 每帧兜底隐藏，这里只收面板）。
    /// </para>
    /// <para>
    /// 二是继续放行配置面板的快捷键：那个面板是重新打开模组的入口之一
    /// （另一个是 ESC → 设置 里那行「海克斯模组设置」）。早先这里在 Enabled 判断之后直接
    /// <c>return</c>，关掉之后 F9 再也按不出面板，只能重启游戏改回来，等于把自己锁在外面。
    /// </para>
    /// </summary>
    private void HandleDisabled()
    {
        if (_modEnabled)
        {
            _modEnabled = false;
            // 「机械手」加长的是交互距离（不属于角色数值基准）：关掉模组时得把手缩回去，
            // 不然玩家以后一直是长手。
            HextechAdvancedPatches.MechanicalHand.Restore();

            CloseRunUi();
        }

        // 设置面板故意不跟着收：玩家多半就是在这个面板里点的「启用模组」，
        // 跟着收掉的话他刚点的那个开关会连面板一起消失，连改回来的地方都看不到。
        HandleConfigInput(IsAnyUiOpen());
    }

    /// <summary>
    /// 关掉模组的那一刻，把这一局开着 / 弹过的界面都收掉。
    /// 设置面板不在其中（见 <see cref="HandleDisabled"/>）。
    /// </summary>
    private static void CloseRunUi()
    {
        // 三选一走 Dismiss 而不是 Hide：那是「玩家自己先收起来了」，机会留在账上，
        // 重新打开模组后按「重新打开三选一」的键还能接着选。
        HextechPanel.Instance?.Dismiss();
        HextechShop.Instance?.Hide();
        HextechBanPanel.Instance?.Hide();
        HextechUpdatePrompt.Instance?.Hide();

        // 编号浮窗也得收：它是模态的，留着的话关掉模组之后玩家还杵在一个点不动的界面上。
        HextechReportCodeHud.Instance?.Hide();

        HextechWindowHost.Sync();
    }

    /// <summary>
    /// 刚进入海滩（第一个阶段）落地时送一次三选一，
    /// 免得玩家要一直等到第一个篝火才有海克斯。
    /// 每局只给一次，回机场重置后下一局会重新给。
    /// </summary>
    private void HandleRunStartChoice(HextechState state)
    {
        var local = state.Character;

        if (local == null || local.data == null
            || HextechScene.InAirport
            || !MapHandler.Exists
            || MapHandler.CurrentSegmentNumber != Segment.Beach)
        {
            return;
        }

        if (_runStartBeachSince < 0f)
        {
            _runStartBeachSince = Time.time;
        }

        if (!local.data.isGrounded)
        {
            // 离过地，说明接下来那次落地才是真正的登岛（开场是坐在飞机里的）。
            _runStartAirborne = true;
            return;
        }

        // 见过空降，或者在沙滩上待够兜底时间，才算登岛。
        if (!_runStartAirborne && Time.time - _runStartBeachSince < RunStartFallbackSeconds)
        {
            return;
        }

        if (!state.TryClaimCampfireReward(RunStartKey))
        {
            return;
        }

        QueueChoice("登岛强化 · 选择一项海克斯强化", 1f);
        HextechHud.Toast("已抵达海滩 · 送你一个海克斯强化");
    }

    /// <summary>
    /// 中途加入（或半路重连）的队友，按当前关卡进度自动补足次数。
    ///
    /// 正常一局里，玩家推到第 S 段时理论上应该拿过 S+1 次三选一
    /// （登岛奖励 1 次 + 1~S 每次篝火各 1 次），这里把这几次里还没领过的补上。
    /// 用的就是篝火那套「每个阶段只发一次」的领取标记，key 也完全一致，
    /// 所以正常从头打到这里的玩家、以及掉线重连回来的玩家都已经被标记过，不会重复发。
    /// </summary>
    private void HandleLateJoinCatchUp(HextechState state)
    {
        var local = Character.localCharacter;

        if (local == null || local.data == null || local != state.Character
            || HextechScene.InAirport || !MapHandler.Exists)
        {
            return;
        }

        // Void（小局）之类的超常规阶段按 Peak 封顶，免得异常进度刷出一长串面板。
        var reach = Mathf.Clamp((int)MapHandler.CurrentSegmentNumber, 0, (int)Segment.Peak);

        // 还在海滩：登岛奖励由 HandleRunStartChoice 照常发，不用在这里补。
        if (reach < (int)Segment.Tropics)
        {
            return;
        }

        var granted = 0;

        // RunStartKey 是登岛奖励，1..reach 是各次篝火推进到的阶段。
        for (var segment = RunStartKey; segment <= reach; segment++)
        {
            // 段号 0（海滩）从来没被发给谁，跳过它，保持和篝火口径一致。
            if (segment == 0 || !state.TryClaimCampfireReward(segment))
            {
                continue;
            }

            granted++;
            QueueChoice($"中途加入补足 · 第 {granted} 次海克斯强化", 2f);
        }

        if (granted > 0)
        {
            AddCatchUpNote($"{granted} 次海克斯强化");
        }
    }

    /// <summary>
    /// 记录「本局已经打了多久」。只在自己进局、活着的时候计时（和代币累积的条件一致），
    /// 中途加入时拿它和房主的时长做差，把错过的代币补上。
    /// </summary>
    private void TrackRunElapsed(HextechState state)
    {
        // 计时条件和代币累积保持一致：在机场、或者不在图里都不算「本局已进行时长」。
        if (HextechScene.InAirport || !MapHandler.Exists)
        {
            return;
        }

        _runElapsedSeconds += Time.deltaTime;

        // 房主不用问自己；已经拿到答复的也不再问了。
        if (_runElapsedAnswered || !PhotonNetwork.InRoom || PhotonNetwork.IsMasterClient)
        {
            return;
        }

        if (_runElapsedAsks >= RunElapsedAskAttempts || Time.time < _runElapsedAskAt)
        {
            return;
        }

        _runElapsedAsks++;
        _runElapsedAskAt = Time.time + RunElapsedAskIntervalSeconds;
        state.RequestRunElapsed();
    }

    /// <summary>
    /// 收到房主答复的「本局已进行时长」后，把差额折成代币补给刚进局的自己。
    /// 玩家自己的代币也在同步累积，这里减掉自己那部分，所以只会补「比房主少拿到的」，不会重复发。
    /// </summary>
    public void ApplyRunElapsedCatchUp(HextechState state, float hostElapsedSeconds)
    {
        if (_runElapsedAnswered)
        {
            return;
        }

        _runElapsedAnswered = true;

        var interval = ModConfig.TokenIntervalSeconds;

        if (interval <= 0f)
        {
            return;
        }

        var missedSeconds = hostElapsedSeconds - _runElapsedSeconds;

        if (missedSeconds < interval)
        {
            return;
        }

        // 补的和局内累积一个口径：**带小数**。以前按「错过的间隔数」取整，速率为 1.5/分 时
        // 那半枚就被抹掉了；现在错过多久就补多少（例：错过 100 秒 = 补 2.5 枚）。
        var tokens = missedSeconds / interval;

        state.AddTokens(tokens);
        AddCatchUpNote($"{HextechState.FormatTokens(tokens)} 枚商店代币");
    }

    /// <summary>把「中途加入补足了什么」攒起来一次性提示，免得几条 toast 互相顶掉。</summary>
    private void AddCatchUpNote(string note)
    {
        _catchUpNotes.Add(note);
        _catchUpFlushAt = Time.time + CatchUpToastDelaySeconds;
    }

    private void FlushCatchUpNotes()
    {
        if (_catchUpNotes.Count == 0 || Time.time < _catchUpFlushAt)
        {
            return;
        }

        HextechHud.Toast($"中途加入 · 补足 {string.Join("、", _catchUpNotes)}");
        _catchUpNotes.Clear();
        _catchUpFlushAt = -1f;
    }

    /// <summary>点燃篝火 / 登岛奖励：等场景稳定下来再弹三选一。</summary>
    private void HandlePendingChoice(HextechState state)
    {
        if (_pendingChoices.Count == 0 || _pendingChoiceHidden)
        {
            return;
        }

        if (_pendingChoiceDelay > 0f)
        {
            _pendingChoiceDelay -= Time.deltaTime;
            return;
        }

        if (Time.time - _sceneSettledAt < SceneSettleSeconds)
        {
            return;
        }

        if (Character.localCharacter != state.Character || IsAnyUiOpen())
        {
            return;
        }

        if (HextechPanel.Instance == null)
        {
            return;
        }

        OfferPendingChoice(state);
    }

    /// <summary>
    /// 按「重新打开三选一」的键：把还欠着的面板重新弹出来。
    ///
    /// 存在的意义就是 ESC —— 面板弹出来的时候可能正被怪追着打，
    /// 玩家想先收起来、安全了再选，这是完全合理的操作，不该付出少一次强化的代价。
    /// </summary>
    private void HandleChoiceInput(HextechState state)
    {
        var key = ModConfig.ChoiceKey.Value;

        if (key == KeyCode.None || !Input.GetKeyDown(key))
        {
            return;
        }

        if (_pendingChoices.Count == 0)
        {
            HextechHud.Toast("现在没有待选的海克斯强化");
            return;
        }

        // 玩家主动要选：解开「收起来了」的状态，这样选完一次之后，
        // 账上还欠着的那些会自己接着弹出来。
        _pendingChoiceHidden = false;

        OfferPendingChoice(state);
    }

    /// <summary>弹出账上还欠着的那次三选一。标题里带上总共欠了几次。</summary>
    private void OfferPendingChoice(HextechState state)
    {
        var choice = _pendingChoices.Peek();

        OfferChoices(
            state,
            choice.Key,
            BuildPendingTitle(),
            onPicked: _ => ConsumePendingChoice(),
            onDismiss: NotifyChoiceStillPending);
    }

    /// <summary>
    /// 队首这次三选一的标题。欠了不止一次时把总数写进标题 ——
    /// 玩家按 ESC 跳过的那些既没丢也不是被吞了，而是叠在这里等着一起选。
    /// </summary>
    private string BuildPendingTitle()
    {
        var title = _pendingChoices.Peek().Title;

        return _pendingChoices.Count <= 1
            ? title
            : $"{title}（含跳过的，共 {_pendingChoices.Count} 次）";
    }

    /// <summary>
    /// 玩家真的选了一项，这才把这次从账上划掉。
    /// 后面还有欠着的就隔一会儿再弹，别一帧糊一个在玩家脸上。
    /// </summary>
    private void ConsumePendingChoice()
    {
        if (_pendingChoices.Count > 0)
        {
            _pendingChoices.Dequeue();
        }

        if (_pendingChoices.Count > 0)
        {
            _pendingChoiceDelay = Mathf.Max(_pendingChoiceDelay, ConsecutiveChoiceGapSeconds);
        }
    }

    /// <summary>
    /// 玩家没选就把面板关掉了。这里什么也不消耗 —— 这次机会原样留在账上，
    /// 只负责告诉玩家面板去哪了、怎么叫回来。
    /// </summary>
    private void NotifyChoiceStillPending()
    {
        _pendingChoiceHidden = true;

        var key = ModConfig.ChoiceKey.Value;

        var reopen = key == KeyCode.None
            ? "面板上的提示里写着重新打开的按键"
            : $"按 {key} 重新打开";

        HextechHud.Toast($"海克斯还没选 · 已给你留着（还欠 {_pendingChoices.Count} 次）· {reopen}");
    }

    private void HandleShopInput(HextechState state, bool uiOpen)
    {
        var shop = HextechShop.Instance;

        if (shop == null || ModConfig.ShopKey.Value == KeyCode.None)
        {
            return;
        }

        // 关店期间如果商店还开着（比如开着的时候在 F9 里把它关了），立刻收掉，别留着。
        if (shop.IsOpen && !ModConfig.ShopEnabled.Value)
        {
            shop.Hide();
            return;
        }

        if (!Input.GetKeyDown(ModConfig.ShopKey.Value))
        {
            return;
        }

        // 商店被关掉了（海克斯设置里的「启用商店」）：开着就收、没开就提示，别让玩家以为能买。
        if (!ModConfig.ShopEnabled.Value)
        {
            if (shop.IsOpen)
            {
                shop.Hide();
            }
            else
            {
                HextechHud.Toast("商店已关闭（在海克斯设置里打开「启用商店」）");
            }

            return;
        }

        if (shop.IsOpen)
        {
            shop.Hide();
            return;
        }

        // 三选一面板开着的时候不让开商店，避免两个界面叠在一起。
        if (uiOpen)
        {
            return;
        }

        shop.Show(state);
    }

    /// <summary>
    /// 禁用键：在机场打开 / 收起「禁用海克斯」面板。
    /// <para>
    /// 只在机场能用 —— 禁用是给下一局准备的，进了图再改也没意义；
    /// 而且中途加入的玩家也不该在对局进行中突然把队友的词条池改掉。
    /// </para>
    /// </summary>
    private void HandleBanInput(bool uiOpen)
    {
        var key = ModConfig.BanPanelKey.Value;

        if (key == KeyCode.None || !Input.GetKeyDown(key))
        {
            return;
        }

        var panel = HextechBanPanel.Instance;

        if (panel == null)
        {
            return;
        }

        // 面板开着的时候按同一个键就是收起来。
        if (panel.IsOpen)
        {
            panel.Hide();
            return;
        }

        // 别的界面开着就先别抢，免得两层界面叠在一起。
        if (uiOpen)
        {
            return;
        }

        if (!HextechScene.InAirport)
        {
            HextechHud.Toast("只能回到机场再禁用词条");
            return;
        }

        panel.Show();
    }

    /// <summary>
    /// 配置面板的入口。原生设置页里那行「海克斯模组设置」已在 2026-09-15 移除，
    /// 现在只剩这一个快捷键入口。
    /// </summary>
    private void HandleConfigInput(bool uiOpen)
    {
        var key = ModConfig.ConfigPanelKey.Value;

        if (key == KeyCode.None || !Input.GetKeyDown(key))
        {
            return;
        }

        var panel = HextechConfigPanel.Instance;

        if (panel == null)
        {
            return;
        }

        if (panel.IsOpen)
        {
            panel.Hide();
            return;
        }

        if (uiOpen)
        {
            return;
        }

        panel.Show();
    }

    /// <summary>
    /// 日志上报键：按一下就把本机状态 + 两份日志的尾部（只取最近十分钟）打包传给作者服务器，换回一个取件编号。
    /// <para>
    /// 打包要读文件、gzip，同步做会卡一下，所以先把话说出去、下一帧再动手。
    /// 拿到编号之后弹一个浮窗（<see cref="HextechReportCodeHud"/>）停在屏幕上，等玩家自己点「确定」才关 ——
    /// 编号是玩家唯一要转达给作者的东西，一条十秒的 toast 太容易看漏、又只能靠手抄，
    /// 抄错一位作者就白跑一趟。
    /// </para>
    /// <para>
    /// 它不依赖本机角色（调用点放在角色判空之前），卡在主菜单、加载界面时也能传。
    /// </para>
    /// </summary>
    private void HandleReportInput()
    {
        var key = ModConfig.ReportKey.Value;

        if (key == KeyCode.None || !Input.GetKeyDown(key))
        {
            return;
        }

        if (LogReporter.Busy)
        {
            HextechHud.Toast("日志还在上传中，稍等一下");
            return;
        }

        StartCoroutine(UploadReport());
    }

    /// <summary>
    /// 上传过程每一步都要有回声（<c>progress</c> 回调）：按了键之后屏幕上一片安静，
    /// 玩家分不清是在传还是卡死了 —— 这曾经就是「一直在上传、半天没编号」的观感来源。
    /// </summary>
    private IEnumerator UploadReport()
    {
        yield return LogReporter.Upload(
            (code, error) =>
            {
                if (string.IsNullOrEmpty(code))
                {
                    HextechHud.Toast($"日志上传失败：{error}", 8f);
                    return;
                }

                // 浮窗自己会把编号写进剪贴板，并且停在屏幕上等玩家点「确定」（见 HextechReportCodeHud）。
                HextechReportCodeHud.Instance?.Show(code);
            },
            message => HextechHud.Toast(message, 3f));
    }

    /// <summary>
    /// 面板键（K）：<b>按住</b>查看「海克斯页面」本局持有总览（见 <see cref="HextechCodex"/>），松开即关。
    /// <para>
    /// 这一段每帧都会走到：图鉴<b>不算</b>在 <see cref="IsAnyUiOpen"/> 里（它只是「按住看一眼」，
    /// 不该挡住放技能等操作），所以页面开着时依然能检测到松手，不会卡住关不掉。
    /// </para>
    /// </summary>
    private void HandleHudInput()
    {
        var key = ModConfig.HudToggleKey.Value;

        if (key == KeyCode.None)
        {
            HextechCodex.Instance?.SetOpen(false);
            return;
        }

        HextechCodex.Instance?.SetOpen(Input.GetKey(key));
    }

    /// <summary>
    /// 界面打开期间不处理面板键，把长按计时清掉 ——
    private void HandleSkillInput(HextechState state)
    {
        if (ModConfig.SkillKey.Value != KeyCode.None && Input.GetKeyDown(ModConfig.SkillKey.Value))
        {
            if (!state.TryCastSkill() && !state.HasSkill)
            {
                HextechHud.Toast("还没有海克斯技能 · 点燃阶段篝火才能抽");
            }
        }

        if (ModConfig.CycleSkillKey.Value != KeyCode.None && Input.GetKeyDown(ModConfig.CycleSkillKey.Value))
        {
            state.CycleSkill();
        }

        // 「死而复生」单独给一个键：复活队友是临时决定的事，不该逼玩家先切技能。
        if (ModConfig.ResurrectKey.Value != KeyCode.None && Input.GetKeyDown(ModConfig.ResurrectKey.Value))
        {
            state.TryResurrect();
        }
    }

    /// <summary>
    /// 弹三选一。这里的池子永远只含正面词条 ——
    /// 负面海克斯只会从「开行李箱抽奖」里被随到。
    /// </summary>
    /// <param name="offerKey">
    /// 这次机会的编号（见 <see cref="PendingChoice.Key"/>）：ESC 收起来再打开时编号不变，
    /// 面板据此把原来那几张（含哪张刷过、刷成了什么）原样摆回来，不再重新抽一副 ——
    /// 否则「收起来再打开」就成了无限刷新。
    /// </param>
    /// <param name="onPicked">玩家真的选了某一项之后回调，调用方据此把这次从账上划掉。</param>
    /// <param name="onDismiss">玩家没选就把面板关掉时回调（按 ESC，见 <see cref="HextechPanel.Dismiss"/>）。</param>
    public void OfferChoices(
        HextechState state,
        int offerKey,
        string title = "海克斯强化 · 三选一",
        Action<HextechEntry>? onPicked = null,
        Action? onDismiss = null)
    {
        var panel = HextechPanel.Instance;

        if (panel == null || panel.IsOpen)
        {
            return;
        }

        // 三选一永远只弹给本机玩家：别的角色（队友、观战视角）身上的机会由他们自己弹。
        if (Character.localCharacter != state.Character)
        {
            return;
        }

        var random = new System.Random(unchecked(Environment.TickCount * 397 ^ state.Character.GetInstanceID()));

        // 抽牌交给面板按需调用：同一次机会重新打开时它不会调，那次摆着的牌原样拿回来。
        panel.Show(
            offerKey,
            () =>
            {
                // 「赌徒」（传说词条）：多摆一张牌 —— 三选一变四选一。
                // 多出来的那张照样是从正面池子里随机，不额外塞负面。
                var count = state.StackOfId(DefaultHextechs.GamblerId) > 0 ? 4 : 3;

                return HextechRegistry.Roll(state, count, random, allowNegative: false);
            },
            title,
            entry =>
            {
                // 统一授权入口：没到上限直接拿；到了（普通 4 / 技能 1）就转替换面板。
                AcquireOrQueueReplacement(state, entry);

                onPicked?.Invoke(entry);
            },
            (current, shown) =>
            {
                // 刷新时把当前摆着的几张（含被刷的那张）都排掉，免得刷出重复项。
                var exclude = new List<HextechEntry>(shown);

                if (!exclude.Contains(current))
                {
                    exclude.Add(current);
                }

                return HextechRegistry.RollOne(state, random, allowNegative: false, exclude);
            },
            onDismiss);
    }

    // ── 拿取上限与替换（2026-09-14）──────────────────────────────
    /// <summary>普通词条最多 4 条、技能词条最多 1 条（数值在 HextechState，可被服务器配置覆盖）。</summary>
    private readonly Queue<(HextechState State, HextechEntry Entry)> _pendingReplacements = new();

    /// <summary>替换面板的 offerKey 专用负数空间，与三选一的编号互不干扰。</summary>
    private int _nextReplacementKey = -1;

    /// <summary>替换面板被 ESC 关掉后，隔一小会儿再把同一张摆回来（替换是必选的，不能靠 ESC 赖掉）。</summary>
    private float _replacementReopenAt;

    /// <summary>
    /// 统一授权入口：同一条词条加层不占新名额，直接拿；
    /// 新词条撞上拿取上限（普通 4 / 技能 1）时不直接发，排进替换队列，
    /// 由 <see cref="HandlePendingReplacements"/> 在界面空闲时弹替换面板。
    /// 商店购买、行李箱抽奖、三选一全部走这里。
    /// </summary>
    public void AcquireOrQueueReplacement(HextechState state, HextechEntry entry)
    {
        var isSkill = entry.UnlocksSkill.HasValue;

        // 同一条词条加层不占新名额；没到上限也直接拿。
        if (state.StackOfId(entry.Id) > 0 || !state.IsAtCap(isSkill))
        {
            state.Acquire(entry);
            HextechHud.Toast($"获得强化：{entry.Title} · {entry.Summary(state.StackOf(entry))}");
            return;
        }

        _pendingReplacements.Enqueue((state, entry));
        HextechHud.Toast(isSkill
            ? "技能栏已满 · 请选择替换哪一个技能（或点新技能放弃）"
            : "海克斯已达上限 · 请选择替换哪一个（或点新词条放弃）");
    }

    private void HandlePendingReplacements(HextechState state)
    {
        if (_pendingReplacements.Count == 0 || Time.time < _replacementReopenAt)
        {
            return;
        }

        // 别的界面开着时等着；不是本机的替换（理论不会有）也先压着。
        if (IsAnyUiOpen() || Character.localCharacter != state.Character)
        {
            return;
        }

        if (HextechPanel.Instance == null)
        {
            return;
        }

        var (pendingState, entry) = _pendingReplacements.Dequeue();
        OfferReplacement(pendingState, entry);
    }

    /// <summary>
    /// 替换面板：摆出已持有的同类词条 + 最后一张「新词条」（点它 = 放弃）。
    /// 点旧词条 → 移除（连效果一起清）→ 发新词条。
    /// </summary>
    private void OfferReplacement(HextechState state, HextechEntry incoming)
    {
        var isSkill = incoming.UnlocksSkill.HasValue;
        var offerKey = _nextReplacementKey--;

        HextechPanel.Instance!.Show(
            offerKey,
            () =>
            {
                var cards = new List<HextechEntry>();

                for (var i = 0; i < state.Owned.Count; i++)
                {
                    var owned = state.Owned[i];

                    if (owned.UnlocksSkill.HasValue == isSkill)
                    {
                        cards.Add(owned);
                    }
                }

                cards.Add(incoming);
                return cards;
            },
            isSkill
                ? "技能栏已满 · 点旧技能替换它，点新技能放弃"
                : "海克斯已达上限 · 点旧词条替换它，点新词条放弃",
            picked =>
            {
                var replaced = !ReferenceEquals(picked, incoming);

                if (replaced)
                {
                    state.RemoveEntry(picked);
                }

                state.Acquire(incoming);

                HextechHud.Toast(replaced
                    ? $"替换成功：移除「{picked.Title}」，获得「{incoming.Title}」 · {incoming.Summary(state.StackOf(incoming))}"
                    : $"放弃了「{incoming.Title}」，现有词条原样保留");
            },
            (current, _) => current,
            () =>
            {
                // ESC 不能赖账：把这次替换插回队首，稍后重新弹出。
                _pendingReplacements.Enqueue((state, incoming));
                _replacementReopenAt = Time.time + 0.4f;
            });
    }
}
