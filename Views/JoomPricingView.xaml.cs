/*
 * 功能说明：JOOM 批量定价页，对齐计算表 JOOM1.0。
 * 主要职责：承接店小秘命中行的 SKU/成本，按成本区间计算美元售价与人民币利润；
 *           也可打开 JOOM 产品详情页读取变种 SKU，搜索定价后回写 MSRP/价格。
 * 创建日期：2026-09-05
 * 更新日期：2026-09-12 打开详情页后回到工作台，避免弹窗盖住定价页
 * 修改记录：2026-09-28 打开产品后检查变种 SKU 是否已被其他 JOOM 产品占用，并给出编辑链接
 *           2026-09-28 「打开该产品」改用店小秘详情窗口，不再调用系统浏览器
 *           2026-09-28 「打开该产品」另开窗口，不覆盖正在定价的详情页
 *           2026-09-28 分档参数默认折叠，产品详情与产品重复可折叠
 *           2026-09-28 产品重复提示改为写出占用店铺名称
 *           2026-09-28 SKU 占用改为检查全部店铺
 *           2026-09-29 产品重复时按通用/颜色后缀生成可修改的新 SKU
 *           2026-09-29 内容超出窗口时整页可滚动，定价表仍占用剩余高度
 */

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using EcommerceWorkbench.Models;
using EcommerceWorkbench.Services;
using EcommerceWorkbench.Services.Dianxiaomi;
using EcommerceWorkbench.Services.Joom;

namespace EcommerceWorkbench.Views;

public partial class JoomPricingView : UserControl
{
    private readonly ObservableCollection<JoomRow> _rows = [];
    private readonly ApiClient _apiClient = new();
    private readonly JoomProductService _joomProduct;
    private readonly ProductSearchService _productSearch;
    private readonly CookieStore _cookieStore = new();
    private readonly JoomUserSettings _settings = JoomUserSettings.Load();
    private bool _suppressAutoCalc;
    private bool _loaded;
    private bool _busy;
    private JoomProductPageWindow? _productWindow;
    private readonly List<JoomProductPageWindow> _occupancyWindows = [];
    private int _occupancyWindowSeq;
    private string? _openedEditUrl;
    private string? _openedProductId;
    private readonly ObservableCollection<OccupancySkuGroup> _occupancies = [];
    private readonly SemaphoreSlim _suggestLock = new(1, 1);
    private readonly Dictionary<string, int> _suggestTokens = new(StringComparer.OrdinalIgnoreCase);
    private int _suggestGeneration;
    private bool _suppressSuggestion;

    public event EventHandler<string>? StatusChanged;
    public event EventHandler<string>? CountChanged;
    public Func<string?>? CookieHeaderProvider { get; set; }
    public Func<IReadOnlyList<CookieRecord>?>? CookieRecordsProvider { get; set; }
    public event EventHandler<string>? CookieInvalidated;

    public JoomPricingView()
    {
        InitializeComponent();
        _joomProduct = new JoomProductService(_apiClient);
        _productSearch = new ProductSearchService(_apiClient);
        PricingGrid.ItemsSource = _rows;
        OccupancyList.ItemsSource = _occupancies;
        Loaded += JoomPricingView_Loaded;
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

        foreach (var window in _occupancyWindows.ToArray())
        {
            try
            {
                window.Close();
            }
            catch
            {
                // 关闭占用产品窗口失败不影响退出
            }
        }

        _productWindow = null;
        _occupancyWindows.Clear();
        _apiClient.Dispose();
    }

