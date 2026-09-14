using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PeakModder.HextechInstaller;

internal enum LogLevel
{
    Info,
    Success,
    Warn,
    Error,
}

internal readonly struct InstallProgress
{
    public InstallProgress(string message, double ratio)
    {
        Message = message;
        Ratio = ratio;
    }

    public string Message { get; }

    /// <summary>0 ~ 1，用于进度条。</summary>
    public double Ratio { get; }
}

/// <summary>某个组件当前的状态，界面直接拿它渲染。</summary>
internal readonly struct ComponentStatus
{
    public ComponentStatus(bool installed, string detail)
    {
        Installed = installed;
        Detail = detail;
    }

    public bool Installed { get; }

    public string Detail { get; }
}

/// <summary>
/// 装/卸的实际动作都在这里，界面只负责调用和显示日志。
/// <para>
/// 安装 = 补 BepInEx 框架（没有就从 Thunderstore 下）+ 从更新服务器下载最新版 mod dll 放进
/// <c>BepInEx/plugins/</c>；卸载 = 删掉 mod dll，必要时连带把框架也清了。
/// </para>
/// </summary>
internal sealed class InstallEngine
{
    public const string ModFileName = "PeakModder.HextechMod.dll";

    /// <summary>和 csproj 里声明的 BepInExPack_PEAK 版本对齐，装出来的框架版本是可复现的。</summary>
    private const string BepInExPackVersion = "5.4.75301";

    private const string BepInExFolderName = "BepInEx";
    private const string PluginsFolderName = "plugins";
    private const string CoreFolderName = "core";

    private const string PluginsRelative = @"BepInEx\plugins";
    private const string BepInExCoreRelative = @"BepInEx\core";
    private const string PreloaderRelative = @"BepInEx\core\BepInEx.Preloader.dll";

    /// <summary>doorstop 的配置，里面写着 BepInEx 的入口在哪。</summary>
    private const string DoorstopConfigFileName = "doorstop_config.ini";

    private const string ManifestFileName = "HextechMod.install.txt";
    private const string ManifestRelative = @"BepInEx\HextechMod.install.txt";

    /// <summary>Thunderstore 官网地址会 302 到 CDN，跟在后面那个是 CDN 直链，当前者打不开时兜底。</summary>
    private static readonly string[] BepInExPackUrls =
    {
        "https://thunderstore.io/package/download/BepInEx/BepInExPack_PEAK/" + BepInExPackVersion + "/",
        "https://gcdn.thunderstore.io/live/repository/packages/BepInEx-BepInExPack_PEAK-" + BepInExPackVersion + ".zip",
    };

