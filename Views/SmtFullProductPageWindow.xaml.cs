/*
 * 功能说明：用已导出 Cookie 打开店小秘速卖通产品编辑页（/web/smt/edit），并按 SKU 替换变种编码。
 * 主要职责：注入 Cookie、等待变种表 formState.skuListData 出现、写后回读确认；
 *           把速卖通售价、重量和各国相对调价写回编辑页。
 * 创建日期：2026-10-22
 * 修改记录：2026-10-22 回写零售价、包装重量和区域调价
 *           2026-10-22 页面加载完成后通知外部，便于把主窗口重新放到前面
 *           2026-10-22 区域调价按变种 id 同步，并刷新 vxe 表格里的各国单元格
 *           2026-10-22 回写脚本改为直接返回结果，避免 Promise 被读成 0 条
 *           2026-10-23 各国无忧运费直接写入区域调价表正在显示的单元格
 */

using System.IO;
using System.Text.Json;
using System.Windows;
using EcommerceWorkbench.Models;
using Microsoft.Web.WebView2.Core;

namespace EcommerceWorkbench.Views;

public partial class SmtFullProductPageWindow : Window
{
    private bool _coreReady;
    private bool _finderInstalled;

    /// <summary>详情页发生导航（含首次打开）。调用方用来把主窗口重新盖到前面。</summary>
    public event EventHandler? PageLoaded;

    public SmtFullProductPageWindow()
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

    public async Task<SmtFullPageWriteResult> ReplaceSkusAsync(
        IReadOnlyDictionary<string, string> oldSkuToNewSku,
        CancellationToken cancellationToken = default)
    {
        if (oldSkuToNewSku.Count == 0)
            return new SmtFullPageWriteResult { Error = "没有需要替换的 SKU。" };

        await EnsureCoreAsync();
        if (Browser.CoreWebView2 is null)
            return new SmtFullPageWriteResult { Error = "产品详情页尚未初始化。" };

        await WaitUntilSkuTableReadyAsync(cancellationToken);
        var mapJson = JsonSerializer.Serialize(oldSkuToNewSku);
        var raw = await Browser.CoreWebView2.ExecuteScriptAsync(ReplaceSkuScriptPrefix + mapJson + ReplaceSkuScriptSuffix);
        return ParseWriteResult(raw);
    }

    /// <summary>把每个 SKU 的零售价、包装重量和各国相对调价写入当前编辑页。</summary>
    public async Task<SmtFullPageWriteResult> WriteQuotesAsync(
        string quotesJson,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(quotesJson))
            return new SmtFullPageWriteResult { Error = "没有可回写的报价。" };

        await EnsureCoreAsync();
        if (Browser.CoreWebView2 is null)
            return new SmtFullPageWriteResult { Error = "产品详情页尚未初始化。" };

