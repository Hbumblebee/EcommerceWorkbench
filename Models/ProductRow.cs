/*
 * 功能说明：批量计算表格中的商品行数据模型（可编辑输入 + 自动计算结果）。
 * 创建日期：2026-07-30
 * 修改记录：
 *   2026-07-30 新增 SKU 唯一键字段
 *   2026-08-14 迁入 EcommerceWorkbench
 */
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EcommerceWorkbench.Models;

public sealed class ProductRow : INotifyPropertyChanged
{
    private int _index;
    private string? _sku;
    private double? _cost;
    private double? _weight;
    private double? _expectedProfit;
    private string? _discountedPrice;
    private string? _priceBeforeDiscount;
    private string? _profitRate;
    private string? _orderRevenue;
    private string? _adjRate;
    private string? _hiddenFee;
    private string? _buyerSf;
    private string? _sellerSf;
    private string? _commission;
    private string? _tradeHf;
    private string? _activitySc;
    private string? _withdrawHf;
    private string? _status;

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Index
    {
        get => _index;
        set => Set(ref _index, value);
    }

    /// <summary>SKU（手动维护，行唯一键）</summary>
    public string? Sku
    {
        get => _sku;
        set => Set(ref _sku, value);
    }

    /// <summary>商品成本价（CNY）</summary>
    public double? Cost
    {
        get => _cost;
        set => Set(ref _cost, value);
    }

    /// <summary>包裹重量（g）</summary>
    public double? Weight
    {
        get => _weight;
        set => Set(ref _weight, value);
    }

    /// <summary>预期净利润（CNY）</summary>
    public double? ExpectedProfit
    {
        get => _expectedProfit;
        set => Set(ref _expectedProfit, value);
    }

    public string? DiscountedPrice
    {
        get => _discountedPrice;
        set => Set(ref _discountedPrice, value);
    }

    public string? PriceBeforeDiscount
    {
        get => _priceBeforeDiscount;
        set => Set(ref _priceBeforeDiscount, value);
    }

    public string? ProfitRate
    {
        get => _profitRate;
        set => Set(ref _profitRate, value);
    }

    public string? OrderRevenue
    {
        get => _orderRevenue;
        set => Set(ref _orderRevenue, value);
    }

    public string? AdjRate
    {
        get => _adjRate;
        set => Set(ref _adjRate, value);
    }

    public string? HiddenFee
    {
        get => _hiddenFee;
        set => Set(ref _hiddenFee, value);
    }

    public string? BuyerSf
    {
        get => _buyerSf;
        set => Set(ref _buyerSf, value);
    }

    public string? SellerSf
    {
        get => _sellerSf;
        set => Set(ref _sellerSf, value);
    }

    public string? Commission
    {
        get => _commission;
        set => Set(ref _commission, value);
    }

    public string? TradeHf
    {
        get => _tradeHf;
        set => Set(ref _tradeHf, value);
    }

    public string? ActivitySc
    {
        get => _activitySc;
        set => Set(ref _activitySc, value);
    }

    public string? WithdrawHf
    {
        get => _withdrawHf;
        set => Set(ref _withdrawHf, value);
    }

    public string? Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public bool HasAnyInput =>
        !string.IsNullOrWhiteSpace(Sku)
        || Cost is not null
        || Weight is not null
        || ExpectedProfit is not null;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
