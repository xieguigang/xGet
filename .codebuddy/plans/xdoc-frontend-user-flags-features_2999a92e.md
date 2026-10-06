---
name: xdoc-frontend-user-flags-features
overview: 部署前五项更新：依赖网络图按 projectUrl 域名着色、新增反向依赖页面 dependents.html、about/package 活动曲线 docViews 取对数、新增用户信息页 user.html（含用户聚合活动曲线）、user_flags 扩展 demo/banned 标记（banned 禁止上传、demo 在包页显示徽章）。
design:
  architecture:
    framework: html
  styleKeywords:
    - 深色主题
    - 卡片区块
    - 等宽字体数据表
    - 状态徽章
  fontSystem:
    fontFamily: PingFang SC, Segoe UI, sans-serif
    heading:
      size: 24px
      weight: 600
    subheading:
      size: 14px
      weight: 500
    body:
      size: 13px
      weight: 400
  colorSystem:
    primary:
      - "#3fae4a"
      - "#6fa8dc"
    background:
      - "#030303"
      - rgba(255,255,255,.03)
    text:
      - "#f2f2f2"
      - "#9a9a9a"
    functional:
      - "#3fae4a"
      - "#e0c24a"
      - "#ff7a6e"
todos:
  - id: store-flags-queries
    content: NugetStore：user_flags 增加 demo/banned 列（含防御性补列迁移），新增 SetUserFlag/IsUserBanned/GetUserFlags/GetUserPackages/GetUserProjects/GetUserActivityAggregate/GetPackageDependents
    status: completed
  - id: service-endpoints
    content: Service.vb：新增 /api/dependents/{id}、/api/user/{email}、/api/activity/user/{email} 端点，上传链路 banned 403 拦截，详情 JSON 增加 uploaderDemo
    status: completed
    dependencies:
      - store-flags-queries
  - id: xconsole-flags
    content: xConsole：user 命令扩展 demo/banned 设置动作，user list 展示 official/demo/banned 三标记，更新 usage 文本
    status: completed
    dependencies:
      - store-flags-queries
  - id: graph-project-colors
    content: NugetStatistics.BuildDependencyNetwork 节点携带 projectUrl 域名字段；charts.js 按域名分组动态着色与图例，tooltip 显示 project；graph.html note 文案更新
    status: completed
  - id: frontend-pages
    content: 新建 dependents.html 与 user.html（含徽章、project 列表、包表格、聚合活动曲线），package.html 增加 dependents 入口，app.js 实现 docViews log10 变换与 user 页渲染分支，scibasic.css 增加 demo/banned 徽章样式
    status: completed
    dependencies:
      - service-endpoints
  - id: build-verify
    content: 编译全部项目并自查：依赖图 project 着色 JSON、dependents/user 端点返回、banned 上传 403、docViews 对数曲线、demo 徽章显示
    status: completed
    dependencies:
      - xconsole-flags
      - graph-project-colors
      - frontend-pages
---

## 产品概述

对 xDoc NuGet 服务器进行部署前最后一轮功能与前端增强，共五项：依赖网络图按 Project 域名着色、反向依赖独立页面、活动曲线 docViews 对数化、用户信息页面、账号 demo/banned 标记体系。

## 核心功能

- **依赖网络图 Project 着色**：`/api/stats/dependency-network` 的站内节点携带 nuspec `projectUrl` 的域名分组；前端按域名分组着色（同域名同色、无 projectUrl 灰色、外部依赖保留蓝色），图例列出域名分组，tooltip 显示 project 信息。
- **反向依赖页面**：新增 `dependents.html` 与 `/api/dependents/{id}` 接口，列举所有依赖了指定程序包的高层包（含最新版本、下载量、依赖版本范围），`package.html` 提供入口链接。
- **docViews 取对数**：`about.html` 与 `package.html` 的三条访问曲线中，docViews 序列做 log10(1+v) 变换（tooltip 显示原始值、系列名标注 log），解决与其他两条曲线数量级悬殊导致的视觉压扁问题。
- **用户信息页**：新增 `user.html`（`user.html?email=`），显示用户 email、official/demo/banned 徽章、其程序包的 project 列表、程序包列表（可跳转详情），以及该用户全部程序包聚合的 downloads/page views/docViews 三条 echarts 曲线；配套新增 `/api/user/{email}` 与 `/api/activity/user/{email}` 接口；入口为 `package.html` 上传者 email 链接。
- **demo/banned 标记**：`user_flags` 表扩展 `demo`、`banned` 两个布尔列（含对已建库的防御性补列迁移）；banned 账号上传请求返回 403 拒绝（已上传包保留显示）；demo 账号在 `package.html` 上传者行显示 demo 徽章；xConsole 新增 `user demo/banned` 管理命令。

