using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Threading;
using BepInEx;
using Photon.Pun;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace PeakModder.HextechMod;

/// <summary>
/// 「遇到 bug 把日志传上来」的上报端。
/// <para>
/// 玩家按一下键（默认 <c>F10</c>，见配置「界面 / 上传日志」），这里把这台机器上看得到的东西拼成一段纯文本、
/// gzip 之后 POST 给服务器上的 <c>report.php</c>：<b>本机 + 全房间角色</b>（每人词条 / 状态）、
/// <b>全局机制状态</b>（防挂机计时 / 禁用名单 / 商店改价 / 本局进度）、<b>补丁体检</b>、
/// <b>全部配置</b>、<b>BepInEx 日志尾部</b>、<b>Unity 日志尾部</b>（两份日志都只取最近 10 分钟，见下文）。
/// <para>
/// 它服务的是<b>整个模组</b>的排查：每个子系统都该在这里留下能读懂的证据，
/// 而不是只有「这次出事的那条功能」的日志。有新的全局机制时，顺手往 <see cref="AppendMechanisms"/> 里加一行。
/// </para>
/// 服务器不解压，只存盘并回一个 6 位数字编号，作者拿编号去服务器取（<c>_tools/fetch-report.ps1 -Code 482915</c>）。
/// </para>
/// <para>
/// 日志<b>只取「按 F10 那一刻往前 10 分钟」</b>：两份日志都<b>没有时间戳</b>（BepInEx 5 的 LogOutput.log
/// 每行只有级别与来源，Unity 的 Player.log 每行只有内容），所以每 30 秒记一次「现在几点 + 两份文件各多长」
/// （<see cref="Tick"/>），事后用这份尺子反推出「十分钟前」落在文件的第几个字节；再有每份的字节上限兜底
/// （<see cref="BepInExLogMaxBytes"/> / <see cref="UnityLogMaxBytes"/>），刷屏日志也传不出几 MB。
/// </para>
/// <para>
/// 上传走<b>两条互不相同的通道</b>：先试 Mono 的 <c>HttpWebRequest</c>（托管实现、在后台线程上、关掉系统代理自动探测），
/// 不成再换 Unity 自己的 <c>UnityWebRequest</c>。两条都超时的话，把报告落在 BepInEx 目录下让玩家手动发给作者 ——
/// 「按了键一直停在正在上传」曾经就是卡在某一条通道上，玩家最后什么也拿不到，那是这个功能最难受的失败方式。
/// </para>
/// <para>
/// 只走「玩家自己按」这一条路，不做任何自动上传 —— 日志里有昵称和本地路径，
/// 玩家得知道自己传了什么，所以按键说明里把包含的内容写清楚了。
/// 地址不写死在源码里：跟着更新源（<see cref="UpdateFeed.ManifestUrl"/>）同一个目录取 <c>report.php</c>。
/// </para>
/// </summary>
internal static class LogReporter
{
    /// <summary>日志取样窗口：按 F10 那一刻往前推这么多分钟，再早的内容不传。</summary>
    public const int WindowMinutes = 10;

    /// <summary>
    /// BepInEx 日志在窗口内最多取这么多字节。窗口本身才是主限制，这个是防爆量日志的兜底
    /// （mod 自己的输出都在这份里）。
    /// </summary>
    private const int BepInExLogMaxBytes = 256 * 1024;

    /// <summary>Unity 的 Player.log 在窗口内最多取这么多字节（游戏侧的异常只在这份里）。</summary>
    private const int UnityLogMaxBytes = 512 * 1024;

    /// <summary>单条通道等这么久就放弃（另加 5 秒宽限，见 <see cref="Upload"/>）。</summary>
    private const int RequestTimeoutSeconds = 20;

    /// <summary>多久采一次「两份日志各多长」（用来算「十分钟前」对应的文件偏移）。</summary>
    private const float SampleIntervalSeconds = 30f;

    /// <summary>
    /// <see cref="Busy"/> 的自我保护：上传超过这么久还没回来，就当那次已经没了、允许再按一次。
    /// 万一协程被意外掐断（对象被销毁之类），不至于让 F10 永远只回一句「还在上传中」。
    /// </summary>
    private const float BusyStaleSeconds = 150f;

    /// <summary>「词条触发统计」里纯被动词条最多列几行（剩下的只报个数量，免得报告被几十行灌满）。</summary>
    private const int MaxPassiveUsageLines = 12;

