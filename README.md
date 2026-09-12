# 店小秘 × Shopee 定价工作台

独立桌面工具：在同一窗口完成店小秘 SKU 查询与 Shopee 台湾站批量定价，减少两个工具来回切换。

## 功能

1. **店小秘搜索**：WebView2 登录 → 导出 Cookie → 按 SKU 批量查询参考价与重量
2. **导入到定价**：命中结果一键写入定价表（SKU / 成本 / 重量）
3. **Shopee 定价**：对齐官网定价模拟器（台湾站 / 普货 / 蝦皮海外-7-11）

## 使用

1. 运行 `publish\EcommerceWorkbench.exe`（可拷到任意 Windows x64，无需安装 .NET）
2. 在「店小秘搜索」登录并导出 Cookie，粘贴 SKU 后搜索
3. 点 **导入到定价**，切到定价页；填写「默认预期净利润率」后表格净利润率同步并自动计算售价
4. 点 **批量计算** 或按 `F5`（也可逐行改预期净利润后再算）
5. 需要最新运费时点 **刷新费率**

本地数据目录：`%LocalAppData%\EcommerceWorkbench\`

## 重新打包

双击 `publish.bat`，或：

```bat
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o .\publish
```
