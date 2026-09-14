using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using HarmonyLib;
using UnityEngine;
using Zorro.Core.Serizalization;

namespace PeakModder.HextechMod;

/// <summary>
/// 「背包客」的扩容实现。
/// <para>
/// 背包容量一共牵扯四处，必须同时改，少一处就是崩溃：
/// </para>
/// <list type="number">
/// <item><b>数据</b>：<see cref="BackpackData.itemSlots"/> 的数组长度决定
/// <c>HasFreeSlot()</c> / <c>AddItem()</c> 能不能用新格子。新格子必须是
/// <c>new ItemSlot((byte)i)</c>，只 <c>Array.Copy</c> 的话第 5、6 格是 null，
/// <c>IsEmpty()</c> 立刻 NRE（BackpackVisuals.UpdatePocketBehaviors 每帧刷屏也是这个原因）。</item>
/// <item><b>轮盘</b>：物品格占 <c>slices[1..]</c>，<c>InitWheel</c> 的循环上界是
/// <c>itemSlots.Length</c>，两个分支都无条件读 <c>slices[i + 1]</c>，
/// 所以切片数必须 &gt;= <c>itemSlots.Length + 1</c>，不够就得自己克隆补上。
/// 摆位见 <see cref="ArrangeExtraSlices"/>（只摆扩出来的那几格，原版格子不碰）。</item>
/// <item><b>同步</b>：<c>BackpackData.DeserializeValue</c> 的循环上界写死 4，扩出来的格子会被丢掉。</item>
/// <item><b>外观</b>：<c>BackpackVisuals.RefreshVisuals</c> 的循环上界写死 4，
/// 而且 <c>backpackSlots</c> 只有 4 个挂点，第 5、6 格没有地方放模型。
/// <para>
/// 这条比看上去要命：模型不生成 → <c>Item.PutInBackpackRPC</c> 就不会广播 →
/// <c>spawnedVisualItems</c> 里没有那一格 → <c>BackpackWheel.Choose</c> 取不出东西，
/// 表现就是「格子多了、东西放得进，但拿不出来」。挂点必须在<b>所有</b>客户端、
/// 对场上<b>所有</b>背包都补齐（房主要替所有人跑 <c>RefreshVisuals</c>），
/// 见 <see cref="BackpackVisualsMountFix"/> 与 <see cref="BackpackCapacityWear"/>。
/// </para></item>
/// </list>
/// <para>
/// <c>slotCount</c> 与 <c>itemSlots.Length</c> 是两回事：前者只决定「几片 SetActive(true)」，
/// 后者才是循环上界，所以扩容后要把 slotCount 一起抬上去，否则多出来的格子会被关掉。
/// </para>
/// </summary>
internal static class BackpackCapacity
{
    /// <summary>原版背包格数（出厂 itemSlots 长度）。</summary>
    public const int VanillaSlots = 4;

    /// <summary>扩容上限，词条算错时也不会把数组撑爆。</summary>
    public const int MaxSlots = 10;

    /// <summary>扩容格子之间的中心距离（相对格子边长），留一点缝。</summary>
    private const float RowSpacing = 1.15f;

    /// <summary>量不出轮盘半径时的兜底：按「两个格子」的距离把扩容格子摆到圈外。</summary>
    private const float FallbackRadiusInCells = 2f;

    /// <summary>原版 4 个挂点围成圈时，相邻弦长正好是半径的 √2 倍；量不出间距时用它兜底。</summary>
    private const float CircleGapFactor = 1.4142f;

    /// <summary>背包外观挂点连半径都量不出来时的兜底间距（米）。</summary>
    private const float FallbackMountGap = 0.15f;

    // ── 容量 ─────────────────────────────────────────────────────

    /// <summary>本地玩家是否抽到了「背包客」。</summary>
    public static bool HasBackpacker(Character? local)
    {
        if (local == null)
        {
            return false;
        }

        var state = HextechState.Get(local);

        return state != null && state.StackOfId(AdvancedHextechs.BackpackerId) > 0;
    }

    /// <summary>
    /// 本地玩家背的是不是「滑稽背包」—— 扩容只认它一种。
    /// <para>
    /// 普通背包（原版就 4 格，扩了也看不出多出来）、喷气背包、火箭背包都不算「背包」：
    /// 火箭背包压根没有物品格，背上之后按住 E 是点火，格子和轮盘一动它就出怪状态
    /// （唯一能脱下来的路被堵住），所以从一开始就把它们排除掉，补丁对它们等于不存在。
    /// </para>
    /// </summary>
    public static bool IsFannypack(Character? local)
    {
        if (local == null)
        {
            return false;
        }

        try
        {
            var player = local.player;

            return player != null
                && player.backpackSlot != null
                && player.backpackSlot.backpackType == BackpackSlot.BackpackType.Fannypack;
        }
        catch (Exception)
        {
            // 玩家对象还没初始化好时读这些字段会抛，当作「不是滑稽背包」。
            return false;
        }
    }

    /// <summary>本地玩家背的背包现在该不该按词条扩容：滑稽背包 + 背包客，两个都占才行。</summary>
    public static bool IsExpansionTarget(Character? local) => HasBackpacker(local) && IsFannypack(local);

    /// <summary>本地玩家背的背包应该有多少格：原版 + 词条加减（只有滑稽背包会加）。</summary>
    public static int TargetSlots(Character? local)
    {
        var extra = IsExpansionTarget(local) ? AdvancedHextechs.BackpackerExtraSlots : 0;

        return Mathf.Clamp(VanillaSlots + extra, 1, MaxSlots);
    }

    /// <summary>
    /// 把格子数组补到至少 <paramref name="count"/> 个。
    /// <para>只扩不缩：缩格子会让已经放进去的物品没地方去，那是「破洞背包」要单独处理的事。</para>
    /// </summary>
    public static void EnsureCapacity(BackpackData? data, int count)
    {
        var slots = data != null ? data.itemSlots : null;

        if (slots == null)
        {
            return;
        }

        count = Mathf.Clamp(count, 0, MaxSlots);

        if (slots.Length < count)
        {
            var grown = new ItemSlot[count];
            Array.Copy(slots, grown, slots.Length);

            for (var i = slots.Length; i < count; i++)
            {
                grown[i] = new ItemSlot((byte)i);
            }

            data!.itemSlots = grown;
        }

        EnsureEntries(data);
    }

