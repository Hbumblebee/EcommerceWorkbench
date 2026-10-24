/*
 * 功能说明：速卖通全托标签页的变种行。
 * 主要职责：保存详情页读到的 SKU、属性、搜索到的成本与重量、挂号包邮报价，以及查重后的建议 SKU。
 * 创建日期：2026-10-22
 * 修改记录：2026-10-22 增加成本、重量和挂号包邮计算结果
 *           2026-10-22 增加搜索 SKU：去掉末尾通用后缀、保留颜色
 *           2026-10-24 保存基准运费和各国无忧调价，供 SKU 表下方展示和回写
 */

using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EcommerceWorkbench.Models;

public sealed class SmtFullRow : INotifyPropertyChanged
{
    private int _index;
    private string? _pageSku;
    private string? _sku;
    private string? _attr;
    private double? _cost;
    private double? _weightKg;
    private double? _undiscountedPrice;
    private double? _profit;
    private double? _smtPrice;
    private double? _freightCny;
    private IReadOnlyList<double?> _countryAmounts = [];
    private string? _suggestedSku;
    private string? _appliedPageSku;
    private string? _status;

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Index
    {
        get => _index;
        set => Set(ref _index, value);
    }

    /// <summary>详情页变种 SKU 编码。查重和写回都按这个值匹配。</summary>
    public string? PageSku
    {
        get => _pageSku;
        set => Set(ref _pageSku, value);
    }

    /// <summary>商品库搜索用的货号：去掉末尾通用后缀，保留颜色。可手改。</summary>
    public string? Sku
    {
        get => _sku;
        set => Set(ref _sku, value);
    }

    /// <summary>变种属性，例如颜色。</summary>
    public string? Attr
    {
        get => _attr;
        set => Set(ref _attr, value);
    }

    /// <summary>商品库搜到的参考价，当作产品成本（人民币）。</summary>
    public double? Cost
    {
        get => _cost;
        set => Set(ref _cost, value);
    }

    /// <summary>商品库搜到的重量，已从克换算成千克。</summary>
    public double? WeightKg
    {
        get => _weightKg;
        set => Set(ref _weightKg, value);
    }

    public double? UndiscountedPrice
    {
        get => _undiscountedPrice;
        set => Set(ref _undiscountedPrice, value);
    }

    public double? Profit
    {
        get => _profit;
        set => Set(ref _profit, value);
    }

    /// <summary>写入详情页零售价的速卖通售价。</summary>
    public double? SmtPrice
    {
        get => _smtPrice;
        set => Set(ref _smtPrice, value);
    }

    /// <summary>基准运费：重量千克 × 售价续重 + 售价挂号。</summary>
    public double? FreightCny
    {
        get => _freightCny;
        set => Set(ref _freightCny, value);
    }

    /// <summary>与当前公式国家列顺序一致的无忧调价。空表示这一行还没算出运费。</summary>
    public IReadOnlyList<double?> CountryAmounts => _countryAmounts;

    /// <summary>按当前公式的国家顺序写入调价。传 null 表示清空。</summary>
    public void SetCountryAmounts(IReadOnlyList<double?>? amounts)
    {
        _countryAmounts = amounts ?? [];
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }

    /// <summary>国家列重建后刷新横向结果，不改已经算好的金额。</summary>
    public void RefreshCountryAmounts() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));

    /// <summary>按下标读取某一国的调价，供横向结果表绑定。</summary>
    public string this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_countryAmounts.Count || _countryAmounts[index] is null)
                return "";
            return _countryAmounts[index]!.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    public string? Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    /// <summary>查重后建议换成的 SKU。空表示这个 SKU 没有被占用。</summary>
    public string? SuggestedSku
    {
        get => _suggestedSku;
        set => Set(ref _suggestedSku, value);
    }

    /// <summary>已经写进详情页、尚未点保存的 SKU。恢复原 SKU 时用它定位当前页面值。</summary>
    public string? AppliedPageSku
    {
        get => _appliedPageSku;
        set => Set(ref _appliedPageSku, value);
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
