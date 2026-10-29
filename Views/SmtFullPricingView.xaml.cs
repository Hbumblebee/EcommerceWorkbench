/*
 * 功能说明：速卖通半托页。打开 /web/smt/edit 详情，读取变种 SKU，并按与速卖通7 相同的规则查重。
 * 主要职责：维护产品链接；调用 smtProduct/edit.json 读 SKU；
 *           在线产品列表各状态合计超过 1 才算 SKU 重复；
 *           用商品库参考价和重量按普货挂号包邮公式计价，并回写详情页。
 * 创建日期：2026-10-22
 * 修改记录：2026-10-22 查重改为在线列表绿框各状态合计；增加搜索定价并回写
 *           2026-10-22 打开详情后主窗口回到前面，并自动按原 SKU 后缀向后依次查重
 *           2026-10-22 带颜色后缀的 SKU 自动启用颜色后缀；页面加载抢焦点时再把主窗口盖回去
 *           2026-10-22 增加搜索 SKU 列：去掉末尾通用后缀、保留颜色，定价按这一列搜索
 *           2026-10-22 计算公式放到公共折叠区，调整后保存并重算已有行
 *           2026-10-22 公式区表格滚轮先滚内部，到边界再交给整页
 *           2026-10-22 回写结果提示区域调价表格已写入；0 条时带上变种数和报价数
 *           2026-10-23 无忧运费改成与计算表相同的横向国家列
 *           2026-10-23 各国无忧运费写入详情页区域调价表
 *           2026-10-23 操作收成三步，变种表占满剩余高度
 *           2026-10-23 查重区加高到 400，变种表加高到 480
 *           2026-10-24 页面标题改为速卖通半托
 *           2026-10-24 打开详情后不再自动查重
 *           2026-10-24 SKU 表下方展示各国运费
 *           2026-10-24 定价并回写拆成搜索并定价、回写
 *           2026-10-24 编辑半托管信息后检查 SKU编码 和 货品编码 是否重复
 *           2026-10-24 半托管查重拆成 SKU 后缀区和 SKU 表格区
 *           2026-10-24 半托管表格按 SKU编码、货品编码分成两列区域
 *           2026-10-24 半托管查重改为分色列表，行内可开关颜色后缀并选择后缀
 *           2026-10-24 半托管查重列表高度改为 840
 *           2026-10-24 半托管查重列表宽度撑满所在区域
 *           2026-10-24 半托管表增加搜索SKU，按该列查价格并支持回写
 *           2026-10-24 半托管回写与详情页回写分开
 *           2026-10-24 已打开半托管页时直接回写，不再跳回详情页
 */

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EcommerceWorkbench.Models;
using EcommerceWorkbench.Services;
using EcommerceWorkbench.Services.Dianxiaomi;
using EcommerceWorkbench.Services.Joom;
using EcommerceWorkbench.Services.SmtFull;
using EcommerceWorkbench.UI;

namespace EcommerceWorkbench.Views;

public partial class SmtFullPricingView : UserControl
{
    private readonly ObservableCollection<SmtFullRow> _rows = [];
    private readonly ObservableCollection<SmtFreightRate> _freightRates = [];
    private readonly FreightSheetRow[] _freightSheetRows;
    private readonly ObservableCollection<SmtPercentRate> _percentRates = [];
    private readonly ApiClient _apiClient = new();
    private readonly SmtProductService _productService;
    private readonly ProductSearchService _productSearch;
    private readonly CookieStore _cookieStore = new();
    private readonly SmtFullUserSettings _settings = SmtFullUserSettings.Load();
    private bool _loaded;
    private bool _busy;
    private SmtFullProductPageWindow? _productWindow;
    private string? _openedEditUrl;
    private string? _openedProductId;
    private string? _openedShopId;
    private readonly List<SmtFullProductPageWindow> _occupancyWindows = [];
    private readonly ObservableCollection<SmtFullOccupancyGroup> _occupancies = [];
    private readonly ObservableCollection<SmtFullOccupancyGroup> _semiOccupancies = [];
    private readonly ObservableCollection<SmtSemiLineRow> _semiLines = [];
    private readonly ObservableCollection<string> _detailProbes = [];
    private readonly HashSet<string> _verifiedSkus = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<SmtShopOption> _shopOptions = [];
    private int _suggestGeneration;
    private bool _loadingShopOptions;
    private bool _loadingColorOptions;
    private bool _bringingMainForward;
    private long _keepMainInFrontUntil;
    private int _focusGeneration;

    public event EventHandler<string>? StatusChanged;
    public event EventHandler<string>? CountChanged;
    public Func<string?>? CookieHeaderProvider { get; set; }
    public Func<IReadOnlyList<CookieRecord>?>? CookieRecordsProvider { get; set; }
    public event EventHandler<string>? CookieInvalidated;

    public SmtFullPricingView()
    {
        _freightSheetRows =
        [
            new FreightSheetRow("代码", "Code", _freightRates),
            new FreightSheetRow("续重 元/kg", "PerKg", _freightRates),
            new FreightSheetRow("挂号费", "Registration", _freightRates)
        ];
        InitializeComponent();
        _productService = new SmtProductService(_apiClient);
        _productSearch = new ProductSearchService(_apiClient);
        SkuGrid.ItemsSource = _rows;
        FreightResultGrid.ItemsSource = _rows;
        FreightRateGrid.ItemsSource = _freightSheetRows;
        PercentRateGrid.ItemsSource = _percentRates;
        OccupancyList.ItemsSource = _occupancies;
        SemiOccupancyList.ItemsSource = _semiLines;
        TableRegionCopy.EnableDataGrid(SkuGrid);
        TableRegionCopy.EnableDataGrid(FreightResultGrid);
        TableRegionCopy.EnableSemiList(SemiOccupancyPanel);
        DetailProbeList.ItemsSource = _detailProbes;
        OccupancyShopCombo.ItemsSource = _shopOptions;
        Loaded += SmtFullPricingView_Loaded;
    }

    public void Cleanup()
    {
        try
        {
            _productWindow?.Close();
        }
        catch
        {
            // 关闭时忽略
        }

        foreach (var window in _occupancyWindows.ToList())
        {
            try
            {
                window.Close();
            }
            catch
            {
                // 关闭时忽略
            }
        }
    }

    public void RefreshCount() => UpdateCount();

    /// <summary>DataGrid 内部 ScrollViewer 的模板部件名。</summary>
    private const string GridScrollViewerName = "DG_ScrollViewer";

    /// <summary>鼠标在公式表、结果表、查重区里时滚那一块。滚到头就交给这块自己的滚动条。</summary>
    private void PageScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        ScrollViewer? target = null;
        var step = WheelScrollHelper.LinesPerNotch() * WheelScrollHelper.TextLinePixels;

        if (TryScrollableGrid(FreightRateGrid, e.Delta, out var freightScroll))
        {
            target = freightScroll;
            step = WheelScrollHelper.LinesPerNotch();
        }
        else if (TryScrollableGrid(PercentRateGrid, e.Delta, out var percentScroll))
        {
            target = percentScroll;
            step = WheelScrollHelper.LinesPerNotch();
        }
        else if (TryScrollableGrid(SkuGrid, e.Delta, out var skuScroll))
        {
            target = skuScroll;
            step = WheelScrollHelper.LinesPerNotch();
        }
        else if (TryScrollableGrid(FreightResultGrid, e.Delta, out var freightResultScroll))
        {
            target = freightResultScroll;
            step = WheelScrollHelper.LinesPerNotch();
        }
        else if (SemiOccupancyScroll.IsMouseOver && WheelScrollHelper.CanScroll(SemiOccupancyScroll, e.Delta))
        {
            target = SemiOccupancyScroll;
        }
        else if (OccupancyScroll.IsMouseOver && WheelScrollHelper.CanScroll(OccupancyScroll, e.Delta))
        {
            target = OccupancyScroll;
        }
        else if (DetailProbeList.IsMouseOver
                 && TryGetInnerScroll(DetailProbeList, "", out var probeScroll)
                 && WheelScrollHelper.CanScroll(probeScroll, e.Delta))
        {
            target = probeScroll;
        }
        else if (FormulaScroll.IsMouseOver && WheelScrollHelper.CanScroll(FormulaScroll, e.Delta))
        {
            target = FormulaScroll;
        }
        else if (WheelScrollHelper.CanScroll(PageScroll, e.Delta))
        {
            target = PageScroll;
        }

        if (target is null)
            return;