    /// <summary>兜底：数组里任何 null 元素都补成真的 ItemSlot（<c>IsEmpty()</c> 不能碰 null）。</summary>
    public static void EnsureEntries(BackpackData? data)
    {
        var slots = data != null ? data.itemSlots : null;

        if (slots == null)
        {
            return;
        }

        for (var i = 0; i < slots.Length; i++)
        {
            if (slots[i] == null)
            {
                slots[i] = new ItemSlot((byte)i);
            }
        }
    }

    /// <summary>
    /// 把格子数组缩回 <paramref name="count"/> 格。
    /// <para>
    /// 只在「这个背包不该扩容」时用：普通背包 / 喷气背包 / 火箭背包被老版本补丁扩到 6 格之后，
    /// 数据比轮盘放得下的还长，正是「轮盘打不开 / 背包脱不下来」的根源，这里给它缩回原版去。
    /// </para>
    /// <para>
    /// 缩掉的那几格里有东西时会丢：那几格只有扩容轮盘才够得着（正常打不到），所以只警告不搬运。
    /// </para>
    /// </summary>
    public static void ShrinkCapacity(BackpackData? data, int count)
    {
        var slots = data != null ? data.itemSlots : null;

        if (slots == null)
        {
            return;
        }

        count = Mathf.Clamp(count, 0, MaxSlots);

        if (slots.Length <= count)
        {
            EnsureEntries(data);
            return;
        }

        for (var i = count; i < slots.Length; i++)
        {
            if (slots[i] != null && !slots[i].IsEmpty())
            {
                HextechPlugin.Log.LogWarning($"背包多出来的第 {i + 1} 格里有东西，缩回 {count} 格时会被丢掉。");
            }
        }

        var shrunk = new ItemSlot[count];
        Array.Copy(slots, shrunk, count);
        data!.itemSlots = shrunk;
    }

    /// <summary>本地玩家背上的背包数据；没背包或引用失效时返回 null。</summary>
    public static BackpackData? LocalWornData()
    {
        var local = Character.localCharacter;

        if (local == null)
        {
            return null;
        }

        try
        {
            return BackpackReference.GetFromEquippedBackpack(local).GetData();
        }
        catch (Exception)
        {
            // 没背背包时这个引用是空的，GetData() 会抛——当作没有背包处理。
            return null;
        }
    }

    // ── 轮盘切片 ─────────────────────────────────────────────────

    /// <summary>
    /// 轮盘切片不够就克隆补上。
    /// <para>
    /// <c>InitWheel</c> 会读 <c>slices[itemSlots.Length]</c>，所以 <paramref name="needed"/>
    /// 至少要是「格数 + 1」（第 0 片是捡背包那格）。
    /// </para>
    /// <para>
    /// 克隆出来的切片要先 <c>SetActive(false)</c>：它是照抄最后一格做的，位置一样，
    /// 直接激活就会正好压在原有格子上。摆位统一交给 <see cref="ArrangeExtraSlices"/>。
    /// </para>
    /// </summary>
    public static void EnsureWheelSlices(BackpackWheel? wheel, int needed)
    {
        var slices = wheel != null ? wheel.slices : null;

        if (wheel == null || slices == null || slices.Length == 0)
        {
            return;
        }

        if (needed > slices.Length)
        {
            var prototype = slices[slices.Length - 1];

            if (prototype == null || prototype.transform.parent == null)
            {
                HextechPlugin.Log.LogWarning("背包轮盘切片没法复制，多出来的格子不会显示在轮盘上。");
                return;
            }

            var parent = prototype.transform.parent;
            var grown = new BackpackWheelSlice[needed];
            Array.Copy(slices, grown, slices.Length);

            for (var i = slices.Length; i < needed; i++)
            {
                var copy = UnityEngine.Object.Instantiate(prototype.gameObject, parent, false);
                copy.name = prototype.gameObject.name + "_extra" + (i - slices.Length + 1);
                copy.SetActive(false);

                var slice = copy.GetComponent<BackpackWheelSlice>();

                if (slice == null)
                {
                    UnityEngine.Object.Destroy(copy);
                    HextechPlugin.Log.LogWarning("背包轮盘切片复制失败，多出来的格子不会显示在轮盘上。");
                    return;
                }

                grown[i] = slice;
            }

            wheel.slices = grown;
            slices = grown;
        }

        ArrangeExtraSlices(wheel);
    }

    /// <summary>
    /// 轮盘最多能安全显示几个物品格：物品格占 <c>slices[1..]</c>，所以是「第一个空切片之前」的数量。
    /// <para>
    /// 切片不够时就必须少显示，绝不能反过来让数据格数超过它 —— 而且是这里说了算，
    /// 因为轮盘只放得下这么多格。数组尾巴上如果是没摆过的空位，也算放不下。
    /// </para>
    /// </summary>
    public static int SliceItemCapacity(BackpackWheel? wheel)
    {
        var slices = wheel != null ? wheel.slices : null;

        if (slices == null)
        {
            return 0;
        }

        var count = 0;

        for (var i = 1; i < slices.Length; i++)
        {
            if (slices[i] == null)
            {
                break;
            }

            count++;
        }

        return count;
    }

    /// <summary>
    /// 给 <c>InitWheel</c> 的 transpiler 用：物品格循环的安全上界
    /// <c>min(格数, 切片数 - 1)</c>。数组尾巴上的空位不算能放，跟 <see cref="SliceItemCapacity"/> 一致。
    /// </summary>
    public static int WheelSlotLimit(ItemSlot[]? slots, BackpackWheelSlice[]? slices)
    {
        var slotCount = slots != null ? slots.Length : 0;

        if (slices == null || slices.Length == 0)
        {
            return 0;
        }

        var usable = 0;

        for (var i = 1; i < slices.Length; i++)
        {
            if (slices[i] == null)
            {
                break;
            }

            usable++;
        }

        return Mathf.Min(slotCount, usable);
    }

