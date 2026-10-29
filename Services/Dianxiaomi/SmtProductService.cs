/*
 * 功能说明：读取店小秘速卖通产品编辑页（/web/smt/edit）的变种 SKU，并按当前店铺查重。
 * 主要职责：解析 edit 链接；带 Cookie 调用 smtProduct/edit.json 读取 skuCode；
 *           查重走在线产品列表 smtProduct/pageList.json（searchType=1，SKU 编码精确）。
 *           把在线、草稿箱、已下架、审核中、审核不通过、疑似删除的命中数加总，
 *           合计大于 1 才视为重复；多值按对分法定位。
 * 创建日期：2026-10-22
 * 修改记录：2026-10-22 查重改为在线产品列表各状态合计，超过 1 才算重复
 *           2026-10-24 系统繁忙时重试，失败不再抛成未处理异常
 */

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace EcommerceWorkbench.Services.Dianxiaomi;

public sealed class SmtProductDetail
{
    public string Id { get; init; } = "";
    public string EditUrl { get; init; } = "";
    public string? Name { get; init; }
    /// <summary>当前产品所属店铺；查重只在该店铺内进行。</summary>
    public string ShopId { get; init; } = "";
    public List<SmtVariantSku> Skus { get; init; } = [];
}

public sealed class SmtVariantSku
{
    public string PageSku { get; init; } = "";
    /// <summary>变种属性名，例如「浅灰色(5PCS)」。</summary>
    public string Attr { get; init; } = "";
}

/// <summary>某个 SKU 已被同店铺其它速卖通产品占用时的一条记录。</summary>
public sealed class SmtSkuOccupancy
{
    public string Sku { get; init; } = "";
    public string Location { get; init; } = "";
    public string ProductName { get; init; } = "";
    public string ProductId { get; init; } = "";
    public string EditUrl { get; init; } = "";
}

public sealed class SmtSkuOccupancyReport
{
    public List<SmtSkuOccupancy> Hits { get; init; } = [];
    public List<string> FailedLocations { get; init; } = [];
    public List<string> IncompleteReasons { get; init; } = [];
    public List<string> CompletedLocations { get; init; } = [];
    public string Scope { get; init; } = "";
    public List<string> DetailProbes { get; init; } = [];
}

public sealed class SmtShopOption
{
    public string ShopId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Display => Name.Length == 0 || Name == ShopId ? ShopId : Name + "（" + ShopId + "）";
}

public sealed class SmtProductService
{
    public const string EditJsonUrl = "https://www.dianxiaomi.com/api/smtProduct/edit.json";
    public const string PageListUrl = "https://www.dianxiaomi.com/api/smtProduct/pageList.json";
    private const int PageSize = 50;
    private const int MaxQueriesPerLocation = 400;
    private const int RetryCount = 2;
    private const int MaxDetailProbes = 12;
    private const int ShopScanPages = 5;

    private readonly ApiClient _apiClient;

    /// <summary>
    /// 在线产品列表页上的一个状态栏。dxmState 固定为 online，用 productStatusType 区分绿框里的类型。
    /// </summary>
    public readonly record struct SmtListLocation(
        string Label,
        string DxmState,
        string? DxmOfflineState,
        string? ProductStatusType,
        string RefererPath);

    private const string OnlineReferer = "/web/smt/smtProductList/online?pageType=0";

    /// <summary>在线产品列表绿框里的全部状态，顺序与页面标签一致。</summary>
    public static readonly IReadOnlyList<SmtListLocation> OnlineStatusTabs =
    [
        new("在线", "online", null, "onSelling", OnlineReferer),
        new("草稿箱", "online", null, "draft-box", OnlineReferer),
        new("已下架", "online", null, "offline", OnlineReferer),
        new("审核中", "online", null, "auditing", OnlineReferer),
        new("审核不通过", "online", null, "editingRequired", OnlineReferer),
        new("疑似删除", "online", null, "doubtdelete", OnlineReferer)
    ];

    public static readonly SmtListLocation AllLocations = new("全部状态", "online", null, null, OnlineReferer);

    public SmtProductService(ApiClient apiClient)
    {
        _apiClient = apiClient;
    }

