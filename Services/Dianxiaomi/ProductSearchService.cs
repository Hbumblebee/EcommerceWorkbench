/*
 * 功能说明：店小秘商品 pageList 搜索服务——参数拼接、单次请求、结果解析。
 * 主要职责：≤200 个 SKU 仅发送一次请求；命中结果按输入顺序展示，并列出未命中 SKU。
 * 创建日期：2026-07-23
 * 修改记录：2026-08-14 迁入 EcommerceWorkbench
 *           2026-08-15 参考价四舍五入保留两位小数
 */

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using EcommerceWorkbench.Models;

namespace EcommerceWorkbench.Services.Dianxiaomi;

/// <summary>
/// 商品搜索服务。
/// </summary>
public sealed class ProductSearchService
{
    public const string DefaultApiUrl = "https://www.dianxiaomi.com/api/dxmCommodityProduct/pageList.json";
    public const int MaxSearchValuesPerRequest = 200;
    public const int PageSize = 200;

    private readonly ApiClient _apiClient;

    public ProductSearchService(ApiClient apiClient)
    {
        _apiClient = apiClient;
    }

    /// <summary>
    /// 解析用户输入的搜索值（支持逗号、换行、中文逗号分隔），保持输入顺序。
    /// </summary>
    public static List<string> ParseSearchValues(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var part in raw
                     .Replace('，', ',')
                     .Split([',', '\r', '\n', '\t', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrWhiteSpace(part))
            {
                continue;
            }

            if (seen.Add(part))
            {
                result.Add(part);
            }
        }

        return result;
    }

    /// <summary>
    /// 一次请求查询全部 SKU（最多 200 个）；命中结果严格按输入顺序返回，并收集未命中 SKU。
    /// </summary>
    public async Task<ProductSearchOutcome> SearchAsync(
        IReadOnlyList<string> searchValues,
        string cookieHeader,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (searchValues.Count == 0)
        {
            throw new InvalidOperationException("请至少填写一个搜索值。");
        }

        if (searchValues.Count > MaxSearchValuesPerRequest)
        {
            throw new InvalidOperationException($"单次最多搜索 {MaxSearchValuesPerRequest} 个 SKU，当前为 {searchValues.Count} 个。");
        }

        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            throw new InvalidOperationException("请先导出 Cookie。");
        }

        progress?.Report($"一次请求查询 {searchValues.Count} 个 SKU…");

        var body = BuildFormBody(pageNo: 1, searchValues);
        var (status, responseBody) = await _apiClient.SendAsync(
            "POST",
            DefaultApiUrl,
            cookieHeader,
            body,
            "application/x-www-form-urlencoded",
            cancellationToken);

        if (status is < 200 or >= 300)
        {
            throw new InvalidOperationException($"HTTP {status}：{Truncate(responseBody, 300)}");
        }

        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;
        var code = root.TryGetProperty("code", out var codeEl) ? codeEl.GetInt32() : -1;
        var msg = root.TryGetProperty("msg", out var msgEl) ? msgEl.GetString() : null;

        if (code != 0)
        {
            throw new InvalidOperationException($"接口返回失败 code={code}，msg={msg ?? "未知错误"}");
        }

        var rows = new List<ProductResultRow>();
        if (root.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("page", out var page)
            && page.ValueKind == JsonValueKind.Object
            && page.TryGetProperty("list", out var list)
            && list.ValueKind == JsonValueKind.Array)
        {
            rows.AddRange(ParseList(list));
        }

        var (ordered, missed) = OrderHitsByInput(searchValues, rows);

