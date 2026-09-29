using System;
using System.Collections;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace PeakModder.HextechMod;

/// <summary>
/// 服务器上 version.json 的内容。字段名和服务器上的文件一一对应，
/// 改一边记得改另一边（安装器里有一份等价的读取逻辑）。
/// </summary>
public sealed class UpdateInfo
{
    public string Version = string.Empty;

    /// <summary>发布时间，纯展示用，不做解析。</summary>
    public string Published = string.Empty;

    /// <summary>更新公告，可以带 \n 换行。</summary>
    public string Notes = string.Empty;

    /// <summary>dll 的文件名或完整地址。模组自己不再下载（更新交给安装器），保留解析以对齐服务端清单。</summary>
    public string Dll = string.Empty;

    /// <summary>dll 的 SHA256（小写十六进制）。为空表示不校验。同 <see cref="Dll"/>，现在只给安装器用。</summary>
    public string Sha256 = string.Empty;

    /// <summary>true = 强制更新（「忽略此版本」不生效）。</summary>
    public bool Force;
}

/// <summary>
/// 版本清单的拉取与解析。
///
/// 这里刻意只依赖内置的 UnityWebRequest 和手写解析：manifest 就固定那几个字段，
/// 为了它去引一个 JSON 库（Unity 下还要担心 IL2CPP 裁剪）不划算。
/// </summary>
public static class UpdateFeed
{
    /// <summary>
    /// 兜底更新源。构建时若没注入 <c>HextechUpdateFeedUrl</c>（比如本地 dev 构建、或
    /// <c>Config.Build.user.props</c> 没配），就用这个地址照常查版本 —— 免得版本检测因为
    /// 「没注入地址」就整段静默跳过（玩家本地跑自己编的 dll 时收不到任何提示）。
    /// 生产构建会通过程序集元数据注入同一个地址覆盖它，所以两者指向同一份 version.json。
    /// </summary>
    private const string FallbackManifestUrl = "http://<你的服务器>/version.json";

    /// <summary>
    /// 内置更新源。优先用构建时注入的程序集元数据（值来自被 gitignore 的 Config.Build.user.props，
    /// 见 Directory.Build.props）；没注入就退回 <see cref="FallbackManifestUrl"/>，
    /// 玩家也能在配置里用「更新源地址」覆盖。永远不会是空串，版本检查始终会跑。
    /// </summary>
    public static string DefaultManifestUrl { get; } = PickManifestUrl();

    private static string PickManifestUrl()
    {
        var injected = ReadInjectedManifestUrl();
        return injected.Length > 0 ? injected : FallbackManifestUrl;
    }

    private static string ReadInjectedManifestUrl()
    {
        foreach (var attribute in typeof(UpdateFeed).Assembly.GetCustomAttributes(typeof(AssemblyMetadataAttribute), false))
        {
            if (attribute is AssemblyMetadataAttribute meta
                && string.Equals(meta.Key, "HextechUpdateFeedUrl", StringComparison.Ordinal))
            {
                return (meta.Value ?? string.Empty).Trim();
            }
        }

        return string.Empty;
    }

    private const int RequestTimeoutSeconds = 10;

