---
name: xDoc-Nuget-sitemap
overview: 在 src/Nuget 新增 sitemap 生成模块：汇总 dist/wwwroot 静态页、全部包详情页与数据库中的帮助文档伪静态页，在「文档库有变更且服务器空闲」时写入 dist/tmp/sitemap.xml，并提供 /sitemap.xml 伪静态路由与站点主题一致的 dist/wwwroot/assets/sitemap.xsl 样式，最后在各静态页面挂上 sitemap 链接。
design:
  architecture:
    framework: html
  styleKeywords:
    - 深色编辑风
    - 与站点主题一致
    - 极细分割线
    - 绿色强调
    - 等宽数字
    - 克制微动效
  fontSystem:
    fontFamily: Inter
    heading:
      size: 26px
      weight: 600
    subheading:
      size: 13px
      weight: 500
    body:
      size: 14px
      weight: 400
  colorSystem:
    primary:
      - "#3FAE4A"
      - "#2E8B3A"
      - "#1F6B2A"
    background:
      - "#030303"
      - "#0A0A0A"
      - "#101010"
    text:
      - "#F2F2F2"
      - "#9A9A9A"
      - "#6B6B6B"
      - "#4A4A4A"
    functional:
      - "#2A2A2A"
      - "#6FA8DC"
      - "#E0C24A"
      - "#FF7A6E"
todos:
  - id: sitemap-config
    content: 扩展 NugetConfiguration：新增 sitemap 与 tmp 配置项、默认值及校验
    status: completed
  - id: sitemap-generator
    content: 新增 SitemapGenerator.vb：收集首页/静态页/包详情页/文档页 URL 并原子写出 sitemap.xml
    status: completed
    dependencies:
      - sitemap-config
  - id: sitemap-scheduler
    content: 新增 SitemapScheduler.vb：活动时钟、变更指纹与状态文件、空闲定时后台生成
    status: completed
    dependencies:
      - sitemap-generator
  - id: service-sitemap-route
    content: Service.vb 接入 /sitemap.xml 路由、Touch 埋点与变更通知、.xsl MIME 修补；用 [subagent:code-explorer] 核对其余响应写出点
    status: completed
    dependencies:
      - sitemap-scheduler
  - id: sitemap-xsl
    content: 新增 dist/wwwroot/assets/sitemap.xsl，按站点深色主题样式化 sitemap.xml
    status: completed
    dependencies:
      - sitemap-config
  - id: wwwroot-sitemap-links
    content: 在 dist/wwwroot 五个静态页面头部与页脚添加 sitemap.xml 链接
    status: completed
    dependencies:
      - sitemap-xsl
  - id: verify-and-docs
    content: 编译并用 [skill:agent-browser] 验证 sitemap 渲染与链接，更新 README
    status: completed
    dependencies:
      - service-sitemap-route
      - wwwroot-sitemap-links
---

## 产品概述

在现有 xDoc NuGet 程序包站点上新增「站点地图（sitemap）」能力：由服务端模块生成 sitemap.xml，把站点主页与全部帮助文档伪静态页对外暴露，生成动作放在数据库文档变更后且服务器空闲时执行，产物落在 `dist/tmp`；同时提供与站点主题一致的样式表，并通过伪静态路由对外访问，最后在站点静态页面中加上 sitemap 入口。

## 核心功能

