using UnityEngine;

namespace PeakModder.HextechMod;

/// <summary>
/// 让海克斯自己的界面走游戏原生的窗口系统。
///
/// 游戏的鼠标可见性由 <c>CursorHandler.Update</c> 决定：只有当
/// <c>GUIManager.windowShowingCursor</c> 为真时才会解锁鼠标，而这个标记
/// 是 <c>GUIManager.UpdateWindowStatus</c> 每帧根据
/// <see cref="MenuWindow.AllActiveWindows"/> 里是否有窗口声明
/// <c>showCursorWhileOpen=true</c> 重新算出来的。
///
/// 所以自己直接写 <c>Cursor.visible</c> 会被下一帧覆盖。这里挂一个最小化的
/// <see cref="MenuWindow"/>，界面打开时把自己压进窗口列表即可：
/// 鼠标会自动解锁，玩家操作也会被 <c>windowBlockingInput</c> 一并屏蔽
/// （<c>CharacterInput.Sample(false)</c> 会把视角和移动输入一起清零）。
///
/// 反过来说，只要这个窗口残留在列表里，玩家就会一直「动不了视角、也走不动」。
/// 所以这里不按「开关了几次」记账，而是每帧对照各界面的真实显示状态收尾：
/// 界面都收起来了就一定把窗口关掉，见 <see cref="Sync"/>。
/// </summary>
[DefaultExecutionOrder(-100)]
internal sealed class HextechWindowHost : MenuWindow
{
    private static HextechWindowHost? _host;

    public override bool openOnStart => false;

    public override bool selectOnOpen => false;

    // 这两条必须是 false，别再改回 true：
    // 「海克斯设置」是从游戏原生的设置页里点开的，点下去的那一刻游戏正处在暂停 / 取消状态，
    // 窗口会在压进 MenuWindow.AllActiveWindows 的下一帧被这两条规则直接关掉。
    // Player.log 里能直接看到这个循环（opening window → 紧接着 HextechWindow closing.），
    // 玩家看到的就是「点了没反应 / 海克斯设置打不开」。
    // 按 ESC 收起界面的工作由界面自己负责：每个面板的 Update 都自己认 ESC，
    // 全部收起来之后 HextechManager 每帧兜底的 Sync 会把窗口关掉。
    public override bool closeOnPause => false;

    public override bool closeOnUICancel => false;

    public override bool blocksPlayerInput => true;

    public override bool showCursorWhileOpen => true;

    // 界面显隐由各自的 Canvas 控制，这里不需要窗口去动 GameObject。
    public override bool autoHideOnClose => false;

    /// <summary>
    /// 窗口当前是否真的开着。玩家按 ESC 让游戏把窗口关掉之后这里会变成 false，
    /// 界面据此判断「该收起来了」。
    /// </summary>
    public static bool IsOpen => _host != null && _host.isOpen;

    /// <summary>
    /// 窗口对象本身。打开界面前要把游戏原生的窗口（点入口时就是「设置」所在的菜单窗口）先关掉，
    /// 见 <c>HextechSettingsMenuEntry.CloseOtherWindows</c> —— 那里要靠它把自己排除掉。
    /// </summary>
    public static MenuWindow? Window => _host;

    public static void Create(Transform parent)
    {
        if (_host != null)
        {
            return;
        }

        var go = new GameObject("HextechWindow");
        go.transform.SetParent(parent, false);
        _host = go.AddComponent<HextechWindowHost>();
    }

    /// <summary>打开界面时调用：当帧就把窗口打开，鼠标和输入立刻交给界面。</summary>
    public static void Push()
    {
        if (_host == null || _host.isOpen)
        {
            return;
        }

        _host.Open();
    }

    /// <summary>
    /// 校正窗口状态：界面全部收起时把窗口关掉，还有界面开着（比如面板 + 商店）就留着。
    ///
    /// 界面关闭时调一次，<see cref="HextechManager"/> 每帧再兜底调一次 ——
    /// 以前这里是引用计数（Push +1 / Pop -1，减到 0 才关），只要有一次加减对不上，
    /// 计数就再也回不到 0，窗口会一直留在 <see cref="MenuWindow.AllActiveWindows"/> 里，
    /// 表现就是玩家选完海克斯之后「鼠标一直解锁、视角和移动都动不了」。
    /// 现在按界面的真实状态算，计数型残留不可能再发生，漏掉的路径也会在一帧内自愈。
    /// </summary>
    public static void Sync()
    {
        if (_host == null || !_host.isOpen || AnyUiOpen())
        {
            return;
        }

        _host.Close();
    }

    /// <summary>是否有海克斯界面正在显示（以界面自己的 IsOpen 为准）。</summary>
    private static bool AnyUiOpen()
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

        // 禁用面板也要算进来：它同样是 Push 窗口的界面，
        // 这里漏掉的话刚打开就会被判成「没有界面开着」，每帧兜底 Sync 立刻把窗口关掉。
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

        // 更新提示是独立弹窗，必须算进来：它的 Show 会 Push 窗口，
        // 而这里是每帧兜底 Sync 的判据 —— 漏掉的话提示框刚 Push 就被关掉，
        // 表现成「发现新版本」一闪而过，玩家根本来不及点。
        var prompt = HextechUpdatePrompt.Instance;

        if (prompt != null && prompt.IsOpen)
        {
            return true;
        }

        // 「日志已上传」的编号浮窗同理，而且它比提示框更要紧：
        // 玩家正要在这上面点「复制编号 / 确定」，漏掉就是刚弹出就被收回去，编号都看不全。
        var report = HextechReportCodeHud.Instance;
        return report != null && report.IsOpen;
    }
}