        WheelScrollHelper.ScrollWheel(target, e.Delta, step);
        e.Handled = true;
    }

    private static bool TryScrollableGrid(DataGrid grid, int wheelDelta, out ScrollViewer scroll)
    {
        scroll = null!;
        return grid.IsMouseOver
               && TryGetInnerScroll(grid, GridScrollViewerName, out scroll)
               && WheelScrollHelper.CanScroll(scroll, wheelDelta);
    }

    private static bool TryGetInnerScroll(DependencyObject host, string name, out ScrollViewer scroll)
    {
        scroll = FindVisualChild<ScrollViewer>(host, name)!;
        return scroll is not null;
    }

    private static T? FindVisualChild<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match && (name.Length == 0 || match.Name == name))
                return match;

            var nested = FindVisualChild<T>(child, name);
            if (nested is not null)
                return nested;
        }

        return null;
    }

    private void SmtFullPricingView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
            return;
        _loaded = true;
        ProductUrlBox.Text = _settings.ProductEditUrl;
        SuffixBox.Text = string.IsNullOrWhiteSpace(_settings.GeneralSkuSuffixes)
            ? SmtFullUserSettings.DefaultGeneralSkuSuffixes
            : _settings.GeneralSkuSuffixes;
        ColorSuffixBox.Text = _settings.OccupancyColorSuffixes;
        InitializeOccupancySelectors();
        ShowFormula(_settings.Formula ?? SmtQuoteFormula.CreateDefault());
        UpdateCount();
    }

    private void ProductUrlBox_LostFocus(object sender, RoutedEventArgs e) => PersistProductUrl();

    private void SuffixBox_LostFocus(object sender, RoutedEventArgs e)
    {
        _settings.GeneralSkuSuffixes = SuffixBox.Text?.Trim() ?? "";
        _settings.OccupancyColorSuffixes = ColorSuffixBox.Text?.Trim() ?? "";
        SaveSettings();
        RefreshOccupancyColorOptions();
    }

    private void ColorSuffixBox_LostFocus(object sender, RoutedEventArgs e) => SuffixBox_LostFocus(sender, e);

    private async void OpenProduct_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        PersistProductUrl();
        if (!SmtProductService.TryParseProduct(ProductUrlBox.Text, out var productId, out var editUrl))
        {
            MessageBox.Show(
                "请填写店小秘速卖通产品编辑链接，例如：\nhttps://www.dianxiaomi.com/web/smt/edit?id=184807708954397661",
                "提示",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        ProductUrlBox.Text = editUrl;
        PersistProductUrl();

        var cookieHeader = GetCookieHeader();
        var cookies = GetCookieRecords();
        if (string.IsNullOrWhiteSpace(cookieHeader) || cookies is null || cookies.Count == 0)
        {
            MessageBox.Show("请先在「店小秘搜索」登录并导出 Cookie。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _busy = true;
        try
        {
            SetStatus("正在打开速卖通产品详情页并读取变种 SKU…");
            await OpenProductWindowAsync(new Uri(editUrl), cookies);
            var detail = await _productService.GetDetailAsync(productId, cookieHeader);
            LoadSkus(detail.Skus);
            _openedEditUrl = editUrl;
            _openedProductId = productId;
            _openedShopId = detail.ShopId;
            EnsureCurrentShopOption();
            ClearOccupancy();
            var loaded = $"已读取变种 SKU {detail.Skus.Count} 个。店铺 {detail.ShopId}。需要查重时点「检查占用」。";
            SetStatus(loaded);
            _productWindow?.SetHint("已读取变种 SKU。查重请回到工作台点「检查占用」。");
            UpdateCount();
            BringMainWindowToFront();
        }
        catch (Exception ex)
        {
            SetStatus("读取产品失败：" + ex.Message);
            if (CookieErrorDetector.IsAuthFailure(ex.Message))
                CookieInvalidated?.Invoke(this, ex.Message);
            else
                MessageBox.Show(ex.Message, "读取产品失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
            BringMainWindowToFront();
        }
    }

    private async Task EnsureProductWindowAsync(IReadOnlyList<CookieRecord> cookies)
    {
        if (_productWindow is null)
        {
            _productWindow = new SmtFullProductPageWindow { ShowActivated = false };
            _productWindow.Closed += ProductWindow_Closed;
            _productWindow.PageLoaded += (_, _) => BringMainWindowToFrontIfArmed();
            _productWindow.Activated += (_, _) => BringMainWindowToFrontIfArmed();
            _productWindow.Show();
        }
        else if (!_productWindow.IsVisible)
        {
            _productWindow.ShowActivated = false;
            _productWindow.Show();
        }

        await _productWindow.PrepareAsync(cookies);
    }

    private async Task OpenProductWindowAsync(Uri url, IReadOnlyList<CookieRecord> cookies, bool reload = true)
    {
        await EnsureProductWindowAsync(cookies);
        var window = _productWindow ?? throw new InvalidOperationException("产品详情窗口没有打开。");
        ArmMainWindowInFront();
        if (reload || !window.IsShowing(url))
            await window.OpenProductAsync(url, cookies);

        BringMainWindowToFront();
    }

    /// <summary>打开详情后的几秒内，页面加载抢焦点时把主窗口重新盖回去。</summary>
    private void ArmMainWindowInFront()
    {
        var generation = ++_focusGeneration;
        _keepMainInFrontUntil = Environment.TickCount64 + 6000;
        BringMainWindowToFront();
        _ = KeepMainInFrontAsync(generation);
    }

    private async Task KeepMainInFrontAsync(int generation)
    {
        foreach (var delay in new[] { 300, 800, 1600, 3000 })
        {
            await Task.Delay(delay);
            if (generation != _focusGeneration)
                return;
            await Dispatcher.InvokeAsync(BringMainWindowToFront);
        }
    }

    private void BringMainWindowToFrontIfArmed()
    {
        if (Environment.TickCount64 <= _keepMainInFrontUntil)
            BringMainWindowToFront();
    }

    /// <summary>详情页打开后把工作台主窗口重新放到前面，避免被详情页挡住。</summary>
    private void BringMainWindowToFront()
    {
        if (_bringingMainForward)
            return;
        var owner = Window.GetWindow(this);
        if (owner is null)
            return;

        _bringingMainForward = true;
        try
        {
            if (owner.WindowState == WindowState.Minimized)
                owner.WindowState = WindowState.Normal;
            owner.Activate();
            owner.Topmost = true;
            owner.Topmost = false;
            owner.Focus();
        }
        finally
        {
            _bringingMainForward = false;
        }
    }

    private void ProductWindow_Closed(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, _productWindow))
            _productWindow = null;
    }

    private void LoadSkus(IReadOnlyList<SmtVariantSku> skus)
    {
        _rows.Clear();
        var generalSuffixes = JoomSkuSuffix.Parse(SuffixBox.Text);
        var index = 1;
        foreach (var sku in skus)
        {
            _rows.Add(new SmtFullRow
            {
                Index = index++,
                PageSku = sku.PageSku,
                Sku = JoomSkuSuffix.StripCompositeTrailingSuffix(sku.PageSku, generalSuffixes),
                Attr = sku.Attr,
                AppliedPageSku = sku.PageSku
            });
        }
    }

    private void PersistProductUrl()
    {
        _settings.ProductEditUrl = ProductUrlBox.Text?.Trim() ?? "";
        SaveSettings();
    }

    private void ShowFormula(SmtQuoteFormula formula)
    {
        SaleRateBox.Text = FormatFormula(formula.SaleRatePerKg);
        SaleRegBox.Text = FormatFormula(formula.SaleRegistration);
        CommissionBox.Text = FormatFormula(formula.CommissionRate);
        ProfitRateBox.Text = FormatFormula(formula.ProfitRate);
        DiscountBox.Text = FormatFormula(formula.ShopDiscount);
        SaleDivisorBox.Text = FormatFormula(formula.SaleDivisor);
        AdjustRateBox.Text = FormatFormula(formula.AdjustRatePerKg);
        AdjustRegBox.Text = FormatFormula(formula.AdjustRegistration);
        AdjustDivisorBox.Text = FormatFormula(formula.AdjustDivisor);
        _freightRates.Clear();
        foreach (var item in formula.FreightCountries)
            _freightRates.Add(item);
        RebuildFreightColumns();
        _percentRates.Clear();
        foreach (var item in formula.PercentCountries)
            _percentRates.Add(item);
        RebuildFreightResultColumns();
    }

    private SmtQuoteFormula ReadFormula()
    {
        var defaults = SmtQuoteFormula.CreateDefault();
        return new SmtQuoteFormula
        {
            SaleRatePerKg = ReadDouble(SaleRateBox, defaults.SaleRatePerKg),
            SaleRegistration = ReadDouble(SaleRegBox, defaults.SaleRegistration),
            CommissionRate = ReadDouble(CommissionBox, defaults.CommissionRate),
            ProfitRate = ReadDouble(ProfitRateBox, defaults.ProfitRate),
            ShopDiscount = ReadDouble(DiscountBox, defaults.ShopDiscount),
            SaleDivisor = ReadDouble(SaleDivisorBox, defaults.SaleDivisor),
            AdjustRatePerKg = ReadDouble(AdjustRateBox, defaults.AdjustRatePerKg),
            AdjustRegistration = ReadDouble(AdjustRegBox, defaults.AdjustRegistration),
            AdjustDivisor = ReadDouble(AdjustDivisorBox, defaults.AdjustDivisor),
            FreightCountries = _freightRates.ToList(),
            PercentCountries = _percentRates.ToList()
        };
    }

    private void FormulaBox_LostFocus(object sender, RoutedEventArgs e) => PersistFormula(recalculate: true);

    private void FormulaGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit)
            return;
        Dispatcher.BeginInvoke(() => PersistFormula(recalculate: true));
    }

    private void RestoreFormula_Click(object sender, RoutedEventArgs e)
    {
        ShowFormula(SmtQuoteFormula.CreateDefault());
        PersistFormula(recalculate: true);
        SetStatus("已恢复普货挂号包邮公式的计算表默认值。");
    }

    private void PersistFormula(bool recalculate)
    {
        CommitFormulaEdits();
        _settings.Formula = ReadFormula();
        SaveSettings();
        if (recalculate)
            RecalculateLoadedRows();
    }

    private void CommitFormulaEdits()
    {
        FreightRateGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        FreightRateGrid.CommitEdit(DataGridEditingUnit.Row, true);
        PercentRateGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        PercentRateGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    /// <summary>已有成本和重量的行按当前公式重算，不重新搜索，也不写回详情页。</summary>
    private void RecalculateLoadedRows()
    {
        var formula = _settings.Formula ?? SmtQuoteFormula.CreateDefault();
        foreach (var row in _rows)
        {
            if (row.Cost is null || row.WeightKg is null)
                continue;
            var quote = SmtRegisteredQuoteCalculator.Calculate(row.Cost.Value, row.WeightKg.Value, formula);
            row.UndiscountedPrice = quote.UndiscountedPrice;
            row.Profit = quote.Profit;
            row.SmtPrice = quote.SmtPrice;
            ApplyFreightResult(row, quote);
            row.Status = "已按当前公式重算";
        }
    }

    /// <summary>把基准运费和各国无忧调价记到行上，下方结果表按国家列读这些数。</summary>
    private static void ApplyFreightResult(SmtFullRow row, SmtRegisteredQuote? quote)
    {
        if (quote is null)
        {
            row.FreightCny = null;
            row.SetCountryAmounts(null);
            return;
        }

        row.FreightCny = quote.FreightCny;
        row.SetCountryAmounts(quote.Countries.Select(country => (double?)country.Amount).ToList());
    }

    /// <summary>SKU 表下方的运费结果：序号、页面 SKU、基准运费，以及横向的各国调价。</summary>
    private void RebuildFreightResultColumns()
    {
        FreightResultGrid.Columns.Clear();
        FreightResultGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "序号",
            Binding = new Binding(nameof(SmtFullRow.Index)),
            Width = 52
        });
        FreightResultGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "页面SKU",
            Binding = new Binding(nameof(SmtFullRow.PageSku)),
            Width = 160
        });
        FreightResultGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "运费",
            Binding = new Binding(nameof(SmtFullRow.FreightCny)) { StringFormat = "0.00" },
            Width = 72
        });

        var index = 0;
        foreach (var rate in _freightRates.Concat(_percentRates.Select(item => new SmtFreightRate
                 {
                     Name = item.Name,
                     Code = item.Code
                 })))
        {
            var header = string.IsNullOrWhiteSpace(rate.Name) ? rate.Code : rate.Name;
            FreightResultGrid.Columns.Add(new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding($"[{index}]"),
                Width = 88
            });
            index++;
        }

        foreach (var row in _rows)
            row.RefreshCountryAmounts();
    }

    private static double ReadDouble(TextBox box, double fallback)
    {
        var text = box.Text?.Trim() ?? "";
        return double.TryParse(text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var value)
            || double.TryParse(text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.CurrentCulture, out value)
            ? value
            : fallback;
    }

    private static string FormatFormula(double value) =>
        value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>国家横排，和计算表第 4 行的无忧国家列一致。左列固定为代码、续重、挂号费。</summary>
    private void RebuildFreightColumns()
    {
        FreightRateGrid.Columns.Clear();
        FreightRateGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "无忧",
            Binding = new Binding(nameof(FreightSheetRow.Label)),
            IsReadOnly = true,
            Width = 96
        });
        for (var i = 0; i < _freightRates.Count; i++)
        {
            var rate = _freightRates[i];
            FreightRateGrid.Columns.Add(new DataGridTextColumn
            {
                Header = string.IsNullOrWhiteSpace(rate.Name) ? rate.Code : rate.Name,
                Binding = new Binding($"[{i}]")
                {
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.LostFocus
                },
                Width = 96
            });
        }

        foreach (var row in _freightSheetRows)
            row.Refresh();
    }

    private void SaveSettings()
    {
        try
        {
            _settings.Save();
        }
        catch
        {
            // 本地保存失败不阻断操作
        }
    }

    private string GetCookieHeader()
    {
        var header = CookieHeaderProvider?.Invoke();
        if (!string.IsNullOrWhiteSpace(header))
            return header;

        var saved = _cookieStore.Load(CookieStore.DefaultFilePath);
        return saved?.Cookies.Count > 0 ? CookieStore.ToCookieHeader(saved.Cookies) : "";
    }

    private IReadOnlyList<CookieRecord>? GetCookieRecords()
    {
        var live = CookieRecordsProvider?.Invoke();
        if (live is { Count: > 0 })
            return live;

        return _cookieStore.Load(CookieStore.DefaultFilePath)?.Cookies;
    }

    private void UpdateCount()
    {
        CountChanged?.Invoke(this, _rows.Count == 0 ? "" : "变种 SKU " + _rows.Count + " 个");
    }

    private void SetStatus(string text) => StatusChanged?.Invoke(this, text);

    private static string NormalizeSku(string? sku) => SkuText.Normalize(sku);

    private static readonly string[] ScopeKeys =
        ["all", "onSelling", "draft-box", "offline", "auditing", "editingRequired", "doubtdelete"];

    private void InitializeOccupancySelectors()
    {
        var scopeIndex = Array.IndexOf(ScopeKeys, _settings.OccupancyScope);
        OccupancyScopeCombo.SelectedIndex = scopeIndex < 0 ? 0 : scopeIndex;

        _loadingShopOptions = true;
        try
        {
            _shopOptions.Clear();
            _shopOptions.Add(new SmtShopOption { ShopId = "auto", Name = "自动（当前产品店铺）" });
            OccupancyShopCombo.SelectedIndex = 0;
        }
        finally
        {
            _loadingShopOptions = false;
        }
    }

    private string SelectedScopeKey =>
        OccupancyScopeCombo.SelectedIndex >= 0 && OccupancyScopeCombo.SelectedIndex < ScopeKeys.Length
            ? ScopeKeys[OccupancyScopeCombo.SelectedIndex]
            : "all";

    private string ResolveOccupancyShopId()
    {
        if (OccupancyShopCombo.SelectedItem is SmtShopOption selected)
        {
            return selected.ShopId.Equals("auto", StringComparison.OrdinalIgnoreCase)
                ? _openedShopId ?? ""
                : selected.ShopId;
        }

        if (!string.IsNullOrWhiteSpace(_settings.OccupancyShopId)
            && !_settings.OccupancyShopId.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return _settings.OccupancyShopId;

        return _openedShopId ?? "";
    }

    private void OccupancyScope_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded)
            return;
        _settings.OccupancyScope = SelectedScopeKey;
        SaveSettings();
    }

    private void OccupancyShop_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingShopOptions || !_loaded)
            return;
        if (OccupancyShopCombo.SelectedItem is not SmtShopOption option)
            return;
        _settings.OccupancyShopId = option.ShopId;
        SaveSettings();
    }

    private async void RefreshShops_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        var cookieHeader = GetCookieHeader();
        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            MessageBox.Show("请先在「店小秘搜索」登录并导出 Cookie。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _busy = true;
        try
        {
            SetStatus("正在拉取速卖通店铺列表…");
            var shops = await _productService.ListShopsAsync(cookieHeader);
            var keep = (_settings.OccupancyShopId ?? "auto").Trim();
            _loadingShopOptions = true;
            try
            {
                _shopOptions.Clear();
                _shopOptions.Add(new SmtShopOption { ShopId = "auto", Name = "自动（当前产品店铺）" });
                foreach (var shop in shops)
                    _shopOptions.Add(shop);
                EnsureCurrentShopOption();

                var index = 0;
                for (var i = 0; i < _shopOptions.Count; i++)
                {
                    if (_shopOptions[i].ShopId.Equals(keep, StringComparison.OrdinalIgnoreCase))
                    {
                        index = i;
                        break;
                    }
                }

                OccupancyShopCombo.SelectedIndex = index;
                _settings.OccupancyShopId = _shopOptions[index].ShopId;
                SaveSettings();
            }
            finally
            {
                _loadingShopOptions = false;
            }

            SetStatus($"已拉取 {shops.Count} 个速卖通店铺；查重范围＝{SmtProductService.ScopeName(SelectedScopeKey)}。");
        }
        catch (Exception ex)
        {
            SetStatus("拉取店铺列表失败：" + ex.Message);
            if (CookieErrorDetector.IsAuthFailure(ex.Message))
                CookieInvalidated?.Invoke(this, ex.Message);
            else
                MessageBox.Show(ex.Message, "拉取店铺列表失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
        }
    }

    private void EnsureCurrentShopOption()
    {
        if (string.IsNullOrWhiteSpace(_openedShopId))
            return;
        if (_shopOptions.Any(s => s.ShopId.Equals(_openedShopId, StringComparison.OrdinalIgnoreCase)))
            return;
        _shopOptions.Add(new SmtShopOption
        {
            ShopId = _openedShopId,
            Name = "当前产品店铺 " + _openedShopId
        });
    }

    private IReadOnlyList<string> OccupancySuffixes => JoomSkuSuffix.Parse(SuffixBox.Text);

    private IReadOnlyList<string> OccupancyColors => JoomSkuSuffix.Parse(ColorSuffixBox.Text);

    private bool UseOccupancyColorSuffix => _settings.OccupancyUseColorSuffix;

    private void RefreshOccupancyColorOptions()
    {
        _loadingColorOptions = true;
        try
        {
            foreach (var group in _occupancies)
                group.LoadColorOptions(OccupancyColors);
        }
        finally
        {
            _loadingColorOptions = false;
        }
    }

    private async void UseColorSuffix_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingColorOptions)
            return;
        if (sender is not CheckBox box || box.DataContext is not SmtFullOccupancyGroup group)
            return;

        group.UseColorSuffix = box.IsChecked == true;
        group.SuggestionUserEdited = false;
        _settings.OccupancyUseColorSuffix = group.UseColorSuffix;
        SaveSettings();
        if (group.UseColorSuffix)
            RefreshOccupancyColorOptions();
        await FillSuggestedSkusAsync([group]);
    }

    private async void ColorSuffixCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingColorOptions)
            return;
        if (sender is not ComboBox combo || combo.DataContext is not SmtFullSkuPartSlot part || !part.Owner.UseColorSuffix)
            return;

        part.SelectedColor = combo.SelectedItem as string;
        part.Owner.SuggestionUserEdited = false;
        await FillSuggestedSkusAsync([part.Owner]);
    }

    private async void UseGeneralSuffix_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingColorOptions)
            return;
        if (sender is not CheckBox box || box.DataContext is not SmtFullSkuPartSlot part || !part.Owner.HasMultipleParts)
            return;

        part.UseGeneralSuffix = box.IsChecked == true;
        part.Owner.SuggestionUserEdited = false;
        await FillSuggestedSkusAsync([part.Owner]);
    }

    private void SuggestedSku_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is SmtFullOccupancyGroup group)
        {
            group.SuggestionUserEdited = true;
            RefreshSuggestedSkuColumn();
        }
    }

    private async void SuggestedSku_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not SmtFullOccupancyGroup group || !group.SuggestionUserEdited)
            return;

        var sku = NormalizeSku(box.Text);
        group.SuggestedSku = sku;
        if (sku.Length == 0)
        {
            group.SuggestStatus = "请填写建议 SKU";
            return;
        }

        try
        {
            group.SuggestStatus = "正在确认修改后的 SKU 是否重复…";
            var report = await _productService.FindOccupanciesAsync(
                [sku], ResolveOccupancyShopId(), GetCookieHeader(), SelectedScopeKey);
            var occupied = report.Hits.Any(h => h.Sku.Equals(sku, StringComparison.OrdinalIgnoreCase));
            var gap = BuildVerifyGap(report);
            group.SuggestStatus = occupied
                ? "修改后的 SKU 仍会重复"
                : gap is null
                    ? "修改后的 SKU 未被占用"
                    : "未发现占用，但部分范围未核对完：" + gap;
            RefreshSuggestedSkuColumn();
        }
        catch (Exception ex)
        {
            group.SuggestStatus = "确认失败：" + ex.Message;
            SetStatus("确认建议 SKU 失败：" + ex.Message);
            if (CookieErrorDetector.IsAuthFailure(ex.Message))
                CookieInvalidated?.Invoke(this, ex.Message);
        }
    }

    private async void SearchAndQuote_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        CommitGrid();
        CommitFormulaEdits();
        var skus = CollectSearchSkus();
        foreach (var line in _semiLines)
        {
            var sku = NormalizeSku(line.SearchSku);
            if (sku.Length > 0 && !skus.Contains(sku, StringComparer.OrdinalIgnoreCase))
                skus.Add(sku);
        }
        if (skus.Count == 0)
        {
            MessageBox.Show("请先打开产品详情或完成半托管查重，并在「搜索SKU」列填写要查询的货号。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (skus.Count > ProductSearchService.MaxSearchValuesPerRequest)
        {
            MessageBox.Show($"单次最多搜索 {ProductSearchService.MaxSearchValuesPerRequest} 个 SKU。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var cookieHeader = GetCookieHeader();
        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            MessageBox.Show("请先在「店小秘搜索」登录并导出 Cookie。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _busy = true;
        try
        {
            SetStatus($"正在按 {skus.Count} 个搜索 SKU 查询参考价和重量…");
            var outcome = await _productSearch.SearchAsync(skus, cookieHeader);
            var priced = _rows.Count == 0 ? 0 : ApplySearchAndCalculate(outcome);
            var semiPriced = ApplySemiPrices(outcome);
            var hint = priced == 0 && semiPriced == 0
                ? "没有查到价格。未命中的 SKU 已留空。"
                : $"已定价 {priced} 个详情 SKU，查到半托管价格 {semiPriced} 个。请核对后点「回写」。";
            SetStatus(hint);
            UpdateCount();
            if (priced == 0 && semiPriced == 0)
                MessageBox.Show(hint, "搜索并定价", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            SetStatus("搜索定价失败：" + ex.Message);
            if (CookieErrorDetector.IsAuthFailure(ex.Message))
                CookieInvalidated?.Invoke(this, ex.Message);
            else
                MessageBox.Show(ex.Message, "搜索定价失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
        }
    }

    private async void WriteSemi_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        var semiWrites = CollectSemiWrites();
        if (semiWrites.Count == 0)
        {
            MessageBox.Show("还没有可回写的半托管 SKU 或价格。请先完成半托管查重，并用「搜索并定价」查出价格。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var cookies = GetCookieRecords();
        if (string.IsNullOrWhiteSpace(GetCookieHeader()) || cookies is null || cookies.Count == 0)
        {
            MessageBox.Show("请先在「店小秘搜索」登录并导出 Cookie。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var editUrl = _openedEditUrl;
        if (string.IsNullOrWhiteSpace(editUrl)
            && SmtProductService.TryParseProduct(ProductUrlBox.Text, out _, out var parsedUrl))
        {
            editUrl = parsedUrl;
        }

        if (string.IsNullOrWhiteSpace(editUrl))
        {
            MessageBox.Show("请先打开速卖通产品详情页。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _busy = true;
        try
        {
            SetStatus($"正在把 {semiWrites.Count} 行 SKU 和价格写入半托管页…");
            await EnsureProductWindowAsync(cookies);
            if (!await _productWindow!.IsOnSemiPageAsync())
            {
                if (!_productWindow.IsShowing(new Uri(editUrl)))
                    await _productWindow.OpenProductAsync(new Uri(editUrl), cookies);

                var read = await _productWindow.JumpToSemiAndReadSkusAsync();
                if (!await _productWindow.IsOnSemiPageAsync())
                {
                    var message = string.IsNullOrWhiteSpace(read.Error)
                        ? "没有停留在半托管页，无法回写。"
                        : read.Error;
                    SetStatus(message);
                    MessageBox.Show(message, "回写半托管", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            var semiJson = JsonSerializer.Serialize(new { rows = semiWrites });
            var result = await _productWindow.WriteSemiSkusAsync(semiJson);
            var missed = result.MissedPageSkus.Count == 0
                ? ""
                : " 未匹配：" + string.Join("、", result.MissedPageSkus.Take(12));
            var hint = $"已回写半托管 {result.Updated} 行 SKU 和价格。请在半托管页面确认后保存。{missed}";
            if (!string.IsNullOrWhiteSpace(result.Error))
                hint += " " + result.Error;
            _productWindow.SetHint(hint);
            SetStatus(hint);
            if (result.Updated == 0 || !string.IsNullOrWhiteSpace(result.Error))
                MessageBox.Show(hint, "回写半托管", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            SetStatus("回写半托管失败：" + ex.Message);
            if (CookieErrorDetector.IsAuthFailure(ex.Message))
                CookieInvalidated?.Invoke(this, ex.Message);
            else
                MessageBox.Show(ex.Message, "回写半托管失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
        }
    }

    private async void WriteQuotes_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        CommitGrid();
        CommitFormulaEdits();
        var priced = CollectWriteRows();
        if (priced.Count == 0)
        {
            MessageBox.Show("还没有可回写的详情页售价。请先点「搜索并定价」。半托管请用下方「回写半托管」。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var cookies = GetCookieRecords();
        if (string.IsNullOrWhiteSpace(GetCookieHeader()) || cookies is null || cookies.Count == 0)
        {
            MessageBox.Show("请先在「店小秘搜索」登录并导出 Cookie。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var editUrl = _openedEditUrl;
        if (string.IsNullOrWhiteSpace(editUrl)
            && SmtProductService.TryParseProduct(ProductUrlBox.Text, out _, out var parsedUrl))
        {
            editUrl = parsedUrl;
        }

        if (string.IsNullOrWhiteSpace(editUrl))
        {
            MessageBox.Show("请先打开速卖通产品详情页。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _busy = true;
        try
        {
            SetStatus($"正在把 {priced.Count} 个 SKU 的售价、重量和各国无忧运费写入详情页…");
            await OpenProductWindowAsync(new Uri(editUrl), cookies, reload: false);
            var countryNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var country in ReadFormula().FreightCountries)
            {
                if (!string.IsNullOrWhiteSpace(country.Code))
                    countryNames[country.Code.Trim()] = country.Name;
            }
            foreach (var country in ReadFormula().PercentCountries)
            {
                if (!string.IsNullOrWhiteSpace(country.Code))
                    countryNames[country.Code.Trim()] = country.Name;
            }
            var json = JsonSerializer.Serialize(new { rows = priced, countryNames });
            var result = await _productWindow!.WriteQuotesAsync(json);
            var missed = result.MissedPageSkus.Count == 0
                ? ""
                : " 未匹配：" + string.Join("、", result.MissedPageSkus.Take(12));
            var adjustNote = result.AdjustUpdated > 0
                ? $"，各国无忧运费已写入 {result.AdjustUpdated} 个变种"
                : "";
            var countNote = result.Updated == 0
                ? $" 详情页变种 {result.PageCount} 条，本次报价 {result.QuoteCount} 条。"
                : "";
            var hint = $"已回写零售价和重量 {result.Updated} 条{adjustNote}。请在店小秘页面确认后点击保存。{missed}{countNote}";
            if (!string.IsNullOrWhiteSpace(result.Error))
                hint += " " + result.Error;
            _productWindow.SetHint(hint);
            SetStatus(hint);
            if (result.Updated == 0 || !string.IsNullOrWhiteSpace(result.Error) || result.MissedPageSkus.Count > 0)
                MessageBox.Show(hint, "回写详情页", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            SetStatus("回写失败：" + ex.Message);
            if (CookieErrorDetector.IsAuthFailure(ex.Message))
                CookieInvalidated?.Invoke(this, ex.Message);
            else
                MessageBox.Show(ex.Message, "回写失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>商品库重量是克。参考价当作成本，按挂号包邮表算出售价和各国调价。</summary>
    private int ApplySearchAndCalculate(ProductSearchOutcome outcome)
    {
        var bySku = new Dictionary<string, ProductResultRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var hit in outcome.Rows)
        {
            var key = NormalizeSku(hit.Sku);
            if (key.Length > 0)
                bySku.TryAdd(key, hit);
        }

        var priced = 0;
        foreach (var row in _rows)
        {
            var search = NormalizeSku(string.IsNullOrWhiteSpace(row.Sku) ? row.PageSku : row.Sku);
            var pageSku = NormalizeSku(row.PageSku);
            row.Cost = null;
            row.WeightKg = null;
            row.UndiscountedPrice = null;
            row.Profit = null;
            row.SmtPrice = null;
            ApplyFreightResult(row, null);
            if (search.Length == 0 || pageSku.Length == 0)
                continue;
            if (!bySku.TryGetValue(search, out var hit) || hit.Price is null || hit.Weight is null)
            {
                row.Status = "未搜到价格或重量";
                continue;
            }

            var cost = (double)hit.Price.Value;
            var weightKg = (double)hit.Weight.Value / 1000.0;
            var quote = SmtRegisteredQuoteCalculator.Calculate(cost, weightKg, ReadFormula());
            row.Cost = quote.CostCny;
            row.WeightKg = weightKg;
            row.UndiscountedPrice = quote.UndiscountedPrice;
            row.Profit = quote.Profit;
            row.SmtPrice = quote.SmtPrice;
            ApplyFreightResult(row, quote);
            row.Status = "已计算";
            priced++;
        }

        return priced;
    }

    /// <summary>用表格里已经算出的售价、重量和各国调价组回写数据。</summary>
    private List<object> CollectWriteRows()
    {
        var codes = _freightRates
            .Select(item => item.Code)
            .Concat(_percentRates.Select(item => item.Code))
            .ToList();
        var payload = new List<object>();
        foreach (var row in _rows)
        {
            var pageSku = NormalizeSku(row.PageSku);
            if (pageSku.Length == 0 || row.SmtPrice is null || row.WeightKg is null)
                continue;
            var countries = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < codes.Count && i < row.CountryAmounts.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(codes[i]) || row.CountryAmounts[i] is not double amount)
                    continue;
                countries[codes[i].Trim()] = amount;
            }

            payload.Add(new
            {
                sku = pageSku,
                price = row.SmtPrice.Value,
                weightKg = row.WeightKg.Value,
                countries
            });
        }

        return payload;
    }

    /// <summary>半托管行按搜索 SKU 填入商品库参考价。</summary>
    private int ApplySemiPrices(ProductSearchOutcome outcome)
    {
        var bySku = new Dictionary<string, ProductResultRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var hit in outcome.Rows)
        {
            var key = NormalizeSku(hit.Sku);
            if (key.Length > 0)
                bySku.TryAdd(key, hit);
        }

        var priced = 0;
        foreach (var line in _semiLines)
        {
            var key = NormalizeSku(line.SearchSku);
            line.Price = null;
            if (key.Length == 0 || !bySku.TryGetValue(key, out var hit) || hit.Price is null)
                continue;
            line.Price = (double)hit.Price.Value;
            priced++;
        }

        return priced;
    }

    /// <summary>半托管回写：建议 SKU 写入对应列，参考价写入产品价格。</summary>
    private List<object> CollectSemiWrites()
    {
        var payload = new List<object>();
        foreach (var line in _semiLines)
        {
            var skuCode = line.SkuSide?.Sku ?? "";
            var itemCode = line.ItemSide?.Sku ?? "";
            if (skuCode.Length == 0 && itemCode.Length == 0)
                continue;
            var newSku = SkuToWrite(line.SkuSide);
            var newItem = SkuToWrite(line.ItemSide);
            if (line.Price is null && newSku == skuCode && newItem == itemCode)
                continue;
            payload.Add(new
            {
                skuCode,
                scItemCode = itemCode,
                newSkuCode = newSku,
                newItemCode = newItem,
                price = line.Price
            });
        }

        return payload;
    }

    private static string SkuToWrite(SmtFullOccupancyGroup? side)
    {
        if (side is null)
            return "";
        var suggested = NormalizeSku(side.SuggestedSku);
        return suggested.Length > 0 ? suggested : side.Sku;
    }

    private async void CheckOccupancy_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        var skus = CollectOccupancySkus();
        if (skus.Count == 0)
        {
            MessageBox.Show("请先打开产品详情读取 SKU。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(GetCookieHeader()))
        {
            MessageBox.Show("请先在「店小秘搜索」登录并导出 Cookie。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _busy = true;
        try
        {
            SetStatus(await DescribeOccupancyAsync(skus));
        }
        catch (Exception ex)
        {
            ClearOccupancy();
            SetStatus("SKU 占用检查失败：" + ex.Message);
            if (CookieErrorDetector.IsAuthFailure(ex.Message))
                CookieInvalidated?.Invoke(this, ex.Message);
            else
                MessageBox.Show(ex.Message, "SKU 占用检查失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
        }
    }

    private async void EditSemiAndCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        PersistProductUrl();
        if (!SmtProductService.TryParseProduct(ProductUrlBox.Text, out var productId, out var editUrl))
        {
            MessageBox.Show(
                "请填写店小秘速卖通产品编辑链接，例如：\nhttps://www.dianxiaomi.com/web/smt/edit?id=184807708954397661",
                "提示",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var cookieHeader = GetCookieHeader();
        var cookies = GetCookieRecords();
        if (string.IsNullOrWhiteSpace(cookieHeader) || cookies is null || cookies.Count == 0)
        {
            MessageBox.Show("请先在「店小秘搜索」登录并导出 Cookie。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _busy = true;
        try
        {
            if (string.IsNullOrWhiteSpace(_openedShopId) || !string.Equals(_openedEditUrl, editUrl, StringComparison.OrdinalIgnoreCase))
            {
                var detail = await _productService.GetDetailAsync(productId, cookieHeader);
                _openedEditUrl = editUrl;
                _openedProductId = productId;
                _openedShopId = detail.ShopId;
                EnsureCurrentShopOption();
            }

            SetStatus("正在进入半托管页 https://www.dianxiaomi.com/web/smt/editFromSmt …");
            await OpenProductWindowAsync(new Uri(editUrl), cookies, reload: false);
            ArmMainWindowInFront();
            var read = await _productWindow!.JumpToSemiAndReadSkusAsync();
            if (!string.IsNullOrWhiteSpace(read.Error))
            {
                SetStatus(read.Error);
                MessageBox.Show(read.Error, "编辑半托管信息", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (read.Rows.All(row => row.SkuCode.Length == 0 && row.ItemCode.Length == 0))
            {
                SetStatus("半托管页面已打开，但 SKU编码 和 货品编码 都是空的。");
                return;
            }

            var skuCount = read.Rows.Count(row => row.SkuCode.Length > 0);
            var itemCount = read.Rows.Count(row => row.ItemCode.Length > 0);
            var summary = await DescribeSemiOccupancyAsync(read.Rows);
            SetStatus($"半托管 SKU编码 {skuCount} 个、货品编码 {itemCount} 个。" + summary);
            BringMainWindowToFront();
        }
        catch (Exception ex)
        {
            SetStatus("编辑半托管并查重失败：" + ex.Message);
            if (CookieErrorDetector.IsAuthFailure(ex.Message))
                CookieInvalidated?.Invoke(this, ex.Message);
            else
                MessageBox.Show(ex.Message, "编辑半托管并查重失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
            BringMainWindowToFront();
        }
    }

    private async Task<string> DescribeSemiOccupancyAsync(IReadOnlyList<SmtSemiSkuRow> lines)
    {
        _suggestGeneration++;
        _semiOccupancies.Clear();
        _semiLines.Clear();
        SemiOccupancyTitle.Text = "正在检查半托管 SKU…";
        SemiOccupancyPanel.Visibility = Visibility.Visible;

        var cookieHeader = GetCookieHeader();
        if (string.IsNullOrWhiteSpace(cookieHeader) || lines.Count == 0)
            return "未检查半托管 SKU。";

        var shopId = ResolveOccupancyShopId();
        if (shopId.Length == 0)
            return "请先选择「查重店铺」（可点「刷新店铺列表」后再选）。";

        var byCode = new Dictionary<string, SmtFullOccupancyGroup>(StringComparer.OrdinalIgnoreCase);
        SmtFullOccupancyGroup GroupFor(string code, string label)
        {
            if (byCode.TryGetValue(code, out var existing))
            {
                if (!existing.ColumnLabel.Contains(label, StringComparison.Ordinal))
                    existing.ColumnLabel += "、" + label;
                return existing;
            }

            var stripped = JoomSkuSuffix.StripCompositeTrailingSuffix(code, OccupancySuffixes);
            var group = new SmtFullOccupancyGroup(code, stripped, JoomSkuSuffix.SplitParts(stripped))
            {
                ColumnLabel = label
            };
            byCode[code] = group;
            _semiOccupancies.Add(group);
            return group;
        }

        foreach (var line in lines)
        {
            var sku = line.SkuCode.Length > 0 ? GroupFor(line.SkuCode, "SKU编码") : null;
            var item = line.ItemCode.Length > 0 ? GroupFor(line.ItemCode, "货品编码") : null;
            if (sku is null && item is null)
                continue;
            var source = line.SkuCode.Length > 0 ? line.SkuCode : line.ItemCode;
            var search = JoomSkuSuffix.StripCompositeTrailingSuffix(source, OccupancySuffixes);
            _semiLines.Add(new SmtSemiLineRow(sku, item, search));
        }

        SetStatus($"正在检查半托管 {byCode.Count} 个 SKU（店铺 {shopId}，各状态合计超过 1 才算重复）…");
        var report = await _productService.FindOccupanciesAsync(byCode.Keys.ToList(), shopId, cookieHeader, SelectedScopeKey);
        FillDetailProbes(report);
        foreach (var hit in report.Hits)
        {
            var groupHit = _semiOccupancies.FirstOrDefault(g => g.Sku.Equals(hit.Sku, StringComparison.OrdinalIgnoreCase));
            if (groupHit is null)
                continue;
            var name = string.IsNullOrWhiteSpace(hit.ProductName) ? "未命名产品 " + hit.ProductId : hit.ProductName;
            groupHit.Hits.Add(new SmtFullOccupancyHitLine
            {
                Summary = hit.Location + " · 「" + name + "」",
                EditUrl = hit.EditUrl
            });
        }

        var finished = report.CompletedLocations.Count > 0;
        foreach (var group in _semiOccupancies)
        {
            group.MarkChecked(finished);
            group.NotifyHitsChanged();
        }

        var occupied = _semiOccupancies.Where(g => g.HasHits).ToList();
        _loadingColorOptions = true;
        try
        {
            foreach (var group in occupied)
            {
                group.UseColorSuffix = group.ShouldAutoEnableColorSuffix || UseOccupancyColorSuffix;
                group.LoadColorOptions(OccupancyColors);
            }

            await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
        }
        finally
        {
            _loadingColorOptions = false;
        }

        if (occupied.Count > 0)
        {
            try
            {
                await FillSuggestedSkusAsync(occupied);
            }
            catch (Exception ex)
            {
                foreach (var group in occupied)
                {
                    if (string.IsNullOrWhiteSpace(group.SuggestedSku))
                        group.SuggestStatus = "查找可用后缀失败：" + ex.Message;
                }

                if (CookieErrorDetector.IsAuthFailure(ex.Message))
                    throw;
            }
        }

        var failed = report.FailedLocations.Count == 0
            ? ""
            : " 以下范围未完整核对：" + string.Join("、", report.FailedLocations) + "。";
        var incomplete = report.IncompleteReasons.Count == 0
            ? ""
            : " 以下内容未完整核对：" + string.Join("、", report.IncompleteReasons.Take(5)) + "。";
        var unfinished = report.CompletedLocations.Count == 0 && report.FailedLocations.Count > 0;
        var claim = unfinished
            ? "系统繁忙，查重没有完成，请稍后再试。"
            : occupied.Count == 0
                ? "没有发现重复。"
                : "其中 " + occupied.Count + " 个已被占用，可在表格里选择后缀。";
        SemiOccupancyTitle.Text = "半托管共 " + _semiOccupancies.Count + " 个 SKU，" + claim + failed + incomplete;
        SemiOccupancyPanel.Visibility = Visibility.Visible;
        return claim + failed + incomplete;
    }

    private async Task<string> DescribeOccupancyAsync(
        IReadOnlyList<string> skus,
        IReadOnlyDictionary<string, string>? columnLabels = null)
    {
        ClearOccupancy();
        var cookieHeader = GetCookieHeader();
        if (string.IsNullOrWhiteSpace(cookieHeader) || skus.Count == 0)
            return "未检查 SKU 占用。";

        if (string.IsNullOrWhiteSpace(_openedShopId) && ResolveOccupancyShopId().Length == 0)
            return "未识别当前产品所属店铺，已跳过 SKU 占用检查；可用「查重店铺」下拉手动选一个店铺。";

        var scopeKey = SelectedScopeKey;
        var shopId = ResolveOccupancyShopId();
        if (shopId.Length == 0)
            return "请先选择「查重店铺」（可点「刷新店铺列表」后再选）。";

        SetStatus($"正在按在线产品列表检查 {skus.Count} 个 SKU（店铺 {shopId}，各状态合计超过 1 才算重复）…");
        var report = await _productService.FindOccupanciesAsync(skus, shopId, cookieHeader, scopeKey);
        FillDetailProbes(report);

        foreach (var hit in report.Hits)
        {
            if (!_occupancies.Any(g => g.Sku.Equals(hit.Sku, StringComparison.OrdinalIgnoreCase)))
            {
                var stripped = JoomSkuSuffix.StripCompositeTrailingSuffix(hit.Sku, OccupancySuffixes);
                var group = new SmtFullOccupancyGroup(hit.Sku, stripped, JoomSkuSuffix.SplitParts(stripped));
                if (columnLabels is not null
                    && columnLabels.TryGetValue(hit.Sku, out var columnLabel))
                {
                    group.ColumnLabel = columnLabel;
                }
                _occupancies.Add(group);
                if (UseOccupancyColorSuffix || group.ShouldAutoEnableColorSuffix)
                    group.UseColorSuffix = true;
            }

            var groupHit = _occupancies.First(g => g.Sku.Equals(hit.Sku, StringComparison.OrdinalIgnoreCase));
            var name = string.IsNullOrWhiteSpace(hit.ProductName) ? "未命名产品 " + hit.ProductId : hit.ProductName;
            groupHit.Hits.Add(new SmtFullOccupancyHitLine
            {
                Summary = hit.Location + " · 「" + name + "」",
                EditUrl = hit.EditUrl
            });
        }

        var failed = report.FailedLocations.Count == 0
            ? ""
            : " 以下范围未完整核对：" + string.Join("、", report.FailedLocations) + "。";
        var incomplete = report.IncompleteReasons.Count == 0
            ? ""
            : " 以下内容未完整核对：" + string.Join("、", report.IncompleteReasons.Take(5)) + "。";

        if (_occupancies.Count == 0)
        {
            var scope = report.Scope;
            var claim = report.CompletedLocations.Count == 0
                ? "本次未能核对任何范围，不能保证无重复。"
                : failed.Length > 0 || incomplete.Length > 0
                    ? "未发现其他占用，但检查不完整，不能保证无重复。"
                    : "没有发现其他占用。";
            OccupancyTitle.Text = "";
            OccupancyPanel.Visibility = Visibility.Collapsed;
            return "已检查 " + skus.Count + " 个 SKU（" + scope + "），" + claim + failed + incomplete;
        }

        var semiNote = columnLabels is null ? "" : "半托管 ";
        OccupancyTitle.Text = semiNote + _occupancies.Count + " 个 SKU 已被本店铺产品占用，直接发布可能提示重复";
        OccupancyExpander.IsExpanded = true;
        OccupancyPanel.Visibility = Visibility.Visible;
        _loadingColorOptions = true;
        try
        {
            foreach (var group in _occupancies)
            {
                group.UseColorSuffix = group.ShouldAutoEnableColorSuffix || UseOccupancyColorSuffix;
                group.LoadColorOptions(OccupancyColors);
            }

            await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
        }
        finally
        {
            _loadingColorOptions = false;
        }

        try
        {
            await FillSuggestedSkusAsync(_occupancies.ToList());
        }
        catch (Exception ex)
        {
            foreach (var group in _occupancies)
            {
                if (string.IsNullOrWhiteSpace(group.SuggestedSku))
                    group.SuggestStatus = "查找可用后缀失败：" + ex.Message;
            }

            if (CookieErrorDetector.IsAuthFailure(ex.Message))
                throw;
        }

        var suggested = _occupancies.Count(g => !string.IsNullOrWhiteSpace(g.SuggestedSku));
        var note = suggested == _occupancies.Count
            ? " 已在每个重复 SKU 后面填入未占用的新 SKU，可点「应用新SKU」写入详情页。"
            : " 已填入 " + suggested + " 个未占用的新 SKU，其余请手动填写。";
        return "发现 " + report.Hits.Count + " 处 SKU 占用（" + report.Scope + "），发布前请先更换 SKU。"
               + note + failed + incomplete;
    }

    private void ClearOccupancy()
    {
        _suggestGeneration++;
        _occupancies.Clear();
        _detailProbes.Clear();
        _verifiedSkus.Clear();
        OccupancyTitle.Text = "";
        OccupancyPanel.Visibility = Visibility.Collapsed;
        DetailProbeExpander.Visibility = Visibility.Collapsed;
        foreach (var row in _rows)
            row.SuggestedSku = null;
    }

    private void FillDetailProbes(SmtSkuOccupancyReport report)
    {
        _detailProbes.Clear();
        foreach (var probe in report.DetailProbes)
            _detailProbes.Add(probe);
        DetailProbeExpander.Visibility = _detailProbes.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void CopyDetailProbes_Click(object sender, RoutedEventArgs e)
    {
        if (_detailProbes.Count == 0)
            return;
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, _detailProbes));
            SetStatus($"已复制 {_detailProbes.Count} 条查重诊断。");
        }
        catch (Exception ex)
        {
            SetStatus("复制诊断失败：" + ex.Message);
        }
    }

    private void CommitGrid()
    {
        SkuGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        SkuGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    /// <summary>商品库搜索用「搜索SKU」。查重和回写仍用「页面SKU」。</summary>
    private List<string> CollectSearchSkus()
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _rows)
        {
            var sku = NormalizeSku(string.IsNullOrWhiteSpace(row.Sku) ? row.PageSku : row.Sku);
            if (sku.Length > 0 && seen.Add(sku))
                result.Add(sku);
        }

        return result;
    }

    private List<string> CollectOccupancySkus()
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _rows)
        {
            var sku = NormalizeSku(row.PageSku);
            if (sku.Length > 0 && seen.Add(sku))
                result.Add(sku);
        }

        return result;
    }

    private async Task FillSuggestedSkusAsync(IReadOnlyList<SmtFullOccupancyGroup> targets)
    {
        if (targets.Count == 0)
            return;

        var generation = _suggestGeneration;
        var general = OccupancySuffixes;
        var pending = new List<SmtFullOccupancyGroup>();
        foreach (var group in targets)
        {
            if (general.Count == 0)
            {
                group.SuggestStatus = "请先填写「SKU 后缀」";
                continue;
            }

            group.SuggestStatus = "正在查找未重复的后缀…";
            pending.Add(group);
        }

        if (pending.Count == 0)
        {
            RefreshSuggestedSkuColumn();
            return;
        }

        var reserved = CollectReservedSkus();
        var checkedCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? verifyGap = null;

        for (var i = 0; i < general.Count && pending.Count > 0; i++)
        {
            if (generation != _suggestGeneration)
                return;

            var batch = new List<(SmtFullOccupancyGroup Group, string Candidate)>();
            var waiting = new List<SmtFullOccupancyGroup>();
            foreach (var group in pending)
            {
                if (i > 0 && !group.AnyPartUsesGeneralSuffix)
                {
                    waiting.Add(group);
                    continue;
                }

                var candidate = group.BuildCandidate(general[i]);
                if (candidate.Length == 0
                    || candidate.Contains(',')
                    || general[i].Length == 0
                    || candidate.Equals(group.Sku, StringComparison.OrdinalIgnoreCase)
                    || !reserved.Add(candidate))
                {
                    waiting.Add(group);
                    continue;
                }

                batch.Add((group, candidate));
            }

            var occupied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (batch.Count > 0)
            {
                SetStatus("正在检查 " + batch.Count + " 个候选 SKU 是否仍会重复…");
                var cookieHeader = GetCookieHeader();
                if (string.IsNullOrWhiteSpace(cookieHeader))
                    throw new InvalidOperationException("请先导出 Cookie。");

                var report = await _productService.FindOccupanciesAsync(
                    batch.Select(item => item.Candidate).ToList(),
                    ResolveOccupancyShopId(),
                    cookieHeader,
                    SelectedScopeKey);
                if (generation != _suggestGeneration)
                    return;

                verifyGap ??= BuildVerifyGap(report);
                foreach (var item in batch)
                    checkedCandidates.Add(item.Candidate);
                foreach (var hit in report.Hits)
                    occupied.Add(hit.Sku);
            }

            var still = new List<SmtFullOccupancyGroup>(waiting);
            foreach (var (group, candidate) in batch)
            {
                if (occupied.Contains(candidate))
                {
                    reserved.Remove(candidate);
                    still.Add(group);
                    continue;
                }

                group.SuggestedSku = candidate;
                group.SuggestStatus = verifyGap is null
                    ? "该后缀未被占用"
                    : "该后缀未发现占用（部分范围未核对完）";
            }

            pending = still;
        }

        foreach (var group in pending)
        {
            group.SuggestStatus = verifyGap is null
                ? "这些后缀都会被占用，请手动填写"
                : "这些后缀未发现占用，但部分范围未核对完：" + verifyGap;
        }

        _verifiedSkus.Clear();
        foreach (var sku in checkedCandidates)
            _verifiedSkus.Add(sku);
        RefreshSuggestedSkuColumn();
    }

    private HashSet<string> CollectReservedSkus()
    {
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _rows)
        {
            var page = NormalizeSku(row.PageSku);
            var search = NormalizeSku(row.Sku);
            var applied = NormalizeSku(row.AppliedPageSku);
            if (page.Length > 0) reserved.Add(page);
            if (search.Length > 0) reserved.Add(search);
            if (applied.Length > 0) reserved.Add(applied);
        }

        foreach (var group in _occupancies)
        {
            var suggested = NormalizeSku(group.SuggestedSku);
            if (suggested.Length > 0)
                reserved.Add(suggested);
        }

        return reserved;
    }

    private static string? BuildVerifyGap(SmtSkuOccupancyReport report)
    {
        if (report.FailedLocations.Count == 0 && report.IncompleteReasons.Count == 0)
            return null;

        var parts = report.FailedLocations
            .Concat(report.IncompleteReasons)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var shown = string.Join("、", parts.Take(3));
        return parts.Count > 3 ? shown + " 等 " + parts.Count + " 处" : shown;
    }

    private void RefreshSuggestedSkuColumn()
    {
        var available = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in _occupancies.Concat(_semiOccupancies))
        {
            if (string.IsNullOrWhiteSpace(group.SuggestedSku))
                continue;
            available.TryAdd(group.Sku, NormalizeSku(group.SuggestedSku));
        }

        var managed = new HashSet<string>(
            _occupancies.Concat(_semiOccupancies).Select(g => g.Sku),
            StringComparer.OrdinalIgnoreCase);
        foreach (var row in _rows)
        {
            var original = NormalizeSku(row.PageSku);
            if (original.Length == 0 || !managed.Contains(original))
                continue;
            row.SuggestedSku = available.TryGetValue(original, out var replacement) ? replacement : null;
        }
    }

    private async void OccupancyLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Hyperlink link || link.Tag is not string url || string.IsNullOrWhiteSpace(url))
            return;
        e.Handled = true;
        if (_busy)
            return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return;

        var cookies = GetCookieRecords();
        if (cookies is null || cookies.Count == 0)
        {
            MessageBox.Show("请先在「店小秘搜索」登录并导出 Cookie。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _busy = true;
        try
        {
            SetStatus("正在另开窗口打开占用该 SKU 的产品…");
            var window = new SmtFullProductPageWindow();
            _occupancyWindows.Add(window);
            window.Closed += (_, _) => _occupancyWindows.Remove(window);
            window.Show();
            await window.OpenProductAsync(uri, cookies);
            SetStatus("已另开窗口打开占用该 SKU 的产品。");
        }
        catch (Exception ex)
        {
            SetStatus("打开占用产品失败：" + ex.Message);
            MessageBox.Show(ex.Message, "打开占用产品失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
        }
    }

    private async void ApplySuggestedSkus_Click(object sender, RoutedEventArgs e)
    {
        await ChangePageSkusAsync(restoreOriginal: false);
    }

    private async void RestoreOriginalSkus_Click(object sender, RoutedEventArgs e)
    {
        await ChangePageSkusAsync(restoreOriginal: true);
    }

    private async Task ChangePageSkusAsync(bool restoreOriginal)
    {
        if (_busy)
            return;

        var changes = new List<(SmtFullRow Row, string From, string To)>();
        foreach (var row in _rows)
        {
            var original = NormalizeSku(row.PageSku);
            var applied = NormalizeSku(string.IsNullOrWhiteSpace(row.AppliedPageSku) ? row.PageSku : row.AppliedPageSku);
            var target = restoreOriginal ? original : NormalizeSku(row.SuggestedSku);
            if (applied.Length == 0
                || target.Length == 0
                || applied.Equals(target, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            changes.Add((row, applied, target));
        }

        if (changes.Count == 0)
        {
            MessageBox.Show(
                restoreOriginal ? "没有需要恢复的 SKU。" : "没有已确认可用的建议 SKU。",
                "提示",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var cookies = GetCookieRecords();
        var editUrl = _openedEditUrl;
        if (string.IsNullOrWhiteSpace(editUrl)
            && SmtProductService.TryParseProduct(ProductUrlBox.Text, out _, out var parsedUrl))
        {
            editUrl = parsedUrl;
        }

        if (cookies is null || cookies.Count == 0 || string.IsNullOrWhiteSpace(editUrl))
        {
            MessageBox.Show("请先在「店小秘搜索」登录并打开速卖通产品详情页。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _busy = true;
        try
        {
            SetStatus(restoreOriginal ? "正在恢复详情页原 SKU…" : "正在应用建议 SKU 到详情页…");
            if (!restoreOriginal)
            {
                var targets = changes.Select(c => c.To).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var uncheckedTargets = targets.Where(s => !_verifiedSkus.Contains(s)).ToList();
                if (uncheckedTargets.Count > 0)
                {
                    var confirm = await _productService.FindOccupanciesAsync(
                        uncheckedTargets, ResolveOccupancyShopId(), GetCookieHeader(), SelectedScopeKey);
                    var occupied = confirm.Hits
                        .Select(h => h.Sku)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (occupied.Count > 0)
                    {
                        SetStatus("写回前复检发现建议 SKU 已被占用，已取消应用。");
                        MessageBox.Show(
                            "写回前复检发现以下建议 SKU 已被本店铺产品占用，已取消应用：\n"
                            + string.Join("、", occupied),
                            "应用新SKU", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    var gap = BuildVerifyGap(confirm);
                    if (gap is not null)
                    {
                        var answer = MessageBox.Show(
                            "这些建议 SKU 没有发现被占用，但以下范围未能完整核对：\n" + gap
                            + "\n\n仍要写入详情页吗？（店小秘保存时还会再做一次重复校验）",
                            "应用新SKU", MessageBoxButton.YesNo, MessageBoxImage.Question);
                        if (answer != MessageBoxResult.Yes)
                        {
                            SetStatus("已取消应用建议 SKU。");
                            return;
                        }
                    }
                }
            }

            await OpenProductWindowAsync(new Uri(editUrl), cookies, reload: false);
            var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var change in changes)
                replacements.TryAdd(change.From, change.To);
            var result = await _productWindow!.ReplaceSkusAsync(replacements);
            var missed = result.MissedPageSkus.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var change in changes.Where(c => !missed.Contains(c.From)))
                change.Row.AppliedPageSku = change.To;

            var action = restoreOriginal ? "恢复原 SKU" : "应用新 SKU";
            var unverified = Math.Max(0, result.Updated - result.Verified);
            var verifyNote = result.Updated > 0 && unverified > 0
                ? $"；其中 {unverified} 条写入后未能回读确认，请人工核对"
                : "";
            var hint = $"已{action} {result.Updated} 条{verifyNote}。请在店小秘页面确认后点击保存。";
            _productWindow.SetHint(hint);
            SetStatus(hint);
            if (!string.IsNullOrWhiteSpace(result.Error) || missed.Count > 0 || unverified > 0)
            {
                MessageBox.Show(
                    hint + (missed.Count == 0 ? "" : "\n\n未匹配：" + string.Join("、", missed.Take(20)))
                    + (string.IsNullOrWhiteSpace(result.Error) ? "" : "\n\n" + result.Error),
                    action, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            SetStatus("SKU 替换失败：" + ex.Message);
            MessageBox.Show(ex.Message, "SKU 替换失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
        }
    }

    private sealed class SmtFullOccupancyHitLine
    {
        public string Summary { get; init; } = "";
        public string EditUrl { get; init; } = "";
    }

    private sealed class SmtSemiLineRow : INotifyPropertyChanged
    {
        private string _searchSku;
        private double? _price;

        public SmtSemiLineRow(SmtFullOccupancyGroup? sku, SmtFullOccupancyGroup? item, string searchSku)
        {
            SkuSide = sku;
            ItemSide = item;
            _searchSku = searchSku;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public SmtFullOccupancyGroup? SkuSide { get; }
        public SmtFullOccupancyGroup? ItemSide { get; }

        /// <summary>查商品库价格用的货号。默认从 SKU编码去掉末尾通用后缀、保留颜色。</summary>
        public string SearchSku
        {
            get => _searchSku;
            set
            {
                if (_searchSku == value)
                    return;
                _searchSku = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SearchSku)));
            }
        }

        /// <summary>按搜索 SKU 查到的商品库参考价。</summary>
        public double? Price
        {
            get => _price;
            set
            {
                if (_price == value)
                    return;
                _price = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Price)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PriceText)));
            }
        }

        public string PriceText => _price is null ? "" : _price.Value.ToString("0.00");
    }

    private sealed class SmtFullOccupancyGroup : INotifyPropertyChanged
    {
        private bool _useColorSuffix;
        private string _suggestedSku = "";
        private string _suggestStatus = "";

        public SmtFullOccupancyGroup(string sku, string baseSku, IEnumerable<string> parts)
        {
            Sku = sku;
            BaseSku = baseSku;
            var index = 1;
            foreach (var part in parts)
                Parts.Add(new SmtFullSkuPartSlot(this, index++, part));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Sku { get; }
        public string BaseSku { get; }
        /// <summary>半托管查重时标明来自 SKU编码 还是 货品编码。普通查重为空。</summary>
        private string _columnLabel = "";
        public string ColumnLabel
        {
            get => _columnLabel;
            set
            {
                if (_columnLabel == value)
                    return;
                _columnLabel = value;
                OnPropertyChanged(nameof(ColumnLabel));
            }
        }
        public bool HasHits => Hits.Count > 0;
        public bool Checked { get; private set; }
        public string OccupancyResult =>
            !Checked ? "未核对" : Hits.Count > 0 ? "已占用" : "未占用";

        public void NotifyHitsChanged()
        {
            OnPropertyChanged(nameof(HasHits));
            OnPropertyChanged(nameof(OccupancyResult));
        }

        public void MarkChecked(bool finished)
        {
            Checked = finished;
            OnPropertyChanged(nameof(OccupancyResult));
        }
        public ObservableCollection<SmtFullSkuPartSlot> Parts { get; } = [];
        public ObservableCollection<string> ColorOptions { get; } = [];
        public ObservableCollection<SmtFullOccupancyHitLine> Hits { get; } = [];
        public bool SuggestionUserEdited { get; set; }
        public bool HasMultipleParts => Parts.Count > 1;

        /// <summary>原 SKU 还带着颜色后缀（如 -grey）时自动勾选，单段和复合都算。</summary>
        public bool ShouldAutoEnableColorSuffix =>
            Parts.Any(part => part.OriginalColorSuffix.Length > 0);

        public bool AnyPartUsesGeneralSuffix =>
            !HasMultipleParts || Parts.Any(part => part.UseGeneralSuffix);

        public string BaseHint =>
            string.Equals(BaseSku, Sku, StringComparison.Ordinal)
                ? "该 SKU 没有可去掉的末尾后缀，将直接在每段末尾拼接。"
                : "去掉末尾查重后缀（保留颜色）后得到 " + BaseSku + "，再按段拼接。";

        public bool UseColorSuffix
        {
            get => _useColorSuffix;
            set
            {
                if (_useColorSuffix == value)
                    return;
                _useColorSuffix = value;
                OnPropertyChanged(nameof(UseColorSuffix));
            }
        }

        public string SuggestedSku
        {
            get => _suggestedSku;
            set => Set(ref _suggestedSku, value);
        }

        public string SuggestStatus
        {
            get => _suggestStatus;
            set => Set(ref _suggestStatus, value);
        }

        public void LoadColorOptions(IReadOnlyList<string> colors)
        {
            var previous = Parts.ToDictionary(part => part, part => part.SelectedColor);
            ColorOptions.Clear();
            ColorOptions.Add(JoomSkuSuffix.NoColorOption);
            foreach (var color in colors)
                ColorOptions.Add(color);

            foreach (var part in Parts)
                part.SelectedColor = ResolveColorChoice(previous[part], part.OriginalColorSuffix, colors);
        }

        private static string ResolveColorChoice(string? current, string originalColor, IReadOnlyList<string> colors)
        {
            if (string.Equals(current, JoomSkuSuffix.NoColorOption, StringComparison.Ordinal))
                return JoomSkuSuffix.NoColorOption;

            if (JoomSkuSuffix.IsColorChoice(current))
            {
                var kept = colors.FirstOrDefault(color => color.Equals(current, StringComparison.OrdinalIgnoreCase));
                if (kept is not null)
                    return kept;
            }

            if (originalColor.Length > 0)
            {
                var match = colors.FirstOrDefault(color => color.Equals(originalColor, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                    return match;
            }

            return JoomSkuSuffix.NoColorOption;
        }

        public string BuildCandidate(string occupancySuffix)
        {
            return string.Join("+", Parts.Select(part =>
                JoomSkuSuffix.Combine(
                    part.BaseSku,
                    UseColorSuffix && JoomSkuSuffix.IsColorChoice(part.SelectedColor) ? part.SelectedColor : null,
                    HasMultipleParts && !part.UseGeneralSuffix ? "" : occupancySuffix)));
        }

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value))
                return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private sealed class SmtFullSkuPartSlot : INotifyPropertyChanged
    {
        private string? _selectedColor;
        private bool _useGeneralSuffix = true;

        public SmtFullSkuPartSlot(SmtFullOccupancyGroup owner, int index, string sku)
        {
            Owner = owner;
            Index = index;
            Sku = sku;
            BaseSku = JoomSkuSuffix.StripBase(sku);
            OriginalColorSuffix = sku.Length > BaseSku.Length ? sku[BaseSku.Length..] : "";
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public SmtFullOccupancyGroup Owner { get; }
        public int Index { get; }
        public string Sku { get; }
        public string BaseSku { get; }
        public string OriginalColorSuffix { get; }
        public string Label => "第" + Index + "段 " + Sku;

        public bool UseGeneralSuffix
        {
            get => _useGeneralSuffix;
            set
            {
                if (_useGeneralSuffix == value)
                    return;
                _useGeneralSuffix = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UseGeneralSuffix)));
            }
        }

        public string? SelectedColor
        {
            get => _selectedColor;
            set
            {
                if (string.Equals(_selectedColor, value, StringComparison.Ordinal))
                    return;
                _selectedColor = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedColor)));
            }
        }
    }

    /// <summary>横向无忧表的一行。索引对应国家列，写回同一条运费。</summary>
    private sealed class FreightSheetRow : INotifyPropertyChanged
    {
        private readonly string _field;
        private readonly IList<SmtFreightRate> _rates;

        public FreightSheetRow(string label, string field, IList<SmtFreightRate> rates)
        {
            Label = label;
            _field = field;
            _rates = rates;
        }

        public string Label { get; }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string this[int index]
        {
            get
            {
                if ((uint)index >= (uint)_rates.Count)
                    return "";

                var rate = _rates[index];
                return _field switch
                {
                    "Code" => rate.Code,
                    "PerKg" => FormatFormula(rate.PerKg),
                    "Registration" => FormatFormula(rate.Registration),
                    _ => ""
                };
            }
            set
            {
                if ((uint)index >= (uint)_rates.Count)
                    return;

                var rate = _rates[index];
                var text = value?.Trim() ?? "";
                switch (_field)
                {
                    case "Code":
                        rate.Code = text;
                        break;
                    case "PerKg":
                        rate.PerKg = ParseRate(text, rate.PerKg);
                        break;
                    case "Registration":
                        rate.Registration = ParseRate(text, rate.Registration);
                        break;
                }

                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[" + index + "]"));
            }
        }

        public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));

        private static double ParseRate(string text, double fallback) =>
            double.TryParse(text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var value)
            || double.TryParse(text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.CurrentCulture, out value)
                ? value
                : fallback;
    }
}
