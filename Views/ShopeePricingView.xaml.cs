/*
 * 功能说明：Shopee 台湾站批量定价页（WPF），对齐原 WinForms 定价工具能力。
 * 创建日期：2026-08-14
 * 修改记录：
 *   2026-08-14 默认净利润率驱动计算；折前售价冻结；默认汇率 4.77
 *   2026-08-15 站点调价比例展示改为固定两位小数，对齐官网 toFixed(2)
 *   2026-08-15 调价比例按官网 Ce 百分数直接展示；净利润率固定两位小数
 *   2026-08-15 可编辑单元格选中/编辑时保持深色文字，避免白字看不清
 *   2026-08-15 增加「重计算」：按官网默认模式用各行预期净利润金额计算，不按净利润率倒推
 */

using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EcommerceWorkbench.Models;
using EcommerceWorkbench.Services.Shopee;
using Microsoft.Win32;

namespace EcommerceWorkbench.Views;

public partial class ShopeePricingView : UserControl
{
    private static readonly SolidColorBrush BrandOrange = new(Color.FromRgb(238, 77, 45));
    private static readonly SolidColorBrush TagBg = new(Color.FromRgb(240, 241, 244));
    private static readonly SolidColorBrush TextPrimary = new(Color.FromRgb(28, 28, 30));

    private readonly RateService _rateService = new();
    private readonly ObservableCollection<ProductRow> _rows = [];
    private readonly UserSettings _userSettings = UserSettings.Load();
    private bool _suppressAutoCalc;
    private bool _loaded;

    public event EventHandler<string>? StatusChanged;
    public event EventHandler<string>? CountChanged;

    public ShopeePricingView()
    {
        InitializeComponent();
        PricingGrid.ItemsSource = _rows;
        Loaded += ShopeePricingView_Loaded;
    }

    private async void ShopeePricingView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
            return;
        _loaded = true;

        ApplyUserSettings();
        _rateService.TryLoadLocalCache();
        InitDefaultsFromRates();
        ApplyUserSettings();
        SeedEmptyRows(12);