## 技术栈

- 后端：VB.NET net10.0（与现有 Nuget/Readership/xConsole 一致），JSql `SqlEngine` 存储（手动 esc + SyncLock 串行化、按列名映射解析、无 ALTER TABLE）
- 前端：静态 HTML + 原生 JS + echarts（沿用 `data-page` 属性驱动、`fetchJSON`、`esc()` 转义的现有模式），站点 CSS 复用 `scibasic.css`

## 关键实现方案

### 1. 依赖网络图 Project 着色

- `NugetStatistics.BuildDependencyNetwork`：hosted 节点增加 `project` 字段——`Uri.TryCreate(project_url)` 取 `Uri.Host`（解析失败取首段路径前文本），写入节点 JSON
- `charts.js renderDependencyNetwork`：收集站内节点的域名集合构建动态 categories（域名 → PALETTE 按序取色，无 project 灰色 `#9a9a9a`，external 保留蓝色），legend 动态生成域名分组；tooltip 增加 project 行；`graph.html` note 文案同步更新

### 2. 反向依赖

- `NugetStore.GetPackageDependents(packageId)`：读 `package_dependencies WHERE dependency_id`，内存大小写不敏感过滤，合并 `packages` 表取最新版本/下载量
- `Service.vb` 新增 `<HttpGet("/api/dependents/{id}")>` 返回 `{id, total, dependents:[{id, latestVersion, downloads, versionRange}]}`

### 3. docViews 对数化

- `app.js renderTrend` 内对 docViews 序列做 `Math.log10(1+v)` 变换，原始值存闭包数组供 tooltip formatter 还原显示，系列名标注 `(log)`；about/package/user 三页共用同一函数自动生效

### 4. user_flags 扩展与用户 API

- 建表语句增加 `demo BOOLEAN DEFAULT FALSE`、`banned BOOLEAN DEFAULT FALSE`；防御性迁移：try `SELECT demo, banned FROM user_flags`，列缺失时读出旧行 → 重建表 → 回写（数据无损）
- `NugetStore`：`SetUserFlag(email, demo/banned, flag)`、`IsUserBanned(email)`、`GetUserFlags(email)`、`GetUserPackages(email)`（package_uploaders 反查合并 packages）、`GetUserProjects(email)`（包的 project_url 去重+计数）、`GetUserActivityAggregate(email, days)`（该用户全部包的 activity 日合计）
- `Service.vb`：`uploadPackage` 在 TOTP 认证通过后检查 `IsUserBanned` → 403 拒绝；详情 JSON 增加 `uploaderDemo`；新增 `/api/user/{email}`、`/api/activity/user/{email}?days=N`
- `xConsole`：`user demo <email> on|off`、`user banned <email> on|off`，`user list` 展示三个标记

## 设计方案

两个新页面（user.html、dependents.html）完全沿用现有站点设计语言：深色背景（#030303）+ 顶栏（brand/topnav/home-btn）+ `wrap` 主容器 + `eyebrow` 编号章节标签 + 页脚，无需引入新框架。

- **user.html**：账号头部区块（大号 email + official/demo/banned 三枚徽章并排）→ Project 列表（chip-list 样式，同域名聚合，链接到 projectUrl）→ 程序包表格（tablewrap 样式：包名/最新版本/下载量/发布时间，包名链接到 package.html?id=）→ 活动曲线区块（chart-canvas 内 echarts 折线，复用现有 trend 工具条 7/30/90 天切换）。
- **dependents.html**：标题区显示目标包名（`?id=` 参数），正文为 tablewrap 表格（依赖方包名/最新版本/版本范围/下载量），包名链接到对应 package.html；空状态复用 `.empty` 样式。
- **徽章体系**：`.official-badge`（现有绿）新增 `.demo-badge`（琥珀黄）与 `.banned-badge`（红），均为小圆角徽章、与 uploader 行内联排列。
- **依赖网络图**：域名分组图例动态生成于图表右上角，与现有 legend 位置一致；着色沿用 PALETTE 色系保证与暗色背景对比度。

## Agent Extensions

### SubAgent

- **code-explorer**
- Purpose: 实施前精确探明 package.html 依赖章节与 uploader 渲染的插入点、package_dependencies 表查询模式（大小写处理）、xConsole usage 文本结构、about.html/feed-trend 区块结构
- Expected outcome: 确认各修改点的精确行号与现有辅助函数，避免破坏既有渲染逻辑