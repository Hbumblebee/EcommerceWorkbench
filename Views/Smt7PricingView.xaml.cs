/*
 * 功能说明：速卖通7 批量定价页，对齐计算表全托2.0。
 * 主要职责：打开速卖通全托管详情页读取变种 SKU，搜索参考价/重量后计算最终价，并回写供货价。
 * 创建日期：2026-09-13
 * 更新日期：2026-09-13 重量按货品条码匹配货品信息，搜索只填成本
 *           2026-10-07 「搜索SKU」只去末尾通用后缀、保留颜色后缀（与 JOOM 页口径一致）
 *           2026-10-07 新增按「当前店铺」的 SKU 查重：占用面板、建议 SKU、一键应用/恢复
 *           2026-10-07 查重范围/店铺可选；查重后缀独立可编辑
 *           2026-10-07 查重面板与 JOOM 对齐：启用颜色后缀后组合 SKU 逐段选色，按原顺序拼回
 *           2026-10-08 页面排布与滚动对齐 JOOM：整页 ScrollViewer + 表格/重复区到边界后交给整页
 */

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using EcommerceWorkbench.Models;
using EcommerceWorkbench.Services;
using EcommerceWorkbench.Services.Dianxiaomi;
using EcommerceWorkbench.Services.Joom;
using EcommerceWorkbench.Services.Smt7;
using EcommerceWorkbench.UI;

namespace EcommerceWorkbench.Views;

public partial class Smt7PricingView : UserControl
{
    private readonly ObservableCollection<Smt7Row> _rows = [];
    private readonly ApiClient _apiClient = new();
    private readonly ChoiceProductService _choiceProduct;
    private readonly ProductSearchService _productSearch;
    private readonly CookieStore _cookieStore = new();
    private readonly Smt7UserSettings _settings = Smt7UserSettings.Load();
    private TextBox[] _costMaxBoxes = [];
    private TextBox[] _costFactor1Boxes = [];
    private TextBox[] _costConstBoxes = [];
    private TextBox[] _costFactor2Boxes = [];
    private TextBox[] _costMarginBoxes = [];
    private TextBox[] _weightMaxBoxes = [];
    private TextBox[] _weightMarkupBoxes = [];
    private bool _suppressAutoCalc;
    private bool _loaded;
    private bool _busy;
    private Smt7ProductPageWindow? _productWindow;
    private string? _openedEditUrl;
    private string? _openedProductId;
    /// <summary>当前产品所属店铺；查重只在该店铺内进行。</summary>
    private string? _openedShopId;
    private readonly List<Smt7ProductPageWindow> _occupancyWindows = [];
    private readonly ObservableCollection<Smt7OccupancyGroup> _occupancies = [];
    private readonly ObservableCollection<string> _detailProbes = [];
    private readonly HashSet<string> _verifiedSkus = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<ChoiceShopOption> _shopOptions = [];
    private int _suggestGeneration;
    private bool _loadingShopOptions;
    private bool _loadingColorOptions;

    public event EventHandler<string>? StatusChanged;
    public event EventHandler<string>? CountChanged;
    public Func<string?>? CookieHeaderProvider { get; set; }
    public Func<IReadOnlyList<CookieRecord>?>? CookieRecordsProvider { get; set; }
    public event EventHandler<string>? CookieInvalidated;

    public Smt7PricingView()
    {
        InitializeComponent();
        BindParamBoxes();
        _choiceProduct = new ChoiceProductService(_apiClient);
        _productSearch = new ProductSearchService(_apiClient);
        PricingGrid.ItemsSource = _rows;
        OccupancyList.ItemsSource = _occupancies;
        DetailProbeList.ItemsSource = _detailProbes;
        OccupancyShopCombo.ItemsSource = _shopOptions;
        Loaded += Smt7PricingView_Loaded;
    }

    /// <summary>
    /// 统一接管滚轮：优先滚动鼠标所在的重复区或表格，到边界后平滑交给整页。
    /// 与 JOOM 页同一套做法。
    /// </summary>
    private void PageScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        ScrollViewer target = PageScroll;
        if (OccupancyScroll.IsMouseOver && SmoothScrollHelper.CanScroll(OccupancyScroll, e.Delta))
        {
            target = OccupancyScroll;
        }
        else if (PricingGrid.IsMouseOver)
        {
            var gridScroll = FindVisualChild<ScrollViewer>(PricingGrid, "DG_ScrollViewer");
            if (gridScroll is not null && SmoothScrollHelper.CanScroll(gridScroll, e.Delta))
                target = gridScroll;
        }

        SmoothScrollHelper.ScrollWheel(target, e.Delta);
        e.Handled = true;
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

    public void Cleanup()
    {
        try
        {
            _productWindow?.Close();
        }
        catch
        {
            // 关闭详情窗失败不影响退出
        }

        _productWindow = null;
        _apiClient.Dispose();
    }

