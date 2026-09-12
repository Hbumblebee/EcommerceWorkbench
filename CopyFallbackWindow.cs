/*
 * 功能说明：剪贴板被占用时的备用复制窗口，文本已选中便于 Ctrl+C。
 * 创建日期：2026-07-23
 * 修改记录：2026-08-14 迁入 EcommerceWorkbench
 */

using System.Windows;
using System.Windows.Controls;
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
        Width = 520;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = System.Windows.Media.Brushes.White;

        var root = new DockPanel { Margin = new Thickness(12) };
        var tip = new TextBlock
        {
            Text = "系统剪贴板正被占用。下面内容已全选，请按 Ctrl+C 复制后关闭窗口。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };
        DockPanel.SetDock(tip, Dock.Top);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };
        DockPanel.SetDock(buttons, Dock.Bottom);

        var box = new TextBox
        {
            Text = text,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            IsReadOnly = true
        };

        var retry = new Button { Content = "再试自动复制", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
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

        var close = new Button { Content = "关闭", Padding = new Thickness(12, 6, 12, 6), IsCancel = true };
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
