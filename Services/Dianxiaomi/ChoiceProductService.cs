/*
 * 功能说明：读取店小秘速卖通全托管（smtChoice）产品编辑页变种 SKU（唯一），供定价后按原 SKU 回写；
 *           并可按「当前店铺」核对变种 SKU 是否已被同店铺其它速卖通全托管产品占用。
 * 主要职责：从维护的 edit 链接解析产品 ID，带 Cookie 调用 choiceProduct/edit.json；
 *           变种按 SKU 去重，重量按【货品信息】货品条码唯一匹配【变种信息】后取 packageWeight（kg）；
 *           查重走 choiceProduct/pageList.json：按 searchValue 精确搜 SKU，查到即视为重复。
 * 创建日期：2026-09-13
 * 更新日期：2026-09-13 重量来自货品信息，按货品条码匹配
 *           2026-10-07 新增按当前店铺的 SKU 占用检查（采集箱/待发布/在线）
 *           2026-10-07 重量在货品条码缺失/匹配不到时回退用变种自带 packageWeight（否则整列带不出重量）
 *           2026-10-08 查重改为 choiceProduct/pageList.json 按 SKU 搜索（查到即重复），多值对分定位
 */

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace EcommerceWorkbench.Services.Dianxiaomi;

public sealed class ChoiceProductDetail
{
    public string Id { get; init; } = "";
    public string EditUrl { get; init; } = "";
    public string? Name { get; init; }
    /// <summary>当前产品所属店铺；查重只在该店铺内进行。</summary>
    public string ShopId { get; init; } = "";
    public List<ChoiceVariantSkuGroup> UniqueSkus { get; init; } = [];
}

/// <summary>某个 SKU 已被同店铺其它速卖通全托管产品占用时的一条记录。</summary>
public sealed class ChoiceSkuOccupancy
{
    public string Sku { get; init; } = "";
    public string Location { get; init; } = "";
    public string ProductName { get; init; } = "";
    public string ProductId { get; init; } = "";
    public string EditUrl { get; init; } = "";
    public string ShopName { get; init; } = "";
}

public sealed class ChoiceSkuOccupancyReport
{
    public List<ChoiceSkuOccupancy> Hits { get; init; } = [];
    /// <summary>因网络/接口错误未完成核对的位置名。</summary>
    public List<string> FailedLocations { get; init; } = [];
    /// <summary>部分内容未完成核对的原因（详情预算用尽、分页上限等）。</summary>
    public List<string> IncompleteReasons { get; init; } = [];
    /// <summary>核对过的位置名，用于区分「查过了确实没有」与「一处都没查成」。</summary>
    public List<string> CompletedLocations { get; init; } = [];
    /// <summary>本次核对的范围描述（店铺 + 状态），供界面说明。</summary>
    public string Scope { get; init; } = "";
    /// <summary>详情读不到变种时的诊断记录。</summary>
    public List<string> DetailProbes { get; init; } = [];
}

/// <summary>查重店铺下拉的一项。</summary>
public sealed class ChoiceShopOption
{
    public string ShopId { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>列表接口不回店铺名，所以标签用店铺 id；有名字时才拼上。</summary>
    public string Display => Name.Length == 0 || Name == ShopId ? ShopId : Name + "（" + ShopId + "）";
}

/// <summary>详情读不到变种时的诊断信息，便于区分「接口失败」「字段名不匹配」。</summary>
public sealed record ChoiceDetailProbe
{
    public string Location { get; init; } = "";
    public string ProductId { get; init; } = "";
    public int VisibleCount { get; init; }
    public int DeclaredSize { get; init; }
    public string VisibleSample { get; init; } = "";
    public int HttpStatus { get; init; }
    public int? Code { get; init; }
    public string Message { get; init; } = "";
    public int VariantCount { get; init; }
    public string BodySample { get; init; } = "";
    public string Error { get; init; } = "";

