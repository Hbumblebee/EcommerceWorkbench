/*
 * 功能说明：读取店小秘速卖通全托管（smtChoice）产品编辑页变种 SKU（唯一），供定价后按原 SKU 回写。
 * 主要职责：从维护的 edit 链接解析产品 ID，带 Cookie 调用 choiceProduct/edit.json，解析变种信息。
 * 创建日期：2026-09-13
 */

using System.Text.Json;

namespace EcommerceWorkbench.Services.Dianxiaomi;

public sealed class ChoiceProductDetail
{
    public string Id { get; init; } = "";
    public string EditUrl { get; init; } = "";
    public string? Name { get; init; }
    public List<ChoiceVariantSkuGroup> UniqueSkus { get; init; } = [];
}

public sealed class ChoiceVariantSkuGroup
{
    public string PageSku { get; init; } = "";
    public int VariantCount { get; init; }
}

public sealed class ChoiceProductService
{
    public const string EditJsonUrl = "https://www.dianxiaomi.com/api/choiceProduct/edit.json";

    private readonly ApiClient _apiClient;

    public ChoiceProductService(ApiClient apiClient)
    {
        _apiClient = apiClient;
    }

    public static bool TryParseProduct(string? raw, out string id, out string editUrl)
    {
        id = "";
        editUrl = "";
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        raw = raw.Trim();
        if (LooksLikeId(raw))
        {
            id = raw;
            editUrl = BuildEditUrl(id);
            return true;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || uri.Host.IndexOf("dianxiaomi.com", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        var queryId = GetQueryValue(uri.Query, "id");
        if (string.IsNullOrWhiteSpace(queryId))
            return false;

        id = queryId;
        editUrl = BuildEditUrl(id);
        return true;
    }

    public async Task<ChoiceProductDetail> GetDetailAsync(
        string productId,
        string cookieHeader,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(productId))
            throw new InvalidOperationException("产品 ID 为空。");
        if (string.IsNullOrWhiteSpace(cookieHeader))
            throw new InvalidOperationException("请先导出 Cookie。");

        var editUrl = BuildEditUrl(productId);
        var apiUrl = EditJsonUrl + "?id=" + Uri.EscapeDataString(productId);
        var (status, body) = await _apiClient.SendAsync(
            "GET",
            apiUrl,
            cookieHeader,
            body: null,
            contentType: "application/json",
            cancellationToken,
            referer: editUrl);

        if (status is < 200 or >= 300)
            throw new InvalidOperationException($"HTTP {status}：{Truncate(body, 300)}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var code = root.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.Number
            ? codeEl.GetInt32()
            : -1;
        var msg = root.TryGetProperty("msg", out var msgEl) ? msgEl.GetString() : null;
        if (code != 0)
            throw new InvalidOperationException($"接口返回失败 code={code}，msg={msg ?? "未知错误"}");

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("接口未返回产品数据。");

        var product = data.TryGetProperty("product", out var productEl) && productEl.ValueKind == JsonValueKind.Object
            ? productEl
            : data;

        var variants = ReadVariants(product);
        var unique = DistinctSkus(variants);
        if (unique.Count == 0)
            throw new InvalidOperationException("未在【变种信息】中读到 SKU。请确认该产品已填写变种 SKU。");

        return new ChoiceProductDetail
        {
            Id = productId,
            EditUrl = editUrl,
            Name = GetString(product, "subject", "name"),
            UniqueSkus = unique
        };
    }

    private static List<ChoiceVariantSkuGroup> DistinctSkus(IEnumerable<string> skus)
    {
        var result = new List<ChoiceVariantSkuGroup>();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var sku in skus)
        {
            var key = sku.Trim();
            if (key.Length == 0)
                continue;
            if (index.TryGetValue(key, out var i))
            {
                var existing = result[i];
                result[i] = new ChoiceVariantSkuGroup
                {
                    PageSku = existing.PageSku,
                    VariantCount = existing.VariantCount + 1
                };
            }
            else
            {
                index[key] = result.Count;
                result.Add(new ChoiceVariantSkuGroup { PageSku = key, VariantCount = 1 });
            }
        }

        return result;
    }

    private static List<string> ReadVariants(JsonElement product)
    {
        if (product.TryGetProperty("variationList", out var variants) && variants.ValueKind == JsonValueKind.Array)
            return ReadSkuColumn(variants);

        foreach (var name in new[] { "variationJson", "variationListStr" })
        {
            if (!product.TryGetProperty(name, out var jsonEl)
                || jsonEl.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(jsonEl.GetString()))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(jsonEl.GetString()!);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    return ReadSkuColumn(doc.RootElement);
            }
            catch (JsonException)
            {
                // 忽略无法解析的变种 JSON
            }
        }

        return [];
    }

    private static List<string> ReadSkuColumn(JsonElement array)
    {
        var list = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;
            var sku = GetString(item, "skuCode", "sku");
            if (!string.IsNullOrWhiteSpace(sku))
                list.Add(sku);
        }

        return list;
    }

    private static string BuildEditUrl(string id) =>
        "https://www.dianxiaomi.com/web/smtChoice/edit?id=" + Uri.EscapeDataString(id);

    private static bool LooksLikeId(string raw)
    {
        if (raw.Length is < 1 or > 40)
            return false;
        foreach (var ch in raw)
        {
            if (!char.IsAsciiDigit(ch))
                return false;
        }

        return true;
    }

    private static string GetQueryValue(string query, string name)
    {
        if (string.IsNullOrEmpty(query))
            return "";

        var q = query.TrimStart('?');
        foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var key = eq < 0 ? part : part[..eq];
            if (!key.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;
            var value = eq < 0 ? "" : part[(eq + 1)..];
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }

        return "";
    }

    private static string GetString(JsonElement el, params string[] names)
    {
        foreach (var name in names)
        {
            if (!el.TryGetProperty(name, out var prop) || prop.ValueKind == JsonValueKind.Null)
                continue;
            return prop.ValueKind == JsonValueKind.String ? (prop.GetString() ?? "") : prop.ToString();
        }

        return "";
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";
}
