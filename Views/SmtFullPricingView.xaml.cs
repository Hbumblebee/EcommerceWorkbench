/*
 * 功能说明：速卖通全托页。打开 /web/smt/edit 详情，读取变种 SKU，并按与速卖通7 相同的规则查重。
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
        FreightRateGrid.ItemsSource = _freightSheetRows;
        PercentRateGrid.ItemsSource = _percentRates;
        OccupancyList.ItemsSource = _occupancies;
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

    /// <summary>
    /// 鼠标在公式表、结果表、查重区里时先滚那一块；滚到头再滚整页。
    /// 表格按行，文字区按像素。
    /// </summary>
    private void PageScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var target = PageScroll;
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
            var loaded = $"已读取变种 SKU {detail.Skus.Count} 个。店铺 {detail.ShopId}。";
            try
            {
                var occupancy = await DescribeOccupancyAsync(detail.Skus.Select(s => s.PageSku).ToList());
                SetStatus(loaded + occupancy);
            }
            catch (Exception occupancyEx)
            {
                ClearOccupancy();
                SetStatus(loaded + "占用检查失败：" + occupancyEx.Message);
                if (CookieErrorDetector.IsAuthFailure(occupancyEx.Message))
                    CookieInvalidated?.Invoke(this, occupancyEx.Message);
                return;
            }

            _productWindow?.SetHint("已读取变种 SKU。若列出同店铺已占用的 SKU，建议 SKU 已按原后缀向后依次找过未占用的货号。");
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

    private async Task OpenProductWindowAsync(Uri url, IReadOnlyList<CookieRecord> cookies, bool reload = true)
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

        ArmMainWindowInFront();
        if (reload || !_productWindow.IsShowing(url))
            await _productWindow.OpenProductAsync(url, cookies);

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
            row.Status = "已按当前公式重算";
        }
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

    private async void QuoteAndWrite_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        CommitGrid();
        CommitFormulaEdits();
        var skus = CollectSearchSkus();
        if (skus.Count == 0)
        {
            MessageBox.Show("请先打开产品详情读取 SKU，或在「搜索SKU」列填写要查询的货号。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (skus.Count > ProductSearchService.MaxSearchValuesPerRequest)
        {
            MessageBox.Show($"单次最多搜索 {ProductSearchService.MaxSearchValuesPerRequest} 个 SKU。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var cookieHeader = GetCookieHeader();
        var cookies = GetCookieRecords();
        if (string.IsNullOrWhiteSpace(cookieHeader) || cookies is null || cookies.Count == 0)
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
            SetStatus($"正在按 {skus.Count} 个搜索 SKU 查询参考价和重量…");
            var outcome = await _productSearch.SearchAsync(skus, cookieHeader);
            var priced = ApplySearchAndCalculate(outcome);
            if (priced.Count == 0)
            {
                SetStatus("没有算出可回写的售价。请确认商品库能搜到这些 SKU 的参考价和重量。");
                MessageBox.Show("没有算出可回写的售价。未命中或缺少价格/重量的 SKU 已标在表格状态列。", "搜索定价", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

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
            SetStatus("搜索定价或回写失败：" + ex.Message);
            if (CookieErrorDetector.IsAuthFailure(ex.Message))
                CookieInvalidated?.Invoke(this, ex.Message);
            else
                MessageBox.Show(ex.Message, "搜索定价或回写失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>商品库重量是克。参考价当作成本，按挂号包邮表算出售价和各国调价。</summary>
    private List<object> ApplySearchAndCalculate(ProductSearchOutcome outcome)
    {
        var bySku = new Dictionary<string, ProductResultRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var hit in outcome.Rows)
        {
            var key = NormalizeSku(hit.Sku);
            if (key.Length > 0)
                bySku.TryAdd(key, hit);
        }

        var payload = new List<object>();
        foreach (var row in _rows)
        {
            var search = NormalizeSku(string.IsNullOrWhiteSpace(row.Sku) ? row.PageSku : row.Sku);
            var pageSku = NormalizeSku(row.PageSku);
            row.Cost = null;
            row.WeightKg = null;
            row.UndiscountedPrice = null;
            row.Profit = null;
            row.SmtPrice = null;
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
            row.Status = "已计算";
            var countries = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var country in quote.Countries)
                countries[country.Code] = country.Amount;
            payload.Add(new
            {
                sku = pageSku,
                price = quote.SmtPrice,
                weightKg,
                countries
            });
        }

        return payload;
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

    private async Task<string> DescribeOccupancyAsync(IReadOnlyList<string> skus)
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

        OccupancyTitle.Text = _occupancies.Count + " 个 SKU 已被本店铺产品占用，直接发布可能提示重复";
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
        var available = _occupancies
            .Where(g => !string.IsNullOrWhiteSpace(g.SuggestedSku))
            .GroupBy(g => g.Sku, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => NormalizeSku(g.First().SuggestedSku), StringComparer.OrdinalIgnoreCase);

        foreach (var row in _rows)
        {
            var original = NormalizeSku(row.PageSku);
            if (original.Length == 0)
            {
                row.SuggestedSku = null;
                continue;
            }

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
