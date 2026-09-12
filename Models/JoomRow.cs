/*
 * 功能说明：JOOM 定价表格行（可编辑 SKU/成本 + 自动计算结果）。
 * 创建日期：2026-09-05
 */
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EcommerceWorkbench.Models;

public sealed class JoomRow : INotifyPropertyChanged
{
    private int _index;
    private string? _pageSku;
    private string? _sku;
    private double? _cost;
    private double? _weight;
    private string? _interval;
    private string? _commission;
    private string? _margin;
    private string? _priceUsd;
    private string? _profitCny;
    private string? _status;

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Index
    {
        get => _index;
        set => Set(ref _index, value);
    }

    /// <summary>店小秘 JOOM 产品详情页「变种信息」中的原始 SKU（只读，用于回写一对一匹配）。</summary>
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

    /// <summary>店小秘搜索得到的重量，仅展示；JOOM 售价公式仍按成本计算。</summary>
    public double? Weight
    {
        get => _weight;
        set => Set(ref _weight, value);
    }

    public string? Interval
    {
        get => _interval;
        set => Set(ref _interval, value);
    }

    public string? Commission
    {
        get => _commission;
        set => Set(ref _commission, value);
    }

    public string? Margin
    {
        get => _margin;
        set => Set(ref _margin, value);
    }

    public string? PriceUsd
    {
        get => _priceUsd;
        set => Set(ref _priceUsd, value);
    }

    public string? ProfitCny
    {
        get => _profitCny;
        set => Set(ref _profitCny, value);
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