- **sitemap 生成模块（位于 Nuget 服务端）**：汇总并输出符合 sitemap 规范的 URL 集合，包含：
- 站点首页与 `dist/wwwroot` 中的全部静态页面（index / about / tags / graph 等）；
- 每个程序包详情页（`package.html?id=...`）；
- 数据库中全部帮助文档伪静态页：全局文档索引页、每个包版本的文档索引页、以及每个类型的内容页。
- **变更检测与空闲生成**：以固定周期检查「帮助文档数据库是否变更」，同时满足「超过可配置秒数无请求」时才生成；产物写入 `dist/tmp/sitemap.xml`，整体过程在后台完成，失败只记录告警，不影响上传与站点访问。
- **伪静态路由**：为 tmp 目录中的 sitemap.xml 提供静态路由，浏览器访问固定地址即可拿到该 XML；文件尚不存在时可即时补生成一次。
- **样式渲染**：`dist/wwwroot/assets/sitemap.xsl` 为 sitemap.xml 提供浏览器端样式化渲染，视觉风格（深色底、面板层次、细分割线、绿色强调、等宽字体呈现链接）与当前站点主题保持一致。
- **站点入口**：`dist/wwwroot` 中的静态页面在头部与页脚链接 sitemap.xml，便于用户与搜索引擎发现。

## 技术栈

- 语言与框架：VB.NET（`net10.0`），沿用现有 `src/Nuget` 类库（由 Fluteway `/run` 反射加载、`IHttpAppModule.Mount` 挂载、`HttpRouter` + `<HttpGet>` 特性路由）。
- 数据访问：复用 `NugetStore`（JSql）已有只读方法，不新增数据表。
- 调度与并发：`System.Threading.Timer`（沿用 `checkpointTimer` / `analysisTimer` 范式）+ 共享原子活动时钟 + `SyncLock`。
- 文本与文件：`StringBuilder` / `StreamWriter` 生成 XML，`File.Move(overwrite:=True)` 原子替换；无新增第三方依赖。
- 样式：XSLT 1.0（浏览器端渲染），复用站点 `assets/css/scibasic.css` 的 CSS 变量与字体。

## 实现方案

**总体思路**：新增两个类完成「生成」与「调度」，在 `Service` 中接入路由与埋点。

1. `SitemapGenerator`（纯生成逻辑，无副作用）：

- 输入：`NugetConfiguration`、`store.ReadAllPackages()`、`store.ReadApiDocIndex()`（不带 payload）。
- URL 组装严格复用现有方案，避免与路由解码不一致：
    - 首页/静态页：扫描 `{Wwwroot}` 下的 `*.html`（跳过 `assets/` 等资源目录，排除参数化的 `package.html`），把 `index.html` 归一为站点根 `/`；文档页由路由提供，故另加 `ApiDocPages.GlobalIndexPath`（`/docs/index.html`）。
    - 包详情页：每个包 id 一条 `package.html?id={encodeURIComponent(id)}`（已确认详情页无版本查询参数，展示最新版）。
    - 文档页：包索引用 `ApiDocPages.PackageIndexUrl(id, version)`；类型页用 `DocUrls.NugetTypeUrl(baseUrl:="", id, version, type_fullname)`，两者都使用 `Uri.EscapeDataString`，与 `ApiDocPages` 中 `Uri.UnescapeDataString(routeValue(...))` 的解码保持对称。
- 输出：`&lt;?xml version="1.0" encoding="utf-8"?&gt;` + `&lt;?xml-stylesheet type="text/xsl" href="/assets/sitemap.xsl"?&gt;` + `&lt;urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9"&gt;`，每条 `&lt;url&gt;/&lt;loc&gt;/&lt;lastmod&gt;`（包/文档页取包 `updated`/`published`，静态页取生成时间）。

2. `SitemapScheduler`（空闲调度与状态）：

- 共享活动时钟：`TouchRequest()` 用 `Interlocked.Exchange` 记录最后请求时刻（Ticks），`LastRequestUtc` 读取；`Service` 的所有响应写出辅助与文件下载端点调用它。
- 变更检测：内存 dirty 标记（上传/删除包时 `NotifyDocsChanged()`）+ 持久化指纹双保险。指纹取 `package_api_docs` 行数与最大 id、`packages` 行数，与上次生成时记录的值比对，**重启后仍能识别变更**；指纹与生成时间写在与 sitemap.xml 同目录的小状态文件（纯文本），避免解析自身生成的 XML。
- 空闲判定：定时器（默认 300s）触发时，若 `enabled`、已配置域名、有变更且 `Now - LastRequestUtc &gt;= idleSeconds`（默认 60s）才生成；生成期间用 `generating` 标记防止重入。
- 容错：整个 tick 包在 `Try/Catch` 中并记 `warning`/`App.LogException`，绝不把服务带崩（沿用既有后台任务范式）；写文件先落 `.tmp` 再 `File.Move(overwrite:=True)`，避免并发读到半成品。