    private void JoomPricingView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
            return;
        _loaded = true;
        if (!string.IsNullOrWhiteSpace(_settings.ProductEditUrl))
            ProductUrlBox.Text = _settings.ProductEditUrl;
        GeneralSuffixBox.Text = _settings.GeneralSkuSuffixes ?? JoomUserSettings.DefaultGeneralSkuSuffixes;
        ColorSuffixBox.Text = _settings.ColorSkuSuffixes ?? JoomUserSettings.DefaultColorSkuSuffixes;
        SeedEmptyRows(12);
        Dispatcher.BeginInvoke(UpdatePageLayout, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void JoomPricingView_SizeChanged(object sender, SizeChangedEventArgs e) => UpdatePageLayout();

    private void TopPanel_SizeChanged(object sender, SizeChangedEventArgs e) => UpdatePageLayout();

    /// <summary>
    /// 上方内容增高时压低定价表，但至少留出一块可滚动的表格；仍放不下时整页出现滚动条。
    /// </summary>
    private void UpdatePageLayout()
    {
        if (!IsLoaded || PageScroll is null || TopPanel is null || FooterHint is null || GridHost is null)
            return;

        var viewport = PageScroll.ActualHeight;
        if (viewport <= 1 || double.IsNaN(viewport))
            return;

        var footer = FooterHint.ActualHeight + FooterHint.Margin.Top + FooterHint.Margin.Bottom;
        var gridChrome = GridHost.Margin.Top + GridHost.Margin.Bottom;
        var gridHeight = viewport - TopPanel.ActualHeight - footer - gridChrome - 4;
        if (gridHeight < 220)
            gridHeight = 220;

        if (!double.IsNaN(PricingGrid.Height) && Math.Abs(PricingGrid.Height - gridHeight) < 1)
            return;

        PricingGrid.Height = gridHeight;
    }

    /// <summary>
    /// 将店小秘命中行导入 JOOM 表：按 SKU 覆盖成本，新 SKU 追加，去掉空行后立即计算。
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

            var bySku = new Dictionary<string, JoomRow>(StringComparer.OrdinalIgnoreCase);
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
                    var row = new JoomRow
                    {
                        Sku = hit.Sku.Trim(),
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
            SetStatus($"已导入 {imported} 条到 JOOM 并完成计算。");
        }

        return imported;
    }

    public void RecalculateAll()
    {
        CommitGrid();
        var settings = ReadSettings();
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
                ClearResult(row);
                row.Status = "请填写成本";
                errCount++;
                continue;
            }

            try
            {
                var result = JoomCalculator.Calculate(row.Cost.Value, settings);
                row.Interval = result.Interval;
                row.Commission = FormatPercent(result.Commission);
                row.Margin = FormatPercent(result.Margin);
                row.PriceUsd = FormatMoney(result.PriceUsd);
                row.ProfitCny = FormatMoney(result.ProfitCny);
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
        SetStatus($"JOOM 计算完成：成功 {okCount} · 跳过 {skipCount} · 失败 {errCount}");
    }

    public void RefreshCount() => UpdateCount();

    private void ProductUrlBox_LostFocus(object sender, RoutedEventArgs e) => PersistProductUrl();

