/*
 * 功能说明：用已导出 Cookie 打开店小秘速卖通全托管编辑页，读取变种 SKU 并回写供货价。
 * 创建日期：2026-09-13
 */

using System.IO;
using System.Text.Json;
using System.Windows;
using EcommerceWorkbench.Models;
using Microsoft.Web.WebView2.Core;

namespace EcommerceWorkbench.Views;

public partial class Smt7ProductPageWindow : Window
{
    private bool _coreReady;
    private bool _finderInstalled;

    public Smt7ProductPageWindow()
    {
        InitializeComponent();
        Closed += (_, _) =>
        {
            try
            {
                Browser.Dispose();
            }
            catch
            {
                // 关闭窗口时忽略 WebView2 释放异常
            }
        };
    }

    public bool IsShowing(Uri url)
        => Browser.Source is not null
           && Uri.Compare(Browser.Source, url, UriComponents.HttpRequestUrl, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;

    public async Task OpenProductAsync(Uri url, IReadOnlyList<CookieRecord> cookies)
    {
        HintText.Text = "正在加载：" + url;
        await EnsureCoreAsync();
        InjectCookies(cookies);
        Browser.Source = url;
    }

    public async Task<IReadOnlyList<string>> ReadUniqueSkuCodesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureCoreAsync();
        if (Browser.CoreWebView2 is null)
            throw new InvalidOperationException("产品详情页尚未初始化。");

        await WaitUntilSkuTableReadyAsync(cancellationToken);
        var raw = await Browser.CoreWebView2.ExecuteScriptAsync(ReadSkuScript);
        return ParseSkuList(raw);
    }

    public async Task<Smt7PageWriteResult> WritePricesAsync(
        IReadOnlyDictionary<string, string> pageSkuToPrice,
        CancellationToken cancellationToken = default)
    {
        if (pageSkuToPrice.Count == 0)
            return new Smt7PageWriteResult { Error = "没有可回写的最终价。" };

        await EnsureCoreAsync();
        if (Browser.CoreWebView2 is null)
            return new Smt7PageWriteResult { Error = "产品详情页尚未初始化。" };

        await WaitUntilSkuTableReadyAsync(cancellationToken);
        var mapJson = JsonSerializer.Serialize(pageSkuToPrice);
        var script = WriteScriptPrefix + mapJson + WriteScriptSuffix;
        var raw = await Browser.CoreWebView2.ExecuteScriptAsync(script);
        return ParseWriteResult(raw);
    }

    public void SetHint(string text) => HintText.Text = text;

    private async Task EnsureCoreAsync()
    {
        if (_coreReady && Browser.CoreWebView2 is not null)
            return;

        var userData = Path.Combine(AppPaths.DataDirectory, "webview-smt7");
        Directory.CreateDirectory(userData);
        var env = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
        await Browser.EnsureCoreWebView2Async(env);
        var core = Browser.CoreWebView2
            ?? throw new InvalidOperationException("WebView2 初始化失败。");
        core.Settings.AreDefaultContextMenusEnabled = true;
        if (!_finderInstalled)
        {
            await core.AddScriptToExecuteOnDocumentCreatedAsync(FinderScript);
            _finderInstalled = true;
        }

        _coreReady = true;
    }

    private void InjectCookies(IReadOnlyList<CookieRecord> cookies)
    {
        var mgr = Browser.CoreWebView2?.CookieManager;
        if (mgr is null)
            return;

        foreach (var record in cookies)
        {
            if (string.IsNullOrWhiteSpace(record.Name))
                continue;

            var domain = string.IsNullOrWhiteSpace(record.Domain) ? ".dianxiaomi.com" : record.Domain;
            var path = string.IsNullOrWhiteSpace(record.Path) ? "/" : record.Path;
            var cookie = mgr.CreateCookie(record.Name, record.Value ?? "", domain, path);
            cookie.IsHttpOnly = record.IsHttpOnly;
            cookie.IsSecure = record.IsSecure;
            if (record.ExpiresUtcTicks is > 0)
            {
                try
                {
                    cookie.Expires = new DateTime((long)record.ExpiresUtcTicks.Value, DateTimeKind.Utc);
                }
                catch
                {
                    // 过期时间异常时走会话 Cookie
                }
            }

            mgr.AddOrUpdateCookie(cookie);
        }
    }