3. `Service` 接入：

- `Mount`：`Directory.CreateDirectory(config.TempDirectory)`；创建调度器并保存到字段（防止被 GC 回收）；调用一次 `.xsl` MIME 修补。
- 新增路由 `<HttpGet("/sitemap.xml")>`：优先读取 tmp/sitemap.xml 并以 `application/xml; charset=utf-8` 返回；文件不存在且已配置域名时在请求线程内受锁补生成一次，仍失败则 404。
- 埋点：在 `writeJson` / `writeRawJson` / `writeHtml` / `writeDocumentPage` 及直接写响应的下载端点中调用 `TouchRequest()`；上传流程 `indexApiDocs` 成功后调用 `NotifyDocsChanged()`。**注意口径**：静态文件请求不经过控制器（`HttpRouter.AppHandler` 先查 `WebFileSystemListener`），因此空闲判定以控制器流量为准，此约束需写入代码注释。

**关键决策与理由**

- 用「指纹 + dirty 标记」而非仅内存标记：重启后仍可识别数据库变化，符合「检测到数据库有变更」的语义。
- 用 tmp 文件 + 路由读取而非每次请求现算：请求路径零计算开销，满足「空闲期生成」与伪静态效果。
- 复用 `ApiDocPages` / `DocUrls` 的 URL 函数而非另写拼接：保证 sitemap 链接与站点内链、路由解码完全一致。
- 不新增数据表：站点地图是派生数据，文件形态即可，避免污染存储层。

**MIME 风险与对策（已核实）**：`.xsl` 不在 `MIME.SuffixTable` 中（表中仅有 `.xslt`），静态文件系统对未知扩展名返回 `application/octet-stream`（`FileSystem.GetContentType`），浏览器会拒绝将其作为 XSLT 样式表应用。因此 `Mount` 中做一次幂等修补：把 `MIME.SuffixTable`（运行时实例为 `Dictionary`）以 `TryCast` 取出并写入 `.xsl -&gt; application/xml`，整段 `Try/Catch` 包裹、失败仅告警。备选方案（若不愿改动全局 MIME 表）：把样式表改为 `.xslt` 命名，或新增 `<HttpGet("/sitemap.xsl")>` 路由以 `text/xml` 提供 wwwroot 中的物理文件。

**性能与可靠性**

- 生成复杂度 O(包数 + 类型文档数)，仅两次无 payload 的全表查询，且在后台空闲期执行，对请求路径无影响；XML 通过 `StreamWriter` 顺序写出，内存占用与条目数线性且可控；`ReadApiDocIndex` 为全量读取，包规模增大时以空闲期单次成本换取请求期零成本。
- 原子替换 + 状态文件写入（同样先临时文件再替换）保证任何时刻读到的都是完整文件。

## 架构设计

```mermaid
flowchart LR
    A[uploadPackage 成功] --&gt;|NotifyDocsChanged| S[SitemapScheduler]
    C[控制器响应写出 TouchRequest] --&gt;|记录最后请求时刻| S
    S --&gt;|定时 Tick: 有变更 且 空闲超时| G[SitemapGenerator]
    G --&gt;|原子写入| T[dist/tmp/sitemap.xml]
    R[GET /sitemap.xml 伪静态路由] --&gt;|读取 或 兜底生成| T
    T --&gt;|xml-stylesheet 指令| X[dist/wwwroot/assets/sitemap.xsl]
    P[dist/wwwroot 静态页面] --&gt;|head/footer 链接| R
```

