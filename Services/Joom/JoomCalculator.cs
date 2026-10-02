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
    public const double DefaultCommissionPercent = 15;
    public const double DefaultLowMarginPercent = 50;
    public const double DefaultMidMarginPercent = 45;
    public const double DefaultHighMarginPercent = 40;

    public double ExchangeRate { get; set; } = DefaultExchangeRate;

    /// <summary>成本 ≤ 10 元。</summary>
    public JoomTierParams Low { get; set; } = new()
    {
        Commission = DefaultCommissionPercent / 100.0,
        Margin = DefaultLowMarginPercent / 100.0
    };

    /// <summary>10 元 &lt; 成本 ≤ 20 元。</summary>
    public JoomTierParams Mid { get; set; } = new()
    {
        Commission = DefaultCommissionPercent / 100.0,
        Margin = DefaultMidMarginPercent / 100.0
    };

    /// <summary>成本 &gt; 20 元。</summary>
    public JoomTierParams High { get; set; } = new()
    {
        Commission = DefaultCommissionPercent / 100.0,
        Margin = DefaultHighMarginPercent / 100.0
    };
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

        // NaN/∞ 会让 ResolveTier 的比较全部为 false（静默落到最高档）并产出 NaN 售价。
        if (!double.IsFinite(costCny))
            throw new InvalidOperationException("成本必须是有效数字");
        if (!double.IsFinite(settings.ExchangeRate))
            throw new InvalidOperationException("汇率必须是有效数字");
        if (settings.ExchangeRate == 0)
            throw new InvalidOperationException("汇率不能为 0");

        var (interval, commission, margin) = ResolveTier(costCny, settings);

        if (!double.IsFinite(commission) || !double.IsFinite(margin))
            throw new InvalidOperationException("佣金与毛利率必须是有效数字");

        // 原实现只挡「|1-佣金-毛利率| < 1e-12」，>100% 时分母为负会静默产出负售价。
        if (commission < 0 || margin < 0)
            throw new InvalidOperationException("佣金与毛利率不能为负数");
        if (commission + margin >= 1)
            throw new InvalidOperationException("平台佣金与毛利率合计必须小于 100%");

        var denom = 1 - commission - margin;

        var priceUsd = Math.Round(
            costCny / settings.ExchangeRate / denom,
            2,
            MidpointRounding.AwayFromZero);

        if (!double.IsFinite(priceUsd))
            throw new InvalidOperationException("计算结果超出可表示范围，请检查成本与汇率");

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