        await WaitUntilSkuTableReadyAsync(cancellationToken);
        var raw = await Browser.CoreWebView2.ExecuteScriptAsync(WriteQuoteScriptPrefix + quotesJson + WriteQuoteScriptSuffix);
        return ParseWriteResult(raw);
    }

    public void SetHint(string text) => HintText.Text = text;

    private async Task EnsureCoreAsync()
    {
        if (_coreReady && Browser.CoreWebView2 is not null)
            return;

        var userData = Path.Combine(AppPaths.DataDirectory, "webview-smt-full");
        Directory.CreateDirectory(userData);
        var env = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
        await Browser.EnsureCoreWebView2Async(env);
        var core = Browser.CoreWebView2
            ?? throw new InvalidOperationException("WebView2 初始化失败。");
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.NavigationCompleted += (_, _) =>
            Dispatcher.BeginInvoke(() => PageLoaded?.Invoke(this, EventArgs.Empty));
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
        var storeFound = false;
        for (var i = 0; i < maxTries; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Browser.CoreWebView2 is null)
                throw new InvalidOperationException("产品详情页已关闭。");

            await Browser.CoreWebView2.ExecuteScriptAsync(FinderScript);
            var raw = await Browser.CoreWebView2.ExecuteScriptAsync(ReadyScript);
            var ready = IsReady(raw, out var storeFoundNow);
            storeFound |= storeFoundNow;
            if (ready)
                return;

            await Task.Delay(500, cancellationToken);
        }

        if (!storeFound)
            throw new InvalidOperationException(
                "未在页面上找到变种表格（skuListData）。请确认打开的是速卖通产品编辑页 /web/smt/edit，且变种已加载完成。");

        throw new InvalidOperationException("页面变种表格尚未加载完成。请确认已登录且详情页已打开，然后重试。");
    }

    private static bool IsReady(string? executeResult, out bool storeFound)
    {
        storeFound = false;
        var json = UnwrapExecuteScriptResult(executeResult);
        if (string.IsNullOrWhiteSpace(json))
            return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("store", out var storeEl) && storeEl.ValueKind == JsonValueKind.True)
                storeFound = true;
            return doc.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    private static SmtFullPageWriteResult ParseWriteResult(string? executeResult)
    {
        var json = UnwrapExecuteScriptResult(executeResult);
        if (string.IsNullOrWhiteSpace(json))
            return new SmtFullPageWriteResult { Error = "页面脚本没有返回结果。" };

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var error = root.TryGetProperty("error", out var errEl) ? errEl.GetString() : null;
            var updated = root.TryGetProperty("updated", out var uEl) && uEl.TryGetInt32(out var n) ? n : 0;
            var verified = root.TryGetProperty("verified", out var vEl) && vEl.TryGetInt32(out var vn) ? vn : 0;
            var adjustUpdated = root.TryGetProperty("adjustUpdated", out var aEl) && aEl.TryGetInt32(out var an) ? an : 0;
            var quoteCount = root.TryGetProperty("quoteCount", out var qEl) && qEl.TryGetInt32(out var qn) ? qn : 0;
            var pageCount = root.TryGetProperty("pageCount", out var pEl) && pEl.TryGetInt32(out var pn) ? pn : 0;
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

            return new SmtFullPageWriteResult
            {
                Updated = updated,
                Verified = verified,
                AdjustUpdated = adjustUpdated,
                QuoteCount = quoteCount,
                PageCount = pageCount,
                MissedPageSkus = missed,
                Error = string.IsNullOrWhiteSpace(error) ? null : error
            };
        }
        catch (Exception ex)
        {
            return new SmtFullPageWriteResult { Error = "解析回写结果失败：" + ex.Message };
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
        window.__dxmSmtPopFindSkuRows = function () {
          function looksLikeSkuRows(list) {
            return Array.isArray(list) && list.length && list[0]
              && (typeof list[0].skuCode !== 'undefined' || typeof list[0].sku !== 'undefined');
          }
          function listFromStore(store) {
            if (!store) return null;
            const bags = [store.formState, store];
            for (const bag of bags) {
              if (!bag) continue;
              if (looksLikeSkuRows(bag.skuListData)) return bag.skuListData;
              if (looksLikeSkuRows(bag.skuDataList)) return bag.skuDataList;
            }
            return null;
          }
          function fromPinia(pinia) {
            if (!pinia) return null;
            try {
              if (pinia._s && typeof pinia._s.values === 'function') {
                for (const store of pinia._s.values()) {
                  const list = listFromStore(store);
                  if (looksLikeSkuRows(list)) return list;
                }
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
          return fromApp();
        };
        window.__dxmSmtPopFindSkuStore = function () {
          try {
            return !!(window.__dxmSmtPopFindSkuRows && window.__dxmSmtPopFindSkuRows());
          } catch (e) {}
          return false;
        };
        """;

    private const string ReadyScript = """
        (function () {
          const rows = window.__dxmSmtPopFindSkuRows && window.__dxmSmtPopFindSkuRows();
          const store = !!(window.__dxmSmtPopFindSkuStore && window.__dxmSmtPopFindSkuStore());
          return { ok: !!(rows && rows.length), store: store };
        })();
        """;

    private const string ReplaceSkuScriptPrefix = """
        (function () {
          const map =
        """;

    private const string ReplaceSkuScriptSuffix = """
        ;
          function lookupExact(sku) {
            if (!sku) return null;
            if (Object.prototype.hasOwnProperty.call(map, sku)) return map[sku];
            return null;
          }
          const rows = window.__dxmSmtPopFindSkuRows && window.__dxmSmtPopFindSkuRows();
          if (!rows || !rows.length) return { updated: 0, verified: 0, missed: Object.keys(map), error: '未找到变种信息表格' };
          const seen = {};
          let updated = 0;
          let verified = 0;
          for (const row of rows) {
            const sku = String(row.skuCode || row.sku || '').trim();
            if (!sku) continue;
            const next = lookupExact(sku);
            if (next == null || next === '') continue;
            const text = String(next);
            row.skuCode = text;
            if (String(row.skuCode || row.sku || '').trim() === text) verified += 1;
            seen[sku] = true;
            updated += 1;
          }
          const missed = [];
          for (const key of Object.keys(map)) {
            if (!seen[key]) missed.push(key);
          }
          return { updated: updated, verified: verified, missed: missed };
        })();
        """;

    private const string WriteQuoteScriptPrefix = """
        (function () {
          const payload =
        """;

    private const string WriteQuoteScriptSuffix = """
        ;
          function money(n) {
            const v = Number(n);
            if (!Number.isFinite(v)) return null;
            return Math.round(v * 100) / 100;
          }
          function kilos(n) {
            const v = Number(n);
            if (!Number.isFinite(v)) return null;
            return Math.round(v * 1000) / 1000;
          }
          function adjustCell(amount) {
            const n = money(amount);
            if (n == null || n === 0) return { direction: '1', price: '0' };
            return { direction: n < 0 ? '-1' : '1', price: Math.abs(n).toFixed(2) };
          }
          function collectStores() {
            const found = [];
            const el = document.querySelector('#app');
            const app = el && el.__vue_app__;
            if (!app) return found;
            const pins = [];
            const gp = app.config && app.config.globalProperties && app.config.globalProperties.$pinia;
            if (gp && gp._s) pins.push(gp);
            const provides = app._context && app._context.provides;
            if (provides) {
              for (const key of Reflect.ownKeys(provides)) {
                const val = provides[key];
                if (val && val._s && typeof val._s.values === 'function') pins.push(val);
              }
            }
            const seen = new Set();
            for (const pinia of pins) {
              if (!pinia || seen.has(pinia)) continue;
              seen.add(pinia);
              try {
                for (const store of pinia._s.values()) found.push(store);
              } catch (err) {}
            }
            return found;
          }
          function asArray(value) {
            if (Array.isArray(value)) return value;
            if (value && Array.isArray(value.value)) return value.value;
            return [];
          }
          function countryAmounts(quote) {
            const src = (quote && quote.countries) || {};
            const map = {};
            for (const key of Object.keys(src)) map[String(key).toUpperCase()] = src[key];
            return map;
          }
          const rows = window.__dxmSmtPopFindSkuRows && window.__dxmSmtPopFindSkuRows();
          const quotes = (payload && payload.rows) || [];
          const nameByCode = {};
          const srcNames = (payload && payload.countryNames) || {};
          for (const key of Object.keys(srcNames)) nameByCode[String(key).toUpperCase()] = srcNames[key];
          if (!rows || !rows.length) return { updated: 0, verified: 0, adjustUpdated: 0, missed: quotes.map(q => q.sku), error: '未找到变种信息表格' };
          const bySku = {};
          for (const quote of quotes) {
            if (quote && quote.sku) bySku[String(quote.sku)] = quote;
          }
          const seen = {};
          let updated = 0;
          let verified = 0;
          const written = [];
          for (const row of rows) {
            const sku = String(row.skuCode || row.sku || '').trim();
            if (!sku || !Object.prototype.hasOwnProperty.call(bySku, sku)) continue;
            const quote = bySku[sku];
            const price = money(quote.price);
            const weight = kilos(quote.weightKg);
            if (price != null) row.skuPrice = price;
            if (weight != null) {
              row.packageWeight = weight;
              if ('grossWeight' in row) row.grossWeight = weight;
            }
            const priceOk = price == null || Number(row.skuPrice) === price;
            const weightOk = weight == null || Number(row.packageWeight) === weight;
            if (priceOk && weightOk) verified += 1;
            seen[sku] = true;
            updated += 1;
            written.push(row);
          }
          let adjustUpdated = 0;
          let adjustError = '';
          const stores = collectStores();
          const adjustStore = stores.find(store => store && typeof store.syncAdjustPriceList === 'function' && store.formState) || null;
          const skuStore = stores.find(store => store && store.formState && Array.isArray(store.formState.skuListData)) || null;
          const skuRows = (skuStore && skuStore.formState.skuListData) || rows;
          const quoteById = new Map();
          function remember(id, quote) {
            if (id == null || !quote) return;
            quoteById.set(id, quote);
            quoteById.set(String(id), quote);
          }
          for (const row of skuRows) {
            const sku = String(row.skuCode || row.sku || '').trim();
            if (sku && Object.prototype.hasOwnProperty.call(bySku, sku)) remember(row.id, bySku[sku]);
          }
          for (const row of written) {
            const sku = String(row.skuCode || row.sku || '').trim();
            remember(row.id, bySku[sku]);
          }
          function quoteOf(id) {
            if (id == null) return null;
            return quoteById.get(id) || quoteById.get(String(id)) || null;
          }
          function quoteAt(index, id) {
            const byId = quoteOf(id);
            if (byId) return byId;
            const row = skuRows[index];
            if (!row) return null;
            const sku = String(row.skuCode || row.sku || '').trim();
            return sku && bySku[sku] ? bySku[sku] : null;
          }
          const codes = [];
          const codeSeen = {};
          for (const row of written) {
            const quote = bySku[String(row.skuCode || row.sku || '').trim()];
            for (const code of Object.keys(countryAmounts(quote))) {
              if (!codeSeen[code]) { codeSeen[code] = true; codes.push(code); }
            }
          }
          function applyPrices() {
            if (!adjustStore || !adjustStore.formState) return 0;
            const list = asArray(adjustStore.formState.adjustPriceList);
            let count = 0;
            for (let index = 0; index < list.length; index++) {
              const item = list[index];
              const quote = quoteAt(index, item && item.skuId);
              if (!quote) continue;
              const amounts = countryAmounts(quote);
              if (!item.modifyData || typeof item.modifyData !== 'object') item.modifyData = {};
              let touched = false;
              for (const code of Object.keys(item.modifyData)) {
                const key = String(code).toUpperCase();
                if (!Object.prototype.hasOwnProperty.call(amounts, key)) continue;
                const cell = adjustCell(amounts[key]);
                const prev = item.modifyData[code];
                if (prev && typeof prev === 'object') {
                  prev.direction = cell.direction;
                  prev.price = cell.price;
                } else {
                  item.modifyData[code] = cell;
                }
                touched = true;
              }
              for (const code of codes) {
                if (!Object.prototype.hasOwnProperty.call(amounts, code) || item.modifyData[code]) continue;
                item.modifyData[code] = adjustCell(amounts[code]);
                touched = true;
              }
              if (touched) count += 1;
            }
            return count;
          }
          if (adjustStore && written.length && codes.length) {
            const form = adjustStore.formState;
            const identities = asArray(skuStore && skuStore.finalSkuIdentityList);
            const idList = identities.length
              ? identities
              : skuRows.map(row => ({ id: row.id != null ? row.id : 'singleSku' }));
            const existing = asArray(form.selectedCountries).map(code => String(code));
            const merged = existing.slice();
            for (const code of codes) {
              if (!merged.some(item => item.toUpperCase() === code)) merged.push(code);
            }
            form.configurationType = 'relative';
            form.selectedCountries = merged;
            const previousById = new Map();
            for (const item of asArray(form.adjustPriceList)) {
              if (item && item.skuId != null) previousById.set(String(item.skuId), item);
            }
            form.adjustPriceList = idList.map((item, index) => {
              const id = item && item.id != null ? item.id : item;
              const old = previousById.get(String(id));
              const quote = quoteAt(index, id);
              const amounts = countryAmounts(quote);
              const modifyData = {};
              for (const code of merged) {
                const key = String(code).toUpperCase();
                if (quote && Object.prototype.hasOwnProperty.call(amounts, key)) {
                  modifyData[code] = adjustCell(amounts[key]);
                } else if (old && old.modifyData && old.modifyData[code]) {
                  const prev = old.modifyData[code];
                  modifyData[code] = { direction: prev.direction, price: prev.price };
                } else {
                  modifyData[code] = { direction: '1', price: '' };
                }
              }
              return { skuId: id, modifyData: modifyData };
            });
            try { adjustStore.syncAdjustPriceList(); } catch (err) {}
            adjustUpdated = Math.max(applyPrices(), writeIntoVisibleTable());
          } else if (quotes.length) {
            adjustError = '未找到区域调价数据，零售价和重量已写入。';
          }
          const missed = [];
          for (const quote of quotes) {
            if (!quote || !quote.sku || !seen[String(quote.sku)]) missed.push(quote.sku);
          }
          function writeIntoVisibleTable() {
            const root = document.querySelector('.adjust-price-table');
            const props = root && root.__vueParentComponent && root.__vueParentComponent.props;
            if (!props) return 0;
            const model = asArray(props.modelValue);
            const skuList = asArray(props.skuList);
            const selected = asArray(props.selectedCountries);
            let count = 0;
            model.forEach((item, index) => {
              if (!item) return;
              const sku = skuList[index];
              const quote = quoteAt(index, item.skuId != null ? item.skuId : (sku && sku.id));
              if (!quote) return;
              const amounts = countryAmounts(quote);
              if (!item.modifyData || typeof item.modifyData !== 'object') item.modifyData = {};
              let touched = false;
              const targets = selected.length ? selected : codes.map(code => ({ value: code, label: nameByCode[code] || code }));
              for (const country of targets) {
                const raw = country && country.value != null ? country.value : country;
                const key = String(raw || '').toUpperCase();
                if (!key || !Object.prototype.hasOwnProperty.call(amounts, key)) continue;
                const cell = adjustCell(amounts[key]);
                const field = country && country.value != null ? country.value : key;
                const prev = item.modifyData[field];
                if (prev && typeof prev === 'object') {
                  prev.direction = cell.direction;
                  prev.price = cell.price;
                } else {
                  item.modifyData[field] = cell;
                }
                touched = true;
              }
              if (touched) count += 1;
            });
            return count;
          }
          function skuLabel(sku) {
            const props = (sku && sku.aeopSKUProperty) || [];
            return props.map(part => {
              const name = String(part.attrVal || '').trim();
              const extra = String(part.propertyValueDefinitionName || '').trim();
              return extra ? name + '(' + extra + ')' : name;
            }).join('');
          }
          function paintAdjustTable() {
            const root = document.querySelector('.adjust-price-table');
            if (!root) return 0;
            const inst = root.__vueParentComponent;
            const props = inst && inst.props;
            const model = asArray(props && props.modelValue);
            const countries = asArray(props && props.selectedCountries);
            const skuList = asArray(props && props.skuList);
            const setup = inst && inst.setupState;
            if (setup) {
              for (const key of Object.keys(setup)) {
                const grid = setup[key] && setup[key].value;
                if (!grid || typeof grid.reloadRow !== 'function') continue;
                const data = typeof grid.getData === 'function' ? grid.getData() : skuList;
                if (typeof grid.refreshColumn === 'function') {
                  try { grid.refreshColumn(); } catch (err) {}
                }
                for (const row of data || []) {
                  try { grid.reloadRow(row); } catch (err) {}
                }
                break;
              }
            }
            const wrapper = root.querySelector('.vxe-table--render-wrapper');
            if (!wrapper || !model.length) return 0;
            const inputValue = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value');
            const selectValue = Object.getOwnPropertyDescriptor(window.HTMLSelectElement.prototype, 'value');
            const fixedRows = wrapper.querySelectorAll('.vxe-table--fixed-left-wrapper .vxe-body--row');
            const bodyRows = wrapper.querySelectorAll('.vxe-table--main-wrapper .vxe-body--row');
            let painted = 0;
            bodyRows.forEach((tr, index) => {
              const fixed = fixedRows[index];
              const attrText = String((fixed && fixed.innerText) || '').replace(/\s+/g, '');
              let modelIndex = -1;
              if (attrText) {
                modelIndex = skuList.findIndex(sku => {
                  const label = skuLabel(sku).replace(/\s+/g, '');
                  return label && attrText.indexOf(label) >= 0;
                });
              }
              if (modelIndex < 0) {
                const rowId = tr.getAttribute('rowid');
                modelIndex = model.findIndex(item => item && String(item.skuId) === String(rowId));
              }
              if (modelIndex < 0) return;
              const data = model[modelIndex];
              if (!data || !data.modifyData) return;
              tr.querySelectorAll('.vxe-body--column').forEach(td => {
                const input = td.querySelector('input');
                if (!input) return;
                const colid = td.getAttribute('colid');
                const header = colid ? wrapper.querySelector('.vxe-header--column[colid="' + colid + '"]') : null;
                const span = header && header.querySelector('.vxe-cell span');
                const title = span ? String(span.textContent || '').trim() : '';
                const country = countries.find(item => item && String(item.label || '').trim() === title)
                  || countries.find(item => item && nameByCode[String(item.value || '').toUpperCase()] === title);
                if (!country) return;
                const cell = data.modifyData[country.value];
                if (!cell || cell.price === '' || cell.price == null) return;
                if (inputValue && inputValue.set) inputValue.set.call(input, String(cell.price));
                else input.value = String(cell.price);
                input.dispatchEvent(new Event('input', { bubbles: true }));
                input.dispatchEvent(new Event('change', { bubbles: true }));
                const select = td.querySelector('select');
                if (select && cell.direction) {
                  if (selectValue && selectValue.set) selectValue.set.call(select, String(cell.direction));
                  else select.value = String(cell.direction);
                  select.dispatchEvent(new Event('change', { bubbles: true }));
                }
                painted += 1;
              });
            });
            return painted;
          }
          if (adjustStore) {
            try { writeIntoVisibleTable(); paintAdjustTable(); } catch (err) {}
            setTimeout(() => {
              try {
                applyPrices();
                writeIntoVisibleTable();
                paintAdjustTable();
              } catch (err) {}
            }, 500);
          }
          return {
            updated: updated,
            verified: verified,
            adjustUpdated: adjustUpdated,
            missed: missed,
            error: adjustError,
            quoteCount: quotes.length,
            pageCount: rows.length
          };
        })();
        """;
}

public sealed class SmtFullPageWriteResult
{
    public int Updated { get; init; }
    public int Verified { get; init; }
    /// <summary>写入区域调价的变种条数。</summary>
    public int AdjustUpdated { get; init; }
    public int QuoteCount { get; init; }
    public int PageCount { get; init; }
    public List<string> MissedPageSkus { get; init; } = [];
    public string? Error { get; init; }
}