    public string ToDiagnostic()
    {
        var parts = new List<string>
        {
            "[" + Location + "] id=" + ProductId + " http=" + HttpStatus
            + " code=" + (Code?.ToString(CultureInfo.InvariantCulture) ?? "?")
        };
        if (Message.Length > 0) parts.Add("msg=" + Message);
        if (Error.Length > 0) parts.Add("err=" + Error);
        parts.Add("列表" + VisibleCount + "/声明" + DeclaredSize + " " + VisibleSample);
        parts.Add("详情变种=" + VariantCount);
        if (BodySample.Length > 0) parts.Add("响应=" + BodySample);
        return string.Join(" | ", parts);
    }
}

public sealed class ChoiceVariantSkuGroup
{
    public string PageSku { get; init; } = "";
    public string BarCode { get; init; } = "";
    /// <summary>最终采用的重量（kg）：优先货品条码匹配到的货品重量，其次变种自带重量。</summary>
    public double? PackageWeightKg { get; init; }
    /// <summary>变种自带的 packageWeight（kg），作为条码匹配不到时的兜底。</summary>
    public double? VariantWeightKg { get; init; }
    public int VariantCount { get; init; }
}

public sealed class ChoiceProductService
{
    public const string EditJsonUrl = "https://www.dianxiaomi.com/api/choiceProduct/edit.json";
    /// <summary>查重用的列表接口：按 searchValue 精确搜 SKU，查到即视为重复。</summary>
    public const string PageListUrl = "https://www.dianxiaomi.com/api/choiceProduct/pageList.json";
    private const int PageSize = 50;
    /// <summary>一次查询允许发出的请求数上限（对分法逐 SKU 定位）；超出部分如实记为未核对。</summary>
    private const int MaxQueriesPerLocation = 400;
    /// <summary>单次请求失败重试次数（瞬时超时/限流不该把整轮判成未核对）。</summary>
    private const int RetryCount = 2;
    private const int MaxDetailProbes = 12;

    private readonly ApiClient _apiClient;

    /// <summary>
    /// 查重的 productStatus 取值：覆盖面比单个 ONLINE 宽（实测同一店铺 915 条 vs 仅 ONLINE 581 条），
    /// 包含待上架/待审核/质检未过等在途状态，避免这些状态里的占用被漏掉。
    /// </summary>
    public const string OccupancyProductStatus =
        "ONLINE,PENDING_LAUNCH,OFFLINE,PENDING_APPROVAL,VIOLATION_QC_FAILED";

    /// <summary>查重范围：采集箱 / 待发布 / 在线（productStatus 统一用上面的值）。</summary>
    public readonly record struct ChoiceListLocation(string Label, string DxmState, string RefererPath);

    /// <summary>可选范围：全部、仅在线、仅待发布、仅采集箱。</summary>
    public static readonly ChoiceListLocation AllLocations = new("全部", "all", "/web/smtChoice/index");
    public static readonly ChoiceListLocation OnlineOnly = new("仅在线", "online", "/web/smtChoice/index");
    public static readonly ChoiceListLocation OfflineOnly = new("仅待发布", "offline", "/web/smtChoice/index");
    public static readonly ChoiceListLocation DraftOnly = new("仅采集箱", "draft", "/web/smtChoice/index");

    private static readonly ChoiceListLocation Draft = new("采集箱", "draft", "/web/smtChoice/index");
    private static readonly ChoiceListLocation Offline = new("待发布", "offline", "/web/smtChoice/index");
    private static readonly ChoiceListLocation Online = new("在线", "online", "/web/smtChoice/index");

    /// <summary>把范围选项展开成要逐个扫描的状态列表。</summary>
    public static IReadOnlyList<ChoiceListLocation> ExpandScope(string? scopeKey)
    {
        return scopeKey switch
        {
            "online" => [Online],
            "offline" => [Offline],
            "draft" => [Draft],
            _ => [Draft, Offline, Online]
        };
    }

