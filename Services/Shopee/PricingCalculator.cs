/*
 * 功能说明：对齐 Shopee 定价模拟器前端 JS（xe / ke）的定价与运费计算逻辑。
 * 创建日期：2026-07-30
 * 修改记录：
 *   2026-08-14 迁入 EcommerceWorkbench
 *   2026-08-14 增加按预期净利润率反推售价
 *   2026-08-15 站点调价比例按官网 Ce：百分数四舍五入保留两位小数
 *   2026-08-15 输入按官网 ShopeeInput precision:2 先四舍五入；调价比例按 Ce 存百分数，避免再乘 100 丢 0.01
 */
using EcommerceWorkbench.Models;

namespace EcommerceWorkbench.Services.Shopee;

public sealed class PricingInput
{
    public double Cost { get; set; }
    public double Weight { get; set; }
    public double ExpectedProfit { get; set; }
    public double ExpectedProfitRatePercent { get; set; }
    public double ExchangeRate { get; set; } = 4.77;
    public double DiscountPercent { get; set; } = 30;
    public double WithinBorderSf { get; set; } = 1;
    public double CommissionRatePercent { get; set; } = 14;
    public double TradeHfRatePercent { get; set; } = 2.5;
    public double ActivityScRatePercent { get; set; } = 1;
    public double WithdrawHfRatePercent { get; set; } = 1;
}

public sealed class ShippingFeeResult
{
    public double HiddenFee { get; set; }
    public double BuyerFee { get; set; }
    public string HiddenFeeFormula { get; set; } = "";
    public string BuyerFeeFormula { get; set; } = "";
}

public sealed class PricingResult
{
    public double PriceBeforeDiscount { get; set; }
    public double DiscountedPrice { get; set; }
    public double Profit { get; set; }
    public double ProfitRate { get; set; }
    public double OrderRevenue { get; set; }
    /// <summary>站点调价比例百分数，例如 441.05，对齐官网 Ce。</summary>
    public double AdjRate { get; set; }
    public double Commission { get; set; }
    public double TradeHf { get; set; }
    public double ActivitySc { get; set; }
    public double SellerSf { get; set; }
    public double HiddenFee { get; set; }
    public double BuyerSf { get; set; }
    public double WithdrawHf { get; set; }
    public string HiddenFeeFormula { get; set; } = "";
    public string BuyerFeeFormula { get; set; } = "";
}

public static class PricingCalculator
{
    /// <summary>
    /// 计算模式：通过输入预期净利润，计算售价和净利润率（对齐网页 get_price_by_profit）。
    /// </summary>
    public static PricingResult CalculateByExpectedProfit(PricingInput input, IReadOnlyList<FeeMode> feeModes)
    {
        var e = AlignWebsitePrecision(input);
        var shipping = CalculateShippingFee(e.Weight, feeModes);

        // 与官网 xe() get_price_by_profit 同一条表达式，避免拆项造成 1 ulp 差
        double o = e.Cost * e.ExchangeRate;
        double r = e.ExpectedProfit * e.ExchangeRate;
        double m = e.WithinBorderSf * e.ExchangeRate;
        double s = e.CommissionRatePercent / 100.0;
        double c = e.TradeHfRatePercent / 100.0;
        double l = e.ActivityScRatePercent / 100.0;
        double u = e.WithdrawHfRatePercent / 100.0;
        double p = shipping.HiddenFee;
        double f = shipping.BuyerFee;

        double denom = 1 - s - c - l;
        if (Math.Abs(denom) < 1e-12)
            throw new InvalidOperationException("费率合计不能为 100%");

        double n = ((o + r + m) / (1 - u) + p + f * c) / denom;
        return BuildResult(e, shipping, o, n, r, s, c, l, u, p, f);
    }

    /// <summary>
    /// 通过预期净利润率反推折后售价与净利润金额，再计算其余费用（与 get_price_by_profit 互为反函数）。
    /// </summary>
    public static PricingResult CalculateByExpectedProfitRate(PricingInput input, IReadOnlyList<FeeMode> feeModes)
    {
        var e = AlignWebsitePrecision(input);
        var shipping = CalculateShippingFee(e.Weight, feeModes);

        // 与官网 xe() get_price_by_profit_rate 同一条表达式
        double o = e.Cost * e.ExchangeRate;
        double m = e.WithinBorderSf * e.ExchangeRate;
        double s = e.CommissionRatePercent / 100.0;
        double c = e.TradeHfRatePercent / 100.0;
        double l = e.ActivityScRatePercent / 100.0;
        double u = e.WithdrawHfRatePercent / 100.0;
        double i = e.ExpectedProfitRatePercent / 100.0;
        double p = shipping.HiddenFee;
        double f = shipping.BuyerFee;

        if (Math.Abs(1 - s - c - l) < 1e-12)
            throw new InvalidOperationException("费率合计不能为 100%");
        if (u >= 1 - 1e-12)
            throw new InvalidOperationException("提现手续费不能为 100%");

        double denom = 1 - s - c - l - i / (1 - u);
        if (denom <= 1e-12)
            throw new InvalidOperationException("净利润率过高，无法计算售价");

        double n = ((o + m) / (1 - u) + p + f * c) / denom;
        double r = n * i;
        return BuildResult(e, shipping, o, n, r, s, c, l, u, p, f);
    }

