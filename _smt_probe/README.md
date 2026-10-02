# _smt_probe — 店小秘前端逆向探针

这三个文件是 2026-09-13 前后从店小秘速卖通全托管（smtChoice）前端抓取的压缩 ESM chunk，
用于逆向定位 `Views\Smt7ProductPageWindow.xaml.cs` 注入脚本依赖的 Pinia store 与字段名。
它们不参与编译、不进发布包，仅作研究留档。

| 文件 | 用途 |
|---|---|
| `useComp.js` | 定义 `smtChoiceSkuDataStore`（`defineStore` + `reactive({ skuDataList: [] })`），即注入脚本 `pinia._s.get('smtChoiceSkuDataStore')` 的来源 |
| `skuTable.js` | 变种表列定义（`skuPrice` / `skuCode` / `scItemBarCode` / `packageWeight`）、保存白名单与 0.01~999999 的供货价校验 |
| `edit.js` | smtChoice 编辑页主体；注意其中 **没有** `variationList` —— `ChoiceProductService` 的兜底字段名属于未经证实的猜测 |

维护约定：店小秘前端升级导致注入失效时（工作台会提示「未找到 smtChoiceSkuDataStore」），
先重新抓取对应 chunk 放回本目录，核对 store id 与字段名是否变化，再同步更新注入脚本。