    /// <summary>
    /// 在代码里埋了「事件计数」的词条 id（就是 <see cref="HextechState.RecordEffect"/> 的调用点）。
    /// <para>
    /// 报告里只有这些 id 的「0 次」才等于「本局一次都没生效」，是排查「词条好像没作用」的第一手证据；
    /// 没埋点的词条只能报在场时长。**新埋一处就往下加一条 id**，否则报告会把「没埋点」和「没触发」混在一起。
    /// </para>
    /// </summary>
    private static readonly string[] EventCountedIds =
    {
        // 基础词条里按秒触发的（DefaultHextechs 的 TickStatus / 吐纳 / 石化抗性）
        // 「挣脱」已于 2026-09-14 删除，从埋点表里移除。
        // 「围炉谈心」2026-09-14 起改回受伤值并埋点（AdvancedHextechs.TickFiresideChat）。
        "recovery", "warm_body", "full_belly", "clear_mind", "breathe",
        AdvancedHextechs.FiresideChatId,
        DefaultHextechs.ScavengerId,
        DefaultHextechs.SnackId,
        DefaultHextechs.ToughBodyId,
        DefaultHextechs.GlassyId,
        DefaultHextechs.ThickHideId,
        DefaultHextechs.AntidoteBodyId,
        DefaultHextechs.HeatShieldId,
        DefaultHextechs.CurseBreakerId,
        DefaultHextechs.FeatherFallId,
        DefaultHextechs.PhoenixId,
        DefaultHextechs.SpiderManId,
        DefaultHextechs.TumblerId,
        DefaultHextechs.ThrowMasterId,
        DefaultHextechs.PetrifyWardId,

        // 进阶词条
        AdvancedHextechs.ButterfingersId,
        AdvancedHextechs.HotHandsId,
        AdvancedHextechs.SporeImmunityId,
        AdvancedHextechs.OsteoporosisId,
        AdvancedHextechs.ResurrectId,
    };

    private static bool _busy;
    private static float _busySince;

    /// <summary>正在上传中。按键那边用它挡住连点（带过期，见 <see cref="BusyStaleSeconds"/>）。</summary>
    public static bool Busy => _busy && Time.realtimeSinceStartup - _busySince < BusyStaleSeconds;

    /// <summary>
    /// 「此刻是几点、两份日志各有多长」。两个日志文件都没有时间戳，
    /// 所以「最近十分钟」只能靠这些样本事后反推（详见 <see cref="Tick"/>）。
    /// </summary>
    private sealed class LogSample
    {
        public DateTime At;
        public long BepInEx;
        public long Unity;
    }

    private static readonly List<LogSample> Samples = new();
    private static float _nextSampleAt;

    private static string BepInExLogPath => Path.Combine(Paths.BepInExRootPath, "LogOutput.log");

    private static string UnityLogPath => Path.Combine(Application.persistentDataPath, "Player.log");

    /// <summary>
    /// 每帧由 <see cref="HextechManager"/> 调一次，内部自己按 <see cref="SampleIntervalSeconds"/> 节流：
    /// 记下「现在几点 + 两份日志各有多少字节」，用来给「最近十分钟」当尺子。
    /// <para>
    /// 为什么不用日志里的时间：BepInEx 5 的 LogOutput.log 是 <c>[级别 : 来源] 内容</c>、Unity 的 Player.log
    /// 更是只有裸内容，两份都没有时间列，没法按行筛。样本只活在内存里、进程一退就没了 ——
    /// 这没关系，报告本来就是给「这一次」，采样从模组加载那一刻就开始，够覆盖十分钟。
    /// </para>
    /// </summary>
    public static void Tick()
    {
        var now = Time.realtimeSinceStartup;

        if (now < _nextSampleAt)
        {
            return;
        }

        _nextSampleAt = now + SampleIntervalSeconds;

        Samples.Add(new LogSample
        {
            At = DateTime.Now,
            BepInEx = FileLength(BepInExLogPath),
            Unity = FileLength(UnityLogPath),
        });

        // 只留「窗口 + 一分钟」这一段的样本（多留一条在窗口之外的，它才是「十分钟前」的落点）。
        var cutoff = DateTime.Now.AddMinutes(-(WindowMinutes + 1));

        while (Samples.Count > 1 && Samples[1].At < cutoff)
        {
            Samples.RemoveAt(0);
        }

        if (Samples.Count > 64)
        {
            Samples.RemoveRange(0, Samples.Count - 64);
        }
    }

