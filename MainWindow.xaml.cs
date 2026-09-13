/*
 * 功能说明：工作台主窗口——页签壳（视图常驻以免卸载 WebView2）、状态栏，以及搜索结果导入定价。
 * 创建日期：2026-08-14
 * 修改记录：
 *   2026-08-14 导入后由定价页按默认净利润率计算并更新状态
 *   2026-08-14 启动时还原上次窗口大小
 *   2026-09-05 增加 JOOM 页签，店小秘命中结果可导入并计算
 *   2026-09-05 JOOM 产品详情页读取变种 SKU 并回写价格
 *   2026-09-13 增加速卖通7 页签，对齐全托2.0 公式并回写供货价
 */

using System.Windows;
using System.Windows.Input;
using EcommerceWorkbench.Models;
using EcommerceWorkbench.Services;

namespace EcommerceWorkbench;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        RestoreWindowBounds();

        SearchView.StatusChanged += (_, msg) => SetStatus(msg);
        PricingView.StatusChanged += (_, msg) => SetStatus(msg);
        PricingView.CountChanged += (_, msg) =>
        {
            if (TabPricing.IsChecked == true)
                CountText.Text = msg;
        };
        JoomView.StatusChanged += (_, msg) => SetStatus(msg);
        JoomView.CountChanged += (_, msg) =>
        {
            if (TabJoom.IsChecked == true)
                CountText.Text = msg;
        };
        SearchView.ImportToPricingRequested += OnImportToPricing;
        SearchView.ImportToJoomRequested += OnImportToJoom;
        JoomView.CookieHeaderProvider = () => SearchView.TryGetCookieHeader();
        JoomView.CookieRecordsProvider = () => SearchView.TryGetCookies();
        JoomView.CookieInvalidated += (_, msg) => SearchView.HandleExternalCookieInvalid(msg);
        Smt7View.StatusChanged += (_, msg) => SetStatus(msg);
        Smt7View.CountChanged += (_, msg) =>
        {
            if (TabSmt7.IsChecked == true)
                CountText.Text = msg;
        };
        SearchView.ImportToSmt7Requested += OnImportToSmt7;
        Smt7View.CookieHeaderProvider = () => SearchView.TryGetCookieHeader();
        Smt7View.CookieRecordsProvider = () => SearchView.TryGetCookies();
        Smt7View.CookieInvalidated += (_, msg) => SearchView.HandleExternalCookieInvalid(msg);
        Closing += (_, _) => WindowBoundsStore.SaveFrom(this);
        Closed += (_, _) =>
        {
            SearchView.Cleanup();
            JoomView.Cleanup();
            Smt7View.Cleanup();
        };
    }

    private void RestoreWindowBounds()
    {
        WindowBoundsStore.Load()?.ApplyTo(this);
    }

    private void TabSearch_Checked(object sender, RoutedEventArgs e)
    {
        if (SearchView is null || PricingView is null || JoomView is null || Smt7View is null)
            return;
        SearchView.Visibility = Visibility.Visible;
        PricingView.Visibility = Visibility.Collapsed;
        JoomView.Visibility = Visibility.Collapsed;
        Smt7View.Visibility = Visibility.Collapsed;
        ShortcutHint.Text = "F5 计算 | Ctrl+E 导出";
    }

    private void TabPricing_Checked(object sender, RoutedEventArgs e)
    {
        if (SearchView is null || PricingView is null || JoomView is null || Smt7View is null)
            return;
        SearchView.Visibility = Visibility.Collapsed;
        PricingView.Visibility = Visibility.Visible;
        JoomView.Visibility = Visibility.Collapsed;
        Smt7View.Visibility = Visibility.Collapsed;
        ShortcutHint.Text = "F5 计算 | Ctrl+E 导出";
    }

    private void TabJoom_Checked(object sender, RoutedEventArgs e)
    {
        if (SearchView is null || PricingView is null || JoomView is null || Smt7View is null)
            return;
        SearchView.Visibility = Visibility.Collapsed;
        PricingView.Visibility = Visibility.Collapsed;
        JoomView.Visibility = Visibility.Visible;
        Smt7View.Visibility = Visibility.Collapsed;
        ShortcutHint.Text = "F5 计算";
        JoomView.RefreshCount();
    }

    private void TabSmt7_Checked(object sender, RoutedEventArgs e)
    {
        if (SearchView is null || PricingView is null || JoomView is null || Smt7View is null)
            return;
        SearchView.Visibility = Visibility.Collapsed;
        PricingView.Visibility = Visibility.Collapsed;
        JoomView.Visibility = Visibility.Collapsed;
        Smt7View.Visibility = Visibility.Visible;
        ShortcutHint.Text = "F5 计算";
        Smt7View.RefreshCount();
    }

    private void OnImportToPricing(object? sender, IReadOnlyList<ProductResultRow> hits)
    {
        PricingView.ImportHits(hits);
        TabPricing.IsChecked = true;
    }

    private void OnImportToJoom(object? sender, IReadOnlyList<ProductResultRow> hits)
    {
        JoomView.ImportHits(hits);
        TabJoom.IsChecked = true;
    }

    private void OnImportToSmt7(object? sender, IReadOnlyList<ProductResultRow> hits)
    {
        Smt7View.ImportHits(hits);
        TabSmt7.IsChecked = true;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5 || (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control))
        {
            if (TabJoom.IsChecked == true)
                JoomView.RecalculateAll();
            else if (TabSmt7.IsChecked == true)
                Smt7View.RecalculateAll();
            else
                PricingView.RecalculateAll();
            e.Handled = true;
        }
        else if (e.Key == Key.E && Keyboard.Modifiers == ModifierKeys.Control && TabPricing.IsChecked == true)
        {
            PricingView.ExportCsv();
            e.Handled = true;
        }
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }
}
