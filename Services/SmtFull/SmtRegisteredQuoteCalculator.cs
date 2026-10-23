/*
 * 功能说明：按计算表「普货挂号包邮人民币报价」把成本和重量算成速卖通售价与各国调价。
 * 主要职责：售价用表下半区（基准续重 70、挂号 20、佣金 15%、利润率 25%、店铺折扣 25%、再除 0.94）；
 *           各国调价用表上半区（相对基准续重 60、挂号 20，再除 0.92 和链接折扣 0.75）。
 *           哥伦比亚、摩洛哥不走运费差，分别是速卖通售价的 20% 和 50%。
 * 创建日期：2026-10-22
 * 修改记录：2026-10-22 公式参数改为可传入，供页面折叠区调整后保存
 */

namespace EcommerceWorkbench.Services.SmtFull;

public sealed class SmtCountryAdjust
{
    public string Name { get; init; } = "";
    /// <summary>速卖通区域调价使用的国家代码。</summary>
    public string Code { get; init; } = "";
    /// <summary>相对基准价的增减金额（人民币）。正数加价，负数减价。</summary>
    public double Amount { get; init; }
}

public sealed class SmtRegisteredQuote
{
    public double CostCny { get; init; }
    public double WeightKg { get; init; }
    public double FreightCny { get; init; }
    public double UndiscountedPrice { get; init; }
    public double Profit { get; init; }
    public double SmtPrice { get; init; }
    public IReadOnlyList<SmtCountryAdjust> Countries { get; init; } = [];
}

public sealed class SmtFreightRate
{
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    /// <summary>该国无忧续重，元/kg。</summary>
    public double PerKg { get; set; }
    /// <summary>该国挂号费，元。</summary>
    public double Registration { get; set; }
}

public sealed class SmtPercentRate
{
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    /// <summary>占速卖通售价的比例，0.20 表示 20%。</summary>
    public double Rate { get; set; }
}

/// <summary>普货挂号包邮公式里可调整的参数。默认值对齐计算表。</summary>
public sealed class SmtQuoteFormula
{
    public double SaleRatePerKg { get; set; } = SmtRegisteredQuoteCalculator.SaleRatePerKg;
    public double SaleRegistration { get; set; } = SmtRegisteredQuoteCalculator.SaleRegistration;
    public double CommissionRate { get; set; } = SmtRegisteredQuoteCalculator.CommissionRate;
    public double ProfitRate { get; set; } = SmtRegisteredQuoteCalculator.ProfitRate;
    public double ShopDiscount { get; set; } = SmtRegisteredQuoteCalculator.ShopDiscount;
    public double SaleDivisor { get; set; } = SmtRegisteredQuoteCalculator.SaleDivisor;
    public double AdjustRatePerKg { get; set; } = SmtRegisteredQuoteCalculator.AdjustRatePerKg;
    public double AdjustRegistration { get; set; } = SmtRegisteredQuoteCalculator.AdjustRegistration;
    public double AdjustDivisor { get; set; } = SmtRegisteredQuoteCalculator.AdjustDivisor;
    public List<SmtFreightRate> FreightCountries { get; set; } = [];
    public List<SmtPercentRate> PercentCountries { get; set; } = [];

    public static SmtQuoteFormula CreateDefault()
    {
        return new SmtQuoteFormula
        {
            FreightCountries = SmtRegisteredQuoteCalculator.DefaultFreightCountries()
                .Select(item => new SmtFreightRate
                {
                    Name = item.Name,
                    Code = item.Code,
                    PerKg = item.PerKg,
                    Registration = item.Registration
                })
                .ToList(),
            PercentCountries = SmtRegisteredQuoteCalculator.DefaultPercentCountries()
                .Select(item => new SmtPercentRate
                {
                    Name = item.Name,
                    Code = item.Code,
                    Rate = item.Rate
                })
                .ToList()
        };
    }
}

public static class SmtRegisteredQuoteCalculator
{
    /// <summary>表下半区基准续重（元/kg），单元格 A64。</summary>
    public const double SaleRatePerKg = 70;
    /// <summary>表下半区基准挂号费，单元格 B64。</summary>
    public const double SaleRegistration = 20;
    /// <summary>公式里写死的平台扣费。</summary>
    public const double CommissionRate = 0.15;
    /// <summary>表头利润率，单元格 H64。</summary>
    public const double ProfitRate = 0.25;
    /// <summary>店铺折扣 / 链接折扣，单元格 A3、G64。</summary>
    public const double ShopDiscount = 0.25;
    /// <summary>售价公式里的除数。</summary>
    public const double SaleDivisor = 0.94;

    /// <summary>表上半区运费差额的基准续重，单元格 B3。</summary>
    public const double AdjustRatePerKg = 60;
    /// <summary>表上半区运费差额的基准挂号费，单元格 C3。</summary>
    public const double AdjustRegistration = 20;
    /// <summary>国家调价公式里的除数。</summary>
    public const double AdjustDivisor = 0.92;

    public readonly record struct FreightCountry(string Name, string Code, double PerKg, double Registration);
    public readonly record struct PercentCountry(string Name, string Code, double Rate);