职责边界：`SitemapGenerator` 只做数据到 XML 的转换（易测试）；`SitemapScheduler` 负责时间、状态与线程安全；`Service` 只做路由接入与埋点，保持既有控制器结构不变。

## 目录结构

```
g:/xDoc/
├── src/Nuget/
│   ├── SitemapGenerator.vb    # [NEW] sitemap 生成模块。职责：收集首页、wwwroot 静态页、包详情页、文档伪静态页 URL，拼装
│   │                          #   sitemaps.org 0.9 规范的 XML（含 xml-stylesheet 指令、lastmod）；复用 ApiDocPages.PackageIndexUrl
│   │                          #   与 DocUrls.NugetTypeUrl 保证编码一致；提供指纹计算（package_api_docs 行数/最大 id、packages 行数）
│   │                          #   与状态文件读写；写文件采用「临时文件 + File.Move(overwrite:=True)」原子替换。
│   ├── SitemapScheduler.vb    # [NEW] 调度与状态模块。职责：共享活动时钟（Interlocked 记录最后请求时刻）、dirty 标记、
│   │                          #   定时器 Tick 中的「有变更 + 空闲超时」判定、生成期间防重入、Try/Catch 容错与日志、
│   │                          #   对外暴露 TouchRequest()/NotifyDocsChanged()/TryReadXml()（缺失时兜底生成）。
│   ├── NugetConfiguration.vb  # [MODIFY] 新增只读属性与配置项：sitemap-enabled（默认 True）、sitemap-base-url（默认空，
│   │                          #   为空时回退 base-url，两者皆无则跳过生成并告警）、tmp（默认 wwwroot 同级的 tmp 目录）、
│   │                          #   sitemap-idle-seconds（默认 60）、sitemap-interval-seconds（默认 300）；同步修改私有构造签名、
│   │                          #   FromConfig 取值与默认值、常量区。
│   └── Service.vb             # [MODIFY] Mount 中创建 tmp 目录、启动调度器并保存字段、修补 .xsl MIME 映射；新增
│                              #   <HttpGet("/sitemap.xml")> 伪静态路由（application/xml，缺失时兜底生成，失败 404）；
│                              #   在 writeJson/writeRawJson/writeHtml/writeDocumentPage 与文件下载端点调用 TouchRequest()；
│                              #   在 indexApiDocs 成功后、以及删除包/文档时调用 NotifyDocsChanged()。
├── dist/
│   ├── tmp/
│   │   ├── sitemap.xml        # [生成产物] 由调度器在空闲期写入（原子替换），供 /sitemap.xml 路由读取。
│   │   └── sitemap.state      # [生成产物] 上次生成的指纹与时间（纯文本，供重启后变更比对）。
│   └── wwwroot/
│       ├── assets/
│       │   └── sitemap.xsl    # [NEW] sitemap.xml 的 XSLT 样式表。职责：浏览器端把 urlset 渲染为站点风格页面
│       │                      #   （复用 scibasic.css 的深色令牌与 Inter/mono 字体、表格化 URL 列表、hover 高亮、
│       │                      #   页首返回首页与文档索引的入口）；静态目录直出，需 correct MIME 才能被浏览器应用。
│       ├── index.html         # [MODIFY] head 增加 rel="sitemap" 链接；页脚 legal 行增加 Sitemap 链接。
│       ├── about.html         # [MODIFY] 同上。
│       ├── tags.html          # [MODIFY] 同上。
│       ├── graph.html         # [MODIFY] 同上。
│       └── package.html       # [MODIFY] 同上（相对路径引用 sitemap.xml，与既有资源引用风格一致）。
└── README.md                  # [MODIFY] 补充 sitemap 能力：新配置项与默认值、/sitemap.xml 路由、dist/tmp 产物、
                               #   新增源文件与 assets/sitemap.xsl，沿用既有中文 + 代码块 + [新增] 标记风格。
```

