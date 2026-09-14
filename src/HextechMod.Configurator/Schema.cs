using System.Collections.Generic;

namespace PeakModder.HextechConfigurator;

/// <summary>一个可调字段：键与模组端静态字段「类名.字段名」一一对应（见模组 Core/BalanceConfig.cs）。</summary>
internal sealed class BalanceField
{
    public string Key = string.Empty;
    public string Label = string.Empty;
    public double Min;
    public double Max = 100;
    public double Step = 1;
    public double Default;
    public bool IsInt;
}

/// <summary>一个「词条卡」：三选一式的方块，点进去改它的数值。</summary>
internal sealed class ModuleDef
{
    public string Title = string.Empty;
    public string Subtitle = string.Empty;
    public List<BalanceField> Fields = new();
}

/// <summary>
/// 配置器的模块清单。⚠️ 字段的 Key 必须与模组端静态字段「类名.字段名」一致、
/// Default 必须与模组端默认值一致 —— 模组端改了默认值，这里要同步。
/// </summary>
internal static class Schema
{
    public static readonly List<ModuleDef> Modules = Build();

    private static List<ModuleDef> Build()
    {
        var modules = new List<ModuleDef>
        {
            M("品质加成", "所有走品质表的词条一起变",
                F("HextechQualityMath.BronzeValue", "青铜·单次加成", 0.01, 0.50, 0.01, 0.08),
                F("HextechQualityMath.SilverValue", "白银·单次加成", 0.01, 0.50, 0.01, 0.12),
                F("HextechQualityMath.GoldValue", "黄金·单次加成", 0.01, 0.50, 0.01, 0.16),
                F("HextechQualityMath.LegendaryValue", "传说·单次加成", 0.01, 0.50, 0.01, 0.20)),

            M("拿取上限", "普通与技能的持有数量",
                F("HextechState.MaxNormalHextechs", "普通海克斯上限", 1, 20, 1, 4, isInt: true),
                F("HextechState.MaxSkillHextechs", "技能海克斯上限", 1, 5, 1, 1, isInt: true)),

            M("漂浮", "技能解锁词条",
                F("SkillRegistry.SuperJumpEnergy", "能量", 5, 100, 5, 30),
                F("SkillRegistry.SuperJumpCooldown", "冷却(秒)", 3, 180, 1, 8),
                F("SkillRegistry.SuperJumpHungerCost", "饥饿代价(点)", 0, 50, 1, 10)),

            M("疾风步", "技能解锁词条",
                F("SkillRegistry.SprintEnergy", "能量", 5, 100, 5, 35),
                F("SkillRegistry.SprintCooldown", "冷却(秒)", 3, 180, 1, 30),
                F("SkillRegistry.SprintDurationSeconds", "无限耐力(秒)", 1, 30, 1, 5),
                F("SkillRegistry.SprintHungerCost", "饥饿代价(点)", 0, 50, 1, 10)),

            M("净化", "技能解锁词条 · 寒冷固定额外清 50%",
                F("SkillRegistry.CleanseEnergy", "能量", 5, 100, 5, 60),
                F("SkillRegistry.CleanseCooldown", "冷却(秒)", 3, 300, 1, 60),
                F("SkillRegistry.CleansePercentPerStatus", "普通负面清除比例(0.1=10%)", 0, 0.5, 0.01, 0.1)),

            M("治疗波", "技能解锁词条",
                F("SkillRegistry.HealEnergy", "能量", 5, 100, 5, 45),
                F("SkillRegistry.HealCooldown", "冷却(秒)", 3, 300, 1, 120),
                F("SkillRegistry.HealInjuryPoints", "回伤(点)", 1, 100, 1, 15),
                F("SkillRegistry.HealPetrifyCostPoints", "石化代价(点)", 0, 50, 1, 5)),

            M("肾上腺素", "技能解锁词条",
                F("SkillRegistry.AdrenalineEnergy", "能量", 5, 100, 5, 40),
                F("SkillRegistry.AdrenalineCooldown", "冷却(秒)", 3, 300, 1, 60),
                F("SkillRegistry.AdrenalineDurationSeconds", "加速时长(秒)", 1, 30, 1, 12),
                F("SkillRegistry.AdrenalineMoveSpeedMod", "加速幅度", 0, 1, 0.05, 0.5),
                F("SkillRegistry.AdrenalineHungerCost", "饥饿代价(点)", 0, 50, 1, 15)),

            M("无敌", "技能解锁词条",
                F("SkillRegistry.InvincibleEnergy", "能量", 5, 200, 5, 80),
                F("SkillRegistry.InvincibleCooldown", "冷却(秒)", 3, 300, 1, 45),
                F("SkillRegistry.InvincibleDurationSeconds", "无敌时长(秒)", 1, 30, 1, 6),
                F("SkillRegistry.InvinciblePetrifyCost", "石化代价(点)", 0, 50, 1, 5)),

            M("机械手", "技能解锁词条",
                F("SkillRegistry.MechanicalHandEnergy", "能量", 5, 100, 5, 45),
                F("SkillRegistry.MechanicalHandCooldown", "冷却(秒)", 3, 300, 1, 60),
                F("HextechAdvancedPatches.MechanicalHand.ExtraDistance", "伸长距离(米)", 1, 20, 1, 5),
                F("HextechAdvancedPatches.MechanicalHand.PetrifyCostPoints", "石化代价(点)", 0, 50, 1, 5)),

            M("羽落", "摔落伤害减免 · 数值越小落地越轻（0.4 = 落地伤害减 60%；最多叠 4 层）",
                F("DefaultHextechs.FeatherFallFactorPerStack", "每层摔落伤害 ×倍", 0.05, 1, 0.05, 0.4)),

            M("投掷专家", "投掷力度",
                F("DefaultHextechs.ThrowMasterFactorPerStack", "力度倍数", 1, 2, 0.05, 1.2)),

            M("不倒翁", "硬直缩短",
                F("DefaultHextechs.TumblerStunFactorPerStack", "硬直系数", 0.3, 1, 0.05, 0.65)),

            M("长跑运动员", "冲刺消耗",
                F("DefaultHextechs.MarathonerSprintFactorPerStack", "冲刺耗体系数", 0.5, 1, 0.05, 0.8)),

            M("机能零食", "拾起削减负面",
                F("DefaultHextechs.SnackReduceRatioPerStack", "单层削减比例", 0.01, 0.2, 0.01, 0.05)),

            M("皮糙肉厚", "伤势积累减免",
                F("DefaultHextechs.ThickHidePerStack", "积累系数", 0.5, 1, 0.01, 0.75)),

            M("抗毒体质", "中毒积累减免",
                F("DefaultHextechs.AntidoteBodyPerStack", "积累系数", 0.5, 1, 0.01, 0.75)),

            M("隔热层", "灼热积累减免",
                F("DefaultHextechs.HeatShieldPerStack", "积累系数", 0.5, 1, 0.01, 0.8)),

            M("破咒者", "诅咒积累减免",
                F("DefaultHextechs.CurseBreakerPerStack", "积累系数", 0.5, 1, 0.01, 0.8)),

            M("冷血体质", "负面词条捎带的晒伤减免",
                F("DefaultHextechs.ColdBloodedHeatResistPerStack", "晒伤积累系数", 0.1, 1, 0.01, 0.5)),

            M("石化抗性", "石化值削减",
                F("DefaultHextechs.PetrifyWardReduction", "削减比例", 0, 0.9, 0.01, 0.3333)),

            M("倒吊人", "负面词条：伤势填充",
                F("DefaultHextechs.HangedManInjuryRatio", "伤势填充", 0.9, 1, 0.01, 0.99)),

            M("不死鸟", "摔落保底",
                F("DefaultHextechs.PhoenixMinInjuryLeft", "保底伤势（0.2 = 20 点）", 0, 0.5, 0.01, 0.2)),

            M("手滑", "负面词条：投掷变弱",
                F("AdvancedHextechs.ButterfingersThrowFactorPerStack", "投掷系数", 0.3, 1, 0.05, 0.7)),

            M("拖后腿", "负面词条：光环",
                F("AdvancedHextechs.DeadweightStaminaPerStack", "耗体倍率", 1, 2, 0.02, 1.12)),

            M("收藏家", "代币积累加速",
                F("AdvancedHextechs.CollectorTokenBonusPerStack", "代币加成", 0, 2, 0.1, 0.5)),

            M("资本家", "被动代币收入",
                F("AdvancedHextechs.CapitalistTokensPerMinute", "代币/分", 0, 5, 0.1, 0.5)),

            M("围炉谈心", "贴贴回复",
                F("AdvancedHextechs.FiresideIntervalSeconds", "间隔(秒)", 5, 60, 1, 10)),

            M("送葬人", "队友倒地救援",
                F("AdvancedHextechs.UndertakerRadius", "触发半径", 5, 30, 1, 10),
                F("AdvancedHextechs.UndertakerStamina", "回体(点)", 5, 100, 5, 20),
                F("AdvancedHextechs.UndertakerBuffSeconds", "加速(秒)", 1, 15, 0.5, 4)),

            M("拉拉手", "扶起队友",
                F("AdvancedHextechs.GrabHandDistanceMultiplier", "距离倍数", 1, 3, 0.1, 1.5),
                F("AdvancedHextechs.GrabHandStamina", "回体(点)", 5, 100, 5, 20)),

            M("人人为我", "队友阵亡回体",
                F("AdvancedHextechs.AllForOneExtraStamina", "额外体力", 10, 300, 10, 60)),

            M("士气大提升", "离篝火加速",
                F("AdvancedHextechs.MoraleBoostSeconds", "时长(秒)", 5, 60, 1, 16)),

            M("背包客", "背包扩容",
                F("AdvancedHextechs.BackpackerExtraSlots", "额外格数", 0, 6, 1, 2, isInt: true)),

            M("我还有光", "石化转体力",
                F("AdvancedHextechs.StillLightConvertPerSecond", "转化速率", 0.01, 0.2, 0.01, 0.04),
                F("AdvancedHextechs.StillLightConvertThreshold", "转化阈值", 0.2, 0.9, 0.05, 0.5)),

            M("精致胃袋", "食物效果增减",
                F("HextechAdvancedPatches.RefinedStomach.WildFoodMultiplier", "野生食物倍率", 0, 1, 0.05, 0.5),
                F("HextechAdvancedPatches.RefinedStomach.PackagedFoodMultiplier", "包装食物倍率", 0.5, 5, 0.25, 2)),
        };

        return modules;
    }

    private static ModuleDef M(string title, string subtitle, params BalanceField[] fields)
    {
        return new ModuleDef
        {
            Title = title,
            Subtitle = subtitle,
            Fields = new List<BalanceField>(fields),
        };
    }

    private static BalanceField F(string key, string label, double min, double max, double step, double def, bool isInt = false)
    {
        return new BalanceField
        {
            Key = key,
            Label = label,
            Min = min,
            Max = max,
            Step = step,
            Default = def,
            IsInt = isInt,
        };
    }
}
