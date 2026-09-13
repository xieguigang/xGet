---
name: xDoc-文档访问统计
overview: 为 API 文档控制器新增按 nuget 程序包的文档访问量统计（按 UTC 日计数并持久化），并在 about.html 与包详情页的访问统计曲线中加入「文档访问」曲线，同时补充文档访问统计卡片与周期合计。
todos:
  - id: explore-activity-callsites
    content: 用 [subagent:code-explorer] 定位活动统计/图表相关调用点与影响面
    status: completed
  - id: store-doc-activity
    content: NugetStore 新增 package_doc_activity 表、RecordDocView、docViews 聚合与 Stats().docViews
    status: completed
    dependencies:
      - explore-activity-callsites
  - id: service-doc-views
    content: Service 文档页渲染成功后计数，活动接口与 /api/stats 返回 docViews/totalDocViews
    status: completed
    dependencies:
      - store-doc-activity
  - id: frontend-doc-curve
    content: app.js 三线图与绑定，about.html/package.html 新增卡片与周期合计
    status: completed
    dependencies:
      - service-doc-views
  - id: tests-and-readme
    content: ServerDocsTest 增加计数自检，README 同步新表/接口/曲线说明
    status: completed
    dependencies:
      - frontend-doc-curve
  - id: verify-e2e
    content: 用 [skill:playwright-cli] 端到端验证两个页面的文档访问曲线与计数增长
    status: completed
    dependencies:
      - tests-and-readme
---

## 产品概述

在现有 NuGet 程序包服务上，为「随包 API 帮助文档」增加访问量统计能力：访问某个程序包的文档页时，对该包的文档访问计数器 +1；站点统计页（about.html）与程序包详情页（package.html）的活动曲线图新增「文档访问」曲线，about.html 同时新增文档访问总量卡片与周期合计。

## 核心功能

- 新增按「程序包 + UTC 日」持久化的文档访问统计；每个包独立计数，包 id 统一小写、大小写不敏感聚合。
- 仅对「具体程序包」的文档页计数：包帮助文档索引页 `/docs/{id}/{version}/index.html` 与类型内容页 `/docs/{id}/{version}/{type}.html` 各计 1 次；全局文档主索引 `/docs/index.html` 与 `/docs` 不计入；包/版本/类型不存在而返回 404 的请求不计入。
- 活动统计接口扩展：包活动 `/api/activity/package/{id}`、全站活动 `/api/activity/feed` 的每个数据点新增 `docViews`，并返回 `totalDocViews`；`/api/stats` 新增 `docViews` 总量。
- about.html：新增「Total Doc Views」统计卡片；「Feed Activity」三线图新增文档访问曲线；图表下方合计行新增「Doc views (period)」。
- package.html：活动三线图新增文档访问曲线；合计行新增「Doc views (period)」。
- 曲线视觉沿用现有暗色面积折线风格，文档访问使用独立的强调色（金色系），与 downloads（绿）、page views（蓝）区分；图例与 tooltip 同步包含新曲线。

## 视觉与交互效果

三个统计口径以同一时间轴（UTC 连续日）呈现：下载量、页面浏览量、文档访问量。切换 7d/30d/90d 时三条曲线同步刷新，图例可点击隐藏；缺失日期补 0，保证时间轴稳定。about 页新增第 6 张统计卡片与既有卡片保持同一网格与动画节奏。

## 技术栈

- 语言与运行时：沿用 VB.NET / `net10.0`（`src/Nuget`、`src/Readership`、`test`）。
- 服务端：Fluteway/Flute 控制器（`IHttpAppModule` + `<HttpGet>` 特性路由）+ JSql 数据引擎 + `System.Text.Json`。
- 前端：沿用 `dist/wwwroot` 的 `scibasic.css` 与 `assets/js/app.js`（ECharts 面积折线），无新依赖。

## 技术方案

### 关键约束与决策

