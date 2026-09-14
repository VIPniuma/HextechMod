namespace PeakModder.HextechMod;

/// <summary>
/// 「保存 / 应用价格预设」这一步的共用动作：商店（调价模式）和海克斯设置面板都有入口，
/// 逻辑与提示文案只留一份，两边点出来的结果一模一样。
/// </summary>
internal static class PricePresetActions
{
    /// <summary>把当前价格存成一份预设（同名覆盖）。</summary>
    public static void Save(string name)
    {
        var replaced = PricePresets.Store(name);
        HextechHud.Toast(replaced ? $"已更新预设「{name}」" : $"已保存为预设「{name}」");
    }

    /// <summary>应用一份预设；<paramref name="preset"/> 传 null 表示切回默认配置。</summary>
    public static void Apply(PricePreset? preset, HextechState? state)
    {
        if (preset == null)
        {
            PricePresets.ApplyDefault(state);
            HextechHud.Toast("已切回默认配置：调价清零，倍率等数值回默认");
            return;
        }

        PricePresets.Apply(preset, state);

        var empty = preset.Prices.Count == 0 ? "（这份预设里没有改过单件价）" : string.Empty;
        var host = ShopPricing.CanEdit ? string.Empty : " · 联机时价格以房主为准";
        HextechHud.Toast($"已应用预设「{preset.Name}」{empty}{host}");
    }
}
