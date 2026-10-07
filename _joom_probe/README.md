# _joom_probe — 店小秘 JOOM / 速卖通7 前端逆向留档

这些文件是 2026-10-07 从店小秘页面抓取的压缩 ESM chunk（`https://s13.dianxiaomi.com/dxm-web/2026-09/assets/`），
用于核对 `Services\Dianxiaomi\JoomProductService.cs` 与 `ChoiceProductService.cs` 依赖的接口契约。
不参与编译、不进发布包，仅作研究留档。

| 文件 | 用途 |
|---|---|
| `index-C1_3EQeq.js` | JOOM 产品列表页；`searchType` / `productSearchType` 的组装逻辑 |
| `index-D7a5uPQd.js` | JOOM 编辑页；`edit.json` 请求方式、`variantJson` 保存结构、`checkVariantSKUS` 调用方式 |
| `productTitle-r0odmDKP.js` | JOOM 列表行渲染与接口声明（`pageList.json` 等） |
| `type-BN-PRpTK.js` | 状态枚举（draft/offline/online、publishingTiming 等） |
| `index-LfFrbMIL.js` | 速卖通（smtProduct）接口声明：`pageList.json`、`advancedSearch.json` 等 |
| `index-DguhYt7x.js` | 速卖通产品列表页主体（批量操作、`dxmState` 用法） |
| `layout-PQvC5zzD.js` / `useSidebarStore-BM7CM-6F.js` | 速卖通产品页布局与侧栏 store |
| 其余 | 上述 chunk 的依赖桩（vue / lodash / vxe），仅用于让引用关系可读 |

## 已核实的接口契约（2026-10-07 实测）

### JOOM

1. **`GET /api/joomProduct/edit.json?id=<idStr>`** —— 详情，`id` 取列表行的 `idStr`。
   - `data.product.variants[]` 是全量变种，每项含 `sku`；`variantJson` 在详情里为空串。
   - `data.product.variantSize` 恒为 `0`，**不能**当变种总数用。
   - 无效 id 返回 `{"code":0,"data":{}}`（不是报错）。
2. **`POST /api/joomProduct/pageList.json`** —— 列表（form-urlencoded）。
   - `searchType`：`0`=Parent SKU、`1`=SKU（页面默认 `0`）；`productSearchType`：`1`=精确、`2`=模糊。
   - **列表行的 `variants` 数组只回前几个变种**：实测 `variantSize=7` 而 `variants` 只有 5 项，
     漏掉的 SKU 必须读 `edit.json` 才能核对 —— 这是「部分命中」漏检的根因。
   - 列表行的 `sku` 字段为空串，不能作为 SKU 来源；`parentSku` 有时只是其中某个变种的 SKU。
   - 搜索按行匹配时，`searchValue` 用**完整变种 SKU** 命中更准（`J0071-1` → 1 条），
     用剥离后的基础货号（`J0071`）会带回大量无关商品（实测 7 条）。
3. **`POST /api/joomProduct/checkVariantSKUS.json`** —— 店小秘自带的变种 SKU 重复校验。
   - 参数：`sku=<SKU 用 &amp;#&amp; 连接>&shopId=<店铺>&productId=<当前商品 idStr>`；返回冲突 SKU 数组。
   - 实测在本账号数据上总是返回 `[]`（含自家 SKU、不带 `productId` 也一样），
     因此**没有**采用它做查重，仍走「列表搜索 + 详情全量比对」。
4. **含「+」的组合 SKU（实测该账号大量存在）** —— 列表/详情的 `variants[].sku` 会是
   `J0071+J0043-01`、`J0075+J0043-1` 这种形式，它是店小秘里的**一个**变种 SKU。
   - **查重全程不按「+」拆分**：拆开会让 `J0043-1` 被当成「只提供 `J0075+J0043-1` 的产品」占用
     （实测 3 个假阳性），也会把大量无关商品拉进候选集（搜 `J0076` 回 5 条，搜完整组合货号只回 1 条）。
   - 搜索关键词与比对都用**完整 SKU**：实测 `searchValue=J0076+J0043-1` 精确返回该产品，
     多值批量（`J0076+J0043-1,J0071+J0043-1`）同样成立，故不需要拆分。
   - 代码中已删除 `ExpandCompositeSkus`，避免再被误用。