        SetStatus("正在获取最新运费费率…");
        var (ok, msg) = await _rateService.RefreshFromApiAsync();
        InitDefaultsFromRates();
        ApplyUserSettings();
        SetStatus(ok ? msg : msg + "（可继续用当前费率）");
    }

    /// <summary>
    /// 将店小秘命中行导入定价表：按 SKU 覆盖成本/重量，新 SKU 追加，去掉空行。
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

            var bySku = new Dictionary<string, ProductRow>(StringComparer.OrdinalIgnoreCase);
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
                var weight = hit.Weight.HasValue
                    ? (double)Math.Round(hit.Weight.Value, 2, MidpointRounding.AwayFromZero)
                    : (double?)null;

                if (bySku.TryGetValue(sku, out var existing))
                {
                    existing.Cost = cost;
                    existing.Weight = weight;
                    imported++;
                }
                else
                {
                    var row = new ProductRow
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
            UpdateCount();
            if (ReadDefaultProfitRate() is not null)
            {
                ApplyDefaultProfitRateToRows();
            }
            else if (AutoCalcCheck.IsChecked == true)
            {
                RecalculateAll();
            }
            else
            {
                SetStatus($"已导入 {imported} 条到定价（按 SKU 覆盖成本/重量）。请填写默认预期净利润率后自动计算。");
            }
        }

        return imported;
    }

    /// <summary>
    /// 将默认预期净利润率写入各行，并按该利润率重算售价、净利润与费用。
    /// </summary>
    public void ApplyDefaultProfitRateToRows()
    {
        CommitGrid();
        var ratePercent = ReadDefaultProfitRate();
        if (ratePercent is null)
        {
            SetStatus("请填写默认预期净利润率后再计算。");
            return;
        }

        var defaults = _rateService.CreateDefaultInput();
        defaults.ExchangeRate = (double)ReadDecimal(RateBox, UserSettings.DefaultExchangeRate);
        defaults.DiscountPercent = (double)ReadDecimal(DiscountBox, 30m);
        defaults.WithinBorderSf = (double)ReadDecimal(DomesticBox, 1m);
        defaults.ActivityScRatePercent = (double)ReadDecimal(ActivityBox, 1m);
        defaults.WithdrawHfRatePercent = (double)ReadDecimal(WithdrawBox, 1m);

        var feeModes = _rateService.Current.Channel.FeeModes;
        var okCount = 0;
        var skipCount = 0;
        var errCount = 0;

        var duplicateSkus = _rows
            .Select(r => NormalizeSku(r.Sku))
            .Where(s => s.Length > 0)
            .GroupBy(s => s, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var profitRateText = Math.Round(ratePercent.Value, 2, MidpointRounding.AwayFromZero)
            .ToString("0.00", CultureInfo.InvariantCulture) + "%";

        foreach (var row in _rows)
        {
            if (!row.HasAnyInput)
            {
                ClearResult(row);
                row.Status = "";
                skipCount++;
                continue;
            }

            var sku = NormalizeSku(row.Sku);
            if (sku.Length == 0)
            {
                ClearResult(row);
                row.Status = "请填写 SKU";
                errCount++;
                continue;
            }

            if (duplicateSkus.Contains(sku))
            {
                ClearResult(row);
                row.Status = "SKU 重复";
                errCount++;
                continue;
            }

            if (row.Cost is null || row.Weight is null)
            {
                ClearResult(row);
                row.ProfitRate = profitRateText;
                row.Status = "请补全成本/重量";
                errCount++;
                continue;
            }

            try
            {
                var input = new PricingInput
                {
                    Cost = row.Cost.Value,
                    Weight = row.Weight.Value,
                    ExpectedProfitRatePercent = Math.Round(ratePercent.Value, 2, MidpointRounding.AwayFromZero),
                    ExchangeRate = defaults.ExchangeRate,
                    DiscountPercent = defaults.DiscountPercent,
                    WithinBorderSf = defaults.WithinBorderSf,
                    CommissionRatePercent = defaults.CommissionRatePercent,
                    TradeHfRatePercent = defaults.TradeHfRatePercent,
                    ActivityScRatePercent = defaults.ActivityScRatePercent,
                    WithdrawHfRatePercent = defaults.WithdrawHfRatePercent
                };

                var result = PricingCalculator.CalculateByExpectedProfitRate(input, feeModes);
                ApplyPricingResult(row, result, defaults.ExchangeRate, syncExpectedProfit: true);
                row.ProfitRate = profitRateText;
                okCount++;
            }
            catch (Exception ex)
            {
                ClearResult(row);
                row.ProfitRate = profitRateText;
                row.Status = ex.Message;
                errCount++;
            }
        }

        UpdateCount();
        SetStatus($"已按净利润率 {profitRateText} 计算：成功 {okCount} · 跳过 {skipCount} · 待补全/失败 {errCount} · 费率 {_rateService.Current.Date}");
    }

    public void RecalculateAll()
    {
        if (ReadDefaultProfitRate() is not null)
        {
            ApplyDefaultProfitRateToRows();
            return;
        }

        RecalculateByExpectedProfit();
    }

    /// <summary>
    /// 对齐官网默认模式 get_price_by_profit：用各行预期净利润（金额）计算售价与净利润率，不按默认净利润率倒推。
    /// </summary>
    public void RecalculateByExpectedProfit()
    {
        CommitGrid();
        var defaults = _rateService.CreateDefaultInput();
        defaults.ExchangeRate = (double)ReadDecimal(RateBox, UserSettings.DefaultExchangeRate);
        defaults.DiscountPercent = (double)ReadDecimal(DiscountBox, 30m);
        defaults.WithinBorderSf = (double)ReadDecimal(DomesticBox, 1m);
        defaults.ActivityScRatePercent = (double)ReadDecimal(ActivityBox, 1m);
        defaults.WithdrawHfRatePercent = (double)ReadDecimal(WithdrawBox, 1m);

        var feeModes = _rateService.Current.Channel.FeeModes;
        var okCount = 0;
        var skipCount = 0;
        var errCount = 0;

        var duplicateSkus = _rows
            .Select(r => NormalizeSku(r.Sku))
            .Where(s => s.Length > 0)
            .GroupBy(s => s, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var row in _rows)
        {
            if (!row.HasAnyInput)
            {
                ClearResult(row);
                row.Status = "";
                skipCount++;
                continue;
            }

            var sku = NormalizeSku(row.Sku);
            if (sku.Length == 0)
            {
                ClearResult(row);
                row.Status = "请填写 SKU";
                errCount++;
                continue;
            }

            if (duplicateSkus.Contains(sku))
            {
                ClearResult(row);
                row.Status = "SKU 重复";
                errCount++;
                continue;
            }

            if (row.Cost is null || row.Weight is null || row.ExpectedProfit is null)
            {
                ClearResult(row);
                row.Status = "请补全成本/重量/净利润";
                errCount++;
                continue;
            }

            try
            {
                var input = new PricingInput
                {
                    Cost = row.Cost.Value,
                    Weight = row.Weight.Value,
                    ExpectedProfit = row.ExpectedProfit.Value,
                    ExchangeRate = defaults.ExchangeRate,
                    DiscountPercent = defaults.DiscountPercent,
                    WithinBorderSf = defaults.WithinBorderSf,
                    CommissionRatePercent = defaults.CommissionRatePercent,
                    TradeHfRatePercent = defaults.TradeHfRatePercent,
                    ActivityScRatePercent = defaults.ActivityScRatePercent,
                    WithdrawHfRatePercent = defaults.WithdrawHfRatePercent
                };

                var result = PricingCalculator.CalculateByExpectedProfit(input, feeModes);
                ApplyPricingResult(row, result, defaults.ExchangeRate, syncExpectedProfit: false);
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
        SetStatus($"已按官网逻辑（预期净利润）重计算：成功 {okCount} · 跳过 {skipCount} · 待补全/失败 {errCount} · 费率 {_rateService.Current.Date}");
    }

    public void ExportCsv()
    {
        CommitGrid();
        var dialog = new SaveFileDialog
        {
            Filter = "CSV 文件|*.csv",
            FileName = $"定价结果_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
        };
        if (dialog.ShowDialog() != true)
            return;

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",",
            "#", "SKU", "商品成本价(CNY)", "包裹重量(g)", "预期净利润(CNY)",
            "折前售价", "商品售价（折后售价）", "净利润率", "订单收入", "站点调价比例",
            "佣金", "交易手续费", "活动服务费", "卖家支付运费", "跨境物流成本（藏价）", "买家支付运费", "提现手续费", "状态"));

        foreach (var row in _rows)
        {
            if (!row.HasAnyInput && string.IsNullOrEmpty(row.Status))
                continue;

            sb.AppendLine(string.Join(",",
                row.Index,
                Csv(row.Sku),
                Csv(row.Cost),
                Csv(row.Weight),
                Csv(row.ExpectedProfit),
                Csv(row.PriceBeforeDiscount),
                Csv(row.DiscountedPrice),
                Csv(row.ProfitRate),
                Csv(row.OrderRevenue),
                Csv(row.AdjRate),
                Csv(row.Commission),
                Csv(row.TradeHf),
                Csv(row.ActivitySc),
                Csv(row.SellerSf),
                Csv(row.HiddenFee),
                Csv(row.BuyerSf),
                Csv(row.WithdrawHf),
                Csv(row.Status)));
        }

        File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        SetStatus($"已导出：{dialog.FileName}");
    }

    private void Calc_Click(object sender, RoutedEventArgs e) => RecalculateAll();

    private void RecalcOfficial_Click(object sender, RoutedEventArgs e) => RecalculateByExpectedProfit();

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshRatesAsync();

    private void AddRow_Click(object sender, RoutedEventArgs e)
    {
        _rows.Add(new ProductRow { Index = _rows.Count + 1 });
        UpdateCount();
        PricingGrid.SelectedItem = _rows[^1];
        PricingGrid.ScrollIntoView(_rows[^1]);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var selected = PricingGrid.SelectedCells
            .Select(c => c.Item)
            .OfType<ProductRow>()
            .Distinct()
            .ToList();
        if (selected.Count == 0 && PricingGrid.CurrentItem is ProductRow current)
            selected.Add(current);

        foreach (var row in selected)
            _rows.Remove(row);

        if (_rows.Count == 0)
            SeedEmptyRows(5);

        Renumber();
        UpdateCount();
        SetStatus($"已删除 {Math.Max(1, selected.Count)} 行");
    }

    private void ClearInput_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows)
        {
            row.Sku = null;
            row.Cost = null;
            row.Weight = null;
            row.ExpectedProfit = null;
            ClearResult(row);
            row.Status = "";
        }
        SetStatus("已清空全部输入与结果");
        UpdateCount();
    }

    private void ClearResult_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows)
        {
            ClearResult(row);
            row.Status = "";
        }
        SetStatus("已清空计算结果（输入保留）");
        UpdateCount();
    }

    private void Export_Click(object sender, RoutedEventArgs e) => ExportCsv();

    private void SaveRate_Click(object sender, RoutedEventArgs e)
    {
        PersistUserSettings(showStatus: true);
        if (AutoCalcCheck.IsChecked == true)
            RecalculateAll();
    }

    private void DefaultProfitRate_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressAutoCalc)
            return;
        PersistUserSettings(showStatus: false);
        ApplyDefaultProfitRateToRows();
    }

    private void Param_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressAutoCalc)
            return;
        PersistUserSettings(showStatus: ReferenceEquals(sender, RateBox));
        if (AutoCalcCheck.IsChecked == true)
            RecalculateAll();
    }

    private void AutoCalc_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _suppressAutoCalc)
            return;
        PersistUserSettings(showStatus: false);
    }

    private void PricingGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (_suppressAutoCalc || AutoCalcCheck.IsChecked != true)
            return;
        Dispatcher.BeginInvoke(RecalculateAll, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void PricingGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
        {
            var text = TryGetClipboardText();
            if (text.Contains('\n') || text.Contains('\t') || text.Contains('\r'))
            {
                PricingGrid.CancelEdit();
                PasteFromClipboard();
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Delete && PricingGrid.CurrentCell.Column is { IsReadOnly: false })
        {
            ClearCurrentEditableCell();
            e.Handled = true;
        }
    }

    private void ClearCurrentEditableCell()
    {
        if (PricingGrid.CurrentItem is not ProductRow row || PricingGrid.CurrentCell.Column is null)
            return;

        var header = PricingGrid.CurrentCell.Column.Header?.ToString();
        switch (header)
        {
            case "SKU":
                row.Sku = null;
                break;
            case "商品成本价 CNY":
                row.Cost = null;
                break;
            case "包裹重量 g":
                row.Weight = null;
                break;
            case "预期净利润 CNY":
                row.ExpectedProfit = null;
                break;
        }
    }

    private void PasteFromClipboard()
    {
        var text = TryGetClipboardText();
        if (string.IsNullOrWhiteSpace(text))
            return;

        var startRow = 0;
        var startCol = 1;
        if (PricingGrid.CurrentItem is ProductRow current)
            startRow = Math.Max(0, _rows.IndexOf(current));
        if (PricingGrid.CurrentCell.Column is not null)
            startCol = PricingGrid.CurrentCell.Column.DisplayIndex;

        while (startCol < PricingGrid.Columns.Count && PricingGrid.Columns[startCol].IsReadOnly)
            startCol++;
        if (startCol >= PricingGrid.Columns.Count)
            startCol = 1;

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized
            .Split('\n')
            .Select(l => l.TrimEnd())
            .Where(l => l.Length > 0)
            .ToArray();
        if (lines.Length == 0)
            return;

        var isTsv = lines.Any(l => l.Contains('\t'));
        var pastedCells = 0;

        _suppressAutoCalc = true;
        try
        {
            for (var i = 0; i < lines.Length; i++)
            {
                while (startRow + i >= _rows.Count)
                    _rows.Add(new ProductRow { Index = _rows.Count + 1 });

                var cells = isTsv
                    ? lines[i].Split('\t')
                    : SplitNonTsvLine(lines[i]);

                for (var j = 0; j < cells.Length; j++)
                {
                    var colIndex = startCol + j;
                    if (colIndex < 0 || colIndex >= PricingGrid.Columns.Count)
                        break;

                    var col = PricingGrid.Columns[colIndex];
                    if (col.IsReadOnly)
                        continue;

                    var raw = cells[j].Trim().Trim('"');
                    var row = _rows[startRow + i];
                    var header = col.Header?.ToString();

                    if (header == "SKU")
                    {
                        row.Sku = string.IsNullOrEmpty(raw) ? null : raw;
                        pastedCells++;
                        continue;
                    }

                    if (!TryParseDouble(raw, out var val))
                        continue;

                    switch (header)
                    {
                        case "商品成本价 CNY":
                            row.Cost = val;
                            pastedCells++;
                            break;
                        case "包裹重量 g":
                            row.Weight = val;
                            pastedCells++;
                            break;
                        case "预期净利润 CNY":
                            row.ExpectedProfit = val;
                            pastedCells++;
                            break;
                    }
                }
            }
        }
        finally
        {
            _suppressAutoCalc = false;
        }

        Renumber();
        UpdateCount();

        var formatLabel = isTsv ? "TSV（制表符分隔）" : "普通文本";
        if (AutoCalcCheck.IsChecked == true)
        {
            SetStatus($"已按 {formatLabel} 粘贴 {lines.Length} 行（{pastedCells} 个单元格）");
            RecalculateAll();
        }
        else
        {
            SetStatus($"已按 {formatLabel} 粘贴 {lines.Length} 行，按 F5 计算");
        }
    }

    private static string[] SplitNonTsvLine(string line)
    {
        if (line.Contains(',') || line.Contains('，'))
            return line.Split([',', '，'], StringSplitOptions.None);

        return line.Split([' ', '\u3000'], StringSplitOptions.RemoveEmptyEntries);
    }

    private async Task RefreshRatesAsync()
    {
        RefreshButton.IsEnabled = false;
        SetStatus("正在刷新运费费率…");
        try
        {
            var (ok, msg) = await _rateService.RefreshFromApiAsync();
            InitDefaultsFromRates();
            SetStatus(msg);
            if (ok)
                RecalculateAll();
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void ApplyUserSettings()
    {
        _suppressAutoCalc = true;
        try
        {
            RateBox.Text = _userSettings.ExchangeRate.ToString("0.####", CultureInfo.InvariantCulture);
            DiscountBox.Text = _userSettings.DiscountPercent.ToString("0.##", CultureInfo.InvariantCulture);
            DomesticBox.Text = _userSettings.WithinBorderSf.ToString("0.##", CultureInfo.InvariantCulture);
            ActivityBox.Text = _userSettings.ActivityScRatePercent.ToString("0.##", CultureInfo.InvariantCulture);
            WithdrawBox.Text = _userSettings.WithdrawHfRatePercent.ToString("0.##", CultureInfo.InvariantCulture);
            DefaultProfitRateBox.Text = _userSettings.DefaultExpectedProfitRate?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
            AutoCalcCheck.IsChecked = _userSettings.AutoCalc;
            UpdateRateSavedLabel(saved: true);
        }
        finally
        {
            _suppressAutoCalc = false;
        }
    }

    private void PersistUserSettings(bool showStatus)
    {
        _userSettings.ExchangeRate = ReadDecimal(RateBox, UserSettings.DefaultExchangeRate);
        _userSettings.DiscountPercent = ReadDecimal(DiscountBox, 30m);
        _userSettings.WithinBorderSf = ReadDecimal(DomesticBox, 1m);
        _userSettings.ActivityScRatePercent = ReadDecimal(ActivityBox, 1m);
        _userSettings.WithdrawHfRatePercent = ReadDecimal(WithdrawBox, 1m);
        _userSettings.DefaultExpectedProfitRate = ReadDefaultProfitRate() is { } p ? (decimal)p : null;
        _userSettings.AutoCalc = AutoCalcCheck.IsChecked == true;
        try
        {
            _userSettings.Save();
            UpdateRateSavedLabel(saved: true);
            if (showStatus)
                SetStatus($"汇率已保存：1 CNY ≈ {ReadDecimal(RateBox, UserSettings.DefaultExchangeRate):0.####} TWD（下次启动自动带出）");
        }
        catch (Exception ex)
        {
            UpdateRateSavedLabel(saved: false);
            SetStatus($"汇率保存失败：{ex.Message}");
        }
    }

    private void UpdateRateSavedLabel(bool saved)
    {
        if (saved)
        {
            RateSavedLabel.Foreground = (Brush)FindResource("OkGreenBrush");
            RateSavedLabel.Text = $"已保存 1 CNY ≈ {ReadDecimal(RateBox, UserSettings.DefaultExchangeRate):0.####} TWD";
        }
        else
        {
            RateSavedLabel.Foreground = (Brush)FindResource("BrandOrangeBrush");
            RateSavedLabel.Text = "保存失败，请重试";
        }
    }

    private void InitDefaultsFromRates()
    {
        var bundle = _rateService.Current;
        var site = bundle.Site;
        var ch = bundle.Channel;

        TagPanel.Children.Clear();
        TagPanel.Children.Add(MakeTag(ch.SiteCn, accent: true));
        TagPanel.Children.Add(MakeTag(ch.CargoCn));
        TagPanel.Children.Add(MakeTag(ch.ChannelCn));
        TagPanel.Children.Add(MakeTag(ch.Zone));
        TagPanel.Children.Add(MakeTag($"佣金 {site.PlatformCommission}%"));
        TagPanel.Children.Add(MakeTag($"交易费 {site.HandlingFee}%"));
        TagPanel.Children.Add(MakeTag(site.CurrencyUnit));

        RateSourceText.Text = "运费来源：" + _rateService.SourceDescription;
    }

    private Border MakeTag(string text, bool accent = false)
    {
        return new Border
        {
            Background = accent ? BrandOrange : TagBg,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 2, 8, 4),
            Child = new TextBlock
            {
                Text = text,
                Foreground = accent ? Brushes.White : TextPrimary
            }
        };
    }

    private void SeedEmptyRows(int count)
    {
        _rows.Clear();
        for (var i = 0; i < count; i++)
            _rows.Add(new ProductRow { Index = i + 1 });
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

    private double? ReadDefaultProfitRate()
    {
        var raw = DefaultProfitRateBox.Text?.Trim().TrimEnd('%').Trim();
        if (string.IsNullOrEmpty(raw))
            return null;
        return TryParseDouble(raw, out var v)
            ? Math.Round(v, 2, MidpointRounding.AwayFromZero)
            : null;
    }

    private static decimal ReadDecimal(TextBox box, decimal fallback)
    {
        var raw = box.Text?.Trim();
        if (string.IsNullOrEmpty(raw))
            return fallback;
        if (decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var inv))
            return inv;
        if (decimal.TryParse(raw, NumberStyles.Any, CultureInfo.CurrentCulture, out var loc))
            return loc;
        return fallback;
    }

    private void SetStatus(string text) => StatusChanged?.Invoke(this, text);

    private static void ApplyPricingResult(ProductRow row, PricingResult result, double exchangeRate, bool syncExpectedProfit)
    {
        if (syncExpectedProfit && exchangeRate != 0)
        {
            var fx = Math.Round(exchangeRate, 2, MidpointRounding.AwayFromZero);
            if (fx != 0)
                row.ExpectedProfit = Math.Round(result.Profit / fx, 2, MidpointRounding.AwayFromZero);
        }
        row.DiscountedPrice = FormatMoney(result.DiscountedPrice);
        row.PriceBeforeDiscount = FormatMoney(result.PriceBeforeDiscount);
        row.ProfitRate = (result.ProfitRate * 100).ToString("0.00", CultureInfo.InvariantCulture) + "%";
        row.AdjRate = result.AdjRate.ToString("0.00", CultureInfo.InvariantCulture) + "%";
        row.OrderRevenue = FormatMoney(result.OrderRevenue);
        row.HiddenFee = FormatMoney(result.HiddenFee);
        row.BuyerSf = FormatMoney(result.BuyerSf);
        row.SellerSf = FormatMoney(result.SellerSf);
        row.Commission = FormatMoney(result.Commission);
        row.TradeHf = FormatMoney(result.TradeHf);
        row.ActivitySc = FormatMoney(result.ActivitySc);
        row.WithdrawHf = FormatMoney(result.WithdrawHf);
        row.Status = "OK";
    }

    private static void ClearResult(ProductRow row)
    {
        row.DiscountedPrice = null;
        row.PriceBeforeDiscount = null;
        row.ProfitRate = null;
        row.OrderRevenue = null;
        row.AdjRate = null;
        row.HiddenFee = null;
        row.BuyerSf = null;
        row.SellerSf = null;
        row.Commission = null;
        row.TradeHf = null;
        row.ActivitySc = null;
        row.WithdrawHf = null;
    }

    private static string TryGetClipboardText()
    {
        try
        {
            return Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string NormalizeSku(string? sku) => (sku ?? "").Trim();

    private static string FormatMoney(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Csv(object? value)
    {
        var s = value?.ToString() ?? "";
        if (s.Contains('"') || s.Contains(',') || s.Contains('\n'))
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

    private static bool TryParseDouble(string text, out double value)
    {
        text = text.Trim().Replace(",", "");
        return double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out value)
               || double.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out value);
    }
}
