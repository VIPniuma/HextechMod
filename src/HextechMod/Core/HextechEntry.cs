using System;
using UnityEngine;

namespace PeakModder.HextechMod;

/// <summary>
/// 海克斯的四档品质。品质同时决定「这一项强化一次给多少加成」：
/// 青铜 8% / 白银 12% / 黄金 16% / 传说 20%（2026-09-13 全量削弱：10/15/20/25 → 这组）。
/// <para>
/// 「传说」是头奖档：权重极小（见 <see cref="HextechRegistry.WeightOf"/>，可配），
/// 而且商店的抽奖券只开到黄金 —— 想要传说只能靠自然三选一 / 开行李箱碰运气。
/// </para>
/// </summary>
public enum HextechQuality
{
    Bronze = 0,
    Silver = 1,
    Gold = 2,
    Legendary = 3,
}

/// <summary>
/// 品质数值与叠加公式。
/// <para>
/// 重复获得同一条词条时，新的一层加在「已经拿到的加成」上，而不是加在基础数值上：
/// 12% 之后再拿一次 12% → 12% + 12%×12% = 13.44%，再拿一次 → 13.44% + 13.44%×12% = 15.05%。
/// 也就是第 n 层的累计加成 = v × (1+v)^(n-1)。
/// </para>
/// </summary>
public static class HextechQualityMath
{
    public static float BronzeValue = 0.08f;
    public static float SilverValue = 0.12f;
    public static float GoldValue = 0.16f;
    public static float LegendaryValue = 0.20f;

    /// <summary>单次获得（第一层）的加成值。</summary>
    public static float Value(this HextechQuality quality)
    {
        return quality switch
        {
            HextechQuality.Legendary => LegendaryValue,
            HextechQuality.Gold => GoldValue,
            HextechQuality.Silver => SilverValue,
            _ => BronzeValue,
        };
    }

    /// <summary>第 <paramref name="stacks"/> 层时的累计加成，第一层就是品质数值本身。</summary>
    public static float TotalValue(this HextechQuality quality, int stacks)
    {
        if (stacks <= 0)
        {
            return 0f;
        }

        var value = quality.Value();
        return value * Mathf.Pow(1f + value, stacks - 1);
    }

    /// <summary>
    /// 拿到第 <paramref name="stacks"/> 层时应该乘到属性上的系数（「增加」类效果）。
    /// 词条是「按层乘上去、回机场按基准整体还原」，所以这里给的是这一次的增量，
    /// 各层连乘起来正好是 1 + <see cref="TotalValue"/>。
    /// </summary>
    public static float GainFactor(this HextechQuality quality, int stacks)
    {
        return (1f + quality.TotalValue(stacks)) / (1f + quality.TotalValue(stacks - 1));
    }

    /// <summary>「减少」类效果用的系数：减益 10% 就是 ×0.9。</summary>
    public static float ReduceFactor(this HextechQuality quality, int stacks)
    {
        return (1f - quality.TotalValue(stacks)) / (1f - quality.TotalValue(stacks - 1));
    }
}

/// <summary>
/// 一个「海克斯强化」的定义。所有词条都是无状态的单例，
/// 每名玩家持有的层数记录在 <see cref="HextechState"/> 里。
/// </summary>
public abstract class HextechEntry
{
    public abstract string Id { get; }

    public abstract string Title { get; }

    /// <summary>第一层时的效果说明。</summary>
    public abstract string Description { get; }

    /// <summary>品质决定这条词条一次的加成数值，见 <see cref="HextechQualityMath"/>。</summary>
    public virtual HextechQuality Quality => HextechQuality.Bronze;

    /// <summary>
    /// 是否是代价型（负面）词条。阶段三选一与商店只会给正面词条，
    /// 只有「开行李箱抽奖」这种随机抽取才会抽到负面。
    /// </summary>
    public virtual bool Negative => false;

    /// <summary>
    /// 是否只在「开行李箱抽奖」里出现。三选一与商店只给永久收益的词条，
    /// 所以「有时限的一次性爆发」全部打上这个标记，关进随机池里当彩蛋。
    /// </summary>
    public virtual bool LuggageOnly => false;

    /// <summary>
    /// 当前层数下这条词条的实际效果，HUD 上显示的是它。
    /// 重复获得是乘法叠加，所以「×2」并不等于「两倍加成」，
    /// 直接把算好的累计值写出来最不容易看错。
    /// </summary>
    public virtual string Summary(int stacks)
    {
        return Description;
    }

    /// <summary>是否可重复获得。</summary>
    public virtual bool Stackable => false;

    public virtual int MaxStacks => 1;