    /// <summary>第 33–60 行的无忧续重和挂号费。英国用速卖通区域调价代码 UK。</summary>
    private static readonly FreightCountry[] FreightCountries =
    [
        new("俄罗斯", "RU", 81, 19),
        new("西班牙", "ES", 53, 18),
        new("法国", "FR", 76, 16),
        new("巴西", "BR", 66, 25),
        new("美国", "US", 60, 26.2),
        new("韩国", "KR", 27, 15.5),
        new("沙特阿拉伯", "SA", 109.6, 52.03),
        new("以色列", "IL", 79, 19),
        new("墨西哥", "MX", 65, 22),
        new("智利", "CL", 105, 20),
        new("乌克兰", "UA", 98, 7),
        new("波兰", "PL", 59, 12),
        new("白俄罗斯", "BY", 136, 21),
        new("德国", "DE", 58, 18),
        new("英国", "UK", 54.5, 15.5),
        new("荷兰", "NL", 55, 23),
        new("意大利", "IT", 51.5, 23),
        new("日本", "JP", 60.5, 20.8),
        new("澳大利亚", "AU", 89.66, 18),
        new("加拿大", "CA", 109.5, 27.2),
        new("印度尼西亚", "ID", 54.6, 16.8),
        new("马来西亚", "MY", 23, 8),
        new("菲律宾", "PH", 113.8, 17.6),
        new("越南", "VN", 27, 6),
        new("泰国", "TH", 40, 14),
        new("阿联酋", "AE", 132.04, 15.85),
        new("土耳其", "TR", 100, 20),
        new("葡萄牙", "PT", 68, 20)
    ];

    private static readonly PercentCountry[] PercentCountries =
    [
        new("哥伦比亚", "CO", 0.20),
        new("摩洛哥", "MA", 0.50)
    ];

    public static IReadOnlyList<FreightCountry> DefaultFreightCountries() => FreightCountries;

    public static IReadOnlyList<PercentCountry> DefaultPercentCountries() => PercentCountries;

    public static SmtRegisteredQuote Calculate(double costCny, double weightKg, SmtQuoteFormula? formula = null)
    {
        formula ??= SmtQuoteFormula.CreateDefault();
        var saleRate = formula.SaleRatePerKg;
        var saleReg = formula.SaleRegistration;
        var commission = formula.CommissionRate;
        var profitRate = formula.ProfitRate;
        var discount = formula.ShopDiscount;
        var saleDivisor = formula.SaleDivisor == 0 ? SaleDivisor : formula.SaleDivisor;
        var adjustRate = formula.AdjustRatePerKg;
        var adjustReg = formula.AdjustRegistration;
        var adjustDivisor = formula.AdjustDivisor == 0 ? AdjustDivisor : formula.AdjustDivisor;
        var discountLeft = 1 - discount;
        if (discountLeft == 0)
            discountLeft = 1 - ShopDiscount;
        var net = 1 - commission - profitRate;
        if (net == 0)
            net = 1 - CommissionRate - ProfitRate;

        var freight = weightKg * saleRate + saleReg;
        var undiscounted = (freight + costCny) / net / saleDivisor;
        var profit = undiscounted * (1 - commission) - costCny - freight;
        var smtPrice = undiscounted / discountLeft / saleDivisor;

        var freightCountries = formula.FreightCountries.Count > 0
            ? formula.FreightCountries
            : DefaultFreightCountries().Select(item => new SmtFreightRate
            {
                Name = item.Name,
                Code = item.Code,
                PerKg = item.PerKg,
                Registration = item.Registration
            }).ToList();
        var percentCountries = formula.PercentCountries.Count > 0
            ? formula.PercentCountries
            : DefaultPercentCountries().Select(item => new SmtPercentRate
            {
                Name = item.Name,
                Code = item.Code,
                Rate = item.Rate
            }).ToList();

        var countries = new List<SmtCountryAdjust>(freightCountries.Count + percentCountries.Count);
        foreach (var country in freightCountries)
        {
            if (string.IsNullOrWhiteSpace(country.Code))
                continue;
            var raw = ((country.PerKg - adjustRate) * weightKg + (country.Registration - adjustReg))
                      / adjustDivisor / discountLeft;
            countries.Add(new SmtCountryAdjust
            {
                Name = country.Name,
                Code = country.Code.Trim(),
                Amount = RoundMoney(raw)
            });
        }

        foreach (var country in percentCountries)
        {
            if (string.IsNullOrWhiteSpace(country.Code))
                continue;
            countries.Add(new SmtCountryAdjust
            {
                Name = country.Name,
                Code = country.Code.Trim(),
                Amount = RoundMoney(smtPrice * country.Rate)
            });
        }

        return new SmtRegisteredQuote
        {
            CostCny = costCny,
            WeightKg = weightKg,
            FreightCny = RoundMoney(freight),
            UndiscountedPrice = RoundMoney(undiscounted),
            Profit = RoundMoney(profit),
            SmtPrice = RoundMoney(smtPrice),
            Countries = countries
        };
    }

    public static double RoundMoney(double value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