    public static IReadOnlyList<SmtListLocation> ExpandScope(string? scopeKey)
    {
        if (!string.IsNullOrWhiteSpace(scopeKey))
        {
            foreach (var tab in OnlineStatusTabs)
            {
                if (string.Equals(tab.ProductStatusType, scopeKey, StringComparison.OrdinalIgnoreCase))
                    return [tab];
            }
        }

        return OnlineStatusTabs;
    }

    public static string ScopeName(string? scopeKey)
    {
        foreach (var tab in OnlineStatusTabs)
        {
            if (string.Equals(tab.ProductStatusType, scopeKey, StringComparison.OrdinalIgnoreCase))
                return "仅" + tab.Label;
        }

        return AllLocations.Label;
    }

    /// <summary>
    /// 只接受店小秘速卖通产品编辑页 <c>/web/smt/edit?id=</c>，或纯数字产品 id。
    /// 全托管 <c>/web/smtChoice/edit</c> 与半托管 <c>/web/smt/editFromSmt</c> 不在此页处理。
    /// </summary>
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

        var path = uri.AbsolutePath.TrimEnd('/');
        if (!path.Equals("/web/smt/edit", StringComparison.OrdinalIgnoreCase))
            return false;

        var queryId = GetQueryValue(uri.Query, "id");
        if (!LooksLikeId(queryId))
            return false;