    /// <summary>获得时解锁的主动技能（可选）。</summary>
    public virtual SkillId? UnlocksSkill => null;

    /// <summary>
    /// 顶级词条自带的小小负面效果，没有就是 <c>null</c>。
    /// 说明文字会自动接在描述后面并标红，效果在 <see cref="HextechState.Acquire"/> 里结算。
    /// </summary>
    public virtual HextechDrawback? Drawback => null;

    /// <summary>
    /// 「本局全队最多只能有几名玩家拿到这条词条」，0 = 不限制。
    /// 在 <see cref="HextechState.CanAcquire"/> 里过滤 —— 也就是造抽奖池子的时候就跳过，
    /// 名额满了之后这条词条对第 3 个人来说等于不存在。
    /// </summary>
    public virtual int MaxHoldersPerRun => 0;

    public virtual void OnAcquired(HextechState state, int stacks)
    {
    }

    public virtual void OnTick(HextechState state, int stacks, float deltaTime)
    {
    }
}

/// <summary>
/// 一条「代价」：顶级词条为了平衡而附带的小幅度负面效果。
/// <para>
/// 每拿到一层结算一次，和代价型负面词条一样只用乘法乘在基准值上，
/// 回机场按基准整体还原时不会有残留。
/// </para>
/// </summary>
public sealed class HextechDrawback
{
    private readonly Action<HextechState, int> _apply;

    public HextechDrawback(string description, Action<HextechState, int> apply)
    {
        Description = description;
        _apply = apply;
    }

    /// <summary>「代价：」后面的那句话。</summary>
    public string Description { get; }

    public void Apply(HextechState state, int stacks)
    {
        _apply(state, stacks);
    }
}

/// <summary>用委托快速构造词条，避免为每个词条写一个类。</summary>
internal sealed class ActionHextech : HextechEntry
{
    private readonly string _id;
    private readonly string _title;
    private readonly string _description;
    private readonly Action<HextechState, int>? _onAcquired;
    private readonly Action<HextechState, int, float>? _onTick;

    public ActionHextech(
        string id,
        string title,
        string description,
        HextechQuality quality,
        Action<HextechState, int>? onAcquired = null,
        Action<HextechState, int, float>? onTick = null,
        bool stackable = false,
        int maxStacks = 1,
        SkillId? unlocksSkill = null,
        bool negative = false,
        bool luggageOnly = false,
        HextechDrawback? drawback = null,
        int maxHoldersPerRun = 0)
    {
        _id = id;
        _title = title;
        _description = description;
        Quality = quality;
        _onAcquired = onAcquired;
        _onTick = onTick;
        Stackable = stackable;
        MaxStacks = maxStacks;
        UnlocksSkill = unlocksSkill;
        Negative = negative;
        LuggageOnly = luggageOnly;
        Drawback = drawback;
        MaxHoldersPerRun = maxHoldersPerRun;
    }

    public override string Id => _id;

    public override string Title => _title;

    public override HextechQuality Quality { get; }

    public override bool Negative { get; }

    public override bool LuggageOnly { get; }

    public override bool Stackable { get; }

    public override int MaxStacks { get; }

    public override SkillId? UnlocksSkill { get; }

    public override HextechDrawback? Drawback { get; }

    public override int MaxHoldersPerRun { get; }

    /// <summary>
    /// 说明里写了 <c>{0}</c> 的词条，数字会按品质自动填进去，
    /// 所以「说明上的数值」和「实际乘上去的系数」不可能对不上。
    /// 没写占位符的说明原样返回。
    /// </summary>
    public override string Description => Describe(1);

    public override string Summary(int stacks)
    {
        return Describe(stacks);
    }

    public override void OnAcquired(HextechState state, int stacks)
    {
        _onAcquired?.Invoke(state, stacks);
    }

    public override void OnTick(HextechState state, int stacks, float deltaTime)
    {
        _onTick?.Invoke(state, stacks, deltaTime);
    }

    /// <summary>
    /// 把 <c>{0}</c> 换成第 <paramref name="stacks"/> 层时的累计加成，例如「11%」；
    /// 带「代价」的词条在后面再接一句标红的代价说明（HUD、三选一、商店卡都会显示）。
    /// </summary>
    private string Describe(int stacks)
    {
        var value = Quality.TotalValue(stacks) * 100f;
        var text = string.Format(_description, $"{value:0.##}%");

        if (Drawback == null)
        {
            return text;
        }

        return $"{text} <color=#{ColorUtility.ToHtmlStringRGB(UiFactory.Danger)}>代价：{Drawback.Description}</color>";
    }
}
