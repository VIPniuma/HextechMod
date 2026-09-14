using System;
using System.Reflection;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace PeakModder.HextechMod;

/// <summary>
/// 统一的「生成物资」入口。只有房主能往世界里实例化物品，
/// 所以非房主（开箱者 / 商店买家）把请求转给房主，房主再在同样的位置生成。
/// <para>
/// 商店买到的物资不再摆在地上：房主实例化出来之后立刻走原版 <c>Item.RequestPickup</c>
/// （内部是 <c>Player.AddItem</c> + 同步全房间 + 通知买家 + 销毁地上那个物件），
/// 直接进买家的物品栏 —— 和玩家自己弯腰捡起来的结果完全一样。
/// 地上那套会滚、会被队友顺手捡走、会滚进地形，玩家经常以为「买了没给」。
/// 物品栏满了才会退回「掉在地上」，这时照样能自己捡。
/// </para>
/// </summary>
internal static class HextechSpawning
{
    /// <summary>原版 <c>Spawner.InitializePhysics</c>，用来复刻正常的掉落初始化。</summary>
    private static readonly MethodInfo? InitializePhysics = AccessTools.Method(typeof(Spawner), "InitializePhysics");

    /// <summary>
    /// 请求生成一批物资。非房主会自动转给房主。
    /// <paramref name="giveTo"/> 不为空时，房主生成后直接把物品塞给这个人（商店购买用）。
    /// </summary>
    public static void RequestSpawn(Spawner? source, string[] names, Vector3[] positions, bool kinematic, Character? giveTo = null)
    {
        if (names == null || names.Length == 0 || positions == null || positions.Length != names.Length)
        {
            return;
        }

        if (PhotonNetwork.IsMasterClient || !PhotonNetwork.InRoom)
        {
            Spawn(source, names, positions, kinematic, giveTo);
            return;
        }

        var local = Character.localCharacter;
        var view = local == null || local.refs == null ? null : local.refs.view;

        if (view == null)
        {
            return;
        }

        var sourceView = source == null ? null : source.GetComponent<PhotonView>();
        var sourceViewId = sourceView == null ? -1 : sourceView.ViewID;

        // 收货人就是本地下单的那个玩家，房主按 ViewID 找回它的 Character。
        var receiverView = giveTo == null || giveTo.refs == null ? null : giveTo.refs.view;

        view.RPC(
            nameof(HextechState.HextechRPC_SpawnLoot),
            RpcTarget.MasterClient,
            names,
            positions,
            kinematic,
            sourceViewId,
            receiverView == null ? -1 : receiverView.ViewID);
    }

    /// <summary>真正实例化物品，只能在房主上调用。</summary>
    public static void Spawn(Spawner? source, string[] names, Vector3[] positions, bool kinematic, Character? giveTo = null)
    {
        for (var i = 0; i < names.Length; i++)
        {
            var go = PhotonNetwork.InstantiateItemRoom(names[i], positions[i], Quaternion.identity);

            if (go == null)
            {
                // 预制体池找不到这个名字时只会返回 null。原版 Spawner 里的名字都是配好的，
                // 我们是从 ItemDatabase 里挑的，所以这里必须留一条日志 ——
                // 不然玩家只会看到「已购买」但什么都没有，完全无从排查。
                HextechPlugin.Log.LogWarning($"生成物资失败：预制体池里没有 \"0_Items/{names[i]}\"。");
                continue;
            }

            var item = go.GetComponent<Item>();

            if (item == null)
            {
                HextechPlugin.Log.LogWarning($"生成物资失败：\"{names[i]}\" 的实例上没有 Item 组件。");
                continue;
            }

            if (source != null && InitializePhysics != null)
            {
                // 走原版初始化：同步物理状态，并按 Spawner 的配置决定是否设为运动学。
                InitializePhysics.Invoke(source, new object[] { item });
            }
            else
            {
                // 不是 Spawner 生成的（商店购买 / 空投补给）：对齐原版 Spawner.InitializePhysics ——
                // 先让它强制同步几帧物理数据（否则远端拿不到这个新物件的位置），
                // 再显式同步一次 SetKinematic：物品预制体默认是运动学状态，
                // 不这么做的话买下来的东西会悬在半空不掉下来。
                item.ForceSyncForFrames(10);

                go.GetComponent<PhotonView>()?.RPC(
                    "SetKinematicRPC",
                    RpcTarget.AllBuffered,
                    kinematic,
                    positions[i],
                    Quaternion.identity);
            }

            if (giveTo != null && Give(item, giveTo))
            {
                // 已经进物品栏了，地上这个物件原版会自己销毁。
                continue;
            }
        }
    }

    /// <summary>
    /// 把刚生成的物品直接交给某个角色（房主侧）。
    /// <para>
    /// 用原版 <c>Item.RequestPickup</c> 而不是自己调 <c>Player.AddItem</c>：
    /// 它除了加物品，还要给买家客户端发 <c>OnPickupAccepted</c>（手上 / 物品栏的 UI 和装备动作都靠它），
    /// 最后把地上这个房间物件销毁掉。物品栏没有空位时它走 <c>DenyPickupRPC</c>，
    /// 东西留在原地，玩家照样能自己捡。
    /// </para>
    /// </summary>
    /// <returns>塞成功返回 true；false 表示物品留在原地。</returns>
    private static bool Give(Item item, Character receiver)
    {
        try
        {
            var view = receiver.refs == null ? null : receiver.refs.view;

            if (view == null)
            {
                return false;
            }

            item.RequestPickup(view);
            return true;
        }
        catch (Exception exception)
        {
            HextechPlugin.Log.LogWarning($"物资直接交付失败，改为掉在地上：{exception.Message}");
            return false;
        }
    }
}
