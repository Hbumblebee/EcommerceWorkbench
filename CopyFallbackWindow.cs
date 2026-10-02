/*
 * 功能说明：剪贴板被占用时的备用复制窗口，文本已选中便于 Ctrl+C。
 * 创建日期：2026-07-23
 * 修改记录：2026-08-14 迁入 EcommerceWorkbench
 *           2026-10-02 统一现代化工作台视觉
 */

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EcommerceWorkbench.Services;

namespace EcommerceWorkbench;

/// <summary>
/// 备用复制对话框。
/// </summary>
public sealed class CopyFallbackWindow : Window
{
    public CopyFallbackWindow(string text)
    {
        Title = "手动复制 SKU 和价格";
        Width = 620;
        Height = 460;
        MinWidth = 480;
        MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)FindResource("BgBrush");
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");

        var root = new DockPanel { Margin = new Thickness(18) };
        var tip = new TextBlock
        {
            Text = "系统剪贴板正被占用。下面内容已全选，请按 Ctrl+C 复制后关闭窗口。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("MutedBrush"),
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 12)
        };
        DockPanel.SetDock(tip, Dock.Top);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        DockPanel.SetDock(buttons, Dock.Bottom);

        var box = new TextBox
        {
            Text = text,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 13,
            Padding = new Thickness(10),
            IsReadOnly = true,
            Background = (Brush)FindResource("PanelBrush")
        };

        var retry = new Button { Content = "再试自动复制", Margin = new Thickness(0, 0, 8, 0) };
        retry.Click += (_, _) =>
        {
            if (ClipboardHelper.TrySetText(box.Text, this))
            {
                DialogResult = true;
                Close();
            }
            else
            {
                MessageBox.Show(this, "仍无法写入剪贴板，请继续使用 Ctrl+C。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                box.Focus();
                box.SelectAll();
            }
        };

        var close = new Button
        {
            Content = "关闭",
            IsCancel = true,
            Style = (Style)FindResource("GhostButton"),
            Margin = new Thickness(0)
        };
        close.Click += (_, _) => Close();

        buttons.Children.Add(retry);
        buttons.Children.Add(close);
        root.Children.Add(tip);
        root.Children.Add(buttons);
        root.Children.Add(box);
        Content = root;

        Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
            if (ClipboardHelper.TrySetText(text, this))
            {
                DialogResult = true;
                Close();
            }
        };
    }
}
