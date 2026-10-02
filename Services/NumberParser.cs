/*
 * 功能说明：全应用统一的数字解析口径，避免「1,5」这类千分位/小数点歧义被静默放大 10 倍。
 * 主要职责：先按不变文化、再按当前区域解析；显式拒绝带千分位分隔符的输入与非有限值（NaN/∞）。
 * 创建日期：2026-10-03
 * 修改记录：
 *   2026-10-03 新增；修复原各视图 TryParseDouble 先 Replace(",", "") 导致逗号小数点区域致命误读
 */

using System.Globalization;

namespace EcommerceWorkbench.Services;

/// <summary>
/// 统一的数值文字解析器。
/// </summary>
public static class NumberParser
{
    /// <summary>
    /// 尝试把用户输入解析为 double。
    /// 拒绝：空、带千分位分隔符（如 1,234.5）、NaN、±∞。
    /// </summary>
    public static bool TryParseDouble(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var s = text.Trim();
        if (s.Length == 0)
            return false;

        // 显式拒绝千分位：无法在 de-DE「1,5」与 en-US「1,234」之间可靠区分，宁可让用户重填。
        if (s.Contains(','))
            return false;

        // double.TryParse 会接受 "NaN"/"Infinity"，金额与重量绝不接受。
        if (s.Contains("NaN", StringComparison.OrdinalIgnoreCase)
            || s.Contains("Infinity", StringComparison.OrdinalIgnoreCase)
            || s.Contains('∞'))
        {
            return false;
        }

        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && !double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
        {
            return false;
        }

        if (!double.IsFinite(value))
        {
            value = 0;
            return false;
        }

        return true;
    }

    /// <summary>解析失败时返回 <paramref name="fallback"/>。</summary>
    public static double ParseDoubleOr(string? text, double fallback)
        => TryParseDouble(text, out var v) ? v : fallback;

    /// <summary>
    /// 按 Shopee 官网表单 precision:2 的口径四舍五入（AwayFromZero）。
    /// 进位溢出为 ±∞ 时返回 0，避免 Infinity 流入定价公式。
    /// </summary>
    public static double Round2OrZero(double v)
    {
        var r = Math.Round(v, 2, MidpointRounding.AwayFromZero);
        return double.IsFinite(r) ? r : 0;
    }

    /// <summary>
    /// 按「百分数输入」解析（可带尾部 %），返回除以 100 后的比率。
    /// </summary>
    public static bool TryParsePercent(string? text, out double rate)
    {
        rate = 0;
        var s = (text ?? "").Trim().TrimEnd('%').Trim();
        if (!TryParseDouble(s, out var percent))
            return false;

        rate = percent / 100.0;
        return double.IsFinite(rate);
    }
}