    /// <summary>范围选项的可读名称，用于界面与状态提示。</summary>
    public static string ScopeName(string? scopeKey) => scopeKey switch
    {
        "online" => OnlineOnly.Label,
        "offline" => OfflineOnly.Label,
        "draft" => DraftOnly.Label,
        _ => AllLocations.Label
    };

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

        var unique = GroupUniqueSkus(ReadVariantRows(product));
        if (unique.Count == 0)
            throw new InvalidOperationException("未在【变种信息】中读到 SKU。请确认该产品已填写变种 SKU。");

        return new ChoiceProductDetail
        {
            Id = productId,
            EditUrl = editUrl,
            Name = GetString(product, "subject", "name"),
            ShopId = NormalizeKey(GetString(product, "shopId")),
            UniqueSkus = unique
        };
    }

    /// <summary>
    /// 按变种 SKU 核对是否已被**指定店铺**的其它速卖通全托管产品占用。
    /// 走 choiceProduct/pageList.json：按 <c>searchValue</c> 精确搜 SKU，**查到即视为重复**。
    /// 多值搜索是「或」语义且不告诉命中的是哪一个，所以按对分法逐 SKU 定位。
    /// </summary>
    public async Task<ChoiceSkuOccupancyReport> FindOccupanciesAsync(
        IReadOnlyList<string> skus,
        string? currentProductId,
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
            return new ChoiceSkuOccupancyReport { Scope = "店铺 " + shopLabel + " · " + scopeLabel };

        var hits = new ConcurrentBag<ChoiceSkuOccupancy>();
        var failures = new ConcurrentBag<string>();
        var completed = new ConcurrentBag<string>();
        var incomplete = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var probes = new ConcurrentBag<ChoiceDetailProbe>();

        var tasks = scopeLocations
            .Select(location => ScanLocationAsync(
                location, scopeShop, wanted, cookieHeader, hits, failures, completed, incomplete, probes, cancellationToken))
            .ToList();

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            var auth = Flatten(ex).FirstOrDefault(e => CookieErrorDetector.IsAuthFailure(e.Message));
            if (auth is not null)
                throw auth;
        }

        if (hits.IsEmpty && completed.IsEmpty && !failures.IsEmpty)
            throw new InvalidOperationException("SKU 占用检查没有完成：" + string.Join("、", failures) + "。");

        var ordered = hits
            .OrderBy(h => h.Sku, StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h.Location, StringComparer.Ordinal)
            .ThenBy(h => h.ProductName, StringComparer.Ordinal)
            .ToList();