    private static long FileLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception)
        {
            // 文件还没生成 / 被独占着读不到，就当 0：顶多让窗口从更早的地方开始。
            return 0;
        }
    }

    /// <summary>
    /// 「十分钟前」落在 <paramref name="path"/> 里的第几个字节。
    /// 样本里还没有够老的一条（这次会话启动不到十分钟）时返回 0 —— 那一整份本来就在窗口里。
    /// </summary>
    private static long WindowStartBytes(bool unityLog, long length)
    {
        var wanted = DateTime.Now.AddMinutes(-WindowMinutes);
        long start = 0;

        for (var i = 0; i < Samples.Count; i++)
        {
            if (Samples[i].At > wanted)
            {
                break;
            }

            start = unityLog ? Samples[i].Unity : Samples[i].BepInEx;
        }

        // 文件可能比样本记下的还短（日志被截断 / 重开过），别越过 EOF。
        return Math.Max(0, Math.Min(start, length));
    }

    /// <summary>
    /// 上报地址 = 更新源同目录下的 <c>report.php</c>。
    /// 没配置更新源（本地自建、或构建时没注入）时返回空串，调用方直接跳过。
    /// </summary>
    public static string EndpointUrl
    {
        get
        {
            var manifest = UpdateFeed.ManifestUrl;

            if (string.IsNullOrWhiteSpace(manifest))
            {
                return string.Empty;
            }

            var slash = manifest.LastIndexOf('/');

            return slash < 0 ? manifest + "report.php" : manifest.Substring(0, slash + 1) + "report.php";
        }
    }

    /// <summary>
    /// 一条通道的尝试结果。上传线程写、主线程读，所以 <see cref="Done"/> 用 volatile 当栅栏
    /// （写它之前写好的 Code / Error 一定跟着可见），赋值顺序也都是「先内容后 Done」。
    /// </summary>
    private sealed class SendResult
    {
        public string? Code;
        public string? Error;

        private volatile bool _done;

        public bool Done
        {
            get => _done;
            set => _done = value;
        }
    }

    /// <summary>
    /// 打包并上传。<paramref name="done"/> 的参数是「编号, 失败原因」——
    /// 成功时第二个为 null，失败时第一个为 null、第二个是给玩家看的短句。
    /// <para>
    /// <paramref name="progress"/> 会在几个节点被叫一下（打包完 / 每秒一次「已等几秒」/ 换通道），
    /// 调用方拿它去刷提示：按了键之后一片安静是玩家最容易误判成「卡死了」的时候。
    /// </para>
    /// </summary>
    public static IEnumerator Upload(Action<string?, string?> done, Action<string>? progress = null)
    {
        if (Busy)
        {
            done(null, "上一次还在传");
            yield break;
        }

        var url = EndpointUrl;

        if (string.IsNullOrWhiteSpace(url))
        {
            HextechPlugin.Log.LogWarning("[上报] 没有配置上报地址（更新源为空），跳过。");
            done(null, "这个版本没配上报地址");
            yield break;
        }

        _busy = true;
        _busySince = Time.realtimeSinceStartup;

        // 打包是同步的（读文件 + gzip），这里卡一下帧；先让提示画出来，玩家才知道是自己在按。
        progress?.Invoke($"正在打包日志（只取最近 {WindowMinutes} 分钟）…");
        yield return null;

        byte[]? payload = null;
        var report = string.Empty;
        var info = string.Empty;

        try
        {
            report = BuildReport();
            payload = Gzip(Encoding.UTF8.GetBytes(report));
            info = BuildInfoHeader();
        }
        catch (Exception exception)
        {
            HextechPlugin.Log.LogError($"[上报] 打包日志失败：{exception}");
        }

        if (payload == null)
        {
            _busy = false;
            done(null, "打包失败，细节见日志");
            yield break;
        }

        var kilobytes = payload.Length / 1024;

        HextechPlugin.Log.LogInfo($"[上报] 开始上传，压缩后 {kilobytes} KB。");

        var result = new SendResult();

        progress?.Invoke($"正在上传日志（{kilobytes} KB）…");
        yield return SendViaMono(url, payload, info, r => result = r, progress);

        HextechPlugin.Log.LogInfo($"[上报] 通道一（HttpWebRequest）：{DescribeResult(result)}");

        // 一条不通换另一条：两条是完全不同的网络栈，某台机器上其中一条不通（系统代理自动探测、
        // 防火墙放行、libcurl 的怪脾气）时，另一条往往还能用。
        if (string.IsNullOrEmpty(result.Code))
        {
            var firstError = result.Error;

            progress?.Invoke("换一条通道再试…");
            yield return SendViaUnity(url, payload, info, r => result = r, progress);

            HextechPlugin.Log.LogInfo($"[上报] 通道二（UnityWebRequest）：{DescribeResult(result)}");

            if (string.IsNullOrEmpty(result.Code))
            {
                result.Error = Combine(firstError, result.Error);
            }
        }

        _busy = false;

        if (!string.IsNullOrEmpty(result.Code))
        {
            HextechPlugin.Log.LogInfo($"[上报] 上传成功，取件编号 {result.Code}。");

            // 本机留一份账（设置面板里能翻、能再复制一次）：编号只在浮窗里显示一次，
            // 玩家关掉之后要是剪贴板又被别的东西顶了，就只能重传一整份。
            ReportHistory.Add(result.Code, kilobytes, SceneManager.GetActiveScene().name);

            done(result.Code, null);
            yield break;
        }

        var reason = string.IsNullOrEmpty(result.Error) ? "不明原因" : result.Error;

        HextechPlugin.Log.LogWarning($"[上报] 上传失败：{reason}");

        // 两条通道都不通也不能让玩家白按一次：把这份报告落在 BepInEx 目录下，
        // 让他能直接把文件发给作者（传得上去时不写，免得每传一次就在玩家硬盘上留一份带昵称的日志）。
        var saved = SaveLocally(report);

        done(null, saved == null ? reason : $"{reason}（已存到 BepInEx 目录）");
    }

    private static string DescribeResult(SendResult result)
    {
        return string.IsNullOrEmpty(result.Code)
            ? $"失败 —— {result.Error ?? "没拿到编号"}"
            : $"成功，编号 {result.Code}";
    }

    private static string Combine(string? first, string? second)
    {
        if (string.IsNullOrEmpty(second))
        {
            return first ?? "不明原因";
        }

        if (string.IsNullOrEmpty(first) || string.Equals(first, second, StringComparison.Ordinal))
        {
            return second;
        }

        return $"{first} / 换通道后：{second}";
    }

    /// <summary>
    /// 通道一：Mono 自己的 <c>HttpWebRequest</c>，在后台线程上发。
    /// <para>
    /// 两个刻意的设定：<c>Proxy = null</c> 关掉系统代理自动探测 —— WPAD 在部分机器上会让请求干等到超时，
    /// 这正是「按了键一直停在正在上传」的经典成因；线程设成后台线程，游戏关掉时不会被它拖住。
    /// </para>
    /// </summary>
    private static IEnumerator SendViaMono(string url, byte[] payload, string info, Action<SendResult> finished, Action<string>? progress)
    {
        var result = new SendResult();

        var worker = new Thread(() =>
        {
            try
            {
                // 有些服务器对 Expect: 100-continue 响应很慢，直接关掉，省掉一个来回。
                ServicePointManager.Expect100Continue = false;

                var request = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
                request.Method = "POST";
                request.Proxy = null;
                request.Timeout = RequestTimeoutSeconds * 1000;
                request.ReadWriteTimeout = RequestTimeoutSeconds * 1000;
                request.ContentType = "application/octet-stream";
                request.Headers["Content-Encoding"] = "gzip";
                request.Headers["X-Hextech-Info"] = info;
                request.ContentLength = payload.Length;

                using (var body = request.GetRequestStream())
                {
                    body.Write(payload, 0, payload.Length);
                }

                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    var text = reader.ReadToEnd();

                    result.Code = ReadJsonString(text, "code");

                    if (string.IsNullOrEmpty(result.Code))
                    {
                        result.Error = ReadJsonString(text, "error") ?? "服务器没返回编号";
                    }
                }
            }
            catch (WebException webException)
            {
                result.Error = DescribeWebException(webException);
            }
            catch (Exception exception)
            {
                result.Error = exception.Message;
            }
            finally
            {
                result.Done = true;
            }
        })
        {
            IsBackground = true,
            Name = "HextechModReport",
        };

        worker.Start();

        yield return WaitFor(result, progress);

        finished(result);
    }

    /// <summary>
    /// 通道二：Unity 自己的 <c>UnityWebRequest</c>。
    /// <para>
    /// 它以前是唯一的一条，出过「按了键就再也不回来」的事，所以这里除了它自己的 <c>timeout</c>
    /// 还加了一圈按真实时间走的看门狗，到点直接 <c>Abort()</c> —— 不管底层怎么卡，
    /// 玩家总能拿到一句话，而不是永远停在「正在上传」。
    /// </para>
    /// </summary>
    private static IEnumerator SendViaUnity(string url, byte[] payload, string info, Action<SendResult> finished, Action<string>? progress)
    {
        var result = new SendResult();

        using (var request = new UnityWebRequest(url, "POST"))
        {
            request.uploadHandler = new UploadHandlerRaw(payload);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.timeout = RequestTimeoutSeconds;

            request.SetRequestHeader("Content-Type", "application/octet-stream");
            request.SetRequestHeader("Content-Encoding", "gzip");
            request.SetRequestHeader("X-Hextech-Info", info);

            var operation = request.SendWebRequest();
            var startedAt = Time.realtimeSinceStartup;

            while (!operation.isDone && Time.realtimeSinceStartup - startedAt < RequestTimeoutSeconds + 5f)
            {
                progress?.Invoke($"正在上传日志… 已等 {Time.realtimeSinceStartup - startedAt:0} 秒");
                yield return new WaitForSecondsRealtime(1f);
            }

            if (!operation.isDone)
            {
                request.Abort();
                result.Error = $"请求超时（超过 {RequestTimeoutSeconds} 秒没回应）";
                result.Done = true;
                finished(result);
                yield break;
            }

            // 失败时服务器也会回一小段 JSON（{"ok":false,"error":"…"}），能读到就用它当提示。
            var body = request.downloadHandler?.text ?? string.Empty;

            if (request.result == UnityWebRequest.Result.Success)
            {
                result.Code = ReadJsonString(body, "code");

                if (string.IsNullOrEmpty(result.Code))
                {
                    result.Error = "服务器没返回编号";
                }
            }
            else
            {
                result.Error = ReadJsonString(body, "error") ?? request.error;
            }

            result.Done = true;
        }

        finished(result);
    }

    /// <summary>
    /// 等后台线程把请求做完，每秒报一次「已等几秒」。到点还没回来就走人（线程是后台线程，不会拖住游戏）——
    /// 但只在 <see cref="SendResult.Done"/> 还是 false 时才写超时原因，成功结果优先。
    /// </summary>
    private static IEnumerator WaitFor(SendResult result, Action<string>? progress)
    {
        var deadline = RequestTimeoutSeconds + 5f;
        var startedAt = Time.realtimeSinceStartup;

        while (!result.Done && Time.realtimeSinceStartup - startedAt < deadline)
        {
            progress?.Invoke($"正在上传日志… 已等 {Time.realtimeSinceStartup - startedAt:0} 秒");
            yield return new WaitForSecondsRealtime(1f);
        }

        if (!result.Done)
        {
            // 先写内容、后立 Done：Done 是 volatile，写它之后上面这一句别的主线程才一定看得见。
            result.Error = $"请求超时（超过 {deadline:0} 秒没回应）";
            result.Done = true;
        }
    }

    private static string DescribeWebException(WebException exception)
    {
        try
        {
            if (exception.Response is HttpWebResponse response)
            {
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    var body = reader.ReadToEnd();
                    var server = ReadJsonString(body, "error");

                    if (!string.IsNullOrEmpty(server))
                    {
                        return server;
                    }

                    return $"服务器返回 HTTP {(int)response.StatusCode}";
                }
            }
        }
        catch (Exception)
        {
            // 读不到响应体（连接就断在半路）时用下面的通用描述。
        }

        return exception.Status switch
        {
            WebExceptionStatus.Timeout => $"请求超时（超过 {RequestTimeoutSeconds} 秒没回应）",
            WebExceptionStatus.NameResolutionFailure => "域名解析失败",
            WebExceptionStatus.ConnectFailure => "连不上服务器（防火墙 / 网络）",
            _ => exception.Message,
        };
    }

    /// <summary>
    /// 上传失败时把报告写到 BepInEx 目录下，返回文件名（写不进去就返回 null）。
    /// 传得上去时不写 —— 免得每传一次就在玩家硬盘上留一份带昵称与本地路径的日志。
    /// </summary>
    private static string? SaveLocally(string report)
    {
        try
        {
            var path = Path.Combine(Paths.BepInExRootPath, "HextechMod-report.txt");
            File.WriteAllText(path, report, new UTF8Encoding(false));
            HextechPlugin.Log.LogInfo($"[上报] 报告已另存：{path}");
            return "HextechMod-report.txt";
        }
        catch (Exception exception)
        {
            HextechPlugin.Log.LogWarning($"[上报] 报告另存失败：{exception.Message}");
            return null;
        }
    }

    /// <summary>给服务器记 meta 用的一行摘要（自定义头，只能放 ASCII）。</summary>
    private static string BuildInfoHeader()
    {
        int players;

        try
        {
            players = PhotonNetwork.CurrentRoom?.PlayerCount ?? 0;
        }
        catch
        {
            players = 0;
        }

        return $"v{HextechPlugin.Version}|{SceneManager.GetActiveScene().name}|{players}p|master={(PhotonNetwork.IsMasterClient ? 1 : 0)}";
    }

    private static string BuildReport()
    {
        var builder = new StringBuilder(1 << 17);

        builder.AppendLine("# HextechMod 日志报告（玩家主动上传）");
        builder.AppendLine($"生成时间 : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"mod      : v{HextechPlugin.Version}");
        builder.AppendLine($"游戏     : v{Application.version}");
        builder.AppendLine($"Unity    : {Application.unityVersion}");
        builder.AppendLine($"平台     : {Application.platform}");
        builder.AppendLine($"存档目录 : {Application.persistentDataPath}");
        builder.AppendLine($"BepInEx  : {Paths.BepInExRootPath}");
        builder.AppendLine($"场景     : {SceneManager.GetActiveScene().name}");
        builder.AppendLine($"联机     : {DescribeNetwork()}");

        builder.AppendLine(
            $"日志取样 : 最近 {WindowMinutes} 分钟"
            + $"（两份日志都没有时间戳，靠 {Samples.Count} 个位置样本反推起点）");

        AppendLocalCharacter(builder);
        AppendAllCharacters(builder);
        AppendUsage(builder);
        AppendMechanisms(builder);
        AppendPatchHealth(builder);
        AppendConfig(builder);

        AppendLogTail(builder, "BepInEx 日志", BepInExLogPath, false, BepInExLogMaxBytes);
        AppendLogTail(builder, "Unity 日志（Player.log）", UnityLogPath, true, UnityLogMaxBytes);

        return builder.ToString();
    }

    private static string DescribeNetwork()
    {
        try
        {
            var room = PhotonNetwork.CurrentRoom;

            if (room == null)
            {
                return $"不在房间里（昵称={PhotonNetwork.NickName}）";
            }

            return $"昵称={PhotonNetwork.NickName} 人数={room.PlayerCount} 房主={(PhotonNetwork.IsMasterClient ? "是" : "否")}（房间名已省略）";
        }
        catch (Exception exception)
        {
            return $"(读取失败：{exception.Message})";
        }
    }

    private static void AppendLocalCharacter(StringBuilder builder)
    {
        Character? local = null;

        try
        {
            local = Character.localCharacter;
        }
        catch (Exception exception)
        {
            builder.AppendLine($"本机角色 : (读取失败：{exception.Message})");
            return;
        }

        if (local == null || local.data == null)
        {
            builder.AppendLine("本机角色 : (还没生成)");
            return;
        }

        var carried = local.data.carriedPlayer;

        builder.AppendLine(
            $"本机角色 : {local.characterName} 死亡={local.data.dead} "
            + $"完全昏迷={local.data.fullyPassedOut} 半昏迷={local.data.passedOut} "
            + $"被扛着={local.data.isCarried} 扛着={(carried == null ? "无" : carried.characterName)}");

        var state = HextechState.Get(local);

        if (state == null)
        {
            builder.AppendLine("海克斯   : (本机角色上没有状态组件)");
            return;
        }

        builder.AppendLine(
            $"海克斯   : 能量={state.Energy:0.#}/{HextechState.MaxEnergy:0.#} "
            + $"代币={HextechState.FormatTokens(state.Tokens)} 复活次数={state.ReviveCharges}");

        var owned = new List<string>();

        for (var i = 0; i < state.Owned.Count; i++)
        {
            var entry = state.Owned[i];
            owned.Add($"{entry.Id}×{state.StackOf(entry)}");
        }

        builder.AppendLine($"    词条 : {(owned.Count == 0 ? "(无)" : string.Join(" ", owned))}");

        var skills = new List<string>();

        foreach (var skill in state.Skills)
        {
            skills.Add(skill.ToString());
        }

        builder.AppendLine($"    技能 : {(skills.Count == 0 ? "(无)" : string.Join(" ", skills))}");
        builder.AppendLine($"    当前 : {state.CurrentSkill?.ToString() ?? "(无)"}");
    }

    /// <summary>
    /// 房间里<b>每个</b>角色各来一行。
    /// <para>
    /// 上面那节只有本机视角，但很多问题是别人身上的：队友拿到某条词条之后全场开始掉帧、
    /// 房主那侧的判定和客户端对不上、中途加入的人没拿到补足……只写本机等于把这些全漏掉。
    /// 词条那部分只有装了模组的客户端读得到（原版角色身上没有 <see cref="HextechState"/>），读不到就跳过。
    /// </para>
    /// </summary>
    private static void AppendAllCharacters(StringBuilder builder)
    {
        builder.AppendLine();
        builder.AppendLine("## 全房间角色");

        List<Character>? characters = null;

        try
        {
            characters = Character.AllCharacters;
        }
        catch (Exception exception)
        {
            builder.AppendLine($"(读取失败：{exception.Message})");
        }

        if (characters == null || characters.Count == 0)
        {
            builder.AppendLine("(还没有角色)");
            return;
        }

        Character? local = null;

        try
        {
            local = Character.localCharacter;
        }
        catch (Exception)
        {
            // 读不到本机角色也不影响列别人。
        }

        for (var i = 0; i < characters.Count; i++)
        {
            var character = characters[i];

            if (character == null || character.data == null)
            {
                continue;
            }

            var owner = character.refs != null && character.refs.view != null ? character.refs.view.Owner : null;

            builder.AppendLine(
                $"- {character.characterName}{(character == local ? "（本机）" : string.Empty)}"
                + $" actor={owner?.ActorNumber ?? -1}"
                + $" 场景={character.gameObject.scene.name}"
                + $" 位置={character.transform.position}"
                + $" 死亡={character.data.dead} 完全昏迷={character.data.fullyPassedOut} 半昏迷={character.data.passedOut}"
                + $" 僵尸={character.isZombie} 幽灵={character.IsGhost}");

            var state = HextechState.Get(character);

            if (state == null)
            {
                builder.AppendLine("    海克斯 : (这台机器上读不到他的状态 —— 对方大概没装模组)");
                continue;
            }

            var owned = new List<string>();

            for (var k = 0; k < state.Owned.Count; k++)
            {
                var entry = state.Owned[k];
                owned.Add($"{entry.Id}×{state.StackOf(entry)}");
            }

            builder.AppendLine(
                $"    能量={state.Energy:0.#}/{HextechState.MaxEnergy:0.#} "
                + $"代币={HextechState.FormatTokens(state.Tokens)} 复活次数={state.ReviveCharges}");
            builder.AppendLine($"    词条 : {(owned.Count == 0 ? "(无)" : string.Join(" ", owned))}");
        }
    }

    /// <summary>
    /// 各全局机制此刻的状态。这类问题（「防挂机怎么在那儿也触发」「价格对不上」）光看日志尾部看不出来，
    /// 得把每个子系统的开关 + 当时的数据一起拍下来。
    /// </summary>
    private static void AppendMechanisms(StringBuilder builder)
    {
        builder.AppendLine();
        builder.AppendLine("## 全局机制状态");

        builder.AppendLine(
            $"- 模组开关：{(ModConfig.Enabled.Value ? "开" : "关")}"
            + $"　阶段：{(MapHandler.Exists ? MapHandler.CurrentSegmentNumber.ToString() : "不在图里")}"
            + $"　本局已打：{(HextechManager.Instance?.RunElapsedSeconds ?? 0f):0} 秒");

        builder.AppendLine(
            $"- 篝火挂机惩罚：{(ModConfig.CampfireAfkGuard.Value ? "开" : "关")}"
            + $"（半径 {ModConfig.CampfireAfkRadius.Value:0.#} 米 / 时长 {ModConfig.CampfireAfkSeconds.Value:0} 秒）"
            + $" 计时：{CampfireZombieGuard.Describe()}");

        builder.AppendLine($"- 本机禁用：{HextechBans.Mine ?? "(没禁)"}　全房间禁用：{DescribeBans()}");
        builder.AppendLine(
            $"- 商店改价：{(ShopPricing.CanEdit ? "本机可以改" : "只有房主可以改")}，当前改了 {ShopPricing.Count} 件");
    }

    /// <summary>
    /// 每条词条本局的「用量」：效果落地了几次、累计影响了多少、被动在场多久。
    /// <para>
    /// 它答的是玩家那句「某条词条好像没生效 / 数值不对」—— 光看「拥有名单」看不出效果有没有跑，
    /// 而效果都是在**各自的机器**上结算的（本机只跑本机角色的词条），所以这里只有本机角色的统计。
    /// </para>
    /// <para>
    /// 带 <c>※</c> 的词条在代码里埋了事件计数（见 <see cref="EventCountedIds"/>），显示「0 次」
    /// 就是「本局一次都没生效」，那才是要查的；不带 <c>※</c> 的目前只统计在场时长
    /// —— 纯数值型词条在获得那一刻就把数值乘上去了，本身没有可计数的「触发」。
    /// </para>
    /// </summary>
    private static void AppendUsage(StringBuilder builder)
    {
        builder.AppendLine();
        builder.AppendLine("## 词条触发统计（本机 · 本局）");
        builder.AppendLine(
            "（效果各机器自己结算，所以只有本机角色的数据；「累计」的单位随词条 —— 代币 / 状态点 / 秒 / 数值，"
            + "只跟同一条词条纵向比，别跨词条相加）");

        var instances = HextechState.Instances;
        var usage = new List<HextechState.HextechUsage>();
        var fired = new List<HextechState.HextechUsage>();
        var silent = new List<HextechState.HextechUsage>();
        var passive = new List<HextechState.HextechUsage>();
        var wrote = false;

        for (var i = 0; i < instances.Count; i++)
        {
            var state = instances[i];

            // 别人的词条效果在对方机器上跑，这台机器上读到的只会是一串没有意义的 0，所以只列本机。
            if (state == null || state.Character == null || !state.Character.IsLocal)
            {
                continue;
            }

            usage.Clear();
            state.CollectUsage(usage);

            if (usage.Count == 0)
            {
                continue;
            }

            fired.Clear();
            silent.Clear();
            passive.Clear();

            for (var k = 0; k < usage.Count; k++)
            {
                var row = usage[k];

                if (row.Triggers > 0)
                {
                    fired.Add(row);
                }
                else if (Array.IndexOf(EventCountedIds, row.Entry.Id) >= 0)
                {
                    silent.Add(row);
                }
                else
                {
                    passive.Add(row);
                }
            }

            // 触发多的排前面 —— 报告是扫一眼用的，最该看的先出来。
            fired.Sort((a, b) => b.Triggers.CompareTo(a.Triggers));
            passive.Sort((a, b) => b.Seconds.CompareTo(a.Seconds));

            builder.AppendLine();
            builder.AppendLine($"角色 {state.Character.characterName}（本机）");

            if (fired.Count == 0)
            {
                builder.AppendLine("  (本局还没有任何一条词条留下触发记录)");
            }

            for (var k = 0; k < fired.Count; k++)
            {
                var row = fired[k];
                builder.AppendLine($"  [触发] {row.Entry.Title}×{row.Stacks} · {row.Triggers} 次 · 累计 {row.Amount:0.###}");
            }

            for (var k = 0; k < silent.Count; k++)
            {
                var row = silent[k];
                builder.AppendLine($"  [0 次] ※ {row.Entry.Title}×{row.Stacks} —— 埋了点，本局一次都没生效");
            }

            for (var k = 0; k < passive.Count && k < MaxPassiveUsageLines; k++)
            {
                var row = passive[k];
                builder.AppendLine($"  [被动] {row.Entry.Title}×{row.Stacks} · 在场 {row.Seconds:0.#} 秒（{row.Frames} 帧）");
            }

            if (passive.Count > MaxPassiveUsageLines)
            {
                builder.AppendLine($"  …另有 {passive.Count - MaxPassiveUsageLines} 条纯被动词条（在场时间更短，略）");
            }

            wrote = true;
        }

        if (!wrote)
        {
            builder.AppendLine();
            builder.AppendLine("(本机手上没有任何词条)");
        }
    }

    private static string DescribeBans()
    {
        var all = HextechBans.All;

        if (all.Count == 0)
        {
            return "(无)";
        }

        var parts = new List<string>(all.Count);

        foreach (var pair in all)
        {
            var name = "actor" + pair.Key;

            try
            {
                var player = PhotonNetwork.CurrentRoom?.GetPlayer(pair.Key);

                if (player != null)
                {
                    name = player.NickName;
                }
            }
            catch (Exception)
            {
                // 昵称读不到就用 actor 号，名单本身照样有用。
            }

            parts.Add($"{name}→{pair.Value}");
        }

        return string.Join("，", parts);
    }

    /// <summary>
    /// 补丁体检。Harmony 补丁是「某个功能整个没反应」的唯一常见原因，
    /// 而它失败时界面上完全看不出来（只在日志里有一条 error），所以这里专门列一节。
    /// </summary>
    private static void AppendPatchHealth(StringBuilder builder)
    {
        builder.AppendLine();
        builder.AppendLine("## 补丁体检（Harmony）");
        builder.AppendLine(
            $"补丁类：成功 {HextechPlugin.PatchedTypeCount} 个，失败 {HextechPlugin.PatchFailures.Count} 个");

        for (var i = 0; i < HextechPlugin.PatchFailures.Count; i++)
        {
            builder.AppendLine($"  X {HextechPlugin.PatchFailures[i]}");
        }
    }

    /// <summary>
    /// 直接把 .cfg 原文附上。BepInEx 的 <c>ConfigFile.Entries</c> 是 internal（拿不到），
    /// 而这份配置默认每次改动都会立刻落盘，读文件既简单、又和玩家在硬盘上看到的完全一致。
    /// </summary>
    private static void AppendConfig(StringBuilder builder)
    {
        builder.AppendLine();
        builder.AppendLine("## 模组配置（.cfg 原文）");

        var path = ModConfig.File.ConfigFilePath;
        builder.AppendLine($"路径：{path}");

        try
        {
            builder.AppendLine(File.ReadAllText(path, new UTF8Encoding(false, false)));
        }
        catch (Exception exception)
        {
            builder.AppendLine($"(读取失败：{exception.Message})");
        }
    }

    /// <summary>
    /// 追加一个日志文件里「最近 <see cref="WindowMinutes"/> 分钟」的那一段。
    /// <c>FileShare.ReadWrite</c> 是必须的 —— 这两个文件此刻正被游戏 / BepInEx 写着，独占打开会直接失败。
    /// <para>
    /// 起点取「窗口起点」与「文件尾往前 maxBytes」里更靠后的那个：正常情况按十分钟切，
    /// 遇到刷屏日志时由字节上限接管（报告里会写清楚是哪一种）。
    /// </para>
    /// </summary>
    private static void AppendLogTail(StringBuilder builder, string title, string path, bool unityLog, int maxBytes)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}（最近 {WindowMinutes} 分钟）");
        builder.AppendLine($"路径：{path}");

        try
        {
            var info = new FileInfo(path);

            if (!info.Exists)
            {
                builder.AppendLine("(文件不存在)");
                return;
            }

            var end = info.Length;
            var windowStart = WindowStartBytes(unityLog, end);
            var from = Math.Max(windowStart, end - maxBytes);
            var take = (int)Math.Min(end - from, int.MaxValue);

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                stream.Seek(from, SeekOrigin.Begin);

                var buffer = new byte[take];
                var read = ReadFully(stream, buffer, take);
                var skip = 0;

                // 从文件中间切进去时开头是半行，丢掉它。
                if (from > 0)
                {
                    while (skip < read && buffer[skip] != (byte)'\n')
                    {
                        skip++;
                    }

                    if (skip < read)
                    {
                        skip++;
                    }
                }

                var capped = from > windowStart ? "，十分钟的内容超过上限、只留了最后这一截" : string.Empty;

                builder.AppendLine($"(文件共 {end / 1024} KB；这里从第 {from / 1024} KB 读到 {end / 1024} KB，共 {read / 1024} KB{capped})");
                builder.AppendLine(new UTF8Encoding(false, false).GetString(buffer, skip, read - skip));
            }
        }
        catch (Exception exception)
        {
            builder.AppendLine($"(读取失败：{exception.Message})");
        }
    }

    /// <summary>流式读满（<c>Read</c> 一次不一定给全）。</summary>
    private static int ReadFully(Stream stream, byte[] buffer, int count)
    {
        var total = 0;

        while (total < count)
        {
            var read = stream.Read(buffer, total, count - total);

            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static byte[] Gzip(byte[] data)
    {
        using (var output = new MemoryStream())
        {
            using (var gzip = new GZipStream(output, CompressionMode.Compress, true))
            {
                gzip.Write(data, 0, data.Length);
            }

            return output.ToArray();
        }
    }

    /// <summary>
    /// 从响应里抠一个字符串字段。上报的响应就 <c>{"ok":true,"code":"…"}</c> / <c>{"ok":false,"error":"…"}</c>
    /// 两种，为它引一个 JSON 库不划算（和 <see cref="UpdateFeed"/> 里那份是同一个理由）。
    /// </summary>
    private static string? ReadJsonString(string json, string key)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        var token = "\"" + key + "\"";
        var start = json.IndexOf(token, StringComparison.Ordinal);

        if (start < 0)
        {
            return null;
        }

        var colon = json.IndexOf(':', start + token.Length);

        if (colon < 0)
        {
            return null;
        }

        var i = colon + 1;

        while (i < json.Length && char.IsWhiteSpace(json[i]))
        {
            i++;
        }

        if (i >= json.Length || json[i] != '"')
        {
            return null;
        }

        i++;
        var builder = new StringBuilder();

        while (i < json.Length && json[i] != '"')
        {
            if (json[i] == '\\' && i + 1 < json.Length)
            {
                i++;
            }

            builder.Append(json[i]);
            i++;
        }

        return builder.ToString();
    }
}
