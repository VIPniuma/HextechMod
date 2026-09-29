using System;
using System.Collections.Generic;
using System.IO;

namespace PeakModder.HextechInstaller;

internal static class InstallerLocalization
{
    public const string Chinese = "简体中文";
    public const string English = "English";

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HextechModInstaller",
        "language.txt");

    public static string Current { get; private set; } = Load();
    public static bool IsEnglish => string.Equals(Current, English, StringComparison.Ordinal);

    private static readonly Dictionary<string, string> EnglishText = new(StringComparer.Ordinal)
    {
        ["小王同学模组安装器"] = "Xiao Wang's Mod Installer",
        ["小王同学"] = "Xiao Wang", ["模组安装器"] = "Mod Installer",
        ["主页"] = "Home", ["前置框架"] = "Framework", ["模组仓库"] = "Mod Repository",
        ["设置"] = "Settings", ["语言"] = "Language",
        ["选择安装器界面使用的语言。更改后立即生效。"] = "Choose the installer language. Changes apply immediately.",
        ["游戏目录"] = "Game folder", ["正在查找…"] = "Searching...", ["浏览…"] = "Browse...",
        ["刷新仓库"] = "Refresh", ["就绪"] = "Ready", ["正在加载，请稍候…"] = "Loading, please wait...",
        ["模组作者"] = "Mod author", ["复制 QQ"] = "Copy QQ", ["三步开玩"] = "Three steps to play",
        ["装前置"] = "Install framework", ["挑模组"] = "Choose mods", ["进游戏"] = "Launch game",
        ["「前置框架」页一键安装。"] = "Install it from the Framework page.",
        ["「模组仓库」页点「安装」。"] = "Click Install on the Mod Repository page.",
        ["启动 PEAK。"] = "Launch PEAK.", ["BepInEx 框架"] = "BepInEx Framework",
        ["所有模组的运行前提。没有它，任何模组都不会被游戏加载。"] = "Required by every mod. PEAK cannot load mods without it.",
        ["正在自动检测 PEAK 的安装位置…"] = "Detecting the PEAK installation folder...",
        ["模组的安装 / 卸载在「模组仓库」页。"] = "Install or remove mods from the Mod Repository page.",
        ["卸载框架"] = "Remove Framework", ["安装 BepInEx 框架"] = "Install BepInEx Framework",
        ["正在从服务器读取仓库…"] = "Loading the repository from the server...",
        ["正在检查本机状态…"] = "Checking local status...", ["安装"] = "Install", ["卸载"] = "Uninstall",
        ["当前状态"] = "Current status", ["未安装，安装时自动下载"] = "Not installed; downloaded automatically",
        ["已安装"] = "Installed", ["已是最新"] = "Up to date", ["卸载完成。"] = "Uninstall complete.",
        ["需要先指定游戏目录"] = "Select the game folder first", ["游戏目录已就绪。"] = "Game folder is ready.",
        ["框架已就绪。模组去「模组仓库」页安装。"] = "Framework is ready. Install mods from the Mod Repository page.",
        ["未检测到，请手动选择 PEAK.exe"] = "Not found. Select PEAK.exe manually",
        ["先在上方指定游戏目录，再安装模组。"] = "Select the game folder above before installing a mod.",
        ["仓库读取失败（可以点「刷新仓库」重试；不影响装框架）。"] = "Could not load the repository. Click Refresh to try again; framework installation is unaffected.",
        ["这个目录看起来不是 PEAK 的安装目录，请选到 PEAK.exe 那一层。"] = "This does not look like the PEAK folder. Select the folder containing PEAK.exe.",

        // ── 状态 / 按钮 / 提示（以前漏翻的部分）──────────────────
        ["未安装"] = "Not installed", ["重装"] = "Reinstall", ["更新"] = "Update",
        ["操作已取消。"] = "Operation cancelled.", ["安装失败"] = "Installation failed",
        ["确认卸载"] = "Confirm uninstall", ["当前状态"] = "Current status",
        ["只在雷霆商店 profile 里 · 从 Steam 启动不生效"] = "Only inside a Thunderstore profile · inactive when launched from Steam",
        ["仓库清单是空的（服务器还没上架任何模组）。"] = "The repository is empty (no mods have been published yet).",
        ["仓库暂时是空的。"] = "The repository is empty for now.",
        ["点「安装」把模组放进游戏的 BepInEx\\plugins（框架没装会自动先补框架）。"] =
            "Click Install to place the mod in the game's BepInEx\\plugins (the framework is installed first if missing).",
        ["正在读取仓库…"] = "Loading the repository...",
        ["BepInEx 框架还没装，先补框架（一次就好，以后装别的模组不会再下）…"] =
            "BepInEx is missing; installing it first (only once — later mods skip this)...",
        ["BepInEx 框架已存在，无需安装。"] = "BepInEx is already installed. Nothing to do.",
        ["检测到 BepInEx 装在雷霆商店 / r2modman 的 profile 里：那份只有从 Mod Manager 启动才加载得到，从 Steam 启动是空转。这里会把完整框架补到游戏目录。"] =
            "BepInEx was only found in a Thunderstore / r2modman profile: it loads when launched from the Mod Manager, not from Steam. The full framework will be added to the game folder.",
        ["框架安装完成"] = "Framework installed",
        ["BepInEx 框架安装完成，可以去「模组仓库」挑模组装了。"] =
            "BepInEx is installed. Pick your mods on the Mod Repository page.",
        ["解压 BepInEx 框架…"] = "Extracting BepInEx...",
        ["解压后的目录里没有 BepInEx 核心文件，压缩包结构可能变了。"] =
            "No BepInEx core files after extraction; the archive layout may have changed.",
        ["BepInEx 框架下载失败，请检查网络（或代理）后重试。"] =
            "BepInEx download failed. Check your network (or proxy) and try again.",
        ["版本清单里没有 version 字段。"] = "The version manifest has no version field.",
        ["服务器返回了空文件。"] = "The server returned an empty file.",
        ["下载的模组校验不通过（SHA256 不一致），已丢弃。"] =
            "The downloaded mod failed its SHA256 check and was discarded.",
        ["模组"] = "Mod", ["<更新源>"] = "<update feed>",
        ["最后一条错误："] = "Last error: ", ["未知错误"] = "unknown error",
        ["准备卸载 {0}…"] = "Preparing to uninstall {0}...",
        ["解压框架…"] = "Extracting the framework...",
    };

    /// <summary>
    /// 带占位符的文本（"已安装 · v{0}" 这类）：英文模式下查模板词典再格式化，
    /// 中文模式直接格式化（模板本身就是中文）。
    /// <para>
    /// 插值出来的字符串没法用整串查表，所以凡是 "…{0}…" 这种都走这里，
    /// 词典里存的是**带 {0} 的模板**而不是拼好的结果。
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> EnglishFormats = new(StringComparer.Ordinal)
    {
        ["已安装 · v{0}"] = "Installed · v{0}",
        ["未安装 · 在线版 v{0}"] = "Not installed · online v{0}",
        [" · 可更新到 v{0}"] = " · update available: v{0}",
        [" · 已是最新"] = " · up to date",
        ["已获取 {0} v{1}（{2}）"] = "Fetched {0} v{1} ({2})",
        ["，SHA256 校验通过"] = ", SHA256 verified",
        ["部署 {0}…"] = "Deploying {0}...",
        ["{0} v{1} 安装完成"] = "{0} v{1} installed",
        ["{0} v{1} 安装完成。"] = "{0} v{1} installed.",
        ["下载完成，{0}，开始解压…"] = "Download complete: {0}. Extracting...",
        ["已释放 {0} 个文件到游戏目录。"] = "Extracted {0} files into the game folder.",
        ["从 {0} 下载失败：{1}"] = "Download from {0} failed: {1}",
        ["── 安装 {0} v{1} ──"] = "── Installing {0} v{1} ──",
        ["准备安装 {0}…"] = "Preparing to install {0}...",
        ["确定卸载 {0} 吗？（只删这一个模组，保留框架和其它模组）"] =
            "Uninstall {0}? (Only this mod is removed; the framework and other mods stay.)",
        ["仓库共 {0} 个模组：{1}"] = "Repository: {0} mods — {1}",
        ["作者 QQ 已复制：{0}"] = "Author QQ copied: {0}",
        ["头像加载失败（{0}）：{1}"] = "Avatar failed to load ({0}): {1}",
        ["读取模组仓库失败：{0}"] = "Could not load the repository: {0}",
        ["下载模组 v{0}… {1}%"] = "Downloading mod v{0}... {1}%",
        ["下载模组 v{0}…"] = "Downloading mod v{0}...",
        ["未检测到 BepInEx 框架，开始下载 BepInExPack_PEAK {0}…"] =
            "BepInEx not found. Downloading BepInExPack_PEAK {0}...",
    };

    public static string Format(string template, params object?[] args)
    {
        var text = IsEnglish && EnglishFormats.TryGetValue(template, out var translated)
            ? translated
            : template;

        try
        {
            return string.Format(text, args);
        }
        catch (FormatException)
        {
            return text;
        }
    }

    public static string T(string? text)
    {
        if (string.IsNullOrEmpty(text) || !IsEnglish)
        {
            return text ?? string.Empty;
        }

        var source = text ?? string.Empty;

        if (EnglishText.TryGetValue(source, out var translated))
        {
            return translated;
        }

        return source
            .Replace("已安装 · ", "Installed · ")
            .Replace("未安装 · 在线版 ", "Not installed · Online ")
            .Replace(" · 可更新到 ", " · Update available: ")
            .Replace("正在下载", "Downloading")
            .Replace("安装完成", "installed")
            .Replace("下载完成", "Download complete")
            .Replace("解压", "Extracting")
            .Replace("已删除", "Removed")
            .Replace("失败：", " failed: ");
    }

    public static void Set(string language)
    {
        Current = string.Equals(language, English, StringComparison.Ordinal) ? English : Chinese;

        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(SettingsPath, Current);
        }
        catch
        {
            // The UI still switches for this run if persistence is unavailable.
        }
    }

    private static string Load()
    {
        try
        {
            var value = File.Exists(SettingsPath) ? File.ReadAllText(SettingsPath).Trim() : Chinese;
            return string.Equals(value, English, StringComparison.Ordinal) ? English : Chinese;
        }
        catch
        {
            return Chinese;
        }
    }
}
