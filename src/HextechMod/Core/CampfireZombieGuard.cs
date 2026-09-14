using System;
using System.Collections.Generic;
using Photon.Pun;
using UnityEngine;

namespace PeakModder.HextechMod;

/// <summary>
/// 防挂机：在点着的篝火附近「站桩」太久，就叫一只蘑菇僵尸来追你。
/// <para>
/// 判定放在<b>房主</b>这一侧：只有房主能往世界里实例化网络物体，而且这样不依赖挂机那个人
/// 自己的客户端在正常跑。每帧遍历 <c>Character.AllCharacters</c>，谁在任意一个点着的篝火半径内
/// 就把「待在圈里的时间」往上累，出了圈按同样的速度往回退（退到 0 为止）；攒满阈值就把他附近生成一只原版蘑菇僵尸，
/// 并用原版自己的广播 RPC 把僵尸锁死在这名玩家身上。
/// </para>
/// <para>
/// 生成方式刻意照抄原版 <c>MushroomZombieSpawner.Spawn()</c>：
/// <c>PhotonNetwork.Instantiate(预制体名, 位置, 朝向, 0, null)</c>。僵尸的 <c>Awake</c> 会自己
/// <c>ZombieManager.RegisterZombie</c>、<c>Start</c> 会自己进入「埋在蘑菇里」的沉睡状态、
/// 醒来后自己追人 —— 我们只负责把它放在玩家旁边、再强行指定目标。
/// </para>
/// </summary>
internal static class CampfireZombieGuard
{
    /// <summary>召唤点离玩家最近 / 最远多少米：别贴脸生成，也别远到它自己都不醒。</summary>
    private const float SpawnMinDistance = 5f;
    private const float SpawnMaxDistance = 11f;

    /// <summary>查不到预制体上的「唤醒距离」时按这个算（原版僵尸基本都在这个量级内）。</summary>
    private const float FallbackWakeDistance = 15f;

    /// <summary>强制锁定目标的时长（秒）：这段时间里这只僵尸只认这名玩家，不会中途改追别人。</summary>
    private const float TargetForceSeconds = 180f;

    /// <summary>召唤点没探到地面时，相对玩家高度往上抬多少（免得直接生在玩家脚下的地形里）。</summary>
    private const float SpawnFallbackHeight = 1f;

    /// <summary>
    /// 每名玩家「净在篝火圈里待了多久」，只在房主侧维护，按角色的网络 ViewID 记。
    /// 在圈里累加、出圈按同样的速度往回退，退到 0 就把这条清掉 —— 所以这惩罚打的是「净站着不动」，
    /// 短暂出圈再回来是接得上的（反过来说：只出去一下就回来，几乎等于没出去）。
    /// </summary>
    private static readonly Dictionary<int, float> Loiter = new();

    /// <summary>遍历 <see cref="Loiter"/> 时收集要删掉的 key（不能边遍历边削）。</summary>
    private static readonly List<int> StaleKeys = new();

    /// <summary>召唤用的蘑菇僵尸预制体名（见 <see cref="ResolvePrefab"/>），找到一次就记下来。</summary>
    private static string? _prefabName;

    /// <summary>预制体上报的「玩家进到这个距离内就唤醒」——召唤点必须落在它里面，不然僵尸会一直睡。</summary>
    private static float _prefabWakeDistance = FallbackWakeDistance;

    /// <summary>「这张图上找不到蘑菇僵尸预制体」这条日志只写一次，免得每三分钟刷一条。</summary>
    private static bool _warnedMissingPrefab;

