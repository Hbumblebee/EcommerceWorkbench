/*
 * 功能说明：数值单元格的输入校验，给非法输入可见反馈（红框 + 提示），而不是静默忽略。
 * 主要职责：复用 NumberParser 的口径（拒绝千分位歧义、NaN/∞），允许留空表示「未填写」。
 * 创建日期：2026-10-03
 */

using System.Globalization;
using System.Windows.Controls;
using EcommerceWorkbench.Services;

namespace EcommerceWorkbench.Converters;

/// <summary>
/// 可空数值列的校验规则。
/// </summary>
public sealed class NumberTextValidationRule : ValidationRule
{
    public override ValidationResult Validate(object? value, CultureInfo cultureInfo)
    {
        var text = value?.ToString()?.Trim();

        // 空单元格是合法状态（表示未填写），由 PriceCalculator 的卫语句负责提示。
        if (string.IsNullOrEmpty(text))
            return ValidationResult.ValidResult;

        if (NumberParser.TryParseDouble(text, out _))
            return ValidationResult.ValidResult;

        if (text.Contains(','))
            return new ValidationResult(false, "请勿使用千分位逗号（如 1,234.5），直接输入 1234.5");

        return new ValidationResult(false, "请输入有效数字");
    }
}