    private static PricingResult BuildResult(
        PricingInput input,
        ShippingFeeResult shipping,
        double o,
        double n,
        double r,
        double s,
        double c,
        double l,
        double u,
        double p,
        double f)
    {
        double i = n == 0 ? 0 : r / n;
        double h = n * s;
        double v = (n + f) * c;
        double g = n * l;
        double orderRevenue = n - p - h - v - g;
        double withdraw = orderRevenue * u;
        // 官网：S=n/(1-discount/100)，y=(S*(1-s-l-c)-p)/o；展示 we(S)、Ce(y)
        double priceBeforeDiscount = n / (1 - input.DiscountPercent / 100.0);
        double adjRate = o == 0 ? 0 : (priceBeforeDiscount * (1 - s - l - c) - p) / o;

        return new PricingResult
        {
            PriceBeforeDiscount = Round2(priceBeforeDiscount),
            DiscountedPrice = Round2(n),
            Profit = Round2(r),
            ProfitRate = Round4(i),
            OrderRevenue = Round2(orderRevenue),
            // Ce(y)=(100*y).toFixed(2)：存百分数 441.05，展示时不再乘 100
            AdjRate = Round2(adjRate * 100.0),
            Commission = Round2(h),
            TradeHf = Round2(v),
            ActivitySc = Round2(g),
            SellerSf = Round2(p + f),
            HiddenFee = Round2(p),
            BuyerSf = Round2(f),
            WithdrawHf = Round2(withdraw),
            HiddenFeeFormula = shipping.HiddenFeeFormula,
            BuyerFeeFormula = shipping.BuyerFeeFormula
        };
    }

    /// <summary>
    /// 对齐网页 ke(weight, fee_modes)：Flat 优先；否则累加 WeightRange + Increment。
    /// </summary>
    public static ShippingFeeResult CalculateShippingFee(double weight, IReadOnlyList<FeeMode> feeModes)
    {
        var result = new ShippingFeeResult();
        CalcSide(weight, feeModes, "Seller", out var hidden, out var hiddenFormula);
        CalcSide(weight, feeModes, "Buyer", out var buyer, out var buyerFormula);
        result.HiddenFee = hidden;
        result.BuyerFee = buyer;
        result.HiddenFeeFormula = hiddenFormula;
        result.BuyerFeeFormula = buyerFormula;
        return result;
    }

    private static void CalcSide(
        double weight,
        IReadOnlyList<FeeMode> feeModes,
        string type,
        out double fee,
        out string formula)
    {
        fee = 0;
        formula = "";

        var modes = feeModes
            .Where(m => string.Equals(m.Type, type, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(m => m.EndWeight ?? double.PositiveInfinity)
            .ToList();

        if (modes.Count == 0 || weight <= 0)
            return;

        var flat = modes.FirstOrDefault(m => m.Name == "Flat");
        if (flat != null)
        {
            fee = flat.OriginalFee ?? 0;
            formula = fee.ToString("0.##");
            return;
        }

        double sum = 0;
        var parts = new List<string>();

        foreach (var mode in modes)
        {
            double start = mode.StartWeight ?? 0;
            if (mode.Name == "WeightRange")
            {
                if (weight > start)
                {
                    double amount = mode.OriginalFee ?? 0;
                    sum += amount;
                    parts.Add(amount.ToString("0.##"));
                }
            }
            else if (mode.Name == "Increment")
            {
                if (weight > start)
                {
                    double end = mode.EndWeight ?? double.PositiveInfinity;
                    double unit = mode.IncrementUnit ?? 1;
                    double amount = mode.IncrementAmount ?? 0;
                    if (unit <= 0)
                        continue;

                    double span = Math.Min(weight, end) - start;
                    if (double.IsNaN(span) || span == 0)
                        span = 0;

                    double steps = Math.Ceiling(span / unit);
                    double add = steps * amount;
                    sum += add;
                    parts.Add($"向上取整(({Math.Min(weight, end)}-{start})/{unit})*{amount}");
                }
            }
        }

        fee = sum;
        formula = string.Join("+", parts);
    }

    /// <summary>
    /// 对齐官网表单 precision:2（成本/重量/费率/利润率/汇率）。先四舍五入再计算，避免店小秘多位小数与网页差 0.01。
    /// </summary>
    private static PricingInput AlignWebsitePrecision(PricingInput input) => new()
    {
        Cost = Round2(input.Cost),
        Weight = Round2(input.Weight),
        ExpectedProfit = Round2(input.ExpectedProfit),
        ExpectedProfitRatePercent = Round2(input.ExpectedProfitRatePercent),
        ExchangeRate = Round2(input.ExchangeRate),
        DiscountPercent = Round2(input.DiscountPercent),
        WithinBorderSf = Round2(input.WithinBorderSf),
        CommissionRatePercent = Round2(input.CommissionRatePercent),
        TradeHfRatePercent = Round2(input.TradeHfRatePercent),
        ActivityScRatePercent = Round2(input.ActivityScRatePercent),
        WithdrawHfRatePercent = Round2(input.WithdrawHfRatePercent)
    };

    private static double Round2(double v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>网页 Ce 对利润率的格式化精度按 4 位小数展示更稳妥。</summary>
    private static double Round4(double v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);
}
