using System;
using BepInEx.Configuration;
using Photon.Pun;
using UnityEngine;

namespace PeakModder.HextechMod;

/// <summary>
/// 房主权威的「全房间统一设置」。
/// <para>
/// 背景：商店开关、代币速率、物价倍率、功能溢价、开箱概率、传说权重、共享代币这几项
/// 原本是**每人各读各的本地 .cfg** —— 房主在自己机器上关掉商店，客户端那边照样开着，
/// 于是出现「房主打不开商店、队友却能买」「我这边 10 枚、你那边 45 枚」这类不一致，
/// 概率类（开箱 / 传说权重）同理：每人按自己的数抽，队友看到的出货完全不是一回事。
/// </para>
/// <para>
/// 做法和「禁用名单 / 单品调价」完全同一套：房主改任何一项就广播整份快照，
/// 中途进房（含掉线重连）的人主动向房主要一次，拿不到就隔几秒重试。
/// 客户端**只改内存、绝不写进自己的 .cfg** —— 换房时不该带着上一个房主的设置开局，
/// 离开房间时再把自己原来的值还回去（见 <see cref="Tick"/>）。
/// </para>
/// </summary>
internal static class HostSettings
{
    private const int FlagShopEnabled = 1 << 0;
    private const int FlagSharedTokens = 1 << 1;

    /// <summary>浮点项：[0] 每分钟代币 [1] 物价倍率 [2] 功能溢价 [3] 正面概率 [4] 负面概率 [5] 出代币概率。</summary>
    private const int ValueCount = 6;

    /// <summary>整数项：[0] 代币上限 [1] 传说权重。</summary>
    private const int IntCount = 2;

    private static bool _subscribed;
    private static bool _remoteApplied;
    private static bool _dirty;

    private static int _localFlags;
    private static float[]? _localValues;
    private static int[]? _localInts;

    /// <summary>客户端侧：当前生效的是房主下发的值（不是本机 .cfg）。</summary>
    public static bool RemoteApplied => _remoteApplied;

    /// <summary>房主侧：自己的设置改过、还没广播出去（等一个拿得到 PhotonView 的时机再发）。</summary>
    public static bool IsDirty => _dirty;

    public static void ClearDirty() => _dirty = false;

    /// <summary>
    /// 订阅这几项的变更事件：房主在 F9 面板 / BepInEx 配置管理器 / 价格预设里改任意一项都会走到这里。
    /// <para>
    /// 用事件而不是在面板里埋点，是为了把「配置管理器里改的」「预设切的」这些入口一起兜住 ——
    /// 漏一个入口就会出现「房主改了、队友没跟上」。
    /// 客户端收到广播时改值也会触发事件，但它不是房主，不会记账。
    /// </para>
    /// </summary>
    public static void Init()
    {
        if (_subscribed)
        {
            return;
        }

        _subscribed = true;

        Subscribe(ModConfig.ShopEnabled);
        Subscribe(ModConfig.SharedTokens);
        Subscribe(ModConfig.TokensPerMinute);
        Subscribe(ModConfig.ShopPriceMultiplier);
        Subscribe(ModConfig.ShopFunctionPremium);
        Subscribe(ModConfig.LuggagePositiveChance);
        Subscribe(ModConfig.LuggageNegativeChance);
        Subscribe(ModConfig.LuggageTokenChance);
        Subscribe(ModConfig.LuggageTokenMax);
        Subscribe(ModConfig.LegendaryWeight);
    }

    // SettingChanged 挂在泛型 ConfigEntry<T> 上（基类 ConfigEntryBase 没有），所以这里走泛型。
    private static void Subscribe<T>(ConfigEntry<T> entry)
    {
        entry.SettingChanged += (_, _) =>
        {
            if (PhotonNetwork.InRoom && PhotonNetwork.IsMasterClient)
            {
                _dirty = true;
            }
        };
    }

