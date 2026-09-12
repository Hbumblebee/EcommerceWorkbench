/*
 * 功能说明：对齐「副本计算表.xls」JOOM1.0 页的售价与利润计算。
 * 主要职责：按人民币成本分档取佣金/毛利率，再按固定汇率反推美元售价与人民币利润。
 * 创建日期：2026-09-05
 */

namespace EcommerceWorkbench.Services.Joom;

public sealed class JoomTierParams
{
    public double Commission { get; set; }
    public double Margin { get; set; }
}

public sealed class JoomSettings
{
    public const double DefaultExchangeRate = 6.3;

    public double ExchangeRate { get; set; } = DefaultExchangeRate;

    /// <summary>成本 ≤ 10 元。</summary>
    public JoomTierParams Low { get; set; } = new() { Commission = 0.15, Margin = 0.50 };

    /// <summary>10 元 &lt; 成本 ≤ 20 元。</summary>
    public JoomTierParams Mid { get; set; } = new() { Commission = 0.15, Margin = 0.45 };

    /// <summary>成本 &gt; 20 元。</summary>
    public JoomTierParams High { get; set; } = new() { Commission = 0.15, Margin = 0.40 };
}

public sealed class JoomCalcResult
{
    public string Interval { get; set; } = "";
    public double Commission { get; set; }
    public double Margin { get; set; }
    public double PriceUsd { get; set; }
    public double ProfitCny { get; set; }
}

public static class JoomCalculator
{
    /// <summary>
    /// 对齐 Excel：区间/佣金/毛利率用 IFS(成本&lt;=10, …, 成本&lt;=20, …, TRUE, …)；
    /// 售价 = ROUND(成本/汇率/(1-佣金-毛利率), 2)；
    /// 利润额 = ROUND(售价*汇率-成本-(售价*佣金*汇率), 2)。
    /// </summary>
    public static JoomCalcResult Calculate(double costCny, JoomSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.ExchangeRate == 0)
            throw new InvalidOperationException("汇率不能为 0");

        var (interval, commission, margin) = ResolveTier(costCny, settings);
        var denom = 1 - commission - margin;
        if (Math.Abs(denom) < 1e-12)
            throw new InvalidOperationException("平台佣金与毛利率合计不能为 100%");

        var priceUsd = Math.Round(
            costCny / settings.ExchangeRate / denom,
            2,
            MidpointRounding.AwayFromZero);

        var profitCny = Math.Round(
            priceUsd * settings.ExchangeRate - costCny - priceUsd * commission * settings.ExchangeRate,
            2,
            MidpointRounding.AwayFromZero);

        return new JoomCalcResult
        {
            Interval = interval,
            Commission = commission,
            Margin = margin,
            PriceUsd = priceUsd,
            ProfitCny = profitCny
        };
    }

    public static (string Interval, double Commission, double Margin) ResolveTier(double costCny, JoomSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (costCny <= 10)
            return ("0-10元", settings.Low.Commission, settings.Low.Margin);
        if (costCny <= 20)
            return ("10-20元", settings.Mid.Commission, settings.Mid.Margin);
        return ("20元以上", settings.High.Commission, settings.High.Margin);
    }
}
