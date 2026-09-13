/*
 * 功能说明：速卖通7（全托2.0）定价表格行。
 * 主要职责：承接页面 SKU、搜索 SKU、成本/重量，以及 Excel 全托2.0 各计算列。
 * 创建日期：2026-09-13
 */
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EcommerceWorkbench.Models;

public sealed class Smt7Row : INotifyPropertyChanged
{
    private int _index;
    private string? _pageSku;
    private string? _sku;
    private double? _cost;
    private double? _weight;
    private string? _convertedWeight;
    private string? _interval;
    private string? _margin;
    private string? _factor1;
    private string? _constant;
    private string? _factor2;
    private string? _retailPrice;
    private string? _markup;
    private string? _finalPrice;
    private string? _status;

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Index
    {
        get => _index;
        set => Set(ref _index, value);
    }

    /// <summary>店小秘速卖通全托管详情页「变种信息」SKU 编码（只读，用于回写一对一匹配）。</summary>
    public string? PageSku
    {
        get => _pageSku;
        set => Set(ref _pageSku, value);
    }

    /// <summary>用于店小秘商品搜索的 SKU，可编辑；默认等于页面 SKU。</summary>
    public string? Sku
    {
        get => _sku;
        set => Set(ref _sku, value);
    }

    /// <summary>产品成本（人民币），来自店小秘参考价。</summary>
    public double? Cost
    {
        get => _cost;
        set => Set(ref _cost, value);
    }

    /// <summary>重量（克），可编辑；与换算重量按 kg = g/1000 同步。</summary>
    public double? Weight
    {
        get => _weight;
        set => Set(ref _weight, value);
    }

    /// <summary>换算重量（千克），可编辑；用于重量加价分档，与重量(g)互相同步。</summary>
    public string? ConvertedWeight
    {
        get => _convertedWeight;
        set => Set(ref _convertedWeight, value);
    }

    public string? Interval
    {
        get => _interval;
        set => Set(ref _interval, value);
    }

    public string? Margin
    {
        get => _margin;
        set => Set(ref _margin, value);
    }

    public string? Factor1
    {
        get => _factor1;
        set => Set(ref _factor1, value);
    }

    public string? Constant
    {
        get => _constant;
        set => Set(ref _constant, value);
    }

    public string? Factor2
    {
        get => _factor2;
        set => Set(ref _factor2, value);
    }

    public string? RetailPrice
    {
        get => _retailPrice;
        set => Set(ref _retailPrice, value);
    }

    public string? Markup
    {
        get => _markup;
        set => Set(ref _markup, value);
    }

    /// <summary>最终价，回写到详情页变种「供货价」。</summary>
    public string? FinalPrice
    {
        get => _finalPrice;
        set => Set(ref _finalPrice, value);
    }

    public string? Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public bool HasAnyInput =>
        !string.IsNullOrWhiteSpace(PageSku) || !string.IsNullOrWhiteSpace(Sku) || Cost is not null;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
