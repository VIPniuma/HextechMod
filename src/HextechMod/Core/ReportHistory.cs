using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using UnityEngine;

namespace PeakModder.HextechMod;

/// <summary>一条本机上传记录：编号 + 上传时间 + 当时在哪个场景 + 压完多大。</summary>
internal sealed class ReportRecord
{
    public string Code = string.Empty;
    public DateTime Time;
    public string Scene = string.Empty;
    public int Kilobytes;
}

/// <summary>
/// 本机上传过的报告编号（存在 BepInEx 配置目录的 <c>HextechMod-reports.txt</c> 里，和 .cfg 挨着）。
/// <para>
/// 编号是玩家唯一要转达给作者的东西，但它只在「日志已上传」那个浮窗里显示一次，
/// 关掉之后就只能靠剪贴板 —— 而剪贴板会被别的东西顶掉（复制个房间码就没了）。
/// 所以本机留一份小账：设置面板里能翻、能再点一次「复制编号」。
/// </para>
/// <para>
/// 保留 <see cref="KeepHours"/> 小时，和服务端一致：服务端那份到点就删了，
/// 本机再留着这串数字只会让人以为还能取件。文件是一行一条的纯文本，
/// 读到坏行就跳过、整个解析不了就当作没有过 —— 这只是一份便利账，不值得为它挡住启动。
/// </para>
/// </summary>
internal static class ReportHistory
{
    /// <summary>保留多少小时。和服务端 <c>server/report.php</c> 的 <c>KEEP_HOURS</c> 对齐。</summary>
    public const int KeepHours = 48;

    /// <summary>本机最多记这么多条（够翻回这几局上传过的）。</summary>
    private const int MaxRecords = 12;

    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    private static readonly List<ReportRecord> Records = new();
    private static bool _loaded;

    /// <summary>从新到旧。内容改过之后 <see cref="Revision"/> 会变。</summary>
    public static IReadOnlyList<ReportRecord> All
    {
        get
        {
            EnsureLoaded();
            return Records;
        }
    }

    /// <summary>内容版本号：每次增删 +1（界面拿它判断要不要重算文字）。</summary>
    public static int Revision { get; private set; }

    private static string FilePath => Path.Combine(Paths.ConfigPath, "HextechMod-reports.txt");

    /// <summary>记一条新上传的记录（最新的排最前面），顺手把过期的清掉。</summary>
    public static void Add(string code, int kilobytes, string scene)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        EnsureLoaded();
        Prune();

        Records.Insert(0, new ReportRecord
        {
            Code = code,
            Time = DateTime.Now,
            Scene = string.IsNullOrWhiteSpace(scene) ? "未知场景" : scene,
            Kilobytes = Mathf.Max(0, kilobytes),
        });

        // 超上限就从尾巴上砍，文件不会无限长下去。
        if (Records.Count > MaxRecords)
        {
            Records.RemoveRange(MaxRecords, Records.Count - MaxRecords);
        }

        Revision++;
        Save();
    }

    /// <summary>
    /// 丢掉超过 48 小时的记录。设置面板每次打开都会调一次
    /// —— 不调的话列表里会留着「剩 0 小时」的编号，看着还能用其实早取不到了。
    /// </summary>
    public static void PruneExpired()
    {
        EnsureLoaded();

        if (Prune())
        {
            Revision++;
            Save();
        }
    }

    /// <summary>这条记录还有多久作废（服务端就是照这个点删的）。</summary>
    public static TimeSpan Remaining(ReportRecord record)
    {
        var left = TimeSpan.FromHours(KeepHours) - (DateTime.Now - record.Time);
        return left < TimeSpan.Zero ? TimeSpan.Zero : left;
    }

    /// <summary>
    /// 把编号写进系统剪贴板。失败只记一条日志 ——
    /// 剪贴板在某些环境（无窗口 / 无 X11）会直接抛异常，那不该影响玩家看到编号。
    /// </summary>
    public static void CopyToClipboard(string code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return;
        }

        try
        {
            GUIUtility.systemCopyBuffer = code;
        }
        catch (Exception exception)
        {
            HextechPlugin.Log.LogWarning($"[上报] 写剪贴板失败：{exception.Message}");
        }
    }

    /// <summary>过期就删，返回有没有删东西（调用方据此决定要不要落盘）。</summary>
    private static bool Prune()
    {
        return Records.RemoveAll(record => DateTime.Now - record.Time > TimeSpan.FromHours(KeepHours)) > 0;
    }

    private static void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        Load();
    }

    private static void Load()
    {
        Records.Clear();

        try
        {
            if (!File.Exists(FilePath))
            {
                return;
            }

            foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
            {
                var parts = line.Split('|');

                if (parts.Length < 4
                    || !DateTime.TryParseExact(parts[1], TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
                    || !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kilobytes))
                {
                    continue;
                }

                Records.Add(new ReportRecord
                {
                    Code = parts[0],
                    Time = time,
                    Scene = parts[2],
                    Kilobytes = kilobytes,
                });
            }
        }
        catch (Exception exception)
        {
            HextechPlugin.Log.LogWarning($"[上报] 读本机上传记录失败（当作没有过）：{exception.Message}");
            Records.Clear();
        }

        Prune();
    }

    private static void Save()
    {
        try
        {
            var builder = new StringBuilder();

            foreach (var record in Records)
            {
                builder.Append(Clean(record.Code)).Append('|')
                    .Append(record.Time.ToString(TimeFormat, CultureInfo.InvariantCulture)).Append('|')
                    .Append(Clean(record.Scene)).Append('|')
                    .Append(record.Kilobytes).Append('\n');
            }

            File.WriteAllText(FilePath, builder.ToString(), new UTF8Encoding(false));
        }
        catch (Exception exception)
        {
            // 写不进去（目录只读之类）只是「下次打开面板看不到记录」，不该往上抛。
            HextechPlugin.Log.LogWarning($"[上报] 写本机上传记录失败：{exception.Message}");
        }
    }

    /// <summary>分隔符和换行会把这一行读坏，落盘前一律换成下划线。</summary>
    private static string Clean(string text)
    {
        return text.Replace('|', '_').Replace('\n', '_').Replace('\r', '_');
    }
}
