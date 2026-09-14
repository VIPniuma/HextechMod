using System;
using System.Collections;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

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
    /// 内置更新源。真实地址不写进源码 —— 它由构建时注入的程序集元数据提供
    /// （值来自本地被 gitignore 的 Config.Build.user.props，见 Directory.Build.props），
    /// 玩家也可以在配置里覆盖。没注入时是空串，版本检查直接跳过，不影响正常游玩。
    /// </summary>
    public static string DefaultManifestUrl { get; } = ReadInjectedManifestUrl();

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

        using (var request = UnityWebRequest.Get(url))
        {
            request.timeout = RequestTimeoutSeconds;

            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                // 刻意不带 url：日志常被玩家贴出来求助，没必要顺手把服务器地址也发出去。
                HextechPlugin.Log.LogInfo($"[更新] 版本清单获取失败（忽略）：{request.error}");
                done(null);
                yield break;
            }

            UpdateInfo? info = null;

            try
            {
                info = Parse(request.downloadHandler.text);
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
