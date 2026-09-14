using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace PeakModder.HextechInstaller;

/// <summary>
/// 仓库里的一个模组条目。
/// <para>
/// 主模组（海克斯 HEX）来自 version.json —— 那是发版脚本每次都更新的权威清单，
/// 仓库页直接复用它，版本永远不会和游戏内的更新检查打架。
/// 其余模组来自同目录的 <c>repository.json</c>，想上架新模组时把 dll 传到服务器、
/// 往那份清单的 mods 数组里加一条即可，安装器不用改代码。
/// </para>
/// </summary>
internal sealed class RepoMod
{
    /// <summary>稳定标识（卸载、清单按它记）。主模组固定 "hextechmod"。</summary>
    public string Id = string.Empty;

    /// <summary>展示名。</summary>
    public string Name = string.Empty;

    /// <summary>服务器上的版本号。</summary>
    public string Version = string.Empty;

    public string Published = string.Empty;

    public string Notes = string.Empty;

    /// <summary>dll 的文件名（相对清单目录）或完整地址。</summary>
    public string Dll = string.Empty;

    /// <summary>SHA256（十六进制），空表示不校验。</summary>
    public string Sha256 = string.Empty;

    /// <summary>是否主模组（来自 version.json 的那一条）。</summary>
    public bool Main;

    /// <summary>下载用的远端描述（复用 UpdateFeed 的下载与校验管线）。</summary>
    public RemoteRelease ToRelease()
    {
        return new RemoteRelease
        {
            Name = string.IsNullOrWhiteSpace(Name) ? Id : Name,
            Version = Version,
            Published = Published,
            Notes = Notes,
            DllUrl = Dll,
            Sha256 = Sha256,
        };
    }
}

/// <summary>
/// 模组仓库：version.json（主模组）+ repository.json（其它模组）合并成一份列表。
/// 两个清单任何一边拉不到都不挡另一边 —— 仓库页宁可少列几条，也不要整个白屏。
/// </summary>
internal static class ModRepository
{
    public const string MainModId = "hextechmod";
    public const string MainModName = "海克斯 HEX";

    private const string RepositoryFileName = "repository.json";
    private const int TimeoutSeconds = 10;

    /// <summary>拉仓库列表。失败的那一路在 out 参数里带回原因，界面写一行日志即可。</summary>
    public static async Task<List<RepoMod>> FetchAsync(CancellationToken token)
    {
        var result = new List<RepoMod>();

        // 1) 主模组：version.json（与游戏内更新检查同源）。
        RemoteRelease? main = null;

        try
        {
            main = await UpdateFeed.TryFetchAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // 主清单拉不到就不列主模组；原因由调用方在整体失败时说，这里保持安静。
        }

        if (main != null && !string.IsNullOrWhiteSpace(main.Version))
        {
            result.Add(new RepoMod
            {
                Id = MainModId,
                Name = MainModName,
                Version = main.Version,
                Published = main.Published,
                Notes = main.Notes,
                Dll = main.DllUrl,
                Sha256 = main.Sha256,
                Main = true,
            });
        }

        // 2) 其它模组：repository.json（可选，现在还是空的，以后上架新模组往里加）。
        var repositoryUrl = UpdateFeed.SiblingUrl(RepositoryFileName);

        if (repositoryUrl != null)
        {
            try
            {
                using (var client = CreateClient())
                using (var response = await client.GetAsync(repositoryUrl, token).ConfigureAwait(false))
                {
                    // 404 = 服务器上还没放这份清单，不算错误。
                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        result.AddRange(ParseRepository(json));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // 附加清单坏了不影响主模组，仓库页少几条而已。
            }
        }

        return result;
    }

    /// <summary>
    /// 解析 repository.json。格式（字段都可缺省，缺了用兜底值）：
    /// <code>
    /// { "updated": "2026-09-14", "mods": [
    ///   { "id": "example", "name": "示例模组", "version": "1.0.0",
    ///     "dll": "Example.dll", "sha256": "…", "notes": "一句话说明", "published": "2026-09-14" }
    /// ] }
    /// </code>
    /// </summary>
    internal static List<RepoMod> ParseRepository(string json)
    {
        var result = new List<RepoMod>();

        if (string.IsNullOrEmpty(json))
        {
            return result;
        }

        var modsIndex = json.IndexOf("\"mods\"", StringComparison.Ordinal);

        if (modsIndex < 0)
        {
            return result;
        }

        var arrayStart = json.IndexOf('[', modsIndex);

        if (arrayStart < 0)
        {
            return result;
        }

        // 简单 JSON 没有对象解析器（net48 又不想为几个字段拉 Newtonsoft），
        // 这里按 "{}" 配对切出每个对象，再交给 UpdateFeed 的字符串读取器逐字段取。
        var depth = 0;
        int? objectStart = null;

        for (var i = arrayStart; i < json.Length; i++)
        {
            var c = json[i];

            if (c == '{')
            {
                if (depth == 0)
                {
                    objectStart = i;
                }

                depth++;
            }
            else if (c == '}')
            {
                depth--;

                if (depth == 0 && objectStart.HasValue)
                {
                    var entry = ParseEntry(json.Substring(objectStart.Value, i - objectStart.Value + 1));

                    if (entry != null)
                    {
                        result.Add(entry);
                    }

                    objectStart = null;
                }
            }
        }

        return result;
    }

    private static RepoMod? ParseEntry(string json)
    {
        var id = UpdateFeed.ReadString(json, "id");
        var dll = UpdateFeed.ReadString(json, "dll");
        var version = UpdateFeed.ReadString(json, "version");

        // net48 的引用程序集没有 [NotNullWhen] 注解，IsNullOrWhiteSpace 过了编译器也不认，
        // 这里先把空值归一成空串再判断。
        var safeId = (id ?? string.Empty).Trim();
        var safeDll = (dll ?? string.Empty).Trim();

        if (safeId.Length == 0 || safeDll.Length == 0)
        {
            // 没有 id / dll 的条目没法安装，直接跳过 —— 宁可少列，也别列一个点不动的卡片。
            return null;
        }

        return new RepoMod
        {
            Id = safeId,
            Name = (UpdateFeed.ReadString(json, "name") ?? safeId).Trim(),
            Version = (version ?? "0").Trim(),
            Published = (UpdateFeed.ReadString(json, "published") ?? string.Empty).Trim(),
            Notes = UpdateFeed.ReadString(json, "notes") ?? string.Empty,
            Dll = safeDll,
            Sha256 = (UpdateFeed.ReadString(json, "sha256") ?? string.Empty).Trim(),
        };
    }

    private static HttpClient CreateClient()
    {
        System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;

        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromSeconds(TimeoutSeconds),
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("HextechModInstaller/1.0");
        return client;
    }
}
