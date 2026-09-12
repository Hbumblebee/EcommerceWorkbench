/*
 * 功能说明：店小秘 Cookie 本地读写与 Cookie 请求头拼接。
 * 主要职责：保存/加载导出文件，生成 HttpClient 可用的 Cookie 字符串。
 * 创建日期：2026-07-23
 * 修改记录：2026-08-14 迁入 EcommerceWorkbench，数据目录改为独立路径
 */

using System.IO;
using System.Text.Json;
using EcommerceWorkbench.Models;

namespace EcommerceWorkbench.Services.Dianxiaomi;

/// <summary>
/// Cookie 本地存储服务。
/// </summary>
public sealed class CookieStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// 默认 Cookie 文件路径（用户本地 AppData）。
    /// </summary>
    public static string DefaultFilePath => Path.Combine(AppPaths.DataDirectory, "cookies.json");

    /// <summary>
    /// 将导出对象写入文件。
    /// </summary>
    public void Save(CookieExportFile file, string path)
    {
        var json = JsonSerializer.Serialize(file, JsonOptions);
        File.WriteAllText(path, json);
    }

    /// <summary>
    /// 从文件加载 Cookie 导出对象；文件不存在时返回 null。
    /// </summary>
    public CookieExportFile? Load(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<CookieExportFile>(json, JsonOptions);
    }

    /// <summary>
    /// 将 Cookie 列表拼接为 HTTP Cookie 请求头。
    /// </summary>
    public static string ToCookieHeader(IEnumerable<CookieRecord> cookies)
    {
        return string.Join("; ", cookies
            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .Select(c => $"{c.Name}={c.Value}"));
    }
}