    /// <summary>
    /// <c>BackpackWheel.InitWheel</c> 的物品格循环是 <c>for (i = 0; i &lt; itemSlots.Length; i++)</c>，
    /// 循环体两个分支都无条件写 <c>slices[i + 1]</c>，所以「数据格数 &gt; 切片数 - 1」时必然越界。
    /// <para>
    /// 这个状态是真实会出现的：背包扩过容（第 5、6 格）而轮盘切片没复制成功，
    /// 或者存档 / 别的补丁把格数撑得比当前轮盘能放下的多。越界之后异常会被
    /// <c>BackpackWheelFix</c> 的 Finalizer 吃掉并把轮盘关掉，之后每次开都抛 ——
    /// 而<b>捡背包 / 卸背包就在轮盘第 0 片上</b>，于是玩家看到的是「背包背上了脱不下来」。
    /// </para>
    /// <para>
    /// 这里把循环上界夹成 <c>min(格数, 切片数 - 1)</c>：少显示几格只是难看，打不开是玩不了。
    /// </para>
    /// </summary>
    [HarmonyPatch(typeof(BackpackWheel), "InitWheel")]
    internal static class BackpackWheelSliceLimit
    {
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            var slices = AccessTools.Field(typeof(BackpackWheel), "slices");
            var limit = AccessTools.Method(typeof(BackpackCapacity), nameof(WheelSlotLimit));

            if (slices == null || limit == null)
            {
                return list;
            }

            for (var i = 0; i < list.Count - 2; i++)
            {
                // 只认「取长度 → 转 int → 比较跳转」这一组；InitWheel 里满足它的只有物品格循环那一处。
                if (list[i].opcode != OpCodes.Ldlen
                    || list[i + 1].opcode != OpCodes.Conv_I4
                    || (list[i + 2].opcode != OpCodes.Blt && list[i + 2].opcode != OpCodes.Blt_S))
                {
                    continue;
                }

                // 此刻栈上是「这个数组」，换成把切片数组也压上去，再调 WheelSlotLimit 换回一个 int；
                // 后面原本的 conv.i4 对 int 是空操作，正好不用动。
                list[i] = new CodeInstruction(OpCodes.Ldarg_0) { labels = list[i].labels };
                list.Insert(i + 1, new CodeInstruction(OpCodes.Ldfld, slices));
                list.Insert(i + 2, new CodeInstruction(OpCodes.Call, limit));

                return list;
            }

