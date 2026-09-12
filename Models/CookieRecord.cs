/*
 * 功能说明：Cookie 持久化数据模型，用于短期保存浏览器登录会话。
 * 主要职责：描述单条 Cookie 及导出文件结构。
 * 创建日期：2026-07-23
 * 修改记录：2026-08-14 迁入 EcommerceWorkbench
 */

namespace EcommerceWorkbench.Models;

/// <summary>
/// 单条 Cookie 记录。
/// </summary>
public sealed class CookieRecord
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string Path { get; set; } = "/";
    public bool IsHttpOnly { get; set; }
    public bool IsSecure { get; set; }
    public double? ExpiresUtcTicks { get; set; }
}

/// <summary>
/// Cookie 导出文件根对象。
/// </summary>
public sealed class CookieExportFile
{
    public string Site { get; set; } = "https://www.dianxiaomi.com";
    public DateTime ExportedAtUtc { get; set; }
    public List<CookieRecord> Cookies { get; set; } = [];
}