    private async void OpenProduct_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        CommitGrid();
        PersistProductUrl();
        if (!JoomProductService.TryParseProduct(ProductUrlBox.Text, out var productId, out var editUrl))
        {
            MessageBox.Show(
                "请填写店小秘 JOOM 产品详情链接，例如：\nhttps://www.dianxiaomi.com/web/joomProduct/edit?id=184807701445417881",
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
            SetStatus("正在打开 JOOM 产品详情页并读取变种 SKU…");
            await OpenProductWindowAsync(new Uri(editUrl), cookies);

            var detail = await _joomProduct.GetDetailAsync(productId, cookieHeader);
            LoadUniqueSkus(detail);
            _openedEditUrl = editUrl;
            _openedProductId = detail.Id;
            var name = string.IsNullOrWhiteSpace(detail.Name) ? "" : "「" + detail.Name + "」";
            var loaded = $"已读取{name}变种 SKU {detail.UniqueSkus.Count} 个（已去重）。";
            try
            {
                var occupancy = await DescribeOccupancyAsync(detail.UniqueSkus.Select(s => s.PageSku).ToList());
                SetStatus(loaded + occupancy);
            }
            catch (Exception occupancyEx)
            {
                ClearOccupancy();
                SetStatus(loaded + "占用检查失败：" + occupancyEx.Message);
                if (IsCookieInvalidError(occupancyEx.Message))
                    CookieInvalidated?.Invoke(this, occupancyEx.Message);
            }

            _productWindow?.SetHint("已读取变种 SKU。在工作台编辑搜索 SKU 并定价后，点「回写MSRP和价格」。若上方列出 SKU 占用，请先处理再发布。");
        }
        catch (Exception ex)
        {
            SetStatus($"打开产品详情失败：{ex.Message}");
            if (IsCookieInvalidError(ex.Message))
                CookieInvalidated?.Invoke(this, ex.Message);
            else
                MessageBox.Show(ex.Message, "打开产品详情失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
        }
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
            SetStatus($"搜索并定价完成：请求 {outcome.RequestedValueCount} 个，命中 {outcome.Rows.Count} 条，未命中 {outcome.MissedSkus.Count} 个。");
        }
        catch (Exception ex)
        {
            SetStatus($"搜索定价失败：{ex.Message}");
            if (IsCookieInvalidError(ex.Message))
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
            MessageBox.Show("没有可回写的行。请先打开产品详情、完成「搜索并定价」，并确保售价已算出。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
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
            && JoomProductService.TryParseProduct(ProductUrlBox.Text, out _, out var parsedUrl))
        {
            editUrl = parsedUrl;
        }

        if (string.IsNullOrWhiteSpace(editUrl))
        {
            MessageBox.Show("请先打开 JOOM 产品详情页。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _busy = true;
        try
        {
            SetStatus("正在把售价写入详情页 MSRP 和价格…");
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
            var hint = $"已写入 {result.Updated} 条变种的 MSRP 和价格{missed}。请在店小秘页面确认后点击保存/发布。";
            _productWindow.SetHint(hint);
            SetStatus(hint);
            if (result.MissedPageSkus.Count > 0)
            {
                MessageBox.Show(
                    hint + "\n\n未匹配：" + string.Join("、", result.MissedPageSkus.Take(20)),
                    "回写完成",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"回写失败：{ex.Message}");
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
            // 不设 Owner：否则详情窗会一直压在工作台上面，挡住定价页
            _productWindow = new JoomProductPageWindow();
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

    private async Task OpenOccupancyWindowAsync(Uri url, IReadOnlyList<CookieRecord> cookies)
    {
        var existing = _occupancyWindows.FirstOrDefault(w => w.IsShowing(url));
        if (existing is not null)
        {
            if (existing.WindowState == WindowState.Minimized)
                existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        var window = new JoomProductPageWindow
        {
            Title = "JOOM 占用产品",
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        var shift = (_occupancyWindowSeq % 8) * 32;
        _occupancyWindowSeq++;
        var area = SystemParameters.WorkArea;
        window.Left = area.Left + 48 + shift;
        window.Top = area.Top + 48 + shift;
        window.Closed += OccupancyWindow_Closed;
        _occupancyWindows.Add(window);
        window.Show();
        await window.OpenProductAsync(url, cookies);
        window.SetHint("这是占用 SKU 的产品。确认页面内容后，可在店小秘里修改并保存。");
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private void OccupancyWindow_Closed(object? sender, EventArgs e)
    {
        if (sender is JoomProductPageWindow window)
            _occupancyWindows.Remove(window);
    }

    private void ProductWindow_Closed(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, _productWindow))
            _productWindow = null;
    }

    private void LoadUniqueSkus(JoomProductDetail detail)
    {
        _suppressAutoCalc = true;
        try
        {
            _rows.Clear();
            foreach (var group in detail.UniqueSkus)
            {
                _rows.Add(new JoomRow
                {
                    PageSku = group.PageSku,
                    Sku = group.PageSku
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
                row.Weight = null;
                ClearResult(row);
                continue;
            }

            row.Cost = hit.Price.HasValue
                ? (double)Math.Round(hit.Price.Value, 2, MidpointRounding.AwayFromZero)
                : null;
            row.Weight = hit.Weight.HasValue ? (double)hit.Weight.Value : null;
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
            if (pageSku.Length == 0 || string.IsNullOrWhiteSpace(row.PriceUsd) || row.Status != "OK")
                continue;
            map.TryAdd(pageSku, row.PriceUsd);
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

    private static bool IsCookieInvalidError(string message)
    {
        return message.Contains("验证失败", StringComparison.OrdinalIgnoreCase)
               || message.Contains("code=2001", StringComparison.OrdinalIgnoreCase)
               || message.Contains("未登录", StringComparison.OrdinalIgnoreCase)
               || message.Contains("登录", StringComparison.OrdinalIgnoreCase) && message.Contains("失效", StringComparison.OrdinalIgnoreCase);
    }

    private void Calc_Click(object sender, RoutedEventArgs e) => RecalculateAll();

    private void AddRow_Click(object sender, RoutedEventArgs e)
    {
        _rows.Add(new JoomRow { Index = _rows.Count + 1 });
        UpdateCount();
        PricingGrid.SelectedItem = _rows[^1];
        PricingGrid.ScrollIntoView(_rows[^1]);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var selected = PricingGrid.SelectedCells
            .Select(c => c.Item)
            .OfType<JoomRow>()
            .Distinct()
            .ToList();
        if (selected.Count == 0 && PricingGrid.CurrentItem is JoomRow current)
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
        ClearOccupancy();
        SetStatus("已清空全部输入与结果");
        UpdateCount();
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
            var occupancy = await DescribeOccupancyAsync(skus);
            SetStatus(occupancy);
        }
        catch (Exception ex)
        {
            ClearOccupancy();
            SetStatus($"SKU 占用检查失败：{ex.Message}");
            if (IsCookieInvalidError(ex.Message))
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

        SetStatus($"正在检查 {skus.Count} 个 SKU 是否已被各店铺的 JOOM 产品占用…");
        var report = await _joomProduct.FindOccupanciesAsync(skus, _openedProductId, cookieHeader);
        var groups = new Dictionary<string, OccupancySkuGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var hit in report.Hits)
        {
            var name = string.IsNullOrWhiteSpace(hit.ProductName) ? "未命名产品 " + hit.ProductId : hit.ProductName;
            var shop = string.IsNullOrWhiteSpace(hit.ShopName) ? "" : "「" + hit.ShopName + "」 · ";
            var parent = string.IsNullOrWhiteSpace(hit.ParentSku) ? "" : " · Parent SKU " + hit.ParentSku;
            if (!groups.TryGetValue(hit.Sku, out var group))
            {
                group = new OccupancySkuGroup
                {
                    Sku = hit.Sku,
                    BaseSku = JoomSkuSuffix.StripBase(hit.Sku)
                };
                groups[hit.Sku] = group;
                _occupancies.Add(group);
            }

            group.Hits.Add(new OccupancyHitLine
            {
                Summary = hit.Sku + " · " + shop + hit.Location + " · 「" + name + "」" + parent,
                EditUrl = hit.EditUrl
            });
        }

        if (_occupancies.Count > 0)
        {
            var shopNames = report.Hits
                .Select(h => h.ShopName)
                .Where(n => n.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var occupiedBy = shopNames.Count == 0
                ? "当前店铺"
                : string.Join("、", shopNames.Select(n => "「" + n + "」"));
            OccupancyTitle.Text = _occupancies.Count + " 个 SKU 已被" + occupiedBy + "店铺占用，直接发布会提示「产品重复」";
            OccupancyExpander.IsExpanded = true;
            OccupancyPanel.Visibility = Visibility.Visible;
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

                if (IsCookieInvalidError(ex.Message))
                    throw;
            }
        }

        var failed = report.FailedLocations.Count == 0
            ? ""
            : " 以下范围未完整核对：" + string.Join("、", report.FailedLocations) + "。";
        if (_occupancies.Count == 0)
            return "已检查 " + skus.Count + " 个 SKU，各店铺的采集箱、待发布、在线产品中没有其他占用。" + failed;

        var suggested = _occupancies.Count(g => !string.IsNullOrWhiteSpace(g.SuggestedSku));
        var suggestionNote = suggested == _occupancies.Count
            ? " 已在每个重复 SKU 后面填入未占用的新 SKU，可以直接修改。"
            : " 已填入 " + suggested + " 个未占用的新 SKU。未填入的可以启用颜色后缀、补充通用后缀，或手动填写。";
        return "发现 " + report.Hits.Count + " 处 SKU 占用，发布前请先更换 SKU，或处理下面列出的产品。" + suggestionNote + failed;
    }

    private void ClearOccupancy()
    {
        _suggestGeneration++;
        _suggestTokens.Clear();
        _occupancies.Clear();
        OccupancyTitle.Text = "";
        OccupancyPanel.Visibility = Visibility.Collapsed;
    }

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

    private async void SuffixBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!PersistSuffixes())
            return;
        if (_suppressSuggestion || OccupancyPanel.Visibility != Visibility.Visible || _occupancies.Count == 0)
            return;

        foreach (var group in _occupancies)
        {
            group.SuggestionUserEdited = false;
            if (group.UseColorSuffix)
                ReloadColorOptions(group);
        }

        await RunSuggestionAsync(_occupancies.ToList());
    }

    private async void UseColorSuffix_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressSuggestion)
            return;
        if (sender is not CheckBox box || box.DataContext is not OccupancySkuGroup group)
            return;

        group.UseColorSuffix = box.IsChecked == true;
        group.SuggestionUserEdited = false;
        if (group.UseColorSuffix)
            ReloadColorOptions(group);
        await RunSuggestionAsync([group]);
    }

    private async void ColorSuffixCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSuggestion)
            return;
        if (sender is not ComboBox combo || combo.DataContext is not OccupancySkuGroup group || !group.UseColorSuffix)
            return;

        group.SelectedColor = combo.SelectedItem as string;
        group.SuggestionUserEdited = false;
        await RunSuggestionAsync([group]);
    }

    private void SuggestedSku_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressSuggestion)
            return;
        if (sender is FrameworkElement element && element.DataContext is OccupancySkuGroup group)
            group.SuggestionUserEdited = true;
    }

    private async void SuggestedSku_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressSuggestion)
            return;
        if (sender is not TextBox box || box.DataContext is not OccupancySkuGroup group || !group.SuggestionUserEdited)
            return;

        var sku = NormalizeSku(box.Text);
        var token = _suggestTokens.GetValueOrDefault(group.Sku);
        var generation = _suggestGeneration;
        group.SuggestedSku = sku;
        if (sku.Length == 0)
        {
            group.SuggestStatus = "请填写建议 SKU";
            return;
        }

        var conflict = LocalConflict(group, sku);
        if (conflict is not null)
        {
            group.SuggestStatus = conflict;
            return;
        }

        try
        {
            group.SuggestStatus = "正在确认修改后的 SKU 是否重复…";
            var report = await QueryOccupancyAsync([sku], generation);
            if (generation != _suggestGeneration || token != _suggestTokens.GetValueOrDefault(group.Sku))
                return;
            if (!NormalizeSku(group.SuggestedSku).Equals(sku, StringComparison.OrdinalIgnoreCase))
                return;

            var occupied = report.Hits.Any(hit => hit.Sku.Equals(sku, StringComparison.OrdinalIgnoreCase));
            group.SuggestStatus = occupied
                ? "修改后的 SKU 仍会产品重复"
                : report.FailedLocations.Count == 0
                    ? "修改后的 SKU 未被占用"
                    : "修改后的 SKU 在已核对范围未被占用";
        }
        catch (Exception ex)
        {
            group.SuggestStatus = "确认失败：" + ex.Message;
            SetStatus("确认建议 SKU 失败：" + ex.Message);
            if (IsCookieInvalidError(ex.Message))
                CookieInvalidated?.Invoke(this, ex.Message);
        }
    }