## 关键代码结构

```
' src/Nuget/SitemapScheduler.vb —— 调度器对外契约（接口级）
Public Class SitemapScheduler
    ' 由 Service 在请求响应路径上调用：记录最后一次请求时刻（线程安全、无锁阻塞）
    Public Shared Sub TouchRequest()

    ' 由 Service 在上传成功 / 文档入库 / 删除包后调用：标记文档库已变更
    Public Sub NotifyDocsChanged()

    ' 由 Service.Mount 调用：创建并持有定时器（必须保存到字段，否则会被 GC 回收而静默停止）
    Public Sub Start()

    ' 供 <HttpGet("/sitemap.xml")> 使用：返回 tmp 中 sitemap.xml 的内容；
    ' 文件缺失且已配置域名时受锁兜底生成一次，仍不可用则返回 Nothing（调用方回 404）
    Public Function TryReadXml() As String
End Class
```

## 设计说明

本次仅新增「sitemap 样式页」这一视觉产物，其设计目标是**与现有站点主题完全一致**，不引入新风格：沿用 `dist/wwwroot/assets/css/scibasic.css` 的深色编辑风令牌（黑底、分层面板、极细分割线、克制的绿色强调、等宽字体呈现链接），并复用站点排版节奏。

**实现方式**：`dist/wwwroot/assets/sitemap.xsl` 用 XSLT 1.0 输出 HTML，`<link>` 引入 `/assets/css/scibasic.css` 以继承字体与基础排版，再以内嵌样式定义 sitemap 专用结构；整体为单列居中布局，与站点内页信息密度一致。

**块设计（自上而下）**

1. 顶栏：左侧品牌标记（favicon + `nuget` + `package server` 小字），右侧导航入口链接回站点首页与 `/docs/index.html` 文档索引；沿用顶栏细底边。
2. 标题区：小号大写字距眉标 `SITEMAP` + 主标题「站点地图」+ 一行说明（收录范围简述），下方细线分隔。
3. 概况条：以等宽数字展示 URL 总数与生成时间（本地时间），沿用站点 meta 条的上/下细线与字距样式。
4. URL 列表：表格呈现每条记录——链接（等宽字体、悬停变强调绿并带下划线位移）、`lastmod`、类型标签（page/package/docs 用小号大写字距徽标区分）。表头大写字距、行间 1px 细线、悬停整行轻微提亮，保持与站点表格一致的手感。
5. 页脚：与站点 legal 行一致的小字排版，附带返回首页与文档索引的文字链接。

**响应式与交互**：宽度自适应，窄屏下表格允许横向滚动（沿用站点 `overflow-x:auto` 处理），链接与行悬停提供 0.2s 平滑过渡；不引入脚本，纯静态渲染以保证抓取与渲染稳定。

## Agent Extensions

### SubAgent

- **code-explorer**
- Purpose: 在实施 `Service.vb` 埋点前，完整枚举该控制器的全部响应写出点与文件下载端点（`writeJson` / `writeRawJson` / `writeHtml` / `writeDocumentPage` / 直接 `res.WriteHeader + SendData` 的下载处），以及调用它们的方法清单，确保 `TouchRequest()` 覆盖无遗漏、且不会破坏仅有 `Shared` 上下文的方法。
- Expected outcome: 一份精确的「需埋点位置清单」（文件、行号、方法、上下文），据此完成埋点且不引入编译错误（`Shared`/实例上下文正确）。

### Skill

- **agent-browser**
- Purpose: 本地以非特权端口启动服务后，验证 `/sitemap.xml` 能被正确返回并由 `assets/sitemap.xsl` 完成样式化渲染（截图确认与站点主题一致），同时检查 `dist/wwwroot` 各静态页面的 sitemap 入口链接可达。
- Expected outcome: sitemap 页面截图与页面文本/链接检查结果（URL 条目数量、样式生效、无 MIME 或 XSLT 加载报错），作为本任务的验收证据。