    /// <summary>由 <see cref="HextechManager"/> 每帧调用。</summary>
    public static void Tick(float deltaTime)
    {
        if (!ModConfig.CampfireAfkGuard.Value
            || deltaTime <= 0f
            || HextechScene.InAirport
            || !PhotonNetwork.InRoom
            || !PhotonNetwork.IsMasterClient)
        {
            return;
        }

        var radius = ModConfig.CampfireAfkRadius.Value;
        var threshold = ModConfig.CampfireAfkSeconds.Value;

        if (radius <= 0f || threshold <= 0f)
        {
            return;
        }

        var characters = Character.AllCharacters;

        if (characters == null || characters.Count == 0)
        {
            Loiter.Clear();
            return;
        }

        for (var i = 0; i < characters.Count; i++)
        {
            var character = characters[i];

            if (!IsEligible(character))
            {
                if (character != null)
                {
                    Loiter.Remove(Key(character));
                }

                continue;
            }

            var key = Key(character!);
            var waited = Loiter.TryGetValue(key, out var stored) ? stored : 0f;
            var campfire = FindLoiterCampfire(character!, radius);

            if (campfire == null)
            {
                // 出了圈不倒扣到 0 就丢、也不是清零，而是按同样的速度**往回退**：打的是「净站在圈里的时间」。
                // 偶尔出去拿个东西、绕一圈再回来能接上；真一直站着不动的人退到 0 就没了。
                waited -= deltaTime;

                if (waited <= 0f)
                {
                    Loiter.Remove(key);
                }
                else
                {
                    Loiter[key] = waited;
                }

                continue;
            }

            waited += deltaTime;

            if (waited < threshold)
            {
                Loiter[key] = waited;
                continue;
            }

            // 到点了，计时归零：继续挂着就每过这么久再来一只。
            Loiter[key] = 0f;
            Summon(character!, campfire);
        }

        Prune(characters);
    }

    /// <summary>一局结束（回机场）时清空计时，下一局重新数。</summary>
    public static void Reset()
    {
        Loiter.Clear();
    }

    /// <summary>
    /// 给「上传日志」那份报告用：现在谁在篝火圈里累计了多久。
    /// 计时表只在房主侧维护，所以非房主这里只能说明「判定不在这台机器上」。
    /// </summary>
    public static string Describe()
    {
        if (!PhotonNetwork.InRoom)
        {
            return "（不在房间里）";
        }

        if (!PhotonNetwork.IsMasterClient)
        {
            return "（本机不是房主：计时表在房主那台机器上）";
        }

        if (Loiter.Count == 0)
        {
            return "（当前没人在篝火圈里累计）";
        }

        var parts = new List<string>(Loiter.Count);

        foreach (var pair in Loiter)
        {
            var view = PhotonNetwork.GetPhotonView(pair.Key);

            var who = view == null
                ? "已离场"
                : (view.Owner != null ? view.Owner.NickName : view.name);

            parts.Add($"{who}(ViewID {pair.Key}) {pair.Value:0.0}s");
        }

        return string.Join("，", parts);
    }

    /// <summary>只盯活人：死了 / 已经完全昏迷 / 已经是僵尸的不算，观战幽灵也不算。</summary>
    private static bool IsEligible(Character? character)
    {
        if (character == null
            || character.data == null
            || character.refs == null
            || character.refs.view == null)
        {
            return false;
        }

        return !character.data.dead
            && !character.data.fullyPassedOut
            && !character.isZombie
            && !character.IsGhost;
    }

    /// <summary>
    /// 找出玩家此刻正「待在圈里」的那只篝火；没有就返回 null。
    /// <para>
    /// 三个条件必须同时成立：
    /// ① 玩家自己不在机场场景里 —— 机场既不是一局、也没有挂机惩罚这回事；
    /// ② 篝火和玩家在<b>同一张 Unity 场景</b>里。这条是 2026-09-12 玩家报「在机场那边被招僵尸、
    /// 旁边根本没有篝火」之后补的：机场那张场景在跑图时可能还挂着，里面的篝火坐标正好压在上机 / 落地
    /// 那一片，只比距离、不分场景就会把人误判成「站在篝火边」；
    /// ③ 篝火是<b>真在烧</b>的。游戏里 <c>Lit</c> 的口径是 <c>state == Lit || forceLit</c>，
    /// 而 <c>forceLit</c> 的装饰篝火（机场那类）根本不冒火苗，玩家不会围着它挂机 —— 所以这里看 <c>state</c>。
    /// </para>
    /// </summary>
    private static Campfire? FindLoiterCampfire(Character character, float radius)
    {
        var scene = character.gameObject.scene;

        if (!scene.IsValid() || HextechScene.IsAirportScene(scene.name))
        {
            return null;
        }

        var campfires = AdvancedHextechs.Campfires();
        var center = character.Center;

        for (var i = 0; i < campfires.Count; i++)
        {
            var campfire = campfires[i];

            if (campfire == null
                || !campfire.isActiveAndEnabled
                || campfire.gameObject.scene != scene
                || campfire.state != Campfire.FireState.Lit)
            {
                continue;
            }

            if (Vector3.Distance(center, campfire.Center()) <= radius)
            {
                return campfire;
            }
        }

        return null;
    }