    /// <summary>打包当前生效值（房主用它广播，客户端拿它做「被覆盖前的备份」）。</summary>
    public static (int Flags, float[] Values, int[] Ints) Snapshot()
    {
        var flags = 0;

        if (ModConfig.ShopEnabled.Value)
        {
            flags |= FlagShopEnabled;
        }

        if (ModConfig.SharedTokens.Value)
        {
            flags |= FlagSharedTokens;
        }

        var values = new float[ValueCount];
        values[0] = ModConfig.TokensPerMinute.Value;
        values[1] = ModConfig.ShopPriceMultiplier.Value;
        values[2] = ModConfig.ShopFunctionPremium.Value;
        values[3] = ModConfig.LuggagePositiveChance.Value;
        values[4] = ModConfig.LuggageNegativeChance.Value;
        values[5] = ModConfig.LuggageTokenChance.Value;

        var ints = new int[IntCount];
        ints[0] = ModConfig.LuggageTokenMax.Value;
        ints[1] = ModConfig.LegendaryWeight.Value;

        return (flags, values, ints);
    }

    /// <summary>收到房主下发的整份设置。只改内存 —— 绝不落盘（理由见类注释）。</summary>
    public static void ApplyRemote(int flags, float[]? values, int[]? ints)
    {
        // 房主自己就是权威，不套别人的，也顺手挡掉回声。
        if (PhotonNetwork.IsMasterClient)
        {
            return;
        }

        if (!_remoteApplied)
        {
            var backup = Snapshot();
            _localFlags = backup.Flags;
            _localValues = backup.Values;
            _localInts = backup.Ints;
        }

        Write(flags, values, ints);
        _remoteApplied = true;

        HextechPlugin.Log.LogInfo("[海克斯] 已套用房主的房间设置（商店 / 代币 / 物价 / 概率以房主为准）。");
    }

    /// <summary>把一组值写进 ModConfig 的各条目（全房间统一的那几项）。</summary>
    private static void Write(int flags, float[]? values, int[]? ints)
    {
        // 关键：套用房主的值时**不能让它落进本机 .cfg**。BepInEx 的 ConfigFile 默认
        // SaveOnConfigSet = true（一赋值就写盘），不临时关掉的话，玩家退出联机去打单机
        // 会带着上一个房主的商店开关 / 概率，还以为是自己改坏了。
        var file = ModConfig.File;
        var saveOnSet = file != null && file.SaveOnConfigSet;

        if (file != null)
        {
            file.SaveOnConfigSet = false;
        }

        try
        {
            ModConfig.ShopEnabled.Value = (flags & FlagShopEnabled) != 0;
            ModConfig.SharedTokens.Value = (flags & FlagSharedTokens) != 0;

            if (values != null && values.Length >= ValueCount)
            {
                ModConfig.TokensPerMinute.Value = Mathf.Clamp(values[0], 0f, 100f);
                ModConfig.ShopPriceMultiplier.Value = Mathf.Clamp(values[1], 0f, 10f);

                // 功能溢价在 .cfg 里限定 0~2，房主传过来的也按同一个区间夹住。
                ModConfig.ShopFunctionPremium.Value = Mathf.Clamp(values[2], 0f, 2f);
                // 同样走 NormalizeChance：房主那边按百分数填的（2.5 = 2.5%）别在客户端被夹成 100%。
                ModConfig.LuggagePositiveChance.Value = ModConfig.NormalizeChance(values[3]);
                ModConfig.LuggageNegativeChance.Value = ModConfig.NormalizeChance(values[4]);
                ModConfig.LuggageTokenChance.Value = ModConfig.NormalizeChance(values[5]);
            }

            if (ints != null && ints.Length >= IntCount)
            {
                // 代币上限硬顶 20（和 .cfg 说明一致），往大了传不会生效。
                ModConfig.LuggageTokenMax.Value = Mathf.Clamp(ints[0], 0, 20);
                ModConfig.LegendaryWeight.Value = Mathf.Clamp(ints[1], 0, 100);
            }
        }
        finally
        {
            if (file != null)
            {
                file.SaveOnConfigSet = saveOnSet;
            }
        }
    }

    /// <summary>
    /// 每帧调一次：离开房间之后把房主的设置还回本机值 ——
    /// 否则玩家退出联机去打单机，会一直带着上一个房主的商店开关。
    /// </summary>
    public static void Tick()
    {
        if (_remoteApplied && !PhotonNetwork.InRoom)
        {
            RestoreLocal();
        }
    }

    private static void RestoreLocal()
    {
        _remoteApplied = false;
        Write(_localFlags, _localValues, _localInts);
        _localValues = null;
        _localInts = null;

        HextechPlugin.Log.LogInfo("[海克斯] 已离开房间：房间设置还原为本机配置。");
    }
}
