/*
 * 功能说明：把 JOOM 重复 SKU 拆成基础货号，再按通用后缀、颜色后缀拼出候选 SKU。
 * 主要职责：解析中英文逗号分隔的后缀列表；按需去掉后缀（保留颜色）或去掉整个后缀；按「基础 + 颜色 + 通用」拼接。
 * 创建日期：2026-09-29
 * 修改记录：2026-10-02 支持组合 SKU 逐项去除后缀
 *           2026-10-02 兼容全角加号与连字符
 *           2026-10-07 新增只去末尾通用后缀（保留颜色）的口径，供「搜索SKU」列使用
 *           2026-10-07 新增 SplitParts，供组合 SKU 按段选择颜色后缀
 *           2026-10-08 去掉后缀后补齐清掉残留的「-」（后缀列表里 AS/AS01 不带前导「-」时会留下 12xJ0021-）
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

    /// <summary>
    /// 只去掉末尾的通用后缀，保留颜色后缀。
    /// 店小秘商品库里的货号是带颜色的（<c>4xJ0058-black</c>、<c>12xJ0103-grey</c>），
    /// 把颜色一起去掉（<c>4xJ0058</c>）会查不到参考价；而颜色后缀之后那段
    /// （<c>-001</c>、<c>-1</c>、<c>-AS01</c> 等）才是报价时现加的，应当去掉。
    /// 注意后缀列表里有 <c>AS</c>/<c>AS01</c> 这种不带前导「-」的写法，
    /// 去掉后要再把剩下的分隔符「-」补齐清掉（否则会留下 <c>12xJ0021-</c>）。
    /// </summary>
    public static string StripTrailingSuffix(string? sku, IEnumerable<string>? generalSuffixes)
    {
        var text = NormalizeSeparators(sku);
        if (text.Length == 0)
            return text;

        if (generalSuffixes is not null)
        {
            // 从长到短匹配，避免「-1」抢先命中「-01」而错误地切掉「-0」。
            foreach (var suffix in generalSuffixes
                         .Where(s => !string.IsNullOrEmpty(s))
                         .OrderByDescending(s => s.Length))
            {
                if (!text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    continue;

                var trimmed = text[..^suffix.Length].Trim();
                return trimmed.TrimEnd('-').Trim();
            }
        }

        return text;
    }

    /// <summary>组合 SKU 的每一段各自去掉末尾通用后缀，保留颜色后缀。</summary>
    public static string StripCompositeTrailingSuffix(string? sku, IEnumerable<string>? generalSuffixes)
    {
        var parts = NormalizeSeparators(sku).Split(
            '+',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join("+", parts
            .Select(part => StripTrailingSuffix(part, generalSuffixes))
            .Where(part => part.Length > 0));
    }

    /// <summary>
    /// 拆出组合 SKU 的每一段（保持原顺序，不去后缀）。
    /// 组合 SKU 里颜色后缀通常只出现在最后一段（<c>4xJ0094+4xJ0103-grey-1</c>），
    /// 但启用颜色后缀时需要逐段选择，因此由调用方按段处理。
    /// </summary>
    public static IReadOnlyList<string> SplitParts(string? sku)
    {
        return NormalizeSeparators(sku)
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => part.Length > 0)
            .ToList();
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