    /// <summary>把已经不在这一局里的人（退出 / 换角色 / 重连换了新 ViewID）从计时表里清掉。</summary>
    private static void Prune(List<Character> characters)
    {
        if (Loiter.Count == 0)
        {
            return;
        }

        StaleKeys.Clear();

        foreach (var pair in Loiter)
        {
            var alive = false;

            for (var i = 0; i < characters.Count; i++)
            {
                var character = characters[i];

                if (character != null && Key(character) == pair.Key)
                {
                    alive = true;
                    break;
                }
            }

            if (!alive)
            {
                StaleKeys.Add(pair.Key);
            }
        }

        for (var i = 0; i < StaleKeys.Count; i++)
        {
            Loiter.Remove(StaleKeys[i]);
        }
    }

    private static int Key(Character character)
    {
        var view = character.refs == null ? null : character.refs.view;

        return view != null ? view.ViewID : character.GetInstanceID();
    }

    /// <summary>
    /// 在目标玩家附近召唤一只蘑菇僵尸，并把它强制锁在目标身上。
    /// 非房主上不该走到这里（<see cref="Tick"/> 已经拦过），原版也是房主生成、房主当 owner。
    /// </summary>
    private static void Summon(Character target, Campfire campfire)
    {
        var targetView = target.refs == null ? null : target.refs.view;

        if (targetView == null)
        {
            return;
        }

        // 留一条日志：万一再出现「站在没有篝火的地方被招僵尸」，这条能直接指出是哪只篝火、隔了多远。
        var who = targetView.Owner != null ? targetView.Owner.NickName : "?";
        HextechPlugin.Log.LogInfo(
            $"篝火挂机惩罚：{who}（ViewID {targetView.ViewID}）在「{campfire.gameObject.name}」"
            + $"（场景 {campfire.gameObject.scene.name}，相隔 {Vector3.Distance(target.Center, campfire.Center()):0.0} 米）"
            + $"边站满 {ModConfig.CampfireAfkSeconds.Value:0} 秒，召唤蘑菇僵尸。");

        if (!ResolvePrefab())
        {
            if (!_warnedMissingPrefab)
            {
                _warnedMissingPrefab = true;
                HextechPlugin.Log.LogWarning(
                    "篝火挂机惩罚：这张图上找不到蘑菇僵尸预制体（没有僵尸生成器、内存里也没有它的预制体），本次跳过。");
            }

            return;
        }

        GameObject? spawned;

        try
        {
            spawned = PhotonNetwork.Instantiate(_prefabName!, PickSpawnPosition(target), Quaternion.identity, 0, null);
        }
        catch (Exception exception)
        {
            HextechPlugin.Log.LogWarning($"篝火挂机惩罚：生成蘑菇僵尸失败（{exception.Message}）");
            return;
        }

        if (spawned == null)
        {
            HextechPlugin.Log.LogWarning($"篝火挂机惩罚：预制体池里没有 \"{_prefabName}\"，这次没生成出来。");
            return;
        }

        var zombieView = spawned.GetComponent<PhotonView>();

        if (zombieView == null)
        {
            return;
        }

        // 锁定目标走原版自己的广播 RPC（内部就是 set_currentTarget + targetForcedUntil）；
        // 不用 protected 的 SetCurrentTarget：那条只在「目标变了」时才转发，而且得反射调用。
        // 广播的意义在于各端的目标一致，不会出现「每台机器各追一个最近的人」。
        zombieView.RPC("RPCA_SetCurrentTarget", RpcTarget.All, targetView.ViewID, TargetForceSeconds);

        if (target == Character.localCharacter)
        {
            HextechHud.Toast($"在篝火边站太久（{ModConfig.CampfireAfkSeconds.Value:0} 秒）—— 蘑菇僵尸被招来了");
        }
    }

