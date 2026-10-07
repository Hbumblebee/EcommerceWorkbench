/*
 * 功能说明：读取店小秘 JOOM 产品编辑页变种 SKU（唯一），供定价后按原 SKU 回写。
 * 主要职责：从维护的 edit 链接解析产品 ID，带 Cookie 调用 edit.json，解析变种信息；
 *           发布前按变种 SKU 精确查询采集箱、待发布、在线产品，返回已占用的产品与编辑链接。
 * 创建日期：2026-09-05
 * 修改记录：2026-09-28 增加 SKU 占用查询（采集箱 / 待发布 / 在线）
 *           2026-09-28 占用结果带上店铺名称
 *           2026-09-28 SKU 占用改为检查全部店铺
 *           2026-10-02 支持以 + 分隔的组合 SKU 逐项精确检查
 *           2026-10-03 占用检查按 SKU 分批搜索；失败位置与「未核对原因」分开报告，不再伪装成位置名
 *           2026-10-04 列表变种表覆盖不全时改读详情页补全，避免部分命中导致其余 SKU 漏检（假阴性）
 *           2026-10-07 用列表行的 variantSize 识别变种截断，截断商品一律读 edit.json 全量变种核对
 *           2026-10-07 组合货号不再按「+」拆分：搜索与比对一律用完整 SKU，消除组合货号误报
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
    public string? Name { get; init; }
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
    /// <summary>因网络/接口错误未完成核对的位置名。</summary>
    public List<string> FailedLocations { get; init; } = [];
    /// <summary>部分内容未完成核对的原因（如详情核对预算用尽、结果超过分页上限），不混入 FailedLocations。</summary>
    public List<string> IncompleteReasons { get; init; } = [];
    /// <summary>详情页读不到变种时的诊断记录，用于定位「为什么没核对完整」。</summary>
    public List<string> DetailProbes { get; init; } = [];
}

/// <summary>
/// 详情页读不到变种时的诊断信息：记录请求地址与响应外形，
/// 便于区分「id 不是主商品 id」「接口返回失败」「变种字段名不匹配」。
/// </summary>
public sealed record JoomDetailProbe
{
    public string Location { get; init; } = "";
    public string ProductId { get; init; } = "";
    public int VisibleCount { get; init; }
    public string VisibleSample { get; init; } = "";
    public string RequestUrl { get; init; } = "";
    public int HttpStatus { get; init; }
    public int? Code { get; init; }
    public string Message { get; init; } = "";
    public string TopKeys { get; init; } = "";
    public int VariantCount { get; init; }
    /// <summary>列表行声明的变种总数（用于判断列表是否截断了变种）。</summary>
    public int VariantSize { get; init; }
    public string BodySample { get; init; } = "";
    public string Error { get; init; } = "";

    /// <summary>压成一行，方便在界面里直接复制排查。</summary>
    public string ToDiagnostic()
    {
        var lines = new List<string>
        {
            "[" + Location + "] id=" + ProductId + " http=" + HttpStatus + " code=" + (Code?.ToString(CultureInfo.InvariantCulture) ?? "?")
        };
        if (Message.Length > 0)
            lines.Add("msg=" + Message);
        if (Error.Length > 0)
            lines.Add("err=" + Error);
        lines.Add("list可见=" + VisibleCount + "个(声明" + VariantSize + "个) " + VisibleSample);
        lines.Add("edit变种=" + VariantCount + "个");
        if (TopKeys.Length > 0)
            lines.Add("顶层字段=" + TopKeys);
        if (BodySample.Length > 0)
            lines.Add("响应=" + BodySample);
        return string.Join(" | ", lines);
    }
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
    private const int EditFallbackBudget = 60;
    /// <summary>详情诊断最多回传的条数，够定位问题且不至于淹没界面。</summary>
    private const int MaxDetailProbes = 12;
    /// <summary>占用检查按此数量分批搜索，避免单个 searchValue 过大触发后端截断。</summary>
    private const int OccupancySearchBatchSize = 20;

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
            Name = GetString(product, "name"),
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
        CancellationToken cancellationToken = default,
        string? currentProductName = null)
    {
        if (string.IsNullOrWhiteSpace(cookieHeader))
            throw new InvalidOperationException("请先导出 Cookie。");

        // 待查集合就是完整货号本身。组合货号（J0071-1+J0043-1）是店小秘里的**一个**变种 SKU，
        // 不按「+」拆分：拆开会让 J0043-1 被当成只提供 J0075+J0043-1 的产品占用（假阳性），
        // 也会把大量无关商品拉进候选集。查重全程只认完整 SKU 精确相等。
        var wanted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sku in skus)
        {
            var key = (sku ?? "").Trim();
            if (key.Length == 0 || key.Contains(','))
                continue;
            wanted.TryAdd(key, key);
        }

        if (wanted.Count == 0)
            return new JoomSkuOccupancyReport();

        var shopNames = await LoadShopNamesAsync(cookieHeader, cancellationToken);
        var currentId = (currentProductId ?? "").Trim();
        // 店小秘列表有时按变种返回行、其 id 与编辑页商品 id 不一致，只靠 id 排不掉自己，
        // 会把「当前这个产品」当成占用自己的产品报出来（假阳性）。用产品名再兜一层。
        var currentName = (currentProductName ?? "").Trim();
        var hits = new ConcurrentBag<JoomSkuOccupancy>();
        var failures = new ConcurrentBag<string>();
        var completed = new ConcurrentBag<string>();
        var seen = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var incomplete = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var probes = new ConcurrentBag<JoomDetailProbe>();
        // 详情兜底预算由全部遍历位置共享：只用于「列表变种表覆盖不全」的商品，
        // 且按需合并补全，不会因为一个多变种商品而重复消耗。
        var editBudget = EditFallbackBudget;

        // 按固定大小分批搜索：单个 searchValue 过大时后端可能截断结果，分批后每批的结果集更小、
        // 更容易落在分页上限内；批次内排序保证请求内容可复现。
        var batches = wanted.Keys
            .OrderBy(k => k, StringComparer.Ordinal)
            .Chunk(OccupancySearchBatchSize)
            .Select(chunk => string.Join(",", chunk))
            .ToList();

        var tasks = new List<Task>(batches.Count * Locations.Length);
        foreach (var searchValue in batches)
        {
            foreach (var location in Locations)
            {
                tasks.Add(ScanLocationAsync(
                    location,
                    searchValue,
                    shopNames,
                    currentId,
                    currentName,
                    wanted,
                    cookieHeader,
                    hits,
                    failures,
                    completed,
                    seen,
                    incomplete,
                    probes,
                    () => Interlocked.Decrement(ref editBudget) >= 0,
                    cancellationToken));
            }
        }

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
        var failedLocations = failures
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        var incompleteReasons = incomplete.Keys
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
            FailedLocations = failedLocations,
            IncompleteReasons = incompleteReasons,
            DetailProbes = probes.Count == 0
                ? []
                : probes
                    .Select(p => p.ToDiagnostic())
                    .Distinct(StringComparer.Ordinal)
                    .Take(MaxDetailProbes)
                    .ToList()
        };
    }

    private async Task ScanLocationAsync(
        JoomListLocation location,
        string searchValue,
        IReadOnlyDictionary<string, string> shopNames,
        string currentProductId,
        string currentProductName,
        Dictionary<string, string> wanted,
        string cookieHeader,
        ConcurrentBag<JoomSkuOccupancy> hits,
        ConcurrentBag<string> failures,
        ConcurrentBag<string> completed,
        ConcurrentDictionary<string, byte> seen,
        ConcurrentDictionary<string, byte> incomplete,
        ConcurrentBag<JoomDetailProbe> probes,
        Func<bool> tryConsumeEditFallback,
        CancellationToken cancellationToken)
    {
        try
        {
            var pageNo = 1;
            var totalPage = 1;
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

                if (!TryReadPage(data, out var list, out totalPage))
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

                    var name = FirstNonEmpty(GetString(item, "name"), GetString(item, "title"));
                    if (currentProductName.Length > 0
                        && name.Trim().Equals(currentProductName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var itemShop = NormalizeShopId(GetString(item, "shopId"));
                    var (visible, variantSize) = ReadVisibleSkus(item);

                    // 店小秘列表接口只回前几个变种（实测 variantSize=7 时 variants 只有 5 条）。
                    // 变种被截断时，命中 SKU 可能落在没回来的那部分，只看列表必然漏检，
                    // 因此这种情况下无条件读详情页取全量变种。
                    var truncated = variantSize > visible.Count;
                    var overlapsWanted = visible.Any(wanted.ContainsKey);

                    // 详情兜底只对「可能命中」的商品做：列表已给出待查 SKU、或变种被截断但
                    // 列表里一个待查 SKU 都没有的商品，读到全量变种也是零命中，不该占用预算。
                    var detailUnknown = false;
                    if ((truncated || overlapsWanted) && (truncated || IsDetailNeeded(visible, wanted)))
                    {
                        var detailFailNote = location.Label + "：部分商品的详情未能读取，" + productId + " 只按列表可见变种核对";
                        if (!tryConsumeEditFallback())
                        {
                            detailUnknown = true;
                            // 原实现把这句话塞进 failures，伪装成「位置」名并污染后续判断；
                            // 现在单独归入 incomplete，语义与失败位置分开。
                            incomplete.TryAdd(
                                location.Label + "：部分商品未能核对详情（详情核对预算用尽）", 0);
                        }
                        else
                        {
                            var (detailed, probed) = await TryReadEditSkusAsync(
                                productId, cookieHeader, cancellationToken, location.Label, visible, variantSize);
                            if (detailed.Count == 0)
                            {
                                detailUnknown = true;
                                incomplete.TryAdd(detailFailNote, 0);
                            }
                            else
                            {
                                // 详情给的是全量变种，合并后仍有待查 SKU 缺失才算没核对完
                                // （详情本身也可能被裁），否则就是已完整核对。
                                visible = detailed
                                    .Concat(visible)
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .ToList();
                                if (IsDetailNeeded(visible, wanted))
                                {
                                    detailUnknown = true;
                                    incomplete.TryAdd(detailFailNote, 0);
                                }
                            }

                            if (probed is not null)
                                probes.Add(probed);
                        }
                    }

                    var parentSku = GetString(item, "parentSku").Trim();
                    var resolvedShop = itemShop;
                    var shopName = "";
                    if (resolvedShop is not null && shopNames.TryGetValue(resolvedShop, out var foundShopName))
                        shopName = foundShopName;

                    // 一个产品的全部命中一次性报出：键只用「产品 + 位置」，
                    // 避免同一产品的分页重复回调里，后面的完整变种表被当成另一次占用。
                    var matched = visible.Where(wanted.ContainsKey).ToList();
                    if (matched.Count > 0
                        && seen.TryAdd(productId + "\n" + location.Label, 0))
                    {
                        foreach (var found in matched)
                        {
                            if (!wanted.TryGetValue(found, out var displaySku))
                                continue;
                            hits.Add(new JoomSkuOccupancy
                            {
                                Sku = displaySku,
                                Location = location.Label,
                                ProductName = name,
                                ProductId = productId,
                                EditUrl = BuildEditUrl(productId),
                                // 详情兜底会合并多个变种，此时 item.parentSku 只是其中一个变种的 SKU，
                                // 作为 Parent SKU 展示会误导；仅单变种商品才带上。
                                ParentSku = parentSku.Equals(displaySku, StringComparison.OrdinalIgnoreCase)
                                            || visible.Count != 1
                                    ? ""
                                    : parentSku,
                                ShopName = shopName
                            });
                        }
                    }

                    // 变种没读全的商品，其余 SKU 是否命中仍是未知：记一条诊断，
                    // 让界面能说清「为什么没核对完整」，而不是只给一句笼统提示。
                    if (detailUnknown)
                    {
                        var probe = new JoomDetailProbe
                        {
                            Location = location.Label,
                            ProductId = productId,
                            VisibleCount = visible.Count,
                            VisibleSample = string.Join("/", visible.Take(6))
                        };
                        probes.Add(probe with
                        {
                            Error = probe.Error.Length > 0 || probe.BodySample.Length > 0
                                ? probe.Error
                                : "未读到完整变种"
                        });
                    }
                }

                if (pageNo >= totalPage || list.GetArrayLength() == 0)
                    break;
                pageNo++;
            }

            // 分页上限截断：结果超过 MaxPagesPerLocation×PageSize 条时，超出部分没有核对过，
            // 原实现静默当作「检查完成」，会给出「无占用」的假阴性。
            if (pageNo > MaxPagesPerLocation && totalPage > MaxPagesPerLocation)
            {
                incomplete.TryAdd(
                    location.Label + $"：结果超过 {MaxPagesPerLocation * PageSize} 条，超出部分未核对", 0);
            }

            completed.Add(location.Label);
        }
        catch (Exception ex) when (!CookieErrorDetector.IsAuthFailure(ex.Message))
        {
            failures.Add(location.Label);
        }
    }

    /// <summary>
    /// 读取编辑页全量变种 SKU。失败时返回诊断信息（probe），用于说明「为什么这个商品没核对完整」。
    /// </summary>
    private async Task<(List<string> Skus, JoomDetailProbe? Probe)> TryReadEditSkusAsync(
        string productId,
        string cookieHeader,
        CancellationToken cancellationToken,
        string locationLabel,
        IReadOnlyCollection<string> visible,
        int listVariantSize)
    {
        var editUrl = BuildEditUrl(productId);
        var apiUrl = EditJsonUrl + "?id=" + Uri.EscapeDataString(productId);
        var probe = new JoomDetailProbe
        {
            Location = locationLabel,
            ProductId = productId,
            VisibleCount = visible.Count,
            VisibleSample = string.Join("/", visible.Take(6)),
            RequestUrl = apiUrl
        };

        try
        {
            var (status, body) = await _apiClient.SendAsync(
                "GET",
                apiUrl,
                cookieHeader,
                body: null,
                contentType: "application/json",
                cancellationToken,
                referer: editUrl);
            probe = probe with { HttpStatus = status, BodySample = Truncate(body, 300) };
            if (status is < 200 or >= 300)
                return ([], probe);

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var topKeys = new HashSet<string>(StringComparer.Ordinal);
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in root.EnumerateObject())
                    topKeys.Add(prop.Name);
            }

            var code = root.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.Number
                ? codeEl.GetInt32()
                : (int?)null;
            var msg = root.TryGetProperty("msg", out var msgEl) && msgEl.ValueKind == JsonValueKind.String
                ? msgEl.GetString() ?? ""
                : "";
            probe = probe with { Code = code, Message = msg, TopKeys = string.Join(",", topKeys) };

            if (code != 0 || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return ([], probe);

            var product = data.TryGetProperty("product", out var productEl) && productEl.ValueKind == JsonValueKind.Object
                ? productEl
                : data;
            // 详情响应的 variantSize 恒为 0，不能当总数用，这里以实际读到的条数为准。
            var variants = ReadVariants(product);
            probe = probe with { VariantCount = variants.Count, VariantSize = listVariantSize };
            return (variants, variants.Count == 0 ? probe : null);
        }
        catch (JsonException)
        {
            return ([], probe with { Error = "响应不是合法 JSON" });
        }
        catch (Exception ex) when (!CookieErrorDetector.IsAuthFailure(ex.Message))
        {
            return ([], probe with { Error = Truncate(ex.Message, 200) });
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
        catch (Exception ex) when (!CookieErrorDetector.IsAuthFailure(ex.Message))
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

    /// <summary>
    /// 读列表行的可见变种 SKU，并带上该商品声明的变种总数。
    /// 店小秘列表接口只回前几个变种，<c>variantSize</c> 才是总数（实测 7 个变种只回 5 条），
    /// 用它判断「列表给的变种表是否被截断」。
    /// </summary>
    private static (List<string> Skus, int VariantSize) ReadVisibleSkus(JsonElement item)
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

        var skuCount = list.Count;
        var ownSku = GetString(item, "sku").Trim();
        if (ownSku.Length > 0)
            list.Add(ownSku);
        var parentSku = GetString(item, "parentSku").Trim();
        if (parentSku.Length > 0)
            list.Add(parentSku);

        var variantSize = GetInt(item, "variantSize") ?? 0;
        return (list, Math.Max(variantSize, skuCount));
    }

    /// <summary>
    /// 列表返回的变种表是否还缺少待查 SKU。缺少就必须读详情页补全，
    /// 否则「部分命中」会被误当成已核对，导致其余 SKU 漏检。
    /// </summary>
    private static bool IsDetailNeeded(
        IReadOnlyCollection<string> visible,
        IReadOnlyDictionary<string, string> wanted)
    {
        if (visible.Count == 0)
            return wanted.Count > 0;

        foreach (var sku in wanted.Keys)
        {
            if (!visible.Contains(sku, StringComparer.OrdinalIgnoreCase))
                return true;
        }

        return false;
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
        // 只要 variants 字段存在就返回会让空数组/无 sku 项短路掉 variantJson，
        // 进而误报「未在【变种信息】中读到 SKU」。改为数组读不出 SKU 时继续尝试 variantJson。
        if (product.TryGetProperty("variants", out var variants) && variants.ValueKind == JsonValueKind.Array)
        {
            var fromArray = ReadSkuColumn(variants);
            if (fromArray.Count > 0)
                return fromArray;
        }

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
