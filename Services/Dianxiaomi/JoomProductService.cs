/*
 * 功能说明：读取店小秘 JOOM 产品编辑页变种 SKU（唯一），供定价后按原 SKU 回写。
 * 主要职责：从维护的 edit 链接解析产品 ID，带 Cookie 调用 edit.json，解析变种信息。
 * 创建日期：2026-09-05
 */

using System.Text.Json;

namespace EcommerceWorkbench.Services.Dianxiaomi;

public sealed class JoomProductDetail
{
    public string Id { get; init; } = "";
    public string EditUrl { get; init; } = "";
    public int? State { get; init; }
    public string? Name { get; init; }
    public List<JoomVariantSkuGroup> UniqueSkus { get; init; } = [];
}

public sealed class JoomVariantSkuGroup
{
    public string PageSku { get; init; } = "";
    public int VariantCount { get; init; }
}

public sealed class JoomProductService
{
    public const string EditJsonUrl = "https://www.dianxiaomi.com/api/joomProduct/edit.json";

    private readonly ApiClient _apiClient;

    public JoomProductService(ApiClient apiClient)
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

    public async Task<JoomProductDetail> GetDetailAsync(
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

        return new JoomProductDetail
        {
            Id = productId,
            EditUrl = editUrl,
            State = GetInt(product, "state"),
            Name = GetString(product, "name"),
            UniqueSkus = unique
        };
    }

    private static List<JoomVariantSkuGroup> DistinctSkus(IEnumerable<string> skus)
    {
        var result = new List<JoomVariantSkuGroup>();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var sku in skus)
        {
            var key = sku.Trim();
            if (key.Length == 0)
                continue;
            if (index.TryGetValue(key, out var i))
            {
                var existing = result[i];
                result[i] = new JoomVariantSkuGroup
                {
                    PageSku = existing.PageSku,
                    VariantCount = existing.VariantCount + 1
                };
            }
            else
            {
                index[key] = result.Count;
                result.Add(new JoomVariantSkuGroup { PageSku = key, VariantCount = 1 });
            }
        }

        return result;
    }

    private static List<string> ReadVariants(JsonElement product)
    {
        if (product.TryGetProperty("variants", out var variants) && variants.ValueKind == JsonValueKind.Array)
            return ReadSkuColumn(variants);

        if (product.TryGetProperty("variantJson", out var jsonEl)
            && jsonEl.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(jsonEl.GetString()))
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonEl.GetString()!);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    return ReadSkuColumn(doc.RootElement);
            }
            catch (JsonException)
            {
                // 忽略无法解析的 variantJson，按无变种处理
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
            var sku = GetString(item, "sku");
            if (!string.IsNullOrWhiteSpace(sku))
                list.Add(sku);
        }

        return list;
    }

    private static string BuildEditUrl(string id) =>
        "https://www.dianxiaomi.com/web/joomProduct/edit?id=" + Uri.EscapeDataString(id);

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

    private static string GetString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop) || prop.ValueKind == JsonValueKind.Null)
            return "";
        return prop.ValueKind == JsonValueKind.String ? (prop.GetString() ?? "") : prop.ToString();
    }

    private static int? GetInt(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop) || prop.ValueKind == JsonValueKind.Null)
            return null;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var n))
            return n;
        if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var s))
            return s;
        return null;
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";
}