    /// <summary>
    /// 共享的 .NET HTTP 客户端。
    /// <para>
    /// 刻意<b>不用</b> UnityWebRequest：PEAK 打包时「Allow downloads over HTTP」处于禁止状态，
    /// 明文 http 的请求在 UnityWebRequest 里直接抛
    /// <c>InvalidOperationException: Insecure connection not allowed</c>（且只进 Player.log，不进 BepInEx 日志）——
    /// 2026-09-16 的版本检测就是这么「静默挂掉」的。HttpClient 走 .NET 自己的网络栈，不受这条限制。
    /// UA 标成 HextechMod/&lt;版本&gt;，服务器日志里一眼就能认出游戏客户端的请求。
    /// </para>
    /// </summary>
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds + 20),
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd($"HextechMod/{HextechPlugin.Version}");
        return client;
    }

    public static string ManifestUrl
    {
        get
        {
            var configured = ModConfig.UpdateFeedUrl.Value;

            return string.IsNullOrWhiteSpace(configured) ? DefaultManifestUrl : configured.Trim();
        }
    }

    /// <summary>远端版本是不是比本地新。任何一边解析不出来都当作「不新」，宁可不打扰玩家。</summary>
    public static bool IsNewer(string remote, string local)
    {
        return Version.TryParse(remote, out var r) && Version.TryParse(local, out var l) && r > l;
    }

    /// <summary>
    /// 拉取清单。失败（断网、服务器挂了、格式不对）一律回调 null，由调用方决定怎么兜底 ——
    /// 这个功能不能影响正常进游戏。
    /// </summary>
    public static IEnumerator Fetch(Action<UpdateInfo?> done)
    {
        var url = ManifestUrl;

        if (string.IsNullOrWhiteSpace(url))
        {
            // 本地构建时没注入更新源，跳过就行 —— 这不是错误。
            HextechPlugin.Log.LogInfo("[更新] 没有配置更新源，跳过版本检查。");
            done(null);
            yield break;
        }

        // 请求在后台线程跑（HttpClient 同步等会卡住主线程），协程每帧只看它完没完成。
        // 用字节流 + 显式 UTF-8 解码：清单无 BOM，服务器响应头也未必标 charset，
        // 交给 HTTP 库按默认猜编码的话，更新公告的中文会变成乱码。
        byte[]? payload = null;
        string? error = null;

        var task = Task.Run(async () =>
        {
            try
            {
                payload = await Http.GetByteArrayAsync(url);
            }
            catch (Exception e)
            {
                error = e.Message;
            }
        });

        while (!task.IsCompleted)
        {
            yield return null;
        }

        if (payload == null)
        {
            // 刻意不带 url：日志常被玩家贴出来求助，没必要顺手把服务器地址也发出去。
            HextechPlugin.Log.LogInfo($"[更新] 版本清单获取失败（忽略）：{error}");
            done(null);
            yield break;
        }

        var body = Encoding.UTF8.GetString(payload);
        UpdateInfo? info = null;

        try
        {
            info = Parse(body);
        }
        catch (Exception e)
        {
            HextechPlugin.Log.LogWarning($"[更新] 版本清单解析失败：{e.Message}");
        }

        if (info == null || info.Version.Length == 0)
        {
            HextechPlugin.Log.LogWarning("[更新] 版本清单里没有 version 字段，按「没有更新」处理。");
            done(null);
            yield break;
        }

        done(info);
    }

    /// <summary>清单同目录下的文件地址：dll 就摆在 version.json 旁边，拼一下就行。</summary>
    public static string SiblingUrl(string fileName)
    {
        var base_ = ManifestUrl;
        var cut = base_.LastIndexOf('/');

        return (cut >= 0 ? base_.Substring(0, cut + 1) : string.Empty) + fileName;
    }

    /// <summary>
    /// 下载新版 dll（游戏内自更新用）。成功回调整份文件内容（清单里带 SHA256 时先校验再回调），
    /// 失败回调 null —— 由调用方弹提示。下载进度通过 <paramref name="progress"/> 逐帧上报（0~1）。
    /// </summary>
    public static IEnumerator DownloadDll(UpdateInfo info, Action<float> progress, Action<byte[]?> done)
    {
        var url = SiblingUrl(info.Dll);

        if (string.IsNullOrWhiteSpace(info.Dll) || string.IsNullOrWhiteSpace(url))
        {
            done(null);
            yield break;
        }

        // 流式下载才有进度条可画。下载进度在后台线程里记成浮点数，
        // 协程每帧取最新值回调 —— Unity 的 UI 只能在主线程动，不能让后台线程直接碰。
        byte[]? data = null;
        string? error = null;
        var downloaded = 0f;

        var task = Task.Run(async () =>
        {
            try
            {
                using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);

                if (!response.IsSuccessStatusCode)
                {
                    error = $"HTTP {(int)response.StatusCode}";
                    return;
                }

                await using var stream = await response.Content.ReadAsStreamAsync();
                using var buffer = new MemoryStream();
                var chunk = new byte[65536];
                var total = response.Content.Headers.ContentLength ?? -1;
                int read;

                while ((read = await stream.ReadAsync(chunk, 0, chunk.Length)) > 0)
                {
                    buffer.Write(chunk, 0, read);

                    if (total > 0)
                    {
                        downloaded = Mathf.Clamp01(buffer.Length / (float)total);
                    }
                }

                data = buffer.ToArray();
            }
            catch (Exception e)
            {
                error = e.Message;
            }
        });

        while (!task.IsCompleted)
        {
            progress(downloaded);
            yield return null;
        }

        progress(1f);

        if (data == null || data.Length == 0)
        {
            HextechPlugin.Log.LogWarning($"[更新] 新版 dll 下载失败（忽略）：{error ?? "内容为空"}");
            done(null);
            yield break;
        }

        if (info.Sha256.Length > 0 && !MatchesSha256(data, info.Sha256))
        {
            HextechPlugin.Log.LogWarning("[更新] 新版 dll 的 SHA256 和清单对不上，拒绝安装。");
            done(null);
            yield break;
        }

        done(data);
    }

    /// <summary>内容的 SHA256 和清单里的十六进制串是否一致（比较不看大小写）。</summary>
    private static bool MatchesSha256(byte[] data, string expected)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(data);
        var builder = new StringBuilder(hash.Length * 2);

        foreach (var b in hash)
        {
            builder.Append(b.ToString("x2"));
        }

        return string.Equals(builder.ToString(), expected.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static UpdateInfo? Parse(string json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        var info = new UpdateInfo
        {
            Version = ReadString(json, "version") ?? string.Empty,
            Published = ReadString(json, "published") ?? string.Empty,
            Notes = ReadString(json, "notes") ?? string.Empty,
            Dll = ReadString(json, "dll") ?? string.Empty,
            Sha256 = ReadString(json, "sha256") ?? string.Empty,
            Force = ReadBool(json, "force"),
        };

        return info;
    }

    private static string? ReadString(string json, string key)
    {
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

        while (i < json.Length)
        {
            var c = json[i];

            if (c == '\\' && i + 1 < json.Length)
            {
                i++;

                builder.Append(json[i] switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    'b' => '\b',
                    'f' => '\f',
                    _ => json[i],
                });
            }
            else if (c == '"')
            {
                break;
            }
            else
            {
                builder.Append(c);
            }

            i++;
        }

        return builder.ToString();
    }

    private static bool ReadBool(string json, string key)
    {
        var token = "\"" + key + "\"";
        var start = json.IndexOf(token, StringComparison.Ordinal);

        if (start < 0)
        {
            return false;
        }

        var colon = json.IndexOf(':', start + token.Length);

        if (colon < 0)
        {
            return false;
        }

        return json.Substring(colon + 1).TrimStart().StartsWith("true", StringComparison.OrdinalIgnoreCase);
    }
}
