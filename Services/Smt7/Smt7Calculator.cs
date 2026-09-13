/*
 * 功能说明：对齐「副本计算表.xls」全托2.0 页的零售价、重量加价与最终价。
 * 主要职责：按人民币成本分档取分子1/常量/分子2，再按换算重量（千克）加价。
 * 创建日期：2026-09-13
 * 更新日期：2026-09-13 搜索重量为克，换算重量=克/1000（kg）；分档参数可由界面调整
 */

namespace EcommerceWorkbench.Services.Smt7;

public sealed class Smt7CostTier
{
    public string Interval { get; set; } = "";
    public double? MaxExclusiveCost { get; set; }
    public double MarginPercent { get; set; }
    public double Factor1 { get; set; }
    public double Constant { get; set; }
    public double Factor2 { get; set; }
}

public sealed class Smt7WeightTier
{
    public string Label { get; set; } = "";
    public double? MaxExclusiveKg { get; set; }
    public double Markup { get; set; }
}

public sealed class Smt7Settings
{
    public List<Smt7CostTier> CostTiers { get; set; } = [];
    public List<Smt7WeightTier> WeightTiers { get; set; } = [];

    public static Smt7Settings CreateDefault() => new()
    {
        CostTiers =
        [
            new() { Interval = "2元以下", MaxExclusiveCost = 2, MarginPercent = 35, Factor1 = 0.50, Constant = 0.50, Factor2 = 0.94 },
            new() { Interval = "2-4元", MaxExclusiveCost = 4, MarginPercent = 30, Factor1 = 0.55, Constant = 0.50, Factor2 = 0.94 },
            new() { Interval = "4-8元", MaxExclusiveCost = 8, MarginPercent = 25, Factor1 = 0.58, Constant = 0.50, Factor2 = 0.94 },
            new() { Interval = "8-20元", MaxExclusiveCost = 20, MarginPercent = 25, Factor1 = 0.60, Constant = 0.50, Factor2 = 0.94 },
            new() { Interval = "20-30元", MaxExclusiveCost = 30, MarginPercent = 27, Factor1 = 0.62, Constant = 0.50, Factor2 = 0.94 },
            new() { Interval = "30元以上", MaxExclusiveCost = null, MarginPercent = 25, Factor1 = 0.65, Constant = 1.00, Factor2 = 0.94 }
        ],
        WeightTiers =
        [
            new() { Label = "[0,0.2)", MaxExclusiveKg = 0.2, Markup = 0 },
            new() { Label = "[0.2,0.5)", MaxExclusiveKg = 0.5, Markup = 0.50 },
            new() { Label = "[0.5,1)", MaxExclusiveKg = 1.0, Markup = 1.00 },
            new() { Label = "[1,∞)", MaxExclusiveKg = null, Markup = 2.00 }
        ]
    };
}

public sealed class Smt7CalcResult
{
    public double ConvertedWeightKg { get; set; }
    public string Interval { get; set; } = "";
    public string Margin { get; set; } = "";
    public double Factor1 { get; set; }
    public double Constant { get; set; }
    public double Factor2 { get; set; }
    public double RetailPrice { get; set; }
    public double Markup { get; set; }
    public double FinalPrice { get; set; }
}

public static class Smt7Calculator
{
    /// <summary>
    /// 对齐 Excel 全托2.0。搜索接口重量为克，换算重量 = 克/1000（kg），加价按换算重量(kg)分档。
    /// 零售价 = ROUND((成本/分子1 + 常量)/分子2, 2)；
    /// 最终价 = ROUND(零售价 + 重量加价, 2)。
    /// </summary>
    public static Smt7CalcResult Calculate(double costCny, double? weightGrams, Smt7Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.CostTiers.Count == 0)
            throw new InvalidOperationException("未配置成本分档。");
        if (settings.WeightTiers.Count == 0)
            throw new InvalidOperationException("未配置重量加价。");

        var tier = ResolveCostTier(costCny, settings);
        if (Math.Abs(tier.Factor1) < 1e-12)
            throw new InvalidOperationException("分子1不能为 0");
        if (Math.Abs(tier.Factor2) < 1e-12)
            throw new InvalidOperationException("分子2不能为 0");

        var convertedKg = (weightGrams ?? 0) / 1000.0;
        var retail = Math.Round(
            (costCny / tier.Factor1 + tier.Constant) / tier.Factor2,
            2,
            MidpointRounding.AwayFromZero);
        var markup = ResolveMarkup(convertedKg, settings);
        var finalPrice = Math.Round(retail + markup, 2, MidpointRounding.AwayFromZero);

        return new Smt7CalcResult
        {
            ConvertedWeightKg = convertedKg,
            Interval = string.IsNullOrWhiteSpace(tier.Interval) ? BuildCostInterval(settings, tier) : tier.Interval,
            Margin = tier.MarginPercent.ToString("0.##") + "%毛利",
            Factor1 = tier.Factor1,
            Constant = tier.Constant,
            Factor2 = tier.Factor2,
            RetailPrice = retail,
            Markup = markup,
            FinalPrice = finalPrice
        };
    }

    public static void ApplyIntervalNames(Smt7Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        double? prevCost = null;
        foreach (var tier in settings.CostTiers)
        {
            tier.Interval = BuildCostInterval(prevCost, tier.MaxExclusiveCost);
            prevCost = tier.MaxExclusiveCost;
        }

        double? prevKg = 0;
        foreach (var tier in settings.WeightTiers)
        {
            var max = tier.MaxExclusiveKg;
            tier.Label = max is null
                ? $"[{FormatThreshold(prevKg ?? 0)},∞)"
                : $"[{FormatThreshold(prevKg ?? 0)},{FormatThreshold(max.Value)})";
            prevKg = max;
        }
    }

    private static string BuildCostInterval(Smt7Settings settings, Smt7CostTier current)
    {
        double? prev = null;
        foreach (var tier in settings.CostTiers)
        {
            if (ReferenceEquals(tier, current))
                return BuildCostInterval(prev, tier.MaxExclusiveCost);
            prev = tier.MaxExclusiveCost;
        }

        return current.Interval;
    }

    private static string BuildCostInterval(double? prevMax, double? max)
    {
        if (max is null)
            return prevMax is null ? "全部" : FormatThreshold(prevMax.Value) + "元以上";
        if (prevMax is null)
            return FormatThreshold(max.Value) + "元以下";
        return FormatThreshold(prevMax.Value) + "-" + FormatThreshold(max.Value) + "元";
    }

    private static string FormatThreshold(double v)
    {
        return v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static Smt7CostTier ResolveCostTier(double costCny, Smt7Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        foreach (var tier in settings.CostTiers)
        {
            if (tier.MaxExclusiveCost is null || costCny < tier.MaxExclusiveCost.Value)
                return tier;
        }

        return settings.CostTiers[^1];
    }

    public static double ResolveMarkup(double convertedKg, Smt7Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        foreach (var tier in settings.WeightTiers)
        {
            if (tier.MaxExclusiveKg is null || convertedKg < tier.MaxExclusiveKg.Value)
                return tier.Markup;
        }

        return settings.WeightTiers[^1].Markup;
    }
}
