/*
 * 功能说明：速卖通7 批量定价页，对齐计算表全托2.0。
 * 主要职责：打开速卖通全托管详情页读取变种 SKU，搜索参考价/重量后计算最终价，并回写供货价。
 * 创建日期：2026-09-13
 * 更新日期：2026-09-13 列表重量(g)与换算重量(kg)可编辑并互相同步
 */

using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using EcommerceWorkbench.Models;
using EcommerceWorkbench.Services;
using EcommerceWorkbench.Services.Dianxiaomi;
using EcommerceWorkbench.Services.Smt7;

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
        Loaded += Smt7PricingView_Loaded;
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
        ApplyParamsToBoxes(_settings.ToCalcSettings());
        SeedEmptyRows(12);
    }

    /// <summary>
    /// 将店小秘命中行导入速卖通7 表：按 SKU 覆盖成本与重量，新 SKU 追加，去掉空行后立即计算。
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
                var weight = hit.Weight.HasValue ? (double)hit.Weight.Value : (double?)null;

                if (bySku.TryGetValue(sku, out var existing))
                {
                    existing.Cost = cost;
                    existing.Weight = weight;
                    imported++;
                }
                else
                {
                    var row = new Smt7Row
                    {
                        Sku = hit.Sku.Trim(),
                        Cost = cost,
                        Weight = weight
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
            SetStatus($"已导入 {imported} 条到速卖通7 并完成计算。");
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
                ClearResult(row);
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
        SetStatus($"速卖通7 计算完成：成功 {okCount} · 跳过 {skipCount} · 失败 {errCount}");
    }

    public void RefreshCount() => UpdateCount();

    private void ProductUrlBox_LostFocus(object sender, RoutedEventArgs e) => PersistProductUrl();

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

            var uniqueSkus = await ReadSkusWithFallbackAsync(productId, cookieHeader);
            LoadUniqueSkus(uniqueSkus);
            _openedEditUrl = editUrl;
            SetStatus($"已读取变种 SKU {uniqueSkus.Count} 个（已去重）。请按需修改「搜索SKU」后点「搜索并定价」。");
            _productWindow?.SetHint("已读取变种 SKU。在工作台编辑搜索 SKU 并定价后，点「回写供货价」。");
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

    private async Task<IReadOnlyList<ChoiceVariantSkuGroup>> ReadSkusWithFallbackAsync(string productId, string cookieHeader)
    {
        try
        {
            if (_productWindow is not null)
            {
                var pageSkus = await _productWindow.ReadUniqueSkuCodesAsync();
                if (pageSkus.Count > 0)
                    return pageSkus.Select(sku => new ChoiceVariantSkuGroup { PageSku = sku, VariantCount = 1 }).ToList();
            }
        }
        catch
        {
            // 页面尚未就绪时改走接口
        }

        var detail = await _choiceProduct.GetDetailAsync(productId, cookieHeader);
        return detail.UniqueSkus;
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
            SetStatus($"正在按 {searchValues.Count} 个搜索 SKU 查询参考价与重量…");
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
            var hint = $"已写入 {result.Updated} 条变种的供货价{missed}。请在店小秘页面确认后点击保存/发布。";
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
            _rows.Clear();
            foreach (var group in uniqueSkus)
            {
                _rows.Add(new Smt7Row
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

    private static bool IsCookieInvalidError(string message)
    {
        return message.Contains("验证失败", StringComparison.OrdinalIgnoreCase)
               || message.Contains("code=2001", StringComparison.OrdinalIgnoreCase)
               || message.Contains("未登录", StringComparison.OrdinalIgnoreCase)
               || message.Contains("登录", StringComparison.OrdinalIgnoreCase) && message.Contains("失效", StringComparison.OrdinalIgnoreCase);
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

            if (!TryParseDouble(raw!, out var kg))
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

        if (!TryParseDouble(raw!, out var grams))
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

    private void SetStatus(string text) => StatusChanged?.Invoke(this, text);

    private static string NormalizeSku(string? sku) => (sku ?? "").Trim();

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
        return TryParseDouble(raw, out var v) ? v : fallback;
    }

    private static bool TryParseDouble(string text, out double value)
    {
        text = text.Trim().Replace(",", "");
        return double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out value)
               || double.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out value);
    }

    private static string FormatParam(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    private static string FormatThreshold(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
}