- **JSql 不支持 ALTER TABLE**（已在 `G:\JSql\src\JSql` 全库确认无 `ALTER|AlterTable|AddColumn`），且 `NugetStore.initialize()` 用 `CREATE TABLE IF NOT EXISTS`，因此**不能给既有 `package_activity` 表加列**（老库不会自动补列，会导致读取报错）。
- 决策：**新增独立的 `package_doc_activity` 表**，并从 `package_activity` 的行模型上做“聚合合并”，把文档访问量并入同一套按天序列，避免控制器与前端做两套数据拼接。

### 数据层（`src/Nuget/NugetStore.vb`）

- 新增表：

```sql
CREATE TABLE IF NOT EXISTS package_doc_activity (
  id INT NOT NULL PRIMARY KEY,
  package_id VARCHAR(200) NOT NULL,
  day VARCHAR(20) NOT NULL,
  visits INT DEFAULT 0
) COMMENT='daily api documentation page view counters'
```

- `DailyActivity` 增加 `Public Property docViews As Long`（默认 0），使 `package_activity` 与 `package_doc_activity` 的聚合结果共用同一行模型。
- 新增 `Private Sub IncrementDocActivity(packageId, day)`：按 `(package_id, day)` 查行，存在则 `UPDATE ... SET visits = visits + 1`，否则 `INSERT`（复用 `incrementActivity` 的写法与 `nextId`/`esc`/`SyncLock` 约定）。
- 新增 `Private Function ReadDocActivityRows() As List(Of DailyActivity)`：读取 `package_doc_activity`（`package_id` 小写化，写入 `docViews`，`downloads/views` 置 0）。
- 新增 `Public Sub RecordDocView(packageId)`：`IncrementDocActivity(packageId, DayKey())`，与 `RecordView` 同构。
- `GetPackageActivity(packageId, days)` / `GetFeedActivity(days)`：在原有聚合基础上，把 `ReadDocActivityRows()` 中满足时间窗口与包过滤（包维度）的行按 `day` 合并进结果（`item.docViews += row.docViews`）。
- `NugetStats` 增加 `docViews As Long`；`Stats()` 增加 `docViews = ReadDocActivityRows().Sum(Function(a) a.docViews)`，并把 `views` 保持原义（仅页面浏览）。
- 全部访问仍走 `SyncLock sync` 串行化，字符串仍经 `esc()` 转义，日期仍用 UTC `yyyy-MM-dd`。

### 控制器（`src/Nuget/Service.vb`）

- 在 `ApiDocPage` 中：先渲染（`RenderPackageIndex` / `RenderTypePage`），**渲染成功（html 非空）后**调用 `store.RecordDocView(id)`；返回 404 的请求不计数。`/docs` 与 `/docs/index.html` 两个全局索引路由保持不变、不计数。
- `activitySeries(activity, days)`：每个补零点与原有点都新增 `{"docViews", item.docViews}`。
- `ApiPackageActivity`：新增 `{"totalDocViews", activity.Sum(Function(a) a.docViews)}`。
- `ApiFeedActivity`：新增 `{"totalDocViews", activity.Sum(Function(a) a.docViews)}`。
- `ApiStats`：`stats` 字典新增 `{"docViews", stats.docViews}`。

### 前端（`dist/wwwroot`）

- `assets/js/app.js`：
- `TREND` 新增 `docs` 颜色（金色 `#d9a94a`），与既有 `downloads`/`views` 并列。
- `trendSeries(points)` 增加 `docViews` 数组；`trendOption(series)` 的 `legend.data` 增加 `'doc views'`，`series` 增加第三条 `areaLine('doc views', series.docViews, TREND.docs)`（tooltip 已按 `params` 循环，自动包含新曲线）。
- `renderActivityTotals(prefix, result)`：新增设置 `{prefix}-activity-doc-views`（`result.totalDocViews`）。
- `applyStats(stats)`：新增设置 `stat-doc-views`。
- `about.html`：`#about-stats` 新增第 6 张卡片（`no` 为 `06`，`value` id `stat-doc-views`，label `Total Doc Views`）；`#activity` 图表标题改为包含 doc views；`#feed-activity-totals` 新增 `<span>Doc views (period) <b id="feed-activity-doc-views">0</b></span>`。
- `package.html`：`#activity` 图表标题改为包含 doc views；`#pkg-activity-totals` 新增 `<span>Doc views (period) <b id="pkg-activity-doc-views">0</b></span>`。
- 文档页模板与 `docs.js` 不需要改动。

