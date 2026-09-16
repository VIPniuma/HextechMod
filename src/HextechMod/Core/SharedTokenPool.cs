using System;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace PeakModder.HextechMod;

/// <summary>
/// 共享代币池（2026-09-16 新增，默认关，见 <see cref="ModConfig.SharedTokens"/>）。
/// <para>
/// 开启后，代币不再按人各自积累，而是变成一个「全房间共享」的池子：
/// 只在房主侧按「房间人数」的速率往里加，再广播给所有人；任何人花钱都从同一个池子扣。
/// </para>
/// <para>
/// 同步用 Photon 自定义事件（不需要 PhotonView）：
///   - BalanceEvent：房主 → 其他人，携带当前余额（float，单位 = 代币，与 HextechState._tokens 一致）。
///   - SpendEvent：普通客户端 → 房主，携带本次花费（int，单位 = 代币）；房主扣完后用 BalanceEvent 回广播。
/// 事件码用 122/123（落在 Photon 留给用户自定义的 0–199 区间，避开 200+ 的 PUN/服务器保留码）。
/// </para>
/// </summary>
internal static class SharedTokenPool
{
    private const byte BalanceEvent = 122;
    private const byte SpendEvent = 123;

    /// <summary>共享池余额，单位 = 代币（和 HextechState._tokens 一致）。</summary>
    public static float Balance { get; private set; }

    public static event Action? OnChanged;

    public static bool Active => ModConfig.SharedTokens.Value;

    private static float _lastBroadcast;

    public static void Tick(float dt)
    {
        // 商店关了 = 代币功能整体关掉（2026-09-16）：共享池也不涨。
        if (!ModConfig.ShopEnabled.Value || !Active)
        {
            return;
        }

        // 余额只在房主侧滚动；其他人靠广播同步（0.25 秒一次，足够顺滑，也兜底网络抖动）。
        if (PhotonNetwork.IsMasterClient)
        {
            Balance += ComputeRate(PhotonNetwork.PlayerList.Length) * dt / 60f;

            if (Time.time - _lastBroadcast > 0.25f)
            {
                _lastBroadcast = Time.time;
                Broadcast();
            }
        }
    }

    /// <summary>获取速率（代币 / 分钟）：基础 2，每多 2 名玩家 +1。</summary>
    public static float ComputeRate(int players)
    {
        if (players < 1)
        {
            players = 1;
        }

        return 2f + Mathf.Floor((players - 1) / 2f);
    }

    /// <summary>花掉代币。返回是否成功扣下（余额不足返回 false 且不扣钱）。</summary>
    public static bool TrySpend(int cost)
    {
        if (!Active)
        {
            return false;
        }

        if (!PhotonNetwork.InRoom)
        {
            // 单人 / 没进房：本地就是权威。
            if (Balance < cost)
            {
                return false;
            }

            Balance -= cost;
            OnChanged?.Invoke();
            return true;
        }

        if (PhotonNetwork.IsMasterClient)
        {
            if (Balance < cost)
            {
                return false;
            }

            Balance -= cost;
            Broadcast();
            OnChanged?.Invoke();
            return true;
        }

        // 普通客户端：先乐观扣本地展示值，再请房主正式扣权威池（房主扣完会广播回来覆盖，二者一致）。
        if (Balance < cost)
        {
            return false;
        }

        Balance -= cost;
        OnChanged?.Invoke();

        var options = new RaiseEventOptions { Receivers = ReceiverGroup.MasterClient };
        PhotonNetwork.RaiseEvent(SpendEvent, cost, options, SendOptions.SendReliable);
        return true;
    }

    /// <summary>一回机场（新的一局）把池子清空。</summary>
    public static void Reset()
    {
        Balance = 0f;
        _lastBroadcast = 0f;
        OnChanged?.Invoke();
    }

    /// <summary>Photon 自定义事件入口，注册在 <see cref="HextechManager"/> 的 Awake / OnDestroy。</summary>
    public static void OnEvent(EventData data)
    {
        if (data.Code == BalanceEvent)
        {
            Balance = (float)data.CustomData;
            OnChanged?.Invoke();
        }
        else if (data.Code == SpendEvent && PhotonNetwork.IsMasterClient)
        {
            var cost = (int)data.CustomData;

            if (Balance >= cost)
            {
                Balance -= cost;
                Broadcast();
            }
        }
    }

    private static void Broadcast()
    {
        var options = new RaiseEventOptions { Receivers = ReceiverGroup.Others };
        PhotonNetwork.RaiseEvent(BalanceEvent, Balance, options, SendOptions.SendReliable);
    }
}