    private async Task WaitUntilSkuTableReadyAsync(CancellationToken cancellationToken)
    {
        const int maxTries = 40;
        for (var i = 0; i < maxTries; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Browser.CoreWebView2 is null)
                throw new InvalidOperationException("产品详情页已关闭。");

            await Browser.CoreWebView2.ExecuteScriptAsync(FinderScript);
            var raw = await Browser.CoreWebView2.ExecuteScriptAsync(ReadyScript);
            if (IsReady(raw))
                return;

            await Task.Delay(500, cancellationToken);
        }

        throw new InvalidOperationException("页面变种表格尚未加载完成。请确认已登录且详情页已打开，然后重试。");
    }

    private static bool IsReady(string? executeResult)
    {
        var json = UnwrapExecuteScriptResult(executeResult);
        if (string.IsNullOrWhiteSpace(json))
            return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    private static List<string> ParseSkuList(string? executeResult)
    {
        var json = UnwrapExecuteScriptResult(executeResult);
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("skus", out var skusEl) && skusEl.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var item in skusEl.EnumerateArray())
                {
                    var sku = item.GetString();
                    if (!string.IsNullOrWhiteSpace(sku))
                        list.Add(sku);
                }

                return list;
            }
        }
        catch
        {
            // 解析失败按未读到 SKU 处理
        }

        return [];
    }

    private static Smt7PageWriteResult ParseWriteResult(string? executeResult)
    {
        var json = UnwrapExecuteScriptResult(executeResult);
        if (string.IsNullOrWhiteSpace(json))
            return new Smt7PageWriteResult { Error = "页面脚本没有返回结果。" };

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var error = root.TryGetProperty("error", out var errEl) ? errEl.GetString() : null;
            var updated = root.TryGetProperty("updated", out var uEl) && uEl.TryGetInt32(out var n) ? n : 0;
            var missed = new List<string>();
            if (root.TryGetProperty("missed", out var missedEl) && missedEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in missedEl.EnumerateArray())
                {
                    var sku = item.GetString();
                    if (!string.IsNullOrWhiteSpace(sku))
                        missed.Add(sku);
                }
            }

            return new Smt7PageWriteResult
            {
                Updated = updated,
                MissedPageSkus = missed,
                Error = string.IsNullOrWhiteSpace(error) ? null : error
            };
        }
        catch (Exception ex)
        {
            return new Smt7PageWriteResult { Error = "解析回写结果失败：" + ex.Message };
        }
    }

    private static string UnwrapExecuteScriptResult(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "null")
            return "";
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.ValueKind == JsonValueKind.String
                ? doc.RootElement.GetString() ?? ""
                : raw;
        }
        catch
        {
            return raw;
        }
    }

    private const string FinderScript = """
        window.__dxmSmtFindSkuRows = function () {
          function looksLikeSkuRows(list) {
            return Array.isArray(list) && list.length && list[0]
              && (typeof list[0].skuCode !== 'undefined' || typeof list[0].skuPrice !== 'undefined');
          }
          function listFromStore(store) {
            if (!store) return null;
            return (store.formState && store.formState.skuDataList) || store.skuDataList;
          }
          function fromPinia(pinia) {
            if (!pinia) return null;
            try {
              if (pinia._s && typeof pinia._s.get === 'function') {
                const named = pinia._s.get('smtChoiceSkuDataStore');
                const namedList = listFromStore(named);
                if (looksLikeSkuRows(namedList)) return namedList;
                if (typeof pinia._s.values === 'function') {
                  for (const store of pinia._s.values()) {
                    const list = listFromStore(store);
                    if (looksLikeSkuRows(list)) return list;
                  }
                }
              }
              const state = pinia.state && pinia.state.value && pinia.state.value.smtChoiceSkuDataStore;
              if (state) {
                const list = listFromStore(state);
                if (looksLikeSkuRows(list)) return list;
              }
            } catch (e) {}
            return null;
          }
          function fromApp() {
            const el = document.querySelector('#app');
            const app = el && el.__vue_app__;
            if (!app) return null;
            const gp = app.config && app.config.globalProperties && app.config.globalProperties.$pinia;
            const found = fromPinia(gp);
            if (found) return found;
            const provides = app._context && app._context.provides;
            if (!provides) return null;
            for (const key of Reflect.ownKeys(provides)) {
              const rows = fromPinia(provides[key]);
              if (rows) return rows;
            }
            return null;
          }
          function fromVueTree() {
            const nodes = document.querySelectorAll('.vxe-table, #skuDataInfo, [id="skuDataInfo"]');
            for (const el of nodes) {
              let inst = el.__vueParentComponent;
              let depth = 0;
              while (inst && depth < 40) {
                const bags = [inst.setupState, inst.ctx, inst.props, inst.exposed];
                for (const bag of bags) {
                  if (!bag) continue;
                  const list = bag.modelValue || (bag.formState && bag.formState.skuDataList) || bag.skuDataList;
                  if (looksLikeSkuRows(list)) return list;
                }
                inst = inst.parent;
                depth += 1;
              }
            }
            return null;
          }
          return fromApp() || fromVueTree();
        };
        """;

    private const string ReadyScript = """
        (function () {
          const rows = window.__dxmSmtFindSkuRows && window.__dxmSmtFindSkuRows();
          return { ok: !!(rows && rows.length) };
        })();
        """;

    private const string ReadSkuScript = """
        (function () {
          const rows = window.__dxmSmtFindSkuRows && window.__dxmSmtFindSkuRows();
          if (!rows || !rows.length) return { skus: [] };
          const seen = {};
          const skus = [];
          for (const row of rows) {
            const sku = String(row.skuCode || row.sku || '').trim();
            if (!sku) continue;
            const key = sku.toLowerCase();
            if (seen[key]) continue;
            seen[key] = true;
            skus.push(sku);
          }
          return { skus: skus };
        })();
        """;

    private const string WriteScriptPrefix = """
        (function () {
          const map =
        """;

    private const string WriteScriptSuffix = """
        ;
          function lookup(sku) {
            if (!sku) return null;
            if (Object.prototype.hasOwnProperty.call(map, sku)) return map[sku];
            const lower = String(sku).toLowerCase();
            for (const key of Object.keys(map)) {
              if (key.toLowerCase() === lower) return map[key];
            }
            return null;
          }
          const rows = window.__dxmSmtFindSkuRows && window.__dxmSmtFindSkuRows();
          if (!rows || !rows.length) return { updated: 0, missed: Object.keys(map), error: '未找到变种信息表格' };
          const seen = {};
          let updated = 0;
          for (const row of rows) {
            const sku = String(row.skuCode || row.sku || '').trim();
            if (!sku) continue;
            const price = lookup(sku);
            if (price == null || price === '') continue;
            const text = String(price);
            const num = Number(text);
            row.skuPrice = Number.isFinite(num) ? num : text;
            seen[sku.toLowerCase()] = true;
            updated += 1;
          }
          const missed = [];
          for (const key of Object.keys(map)) {
            if (!seen[key.toLowerCase()]) missed.push(key);
          }
          return { updated: updated, missed: missed };
        })();
        """;
}

public sealed class Smt7PageWriteResult
{
    public int Updated { get; init; }
    public List<string> MissedPageSkus { get; init; } = [];
    public string? Error { get; init; }
}