## 性能与可靠性

- 每次文档页访问仅新增 1 次 `(package_id, day)` 行的 UPDATE/INSERT，与现有 `RecordView` 等同量级；JSql 为整表读写，写入成本与表规模相关，但本功能只追加小整数行，符合现有小数据量定位。
- 读取侧：包活动仅在 `package_id + 时间窗口` 内聚合；全站活动按天聚合；均在单次 `SyncLock` 内完成，不引入额外 N+1。
- 计数在渲染成功之后执行，保证 404 不产生脏计数；异常由既有控制器错误处理捕获，不影响页面返回。
- 表格与接口均为**只增不改**，不破坏既有 REST/v3 端点与既有页面。

## 架构设计

```mermaid
flowchart LR
  B[浏览器] -->|/docs/{id}/{ver}/index.html 或 /{type}.html| C[Nuget Service.ApiDocPage]
  C --> R[ApiDocPages 渲染]
  R -->|渲染成功| S[NugetStore.RecordDocView]
  S --> DB[(package_doc_activity)]
  B -->|/api/activity/package/id 或 /api/activity/feed| A[Service 活动接口]
  A --> S2[NugetStore 聚合 package_activity + package_doc_activity]
  S2 --> DB
  A -->|points 含 docViews| J[app.js trendOption 三线图]
```

## 目录结构

```
src/Nuget/
  NugetStore.vb            # [MODIFY] 新增 package_doc_activity 表、RecordDocView、docViews 聚合、Stats().docViews
  Service.vb               # [MODIFY] ApiDocPage 成功后计数；activitySeries 增加 docViews；两个活动接口与 /api/stats 增加文档访问量
dist/wwwroot/
  assets/js/app.js         # [MODIFY] 三线图（legend/series/颜色）、合计行与统计卡片绑定
  about.html               # [MODIFY] 新增 Total Doc Views 卡片、图表标题、周期合计 span
  package.html             # [MODIFY] 图表标题、周期合计 span
test/
  ServerDocsTest.vb        # [MODIFY] 增加文档访问计数与聚合的自检断言
README.md                  # [MODIFY] 第 6/7/9 节同步（新表、接口字段、前端曲线）
```

## 关键结构

- 新表 DDL（JSql，风格与现有建表一致）：

```sql
CREATE TABLE IF NOT EXISTS package_doc_activity (
  id INT NOT NULL PRIMARY KEY,
  package_id VARCHAR(200) NOT NULL,
  day VARCHAR(20) NOT NULL,
  visits INT DEFAULT 0
) COMMENT='daily api documentation page view counters'
```

- 活动接口数据点形状（新增 `docViews`）：

```
{ "day": "2026-09-13", "downloads": 12, "views": 30, "docViews": 7 }
```

- 响应新增字段：包/全站活动接口 `totalDocViews`；`/api/stats` 的 `stats.docViews`。

### SubAgent

- **code-explorer**
- Purpose: 定位 `package_activity`/`DailyActivity`/`activitySeries`/`applyStats`/`renderActivityTotals` 及活动接口的全部使用点，确认「只增不改」不遗漏调用方（含 `NugetStatistics`、`PackageClusterAnalysis` 是否间接依赖 `Stats()`）。
- Expected outcome: 精确的调用点与影响面清单，作为改动边界与回归验证依据。

### Skill

- **playwright-cli**
- Purpose: 端到端打开 `about.html` 与 `package.html`，核对「文档访问」曲线、Total Doc Views 卡片、周期合计是否出现且随 7d/30d/90d 切换刷新，并验证访问文档页后计数增长。
- Expected outcome: 可核验的页面截图与 DOM 断言结果，确认新统计链路端到端可用。