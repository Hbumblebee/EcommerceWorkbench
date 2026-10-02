/*
 * 功能说明：读取店小秘 JOOM 产品编辑页变种 SKU（唯一），供定价后按原 SKU 回写。
 * 主要职责：从维护的 edit 链接解析产品 ID，带 Cookie 调用 edit.json，解析变种信息；
 *           发布前按变种 SKU 精确查询采集箱、待发布、在线产品，返回已占用的产品与编辑链接。
 * 创建日期：2026-09-05
 * 修改记录：2026-09-28 增加 SKU 占用查询（采集箱 / 待发布 / 在线）
 *           2026-09-28 占用结果带上店铺名称
 *           2026-09-28 SKU 占用改为检查全部店铺
 *           2026-10-02 支持以 + 分隔的组合 SKU 逐项精确检查
 */

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace EcommerceWorkbench.Services.Dianxiaomi;

public sealed class JoomProductDetail
{
    public string Id { get; init; } = "";
    public string EditUrl { get; init; } = "";
    public int? State { get; init; }
    public string? Name { get; init; }
    public string? ShopId { get; init; }
    public List<JoomVariantSkuGroup> UniqueSkus { get; init; } = [];
}

/// <summary>某个 SKU 已被其他 JOOM 产品占用时的一条记录。</summary>
public sealed class JoomSkuOccupancy
{
    public string Sku { get; init; } = "";
    public string Location { get; init; } = "";
    public string ProductName { get; init; } = "";
    public string ProductId { get; init; } = "";
    public string EditUrl { get; init; } = "";
    public string ParentSku { get; init; } = "";
    public string ShopName { get; init; } = "";
}

public sealed class JoomSkuOccupancyReport
{
    public List<JoomSkuOccupancy> Hits { get; init; } = [];
    public List<string> FailedLocations { get; init; } = [];
}

public sealed class JoomVariantSkuGroup
{
    public string PageSku { get; init; } = "";
    public int VariantCount { get; init; }
}

