/*
 * 功能说明：店小秘商品搜索结果行模型（解析 pageList 接口后用于界面展示）。
 * 创建日期：2026-07-23
 * 修改记录：2026-08-14 迁入 EcommerceWorkbench
 */

namespace EcommerceWorkbench.Models;

/// <summary>
/// 搜索结果表格行。
/// </summary>
public sealed class ProductResultRow
{
    public string Sku { get; set; } = string.Empty;
    public string Spu { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public decimal? Price { get; set; }
    public decimal? Weight { get; set; }
    public string Size { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string ImgUrl { get; set; } = string.Empty;
}
