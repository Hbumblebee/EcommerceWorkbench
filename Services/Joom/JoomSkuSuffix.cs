/*
 * 功能说明：把 JOOM 重复 SKU 拆成基础货号，再按通用后缀、颜色后缀拼出候选 SKU。
 * 主要职责：解析中英文逗号分隔的后缀列表；去掉原 SKU 第一个「-」及其后面的内容；按「基础 + 颜色 + 通用」拼接。
 * 创建日期：2026-09-29
 * 修改记录：2026-10-02 支持组合 SKU 逐项去除后缀
 *           2026-10-02 兼容全角加号与连字符
 */

namespace EcommerceWorkbench.Services.Joom;

public static class JoomSkuSuffix
{
    public static IReadOnlyList<string> Parse(string? raw)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(raw))
            return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parts = raw.Split(['，', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            if (part.Length == 0 || !seen.Add(part))
                continue;
            result.Add(part);
        }

        return result;
    }

    /// <summary>「-」及其后内容都视为后缀。没有「-」时保留整个 SKU。</summary>
    public static string StripBase(string? sku)
    {
        var text = NormalizeSeparators(sku);
        var dash = text.IndexOf('-');
        return dash < 0 ? text : text[..dash].Trim();
    }

    /// <summary>
    /// 组合 SKU 按「+」逐项去掉后缀，并保持原顺序。
    /// 例如 J0092-white+J0102-grey 变为 J0092+J0102。
    /// </summary>
    public static string StripCompositeBases(string? sku)
    {
        var parts = NormalizeSeparators(sku).Split(
            '+',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join("+", parts.Select(StripBase).Where(part => part.Length > 0));
    }

    private static string NormalizeSeparators(string? sku)
    {
        return (sku ?? "")
            .Trim()
            .Replace('＋', '+')
            .Replace('－', '-')
            .Replace('–', '-')
            .Replace('—', '-');
    }

    public static string Combine(string baseSku, string? colorSuffix, string generalSuffix)
    {
        if (string.IsNullOrEmpty(colorSuffix))
            return baseSku + generalSuffix;
        return baseSku + colorSuffix + generalSuffix;
    }
}
