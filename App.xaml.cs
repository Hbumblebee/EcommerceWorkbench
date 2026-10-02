/*
 * 功能说明：WPF 应用程序入口。
 * 创建日期：2026-08-14
 * 修改记录：
 *   2026-10-03 增加全局未处理异常兜底：避免损坏的本地 JSON（如 cookies.json）直接终止进程
 */

using System.Windows;
using System.Windows.Threading;

namespace EcommerceWorkbench;

/// <summary>
/// 应用程序入口。
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // 记为已处理：本地设置/Cookie 文件损坏等可恢复异常不应让整个工作台崩掉。
        e.Handled = true;
        MessageBox.Show(
            "操作过程中发生未预期的错误，已跳过本次操作，工作台仍可继续使用。\n\n" + e.Exception.Message,
            "错误",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }
}