    private void ReloadColorOptions(OccupancySkuGroup group)
    {
        var colors = JoomSkuSuffix.Parse(ColorSuffixBox.Text);
        var keep = group.SelectedColor;
        _suppressSuggestion = true;
        try
        {
            group.ColorOptions.Clear();
            foreach (var color in colors)
                group.ColorOptions.Add(color);
            group.SelectedColor = keep is not null && colors.Contains(keep, StringComparer.OrdinalIgnoreCase) ? keep : null;
        }
        finally
        {
            _suppressSuggestion = false;
        }
    }

    private async Task RunSuggestionAsync(IReadOnlyList<OccupancySkuGroup> groups)
    {
        try
        {
            await FillSuggestedSkusAsync(groups);
        }
        catch (Exception ex)
        {
            foreach (var group in groups)
            {
                if (string.IsNullOrWhiteSpace(group.SuggestedSku))
                    group.SuggestStatus = "查找可用后缀失败：" + ex.Message;
            }

            SetStatus("查找可用后缀失败：" + ex.Message);
            if (IsCookieInvalidError(ex.Message))
                CookieInvalidated?.Invoke(this, ex.Message);
        }
    }

    /// <summary>
    /// 去掉原 SKU 的「-」后缀后，按通用后缀从左到右拼接。启用颜色时先接所选颜色。
    /// 同一轮候选一起查询，跳过仍被占用或与当前产品其他 SKU 冲突的结果。
    /// </summary>
    private async Task FillSuggestedSkusAsync(IReadOnlyList<OccupancySkuGroup> targets)
    {
        if (targets.Count == 0)
            return;

        var tokens = new Dictionary<OccupancySkuGroup, int>();
        foreach (var group in targets)
        {
            var next = _suggestTokens.GetValueOrDefault(group.Sku) + 1;
            _suggestTokens[group.Sku] = next;
            tokens[group] = next;
        }

        var generation = _suggestGeneration;
        await _suggestLock.WaitAsync();
        try
        {
            if (generation != _suggestGeneration)
                return;

            var general = JoomSkuSuffix.Parse(GeneralSuffixBox.Text);
            var pending = new List<OccupancySkuGroup>();
            foreach (var group in targets)
            {
                if (!IsSuggestionCurrent(group, tokens, generation) || group.SuggestionUserEdited)
                    continue;
                if (group.UseColorSuffix && string.IsNullOrWhiteSpace(group.SelectedColor))
                {
                    SetSuggestion(group, "", "请选择颜色后缀");
                    continue;
                }

                if (general.Count == 0)
                {
                    SetSuggestion(group, "", "请先填写 SKU 的通用后缀");
                    continue;
                }

                SetSuggestion(group, "", "正在查找未重复的后缀…");
                pending.Add(group);
            }

            var reserved = CollectReservedSkus(pending);
            for (var i = 0; i < general.Count && pending.Count > 0; i++)
            {
                pending = pending.Where(group => IsSuggestionCurrent(group, tokens, generation)).ToList();
                if (pending.Count == 0 || generation != _suggestGeneration)
                    return;

                var batch = new List<(OccupancySkuGroup Group, string Candidate)>();
                var waiting = new List<OccupancySkuGroup>();
                foreach (var group in pending)
                {
                    if (group.SuggestionUserEdited)
                    {
                        if (group.SuggestStatus.StartsWith("正在查找", StringComparison.Ordinal))
                            group.SuggestStatus = "已手动修改";
                        continue;
                    }

                    var color = group.UseColorSuffix ? group.SelectedColor : null;
                    var candidate = JoomSkuSuffix.Combine(group.BaseSku, color, general[i]);
                    if (candidate.Length == 0 || candidate.Contains(',') || !reserved.Add(candidate))
                    {
                        waiting.Add(group);
                        continue;
                    }

                    batch.Add((group, candidate));
                }

                var occupied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (batch.Count > 0)
                {
                    SetStatus("正在检查 " + batch.Count + " 个候选 SKU 是否仍会产品重复…");
                    var cookieHeader = GetCookieHeader();
                    if (string.IsNullOrWhiteSpace(cookieHeader))
                        throw new InvalidOperationException("请先导出 Cookie。");

                    var report = await _joomProduct.FindOccupanciesAsync(
                        batch.Select(item => item.Candidate).ToList(),
                        _openedProductId,
                        cookieHeader);
                    if (generation != _suggestGeneration)
                        return;

                    foreach (var hit in report.Hits)
                        occupied.Add(hit.Sku);
                }

                var still = new List<OccupancySkuGroup>(waiting);
                foreach (var (group, candidate) in batch)
                {
                    if (!IsSuggestionCurrent(group, tokens, generation))
                    {
                        reserved.Remove(candidate);
                        continue;
                    }

                    if (group.SuggestionUserEdited)
                    {
                        if (!NormalizeSku(group.SuggestedSku).Equals(candidate, StringComparison.OrdinalIgnoreCase))
                            reserved.Remove(candidate);
                        continue;
                    }

                    if (occupied.Contains(candidate))
                    {
                        reserved.Remove(candidate);
                        still.Add(group);
                        continue;
                    }

                    SetSuggestion(group, candidate, "该后缀未被占用");
                }

                pending = still;
            }

            foreach (var group in pending)
            {
                if (!IsSuggestionCurrent(group, tokens, generation) || group.SuggestionUserEdited)
                    continue;
                SetSuggestion(group, "", "这些后缀都会产品重复，请改后缀或手动填写");
            }
        }
        finally
        {
            _suggestLock.Release();
        }
    }

