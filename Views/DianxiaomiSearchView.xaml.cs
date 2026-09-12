/*
 * 功能说明：店小秘搜索页——按需登录浏览器、导出 Cookie、SKU 搜索与导入定价。
 * 主要职责：仅在无 Cookie 时允许打开登录页；Cookie 就绪后销毁 WebView2；展示命中/未命中。
 * 创建日期：2026-07-23
 * 修改记录：2026-08-14 迁入工作台 UserControl，增加导入到定价
 *           2026-08-15 参考价复制时按两位小数输出
 *           2026-09-05 命中结果增加 JOOM 计算导入
 */

using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using EcommerceWorkbench.Models;
using EcommerceWorkbench.Services;
using EcommerceWorkbench.Services.Dianxiaomi;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace EcommerceWorkbench.Views;

public partial class DianxiaomiSearchView : UserControl
{
    private const string SiteRoot = "https://www.dianxiaomi.com";
    private const string LoginUrl = "https://www.dianxiaomi.com/index.htm";

    private readonly CookieStore _cookieStore = new();
    private readonly ApiClient _apiClient = new();
    private readonly ProductSearchService _productSearch;
    private CookieExportFile? _currentExport;
    private List<ProductResultRow> _lastRows = [];
    private List<string> _missedSkus = [];
    private WebView2? _browser;
    private bool _isBrowserVisible;
    private bool _loaded;

    public event EventHandler<string>? StatusChanged;
    public event EventHandler<IReadOnlyList<ProductResultRow>>? ImportToPricingRequested;
    public event EventHandler<IReadOnlyList<ProductResultRow>>? ImportToJoomRequested;

    public string? TryGetCookieHeader()
        => HasCookie ? CookieStore.ToCookieHeader(_currentExport!.Cookies) : null;

    public IReadOnlyList<CookieRecord>? TryGetCookies()
        => HasCookie ? _currentExport!.Cookies : null;

    public void HandleExternalCookieInvalid(string detail)
        => InvalidateCookieBecauseExpired(detail);

    public DianxiaomiSearchView()
    {
        InitializeComponent();
        _productSearch = new ProductSearchService(_apiClient);
        Loaded += DianxiaomiSearchView_Loaded;
    }

    private bool HasCookie => _currentExport?.Cookies.Count > 0;

    public void Cleanup()
    {
        DestroyLoginBrowser();
        _apiClient.Dispose();
    }

    private void DianxiaomiSearchView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
            return;
        _loaded = true;

        var saved = _cookieStore.Load(CookieStore.DefaultFilePath);
        if (saved?.Cookies.Count > 0)
        {
            _currentExport = saved;
            UpdateCookiePreview();
            SetStatus($"已加载本地 Cookie（{saved.ExportedAtUtc:yyyy-MM-dd HH:mm} UTC，共 {saved.Cookies.Count} 条）。浏览器已关闭，可直接搜索。");
        }
        else
        {
            SetStatus("当前无 Cookie：请点击「打开登录页」完成登录后导出 Cookie。");
        }

