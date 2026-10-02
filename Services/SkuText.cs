/*
 * 功能说明：SKU 文本的统一归一化口径。
 * 主要职责：消除「搜索侧做兼容性折叠、定价侧只 Trim」导致的全角/兼容字符错配与误判重复。
 * 创建日期：2026-10-03
 */

using System.Text;

namespace EcommerceWorkbench.Services;

/// <summary>
/// SKU 归一化工具。
/// </summary>
public static class SkuText
{
    /// <summary>
    /// 归一键：去首尾空白并做 Unicode 兼容性折叠（全角→半角等）。
    /// 与 <c>ProductSearchService</c> 原有的 <c>Trim().Normalize(FormKC)</c> 一致。
    /// </summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return value.Trim().Normalize(NormalizationForm.FormKC);
    }
}