    /// <summary>
    /// 召唤点：玩家周围随机方向、按预制体的唤醒距离挑一个合适的距离，并尽量贴着地面。
    /// </summary>
    private static Vector3 PickSpawnPosition(Character target)
    {
        var center = target.Center;

        // 必须落在僵尸的唤醒距离内，否则它会一直在土里睡着、玩家白等。
        var maxDistance = Mathf.Clamp(_prefabWakeDistance * 0.6f, SpawnMinDistance, SpawnMaxDistance);
        var distance = UnityEngine.Random.Range(SpawnMinDistance, Mathf.Max(SpawnMinDistance, maxDistance));

        var angle = UnityEngine.Random.value * Mathf.PI * 2f;
        var position = center + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * distance;

        // 从上方垂直打一条射线贴地：斜坡 / 台阶 / 树干旁边都不会把僵尸生在半空或塞进地形里。
        if (Physics.Raycast(
                position + Vector3.up * 12f,
                Vector3.down,
                out var hit,
                40f,
                Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore))
        {
            position = hit.point + Vector3.up * 0.5f;
        }
        else
        {
            position.y = center.y + SpawnFallbackHeight;
        }

        return position;
    }

    /// <summary>
    /// 找到（并记住）蘑菇僵尸的预制体名。我们的生成方式和原版 <c>MushroomZombieSpawner.Spawn()</c> 一样，
    /// 靠 <c>PhotonNetwork.Instantiate(名字, …)</c>，所以这里要的是「预制体资源名」而不是某个场景实例。
    /// <para>
    /// 优先问原版僵尸管理器手里的生成器（它就有预制体引用），其次是场景里的生成器；
    /// 这个地图本来就不刷僵尸时，退一步用 <c>Resources.FindObjectsOfTypeAll</c> 把已经加载进内存的预制体捞出来
    /// —— 能捞到就说明这个名字对 Photon 的预制体池有效。名字找到一次就一直留着：
    /// 换到没有僵尸生成器的地图也能照常召唤，而这正是防挂机需要的。
    /// </para>
    /// </summary>
    private static bool ResolvePrefab()
    {
        if (!string.IsNullOrEmpty(_prefabName))
        {
            return true;
        }

        var manager = ZombieManager.Instance;

        if (manager != null && manager.spawners != null)
        {
            for (var i = 0; i < manager.spawners.Count; i++)
            {
                var spawner = manager.spawners[i];

                if (spawner != null && Remember(spawner.mushroomZombiePrefab))
                {
                    return true;
                }
            }
        }

        foreach (var spawner in UnityEngine.Object.FindObjectsByType<MushroomZombieSpawner>(FindObjectsSortMode.None))
        {
            if (spawner != null && Remember(spawner.mushroomZombiePrefab))
            {
                return true;
            }
        }

        foreach (var spawner in Resources.FindObjectsOfTypeAll<MushroomZombieSpawner>())
        {
            if (spawner != null && Remember(spawner.mushroomZombiePrefab))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>记下这个预制体的名字与唤醒距离；名字不可用（空 / 是场景里跑着的克隆体）时返回 false。</summary>
    private static bool Remember(MushroomZombie? prefab)
    {
        if (prefab == null)
        {
            return false;
        }

        var source = prefab.gameObject;
        var name = source == null ? null : source.name;

        // 场景里正在跑的实例名字带 "(Clone)"，拿去给 PhotonNetwork.Instantiate 是找不到预制体的。
        if (string.IsNullOrEmpty(name) || name!.Contains("(Clone)"))
        {
            return false;
        }

        _prefabName = name;
        _prefabWakeDistance = prefab.distanceToEnable > 0f ? prefab.distanceToEnable : FallbackWakeDistance;

        return true;
    }
}