        ApplyBrowserLayout();
        UpdateActionButtons();
        UpdateResultSummary();
    }

    private void ToggleBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (_browser is null)
        {
            return;
        }

        _isBrowserVisible = !_isBrowserVisible;
        ApplyBrowserLayout();
        SetStatus(_isBrowserVisible ? "已显示登录浏览器。" : "已暂时隐藏登录浏览器（未销毁）。");
    }

    private void ApplyBrowserLayout()
    {
        var showBrowser = _browser is not null && _isBrowserVisible;
        if (showBrowser)
        {
            BrowserPanel.Visibility = Visibility.Visible;
            BrowserColumn.Width = new GridLength(1.15, GridUnitType.Star);
            SplitterColumn.Width = new GridLength(12);
            WorkColumn.Width = new GridLength(1.2, GridUnitType.Star);
            ToggleBrowserButton.Content = "隐藏浏览器";
        }
        else
        {
            BrowserPanel.Visibility = Visibility.Collapsed;
            BrowserColumn.Width = new GridLength(0);
            SplitterColumn.Width = new GridLength(0);
            WorkColumn.Width = new GridLength(1, GridUnitType.Star);
            ToggleBrowserButton.Content = _browser is null ? "隐藏浏览器" : "显示浏览器";
        }
    }

    private async void OpenLogin_Click(object sender, RoutedEventArgs e)
    {
        if (HasCookie)
        {
            MessageBox.Show("当前已有 Cookie，无需打开登录页。若失效请先「清空会话」。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            await EnsureLoginBrowserAsync();
            _browser!.Source = new Uri(LoginUrl);
            _isBrowserVisible = true;
            ApplyBrowserLayout();
            UpdateActionButtons();
            SetStatus("已打开登录浏览器，请输入账号密码与验证码；完成后点击「导出 Cookie」。");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"打开登录浏览器失败。请确认已安装 Microsoft Edge WebView2 Runtime。\n\n{ex.Message}",
                "初始化失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task EnsureLoginBrowserAsync()
    {
        if (_browser is not null)
        {
            return;
        }

        var browser = new WebView2();
        BrowserHost.Children.Add(browser);
        _browser = browser;

        await browser.EnsureCoreWebView2Async();
        browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        browser.CoreWebView2.NavigationCompleted += (_, args) =>
        {
            if (args.IsSuccess)
            {
                SetStatus($"登录页已加载：{browser.Source}");
            }
        };
    }

    private void DestroyLoginBrowser()
    {
        if (_browser is null)
        {
            BrowserHost.Children.Clear();
            _isBrowserVisible = false;
            return;
        }

        try
        {
            _browser.CoreWebView2?.CookieManager.DeleteAllCookies();
        }
        catch
        {
            // 销毁阶段忽略清理异常
        }

        BrowserHost.Children.Clear();
        _browser.Dispose();
        _browser = null;
        _isBrowserVisible = false;
    }

    private async void ExportCookies_Click(object sender, RoutedEventArgs e)
    {
        if (_browser?.CoreWebView2 is null)
        {
            MessageBox.Show("请先点击「打开登录页」并完成登录。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var cookies = await _browser.CoreWebView2.CookieManager.GetCookiesAsync(SiteRoot);
            if (cookies.Count == 0)
            {
                MessageBox.Show("未获取到 Cookie，请确认已登录成功后再导出。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _currentExport = new CookieExportFile
            {
                Site = SiteRoot,
                ExportedAtUtc = DateTime.UtcNow,
                Cookies = cookies.Select(MapCookie).ToList()
            };

            _cookieStore.Save(_currentExport, CookieStore.DefaultFilePath);
            UpdateCookiePreview();

            DestroyLoginBrowser();
            ApplyBrowserLayout();
            UpdateActionButtons();

            SetStatus($"已导出并保存 Cookie {_currentExport.Cookies.Count} 条，登录浏览器已销毁。可直接搜索。");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导出 Cookie 失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveCookies_Click(object sender, RoutedEventArgs e)
    {
        if (!HasCookie)
        {
            MessageBox.Show("当前没有可保存的 Cookie。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            _cookieStore.Save(_currentExport!, CookieStore.DefaultFilePath);
            SetStatus($"已保存到：{CookieStore.DefaultFilePath}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadCookies_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var loaded = _cookieStore.Load(CookieStore.DefaultFilePath);
            if (loaded is null || loaded.Cookies.Count == 0)
            {
                MessageBox.Show("本地没有可用 Cookie。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _currentExport = loaded;
            DestroyLoginBrowser();
            UpdateCookiePreview();
            ApplyBrowserLayout();
            UpdateActionButtons();
            SetStatus($"已从本地加载 Cookie（{loaded.ExportedAtUtc:yyyy-MM-dd HH:mm} UTC），登录浏览器已关闭。");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"加载失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ClearSession_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            DestroyLoginBrowser();
            _currentExport = null;
            _lastRows = [];
            _missedSkus = [];
            CookiePreviewBox.Text = string.Empty;
            ResultGrid.ItemsSource = null;
            MissedSkuList.ItemsSource = null;
            UpdateResultSummary();

            try
            {
                if (File.Exists(CookieStore.DefaultFilePath))
                {
                    File.Delete(CookieStore.DefaultFilePath);
                }
            }
            catch
            {
                // 本地文件删除失败不影响清空内存会话
            }

            ApplyBrowserLayout();
            UpdateActionButtons();
            SetStatus("已清空会话与本地 Cookie。「打开登录页」已可用。");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"清空失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void SearchProducts_Click(object sender, RoutedEventArgs e)
    {
        var values = ProductSearchService.ParseSearchValues(SearchValueBox.Text);
        if (values.Count == 0)
        {
            MessageBox.Show("请填写 searchValue（可用逗号或换行分隔）。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var cookieHeader = HasCookie
            ? CookieStore.ToCookieHeader(_currentExport!.Cookies)
            : string.Empty;

        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            MessageBox.Show("请先打开登录页并导出 Cookie。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            RequestStatusText.Text = "搜索中…";
            var progress = new Progress<string>(msg => RequestStatusText.Text = msg);
            var outcome = await _productSearch.SearchAsync(values, cookieHeader, progress);

            _lastRows = outcome.Rows;
            _missedSkus = outcome.MissedSkus;
            ResultGrid.Items.SortDescriptions.Clear();
            ResultGrid.ItemsSource = null;
            ResultGrid.ItemsSource = _lastRows;
            MissedSkuList.ItemsSource = null;
            MissedSkuList.ItemsSource = _missedSkus;
            UpdateResultSummary();

            RequestStatusText.Text =
                $"完成：请求 {outcome.RequestedValueCount} 个，命中 {_lastRows.Count} 条，未命中 {_missedSkus.Count} 个";
            SetStatus(RequestStatusText.Text);
        }
        catch (Exception ex)
        {
            RequestStatusText.Text = "失败";
            ResultGrid.ItemsSource = null;
            MissedSkuList.ItemsSource = null;
            _lastRows = [];
            _missedSkus = [];
            UpdateResultSummary();
            SetStatus($"搜索失败：{ex.Message}");

            if (IsCookieInvalidError(ex.Message))
            {
                InvalidateCookieBecauseExpired(ex.Message);
            }
            else
            {
                MessageBox.Show(ex.Message, "搜索失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void ImportToPricing_Click(object sender, RoutedEventArgs e)
    {
        if (_lastRows.Count == 0)
        {
            MessageBox.Show("暂无命中结果可导入。请先搜索商品。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ImportToPricingRequested?.Invoke(this, _lastRows);
    }

    private void ImportToJoom_Click(object sender, RoutedEventArgs e)
    {
        if (_lastRows.Count == 0)
        {
            MessageBox.Show("暂无命中结果可计算。请先搜索商品。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ImportToJoomRequested?.Invoke(this, _lastRows);
    }

    private void UpdateResultSummary()
    {
        HitCountText.Text = $"{_lastRows.Count} 条";
        MissCountText.Text = $"{_missedSkus.Count} 个";
    }

    private static bool IsCookieInvalidError(string message)
    {
        return message.Contains("验证失败", StringComparison.OrdinalIgnoreCase)
               || message.Contains("code=2001", StringComparison.OrdinalIgnoreCase)
               || message.Contains("未登录", StringComparison.OrdinalIgnoreCase)
               || message.Contains("登录", StringComparison.OrdinalIgnoreCase) && message.Contains("失效", StringComparison.OrdinalIgnoreCase);
    }

    private void InvalidateCookieBecauseExpired(string detail)
    {
        _currentExport = null;
        CookiePreviewBox.Text = string.Empty;
        try
        {
            if (File.Exists(CookieStore.DefaultFilePath))
            {
                File.Delete(CookieStore.DefaultFilePath);
            }
        }
        catch
        {
            // ignore
        }

        DestroyLoginBrowser();
        ApplyBrowserLayout();
        UpdateActionButtons();
        SetStatus("Cookie 已失效，请重新点击「打开登录页」。");
        MessageBox.Show(
            $"Cookie 已失效或验证失败，请重新登录。\n\n{detail}",
            "需要重新登录",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void UpdateActionButtons()
    {
        OpenLoginButton.IsEnabled = !HasCookie;
        ToggleBrowserButton.IsEnabled = _browser is not null;
        ExportCookiesButton.IsEnabled = _browser is not null;
        SaveCookiesButton.IsEnabled = HasCookie;
    }

    private async void CopySkuPrice_Click(object sender, RoutedEventArgs e)
    {
        await CopyHitRowsAsync(includeWeight: false);
    }

    private async void CopySkuPriceWeight_Click(object sender, RoutedEventArgs e)
    {
        await CopyHitRowsAsync(includeWeight: true);
    }

    private async Task CopyHitRowsAsync(bool includeWeight)
    {
        if (_lastRows.Count == 0)
        {
            MessageBox.Show("暂无命中结果可复制。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var tsv = new StringBuilder(_lastRows.Count * 40);
        foreach (var row in _lastRows)
        {
            var sku = (row.Sku ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
            var price = row.Price?.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            tsv.Append(sku).Append('\t').Append(price);
            if (includeWeight)
            {
                var weight = row.Weight?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                tsv.Append('\t').Append(weight);
            }

            tsv.Append("\r\n");
        }

        var label = includeWeight ? "SKU+价格+重量" : "SKU+价格";
        await CopyTextAsync(tsv.ToString(), $"已复制 {_lastRows.Count} 行 {label}（可直接粘贴到 Excel）。");
    }

    private async void CopyMissedSkus_Click(object sender, RoutedEventArgs e)
    {
        if (_missedSkus.Count == 0)
        {
            MessageBox.Show("当前没有未命中的 SKU。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var text = string.Join("\r\n", _missedSkus);
        await CopyTextAsync(text, $"已复制 {_missedSkus.Count} 个未命中 SKU。");
    }

    private async Task CopyTextAsync(string text, string successStatus)
    {
        var owner = Window.GetWindow(this);
        try
        {
            await Task.Delay(150);
            if (ClipboardHelper.TrySetText(text, owner))
            {
                SetStatus(successStatus);
                return;
            }

            var fallback = new CopyFallbackWindow(text) { Owner = owner };
            fallback.ShowDialog();
            SetStatus(fallback.DialogResult == true ? successStatus : "已打开手动复制窗口（剪贴板被占用）。");
        }
        catch (Exception ex)
        {
            try
            {
                var fallback = new CopyFallbackWindow(text) { Owner = owner };
                fallback.ShowDialog();
                SetStatus("已打开手动复制窗口。");
            }
            catch
            {
                MessageBox.Show($"复制失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                SetStatus($"复制失败：{ex.Message}");
            }
        }
    }

    private void UpdateCookiePreview()
    {
        if (_currentExport is null)
        {
            CookiePreviewBox.Text = string.Empty;
            return;
        }

        CookiePreviewBox.Text =
            $"导出时间(UTC): {_currentExport.ExportedAtUtc:yyyy-MM-dd HH:mm:ss}  |  条数: {_currentExport.Cookies.Count}\r\n" +
            CookieStore.ToCookieHeader(_currentExport.Cookies);
    }

    private static CookieRecord MapCookie(CoreWebView2Cookie cookie)
    {
        return new CookieRecord
        {
            Name = cookie.Name,
            Value = cookie.Value,
            Domain = cookie.Domain,
            Path = cookie.Path,
            IsHttpOnly = cookie.IsHttpOnly,
            IsSecure = cookie.IsSecure,
            ExpiresUtcTicks = cookie.Expires == DateTime.MinValue
                ? null
                : cookie.Expires.ToUniversalTime().Ticks
        };
    }

    private void SetStatus(string message)
    {
        StatusChanged?.Invoke(this, message);
    }
}