        return new ChoiceSkuOccupancyReport
        {
            Hits = ordered,
            FailedLocations = failures.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList(),
            IncompleteReasons = incomplete.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            CompletedLocations = completed.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList(),
            Scope = "店铺 " + shopLabel + " · " + scopeLabel,
            DetailProbes = probes.Count == 0
                ? []
                : probes.Select(p => p.ToDiagnostic())
                    .Distinct(StringComparer.Ordinal)
                    .Take(MaxDetailProbes)
                    .ToList()
        };
    }

    private async Task ScanLocationAsync(
        ChoiceListLocation location,
        string shopId,
        IReadOnlyList<string> wanted,
        string cookieHeader,
        ConcurrentBag<ChoiceSkuOccupancy> hits,
        ConcurrentBag<string> failures,
        ConcurrentBag<string> completed,
        ConcurrentDictionary<string, byte> incomplete,
        ConcurrentBag<ChoiceDetailProbe> probes,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var queriesLeft = MaxQueriesPerLocation;
                await ResolveHitsAsync(
                    location, shopId, wanted, cookieHeader, hits, incomplete, probes,
                    () => Interlocked.Decrement(ref queriesLeft) >= 0, cancellationToken);
                completed.Add(location.Label);
                return;
            }
            catch (Exception ex) when (!CookieErrorDetector.IsAuthFailure(ex.Message))
            {
                if (attempt >= RetryCount)
                {
                    failures.Add(location.Label + "（重试 " + attempt + " 次仍失败）");
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(400 * (attempt + 1)), cancellationToken);
            }
        }
    }

    /// <summary>
    /// 对分法定位命中的 SKU：先整批搜，没命中就整批排除；命中且不止一个值就拆成两半继续。
    /// 这样 k 个占用只需 O(k·log n) 次请求，而不用每个 SKU 各发一次。
    /// </summary>
    private async Task ResolveHitsAsync(
        ChoiceListLocation location,
        string shopId,
        IReadOnlyList<string> candidates,
        string cookieHeader,
        ConcurrentBag<ChoiceSkuOccupancy> hits,
        ConcurrentDictionary<string, byte> incomplete,
        ConcurrentBag<ChoiceDetailProbe> probes,
        Func<bool> tryConsumeQuery,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
            return;

        if (!tryConsumeQuery())
        {
            incomplete.TryAdd(location.Label + "：查询次数已达上限，剩余 SKU 未核对", 0);
            return;
        }

        var searchValue = string.Join(",", candidates);
        var (found, items, probe) = await SearchAsync(location, shopId, searchValue, cookieHeader, cancellationToken);
        if (probe is not null)
            probes.Add(probe);
        if (!found)
            return;

        if (candidates.Count == 1)
        {
            var sku = candidates[0];
            if (items.Count == 0)
            {
                // 有总数但没返回明细：仍按「查到即重复」记账。
                hits.Add(new ChoiceSkuOccupancy
                {
                    Sku = sku,
                    Location = location.Label,
                    ProductName = "",
                    ProductId = "",
                    EditUrl = "",
                    ShopName = ""
                });
                return;
            }

            foreach (var item in items)
            {
                hits.Add(new ChoiceSkuOccupancy
                {
                    Sku = sku,
                    Location = location.Label,
                    ProductName = item.Subject,
                    ProductId = item.ProductId,
                    EditUrl = BuildEditUrl(item.ProductId),
                    ShopName = ""
                });
            }

            return;
        }

        var half = candidates.Count / 2;
        await ResolveHitsAsync(
            location, shopId, candidates.Take(half).ToList(), cookieHeader, hits, incomplete, probes, tryConsumeQuery, cancellationToken);
        await ResolveHitsAsync(
            location, shopId, candidates.Skip(half).ToList(), cookieHeader, hits, incomplete, probes, tryConsumeQuery, cancellationToken);
    }

    private sealed record ChoiceSearchItem(string ProductId, string Subject);

    /// <summary>
    /// 调 choiceProduct/pageList.json 搜一批 SKU。返回是否命中（totalSize&gt;0）、命中产品明细、失败诊断。
    /// </summary>
    private async Task<(bool Found, List<ChoiceSearchItem> Items, ChoiceDetailProbe? Probe)> SearchAsync(
        ChoiceListLocation location,
        string shopId,
        string searchValue,
        string cookieHeader,
        CancellationToken cancellationToken)
    {
        var body = BuildSearchBody(searchValue, shopId, location);
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
            {
                return (false, [], new ChoiceDetailProbe
                {
                    Location = location.Label,
                    ProductId = searchValue,
                    HttpStatus = status,
                    Error = "HTTP " + status,
                    BodySample = Truncate(responseBody, 200)
                });
            }

            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;
            var code = root.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.Number
                ? codeEl.GetInt32()
                : (int?)null;
            var msg = root.TryGetProperty("msg", out var msgEl) && msgEl.ValueKind == JsonValueKind.String
                ? msgEl.GetString() ?? ""
                : "";
            if (code != 0)
            {
                return (false, [], new ChoiceDetailProbe
                {
                    Location = location.Label,
                    ProductId = searchValue,
                    HttpStatus = status,
                    Code = code,
                    Message = msg,
                    Error = "接口返回失败"
                });
            }

            var items = new List<ChoiceSearchItem>();
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
                        items.Add(new ChoiceSearchItem(id, subject));
                    }
                }
            }

            var found = total > 0 || items.Count > 0;
            return (found, items, null);
        }
        catch (JsonException)
        {
            return (false, [], new ChoiceDetailProbe
            {
                Location = location.Label,
                ProductId = searchValue,
                Error = "响应不是合法 JSON"
            });
        }
        catch (Exception ex) when (!CookieErrorDetector.IsAuthFailure(ex.Message))
        {
            return (false, [], new ChoiceDetailProbe
            {
                Location = location.Label,
                ProductId = searchValue,
                Error = Truncate(ex.Message, 200)
            });
        }
    }

    private static void Append(StringBuilder sb, string key, string value)
    {
        if (sb.Length > 0)
            sb.Append('&');
        sb.Append(WebUtility.UrlEncode(key));
        sb.Append('=');
        sb.Append(WebUtility.UrlEncode(value));
    }

    /// <summary>按用户给定的参数拼查重请求体；只变 searchValue、店铺/状态与页码。</summary>
    private static string BuildSearchBody(string searchValue, string shopId, ChoiceListLocation location, int pageNo = 1)
    {
        var sb = new StringBuilder();
        Append(sb, "sortName", "2");
        Append(sb, "pageNo", pageNo.ToString(CultureInfo.InvariantCulture));
        Append(sb, "pageSize", PageSize.ToString(CultureInfo.InvariantCulture));
        Append(sb, "total", "0");
        Append(sb, "searchType", "1");
        Append(sb, "searchValue", searchValue);
        Append(sb, "productSearchType", "1");
        Append(sb, "shopId", shopId);
        Append(sb, "dxmState", location.DxmState);
        Append(sb, "fullCid", "");
        Append(sb, "sortValue", "2");
        Append(sb, "productType", "");
        Append(sb, "productStatus", OccupancyProductStatus);
        return sb.ToString();
    }

    /// <summary>列出速卖通全托管产品覆盖到的店铺（用于查重店铺下拉）。</summary>
    public async Task<List<ChoiceShopOption>> ListShopsAsync(
        string cookieHeader,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cookieHeader))
            throw new InvalidOperationException("请先导出 Cookie。");

        var shops = new ConcurrentDictionary<string, ChoiceShopOption>(StringComparer.OrdinalIgnoreCase);
        var wanted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var tasks = new[] { Draft, Offline, Online }
            .Select(location => CollectShopsAsync(location, cookieHeader, shops, wanted, cancellationToken))
            .ToList();
        await Task.WhenAll(tasks);

        return shops.Values
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.ShopId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>查店铺列表时每个状态翻几页（只为凑齐店铺，不需要全量）。</summary>
    private const int ShopScanPages = 5;

    private async Task CollectShopsAsync(
        ChoiceListLocation location,
        string cookieHeader,
        ConcurrentDictionary<string, ChoiceShopOption> shops,
        Dictionary<string, string> wanted,
        CancellationToken cancellationToken)
    {
        // 只需要店铺 id：拉前几页（searchValue 留空＝不按 SKU 过滤），顺带读 shopName（通常为空）。
        _ = wanted;
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
                if (root.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.Number
                    && codeEl.GetInt32() != 0)
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
                    shops.TryAdd(id, new ChoiceShopOption { ShopId = id, Name = name.Length == 0 ? id : name });
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

    /// <summary>
    /// 变种按 SKU 去重；重量优先按货品条码在货品信息中唯一匹配（同一条码只取第一次出现的重量），
    /// 条码为空或匹配不到时回退用变种自带的 <c>packageWeight</c>（kg）。
    /// 店小秘不少全托管产品没有货品条码（实测 `scItemBarCode` 为空串），
    /// 只认条码会导致整列重量都带不出来。
    /// </summary>
    public static List<ChoiceVariantSkuGroup> GroupUniqueSkus(IEnumerable<ChoiceVariantSkuGroup> variants)
    {
        var goodsByBarcode = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in variants)
        {
            var code = NormalizeKey(row.BarCode);
            if (code.Length == 0 || row.PackageWeightKg is null)
                continue;
            goodsByBarcode.TryAdd(code, row.PackageWeightKg.Value);
        }

        var result = new List<ChoiceVariantSkuGroup>();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in variants)
        {
            var sku = NormalizeKey(row.PageSku);
            if (sku.Length == 0)
                continue;

            var code = NormalizeKey(row.BarCode);
            double? kg = null;
            if (code.Length > 0 && goodsByBarcode.TryGetValue(code, out var mappedKg))
                kg = mappedKg;
            kg ??= row.VariantWeightKg ?? row.PackageWeightKg;

            if (index.TryGetValue(sku, out var i))
            {
                var existing = result[i];
                result[i] = new ChoiceVariantSkuGroup
                {
                    PageSku = existing.PageSku,
                    BarCode = existing.BarCode.Length > 0 ? existing.BarCode : code,
                    PackageWeightKg = existing.PackageWeightKg ?? kg,
                    VariantWeightKg = existing.VariantWeightKg ?? row.VariantWeightKg ?? row.PackageWeightKg,
                    VariantCount = existing.VariantCount + 1
                };
            }
            else
            {
                index[sku] = result.Count;
                result.Add(new ChoiceVariantSkuGroup
                {
                    PageSku = sku,
                    BarCode = code,
                    PackageWeightKg = kg,
                    VariantWeightKg = row.VariantWeightKg ?? row.PackageWeightKg,
                    VariantCount = 1
                });
            }
        }

        return result;
    }

    private static List<ChoiceVariantSkuGroup> ReadVariantRows(JsonElement product)
    {
        // 与 JoomProductService 同一缺陷：variants 字段存在但读不出 SKU 时会短路掉字符串形态的兜底字段。
        if (product.TryGetProperty("variationList", out var variants) && variants.ValueKind == JsonValueKind.Array)
        {
            var fromArray = ReadVariantArray(variants);
            if (fromArray.Count > 0)
                return fromArray;
        }

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
                    return ReadVariantArray(doc.RootElement);
            }
            catch (JsonException)
            {
                // 忽略无法解析的变种 JSON
            }
        }

        return [];
    }

    private static List<ChoiceVariantSkuGroup> ReadVariantArray(JsonElement array)
    {
        var list = new List<ChoiceVariantSkuGroup>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;
            var sku = GetString(item, "skuCode", "sku");
            if (string.IsNullOrWhiteSpace(sku))
                continue;
            var weight = GetDouble(item, "packageWeight", "package_weight");
            list.Add(new ChoiceVariantSkuGroup
            {
                PageSku = sku,
                BarCode = GetString(item, "scItemBarCode", "scItemBarcode", "barcode"),
                PackageWeightKg = weight,
                VariantWeightKg = weight,
                VariantCount = 1
            });
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

    private static double? GetDouble(JsonElement el, params string[] names)
    {
        foreach (var name in names)
        {
            if (!el.TryGetProperty(name, out var prop)
                || prop.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }

            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var n))
                return n;

            if (prop.ValueKind == JsonValueKind.String)
            {
                var raw = prop.GetString()?.Trim();
                if (string.IsNullOrEmpty(raw))
                    continue;
                raw = raw.Replace(",", "");
                if (double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var inv)
                    || double.TryParse(raw, NumberStyles.Any, CultureInfo.CurrentCulture, out inv))
                {
                    return inv;
                }
            }
        }

        return null;
    }

    private static string NormalizeKey(string? value) => (value ?? "").Trim();

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";
}