    /// <summary>Thunderstore 的页面素材，解压时跳过 —— 扔到游戏根目录纯属垃圾。</summary>
    private static readonly HashSet<string> SkippedRootFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "manifest.json",
        "README.md",
        "icon.png",
        "changelog.md",
        "LICENSE",
    };

    /// <summary>由安装器装上的框架文件，卸载时按这份清单清理。</summary>
    private static readonly string[] BepInExOwnedPaths =
    {
        @"BepInEx",
        "doorstop_config.ini",
        "winhttp.dll",
        ".doorstop_version",
    };

    private readonly Action<string, LogLevel>? _log;

    public InstallEngine(Action<string, LogLevel>? log = null)
    {
        _log = log;
    }

    /// <summary>日志是可选依赖：没接日志（比如自动化跑）也不该让流程挂掉。</summary>
    private void Log(string message, LogLevel level)
        => _log?.Invoke(message, level);

    // ── BepInEx 定位 ─────────────────────────────────────────────

    /// <summary>
    /// BepInEx 的实际落点。
    /// <para>
    /// 这里只认「从 Steam 直接启动时 doorstop 真能加载到」的那一份：要么就在游戏目录里，
    /// 要么由 <c>doorstop_config.ini</c> 明确指到别处。
    /// </para>
    /// <para>
    /// 用 r2modman / Thunderstore Mod Manager 的人，框架被托管在 <c>%APPDATA%</c> 的
    /// profile 目录下，游戏目录里只剩 <c>winhttp.dll</c> + <c>doorstop_config.ini</c>，
    /// 而那份配置里写的是相对路径 <c>BepInEx\core\BepInEx.Preloader.dll</c> —— 从 Steam
    /// 启动时 doorstop 照着找就是找不到，整套框架等于没加载。所以 profile 里那份不能算数，
    /// 由 <see cref="FindModManagerBepInExRoots"/> 单独处理（补框架 + 同步 mod）。
    /// </para>
    /// </summary>
    internal sealed class BepInExLocation
    {
        public BepInExLocation(string root)
        {
            Root = root;
        }

        /// <summary>BepInEx 目录本身（里面是 core / plugins / config）。</summary>
        public string Root { get; }

        public string CoreDirectory => Path.Combine(Root, CoreFolderName);

        public string PluginsDirectory => Path.Combine(Root, PluginsFolderName);

        public string PreloaderPath => Path.Combine(CoreDirectory, "BepInEx.Preloader.dll");
    }

    /// <summary>
    /// 找出「从 Steam 启动时会被加载」的那份 BepInEx。顺序是游戏目录 → doorstop 指向的位置，
    /// 都没找到时返回游戏目录下的常规位置，由调用方判断它其实并不存在。
    /// </summary>
    public static BepInExLocation ResolveBepInEx(string gameDirectory)
    {
        // 1) 常规安装：框架就在游戏目录里。
        var local = Path.Combine(gameDirectory, BepInExFolderName);

        if (HasBepInExCore(local))
        {
            return new BepInExLocation(local);
        }

        // 2) doorstop 指到了别处（有人把整合包解到别的盘，配置里写的是绝对路径）。
        var target = ReadDoorstopTarget(gameDirectory);

        if (!string.IsNullOrWhiteSpace(target))
        {
            var fromDoorstop = BepInExDirectoryOf(target!, gameDirectory);

            if (fromDoorstop != null && HasBepInExCore(fromDoorstop))
            {
                return new BepInExLocation(fromDoorstop);
            }
        }

        return new BepInExLocation(local);
    }

    /// <summary>
    /// 这个 BepInEx 目录算不算装好了。只看 core 目录里有没有 preloader ——
    /// 官方是 <c>BepInEx.Preloader.dll</c>，BepInEx 6 那支叫
    /// <c>BepInEx.Unity.Mono.Preloader.dll</c>，按名字写死会漏判。
    /// </summary>
    private static bool HasBepInExCore(string bepInExDirectory)
    {
        var core = Path.Combine(bepInExDirectory, CoreFolderName);

        if (!Directory.Exists(core))
        {
            return false;
        }

        try
        {
            return Directory.GetFiles(core, "*Preloader*.dll").Length > 0
                   || Directory.GetFiles(core, "BepInEx*.dll").Length > 0;
        }
        catch (Exception)
        {
            // 权限不足之类，当它没装好。
            return false;
        }
    }

    /// <summary>读 doorstop_config.ini 里的 target_assembly。</summary>
    private static string? ReadDoorstopTarget(string gameDirectory)
    {
        var path = Path.Combine(gameDirectory, DoorstopConfigFileName);

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var trimmed = line.Trim();

                // doorstop 的模板里注释和数据混在一起，注释行得跳过。
                if (trimmed.Length == 0 || trimmed[0] == '#' || trimmed[0] == ';')
                {
                    continue;
                }

                var separator = trimmed.IndexOf('=');

                if (separator <= 0)
                {
                    continue;
                }

                if (!trimmed.Substring(0, separator).Trim().Equals("target_assembly", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = trimmed.Substring(separator + 1).Trim();
                return value.Length == 0 ? null : value;
            }
        }
        catch (Exception)
        {
            // 读不了就当没这条线索。
        }

        return null;
    }

    /// <summary>把 <c>…\BepInEx\core\BepInEx.Preloader.dll</c> 折回 <c>…\BepInEx</c>。</summary>
    private static string? BepInExDirectoryOf(string target, string gameDirectory)
    {
        try
        {
            // doorstop 认的相对路径是相对游戏目录的，这里保持一致。
            var full = Path.IsPathRooted(target)
                ? target
                : Path.Combine(gameDirectory, target);

            var core = new FileInfo(Path.GetFullPath(full)).Directory;

            return core?.Parent?.FullName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// r2modman 与 Thunderstore Mod Manager 的 profile 目录。它们把 mod 和 BepInEx
    /// 都放在 <c>%APPDATA%</c> 下，游戏目录里只有 doorstop 那三件套。
    /// </summary>
    private static IEnumerable<string> EnumerateModManagerProfiles(string gameDirectory)
    {
        var gameName = new DirectoryInfo(gameDirectory).Name;

        if (string.IsNullOrWhiteSpace(gameName))
        {
            yield break;
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        if (string.IsNullOrWhiteSpace(appData))
        {
            yield break;
        }

        // Thunderstore Mod Manager 比 r2modman 多一层 DataFolder。
        var roots = new[]
        {
            Path.Combine(appData, "Thunderstore Mod Manager", "DataFolder", gameName, "profiles"),
            Path.Combine(appData, "r2modmanPlus-local", gameName, "profiles"),
            Path.Combine(appData, "r2modman", gameName, "profiles"),
        };

        foreach (var root in roots)
        {
            string[] profiles;

            try
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                profiles = Directory.GetDirectories(root);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var profile in profiles)
            {
                yield return profile;
            }
        }
    }

    /// <summary>
    /// Mod Manager 的 profile 里那份 BepInEx。它不算「框架装好了」——
    /// 那种托管方式要靠 Mod Manager 自己启动游戏才生效（由它把 target_assembly 指过去），
    /// 玩家从 Steam 启动时游戏目录里没有 <c>BepInEx\core\BepInEx.Preloader.dll</c>，
    /// doorstop 会直接放弃加载。安装器靠它来判断「要往游戏目录补一份」，以及把 mod 同步过去。
    /// </summary>
    internal static IReadOnlyList<string> FindModManagerBepInExRoots(string gameDirectory)
    {
        var result = new List<string>();

        foreach (var profile in EnumerateModManagerProfiles(gameDirectory))
        {
            var candidate = Path.Combine(profile, BepInExFolderName);

            if (HasBepInExCore(candidate))
            {
                result.Add(candidate);
            }
        }

        return result;
    }

    /// <summary>玩家机器上有没有 Mod Manager 托管的 BepInEx。</summary>
    public static bool HasModManagerBepInEx(string gameDirectory)
        => FindModManagerBepInExRoots(gameDirectory).Count > 0;

    // ── 状态查询 ─────────────────────────────────────────────────

    public static string GetPluginsDirectory(string gameDirectory)
        => ResolveBepInEx(gameDirectory).PluginsDirectory;

    public static string GetModPath(string gameDirectory)
        => Path.Combine(GetPluginsDirectory(gameDirectory), ModFileName);

    public ComponentStatus QueryBepInEx(string gameDirectory)
    {
        var location = ResolveBepInEx(gameDirectory);

        if (!File.Exists(location.PreloaderPath))
        {
            // 官方命名之外还有别的 preloader，退一步只看 core 目录在不在。
            if (!Directory.Exists(location.CoreDirectory))
            {
                return new ComponentStatus(false, "未安装，安装时自动下载");
            }

            return new ComponentStatus(true, DescribeBepInEx(null));
        }

        return new ComponentStatus(true, DescribeBepInEx(ReadFileVersion(location.PreloaderPath)));
    }

    private static string DescribeBepInEx(string? version)
        => version == null ? "已安装" : "已安装 · " + version;

    /// <summary>
    /// 已安装的模组状态（按 dll 文件名查）。<paramref name="latest"/> 是服务器上的版本号
    /// （没问到就传 null），传进来时顺带把「能不能更新」写进详情里。
    /// </summary>
    public ComponentStatus QueryModFile(string gameDirectory, string fileName, string? latest = null)
    {
        var location = ResolveBepInEx(gameDirectory);
        var path = Path.Combine(location.PluginsDirectory, fileName);

        if (!File.Exists(path))
        {
            // 旧版安装器会把 mod 装进 Mod Manager 的 profile：那一份只有从 Mod Manager
            // 启动才加载得到，玩家从 Steam 启动会以为「装了却没效果」，所以照实说清楚。
            foreach (var root in FindModManagerBepInExRoots(gameDirectory))
            {
                if (File.Exists(Path.Combine(root, PluginsFolderName, fileName)))
                {
                    return new ComponentStatus(false, "只在雷霆商店 profile 里 · 从 Steam 启动不生效");
                }
            }

            return new ComponentStatus(
                false,
                latest == null ? "未安装" : "未安装 · 在线版 v" + latest);
        }

        var version = ReadFileVersion(path);
        var text = version == null ? "已安装" : "已安装 · v" + version;

        if (latest != null)
        {
            // 读不出已装版本号时宁愿说「可更新」—— 多说一句比让玩家漏掉更新强。
            text += version != null && !UpdateFeed.IsNewer(latest, version)
                ? " · 已是最新"
                : " · 可更新到 v" + latest;
        }

        return new ComponentStatus(true, text);
    }

    /// <summary>Steam 版游戏的可执行文件名就是 <c>PEAK.exe</c>。</summary>
    private const string GameProcessName = "PEAK";

    /// <summary>还挂在内存里的一个 PEAK 进程。</summary>
    internal sealed class RunningGame
    {
        public int Id { get; set; }

        /// <summary>可执行文件路径。进程受保护或权限不足时可能取不到。</summary>
        public string? Path { get; set; }

        /// <summary>有可见的主窗口。没有窗口的，基本就是上次退出时卡住的残留。</summary>
        public bool HasWindow { get; set; }

        public bool TryKill(int millisecondsTimeout)
        {
            try
            {
                using (var process = Process.GetProcessById(Id))
                {
                    process.Kill();
                    return process.WaitForExit(millisecondsTimeout);
                }
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 游戏正开着的话，BepInEx 的 winhttp.dll 被占着，写不进去。
    /// </summary>
    public static bool IsGameRunning(string? gameDirectory = null)
        => FindRunningGames(gameDirectory).Count > 0;

    /// <summary>
    /// 找还挡着路的 PEAK 进程。
    /// <para>
    /// 只比对进程名是不够的 —— 那样只要机器上有个叫 <c>PEAK.exe</c> 的东西就会误判成
    /// 「游戏正在运行」（别的同名程序、装在另一个盘的另一份游戏、甚至上一次退出时卡住没走干净的
    /// 残留进程都算），玩家就会遇到「我明明没开游戏，安装器却说我开着」。所以拿到进程之后
    /// 再看一眼它的可执行文件路径，只有真落在目标游戏目录里才算数。
    /// </para>
    /// <para>
    /// 路径取不到时（进程受保护之类）宁可当它在跑：文件被占用时硬写会把安装搞坏，
    /// 而多问一句只是麻烦一点。
    /// </para>
    /// </summary>
    public static IReadOnlyList<RunningGame> FindRunningGames(string? gameDirectory = null)
    {
        var result = new List<RunningGame>();
        Process[] processes;

        try
        {
            processes = Process.GetProcessesByName(GameProcessName);
        }
        catch (Exception)
        {
            return result;
        }

        foreach (var process in processes)
        {
            try
            {
                string? path = null;

                try
                {
                    path = process.MainModule?.FileName;
                }
                catch (Exception)
                {
                    // 32/64 位或权限问题，取不到就留空，后面按「不确定」处理。
                }

                // 路径明确落在别处 —— 那是别人家的同名进程，和这次要装的游戏没关系。
                if (path != null && gameDirectory != null && !IsInsideDirectory(path, gameDirectory))
                {
                    continue;
                }

                result.Add(new RunningGame
                {
                    Id = process.Id,
                    Path = path,
                    HasWindow = HasMainWindow(process),
                });
            }
            catch (Exception)
            {
                // 进程可能在枚举途中退出了，跳过就是。
            }
            finally
            {
                process.Dispose();
            }
        }

        return result;
    }

    private static bool HasMainWindow(Process process)
    {
        try
        {
            return process.MainWindowHandle != IntPtr.Zero;
        }
        catch (Exception)
        {
            // 拿不到窗口句柄时按「有窗口」算，让玩家自己退游戏最稳。
            return true;
        }
    }

    private static bool IsInsideDirectory(string file, string directory)
    {
        try
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(file));
            var root = Path.GetFullPath(directory);

            if (string.IsNullOrEmpty(folder))
            {
                return false;
            }

            folder = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // 必须比到分隔符，否则 C:\Games\PEAK2 会被当成在 C:\Games\PEAK 里。
            return string.Equals(folder, root, StringComparison.OrdinalIgnoreCase)
                   || folder.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>目录本身在不在 <paramref name="root"/> 里（含相等）。</summary>
    private static bool IsDirectoryUnder(string directory, string root)
    {
        try
        {
            var full = NormalizePath(directory);
            var normalizedRoot = NormalizePath(root);

            return string.Equals(full, normalizedRoot, StringComparison.OrdinalIgnoreCase)
                   || full.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // 路径不合法之类，当作「不在里面」—— 宁可少删。
            return false;
        }
    }

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(NormalizePath(a), NormalizePath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string NormalizePath(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    // ── 安装 ─────────────────────────────────────────────────────

    /// <summary>
    /// 安装一个模组（dll 从服务器下载到 plugins）。
    /// 框架还没装时会先自动把 BepInEx 补上 —— 没有框架的 mod 只是磁盘上的死文件。
    /// </summary>
    public async Task InstallModAsync(
        string gameDirectory,
        RemoteRelease mod,
        string fileName,
        IProgress<InstallProgress>? progress,
        CancellationToken token)
    {
        EnsureGameNotRunning(gameDirectory);

        // 框架必须是「从 Steam 启动就能加载到」的那一份；只装在 Mod Manager profile 里时，
        // 这里照样往游戏目录补一份完整的（doorstop 的相对路径从 Steam 启动找不到 profile 里的东西）。
        if (!QueryBepInEx(gameDirectory).Installed)
        {
            Log("BepInEx 框架还没装，先补框架（一次就好，以后装别的模组不会再下）…", LogLevel.Info);
            await InstallFrameworkAsync(gameDirectory, progress, token).ConfigureAwait(false);
        }

        token.ThrowIfCancellationRequested();

        // 主页的「安装框架」按钮不会重复下载 —— 这里才真正开始拉模组本体。
        var downloaded = await UpdateFeed.DownloadAsync(mod, progress, token).ConfigureAwait(false);

        Log(
            $"已获取 {mod.Name} v{mod.Version}（{FormatSize(downloaded.Length)}）"
            + (string.IsNullOrWhiteSpace(mod.Sha256) ? string.Empty : "，SHA256 校验通过"),
            LogLevel.Success);

        progress?.Report(new InstallProgress($"部署 {mod.Name}…", 0.92));
        DeployFile(gameDirectory, fileName, downloaded);

        // 再往 Mod Manager 的 profile 同步一份 —— 从那边启动时加载的是 profile 里的框架，
        // 只放游戏目录的话它那边的 plugins 里就没有这个模组。
        MirrorFileToModManagerProfiles(gameDirectory, fileName, downloaded);

        // 「框架是本安装器装的」这个事实要一直记着（卸载框架时靠它判断能不能删）。
        RegisterInstalledFile(gameDirectory, fileName);

        progress?.Report(new InstallProgress($"{mod.Name} v{mod.Version} 安装完成", 1));
        Log($"{mod.Name} v{mod.Version} 安装完成。", LogLevel.Success);
    }

    /// <summary>
    /// 只装 BepInEx 框架，不动任何模组 —— 主页那个按钮干的事。
    /// 框架已经在（哪怕只是在 Mod Manager 的 profile 里）时不重复下载。
    /// </summary>
    public async Task InstallFrameworkAsync(string gameDirectory, IProgress<InstallProgress>? progress, CancellationToken token)
    {
        EnsureGameNotRunning(gameDirectory);

        var bepInEx = QueryBepInEx(gameDirectory);

        if (bepInEx.Installed && File.Exists(ResolveBepInEx(gameDirectory).PreloaderPath))
        {
            Log("BepInEx 框架已存在，无需安装。", LogLevel.Success);
            return;
        }

        if (HasModManagerBepInEx(gameDirectory))
        {
            Log(
                "检测到 BepInEx 装在雷霆商店 / r2modman 的 profile 里：那份只有从 Mod Manager "
                + "启动才加载得到，从 Steam 启动是空转。这里会把完整框架补到游戏目录。",
                LogLevel.Warn);
        }

        await InstallBepInExCoreAsync(gameDirectory, progress, token).ConfigureAwait(false);

        // 「框架是本安装器装的」这个事实一旦成立就要一直记着：重装一次就冲成 false 的话，
        // 以后想「卸载并移除框架」会因为认不出自己装过而删不掉。
        var previous = ReadManifest(gameDirectory);
        WriteManifest(ResolveBepInEx(gameDirectory), previous.BepInExInstalledByInstaller || true);

        progress?.Report(new InstallProgress("框架安装完成", 1));
        Log("BepInEx 框架安装完成，可以去「模组仓库」挑模组装了。", LogLevel.Success);
    }

    private async Task InstallBepInExCoreAsync(string gameDirectory, IProgress<InstallProgress>? progress, CancellationToken token)
    {
        Log("未检测到 BepInEx 框架，开始下载 BepInExPack_PEAK " + BepInExPackVersion + "…", LogLevel.Warn);

        var archive = Path.Combine(Path.GetTempPath(), "HextechInstaller", "BepInExPack_PEAK.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);

        try
        {
            await DownloadWithFallbackAsync(progress, token, archive).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            progress?.Report(new InstallProgress("解压 BepInEx 框架…", 0.8));
            Log("下载完成，" + FormatSize(new FileInfo(archive).Length) + "，开始解压…", LogLevel.Info);

            var extracted = ExtractArchive(archive, gameDirectory);
            Log($"已释放 {extracted} 个文件到游戏目录。", LogLevel.Success);

            if (!File.Exists(Path.Combine(gameDirectory, PreloaderRelative)))
            {
                throw new InvalidOperationException("解压后的目录里没有 BepInEx 核心文件，压缩包结构可能变了。");
            }
        }
        finally
        {
            TryDelete(archive);
        }
    }

    private async Task DownloadWithFallbackAsync(
        IProgress<InstallProgress>? progress,
        CancellationToken token,
        string target)
    {
        // net48 默认不一定开 TLS 1.2，Thunderstore 只收 TLS 1.2 以上。
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

        Exception? last = null;

        foreach (var url in BepInExPackUrls)
        {
            try
            {
                await DownloadAsync(url, target, progress, token).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                last = exception;
                Log("从 " + ShortHost(url) + " 下载失败：" + exception.Message, LogLevel.Warn);
            }
        }

        throw new InvalidOperationException(
            "BepInEx 框架下载失败，请检查网络（或代理）后重试。" + Environment.NewLine +
            "最后一条错误：" + (last?.Message ?? "未知错误"), last);
    }

    private static async Task DownloadAsync(
        string url,
        string targetPath,
        IProgress<InstallProgress>? progress,
        CancellationToken token)
    {
        using (var handler = new HttpClientHandler { AllowAutoRedirect = true })
        using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) })
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("HextechModInstaller/1.0");

            using (var response = await client
                       .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token)
                       .ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();

                var total = response.Content.Headers.ContentLength ?? -1L;

                using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var target = File.Create(targetPath))
                {
                    var buffer = new byte[81920];
                    var watch = Stopwatch.StartNew();
                    long received = 0;
                    int count;

                    while ((count = await source.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
                    {
                        await target.WriteAsync(buffer, 0, count, token).ConfigureAwait(false);
                        received += count;

                        // 进度回调节流到 ~10Hz：每读 80KB 报一次会把 UI 线程刷爆。
                        if (watch.ElapsedMilliseconds < 100 && received < total)
                        {
                            continue;
                        }

                        watch.Restart();

                        var ratio = total > 0 ? (double)received / total : 0d;
                        var text = total > 0
                            ? $"下载 BepInEx 框架… {received * 100 / total}%（{FormatSize(received)} / {FormatSize(total)}）"
                            : $"下载 BepInEx 框架… {FormatSize(received)}";

                        progress?.Report(new InstallProgress(text, total > 0 ? ratio * 0.75 : 0.35));
                    }
                }
            }
        }
    }

    private void DeployFile(string gameDirectory, string fileName, byte[] bytes)
    {
        var plugins = GetPluginsDirectory(gameDirectory);
        Directory.CreateDirectory(plugins);

        WriteFileTo(plugins, fileName, bytes, "已写入 " + Path.Combine("BepInEx", "plugins", fileName));
    }

    /// <summary>
    /// 把模组也放一份到 Mod Manager 的 profile 里。
    /// <para>
    /// 玩家从雷霆商店 / r2modman 启动时，游戏加载的是 profile 里那份框架，
    /// 插件也只从它的 <c>plugins</c> 里找。两边各放一份，怎么启动都能用。
    /// </para>
    /// </summary>
    private void MirrorFileToModManagerProfiles(string gameDirectory, string fileName, byte[] bytes)
    {
        var primary = GetPluginsDirectory(gameDirectory);

        foreach (var root in FindModManagerBepInExRoots(gameDirectory))
        {
            var plugins = Path.Combine(root, PluginsFolderName);

            // 框架本来就是从这个 profile 里找到的，那就是同一个目录，不用复制。
            if (PathsEqual(plugins, primary))
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(plugins);
                WriteFileTo(
                    plugins,
                    fileName,
                    bytes,
                    "已同步到雷霆商店 profile：" + TailPath(Path.Combine(plugins, fileName)));
            }
            catch (Exception exception)
            {
                // 同步不过去不影响游戏目录那份，从 Steam 启动照样有效。
                Log("同步到 Mod Manager profile 失败（不影响从 Steam 启动）：" + exception.Message, LogLevel.Warn);
            }
        }
    }

    /// <summary>写模组的 dll，内容一致时跳过。</summary>
    private void WriteFileTo(string pluginsDirectory, string fileName, byte[] bytes, string message)
    {
        var target = Path.Combine(pluginsDirectory, fileName);

        if (File.Exists(target) && FilesEqual(target, bytes))
        {
            Log(Path.GetFileName(fileName) + " 内容没有变化，跳过写入。", LogLevel.Success);
            return;
        }

        File.WriteAllBytes(target, bytes);
        Log(message, LogLevel.Success);
    }

    // ── 卸载 ─────────────────────────────────────────────────────

    /// <summary>
    /// 卸载仓库里的某一个模组（按文件名清，只删这一个文件）。
    /// </summary>
    public bool UninstallModFile(string gameDirectory, string fileName)
    {
        EnsureGameNotRunning(gameDirectory);

        var manifest = ReadManifest(gameDirectory);
        var removed = false;

        foreach (var plugins in EnumerateInstallTargets(gameDirectory, manifest))
        {
            if (!Directory.Exists(plugins))
            {
                continue;
            }

            var path = Path.Combine(plugins, fileName);

            if (File.Exists(path))
            {
                TryDelete(path);
                Log("已删除 " + TailPath(path), LogLevel.Success);
                removed = true;
            }
        }

        return removed;
    }

    /// <summary>
    /// 删掉 mod；<paramref name="removeBepInEx"/> 为真且框架是本安装器装的时，连框架一起清掉。
    /// 清单里记过 <c>file=</c> 的仓库模组也一并清掉 —— 主页的「卸载」是全清。
    /// </summary>
    public void Uninstall(string gameDirectory, bool removeBepInEx)
    {
        EnsureGameNotRunning(gameDirectory);

        var manifest = ReadManifest(gameDirectory);
        var removedAnything = false;

        // mod 可能装在游戏目录，也可能装在 Mod Manager 的 profile 里。
        // 清单记着上次的落点，两个位置都扫一遍，免得换过启动方式后留下孤儿 dll。
        foreach (var plugins in EnumerateInstallTargets(gameDirectory, manifest))
        {
            removedAnything |= RemoveModFrom(plugins, manifest);
        }

        if (!removedAnything)
        {
            Log("没有找到本安装器装过的插件文件，可能已经卸过了。", LogLevel.Warn);
        }

        if (removeBepInEx)
        {
            if (!manifest.BepInExInstalledByInstaller)
            {
                Log(
                    "BepInEx 不是本安装器装的（你原来就有），为避免误删别的 mod 依赖，这里不动它。",
                    LogLevel.Warn);
            }
            else if (!IsDirectoryUnder(ResolveBepInEx(gameDirectory).Root, gameDirectory))
            {
                // 框架被 doorstop 指到游戏目录外面去了（整合包、Mod Manager 的 profile…），
                // 那些地方往往还挂着别人的 mod，本安装器不越界删。
                Log("BepInEx 装在游戏目录之外，本安装器不越界删除，需要的话请自行清理。", LogLevel.Warn);
            }
            else
            {
                RemoveBepInEx(gameDirectory);
                removedAnything = true;

                // 框架清了，清单也就没用了。反过来，只卸 mod 时清单要留着 ——
                // 玩家完全可能先卸 mod，过一阵再回来把框架也一起卸掉。
                // 新版清单在 BepInEx 目录里，老版在游戏目录，两个位置都清一下。
                TryDelete(Path.Combine(ResolveBepInEx(gameDirectory).Root, ManifestFileName));
                TryDelete(Path.Combine(gameDirectory, ManifestRelative));
            }
        }

        Log(
            removedAnything ? "卸载完成。" : "没有需要清理的内容，当前就是干净状态。",
            removedAnything ? LogLevel.Success : LogLevel.Info);
    }

    /// <summary>
    /// 可能装着本模组的 plugins 目录：当前解析出来的那个，加上清单里上次记录的那个。
    /// </summary>
    private static IEnumerable<string> EnumerateInstallTargets(string gameDirectory, ManifestData manifest)
    {
        var candidates = new List<string> { GetPluginsDirectory(gameDirectory) };

        // 新装法会往 Mod Manager 的 profile 里也同步一份，旧装法甚至只装在那儿。
        foreach (var root in FindModManagerBepInExRoots(gameDirectory))
        {
            candidates.Add(Path.Combine(root, PluginsFolderName));
        }

        if (!string.IsNullOrWhiteSpace(manifest.PluginsDirectory))
        {
            candidates.Add(manifest.PluginsDirectory!);
        }

        var seen = new List<string>();

        foreach (var candidate in candidates)
        {
            var normalized = candidate
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            var duplicate = false;

            foreach (var existing in seen)
            {
                if (string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    duplicate = true;
                    break;
                }
            }

            if (duplicate)
            {
                continue;
            }

            seen.Add(normalized);
            yield return candidate;
        }
    }

    /// <summary>
    /// 从一个 plugins 目录里清掉本安装器装的模组：主模组（含改名前的历史残留）
    /// 加上清单里 <c>file=</c> 记过的仓库模组。
    /// </summary>
    private bool RemoveModFrom(string pluginsDirectory, ManifestData manifest)
    {
        if (!Directory.Exists(pluginsDirectory))
        {
            return false;
        }

        var removed = false;
        var modPath = Path.Combine(pluginsDirectory, ModFileName);

        if (File.Exists(modPath))
        {
            TryDelete(modPath);
            Log("已删除 " + TailPath(modPath), LogLevel.Success);
            removed = true;
        }

        // 清单里记过的仓库模组（InstallModAsync 每装一个就记一条）。
        foreach (var file in manifest.Files)
        {
            if (string.Equals(file, ModFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var path = Path.Combine(pluginsDirectory, file);

            if (File.Exists(path))
            {
                TryDelete(path);
                Log("已删除 " + TailPath(path), LogLevel.Success);
                removed = true;
            }
        }

        // 顺带扫一下旧版本可能留下的同名文件（改过名的历史遗留）。
        try
        {
            foreach (var leftover in Directory.GetFiles(pluginsDirectory, "PeakModder.Hextech*.dll"))
            {
                if (string.Equals(leftover, modPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                TryDelete(leftover);
                Log("清理残留插件：" + Path.GetFileName(leftover), LogLevel.Warn);
                removed = true;
            }
        }
        catch (Exception)
        {
            // 目录读不动就算了，别让卸载整个失败。
        }

        return removed;
    }

    /// <summary>日志里只留路径最后两段，profile 那种长路径不至于把日志撑爆。</summary>
    private static string TailPath(string path)
    {
        var name = Path.GetFileName(path);
        var parent = Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty);

        return string.IsNullOrEmpty(parent) ? name : parent + "\\" + name;
    }

    private void RemoveBepInEx(string gameDirectory)
    {
        foreach (var relative in BepInExOwnedPaths)
        {
            var path = Path.Combine(gameDirectory, relative);

            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                    Log("已删除目录 " + relative, LogLevel.Success);
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                    Log("已删除 " + relative, LogLevel.Success);
                }
            }
            catch (Exception exception)
            {
                Log($"删除 {relative} 失败：{exception.Message}", LogLevel.Error);
            }
        }
    }

    /// <summary>框架目录里还留着别的插件时，删框架等于把别人的 mod 一起废了 —— 界面要先问一句。</summary>
    public static IReadOnlyList<string> FindForeignPlugins(string gameDirectory)
    {
        var result = new List<string>();
        var plugins = GetPluginsDirectory(gameDirectory);

        if (!Directory.Exists(plugins))
        {
            return result;
        }

        try
        {
            foreach (var file in Directory.GetFiles(plugins, "*.dll"))
            {
                var name = Path.GetFileName(file);

                if (name.StartsWith("PeakModder.Hextech", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                result.Add(name);
            }
        }
        catch (Exception)
        {
            // 读不到就当没有，最多是少问一句。
        }

        return result;
    }

    // ── 压缩包 ───────────────────────────────────────────────────

    /// <summary>
    /// 解压到游戏根目录。Thunderstore 的包外面套了一层同名目录，这里会自动剥掉，
    /// 所以 <c>BepInExPack_PEAK/BepInEx/…</c> 会落到 <c>&lt;游戏&gt;/BepInEx/…</c>。
    /// </summary>
    private int ExtractArchive(string archivePath, string gameDirectory)
    {
        var root = Path.GetFullPath(gameDirectory);
        var count = 0;

        using (var archive = ZipFile.OpenRead(archivePath))
        {
            var prefix = DetectPrefix(archive);

            foreach (var entry in archive.Entries)
            {
                var relative = Strip(entry.FullName, prefix);

                if (string.IsNullOrEmpty(relative))
                {
                    continue;
                }

                // 只剥一层时，顶层的页面素材文件直接跳过。
                if (relative.IndexOf('/') < 0 && SkippedRootFiles.Contains(relative))
                {
                    continue;
                }

                var destination = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));

                // 防 zip slip：压缩包里的路径不许跑到游戏目录外面去。
                if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    Log("跳过越界的压缩包条目：" + entry.FullName, LogLevel.Warn);
                    continue;
                }

                if (entry.Name.Length == 0)
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, true);
                count++;
            }
        }

        return count;
    }

    /// <summary>压缩包里所有条目是否共用同一个顶层目录；是的话返回 <c>"顶层/"</c>，否则空串。</summary>
    private static string DetectPrefix(ZipArchive archive)
    {
        string? prefix = null;

        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            var slash = name.IndexOf('/');

            if (slash <= 0)
            {
                // 根上的 Thunderstore 页面素材（icon.png / manifest.json …）不算「顶层目录」。
                // BepInExPack 的包里正好混了这么几个，不跳过的话会因为一个 icon.png
                // 就判定「没有外层目录」，最后整套 BepInEx 被解到 BepInExPack_PEAK/ 子目录里去。
                if (SkippedRootFiles.Contains(name))
                {
                    continue;
                }

                // 根上真有别的文件，说明没有可以剥的外层。
                return string.Empty;
            }

            var top = name.Substring(0, slash);

            if (prefix == null)
            {
                prefix = top;
            }
            else if (!string.Equals(prefix, top, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }
        }

        return prefix == null ? string.Empty : prefix + "/";
    }

    private static string Strip(string name, string prefix)
    {
        var normalized = name.Replace('\\', '/');

        if (prefix.Length > 0)
        {
            if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            normalized = normalized.Substring(prefix.Length);
        }

        return normalized.Trim('/');
    }

    // ── 清单 ─────────────────────────────────────────────────────

    /// <summary>
    /// 写安装清单（记录框架归属 + plugins 落点 + 装过的模组文件，卸载全靠它）。
    /// <paramref name="installedFiles"/> 传 null 时保留清单里已记录的文件列表。
    /// </summary>
    private void WriteManifest(BepInExLocation location, bool bepInExInstalledByInstaller, IReadOnlyCollection<string>? installedFiles = null)
    {
        try
        {
            var files = installedFiles ?? ReadManifestFiles(location);
            var builder = new StringBuilder();
            builder.AppendLine("mod=" + ModFileName);
            builder.AppendLine("installedAt=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            builder.AppendLine("bepinexInstalledByInstaller=" + (bepInExInstalledByInstaller ? "true" : "false"));
            builder.AppendLine("bepinexVersion=" + BepInExPackVersion);

            // 记下 mod 到底装到哪个 plugins —— Mod Manager 的 profile 路径每次都可能换，
            // 回头卸载时光靠「重新猜一次」未必能猜到同一个地方。
            builder.AppendLine("plugins=" + location.PluginsDirectory);

            foreach (var file in files)
            {
                builder.AppendLine("file=" + file);
            }

            Directory.CreateDirectory(location.Root);
            File.WriteAllText(Path.Combine(location.Root, ManifestFileName), builder.ToString(), Encoding.UTF8);
        }
        catch (Exception exception)
        {
            // 清单只是给卸载用的，写不进去不该让整个安装失败。
            Log("安装清单写入失败（不影响使用）：" + exception.Message, LogLevel.Warn);
        }
    }

    /// <summary>
    /// 仓库模组装好后往清单里登记一条 <c>file=</c>（主页「卸载」时好把仓库模组一起清掉）。
    /// </summary>
    private void RegisterInstalledFile(string gameDirectory, string fileName)
    {
        try
        {
            var manifest = ReadManifest(gameDirectory);
            var files = new List<string>(manifest.Files);

            for (var i = 0; i < files.Count; i++)
            {
                if (string.Equals(files[i], fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            files.Add(fileName);
            WriteManifest(ResolveBepInEx(gameDirectory), manifest.BepInExInstalledByInstaller, files);
        }
        catch (Exception)
        {
            // 登记失败只影响「主页一键全清」时少删一个文件，单个卸载不受影响。
        }
    }

    /// <summary>读一份清单里已登记的 file= 列表（读不到就当没有）。</summary>
    private static List<string> ReadManifestFiles(BepInExLocation location)
    {
        var result = new List<string>();
        var path = Path.Combine(location.Root, ManifestFileName);

        if (!File.Exists(path))
        {
            return result;
        }

        try
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var separator = line.IndexOf('=');

                if (separator <= 0)
                {
                    continue;
                }

                if (line.Substring(0, separator).Trim().Equals("file", StringComparison.OrdinalIgnoreCase)
                    && line.Substring(separator + 1).Trim().Length > 0)
                {
                    result.Add(line.Substring(separator + 1).Trim());
                }
            }
        }
        catch (Exception)
        {
            // 读不动就当没有。
        }

        return result;
    }

    private static ManifestData ReadManifest(string gameDirectory)
    {
        var data = new ManifestData();

        // 新版清单放在 BepInEx 目录里（Mod Manager 模式下那就是 profile 里），
        // 老版本固定在游戏目录，两个位置都找一下。
        var candidates = new[]
        {
            Path.Combine(ResolveBepInEx(gameDirectory).Root, ManifestFileName),
            Path.Combine(gameDirectory, ManifestRelative),
        };

        foreach (var path in candidates)
        {
            if (TryReadManifest(path, data))
            {
                break;
            }
        }

        return data;
    }

    private static bool TryReadManifest(string path, ManifestData data)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var separator = line.IndexOf('=');

                if (separator <= 0)
                {
                    continue;
                }

                var key = line.Substring(0, separator).Trim();
                var value = line.Substring(separator + 1).Trim();

                if (string.Equals(key, "bepinexInstalledByInstaller", StringComparison.OrdinalIgnoreCase))
                {
                    data.BepInExInstalledByInstaller = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
                }
                else if (string.Equals(key, "plugins", StringComparison.OrdinalIgnoreCase) && value.Length > 0)
                {
                    data.PluginsDirectory = value;
                }
                else if (string.Equals(key, "file", StringComparison.OrdinalIgnoreCase) && value.Length > 0)
                {
                    data.Files.Add(value);
                }
            }

            return true;
        }
        catch (Exception)
        {
            // 清单读坏了就当作「不是我们装的」，宁可少删也不误删。
            return false;
        }
    }

    private static string? ReadFileVersion(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return string.IsNullOrWhiteSpace(info.FileVersion) ? null : info.FileVersion!.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>下载回来的 dll 版本号直接来自清单与下载校验，不再需要落盘探测（旧探测逻辑已删）。</summary>

    private sealed class ManifestData
    {
        public bool BepInExInstalledByInstaller { get; set; }

        /// <summary>上次把 mod 装到了哪个 plugins 目录，没记录时为 null。</summary>
        public string? PluginsDirectory { get; set; }

        /// <summary>清单里 <c>file=</c> 记过的仓库模组文件名（主页「卸载」时一并清掉）。</summary>
        public List<string> Files { get; } = new();
    }

    // ── 小工具 ───────────────────────────────────────────────────

    private static void EnsureGameNotRunning(string gameDirectory)
    {
#if DEBUG
        // 自动化测试在临时目录里跑，和玩家真开着的那个游戏不冲突。
        // 这条例外只在 Debug 构建里存在，发布出去的 Release 版本没有。
        if (Environment.GetEnvironmentVariable("HEXTECH_SKIP_RUNNING_CHECK") == "1")
        {
            return;
        }
#endif

        if (IsGameRunning(gameDirectory))
        {
            throw new InvalidOperationException(
                "检测到 PEAK 正在运行，请先完全退出游戏再安装或卸载。" + Environment.NewLine +
                "如果游戏窗口确实已经关掉了，说明进程卡在后台没走干净："
                + "打开任务管理器 → 详细信息 → 结束 PEAK.exe 后再试。");
        }
    }

    private static bool FilesEqual(string path, byte[] expected)
    {
        try
        {
            var info = new FileInfo(path);

            if (info.Length != expected.Length)
            {
                return false;
            }

            var actual = File.ReadAllBytes(path);

            for (var i = 0; i < actual.Length; i++)
            {
                if (actual[i] != expected[i])
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 临时文件删不掉不值得报错。
        }
    }

    private static string ShortHost(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return bytes + " B";
        }

        if (bytes < 1024 * 1024)
        {
            return (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB";
        }

        return (bytes / (1024.0 * 1024.0)).ToString("0.##", CultureInfo.InvariantCulture) + " MB";
    }
}