        id = queryId;
        editUrl = BuildEditUrl(id);
        return true;
    }

    public async Task<SmtProductDetail> GetDetailAsync(
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
        var code = ReadCode(root);
        var msg = root.TryGetProperty("msg", out var msgEl) ? msgEl.GetString() : null;
        if (code != 0)
            throw new InvalidOperationException($"接口返回失败 code={code}，msg={msg ?? "未知错误"}");

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("接口未返回产品数据。");

        var product = data.TryGetProperty("product", out var productEl) && productEl.ValueKind == JsonValueKind.Object
            ? productEl
            : data;

        var skus = ReadSkus(product);
        if (skus.Count == 0)
            throw new InvalidOperationException("未在【变种信息】中读到 SKU。请确认该产品已填写变种 SKU。");

        return new SmtProductDetail
        {
            Id = productId,
            EditUrl = editUrl,
            Name = GetString(product, "subject", "name"),
            ShopId = NormalizeKey(GetString(product, "shopId")),
            Skus = skus
        };
    }

    /// <summary>
    /// 按变种 SKU 核对是否已被指定店铺的其它速卖通在线产品占用。
    /// 搜索方式与在线产品列表一致（SKU 编码、精确）。把所选状态栏的 totalSize 相加，合计大于 1 才视为重复。
    /// </summary>
    public async Task<SmtSkuOccupancyReport> FindOccupanciesAsync(
        IReadOnlyList<string> skus,
        string? shopId,
        string cookieHeader,
        string? scopeKey = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cookieHeader))
            throw new InvalidOperationException("请先导出 Cookie。");

        var scopeShop = NormalizeKey(shopId);
        var shopLabel = scopeShop.Length == 0 ? "（未识别店铺）" : scopeShop;
        var scopeLabel = ScopeName(scopeKey);
        var scopeLocations = ExpandScope(scopeKey);

        var wanted = new List<string>();
        var seenWanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sku in skus)
        {
            var key = NormalizeKey(sku);
            if (key.Length == 0 || key.Contains(','))
                continue;
            if (seenWanted.Add(key))
                wanted.Add(key);
        }

        if (wanted.Count == 0)
            return new SmtSkuOccupancyReport { Scope = "店铺 " + shopLabel + " · " + scopeLabel };

        var hits = new List<SmtSkuOccupancy>();
        var failures = new List<string>();
        var completed = new List<string>();
        var incomplete = new List<string>();
        var probes = new List<string>();
        var queriesLeft = MaxQueriesPerLocation;

        try
        {
            await ResolveHitsAsync(
                scopeLocations, scopeShop, wanted, cookieHeader, hits, failures, completed, incomplete, probes,
                () => Interlocked.Decrement(ref queriesLeft) >= 0, cancellationToken);
        }
        catch (Exception ex) when (CookieErrorDetector.IsAuthFailure(ex.Message))
        {
            throw;
        }

        if (hits.Count == 0 && completed.Count == 0 && failures.Count > 0)
            incomplete.Add("系统繁忙或接口失败，查重没有完成，请稍后重试");

        return new SmtSkuOccupancyReport
        {
            Hits = hits
                .OrderBy(h => h.Sku, StringComparer.OrdinalIgnoreCase)
                .ThenBy(h => h.Location, StringComparer.Ordinal)
                .ThenBy(h => h.ProductName, StringComparer.Ordinal)
                .ToList(),
            FailedLocations = failures.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList(),
            IncompleteReasons = incomplete.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList(),
            CompletedLocations = completed.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList(),
            Scope = "店铺 " + shopLabel + " · " + scopeLabel,
            DetailProbes = probes.Distinct(StringComparer.Ordinal).Take(MaxDetailProbes).ToList()
        };
    }

    public async Task<List<SmtShopOption>> ListShopsAsync(
        string cookieHeader,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cookieHeader))
            throw new InvalidOperationException("请先导出 Cookie。");

        var shops = new ConcurrentDictionary<string, SmtShopOption>(StringComparer.OrdinalIgnoreCase);
        var tasks = OnlineStatusTabs
            .Select(location => CollectShopsAsync(location, cookieHeader, shops, cancellationToken))
            .ToList();
        await Task.WhenAll(tasks);

        return shops.Values
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.ShopId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 对分法定位重复 SKU。先把各状态栏的总数加总：合计不超过 1 就整批排除；
    /// 合计超过 1 且不止一个 SKU 就拆成两半。单个 SKU 合计大于 1 才记为重复。
    /// </summary>
    private async Task ResolveHitsAsync(
        IReadOnlyList<SmtListLocation> locations,
        string shopId,
        IReadOnlyList<string> candidates,
        string cookieHeader,
        List<SmtSkuOccupancy> hits,
        List<string> failures,
        List<string> completed,
        List<string> incomplete,
        List<string> probes,
        Func<bool> tryConsumeQuery,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
            return;

        if (!tryConsumeQuery())
        {
            incomplete.Add("查询次数已达上限，剩余 SKU 未核对");
            return;
        }

        var searchValue = string.Join(",", candidates);
        var (total, items, batchFailures) = await SearchAcrossAsync(
            locations, shopId, searchValue, cookieHeader, cancellationToken);
        foreach (var failure in batchFailures)
        {
            if (!failures.Contains(failure))
                failures.Add(failure);
            probes.Add(failure);
        }

        foreach (var location in locations)
        {
            if (batchFailures.Any(f => f.StartsWith(location.Label, StringComparison.Ordinal))
                || completed.Contains(location.Label))
            {
                continue;
            }

            completed.Add(location.Label);
        }

        // 合计不超过 1：这些 SKU 至多出现在一个产品上（通常就是当前产品），不算重复。
        if (total <= 1)
            return;

        if (candidates.Count == 1)
        {
            var sku = candidates[0];
            if (items.Count == 0)
            {
                hits.Add(new SmtSkuOccupancy
                {
                    Sku = sku,
                    Location = "合计 " + total,
                    ProductName = "",
                    ProductId = "",
                    EditUrl = ""
                });
                return;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                var key = item.Location + "|" + item.ProductId;
                if (!seen.Add(key))
                    continue;
                hits.Add(new SmtSkuOccupancy
                {
                    Sku = sku,
                    Location = item.Location,
                    ProductName = item.Subject,
                    ProductId = item.ProductId,
                    EditUrl = BuildEditUrl(item.ProductId)
                });
            }

            return;
        }

        var half = candidates.Count / 2;
        await ResolveHitsAsync(
            locations, shopId, candidates.Take(half).ToList(), cookieHeader, hits, failures, completed, incomplete, probes, tryConsumeQuery, cancellationToken);
        await ResolveHitsAsync(
            locations, shopId, candidates.Skip(half).ToList(), cookieHeader, hits, failures, completed, incomplete, probes, tryConsumeQuery, cancellationToken);
    }

    private sealed record SmtSearchItem(string ProductId, string Subject, string Location);

    /// <summary>同一批 SKU 在每个状态栏各搜一次，把 totalSize 加总。</summary>
    private async Task<(int Total, List<SmtSearchItem> Items, List<string> Failures)> SearchAcrossAsync(
        IReadOnlyList<SmtListLocation> locations,
        string shopId,
        string searchValue,
        string cookieHeader,
        CancellationToken cancellationToken)
    {
        var total = 0;
        var items = new List<SmtSearchItem>();
        var failures = new List<string>();
        foreach (var location in locations)
        {
            var (count, found, probe) = await SearchAsync(location, shopId, searchValue, cookieHeader, cancellationToken);
            if (probe is not null)
            {
                failures.Add(probe);
                continue;
            }

            total += count;
            items.AddRange(found);
        }

        return (total, items, failures);
    }

    private async Task<(int Total, List<SmtSearchItem> Items, string? Probe)> SearchAsync(
        SmtListLocation location,
        string shopId,
        string searchValue,
        string cookieHeader,
        CancellationToken cancellationToken)
    {
        var body = BuildSearchBody(searchValue, shopId, location);
        for (var attempt = 0; ; attempt++)
        {
            var result = await SearchOnceAsync(location, body, cookieHeader, cancellationToken);
            var busy = result.Probe is not null
                       && (result.Probe.Contains("2005", StringComparison.Ordinal)
                           || result.Probe.Contains("系统繁忙", StringComparison.Ordinal));
            if (!busy || attempt >= 2)
                return result;
            await Task.Delay(TimeSpan.FromMilliseconds(800 * (attempt + 1)), cancellationToken);
        }
    }

    private async Task<(int Total, List<SmtSearchItem> Items, string? Probe)> SearchOnceAsync(
        SmtListLocation location,
        string body,
        string cookieHeader,
        CancellationToken cancellationToken)
    {
        try
        {
            var (status, responseBody) = await _apiClient.SendAsync(
                "POST",
                PageListUrl,
                cookieHeader,
                body,
                "application/x-www-form-urlencoded",
                cancellationToken,
                referer: "https://www.dianxiaomi.com" + location.RefererPath);

            if (status is < 200 or >= 300)
                return (0, [], location.Label + " HTTP " + status + " " + Truncate(responseBody, 160));

            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;
            var code = ReadCode(root);
            var msg = root.TryGetProperty("msg", out var msgEl) && msgEl.ValueKind == JsonValueKind.String
                ? msgEl.GetString() ?? ""
                : "";
            if (code != 0)
                return (0, [], location.Label + " code=" + code + " " + msg);

            var items = new List<SmtSearchItem>();
            var total = 0;
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("page", out var page) && page.ValueKind == JsonValueKind.Object)
            {
                if (page.TryGetProperty("totalSize", out var totalEl)
                    && totalEl.ValueKind == JsonValueKind.Number
                    && totalEl.TryGetInt32(out var totalValue))
                {
                    total = totalValue;
                }

                if (page.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in list.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object)
                            continue;
                        var id = FirstNonEmpty(GetString(item, "idStr"), GetString(item, "id"));
                        var subject = FirstNonEmpty(GetString(item, "subject"), GetString(item, "name"));
                        items.Add(new SmtSearchItem(id, subject, location.Label));
                    }
                }
            }

            return (total, items, null);
        }
        catch (JsonException)
        {
            return (0, [], location.Label + " 响应不是合法 JSON");
        }
        catch (Exception ex) when (!CookieErrorDetector.IsAuthFailure(ex.Message))
        {
            return (0, [], location.Label + " " + Truncate(ex.Message, 160));
        }
    }

    private async Task CollectShopsAsync(
        SmtListLocation location,
        string cookieHeader,
        ConcurrentDictionary<string, SmtShopOption> shops,
        CancellationToken cancellationToken)
    {
        try
        {
            for (var pageNo = 1; pageNo <= ShopScanPages; pageNo++)
            {
                var (status, responseBody) = await _apiClient.SendAsync(
                    "POST", PageListUrl, cookieHeader,
                    BuildSearchBody("", "-1", location, pageNo),
                    "application/x-www-form-urlencoded", cancellationToken,
                    referer: "https://www.dianxiaomi.com" + location.RefererPath);
                if (status is < 200 or >= 300)
                    return;

                using var doc = JsonDocument.Parse(responseBody);
                var root = doc.RootElement;
                if (ReadCode(root) != 0)
                    return;
                if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                    return;
                if (!data.TryGetProperty("page", out var page) || page.ValueKind != JsonValueKind.Object)
                    return;
                if (!page.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
                    return;

                var rows = 0;
                foreach (var item in list.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        continue;
                    rows++;
                    var id = NormalizeKey(GetString(item, "shopId"));
                    if (id.Length == 0)
                        continue;
                    var name = NormalizeKey(GetString(item, "shopName"));
                    shops.TryAdd(id, new SmtShopOption { ShopId = id, Name = name.Length == 0 ? id : name });
                }

                if (rows < PageSize)
                    return;
            }
        }
        catch (JsonException)
        {
            // 店铺列表拿不到不影响查重本身
        }
    }

    public static string BuildEditUrl(string id) =>
        "https://www.dianxiaomi.com/web/smt/edit?id=" + Uri.EscapeDataString(id);

    private static string BuildSearchBody(string searchValue, string shopId, SmtListLocation location, int pageNo = 1)
    {
        var sb = new StringBuilder();
        // 空关键词用标题搜索（searchType=0）只为翻店铺；有 SKU 时 searchType=1 是「SKU编码」精确匹配。
        Append(sb, "pageNo", pageNo.ToString(CultureInfo.InvariantCulture));
        Append(sb, "pageSize", PageSize.ToString(CultureInfo.InvariantCulture));
        Append(sb, "searchType", searchValue.Length == 0 ? "0" : "1");
        Append(sb, "searchValue", searchValue);
        Append(sb, "shopId", shopId.Length == 0 ? "-1" : shopId);
        Append(sb, "dxmState", location.DxmState);
        if (!string.IsNullOrEmpty(location.DxmOfflineState))
            Append(sb, "dxmOfflineState", location.DxmOfflineState);
        if (!string.IsNullOrEmpty(location.ProductStatusType))
            Append(sb, "productStatusType", location.ProductStatusType);
        Append(sb, "groupId", "");
        if (location.DxmState == "online")
        {
            Append(sb, "sortName", "2");
            Append(sb, "sortValue", "2");
        }

        return sb.ToString();
    }

    private static List<SmtVariantSku> ReadSkus(JsonElement product)
    {
        if (product.TryGetProperty("smtProductSkuList", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            var fromList = ReadSkuArray(list);
            if (fromList.Count > 0)
                return fromList;
        }

        if (!product.TryGetProperty("aeopAeProductSKUs", out var raw))
            return [];

        if (raw.ValueKind == JsonValueKind.Array)
            return ReadSkuArray(raw);

        if (raw.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(raw.GetString()))
            return [];

        try
        {
            using var doc = JsonDocument.Parse(raw.GetString()!);
            return doc.RootElement.ValueKind == JsonValueKind.Array
                ? ReadSkuArray(doc.RootElement)
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static List<SmtVariantSku> ReadSkuArray(JsonElement array)
    {
        var result = new List<SmtVariantSku>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;
            var sku = NormalizeKey(GetString(item, "skuCode", "sku"));
            if (sku.Length == 0 || !seen.Add(sku))
                continue;
            result.Add(new SmtVariantSku
            {
                PageSku = sku,
                Attr = NormalizeKey(GetString(item, "name", "propertyValue", "attr"))
            });
        }

        return result;
    }

    private static void Append(StringBuilder sb, string key, string value)
    {
        if (sb.Length > 0)
            sb.Append('&');
        sb.Append(WebUtility.UrlEncode(key));
        sb.Append('=');
        sb.Append(WebUtility.UrlEncode(value));
    }

    private static int ReadCode(JsonElement root)
    {
        if (!root.TryGetProperty("code", out var codeEl))
            return -1;
        if (codeEl.ValueKind == JsonValueKind.Number && codeEl.TryGetInt32(out var n))
            return n;
        if (codeEl.ValueKind == JsonValueKind.String
            && int.TryParse(codeEl.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return -1;
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

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return "";
    }

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

    private static string NormalizeKey(string? value) => (value ?? "").Trim();

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";
}
