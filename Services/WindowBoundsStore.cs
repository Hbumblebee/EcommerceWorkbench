/*
 * 功能说明：记住主窗口上次关闭时的宽高与最大化状态，供下次启动还原。
 * 创建日期：2026-08-14
 */

using System.IO;
using System.Text.Json;
using System.Windows;

namespace EcommerceWorkbench.Services;

/// <summary>
/// 主窗口尺寸本地持久化。
/// </summary>
public sealed class WindowBoundsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public double Width { get; set; }
    public double Height { get; set; }
    public string State { get; set; } = nameof(WindowState.Normal);

    private static string GetFilePath() => Path.Combine(AppPaths.DataDirectory, "window-bounds.json");

    public static WindowBoundsStore? Load()
    {
        try
        {
            var path = GetFilePath();
            if (!File.Exists(path))
                return null;

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<WindowBoundsStore>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public static void SaveFrom(Window window)
    {
        try
        {
            var restore = window.RestoreBounds;
            var width = window.WindowState == WindowState.Normal ? window.Width : restore.Width;
            var height = window.WindowState == WindowState.Normal ? window.Height : restore.Height;
            if (double.IsNaN(width) || double.IsNaN(height) || width < 1 || height < 1)
                return;
            var state = window.WindowState == WindowState.Maximized
                ? nameof(WindowState.Maximized)
                : nameof(WindowState.Normal);

            var data = new WindowBoundsStore
            {
                Width = width,
                Height = height,
                State = state
            };
            File.WriteAllText(GetFilePath(), JsonSerializer.Serialize(data, JsonOptions));
        }
        catch
        {
            // 尺寸保存失败不影响关闭
        }
    }

    public void ApplyTo(Window window)
    {
        var width = Width;
        var height = Height;
        if (width < window.MinWidth)
            width = window.MinWidth;
        if (height < window.MinHeight)
            height = window.MinHeight;

        var area = SystemParameters.WorkArea;
        if (area.Width > 0)
            width = Math.Min(width, area.Width);
        if (area.Height > 0)
            height = Math.Min(height, area.Height);

        window.Width = width;
        window.Height = height;

        if (string.Equals(State, nameof(WindowState.Maximized), StringComparison.OrdinalIgnoreCase))
            window.WindowState = WindowState.Maximized;
    }
}