    private async Task<JoomSkuOccupancyReport> QueryOccupancyAsync(IReadOnlyList<string> skus, int generation)
    {
        await _suggestLock.WaitAsync();
        try
        {
            if (generation != _suggestGeneration)
                return new JoomSkuOccupancyReport();

            var cookieHeader = GetCookieHeader();
            if (string.IsNullOrWhiteSpace(cookieHeader))
                throw new InvalidOperationException("请先导出 Cookie。");

            return await _joomProduct.FindOccupanciesAsync(skus, _openedProductId, cookieHeader);
        }
        finally
        {
            _suggestLock.Release();
        }
    }

    private bool IsSuggestionCurrent(OccupancySkuGroup group, IReadOnlyDictionary<OccupancySkuGroup, int> tokens, int generation)
    {
        return generation == _suggestGeneration
               && tokens.TryGetValue(group, out var token)
               && token == _suggestTokens.GetValueOrDefault(group.Sku);
    }

    private HashSet<string> CollectReservedSkus(IReadOnlyCollection<OccupancySkuGroup> pending)
    {
        var pendingSkus = pending.Select(group => group.Sku).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _rows)
        {
            var page = NormalizeSku(row.PageSku);
            var search = NormalizeSku(row.Sku);
            if (page.Length > 0)
                reserved.Add(page);
            if (search.Length > 0)
                reserved.Add(search);
        }