            HextechPlugin.Log.LogWarning("背包轮盘的循环上界没改到，切片不足时可能会打不开轮盘。");
            return list;
        }
    }

    /// <summary>摆位失败只报一次（这段代码跑在 Update / LateUpdate 里，每次都报会把日志刷爆）。</summary>
    private static bool _reportedArrangeFailure;

    /// <summary>几何诊断每个游戏进程只打一次。</summary>
    private static bool _dumpedLayout;

    /// <summary>挂点诊断每个游戏进程只打一次。</summary>
    private static bool _dumpedMounts;

    /// <summary>
    /// 把「扩出来的那几格」摆到轮盘正上方，排成一排。
    /// <para>
    /// <b>只动 <c>slices[5..]</c></b>（物品格占 <c>slices[1..]</c>，前 4 个是原版格）：
    /// 原版格子、捡背包那一片、喷气背包的油量表一概不碰，所以原版轮盘（含火箭背包那套）
    /// 该什么样还是什么样 —— 这是不再踩「轮盘打不开」那个坑的关键。
    /// </para>
    /// <para>
    /// <b>为什么排成一排、而不是继续围成圈</b>：原版那圈格子是设计者摆死的美术，
    /// 4 片就占满一圈；再往里塞第 5、6 片，无论怎么分角度都会和邻居叠上
    /// （角宽是画死的，往外挪半径并不改变重叠的角度）。排成一排是唯一能保证不重叠的摆法。
    /// </para>
    /// <para>
    /// <b>为什么每帧都要摆</b>：实测轮盘打开的过程中格子位置还会被游戏自己改写，
    /// 只写一次的话下一帧就被盖回去 —— 表现就是「扩容的格子又叠回原来那格上」。
    /// 这里写的都是我们自己克隆出来的格子（没有别的代码会碰），反复写是安全且幂等的。
    /// </para>
    /// </summary>
    public static void ArrangeExtraSlices(BackpackWheel? wheel)
    {
        if (wheel == null)
        {
            return;
        }

        try
        {
            var slices = wheel.slices;
            var first = VanillaSlots + 1;

            if (slices == null || slices.Length <= first || !IsExpanded(wheel))
            {
                return;
            }

            // 扩容格是连续的，遇到 null 就停（和 SliceItemCapacity 一个口径）。
            // 这些要么是我们克隆的、要么是预制体里多出来的，游戏只把它们当物品格用，挪它们不会影响别的 UI。
            var count = 0;

            while (first + count < slices.Length && slices[first + count] != null)
            {
                count++;
            }

            if (count == 0)
            {
                return;
            }

            var wheelTransform = wheel.transform;
            var center = wheelTransform.position;

            MeasureRing(wheel, center, out var radius, out var gap);

            var cell = MeasureCell(slices, first, count, radius, gap);

            // 原版那圈的外沿差不多是「半径 + 半个格子」，再留半个格子的缝，就不会压到轮盘上。
            var ring = radius > cell * 0.5f ? radius : cell * FallbackRadiusInCells;
            var pitch = cell * RowSpacing;
            var distance = ring + cell;
            var middle = (count - 1) * 0.5f;
            var up = wheelTransform.up;
            var right = wheelTransform.right;

            for (var i = 0; i < count; i++)
            {
                var slice = slices[first + i];

                if (slice == null)
                {
                    continue;
                }

                PlaceSlice(slice.transform, center + (up * distance) + (right * ((i - middle) * pitch)));
            }
        }
        catch (Exception exception)
        {
            if (_reportedArrangeFailure)
            {
                return;
            }

            _reportedArrangeFailure = true;
            HextechPlugin.Log.LogWarning($"背包扩容格子摆位失败（扩容格可能会叠在一起）：{exception.Message}");
        }
    }

    /// <summary>
    /// 这个轮盘背后的背包是不是「我们要摆扩容格的那个」：本地玩家背着的滑稽背包，且数据比原版长。
    /// 别人的背包、地上的背包（含普通背包 / 火箭背包）一律不算 —— 它们的轮盘原版怎么样就怎么样。
    /// </summary>
    private static bool IsExpanded(BackpackWheel wheel)
    {
        try
        {
            if (!IsFannypack(Character.localCharacter))
            {
                return false;
            }

            var reference = wheel.backpack;

            if (!reference.exists)
            {
                return false;
            }

            var data = reference.GetData();
            var slots = data != null ? data.itemSlots : null;

            return slots != null && slots.Length > VanillaSlots;
        }
        catch (Exception)
        {
            // 读不到数据（还没初始化 / 引用失效）就当没扩容，别把上面的摆位循环整段废掉。
            return false;
        }
    }

    /// <summary>
    /// 量原版那圈格子：<paramref name="radius"/> 是物品格离轮盘中心的最大距离，
    /// <paramref name="gap"/> 是相邻物品格的最小距离。
    /// <para>
    /// 全按世界坐标量：切片挂在谁下面、父级转了多少度都不影响结果。
    /// 量不出来（没激活 / 格子不够 / 数值明显不合理）时给 0，调用方有兜底。
    /// </para>
    /// </summary>
    private static void MeasureRing(BackpackWheel wheel, Vector3 center, out float radius, out float gap)
    {
        var slices = wheel.slices;
        var previous = Vector3.zero;
        var hasPrevious = false;
        var count = 0;

        radius = 0f;
        gap = float.MaxValue;

        for (var i = 1; i <= VanillaSlots && slices != null && i < slices.Length; i++)
        {
            var slice = slices[i];

            if (slice == null || !slice.gameObject.activeInHierarchy)
            {
                continue;
            }

            var position = slice.transform.position;
            radius = Mathf.Max(radius, Vector3.Distance(position, center));

            if (hasPrevious)
            {
                gap = Mathf.Min(gap, Vector3.Distance(position, previous));
            }

            previous = position;
            hasPrevious = true;
            count++;
        }

        if (count < 2 || gap > radius * 4f)
        {
            gap = 0f;
        }

        if (radius < 0.0001f)
        {
            // 格子全堆在中心（读出来还没排过），算不出圈，交给调用方兜底。
            radius = 0f;
        }
    }

    /// <summary>格子边长（世界单位）：先量扩容格自己的矩形，量不出来再拿原版间距 / 半径估。</summary>
    private static float MeasureCell(BackpackWheelSlice[] slices, int first, int count, float radius, float gap)
    {
        var cell = 0f;

        for (var i = 0; i < count; i++)
        {
            var slice = slices[first + i];

            if (slice == null)
            {
                continue;
            }

            var rect = slice.transform as RectTransform;

            if (rect == null)
            {
                continue;
            }

            var size = rect.rect.size;
            var scale = rect.lossyScale;

            cell = Mathf.Max(cell, Mathf.Max(Mathf.Abs(size.x * scale.x), Mathf.Abs(size.y * scale.y)));
        }

        if (cell > 0.0001f)
        {
            return cell;
        }

        if (gap > 0.0001f)
        {
            return gap;
        }

        return radius > 0.0001f ? radius * 0.5f : 1f;
    }

    /// <summary>
    /// 把切片放到世界坐标 <paramref name="world"/>。
    /// <para>
    /// 能写 <c>anchoredPosition</c> 就写它：RectTransform 以锚点为准，直接改 <c>localPosition</c>
    /// 会在下一次布局重建时被锚点算回来的值覆盖（表现就是「摆了又弹回去」）。
    /// 锚点没拉伸（anchorMin == anchorMax）时两者关系是
    /// <c>localPosition = 父矩形左下角 + 锚点比例 × 父矩形尺寸 + anchoredPosition</c>。
    /// </para>
    /// </summary>
    private static void PlaceSlice(Transform slice, Vector3 world)
    {
        var parent = slice.parent;
        var local = parent != null ? parent.InverseTransformPoint(world) : world;
        var rect = slice as RectTransform;
        var parentRect = parent as RectTransform;

        if (rect == null || parentRect == null)
        {
            slice.localPosition = local;
            return;
        }

        var reference = parentRect.rect.min + (rect.anchorMin * parentRect.rect.size);
        rect.anchoredPosition = new Vector2(local.x, local.y) - reference;
    }

    /// <summary>
    /// 挂在轮盘对象上，负责每帧把扩容格子摆回去。
    /// <para>
    /// 用 <c>LateUpdate</c> 而不是只在某一次调用里摆：格子位置会被游戏自己（动画 / 布局）改写，
    /// 而 LateUpdate 是同样的改写之后最常见的那一帧，写了才不会被盖掉。
    /// </para>
    /// </summary>
    internal sealed class WheelLayoutDriver : MonoBehaviour
    {
        internal BackpackWheel? Wheel;

        private void LateUpdate()
        {
            var wheel = Wheel;

            if (wheel == null)
            {
                Destroy(this);
                return;
            }

            ArrangeExtraSlices(wheel);
        }
    }

    /// <summary>
    /// 每帧在 <c>InitWheel</c> 之外再摆一次：<c>Update</c> 和 <c>LateUpdate</c> 各写一遍，
    /// 能尽量晚于「改写格子位置的那段代码」，别让它把我们摆好的位置盖回去。
    /// </summary>
    [HarmonyPatch(typeof(BackpackWheel), "Update")]
    internal static class BackpackWheelLayoutTick
    {
        private static int _frames;

        [HarmonyPostfix]
        private static void Postfix(BackpackWheel __instance)
        {
            ArrangeExtraSlices(__instance);

            // 轮盘开满 60 帧后打一次几何诊断：这时打开动画早停了，量到的就是最终位置。
            if (++_frames == 60)
            {
                DumpLayout(__instance);
            }
        }
    }

    /// <summary>轮盘第一次初始化时挂上每帧摆位的驱动（一个轮盘只挂一个）。</summary>
    [HarmonyPatch(typeof(BackpackWheel), "InitWheel")]
    internal static class BackpackWheelLayoutDriverAttach
    {
        [HarmonyPostfix]
        private static void Postfix(BackpackWheel __instance)
        {
            try
            {
                if (__instance != null && __instance.GetComponent<WheelLayoutDriver>() == null)
                {
                    __instance.gameObject.AddComponent<WheelLayoutDriver>().Wheel = __instance;
                }
            }
            catch (Exception exception)
            {
                HextechPlugin.Log.LogWarning($"背包轮盘摆位驱动挂不上（扩容格可能会叠在一起）：{exception.Message}");
            }
        }
    }

    /// <summary>
    /// 打一次轮盘几何诊断（每个游戏进程一次）。用来核对三件事：
    /// 原版那圈格子到底摆在哪、切片父级上有没有 <c>LayoutGroup</c> / <c>Animator</c> 在挪格子、
    /// 我们写下去的位置有没有被盖掉。确认扩容格子摆对了之后这块可以整段删掉。
    /// </summary>
    private static void DumpLayout(BackpackWheel wheel)
    {
        if (_dumpedLayout)
        {
            return;
        }

        _dumpedLayout = true;

        try
        {
            var report = new StringBuilder();

            report.AppendLine("[轮盘诊断] 轮盘几何（一次性，用于核对扩容格摆位）");
            report.AppendLine($"  轮盘 {Describe(wheel.transform)}");
            report.AppendLine($"  轮盘自身组件 {Components(wheel.transform)}");
            report.AppendLine($"  轮盘父级 {Name(wheel.transform.parent)} 组件 {Components(wheel.transform.parent)}");

            var slices = wheel.slices;

            if (slices != null)
            {
                for (var i = 0; i < slices.Length; i++)
                {
                    var slice = slices[i];

                    if (slice == null)
                    {
                        report.AppendLine($"  slices[{i}] = null");
                        continue;
                    }

                    report.AppendLine($"  slices[{i}] {Describe(slice.transform)} 父组件 {Components(slice.transform.parent)}");
                }
            }

            report.AppendLine($"  jetpackSlice {Describe(wheel.jetpackSlice != null ? wheel.jetpackSlice.transform : null)}");
            report.AppendLine($"  fuelGauge {Describe(wheel.fuelGauge != null ? wheel.fuelGauge.transform : null)}");
            report.AppendLine($"  名字文字 {Describe(wheel.chosenItemText != null ? wheel.chosenItemText.transform : null)}");
            report.AppendLine($"  手持图标 {Describe(wheel.currentlyHeldItem != null ? wheel.currentlyHeldItem.transform : null)}");

            HextechPlugin.Log.LogInfo(report.ToString());
        }
        catch (Exception exception)
        {
            HextechPlugin.Log.LogWarning($"轮盘诊断打不出来：{exception.Message}");
        }
    }

    private static string Name(Transform? transform) => transform != null ? transform.name : "null";

    private static string Format(Vector3 value) => $"({value.x:0.##}, {value.y:0.##}, {value.z:0.##})";

    /// <summary>一句话描述一个 UI 节点：名字、激活、世界 / 局部坐标、矩形与锚点。</summary>
    private static string Describe(Transform? transform)
    {
        if (transform == null)
        {
            return "null";
        }

        var text = $"{transform.name} active={transform.gameObject.activeSelf} 父={Name(transform.parent)}"
            + $" 世界={Format(transform.position)} 局部={Format(transform.localPosition)} rotZ={transform.localEulerAngles.z:0.#}";

        if (transform is RectTransform rect)
        {
            text += $" 尺寸={Format(rect.rect.size)} 锚点={Format(rect.anchorMin)}→{Format(rect.anchorMax)}"
                + $" 锚位={Format(rect.anchoredPosition)}";
        }

        return text;
    }

    /// <summary>父级上「会挪格子」的组件类型名（布局 / 动画 / 适配器），别的组件没意义就不列了。</summary>
    private static string Components(Transform? transform)
    {
        if (transform == null)
        {
            return "-";
        }

        var names = new List<string>();
        var components = transform.GetComponents<Component>();

        for (var i = 0; i < components.Length; i++)
        {
            var component = components[i];

            if (component == null)
            {
                continue;
            }

            var type = component.GetType().Name;

            if (type.Contains("Layout") || type.Contains("Animator") || type.Contains("Canvas") || type.Contains("Fitter"))
            {
                names.Add(type);
            }
        }

        return names.Count > 0 ? string.Join("/", names) : "-";
    }

    // ── 外观 ─────────────────────────────────────────────────────

    /// <summary>
    /// 外观挂点只有原版那么几个，多出来的格子没有挂点：
    /// 按原挂点围成的那一圈，把新挂点放在「同方向、再往外一个格子间距」的位置 ——
    /// 这样它和最近的挂点之间至少隔着一个原版间距，物品模型才不会叠在一起。
    /// </summary>
    public static void EnsureMounts(BackpackVisuals? visuals, int needed)
    {
        var mounts = visuals != null ? visuals.backpackSlots : null;

        if (mounts == null || mounts.Length == 0 || needed <= mounts.Length)
        {
            return;
        }

        var parent = mounts[0] != null ? mounts[0].parent : null;

        if (parent == null)
        {
            return;
        }

        var baseCount = mounts.Length;
        var center = Vector3.zero;
        var valid = 0;

        for (var i = 0; i < baseCount; i++)
        {
            if (mounts[i] == null)
            {
                continue;
            }

            center += mounts[i].position;
            valid++;
        }

        if (valid == 0)
        {
            return;
        }

        center /= valid;

        // 半径和相邻间距都按世界坐标量：挂点挂在谁下面、父级转了多少度都不影响结果。
        var radius = 0f;
        var gap = float.MaxValue;
        Transform? previous = null;

        for (var i = 0; i < baseCount; i++)
        {
            var mount = mounts[i];

            if (mount == null)
            {
                continue;
            }

            radius = Mathf.Max(radius, Vector3.Distance(mount.position, center));

            if (previous != null)
            {
                gap = Mathf.Min(gap, Vector3.Distance(mount.position, previous.position));
            }

            previous = mount;
        }

        if (gap > radius * 4f)
        {
            gap = 0f;
        }

        if (gap < 0.0001f)
        {
            // 原版 4 个挂点围成圈时，相邻弦长正好是半径的 √2 倍；量不出来才用固定值兜底。
            gap = radius > 0.0001f ? radius * CircleGapFactor : FallbackMountGap;
        }

        var outerRadius = radius + gap;
        var grown = new Transform[needed];
        Array.Copy(mounts, grown, baseCount);

        for (var i = baseCount; i < needed; i++)
        {
            var template = mounts[i % baseCount];
            var mount = new GameObject("HextechBackpackMount" + i);
            var transform = mount.transform;
            transform.SetParent(parent, false);

            if (template != null)
            {
                var offset = template.position - center;

                // 挂点全堆在一点（量不到方向）时给个朝上的固定方向，至少不会和原挂点重合。
                if (offset.sqrMagnitude < 1e-8f)
                {
                    offset = Vector3.up;
                }

                transform.localPosition = parent.InverseTransformPoint(center + (offset.normalized * outerRadius));
                transform.localRotation = template.localRotation;
                transform.localScale = template.localScale;
            }

            grown[i] = transform;
        }

        visuals!.backpackSlots = grown;

        if (!_dumpedMounts)
        {
            _dumpedMounts = true;

            var report = new StringBuilder();
            report.Append($"原版 {baseCount} 个 → 需要 {needed} 个；圈心={Format(center)} 半径={radius:0.###} 间距={gap:0.###}；新增挂点世界位置=");

            for (var i = baseCount; i < grown.Length; i++)
            {
                report.Append(i > baseCount ? "、" : string.Empty);
                report.Append(Format(grown[i] != null ? grown[i].position : Vector3.zero));
            }

            HextechPlugin.Log.LogInfo($"[挂点诊断] 背包外观挂点：{report}");
        }
    }

    /// <summary>
    /// 给 <c>RefreshVisuals</c> 的 transpiler 用：这次的循环上界应该是几。
    /// <para>
    /// 循环体写的是 <c>backpackSlots[i]</c>，所以上界得取「数据格数」和「挂点数」里小的那个：
    /// 背包数据被扩到 6 格、挂点还是原版 4 个时（扩容格还没来得及补挂点），照数据格数走就是
    /// 越界异常 —— 而 <c>RefreshVisuals</c> 是在「背上 / 卸下背包」的协程里跑的，它一抛，
    /// 收尾就断在那里，玩家看到的是「背包脱不下来，地上还凭空多一个」。
    /// 少刷两格只是那两个物品模型不显示，怎么都比越界强。
    /// </para>
    /// </summary>
    public static int SlotCount(BackpackVisuals? visuals)
    {
        var data = visuals != null ? visuals.backpackData : null;
        var slots = data != null ? data.itemSlots : null;
        var mounts = visuals != null ? visuals.backpackSlots : null;

        var count = slots != null ? slots.Length : 0;

        return mounts != null ? Mathf.Min(count, mounts.Length) : count;
    }

    // ── 补丁 ─────────────────────────────────────────────────────

    /// <summary>兜底：数组里出现 null 元素（老存档、别的补丁搞坏的数组）时补齐。</summary>
    [HarmonyPatch(typeof(BackpackData), "Init")]
    internal static class BackpackDataInit
    {
        // 必须是 Postfix：Init 自己会按 itemSlots.Length 逐格 new ItemSlot，
        // 放在 Prefix 里跑在它之前，此刻数组还是构造器里那排 null，等于白跑。
        [HarmonyPostfix]
        private static void Postfix(BackpackData __instance)
        {
            EnsureEntries(__instance);
        }
    }

    /// <summary>背上背包的瞬间就按词条把格子补齐（同一个背包重复背不会重复加）。</summary>
    [HarmonyPatch(typeof(Backpack), "Wear")]
    internal static class BackpackCapacityWear
    {
        [HarmonyPostfix]
        private static void Postfix(Backpack __instance, Character __0)
        {
            try
            {
                if (__instance == null || __0 == null || !__0.IsLocal || !HasBackpacker(__0))
                {
                    return;
                }

                var data = BackpackReference.GetFromBackpackItem(__instance).GetData();

                if (data == null || data.itemSlots == null)
                {
                    return;
                }

                // 扩容只给滑稽背包：普通背包 / 喷气背包 / 火箭背包一律不扩。
                // 火箭背包背上之后按住 E 是点火，格子一动它的状态就乱，所以从这儿就排除掉。
                if (__instance.backpackType != BackpackSlot.BackpackType.Fannypack)
                {
                    // 老版本补丁给普通背包 / 火箭背包扩过容的，这里缩回原版格数。
                    ShrinkCapacity(data, VanillaSlots);

                    if (__instance.slotCount > data.itemSlots.Length)
                    {
                        __instance.slotCount = data.itemSlots.Length;
                    }

                    return;
                }

                EnsureCapacity(data, TargetSlots(__0));

                // 挂点跟着数据一起补。房主端是照挂点生成物品模型的（BackpackVisuals.RefreshVisuals），
                // 挂点不齐就会跳过第 5、6 格，放进去的东西之后谁也取不出来。
                EnsureMounts(BackpackReference.GetFromBackpackItem(__instance).GetVisuals(), data.itemSlots.Length);

                // slotCount 只影响轮盘里几片是 Active，跟数据格数保持一致，地上的背包才能按 6 格开。
                if (__instance.slotCount < data.itemSlots.Length)
                {
                    __instance.slotCount = data.itemSlots.Length;
                }
            }
            catch (Exception exception)
            {
                HextechPlugin.Log.LogWarning($"背包扩容失败：{exception.Message}");
            }
        }
    }

    /// <summary>
    /// 开轮盘之前把「格数 / 切片数 / slotCount」三者对齐。
    /// <para>
    /// 抽到「背包客」之后才穿上的背包、或者穿上之后才抽到词条，都在这里补齐，
    /// 不需要把背包丢掉再捡一次。
    /// </para>
    /// </summary>
    [HarmonyPatch(typeof(BackpackWheel), "InitWheel")]
    internal static class BackpackWheelSetup
    {
        [HarmonyPrefix]
        private static void Prefix(BackpackWheel __instance, BackpackReference __0, ref int __1)
        {
            try
            {
                // 值类型参数在补丁里是副本，先取到本地再调方法（Harmony 会警告直接改副本没意义）。
                var reference = __0;
                var data = reference.GetData();

                if (data == null || data.itemSlots == null)
                {
                    return;
                }

                // 只处理「本地玩家背着的这个背包」：别人的背包、地上的背包（含普通背包 / 火箭背包）
                // 一概不碰，轮盘完全交给原版。
                if (!ReferenceEquals(data, LocalWornData()))
                {
                    return;
                }

                var local = Character.localCharacter;

                if (!IsExpansionTarget(local))
                {
                    if (!IsFannypack(local))
                    {
                        // 背的不是滑稽背包：老版本补丁可能给普通背包 / 喷气背包 / 火箭背包扩过容，
                        // 这里把数据缩回原版格数 —— 数据比轮盘切片长正是「轮盘打不开」的根源。
                        ShrinkCapacity(data, VanillaSlots);
                    }

                    // 滑稽背包 + 暂时没有背包客：格子留着不缩（缩了里面的东西就没了），
                    // 轮盘循环上界由 WheelSlotLimit 收到能放的格数，照样打得开。
                    return;
                }

                var target = TargetSlots(local);

                if (data.itemSlots.Length < target)
                {
                    // 顺序很关键：先确认轮盘切片能补到「目标格数 + 1」，再扩数据。
                    // InitWheel 的循环上界是 itemSlots.Length，循环里两个分支都无条件写 slices[i + 1]，
                    // 所以数据一旦比切片长，之后每次开轮盘都必然抛 —— 异常被下面的 Finalizer 吃掉，
                    // 玩家看到的是「轮盘再也打不开」，而捡 / 卸背包恰恰在轮盘第 0 片上。
                    EnsureWheelSlices(__instance, target + 1);

                    var capacity = SliceItemCapacity(__instance);

                    if (capacity < target)
                    {
                        HextechPlugin.Log.LogWarning($"背包轮盘切片不够，多出来的格子这次先不扩：轮盘只有 {capacity} 个格位（需要的切片没复制出来）。");
                    }

                    // 切片补不上时只扩到轮盘放得下的格数：宁可少两格，也绝不能让轮盘打不开。
                    EnsureCapacity(data, capacity > 0 ? Mathf.Min(target, capacity) : target);
                }

                var items = data.itemSlots.Length;
                EnsureWheelSlices(__instance, items + 1);

                var maxItems = SliceItemCapacity(__instance);

                if (maxItems <= 0)
                {
                    return;
                }

                // 数据比切片能放下的还多（切片复制失败、或者预制体本身就不够）：
                // 循环上界由 WheelSliceLimit 补丁一起收敛到 maxItems，这里把可见格数也压回来。
                if (items > maxItems)
                {
                    HextechPlugin.Log.LogWarning($"背包有 {items} 格，轮盘只放得下 {maxItems} 格：多出来的格子这次不显示（背包里的东西不受影响）。");
                    items = maxItems;
                }

                if (__1 < items)
                {
                    __1 = items;
                }
                else if (__1 > maxItems)
                {
                    __1 = maxItems;
                }
            }
            catch (Exception exception)
            {
                HextechPlugin.Log.LogWarning($"背包轮盘格数修正失败：{exception.Message}");
            }
        }

        /// <summary>
        /// 兜底保命：<c>OpenBackpackWheel</c> 是先置位 <c>usingBackpackWheel</c> 再调用 <c>InitWheel</c> 的，
        /// 这里一抛异常，标志位就再也回不去，视角会卡到退出游戏为止。异常一律吃掉 + 强制关轮盘。
        /// </summary>
        [HarmonyFinalizer]
        private static Exception? Finalizer(Exception __exception, BackpackWheel __instance)
        {
            if (__exception == null)
            {
                return null;
            }

            try
            {
                HextechPlugin.Log.LogWarning($"背包轮盘初始化失败，已强制解锁视角：{__exception}");
                GUIManager.instance.CloseBackpackWheel();

                if (__instance != null)
                {
                    __instance.gameObject.SetActive(false);
                }

                var local = Character.localCharacter;

                if (local != null && local.data != null)
                {
                    local.data.usingBackpackWheel = false;
                }
            }
            catch (Exception)
            {
                // 兜底代码自己不能再抛。
            }

            return null;
        }
    }

    /// <summary>
    /// <c>BackpackData.DeserializeValue</c> 的循环上界写死 4：背包扩到 6 格之后，
    /// 同步/重连过来的第 5、6 格会被直接丢掉（第 5、6 格里的物品在本地就消失了）。
    /// 这里按 payload 里实际的格数重做一遍。
    /// </summary>
    [HarmonyPatch(typeof(BackpackData), "DeserializeValue")]
    internal static class BackpackSyncFix
    {
        [HarmonyPrefix]
        private static bool Prefix(BackpackData __instance, BinaryDeserializer __0)
        {
            try
            {
                // __0 是反序列化器。DeserializeValue 只有这一个声明参数（IL 里是 ldarg.1），
                // 写 __1 的话 Harmony 解析不到参数，整条补丁会在 PatchAll 时失败。
                var sync = default(InventorySyncData);
                sync.Deserialize(__0);

                var payload = sync.slots;
                var slots = __instance.itemSlots;

                if (payload == null || slots == null)
                {
                    return false;
                }

                if (payload.Length > slots.Length)
                {
                    EnsureCapacity(__instance, payload.Length);
                    slots = __instance.itemSlots;
                }

                if (slots == null)
                {
                    return false;
                }

                var count = Math.Min(slots.Length, payload.Length);

                for (var i = 0; i < count; i++)
                {
                    if (slots[i] == null)
                    {
                        slots[i] = new ItemSlot((byte)i);
                    }

                    var item = ItemDatabase.TryGetItem(payload[i].ItemID, out var found) ? found : null;
                    slots[i].SetItem(item, payload[i].Data);
                }
            }
            catch (Exception exception)
            {
                // 这里不能再放手让原版跑一遍：流已经被读过了，会把同步读乱。
                HextechPlugin.Log.LogWarning($"背包格子同步失败：{exception.Message}");
            }

            return false;
        }
    }

    /// <summary>
    /// <c>BackpackVisuals.RefreshVisuals</c> 的循环上界是写死的 <c>ldc.i4.4</c>，
    /// 扩出来的格子不会在背包模型上生成物品模型。这里把那一条常量换成运行时的格数。
    /// </summary>
    [HarmonyPatch(typeof(BackpackVisuals), "RefreshVisuals")]
    internal static class BackpackVisualsSlotFix
    {
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            var targets = new List<int>();

            for (var i = 0; i < list.Count - 1; i++)
            {
                if (list[i].opcode != OpCodes.Ldc_I4_4)
                {
                    continue;
                }

                if (list[i + 1].opcode == OpCodes.Blt || list[i + 1].opcode == OpCodes.Blt_S)
                {
                    targets.Add(i);
                }
            }

            if (targets.Count != 1)
            {
                HextechPlugin.Log.LogWarning($"背包外观的循环上界没改到（匹配 {targets.Count} 处），多出来的格子不会显示在模型上。");
                return list;
            }

            var slotCount = AccessTools.Method(typeof(BackpackCapacity), nameof(SlotCount));

            if (slotCount == null)
            {
                return list;
            }

            var index = targets[0];
            var load = new CodeInstruction(OpCodes.Ldarg_0) { labels = list[index].labels };
            var call = new CodeInstruction(OpCodes.Call, slotCount);

            list[index] = load;
            list.Insert(index + 1, call);

            return list;
        }
    }

    /// <summary>
    /// <b>「放进去拿不出来」的根因补丁。</b>
    /// <para>
    /// 取背包里的东西走的是 <c>BackpackWheel.Choose</c> → <c>BackpackVisuals.TryGetSpawnedItem</c>，
    /// 而 <c>spawnedVisualItems</c> 只有一个写入点：<c>Item.PutInBackpackRPC</c>。
    /// 那个 RPC 又只由 <c>RefreshVisuals</c> 生成物品模型的流程发出
    /// （<c>PhotonNetwork.Instantiate</c> → <c>PutItemInBackpack</c> → RPC 广播到所有客户端）。
    /// </para>
    /// <para>
    /// 而 <c>RefreshVisuals</c> 是照 <c>backpackSlots</c> 生成模型的，循环上界是
    /// <see cref="SlotCount"/> = <c>min(格数, 挂点数)</c>。挂点只有原版 4 个时：
    /// 模型不生成 → RPC 不发 → <c>spawnedVisualItems[5]</c> 永远是空的 →
    /// 轮盘上第 5、6 格显示得出来（那是数据 + 切片扩的），点下去却谁都不动，正是玩家说的
    /// 「多两个格子，东西放进去拿不出来」。
    /// </para>
    /// <para>
    /// 所以这里在每次刷新前把挂点按该背包自己的数据格数补齐，而且<b>对场上所有背包</b>都补：
    /// <c>RefreshVisuals</c> 只在房主端跑，房主要替所有人生成模型，别人的背包挂点不齐一样会卡住。
    /// </para>
    /// </summary>
    [HarmonyPatch(typeof(BackpackVisuals), "RefreshVisuals")]
    internal static class BackpackVisualsMountFix
    {
        [HarmonyPrefix]
        private static void Prefix(BackpackVisuals __instance)
        {
            try
            {
                if (__instance == null)
                {
                    return;
                }

                // 用 GetBackpackData() 而不是读 backpackData 字段：字段是在 RefreshVisuals
                // 方法体开头才赋值的，Prefix 跑在它之前，读到的会是上一帧的旧值。
                var data = __instance.GetBackpackData();
                var slots = data != null ? data.itemSlots : null;

                if (slots == null || slots.Length <= VanillaSlots)
                {
                    return;
                }

                EnsureMounts(__instance, Mathf.Min(slots.Length, MaxSlots));
            }
            catch (Exception exception)
            {
                HextechPlugin.Log.LogWarning($"背包外观挂点补齐失败（扩容格里的物品会拿不出来）：{exception.Message}");
            }
        }
    }

    /// <summary>
    /// 物品模型是按挂点摆的（<c>Item.Update</c> 里拿挂点的世界坐标），
    /// 而 <c>Item.PutInBackpackRPC</c> 直接写 <c>backpackSlots[slot]</c>：
    /// 第 5、6 格必须先有挂点，不然 RPC 一进来就 IndexOutOfRange。
    /// </summary>
    [HarmonyPatch(typeof(Item), "PutInBackpackRPC")]
    internal static class BackpackMountFix
    {
        [HarmonyPrefix]
        private static void Prefix(byte __0, BackpackReference __1)
        {
            try
            {
                var reference = __1;

                EnsureMounts(reference.GetVisuals(), __0 + 1);
            }
            catch (Exception exception)
            {
                HextechPlugin.Log.LogWarning($"背包外观挂点补齐失败：{exception.Message}");
            }
        }
    }
}
