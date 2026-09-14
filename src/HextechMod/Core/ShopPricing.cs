using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using Photon.Pun;

namespace PeakModder.HextechMod;

/// <summary>
/// 商店「单品调价」表：按商品 id 覆盖它自己的基础价，和全局的物价倍率叠加。
/// <para>
/// 只有单人局和多人房的房主能改价（<see cref="CanEdit"/>）：联机时价格是全房间统一的事，
/// 让每个客户端各改各的就会出现「我这边 10 枚、你那边 45 枚」。房主改完会广播一份整表，
/// 中途进房的人则主动向房主问一次（和禁用名单同一套做法，见 <see cref="HextechState"/>）。
/// </para>
/// <para>
/// 表存在 <c>BepInEx/config/HextechMod.prices.txt</c>，每行「id 制表符 价格」。
/// 故意放在单独的文件里：它可能有几十上百行，塞进 .cfg 会让配置面板顶出一大坨看不懂的字符串。
/// </para>
/// </summary>
internal static class ShopPricing
{
    private const string FileName = "HextechMod.prices.txt";

    /// <summary>价格下限：0 会让「白送」把商店刷空，没有意义。</summary>
    private const int MinPrice = 1;

    /// <summary>价格上限：防手滑敲出个六位数把玩家自己的家底掏空。</summary>
    private const int MaxPrice = 999;

    private static readonly Dictionary<string, int> Overrides = new(StringComparer.Ordinal);

    private static bool _loaded;

    /// <summary>
    /// 当前能不能改价：单人（没进房间）随便改，联机房里只有房主说了算。
    /// </summary>
    public static bool CanEdit => !PhotonNetwork.InRoom || PhotonNetwork.IsMasterClient;

    /// <summary>已经改了价的商品件数。</summary>
    public static int Count
    {
        get
        {
            EnsureLoaded();
            return Overrides.Count;
        }
    }

    private static string FilePath
    {
        get
        {
            var directory = Paths.ConfigPath;

            if (string.IsNullOrEmpty(directory))
            {
                directory = Path.Combine(AppContext.BaseDirectory, "config");
            }

            return Path.Combine(directory, FileName);
        }
    }

    /// <summary>这件商品被改过的价；没改过返回 null（用它的默认基础价）。</summary>
    public static int? OverrideFor(string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        EnsureLoaded();

        return Overrides.TryGetValue(id, out var price) ? price : null;
    }

    /// <summary>改一件商品的价并广播给全房间（单人局只写盘）。</summary>
    public static void Set(string id, int price, HextechState? state)
    {
        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        EnsureLoaded();

        price = Math.Clamp(price, MinPrice, MaxPrice);

        if (Overrides.TryGetValue(id, out var current) && current == price)
        {
            return;
        }

        Overrides[id] = price;
        Save();
        state?.BroadcastPrices();
    }

    /// <summary>把一件商品恢复成默认价。</summary>
    public static void Reset(string id, HextechState? state)
    {
        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        EnsureLoaded();

        if (!Overrides.Remove(id))
        {
            return;
        }

        Save();
        state?.BroadcastPrices();
    }

    /// <summary>全部恢复默认价。</summary>
    public static void ResetAll(HextechState? state)
    {
        EnsureLoaded();

        if (Overrides.Count == 0)
        {
            return;
        }

        Overrides.Clear();
        Save();
        state?.BroadcastPrices();
    }

    /// <summary>
    /// 整表替换（切价格预设用）：先清空再灌入 <paramref name="prices"/>；
    /// 传 null / 空集合 = 全部恢复默认价。
    /// <para>
    /// 和 <see cref="Set"/> 一样落盘 + 广播 —— 预设是玩家自己这份配置，要跟着写进 prices.txt。
    /// </para>
    /// </summary>
    public static void Replace(IReadOnlyDictionary<string, int>? prices, HextechState? state)
    {
        EnsureLoaded();

        Overrides.Clear();

        if (prices != null)
        {
            foreach (var pair in prices)
            {
                if (!string.IsNullOrEmpty(pair.Key))
                {
                    Overrides[pair.Key] = Math.Clamp(pair.Value, MinPrice, MaxPrice);
                }
            }
        }

        Save();
        state?.BroadcastPrices();
    }

    /// <summary>打包成 RPC 能传的两个数组。</summary>
    public static (string[] Ids, int[] Prices) Snapshot()
    {
        EnsureLoaded();

        var ids = new string[Overrides.Count];
        var prices = new int[Overrides.Count];
        var index = 0;

        foreach (var pair in Overrides)
        {
            ids[index] = pair.Key;
            prices[index] = pair.Value;
            index++;
        }

        return (ids, prices);
    }

    /// <summary>
    /// 收到房主发来的整表。只改内存不落盘 —— 那是房主的定价，
    /// 不该被客户端抄进自己的配置文件里（换房时就会拿着上一个房主的价格开局）。
    /// </summary>
    public static void ApplyRemote(string[]? ids, int[]? prices)
    {
        if (ids == null || prices == null)
        {
            return;
        }

        Overrides.Clear();

        for (var i = 0; i < ids.Length && i < prices.Length; i++)
        {
            if (string.IsNullOrEmpty(ids[i]))
            {
                continue;
            }

            Overrides[ids[i]] = Math.Clamp(prices[i], MinPrice, MaxPrice);
        }

        _loaded = true;
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
        try
        {
            var path = FilePath;

            if (!File.Exists(path))
            {
                return;
            }

            foreach (var line in File.ReadAllLines(path))
            {
                var separator = line.IndexOf('\t');

                if (separator <= 0)
                {
                    continue;
                }

                var id = line.Substring(0, separator).Trim();
                var text = line.Substring(separator + 1).Trim();

                if (id.Length == 0 || !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var price))
                {
                    continue;
                }

                Overrides[id] = Math.Clamp(price, MinPrice, MaxPrice);
            }

            if (Overrides.Count > 0)
            {
                HextechPlugin.Log.LogInfo($"已读取 {Overrides.Count} 条商店单品调价。");
            }
        }
        catch (Exception exception)
        {
            // 读不出来最多是价格回到默认，不该影响进游戏。
            HextechPlugin.Log.LogWarning($"读取商店调价表失败，这次全部用默认价：{exception.Message}");
        }
    }

    private static void Save()
    {
        try
        {
            var path = FilePath;
            var directory = Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var builder = new StringBuilder();

            // 排一下序只是让文件看起来整齐，改完价格方便肉眼对比。
            var ids = new List<string>(Overrides.Keys);
            ids.Sort(StringComparer.Ordinal);

            foreach (var id in ids)
            {
                builder.Append(id).Append('\t')
                    .Append(Overrides[id].ToString(CultureInfo.InvariantCulture))
                    .Append('\n');
            }

            File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
        }
        catch (Exception exception)
        {
            HextechPlugin.Log.LogWarning($"保存商店调价表失败（这次改动重启后会丢）：{exception.Message}");
        }
    }
}