    private void Smt7PricingView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
            return;
        _loaded = true;
        if (!string.IsNullOrWhiteSpace(_settings.ProductEditUrl))
            ProductUrlBox.Text = _settings.ProductEditUrl;
        SuffixBox.Text = string.IsNullOrWhiteSpace(_settings.GeneralSkuSuffixes)
            ? Smt7UserSettings.DefaultGeneralSkuSuffixes
            : _settings.GeneralSkuSuffixes;
        ColorSuffixBox.Text = string.IsNullOrWhiteSpace(_settings.OccupancyColorSuffixes)
            ? JoomUserSettings.DefaultColorSkuSuffixes
            : _settings.OccupancyColorSuffixes;
        InitializeOccupancySelectors();
        ApplyParamsToBoxes(_settings.ToCalcSettings());
        SeedEmptyRows(12);
    }

    /// <summary>
    /// 将店小秘命中行导入速卖通7 表：按 SKU 覆盖成本（不改重量），新 SKU 追加，去掉空行后立即计算。
    /// 重量只来自详情页货品信息，不取搜索结果。
    /// </summary>
    public int ImportHits(IReadOnlyList<ProductResultRow> hits)
    {
        CommitGrid();
        _suppressAutoCalc = true;
        var imported = 0;
        try
        {
            for (var i = _rows.Count - 1; i >= 0; i--)
            {
                if (!_rows[i].HasAnyInput)
                    _rows.RemoveAt(i);
            }

            var bySku = new Dictionary<string, Smt7Row>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in _rows)
            {
                var key = NormalizeSku(row.Sku);
                if (key.Length > 0)
                    bySku.TryAdd(key, row);
            }

            foreach (var hit in hits)
            {
                var sku = NormalizeSku(hit.Sku);
                if (sku.Length == 0)
                    continue;

                var cost = hit.Price.HasValue
                    ? (double)Math.Round(hit.Price.Value, 2, MidpointRounding.AwayFromZero)
                    : (double?)null;

                if (bySku.TryGetValue(sku, out var existing))
                {
                    existing.Cost = cost;
                    imported++;
                }
                else
                {
                    var row = new Smt7Row
                    {
                        Sku = SkuText.Normalize(hit.Sku),
                        Cost = cost
                    };
                    _rows.Add(row);
                    bySku[sku] = row;
                    imported++;
                }
            }

            Renumber();
        }
        finally
        {
            _suppressAutoCalc = false;
            RecalculateAll();
            SetStatus($"已导入 {imported} 条到速卖通7 并完成计算（重量仍用货品信息，未取搜索重量）。");
        }

        return imported;
    }

    public void RecalculateAll()
    {
        CommitGrid();
        var calcSettings = ReadSettings();
        PersistCalcSettings(calcSettings);
        var okCount = 0;
        var skipCount = 0;
        var errCount = 0;

        foreach (var row in _rows)
        {
            if (!row.HasAnyInput)
            {
                ClearResult(row);
                row.Status = "";
                skipCount++;
                continue;
            }

            if (row.Cost is null)
            {
                ClearResultKeepWeight(row);
                row.Status = "请填写成本";
                errCount++;
                continue;
            }

            try
            {
                var result = Smt7Calculator.Calculate(row.Cost.Value, row.Weight, calcSettings);
                row.ConvertedWeight = FormatKg(result.ConvertedWeightKg);
                row.Interval = result.Interval;
                row.Margin = result.Margin;
                row.Factor1 = FormatMoney(result.Factor1);
                row.Constant = FormatMoney(result.Constant);
                row.Factor2 = FormatMoney(result.Factor2);
                row.RetailPrice = FormatMoney(result.RetailPrice);
                row.Markup = FormatMoney(result.Markup);
                row.FinalPrice = FormatMoney(result.FinalPrice);
                row.Status = "OK";
                okCount++;
            }
            catch (Exception ex)
            {
                ClearResult(row);
                row.Status = ex.Message;
                errCount++;
            }
        }

        UpdateCount();
        // 重量缺失时公式按 0kg 加价，最终价会偏低。原实现只显示「成功 N」，容易整批误用。
        var missingWeight = _rows.Count(r => r.Status == "OK" && r.Weight is null);
        var weightNote = missingWeight == 0
            ? ""
            : $" · 注意 {missingWeight} 行缺重量（已按 0kg 未加价）";
        SetStatus($"速卖通7 计算完成：成功 {okCount} · 跳过 {skipCount} · 失败 {errCount}{weightNote}");
    }

    public void RefreshCount() => UpdateCount();

    private void ProductUrlBox_LostFocus(object sender, RoutedEventArgs e) => PersistProductUrl();

    private void SuffixBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var general = SuffixBox.Text ?? "";
        var color = ColorSuffixBox.Text ?? "";
        if (general == _settings.GeneralSkuSuffixes && color == _settings.OccupancyColorSuffixes)
            return;

        _settings.GeneralSkuSuffixes = general;
        _settings.OccupancyColorSuffixes = color;
        try
        {
            _settings.Save();
        }
        catch
        {
            // 本地保存失败不阻断操作
        }

        RefreshOccupancyColorOptions();
    }

    private void ColorSuffixBox_LostFocus(object sender, RoutedEventArgs e) => SuffixBox_LostFocus(sender, e);

    private async void OpenProduct_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        CommitGrid();
        PersistProductUrl();
        if (!ChoiceProductService.TryParseProduct(ProductUrlBox.Text, out var productId, out var editUrl))
        {
            MessageBox.Show(
                "请填写店小秘速卖通全托管产品详情链接，例如：\nhttps://www.dianxiaomi.com/web/smtChoice/edit?id=184807706170558289",
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

            var (uniqueSkus, shopId) = await ReadSkusWithFallbackAsync(productId, cookieHeader);
            LoadUniqueSkus(uniqueSkus);
            _openedEditUrl = editUrl;
            _openedProductId = productId;
            _openedShopId = shopId;
            var withWeight = uniqueSkus.Count(g => g.PackageWeightKg is not null);
            var loaded = $"已读取变种 SKU {uniqueSkus.Count} 个（已去重），其中 {withWeight} 个已按货品条码匹配到货品重量。";
            try
            {
                var occupancy = await DescribeOccupancyAsync(
                    uniqueSkus.Select(s => s.PageSku).ToList());
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

            _productWindow?.SetHint("已读取变种 SKU 与货品重量。若上方列出同店铺已占用的 SKU，请先换成建议 SKU 再定价回写。");
        }
        catch (Exception ex)
        {
            SetStatus($"打开产品详情失败：{ex.Message}");
            if (CookieErrorDetector.IsAuthFailure(ex.Message))
                CookieInvalidated?.Invoke(this, ex.Message);
            else
                MessageBox.Show(ex.Message, "打开产品详情失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task<(IReadOnlyList<ChoiceVariantSkuGroup> Skus, string ShopId)> ReadSkusWithFallbackAsync(
        string productId,
        string cookieHeader)
    {
        // 详情接口是唯一能给出售店铺 id 的来源（页面注入只读变种表），所以固定先取一次。
        var detail = await _choiceProduct.GetDetailAsync(productId, cookieHeader);
        var shopId = detail.ShopId;

        try
        {
            if (_productWindow is not null)
            {
                var pageRows = await _productWindow.ReadVariantRowsAsync();
                var grouped = ChoiceProductService.GroupUniqueSkus(pageRows);
                if (grouped.Count > 0)
                    return (grouped, shopId);
            }
        }
        catch
        {
            // 页面尚未就绪时改走接口
        }

        return (detail.UniqueSkus, shopId);
    }

    private async void SearchAndPrice_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        CommitGrid();
        var searchValues = CollectSearchSkus();
        if (searchValues.Count == 0)
        {
            MessageBox.Show("请先打开产品详情读取 SKU，或在「搜索SKU」列填写要查询的 SKU。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (searchValues.Count > ProductSearchService.MaxSearchValuesPerRequest)
        {
            MessageBox.Show($"单次最多搜索 {ProductSearchService.MaxSearchValuesPerRequest} 个 SKU，当前为 {searchValues.Count} 个。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
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
            SetStatus($"正在按 {searchValues.Count} 个搜索 SKU 查询参考价…");
            var outcome = await _productSearch.SearchAsync(searchValues, cookieHeader);
            ApplySearchHits(outcome);
            RecalculateAll();
            MarkMissed(outcome.MissedSkus);
            SetStatus($"搜索并定价完成：请求 {outcome.RequestedValueCount} 个，命中 {outcome.Rows.Count} 条，未命中 {outcome.MissedSkus.Count} 个（重量未改，仍用货品信息）。");
        }
        catch (Exception ex)
        {
            SetStatus($"搜索定价失败：{ex.Message}");
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

    private async void WriteBack_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        CommitGrid();
        var prices = CollectPageSkuPrices();
        if (prices.Count == 0)
        {
            MessageBox.Show("没有可回写的行。请先打开产品详情、完成「搜索并定价」，并确保最终价已算出。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var cookies = GetCookieRecords();
        if (cookies is null || cookies.Count == 0)
        {
            MessageBox.Show("请先在「店小秘搜索」登录并导出 Cookie。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var editUrl = _openedEditUrl;
        if (string.IsNullOrWhiteSpace(editUrl)
            && ChoiceProductService.TryParseProduct(ProductUrlBox.Text, out _, out var parsedUrl))
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
            SetStatus("正在把最终价写入详情页供货价…");
            await OpenProductWindowAsync(new Uri(editUrl), cookies, reload: false);
            var result = await _productWindow!.WritePricesAsync(prices);
            if (!string.IsNullOrWhiteSpace(result.Error) && result.Updated == 0)
            {
                MessageBox.Show(result.Error, "回写失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                SetStatus("回写失败：" + result.Error);
                return;
            }

            var missed = result.MissedPageSkus.Count == 0
                ? ""
                : $"；未匹配 {result.MissedPageSkus.Count} 个页面 SKU";
            var unverified = Math.Max(0, result.Updated - result.Verified);
            var verifyNote = result.Updated > 0 && unverified > 0
                ? $"；其中 {unverified} 条写入后未能回读确认，请人工核对"
                : "";
            var hint = $"已写入 {result.Updated} 条变种的供货价{missed}{verifyNote}。请在店小秘页面确认后点击保存/发布。";
            _productWindow.SetHint(hint);
            SetStatus(hint);
            if (result.MissedPageSkus.Count > 0 || unverified > 0)
            {
                var detail = hint;
                if (result.MissedPageSkus.Count > 0)
                    detail += "\n\n未匹配：" + string.Join("、", result.MissedPageSkus.Take(20));
                MessageBox.Show(
                    detail,
                    "回写完成",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"回写失败：{ex.Message}");
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

    private async Task OpenProductWindowAsync(Uri url, IReadOnlyList<CookieRecord> cookies, bool reload = true)
    {
        var owner = Window.GetWindow(this);
        if (_productWindow is null)
        {
            _productWindow = new Smt7ProductPageWindow();
            _productWindow.Closed += ProductWindow_Closed;
            _productWindow.Show();
        }
        else if (!_productWindow.IsVisible)
        {
            _productWindow.Show();
        }

        if (reload || !_productWindow.IsShowing(url))
            await _productWindow.OpenProductAsync(url, cookies);

        owner?.Activate();
    }

    private void ProductWindow_Closed(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, _productWindow))
            _productWindow = null;
    }

    private void LoadUniqueSkus(IReadOnlyList<ChoiceVariantSkuGroup> uniqueSkus)
    {
        _suppressAutoCalc = true;
        try
        {
            // 「搜索SKU」只去掉末尾通用后缀、保留颜色后缀：店小秘商品库里的货号带颜色
            // （4xJ0202-grey、J0242-purple），去掉颜色就查不到参考价。
            var generalSuffixes = JoomSkuSuffix.Parse(SuffixBox.Text);
            _rows.Clear();
            foreach (var group in uniqueSkus)
            {
                var kg = group.PackageWeightKg;
                _rows.Add(new Smt7Row
                {
                    PageSku = group.PageSku,
                    Sku = JoomSkuSuffix.StripCompositeTrailingSuffix(group.PageSku, generalSuffixes),
                    Weight = kg is null ? null : Math.Round(kg.Value * 1000.0, 4, MidpointRounding.AwayFromZero),
                    ConvertedWeight = kg is null ? null : FormatKg(kg.Value)
                });
            }

            Renumber();
        }
        finally
        {
            _suppressAutoCalc = false;
            UpdateCount();
        }
    }

    private void ApplySearchHits(ProductSearchOutcome outcome)
    {
        var bySku = new Dictionary<string, ProductResultRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var hit in outcome.Rows)
        {
            var key = NormalizeSku(hit.Sku);
            if (key.Length > 0)
                bySku.TryAdd(key, hit);
        }

        foreach (var row in _rows)
        {
            var key = NormalizeSku(row.Sku);
            if (key.Length == 0)
                continue;

            if (!bySku.TryGetValue(key, out var hit))
            {
                row.Cost = null;
                ClearResultKeepWeight(row);
                continue;
            }

            row.Cost = hit.Price.HasValue
                ? (double)Math.Round(hit.Price.Value, 2, MidpointRounding.AwayFromZero)
                : null;
        }
    }

    private void MarkMissed(IReadOnlyList<string> missedSkus)
    {
        var missed = new HashSet<string>(missedSkus.Select(NormalizeSku), StringComparer.OrdinalIgnoreCase);
        foreach (var row in _rows)
        {
            var key = NormalizeSku(row.Sku);
            if (key.Length > 0 && missed.Contains(key) && row.Cost is null)
                row.Status = "未命中";
        }
    }

    private List<string> CollectSearchSkus()
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _rows)
        {
            var sku = NormalizeSku(row.Sku);
            if (sku.Length > 0 && seen.Add(sku))
                result.Add(sku);
        }

        return result;
    }

    private Dictionary<string, string> CollectPageSkuPrices()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _rows)
        {
            var pageSku = NormalizeSku(row.PageSku);
            if (pageSku.Length == 0 || string.IsNullOrWhiteSpace(row.FinalPrice) || row.Status != "OK")
                continue;
            map.TryAdd(pageSku, row.FinalPrice);
        }

        return map;
    }

    private void PersistProductUrl()
    {
        var url = ProductUrlBox.Text?.Trim() ?? "";
        if (url == _settings.ProductEditUrl)
            return;
        _settings.ProductEditUrl = url;
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

    private void Calc_Click(object sender, RoutedEventArgs e) => RecalculateAll();

    private void RestoreParams_Click(object sender, RoutedEventArgs e)
    {
        var defaults = Smt7Settings.CreateDefault();
        _suppressAutoCalc = true;
        try
        {
            ApplyParamsToBoxes(defaults);
        }
        finally
        {
            _suppressAutoCalc = false;
        }

        PersistCalcSettings(defaults);
        RecalculateAll();
        SetStatus("已恢复全托2.0 默认分档参数。");
    }

    private void Param_LostFocus(object sender, RoutedEventArgs e)
    {
        PersistCalcSettings(ReadSettings());
        if (_suppressAutoCalc || AutoCalcCheck.IsChecked != true)
            return;
        RecalculateAll();
    }

    private void AddRow_Click(object sender, RoutedEventArgs e)
    {
        _rows.Add(new Smt7Row { Index = _rows.Count + 1 });
        UpdateCount();
        PricingGrid.SelectedItem = _rows[^1];
        PricingGrid.ScrollIntoView(_rows[^1]);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var selected = PricingGrid.SelectedCells
            .Select(c => c.Item)
            .OfType<Smt7Row>()
            .Distinct()
            .ToList();
        if (selected.Count == 0 && PricingGrid.CurrentItem is Smt7Row current)
            selected.Add(current);

        foreach (var row in selected)
            _rows.Remove(row);

        if (_rows.Count == 0)
            SeedEmptyRows(5);

        Renumber();
        UpdateCount();
        SetStatus($"已删除 {Math.Max(1, selected.Count)} 行");
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows)
        {
            row.Sku = null;
            row.PageSku = null;
            row.Cost = null;
            row.Weight = null;
            ClearResult(row);
            row.Status = "";
        }
        SetStatus("已清空全部输入与结果");
        UpdateCount();
    }

    private async void CopySkuPrice_Click(object sender, RoutedEventArgs e)
    {
        var lines = _rows
            .Where(r => !string.IsNullOrWhiteSpace(r.Sku) && !string.IsNullOrWhiteSpace(r.FinalPrice))
            .ToList();
        if (lines.Count == 0)
        {
            MessageBox.Show("暂无可复制的 SKU 和最终价。请先计算。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var tsv = new StringBuilder(lines.Count * 40);
        foreach (var row in lines)
        {
            var sku = (row.Sku ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
            tsv.Append(sku).Append('\t').Append(row.FinalPrice).Append("\r\n");
        }

        var owner = Window.GetWindow(this);
        try
        {
            await Task.Delay(150);
            if (ClipboardHelper.TrySetText(tsv.ToString(), owner))
            {
                SetStatus($"已复制 {lines.Count} 行 SKU+最终价（可直接粘贴到 Excel）。");
                return;
            }

            var fallback = new CopyFallbackWindow(tsv.ToString()) { Owner = owner };
            fallback.ShowDialog();
            SetStatus(fallback.DialogResult == true
                ? $"已复制 {lines.Count} 行 SKU+最终价。"
                : "已打开手动复制窗口（剪贴板被占用）。");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"复制失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            SetStatus($"复制失败：{ex.Message}");
        }
    }

    private void PricingGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit && e.Row.Item is Smt7Row row)
            SyncWeightColumns(row, e.Column.Header?.ToString(), (e.EditingElement as TextBox)?.Text);

        if (_suppressAutoCalc || AutoCalcCheck.IsChecked != true)
            return;
        Dispatcher.BeginInvoke(RecalculateAll, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// 重量(g) 与换算重量(kg) 双向同步：改克则重算千克，改千克则反推克。
    /// </summary>
    private static void SyncWeightColumns(Smt7Row row, string? header, string? text)
    {
        header ??= "";
        var raw = text?.Trim();
        var empty = string.IsNullOrEmpty(raw);

        if (header == "换算重量(kg)")
        {
            if (empty)
            {
                row.Weight = null;
                row.ConvertedWeight = null;
                return;
            }

            if (!NumberParser.TryParseDouble(raw!, out var kg))
                return;

            row.Weight = Math.Round(kg * 1000.0, 4, MidpointRounding.AwayFromZero);
            row.ConvertedWeight = FormatKg(kg);
            return;
        }

        if (header != "重量(g)")
            return;

        if (empty)
        {
            row.Weight = null;
            row.ConvertedWeight = null;
            return;
        }

        if (!NumberParser.TryParseDouble(raw!, out var grams))
            return;

        row.Weight = grams;
        row.ConvertedWeight = FormatKg(grams / 1000.0);
    }

    private void SeedEmptyRows(int count)
    {
        _rows.Clear();
        for (var i = 0; i < count; i++)
            _rows.Add(new Smt7Row { Index = i + 1 });
        UpdateCount();
    }

    private void Renumber()
    {
        for (var i = 0; i < _rows.Count; i++)
            _rows[i].Index = i + 1;
    }

    private void UpdateCount()
    {
        var valid = _rows.Count(r => r.HasAnyInput);
        var ok = _rows.Count(r => r.Status == "OK");
        CountChanged?.Invoke(this, $"有效 {valid} 行 · 已算 {ok} 行");
    }

    private void CommitGrid()
    {
        PricingGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        PricingGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    private static void ClearResult(Smt7Row row)
    {
        row.ConvertedWeight = null;
        row.Interval = null;
        row.Margin = null;
        row.Factor1 = null;
        row.Constant = null;
        row.Factor2 = null;
        row.RetailPrice = null;
        row.Markup = null;
        row.FinalPrice = null;
    }

    private static void ClearResultKeepWeight(Smt7Row row)
    {
        row.Interval = null;
        row.Margin = null;
        row.Factor1 = null;
        row.Constant = null;
        row.Factor2 = null;
        row.RetailPrice = null;
        row.Markup = null;
        row.FinalPrice = null;
    }

    private void SetStatus(string text) => StatusChanged?.Invoke(this, text);

    private static string NormalizeSku(string? sku) => SkuText.Normalize(sku);

    private static string FormatMoney(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    private static string FormatKg(double v)
    {
        if (Math.Abs(v) < 1e-12)
            return "0";
        return v.ToString("0.####", CultureInfo.InvariantCulture);
    }

    private void BindParamBoxes()
    {
        _costMaxBoxes = [CostMaxBox1, CostMaxBox2, CostMaxBox3, CostMaxBox4, CostMaxBox5];
        _costFactor1Boxes = [CostFactor1Box1, CostFactor1Box2, CostFactor1Box3, CostFactor1Box4, CostFactor1Box5, CostFactor1Box6];
        _costConstBoxes = [CostConstBox1, CostConstBox2, CostConstBox3, CostConstBox4, CostConstBox5, CostConstBox6];
        _costFactor2Boxes = [CostFactor2Box1, CostFactor2Box2, CostFactor2Box3, CostFactor2Box4, CostFactor2Box5, CostFactor2Box6];
        _costMarginBoxes = [CostMarginBox1, CostMarginBox2, CostMarginBox3, CostMarginBox4, CostMarginBox5, CostMarginBox6];
        _weightMaxBoxes = [WeightMaxBox1, WeightMaxBox2, WeightMaxBox3];
        _weightMarkupBoxes = [WeightMarkupBox1, WeightMarkupBox2, WeightMarkupBox3, WeightMarkupBox4];
    }

    private void ApplyParamsToBoxes(Smt7Settings settings)
    {
        var defaults = Smt7Settings.CreateDefault();
        var costTiers = settings.CostTiers.Count == defaults.CostTiers.Count ? settings.CostTiers : defaults.CostTiers;
        var weightTiers = settings.WeightTiers.Count == defaults.WeightTiers.Count ? settings.WeightTiers : defaults.WeightTiers;

        for (var i = 0; i < _costMaxBoxes.Length; i++)
            _costMaxBoxes[i].Text = FormatThreshold(costTiers[i].MaxExclusiveCost ?? defaults.CostTiers[i].MaxExclusiveCost ?? 0);

        for (var i = 0; i < costTiers.Count; i++)
        {
            _costFactor1Boxes[i].Text = FormatParam(costTiers[i].Factor1);
            _costConstBoxes[i].Text = FormatParam(costTiers[i].Constant);
            _costFactor2Boxes[i].Text = FormatParam(costTiers[i].Factor2);
            _costMarginBoxes[i].Text = FormatThreshold(costTiers[i].MarginPercent);
        }

        for (var i = 0; i < _weightMaxBoxes.Length; i++)
            _weightMaxBoxes[i].Text = FormatThreshold(weightTiers[i].MaxExclusiveKg ?? defaults.WeightTiers[i].MaxExclusiveKg ?? 0);

        for (var i = 0; i < weightTiers.Count; i++)
            _weightMarkupBoxes[i].Text = FormatParam(weightTiers[i].Markup);
    }

    private Smt7Settings ReadSettings()
    {
        var defaults = Smt7Settings.CreateDefault();
        var settings = new Smt7Settings();

        for (var i = 0; i < defaults.CostTiers.Count; i++)
        {
            var fallback = defaults.CostTiers[i];
            settings.CostTiers.Add(new Smt7CostTier
            {
                MaxExclusiveCost = i == defaults.CostTiers.Count - 1
                    ? null
                    : ReadDouble(_costMaxBoxes[i], fallback.MaxExclusiveCost ?? 0),
                Factor1 = ReadDouble(_costFactor1Boxes[i], fallback.Factor1),
                Constant = ReadDouble(_costConstBoxes[i], fallback.Constant),
                Factor2 = ReadDouble(_costFactor2Boxes[i], fallback.Factor2),
                MarginPercent = ReadDouble(_costMarginBoxes[i], fallback.MarginPercent)
            });
        }

        for (var i = 0; i < defaults.WeightTiers.Count; i++)
        {
            var fallback = defaults.WeightTiers[i];
            settings.WeightTiers.Add(new Smt7WeightTier
            {
                MaxExclusiveKg = i == defaults.WeightTiers.Count - 1
                    ? null
                    : ReadDouble(_weightMaxBoxes[i], fallback.MaxExclusiveKg ?? 0),
                Markup = ReadDouble(_weightMarkupBoxes[i], fallback.Markup)
            });
        }

        Smt7Calculator.ApplyIntervalNames(settings);
        return settings;
    }

    private void PersistCalcSettings(Smt7Settings settings)
    {
        try
        {
            _settings.ApplyCalcSettings(settings);
            _settings.Save();
        }
        catch
        {
            // 本地保存失败不阻断计算
        }
    }

    private static double ReadDouble(TextBox box, double fallback)
    {
        var raw = box.Text?.Trim().TrimEnd('%').Trim();
        if (string.IsNullOrEmpty(raw))
            return fallback;
        return NumberParser.TryParseDouble(raw, out var v) ? v : fallback;
    }

    private static string FormatParam(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    private static string FormatThreshold(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    // ---------------- SKU 占用检查（按当前店铺） ----------------

    /// <summary>范围内的选项键 ↔ 下拉索引，顺序与 XAML 中 ComboBoxItem 一致。</summary>
    private static readonly string[] ScopeKeys = ["all", "online", "offline", "draft"];

    /// <summary>把保存的范围/店铺选择恢复到下拉框，并准备「自动（当前店铺）」这一项。</summary>
    private void InitializeOccupancySelectors()
    {
        var scopeIndex = Array.IndexOf(ScopeKeys, _settings.OccupancyScope);
        OccupancyScopeCombo.SelectedIndex = scopeIndex < 0 ? 0 : scopeIndex;

        _loadingShopOptions = true;
        try
        {
            _shopOptions.Clear();
            _shopOptions.Add(new ChoiceShopOption { ShopId = "auto", Name = "自动（当前产品店铺）" });
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

    /// <summary>实际用于查重的店铺：下拉选「自动」时跟随当前产品店铺，否则用选中的店铺。</summary>
    private string ResolveOccupancyShopId()
    {
        if (OccupancyShopCombo.SelectedItem is ChoiceShopOption selected)
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
        SaveOccupancySelection();
    }

    private void OccupancyShop_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingShopOptions || !_loaded)
            return;
        if (OccupancyShopCombo.SelectedItem is not ChoiceShopOption option)
            return;
        _settings.OccupancyShopId = option.ShopId;
        SaveOccupancySelection();
    }

    private void SaveOccupancySelection()
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
            SetStatus("正在拉取速卖通全托管店铺列表…");
            var shops = await _choiceProduct.ListShopsAsync(cookieHeader);
            var keep = (_settings.OccupancyShopId ?? "auto").Trim();
            _loadingShopOptions = true;
            try
            {
                _shopOptions.Clear();
                _shopOptions.Add(new ChoiceShopOption { ShopId = "auto", Name = "自动（当前产品店铺）" });
                foreach (var shop in shops)
                    _shopOptions.Add(shop);

                // 列表接口不返回店铺名，所以下拉里显示店铺 id；当前产品所属店铺可能尚未同步到列表，
                // 单独补一项并标注出来，方便固定选它。
                if (!string.IsNullOrWhiteSpace(_openedShopId)
                    && !_shopOptions.Any(s => s.ShopId.Equals(_openedShopId, StringComparison.OrdinalIgnoreCase)))
                {
                    _shopOptions.Add(new ChoiceShopOption
                    {
                        ShopId = _openedShopId!,
                        Name = "当前产品店铺 " + _openedShopId
                    });
                }

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
                SaveOccupancySelection();
            }
            finally
            {
                _loadingShopOptions = false;
            }

            SetStatus($"已拉取 {shops.Count} 个速卖通全托管店铺；查重范围＝{ChoiceProductService.ScopeName(SelectedScopeKey)}。");
        }
        catch (Exception ex)
        {
            SetStatus($"拉取店铺列表失败：{ex.Message}");
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

    /// <summary>
    /// 通用后缀列表（表格下方「SKU 后缀」里的通用后缀）：
    /// 既用于打开详情页时去掉「搜索SKU」的末尾后缀，也用于查重时拼接新 SKU。
    /// </summary>
    private IReadOnlyList<string> OccupancySuffixes => JoomSkuSuffix.Parse(SuffixBox.Text);

    /// <summary>颜色后缀列表（同一处的颜色后缀）。</summary>
    private IReadOnlyList<string> OccupancyColors => JoomSkuSuffix.Parse(ColorSuffixBox.Text);

    private bool UseOccupancyColorSuffix => _settings.OccupancyUseColorSuffix;

    /// <summary>
    /// 刷新各段的颜色选项（含把该段原有的颜色自动选中）。
    /// 「自动选中」会触发 ComboBox 的 SelectionChanged，这里用标志位挡掉，
    /// 否则每个下拉都会各自再触发一次候选查询，和调用方的查询并发。
    /// </summary>
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
        if (sender is not CheckBox box || box.DataContext is not Smt7OccupancyGroup group)
            return;

        group.UseColorSuffix = box.IsChecked == true;
        group.SuggestionUserEdited = false;
        _settings.OccupancyUseColorSuffix = group.UseColorSuffix;
        try
        {
            _settings.Save();
        }
        catch
        {
            // 本地保存失败不阻断操作
        }

        if (group.UseColorSuffix)
            RefreshOccupancyColorOptions();
        await FillSuggestedSkusAsync([group]);
    }

    private async void ColorSuffixCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingColorOptions)
            return;
        if (sender is not ComboBox combo || combo.DataContext is not Smt7SkuPartSlot part || !part.Owner.UseColorSuffix)
            return;

        part.SelectedColor = combo.SelectedItem as string;
        part.Owner.SuggestionUserEdited = false;
        await FillSuggestedSkusAsync([part.Owner]);
    }

    private void SuggestedSku_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is Smt7OccupancyGroup group)
        {
            group.SuggestionUserEdited = true;
            RefreshSuggestedSkuColumn();
        }
    }

    private async void SuggestedSku_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not Smt7OccupancyGroup group || !group.SuggestionUserEdited)
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
            var report = await _choiceProduct.FindOccupanciesAsync(
                [sku], _openedProductId, ResolveOccupancyShopId(), GetCookieHeader(), SelectedScopeKey);
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

    private async void CheckOccupancy_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        CommitGrid();
        var skus = CollectOccupancySkus();
        if (skus.Count == 0)
        {
            MessageBox.Show("请先打开产品详情读取 SKU，或在表格中填写「页面SKU」。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
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
            SetStatus($"SKU 占用检查失败：{ex.Message}");
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

    /// <summary>按「当前店铺」核对 SKU 占用，填充面板并给出可用建议 SKU。</summary>
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

        SetStatus($"正在检查 {skus.Count} 个 SKU 是否已被店铺 {shopId} 的其它速卖通全托管产品占用…");
        var report = await _choiceProduct.FindOccupanciesAsync(
            skus, _openedProductId, shopId, cookieHeader, scopeKey);
        FillDetailProbes(report);

        var groups = new Dictionary<string, Smt7OccupancyGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var hit in report.Hits)
        {
            if (!groups.TryGetValue(hit.Sku, out var group))
            {
                // 去掉末尾查重后缀（组合货号逐段处理、保留颜色），并按「+」拆出各段。
                group = new Smt7OccupancyGroup(
                    hit.Sku,
                    JoomSkuSuffix.StripCompositeTrailingSuffix(hit.Sku, OccupancySuffixes),
                    JoomSkuSuffix.SplitParts(
                        JoomSkuSuffix.StripCompositeTrailingSuffix(hit.Sku, OccupancySuffixes)));
                groups[hit.Sku] = group;
                _occupancies.Add(group);
                if (UseOccupancyColorSuffix)
                    group.UseColorSuffix = true;
            }

            var name = string.IsNullOrWhiteSpace(hit.ProductName) ? "未命名产品 " + hit.ProductId : hit.ProductName;
            var shop = string.IsNullOrWhiteSpace(hit.ShopName) ? "" : "「" + hit.ShopName + "」 · ";
            group.Hits.Add(new Smt7OccupancyHitLine
            {
                Summary = hit.Location + " · " + shop + "「" + name + "」",
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
            var scope = report.Scope + "（采集箱/待发布/在线）";
            string claim;
            if (report.CompletedLocations.Count == 0)
            {
                claim = "本次未能核对任何范围，不能保证无重复。";
            }
            else if (failed.Length > 0 || incomplete.Length > 0)
            {
                claim = "未发现其他占用，但检查不完整，不能保证无重复。";
            }
            else
            {
                claim = "没有发现其他占用。";
            }

            // 列表里一条本店铺的产品都没有时提示一下：可能是该店铺尚未同步，也可能是店铺 id 对不上，
            // 无论哪种都不该让用户以为「查过了、肯定没重复」。
            OccupancyTitle.Text = "";
            OccupancyPanel.Visibility = Visibility.Collapsed;
            var emptyNote = "";
            return "已检查 " + skus.Count + " 个 SKU（" + scope + "），" + claim + emptyNote + failed + incomplete;
        }

        OccupancyTitle.Text = _occupancies.Count + " 个 SKU 已被本店铺产品占用，直接发布可能提示重复";
        OccupancyExpander.IsExpanded = true;
        OccupancyPanel.Visibility = Visibility.Visible;
        _loadingColorOptions = true;
        try
        {
            foreach (var group in _occupancies)
            {
                group.UseColorSuffix = UseOccupancyColorSuffix;
                group.LoadColorOptions(OccupancyColors);
            }
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

    private void FillDetailProbes(ChoiceSkuOccupancyReport report)
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
            SetStatus($"已复制 {_detailProbes.Count} 条详情核对诊断。");
        }
        catch (Exception ex)
        {
            SetStatus($"复制诊断失败：{ex.Message}");
        }
    }

    /// <summary>待查 SKU：优先用「页面SKU」（完整货号），没有才用「搜索SKU」。</summary>
    private List<string> CollectOccupancySkus()
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _rows)
        {
            var sku = NormalizeSku(string.IsNullOrWhiteSpace(row.PageSku) ? row.Sku : row.PageSku);
            if (sku.Length > 0 && seen.Add(sku))
                result.Add(sku);
        }

        return result;
    }

    /// <summary>
    /// 为被占用的 SKU 找可用后缀：去掉末尾通用后缀后按顺序拼后缀，
    /// 逐个用「当前店铺」复核，跳过仍被占用或与本产品其它 SKU 冲突的结果。
    /// </summary>
    private async Task FillSuggestedSkusAsync(IReadOnlyList<Smt7OccupancyGroup> targets)
    {
        if (targets.Count == 0)
            return;

        var generation = _suggestGeneration;
        var general = OccupancySuffixes;
        var pending = new List<Smt7OccupancyGroup>();
        foreach (var group in targets)
        {
            if (general.Count == 0)
            {
                group.SuggestStatus = "请先填写「查重后缀（产品重复时使用）」";
                continue;
            }

            if (group.NeedsColorSelection)
            {
                group.SuggestStatus = "请为每一段选择颜色后缀";
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

            var batch = new List<(Smt7OccupancyGroup Group, string Candidate)>();
            var waiting = new List<Smt7OccupancyGroup>();
            foreach (var group in pending)
            {
                // 每一段各自拼「该段基础货号 + 该段颜色 + 查重后缀」，再按原组合顺序连回一个 SKU。
                var candidate = group.BuildCandidate(general[i]);
                // 原 SKU 已以该后缀结尾时（常见于查重后缀列表为空）再拼一次会得到 E0258-1-1，
                // 这种候选直接跳过，等下一个后缀；拼回原值也没有意义，同样跳过。
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

                var report = await _choiceProduct.FindOccupanciesAsync(
                    batch.Select(item => item.Candidate).ToList(),
                    _openedProductId,
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

            var still = new List<Smt7OccupancyGroup>(waiting);
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
            if (page.Length > 0) reserved.Add(page);
            if (search.Length > 0) reserved.Add(search);
        }

        foreach (var group in _occupancies)
        {
            var suggested = NormalizeSku(group.SuggestedSku);
            if (suggested.Length > 0)
                reserved.Add(suggested);
        }

        return reserved;
    }

    private static string? BuildVerifyGap(ChoiceSkuOccupancyReport report)
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

    /// <summary>把建议 SKU 映射到表格行：页面 SKU 是占用 SKU 的行才给出建议。</summary>
    private void RefreshSuggestedSkuColumn()
    {
        var available = _occupancies
            .Where(g => !string.IsNullOrWhiteSpace(g.SuggestedSku))
            .ToDictionary(g => g.Sku, g => NormalizeSku(g.SuggestedSku), StringComparer.OrdinalIgnoreCase);

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
            await OpenOccupancyWindowAsync(uri, cookies);
            SetStatus("已另开窗口打开占用该 SKU 的产品。");
        }
        catch (Exception ex)
        {
            SetStatus($"打开占用产品失败：{ex.Message}");
            MessageBox.Show(ex.Message, "打开占用产品失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task OpenOccupancyWindowAsync(Uri url, IReadOnlyList<CookieRecord> cookies)
    {
        var window = new Smt7ProductPageWindow();
        _occupancyWindows.Add(window);
        window.Closed += (_, _) => _occupancyWindows.Remove(window);
        window.Show();
        await window.OpenProductAsync(url, cookies);
    }

    private async void ApplySuggestedSkus_Click(object sender, RoutedEventArgs e)
    {
        await ChangePageSkusAsync(restoreOriginal: false);
    }

    private async void RestoreOriginalSkus_Click(object sender, RoutedEventArgs e)
    {
        await ChangePageSkusAsync(restoreOriginal: true);
    }

    /// <summary>把建议 SKU（或恢复原 SKU）写入详情页变种信息。</summary>
    private async Task ChangePageSkusAsync(bool restoreOriginal)
    {
        if (_busy)
            return;

        var changes = new List<(Smt7Row Row, string From, string To)>();
        foreach (var row in _rows)
        {
            var original = NormalizeSku(row.PageSku);
            var applied = NormalizeSku(string.IsNullOrWhiteSpace(row.AppliedPageSku)
                ? row.PageSku
                : row.AppliedPageSku);
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
            && ChoiceProductService.TryParseProduct(ProductUrlBox.Text, out _, out var parsedUrl))
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

            // 写回详情页是不可逆动作：先对没核对过的目标 SKU 复检一次当前店铺。
            if (!restoreOriginal)
            {
                var targets = changes.Select(c => c.To)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var uncheckedTargets = targets.Where(s => !_verifiedSkus.Contains(s)).ToList();
                if (uncheckedTargets.Count > 0)
                {
                    var confirm = await _choiceProduct.FindOccupanciesAsync(
                        uncheckedTargets, _openedProductId, ResolveOccupancyShopId(), GetCookieHeader(), SelectedScopeKey);
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
            var hint = $"已{action} {result.Updated} 条{verifyNote}。请在店小秘页面确认后点击保存/发布。";
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
            SetStatus($"SKU 替换失败：{ex.Message}");
            MessageBox.Show(ex.Message, "SKU 替换失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
        }
    }

    private sealed class Smt7OccupancyHitLine
    {
        public string Summary { get; init; } = "";
        public string EditUrl { get; init; } = "";
    }

    private sealed class Smt7OccupancyGroup : INotifyPropertyChanged
    {
        private bool _useColorSuffix;
        private string _suggestedSku = "";
        private string _suggestStatus = "";

        public Smt7OccupancyGroup(string sku, string baseSku, IEnumerable<string> parts)
        {
            Sku = sku;
            BaseSku = baseSku;
            var index = 1;
            foreach (var part in parts)
                Parts.Add(new Smt7SkuPartSlot(this, index++, part));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Sku { get; }
        public string BaseSku { get; }
        public ObservableCollection<Smt7SkuPartSlot> Parts { get; } = [];
        public ObservableCollection<string> ColorOptions { get; } = [];
        public ObservableCollection<Smt7OccupancyHitLine> Hits { get; } = [];
        public bool SuggestionUserEdited { get; set; }

        public bool HasMultipleParts => Parts.Count > 1;

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
                OnPropertyChanged(nameof(NeedsColorSelection));
            }
        }

        /// <summary>启用了颜色后缀但还有段没选颜色。</summary>
        public bool NeedsColorSelection =>
            UseColorSuffix && Parts.Any(part => string.IsNullOrWhiteSpace(part.SelectedColor));

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

        /// <summary>刷新颜色选项：所有段共用同一份可选颜色，并清掉已不在列表里的选择。</summary>
        public void LoadColorOptions(IReadOnlyList<string> colors)
        {
            ColorOptions.Clear();
            foreach (var color in colors)
                ColorOptions.Add(color);

            foreach (var part in Parts)
            {
                if (part.SelectedColor is { } current && !colors.Contains(current, StringComparer.OrdinalIgnoreCase))
                {
                    part.SelectedColor = null;
                    current = null;
                }

                // 该段原本就带颜色（如 -grey、-purple）且在可选列表里的话，默认选中它，
                // 用户一启用颜色后缀就能直接看到建议，不必每段手动选一遍。
                if (part.SelectedColor is null && part.OriginalColorSuffix.Length > 0
                    && colors.Contains(part.OriginalColorSuffix, StringComparer.OrdinalIgnoreCase))
                {
                    part.SelectedColor = part.OriginalColorSuffix;
                }
            }

            OnPropertyChanged(nameof(NeedsColorSelection));
        }

        /// <summary>
        /// 按段拼出候选 SKU：每段 = 该段基础货号 + 该段颜色后缀 + 查重后缀，
        /// 拼好后按原组合顺序用「+」连回一个完整 SKU。
        /// </summary>
        public string BuildCandidate(string occupancySuffix)
        {
            return string.Join("+", Parts.Select(part =>
                JoomSkuSuffix.Combine(
                    part.BaseSku,
                    UseColorSuffix ? part.SelectedColor : null,
                    occupancySuffix)));
        }

        public void NotifyPartsChanged() => OnPropertyChanged(nameof(NeedsColorSelection));

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

    /// <summary>组合 SKU 的一段：各自选颜色后缀，最后按原顺序拼回一个完整 SKU。</summary>
    private sealed class Smt7SkuPartSlot : INotifyPropertyChanged
    {
        private string? _selectedColor;

        public Smt7SkuPartSlot(Smt7OccupancyGroup owner, int index, string sku)
        {
            Owner = owner;
            Index = index;
            Sku = sku;
            BaseSku = JoomSkuSuffix.StripBase(sku);
            // 该段原本带的颜色后缀（可能是 -grey、-purple 等不在默认列表里的值）。
            OriginalColorSuffix = sku.Length > BaseSku.Length ? sku[BaseSku.Length..] : "";
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public Smt7OccupancyGroup Owner { get; }
        public int Index { get; }
        public string Sku { get; }
        public string BaseSku { get; }
        public string OriginalColorSuffix { get; }
        public string Label => "第" + Index + "段 " + Sku;

        public string? SelectedColor
        {
            get => _selectedColor;
            set
            {
                if (string.Equals(_selectedColor, value, StringComparison.Ordinal))
                    return;
                _selectedColor = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedColor)));
                Owner.NotifyPartsChanged();
            }
        }
    }
}