        return new ProductSearchOutcome
        {
            Rows = ordered,
            MissedSkus = missed,
            RawResponse = $"[single-request, count={searchValues.Count}, http={status}]\n{responseBody}",
            RequestedValueCount = searchValues.Count,
            BatchCount = 1
        };
    }

    /// <summary>
    /// 仅保留命中输入的商品，并严格按输入 SKU 顺序排列；未命中的输入值单独列出。
    /// </summary>
    private static (List<ProductResultRow> OrderedHits, List<string> Missed) OrderHitsByInput(
        IReadOnlyList<string> searchValues,
        List<ProductResultRow> rows)
    {
        var bySku = new Dictionary<string, Queue<ProductResultRow>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var key = NormalizeSku(row.Sku);
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            if (!bySku.TryGetValue(key, out var queue))
            {
                queue = new Queue<ProductResultRow>();
                bySku[key] = queue;
            }

            queue.Enqueue(row);
        }

        var orderedHits = new List<ProductResultRow>(searchValues.Count);
        var missed = new List<string>();
        var usedIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var value in searchValues)
        {
            var key = NormalizeSku(value);
            if (!bySku.TryGetValue(key, out var queue) || queue.Count == 0)
            {
                missed.Add(value);
                continue;
            }

            while (queue.Count > 0)
            {
                var row = queue.Dequeue();
                var id = string.IsNullOrEmpty(row.Id) ? $"{key}|{orderedHits.Count}" : row.Id;
                if (usedIds.Add(id))
                {
                    orderedHits.Add(row);
                }
            }
        }

        return (orderedHits, missed);
    }

    private static string NormalizeSku(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Trim().Normalize(NormalizationForm.FormKC);
    }

    /// <summary>
    /// 拼接 pageList 表单参数；searchValue 多个用英文逗号拼接。
    /// </summary>
    public static string BuildFormBody(int pageNo, IReadOnlyList<string> searchValues)
    {
        var searchValue = string.Join(",", searchValues);
        var sb = new StringBuilder();
        Append(sb, "pageNo", pageNo.ToString(CultureInfo.InvariantCulture));
        Append(sb, "pageSize", PageSize.ToString(CultureInfo.InvariantCulture));
        Append(sb, "searchType", "1");
        Append(sb, "searchValue", searchValue);
        Append(sb, "saleMode", "-1");
        Append(sb, "productMode", "-1");
        Append(sb, "productPxId", "1");
        Append(sb, "productPxSxId", "0");
        Append(sb, "fullCid", string.Empty);
        Append(sb, "productSearchType", "2");
        Append(sb, "productGroupLxId", "1");
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, string key, string value)
    {
        if (sb.Length > 0)
        {
            sb.Append('&');
        }

        sb.Append(WebUtility.UrlEncode(key));
        sb.Append('=');
        sb.Append(WebUtility.UrlEncode(value));
    }

    private static IEnumerable<ProductResultRow> ParseList(JsonElement list)
    {
        foreach (var group in list.EnumerateArray())
        {
            var spu = group.TryGetProperty("spu", out var spuEl) ? spuEl.ToString() : string.Empty;
            if (!group.TryGetProperty("dxmCommodityProductList", out var products)
                || products.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var p in products.EnumerateArray())
            {
                var length = GetDecimal(p, "length");
                var width = GetDecimal(p, "width");
                var height = GetDecimal(p, "height");
                yield return new ProductResultRow
                {
                    Id = GetString(p, "idStr", "id"),
                    Sku = GetString(p, "sku"),
                    Spu = string.IsNullOrWhiteSpace(GetString(p, "spu")) ? spu : GetString(p, "spu"),
                    Name = GetString(p, "name"),
                    Price = RoundMoney(GetDecimal(p, "price")),
                    Weight = GetDecimal(p, "weight"),
                    Size = $"{length:0.##}×{width:0.##}×{height:0.##}",
                    FullName = GetString(p, "fullName"),
                    ImgUrl = GetString(p, "imgUrl")
                };
            }
        }
    }

    private static string GetString(JsonElement el, params string[] names)
    {
        foreach (var name in names)
        {
            if (el.TryGetProperty(name, out var prop) && prop.ValueKind != JsonValueKind.Null)
            {
                return prop.ToString();
            }
        }

        return string.Empty;
    }

    private static decimal? GetDecimal(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop) || prop.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.Number => prop.TryGetDecimal(out var d) ? d : null,
            JsonValueKind.String when decimal.TryParse(prop.GetString(), out var d2) => d2,
            _ => null
        };
    }

    private static decimal? RoundMoney(decimal? value)
        => value.HasValue ? Math.Round(value.Value, 2, MidpointRounding.AwayFromZero) : null;

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";
}

/// <summary>
/// 搜索汇总结果。
/// </summary>
public sealed class ProductSearchOutcome
{
    public List<ProductResultRow> Rows { get; init; } = [];
    public List<string> MissedSkus { get; init; } = [];
    public string RawResponse { get; init; } = string.Empty;
    public int RequestedValueCount { get; init; }
    public int BatchCount { get; init; }
}
