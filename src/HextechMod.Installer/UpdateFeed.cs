using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PeakModder.HextechInstaller;

/// <summary>
/// 服务器上 version.json 的内容。和 mod 端 <c>UpdateFeed</c> 读的是同一份文件，
/// 字段名改一边就要改另一边。
/// </summary>
internal sealed class RemoteRelease
{
    public string Version = string.Empty;

    public string Published = string.Empty;

    public string Notes = string.Empty;

    /// <summary>展示名（日志与进度条用）。version.json 没有名字字段，仓库条目会给。</summary>
    public string Name = "模组";

    /// <summary>dll 的文件名或完整地址。</summary>
    public string DllUrl = string.Empty;

    /// <summary>dll 的 SHA256（十六进制）。为空表示不校验。</summary>
    public string Sha256 = string.Empty;

    public bool Force;
}

/// <summary>
/// 联网取最新版模组。
///
/// 安装器本身不再内置 dll，这里就是模组本体的唯一来源：先拉 version.json，
/// 再按清单里的地址下载 dll 并校验 SHA256。服务器连不上就装不了 —— 错误要说明白。
/// </summary>
internal static class UpdateFeed
{
    /// <summary>
    /// 更新源地址。真实地址不写进源码 —— 构建时由本地 Config.Build.user.props 的
    /// <c>HextechUpdateFeedUrl</c> 注入（见 Directory.Build.props）；没配就是空串，直接跳过联网。
    /// </summary>
    public static string DefaultManifestUrl { get; } = ReadInjectedManifestUrl();

    private static string ReadInjectedManifestUrl()
    {
        foreach (var attribute in typeof(UpdateFeed).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false))
        {
            if (attribute is System.Reflection.AssemblyMetadataAttribute meta
                && string.Equals(meta.Key, "HextechUpdateFeedUrl", StringComparison.Ordinal))
            {
                return (meta.Value ?? string.Empty).Trim();
            }
        }

        return string.Empty;
    }

    private const int ManifestTimeoutSeconds = 10;
    private const int DownloadTimeoutMinutes = 5;

    /// <summary>远端版本是不是比本地新。任何一边解析不出来都当作「不新」。</summary>
    public static bool IsNewer(string remote, string local)
    {
        return Version.TryParse(remote, out var r) && Version.TryParse(local, out var l) && r > l;
    }

    /// <summary>
    /// 把文本里的更新源地址抹掉。
    ///
    /// 异常消息在部分平台上会把请求的主机名带出来，而日志恰恰是最常被玩家截图 / 粘贴
    /// 出来求助的东西，所以写日志前统一过一道，服务器地址不从日志里漏出去。
    /// </summary>
    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)
            || !Uri.TryCreate(DefaultManifestUrl, UriKind.Absolute, out var uri)
            || string.IsNullOrEmpty(uri.Host))
        {
            return text;
        }

        // 整条地址和单独的主机名都可能出现，两个都换掉。
        return text
            .Replace(DefaultManifestUrl, "<更新源>")
            .Replace(uri.Host, "<更新源>");
    }

    /// <summary>拉取版本清单。失败一律返回 null，由调用方决定怎么报错。</summary>
    public static async Task<RemoteRelease?> TryFetchAsync(CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(DefaultManifestUrl))
        {
            // 本地构建时没注入更新源：没有清单就没有 mod 本体可装（安装器不再内置 dll）。
            return null;
        }

        using (var client = CreateClient(TimeSpan.FromSeconds(ManifestTimeoutSeconds)))
        using (var response = await client.GetAsync(DefaultManifestUrl, token).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var release = Parse(json);

            if (release == null || release.Version.Length == 0)
            {
                throw new InvalidOperationException("版本清单里没有 version 字段。");
            }

            return release;
        }
    }

    /// <summary>下载清单里指的那个 dll，并按清单里的 SHA256 校验。</summary>
    public static async Task<byte[]> DownloadAsync(
        RemoteRelease release,
        IProgress<InstallProgress>? progress,
        CancellationToken token)
    {
        var url = ResolveUrl(release.DllUrl);

        using (var client = CreateClient(TimeSpan.FromMinutes(DownloadTimeoutMinutes)))
        using (var response = await client
                   .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token)
                   .ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? -1L;
            byte[] bytes;

            using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var memory = new System.IO.MemoryStream(total > 0 ? (int)total : 0))
            {
                var buffer = new byte[81920];
                long received = 0;
                int count;

                while ((count = await source.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
                {
                    await memory.WriteAsync(buffer, 0, count, token).ConfigureAwait(false);
                    received += count;

                    var text = total > 0
                        ? $"下载模组 v{release.Version}… {received * 100 / total}%"
                        : $"下载模组 v{release.Version}…";

                    progress?.Report(new InstallProgress(text, total > 0 ? 0.1 + (0.8 * received / total) : 0.4));
                }

                bytes = memory.ToArray();
            }

            if (bytes.Length == 0)
            {
                throw new InvalidOperationException("服务器返回了空文件。");
            }

            if (!string.IsNullOrWhiteSpace(release.Sha256))
            {
                var actual = Sha256Hex(bytes);

                if (!string.Equals(actual, release.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("下载的模组校验不通过（SHA256 不一致），已丢弃。");
                }
            }

            return bytes;
        }
    }

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        // net48 默认不一定开 TLS 1.2；万一以后换成 https 源，这里得有。
        System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;

        var handler = new HttpClientHandler { AllowAutoRedirect = true };
        var client = new HttpClient(handler) { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("HextechModInstaller/1.0");

        return client;
    }

    /// <summary>dll 字段可以写文件名（相对清单所在目录），也可以写完整地址。</summary>
    internal static string ResolveUrl(string dll)
    {
        var name = string.IsNullOrWhiteSpace(dll) ? InstallEngine.ModFileName : dll.Trim();

        if (name.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return name;
        }

        var cut = DefaultManifestUrl.LastIndexOf('/');

        return cut < 0 ? DefaultManifestUrl + "/" + name : DefaultManifestUrl.Substring(0, cut + 1) + name;
    }

    /// <summary>
    /// 清单所在目录里的另一个文件（如 repository.json）的地址。
    /// 没配更新源时返回 null —— 仓库页此时显示「离线」。
    /// </summary>
    internal static string? SiblingUrl(string fileName)
    {
        if (string.IsNullOrWhiteSpace(DefaultManifestUrl))
        {
            return null;
        }

        var cut = DefaultManifestUrl.LastIndexOf('/');

        return cut < 0
            ? fileName
            : DefaultManifestUrl.Substring(0, cut + 1) + fileName;
    }

    private static RemoteRelease? Parse(string json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        return new RemoteRelease
        {
            Version = ReadString(json, "version") ?? string.Empty,
            Published = ReadString(json, "published") ?? string.Empty,
            Notes = ReadString(json, "notes") ?? string.Empty,
            DllUrl = ReadString(json, "dll") ?? string.Empty,
            Sha256 = ReadString(json, "sha256") ?? string.Empty,
            Force = ReadBool(json, "force"),
        };
    }

    internal static string? ReadString(string json, string key)
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

                switch (json[i])
                {
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    default: builder.Append(json[i]); break;
                }
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

    internal static bool ReadBool(string json, string key)
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

    private static string Sha256Hex(byte[] bytes)
    {
        using (var sha = SHA256.Create())
        {
            var hash = sha.ComputeHash(bytes);
            var builder = new StringBuilder(hash.Length * 2);

            foreach (var b in hash)
            {
                builder.Append(b.ToString("x2"));
            }

            return builder.ToString();
        }
    }
}
