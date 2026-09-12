/*
 * 功能说明：可空 double 与文本互转，供定价表格编辑空单元格。
 * 创建日期：2026-08-14
 */

using System.Globalization;
using System.Windows.Data;

namespace EcommerceWorkbench.Converters;

public sealed class NullableDoubleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null)
            return string.Empty;
        if (value is double d)
            return d.ToString("0.####", culture);
        return value.ToString() ?? string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value?.ToString()?.Trim();
        if (string.IsNullOrEmpty(s))
            return null;

        s = s.Replace(",", "");
        if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var inv))
            return inv;
        if (double.TryParse(s, NumberStyles.Any, culture, out var loc))
            return loc;

        return Binding.DoNothing;
    }
}