public sealed class JoomProductService
{
    public const string EditJsonUrl = "https://www.dianxiaomi.com/api/joomProduct/edit.json";
    public const string PageListUrl = "https://www.dianxiaomi.com/api/joomProduct/pageList.json";
    public const string UserInfoUrl = "https://www.dianxiaomi.com/api/userIn.json";
    private const int PageSize = 100;
    private const int MaxPagesPerLocation = 5;
    private const int EditFallbackBudget = 40;

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
            ShopId = NormalizeShopId(GetString(product, "shopId")),
            UniqueSkus = unique
        };
    }

    /// <summary>
    /// 按变种 SKU 精确查找已被占用的产品。检查全部店铺；当前产品自身不算占用。
    /// 查询范围与店小秘发布校验一致：采集箱、待发布（含发布中/失败/定时）、在线各状态。
    /// </summary>
    public async Task<JoomSkuOccupancyReport> FindOccupanciesAsync(
        IReadOnlyList<string> skus,
        string? currentProductId,
        string cookieHeader,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cookieHeader))
            throw new InvalidOperationException("请先导出 Cookie。");

        var wanted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sku in ExpandCompositeSkus(skus))
        {
            var key = (sku ?? "").Trim();
            if (key.Length == 0 || key.Contains(','))
                continue;
            wanted.TryAdd(key, key);
        }

        if (wanted.Count == 0)
            return new JoomSkuOccupancyReport();

        var searchValue = string.Join(",", wanted.Keys);
        var shopNames = await LoadShopNamesAsync(cookieHeader, cancellationToken);
        var currentId = (currentProductId ?? "").Trim();
        var hits = new ConcurrentBag<JoomSkuOccupancy>();
        var failures = new ConcurrentBag<string>();
        var completed = new ConcurrentBag<string>();
        var seen = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var editBudget = EditFallbackBudget;

        var tasks = Locations.Select(location => ScanLocationAsync(
            location,
            searchValue,
            shopNames,
            currentId,
            wanted,
            cookieHeader,
            hits,
            failures,
            completed,
            seen,
            () => Interlocked.Decrement(ref editBudget) >= 0,
            cancellationToken));

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            var auth = Flatten(ex).FirstOrDefault(e => IsAuthFailure(e.Message));
            if (auth is not null)
                throw auth;
        }

        var failedLocations = failures
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        if (hits.IsEmpty && completed.IsEmpty && failedLocations.Count > 0)
            throw new InvalidOperationException("SKU 占用检查没有完成：" + string.Join("、", failedLocations) + "。");

        var ordered = hits
            .OrderBy(h => h.Sku, StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h.ShopName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h.Location, StringComparer.Ordinal)
            .ThenBy(h => h.ProductName, StringComparer.Ordinal)
            .ToList();

        return new JoomSkuOccupancyReport
        {
            Hits = ordered,
            FailedLocations = failedLocations
        };
    }

    private async Task ScanLocationAsync(
        JoomListLocation location,
        string searchValue,
        IReadOnlyDictionary<string, string> shopNames,
        string currentProductId,
        Dictionary<string, string> wanted,
        string cookieHeader,
        ConcurrentBag<JoomSkuOccupancy> hits,
        ConcurrentBag<string> failures,
        ConcurrentBag<string> completed,
        ConcurrentDictionary<string, byte> seen,
        Func<bool> tryConsumeEditFallback,
        CancellationToken cancellationToken)
    {
        try
        {
            var pageNo = 1;
            while (pageNo <= MaxPagesPerLocation)
            {
                var body = BuildListBody(pageNo, searchValue, shopId: null, location.Extra);
                var (status, responseBody) = await _apiClient.SendAsync(
                    "POST",
                    PageListUrl,
                    cookieHeader,
                    body,
                    "application/x-www-form-urlencoded",
                    cancellationToken,
                    referer: "https://www.dianxiaomi.com" + location.RefererPath);

                if (status is < 200 or >= 300)
                    throw new InvalidOperationException($"{location.Label} HTTP {status}：{Truncate(responseBody, 180)}");

                using var doc = JsonDocument.Parse(responseBody);
                var root = doc.RootElement;
                var code = root.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.Number
                    ? codeEl.GetInt32()
                    : -1;
                var msg = root.TryGetProperty("msg", out var msgEl) ? msgEl.GetString() : null;
                if (code != 0)
                    throw new InvalidOperationException($"{location.Label} 接口返回失败 code={code}，msg={msg ?? "未知错误"}");

                if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                    break;

                if (!TryReadPage(data, out var list, out var totalPage))
                    break;

                foreach (var item in list.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        continue;

                    var productId = FirstNonEmpty(GetString(item, "idStr"), GetString(item, "id"));
                    if (productId.Length == 0)
                        continue;
                    if (currentProductId.Length > 0
                        && productId.Equals(currentProductId, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var itemShop = NormalizeShopId(GetString(item, "shopId"));
                    var visible = ExpandCompositeSkus(ReadVisibleSkus(item));
                    if (!visible.Any(wanted.ContainsKey))
                    {
                        if (!tryConsumeEditFallback())
                        {
                            failures.Add("部分命中未核对");
                            continue;
                        }

                        visible = ExpandCompositeSkus(
                            await TryReadEditSkusAsync(productId, cookieHeader, cancellationToken));
                    }

                    var parentSku = GetString(item, "parentSku").Trim();
                    var name = FirstNonEmpty(GetString(item, "name"), GetString(item, "title"));
                    var resolvedShop = itemShop;
                    var shopName = "";
                    if (resolvedShop is not null && shopNames.TryGetValue(resolvedShop, out var foundShopName))
                        shopName = foundShopName;
                    foreach (var found in visible)
                    {
                        if (!wanted.TryGetValue(found, out var displaySku))
                            continue;
                        var dedupeKey = displaySku + "\n" + productId + "\n" + location.Label;
                        if (!seen.TryAdd(dedupeKey, 0))
                            continue;
                        hits.Add(new JoomSkuOccupancy
                        {
                            Sku = displaySku,
                            Location = location.Label,
                            ProductName = name,
                            ProductId = productId,
                            EditUrl = BuildEditUrl(productId),
                            ParentSku = parentSku.Equals(displaySku, StringComparison.OrdinalIgnoreCase) ? "" : parentSku,
                            ShopName = shopName
                        });
                    }
                }

                if (pageNo >= totalPage || list.GetArrayLength() == 0)
                    break;
                pageNo++;
            }

            completed.Add(location.Label);
        }
        catch (Exception ex) when (!IsAuthFailure(ex.Message))
        {
            failures.Add(location.Label);
        }
    }

    private async Task<List<string>> TryReadEditSkusAsync(
        string productId,
        string cookieHeader,
        CancellationToken cancellationToken)
    {
        try
        {
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
                return [];

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var code = root.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.Number
                ? codeEl.GetInt32()
                : -1;
            if (code != 0 || !root.TryGetProperty("data", out var data))
                return [];

            var product = data.TryGetProperty("product", out var productEl) && productEl.ValueKind == JsonValueKind.Object
                ? productEl
                : data;
            return ReadVariants(product);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task<Dictionary<string, string>> LoadShopNamesAsync(
        string cookieHeader,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var (status, body) = await _apiClient.SendAsync(
                "GET",
                UserInfoUrl,
                cookieHeader,
                body: null,
                contentType: "application/json",
                cancellationToken,
                referer: "https://www.dianxiaomi.com/web/joomProduct/online");
            if (status is < 200 or >= 300)
                return map;

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var data = root;
            if (root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Object)
                data = dataEl;
            if (!data.TryGetProperty("shopMap", out var shopMap) || shopMap.ValueKind != JsonValueKind.Object)
                return map;

            foreach (var prop in shopMap.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object)
                    continue;
                var name = FirstNonEmpty(GetString(prop.Value, "name"), GetString(prop.Value, "shopName"));
                if (name.Length == 0)
                    continue;
                RememberShopName(map, prop.Name, name);
                RememberShopName(map, GetString(prop.Value, "idStr"), name);
                RememberShopName(map, GetString(prop.Value, "id"), name);
            }
        }
        catch (JsonException)
        {
            // 店铺名解析失败时，提示里退回「当前店铺」
        }
        catch (Exception ex) when (!IsAuthFailure(ex.Message))
        {
            // 店铺列表失败不阻断 SKU 占用检查
        }

        return map;
    }

    private static void RememberShopName(Dictionary<string, string> map, string? rawId, string name)
    {
        var id = NormalizeShopId(rawId);
        if (id is not null)
            map.TryAdd(id, name);
    }

    private static bool TryReadPage(JsonElement data, out JsonElement list, out int totalPage)
    {
        list = default;
        totalPage = 1;
        var page = data;
        if (data.TryGetProperty("page", out var pageEl) && pageEl.ValueKind == JsonValueKind.Object)
            page = pageEl;

        if (page.TryGetProperty("list", out var listEl) && listEl.ValueKind == JsonValueKind.Array)
            list = listEl;
        else if (data.TryGetProperty("list", out var dataList) && dataList.ValueKind == JsonValueKind.Array)
            list = dataList;
        else
            return false;

        totalPage = GetInt(page, "totalPage") ?? GetInt(data, "totalPage") ?? 1;
        if (totalPage < 1)
            totalPage = 1;
        return true;
    }

    private static List<string> ReadVisibleSkus(JsonElement item)
    {
        var list = new List<string>();
        if (item.TryGetProperty("variants", out var variants) && variants.ValueKind == JsonValueKind.Array)
            list.AddRange(ReadSkuColumn(variants));
        else if (item.TryGetProperty("variants", out var variantText)
                 && variantText.ValueKind == JsonValueKind.String
                 && !string.IsNullOrWhiteSpace(variantText.GetString()))
        {
            list.AddRange(ParseSkuJson(variantText.GetString()));
        }

        if (item.TryGetProperty("variantJson", out var jsonEl)
            && jsonEl.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(jsonEl.GetString()))
        {
            list.AddRange(ParseSkuJson(jsonEl.GetString()));
        }

        var ownSku = GetString(item, "sku").Trim();
        if (ownSku.Length > 0)
            list.Add(ownSku);
        var parentSku = GetString(item, "parentSku").Trim();
        if (parentSku.Length > 0)
            list.Add(parentSku);
        return list;
    }

    /// <summary>
    /// 店小秘部分列表会把多个变种 SKU 合并为「SKU1+SKU2+…」返回。
    /// 占用检测按每个组成 SKU 精确比较，同时兼容普通单 SKU。
    /// </summary>
    private static List<string> ExpandCompositeSkus(IEnumerable<string> values)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            foreach (var part in (value ?? "").Split(
                         '+',
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (part.Length > 0 && seen.Add(part))
                    result.Add(part);
            }
        }

        return result;
    }

    private static List<string> ParseSkuJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                return ReadSkuColumn(doc.RootElement);
        }
        catch (JsonException)
        {
            // 列表里的变种 JSON 无法解析时，改走编辑页读取
        }

        return [];
    }

    private static string BuildListBody(
        int pageNo,
        string searchValue,
        string? shopId,
        IReadOnlyDictionary<string, string> extra)
    {
        var sb = new StringBuilder();
        Append(sb, "pageNo", pageNo.ToString(CultureInfo.InvariantCulture));
        Append(sb, "pageSize", PageSize.ToString(CultureInfo.InvariantCulture));
        Append(sb, "searchType", "1");
        Append(sb, "searchValue", searchValue);
        Append(sb, "productSearchType", "1");
        Append(sb, "shopId", shopId ?? "-1");
        Append(sb, "shopGroupId", "");
        Append(sb, "fullCid", "");
        foreach (var pair in extra)
            Append(sb, pair.Key, pair.Value);
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, string key, string value)
    {
        if (sb.Length > 0)
            sb.Append('&');
        sb.Append(WebUtility.UrlEncode(key));
        sb.Append('=');
        sb.Append(WebUtility.UrlEncode(value));
    }

    private static string? NormalizeShopId(string? raw)
    {
        raw = (raw ?? "").Trim();
        if (raw.Length == 0 || raw is "0" or "-1")
            return null;
        return raw;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            var text = (value ?? "").Trim();
            if (text.Length > 0)
                return text;
        }

        return "";
    }

    private static bool IsAuthFailure(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;
        return message.Contains("验证失败", StringComparison.OrdinalIgnoreCase)
               || message.Contains("code=2001", StringComparison.OrdinalIgnoreCase)
               || message.Contains("未登录", StringComparison.OrdinalIgnoreCase)
               || (message.Contains("登录", StringComparison.OrdinalIgnoreCase)
                   && message.Contains("失效", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<Exception> Flatten(Exception ex)
    {
        if (ex is AggregateException aggregate)
        {
            foreach (var inner in aggregate.Flatten().InnerExceptions)
                yield return inner;
            yield break;
        }

        yield return ex;
    }

    private readonly record struct JoomListLocation(
        string Label,
        string RefererPath,
        IReadOnlyDictionary<string, string> Extra);

    private static readonly JoomListLocation[] Locations =
    [
        new("采集箱", "/web/joomProduct/draft", new Dictionary<string, string>
        {
            ["state"] = "-3",
            ["productState"] = "-3"
        }),
        new("待发布", "/web/joomProduct/offline", new Dictionary<string, string>
        {
            ["state"] = "0",
            ["productState"] = "0"
        }),
        new("发布中", "/web/joomProduct/offline", new Dictionary<string, string>
        {
            ["state"] = "1",
            ["productState"] = "0"
        }),
        new("发布失败", "/web/joomProduct/offline", new Dictionary<string, string>
        {
            ["state"] = "2",
            ["productState"] = "0"
        }),
        new("定时发布", "/web/joomProduct/offline", new Dictionary<string, string>
        {
            ["state"] = "-2",
            ["productState"] = "0"
        }),
        new("在线·在售", "/web/joomProduct/online", new Dictionary<string, string>
        {
            ["state"] = "3",
            ["productState"] = "1",
            ["joomState"] = "active,warning,locked"
        }),
        new("在线·审核中", "/web/joomProduct/online", new Dictionary<string, string>
        {
            ["productState"] = "1",
            ["joomState"] = "pending"
        }),
        new("在线·已拒绝", "/web/joomProduct/online", new Dictionary<string, string>
        {
            ["productState"] = "1",
            ["joomState"] = "rejected"
        }),
        new("在线·已下架", "/web/joomProduct/online", new Dictionary<string, string>
        {
            ["state"] = "4",
            ["productState"] = "1",
            ["joomState"] = "disabledByJoom,disabledByMerchant"
        }),
        new("在线·疑似删除", "/web/joomProduct/online", new Dictionary<string, string>
        {
            ["state"] = "10",
            ["productState"] = "1"
        })
    ];

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
