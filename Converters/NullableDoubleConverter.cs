/*
 * 功能说明：可空 double 与文本互转，供定价表格编辑空单元格。
 * 创建日期：2026-08-14
 * 修改记录：
 *   2026-10-03 改用 NumberParser：不再先删逗号（原实现会把 de-DE 的「1,5」读成 15）；
 *              非法或歧义输入不再静默保留文本，而是回退到原值，避免单元格显示与绑定值不一致
 */
using System.Globalization;
using System.Windows.Data;
using EcommerceWorkbench.Services;

namespace EcommerceWorkbench.Converters;

public sealed class NullableDoubleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null)
            return string.Empty;
        if (value is double d)
            return d.ToString("0.####", CultureInfo.InvariantCulture);
        return value.ToString() ?? string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value?.ToString()?.Trim();
        if (string.IsNullOrEmpty(s))
            return null;

        if (NumberParser.TryParseDouble(s, out var parsed))
            return parsed;

        // 解析失败（千分位歧义、NaN/∞、乱输）：不写入，保持原值，绝不猜成 15 或 0。
        // 视觉反馈由 XAML 上的 NumberTextValidationRule 负责（单元格红色边框 + 错误提示）。
        return Binding.DoNothing;
    }
}