        foreach (var group in _occupancies)
        {
            if (pendingSkus.Contains(group.Sku))
                continue;
            var suggested = NormalizeSku(group.SuggestedSku);
            if (suggested.Length > 0)
                reserved.Add(suggested);
        }

        return reserved;
    }

    private string? LocalConflict(OccupancySkuGroup group, string sku)
    {
        foreach (var row in _rows)
        {
            var page = NormalizeSku(string.IsNullOrWhiteSpace(row.PageSku) ? row.Sku : row.PageSku);
            if (page.Length > 0 && page.Equals(sku, StringComparison.OrdinalIgnoreCase))
                return "与当前产品里的 SKU 相同";
        }

        foreach (var other in _occupancies)
        {
            if (ReferenceEquals(other, group))
                continue;
            var suggested = NormalizeSku(other.SuggestedSku);
            if (suggested.Length > 0 && suggested.Equals(sku, StringComparison.OrdinalIgnoreCase))
                return "与另一条建议 SKU 相同";
        }

        return null;
    }

    private void SetSuggestion(OccupancySkuGroup group, string sku, string status)
    {
        _suppressSuggestion = true;
        try
        {
            group.SuggestedSku = sku;
            group.SuggestStatus = status;
        }
        finally
        {
            _suppressSuggestion = false;
        }
    }

    private bool PersistSuffixes()
    {
        var general = GeneralSuffixBox.Text ?? "";
        var color = ColorSuffixBox.Text ?? "";
        if (general == _settings.GeneralSkuSuffixes && color == _settings.ColorSkuSuffixes)
            return false;

        _settings.GeneralSkuSuffixes = general;
        _settings.ColorSkuSuffixes = color;
        try
        {
            _settings.Save();
        }
        catch
        {
            // 本地保存失败不阻断操作
        }

        return true;
    }

    private sealed class OccupancyHitLine
    {
        public string Summary { get; init; } = "";
        public string EditUrl { get; init; } = "";
    }

    private sealed class OccupancySkuGroup : INotifyPropertyChanged
    {
        private bool _useColorSuffix;
        private string? _selectedColor;
        private string _suggestedSku = "";
        private string _suggestStatus = "";

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Sku { get; init; } = "";
        public string BaseSku { get; init; } = "";
        public ObservableCollection<string> ColorOptions { get; } = [];
        public ObservableCollection<OccupancyHitLine> Hits { get; } = [];
        public bool SuggestionUserEdited { get; set; }

        public string BaseHint =>
            string.Equals(BaseSku, Sku, StringComparison.Ordinal)
                ? "该 SKU 没有「-」后缀，将直接在末尾拼接。"
                : "将去掉「-」及其后面的内容，按 " + BaseSku + " 再拼接后缀。";

        public bool UseColorSuffix
        {
            get => _useColorSuffix;
            set => Set(ref _useColorSuffix, value);
        }

        public string? SelectedColor
        {
            get => _selectedColor;
            set => Set(ref _selectedColor, value);
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

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value))
                return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    private async void CopySkuPrice_Click(object sender, RoutedEventArgs e)
    {
        var lines = _rows
            .Where(r => !string.IsNullOrWhiteSpace(r.Sku) && !string.IsNullOrWhiteSpace(r.PriceUsd))
            .ToList();
        if (lines.Count == 0)
        {
            MessageBox.Show("暂无可复制的 SKU 和售价。请先计算。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var tsv = new StringBuilder(lines.Count * 40);
        foreach (var row in lines)
        {
            var sku = (row.Sku ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
            tsv.Append(sku).Append('\t').Append(row.PriceUsd).Append("\r\n");
        }

        var owner = Window.GetWindow(this);
        try
        {
            await Task.Delay(150);
            if (ClipboardHelper.TrySetText(tsv.ToString(), owner))
            {
                SetStatus($"已复制 {lines.Count} 行 SKU+售价（可直接粘贴到 Excel）。");
                return;
            }

            var fallback = new CopyFallbackWindow(tsv.ToString()) { Owner = owner };
            fallback.ShowDialog();
            SetStatus(fallback.DialogResult == true
                ? $"已复制 {lines.Count} 行 SKU+售价。"
                : "已打开手动复制窗口（剪贴板被占用）。");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"复制失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            SetStatus($"复制失败：{ex.Message}");
        }
    }

    private void Param_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressAutoCalc || AutoCalcCheck.IsChecked != true)
            return;
        RecalculateAll();
    }

    private void PricingGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (_suppressAutoCalc || AutoCalcCheck.IsChecked != true)
            return;
        Dispatcher.BeginInvoke(RecalculateAll, System.Windows.Threading.DispatcherPriority.Background);
    }

    private JoomSettings ReadSettings()
    {
        return new JoomSettings
        {
            ExchangeRate = ReadDouble(RateBox, JoomSettings.DefaultExchangeRate),
            Low = new JoomTierParams
            {
                Commission = ReadPercent(LowCommissionBox, 15),
                Margin = ReadPercent(LowMarginBox, 50)
            },
            Mid = new JoomTierParams
            {
                Commission = ReadPercent(MidCommissionBox, 15),
                Margin = ReadPercent(MidMarginBox, 45)
            },
            High = new JoomTierParams
            {
                Commission = ReadPercent(HighCommissionBox, 15),
                Margin = ReadPercent(HighMarginBox, 40)
            }
        };
    }

    private void SeedEmptyRows(int count)
    {
        _rows.Clear();
        for (var i = 0; i < count; i++)
            _rows.Add(new JoomRow { Index = i + 1 });
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

    private static void ClearResult(JoomRow row)
    {
        row.Interval = null;
        row.Commission = null;
        row.Margin = null;
        row.PriceUsd = null;
        row.ProfitCny = null;
    }

    private void SetStatus(string text) => StatusChanged?.Invoke(this, text);

    private static string NormalizeSku(string? sku) => (sku ?? "").Trim();

    private static string FormatMoney(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    private static string FormatPercent(double rate) =>
        (rate * 100).ToString("0.00", CultureInfo.InvariantCulture) + "%";

    private static double ReadDouble(TextBox box, double fallback)
    {
        var raw = box.Text?.Trim();
        if (string.IsNullOrEmpty(raw))
            return fallback;
        return TryParseDouble(raw, out var v) ? v : fallback;
    }

    private static double ReadPercent(TextBox box, double fallbackPercent)
    {
        var raw = box.Text?.Trim().TrimEnd('%').Trim();
        if (string.IsNullOrEmpty(raw) || !TryParseDouble(raw, out var v))
            return fallbackPercent / 100.0;
        return v / 100.0;
    }

    private static bool TryParseDouble(string text, out double value)
    {
        text = text.Trim().Replace(",", "");
        return double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out value)
               || double.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out value);
    }
}
