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

    /// <summary>房主扣不动（余额不够）时，把这笔退给发起者的回执。</summary>
    private const byte RefundEvent = 124;

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
            // 自定义事件的负载别硬转：类型不对 / 为 null 时宁可丢一条余额广播，
            // 也别在 Photon 的事件回调里抛异常（会把整条事件处理链炸掉）。
            if (data.CustomData is float balance)
            {
                Balance = balance;
                OnChanged?.Invoke();
            }
        }
        else if (data.Code == SpendEvent && PhotonNetwork.IsMasterClient)
        {
            if (data.CustomData is not int cost)
            {
                return;
            }

            if (Balance >= cost)
            {
                Balance -= cost;
                Broadcast();
            }
            else
            {
                // 扣不动（多半是两个人同一瞬间买了同一样东西）：把这笔退给发起者。
                // 不退的话他那边的余额已经扣了、东西也到手了 —— 白嫖 + 余额跳变（2026-09-20 排查）。
                Deny(data.Sender, cost);
            }
        }
        else if (data.Code == RefundEvent)
        {
            if (data.CustomData is int refund)
            {
                Balance += refund;
                OnChanged?.Invoke();
                HextechHud.Toast(Localization.T("共享代币不足 · 本次花费已退回 {0}", refund));
            }
        }
    }

    /// <summary>把一笔扣不动的话费退回给发起者（只发给他一个人）。</summary>
    private static void Deny(int actorNumber, int cost)
    {
        try
        {
            var options = new RaiseEventOptions { TargetActors = new[] { actorNumber } };
            PhotonNetwork.RaiseEvent(RefundEvent, cost, options, SendOptions.SendReliable);
        }
        catch (Exception exception)
        {
            HextechPlugin.Log.LogWarning($"[海克斯] 退回共享代币失败：{exception.Message}");
        }
    }

    private static void Broadcast()
    {
        var options = new RaiseEventOptions { Receivers = ReceiverGroup.Others };
        PhotonNetwork.RaiseEvent(BalanceEvent, Balance, options, SendOptions.SendReliable);
    }
}