### 速卖通7（smtChoice / smtProduct）

1. **`GET /api/choiceProduct/edit.json?id=<id>`** —— 详情。
   - `data.product.variationList[]` 是变种，SKU 在 **`skuCode`**（不是 `sku`）；`scItemBarCode` 是货品条码。
   - `data.product.shopId` 是**当前店铺**，速卖通7 的查重只在该店铺内进行。
   - **重量**：优先按货品条码在货品信息里匹配；不少全托管产品 `scItemBarCode` 是空串，
     此时回退用变种自带的 `packageWeight`（字符串形态，如 `"0.12"`，单位 kg）。
     只认条码会让整列重量都带不出来 —— 这是实测踩到的坑。
2. **`POST /api/choiceProduct/pageList.json`** —— **查重入口**（按 SKU 搜索，查到即重复）。
   - 参数（实测可用）：`sortName=2&pageNo=1&pageSize=50&total=0&searchType=1&searchValue=<SKU>`
     `&productSearchType=1&shopId=<店铺>&dxmState=online|offline|draft&fullCid=`
     `&sortValue=2&productType=&productStatus=ONLINE|OFFLINE|DRAFT`。
   - 返回 `data.page.totalSize`（命中总数）与 `data.page.list[]`（`idStr`、`subject`、`variationList`…）。
   - **多值（英文逗号分隔）是「或」语义**：实测 `searchValue=A,B,C` 只要有一个命中就返回结果，
     且**不告诉命中的是哪一个**。因此工具按「对分法」逐 SKU 定位：
     整批没命中就整批排除，命中且不止一个值就拆两半递归，k 个占用约 O(k·log n) 次请求。
   - 空 `searchValue` = 不按 SKU 过滤（用于拉店铺列表，不要用于查重）。
   - 注意：同一店铺的在线产品在这里有 581 条，而 `smtProduct/pageList.json` 对
     `shopId=8026031` 返回 0 条 —— **两者口径不同，查重必须用 choiceProduct 这条**。
3. **`POST /api/smtProduct/pageList.json`** —— 速卖通产品列表（另一套口径，仅用于对照）。
   - 参数：`pageNo`、`pageSize`、`shopId`（`-1`=全部）、`dxmState`。
   - 行的 `skuCode` 恒为空；变种在 `smtProductSkuList[].skuCode` 或 `aeopAeProductSKUs`（JSON 文本）。
4. **SKU 写回**：`Views\Smt7ProductPageWindow.xaml.cs` 的 `ReplaceSkusAsync` 通过页面注入
   （`window.__dxmSmtFindSkuRows` 定位变种行）替换 `skuCode`，并做写后回读确认。

## 一次实测对比（当前产品 `184807701449016837`，7 个变种）

搜索基础货号 `J0043,J0071,…,J0076` 返回 26 条：

| 口径 | 命中条数 | 说明 |
|---|---|---|
| 拆「+」后匹配（修复前） | 10 | 多报 3 个产品的 `J0043-1`（假阳性） |
| 完整变种精确匹配（修复后） | 7 | 仅 `9960613368182025` 真正占用了这 7 个 SKU |

## 「搜索SKU」去后缀口径（两个标签页一致）

只去掉**末尾的通用后缀**，保留颜色后缀。原因（实测）：

- 速卖通7 商品库里的货号带颜色（`4xJ0202-grey`、`J0242-purple`、`8xJ0243`、`E0258`）；
- 旧口径（按第一个「-」截断）会把颜色一起去掉，实测参考价命中 **0/16**；新口径 **16/16**；
- JOOM 侧同样：旧 16/24 → 新 24/24。

维护约定：店小秘前端升级导致接口变化时，先按上表重新抓取对应 chunk，
核对请求参数与 `variants` / `variantSize` / `smtProductSkuList` 字段，再同步改对应 Service。
