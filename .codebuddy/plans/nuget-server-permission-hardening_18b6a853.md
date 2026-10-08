---
name: nuget-server-permission-hardening
overview: 在原三项安全加固（上传包所有权校验、统计重建端点管理员门禁、注册/重置限流）基础上，新增第四项：为 xConsole 管理命令行添加 `package owner` 命令组（list/add/remove），通过新增的 package_owners 显式属主表手动管理 email 与包 id 的所有权关系，并使 isPackageOwner 判断同时覆盖该表。
todos:
  - id: store-owner-table
    content: 在 NugetStore.vb 新增 package_owners 表（initialize 建表 + TableNames 白名单）及 AddPackageOwner/RemovePackageOwner/GetPackageOwners/GetPackageUploaders 方法
    status: completed
  - id: upload-ownership-check
    content: 在 Service.vb 新增 isPackageOwner 并接入 uploadPackage：ResolvePackageId 归一化，已存在包 id 非属主推送返回 403；canManagePackage 同步纳入显式属主
    status: completed
    dependencies:
      - store-owner-table
  - id: stats-admin-guard
    content: 为 /api/stats/rebuild 与 /api/stats/clusters/rebuild 追加 IsUserOfficial 校验，非管理员返回 403
    status: completed
  - id: rate-limit-config
    content: 新建 RateLimiter.vb 滑动窗口限流器，并在 NugetConfiguration.vb 注册 register/reset 的 email、ip 限流与窗口秒数参数
    status: completed
  - id: wire-rate-limit
    content: 在 RegisterUser 与 RequestSecretReset 接入 IP+email 双维度限流，超限返回 429 并记录 warning 日志
    status: completed
    dependencies:
      - rate-limit-config
  - id: xconsole-owner-command
    content: 在 xConsole Program.vb 的 package 命令组新增 owner list|add|remove 子命令并更新 UsageText 帮助文本
    status: completed
    dependencies:
      - store-owner-table
  - id: build-verify
    content: 运行 lint 与 dotnet build 验证 Nuget.vbproj 与 xConsole.vbproj 编译通过且无回归
    status: completed
    dependencies:
      - upload-ownership-check
      - stats-admin-guard
      - wire-rate-limit
      - xconsole-owner-command
---

## 需求概述

针对已审查确认的安全漏洞对 `g:/xDoc/src/Nuget` NuGet 服务器进行权限加固，并为 `g:/xDoc/src/xConsole` 管理命令行新增包所有权管理命令。共四项工作，均不改变现有正常功能与协议行为。

## 核心修复内容

1. **严重 - 上传接口包所有权/命名空间校验**：`uploadPackage` 认证通过后直接 `AddPackage + RecordUploader`，任何已注册用户可为他人已存在的包 id 发布新版本（供应链冒充）。补充归属校验：包 id 已存在时，仅允许该包属主（任一版本上传者、`package_owners` 显式属主或 `official` 管理员）推送新版本，否则返回 403；全新包 id 首发保持开放。
2. **高危 - 管理端点权限提升**：`/api/stats/rebuild` 与 `/api/stats/clusters/rebuild`（触发 UMAP+KMeans 全量重计算）目前仅需任意用户 TOTP。在认证之后追加 `official` 管理员校验，非管理员返回 403。
3. **高危 - 注册/重置限流**：`/api/register` 与 `/api/reset` 无需认证且无速率限制，可被用于邮箱轰炸。实现按 **IP + email** 双维度滑动窗口限流（同 email 10 分钟内最多 3 次、同 IP 10 分钟内最多 N 次，N 可配置），超限返回 429 Too Many Requests。
4. **新增 - xConsole 包所有权命令**：为 xConsole 添加 `package owner` 命令组（`list|add|remove <id> [email]`），手动将用户 email 与指定包 id 关联或解除关联，修改程序包的所有者关系。

## 多属主模型设计决策

- 新增 `package_owners` 显式属主表（`package_id + email + created`），与 `package_uploaders`（按版本自动记录）并行。
- `isPackageOwner(id, email)` = `official` 管理员 OrElse 任一版本上传者 OrElse `package_owners` 表条目（均 OrdinalIgnoreCase 比较）。
- 与 NuGet 官方多 owner 概念一致；不向 `package_uploaders` 插入哨兵 version 行，避免污染 `GetPackageUploader` 的"最新版本上传者"语义。

## 技术栈

- 现有项目：VB.NET（net10.0），Fluteway httpd 框架反射加载模块，JSql 自研文档数据库。
- 不引入任何第三方依赖；限流器使用 .NET 内置 `ConcurrentDictionary` 实现进程内滑动窗口。

## 已验证的实现依据

- `HttpRequest.Remote As String` 已暴露客户端 IP（Flute/HttpMessage/HttpRequest.vb:152）。
- `HTTP_RFC.RFC_TOO_MANY_REQUEST = 429`（HTTP_RFC.vb:460）与 `RFC_FORBIDDEN` 均已存在，`res.WriteError(...)` 直接可用。
- `package_uploaders` 表按 `package_id + version + email` 记录；JSql 字符串比较区分大小写，包 id/email 比较必须在内存中做 `OrdinalIgnoreCase`（NugetStore.vb GetPackageUploader 注释确认）。
- `setPackageFlag` 的 `canManagePackage`（Service.vb:1004-1013）是现成归属校验参照：owner 匹配 OrElse `store.IsUserOfficial(email)`。
- `ResolvePackageId(id)` 已用于把用户输入 id 归一化为存储拼写（Service.vb:961），防大小写绕过。
- NugetStore 现有模式：`initialize()` 中 `engine.Execute("CREATE TABLE IF NOT EXISTS ...")` 建表；`SyncLock sync` + `query/exec/esc/nextId/dateLiteral` 帮助方法；`TableNames` 白名单（278-284 行）控制 xConsole `tables` 命令可浏览范围。
- xConsole（单文件 Program.vb，约 790 行）：Main 的 Select Case 分发（116-135 行）；`package` 命令组（370-409 行）已有 list/obsolete/hide；`setPackageFlagAction`（415-460 行）展示 `store.PackageIdExists(packageId)` 校验模式；`confirm(message)`（774 行）+ `--yes` 跳过确认；`UsageText` 常量（28-66 行）。xConsole 直接 `Imports Nuget` 以 MultiProcessAccess 打开运行中服务器的同一 JSql 库，写入立即生效，无需重启服务器。

## 实现要点

1. **NugetStore.vb 新增**：

- `package_owners` 表（`id PK, package_id, email, created`），在 `initialize()` 创建，并加入 `TableNames` 白名单。
- `AddPackageOwner(packageId, email)` / `RemovePackageOwner(packageId, email)` / `GetPackageOwners(packageId) As List(Of String)`，遵循 `SyncLock sync + exec/query/esc/nextId` 模式；email 归一化为小写存储；比较在内存 OrdinalIgnoreCase。
- `GetPackageUploaders(packageId)`：读取 `package_uploaders` 全表后内存过滤，返回该包 id 全部版本的不重复上传者 email。

2. **Service.vb 修改**：

- 新增 `isPackageOwner(packageId, email)`：official OrElse 任一版本上传者 OrElse package_owners 条目。
- `uploadPackage`：在内容校验之后、`AddPackage` 之前，用 `ResolvePackageId` 归一化 id；若该 id 已存在且 `Not isPackageOwner(...)`，返回 `RFC_FORBIDDEN`（403），日志沿用 `$"...".warning()` 风格；被拒上传不留半成品。
- `canManagePackage` 同步纳入 `package_owners` 条目（保持 setPackageFlag 语义一致）。
- `ApiStatsClustersRebuild` / `ApiStatsRebuild`：`auth.Authenticate` 通过后追加 `store.IsUserOfficial(email)` 判断，非管理员返回 403。
- `RegisterUser` / `RequestSecretReset`：读取 email 后、黑名单/存在性检查之前做限流判定（不泄露账号存在性：无论 email 是否注册均计入并统一响应），超限返回 `RFC_TOO_MANY_REQUEST`（429）。

3. **RateLimiter.vb（新文件）**：`ConcurrentDictionary(Of String, ConcurrentQueue(Of Long))` 按键记录时间戳的滑动窗口计数器，`TryAcquire(key, maxHits, windowSeconds)` 判定；惰性清理过期队列防内存膨胀；键格式 `email:{addr}` 与 `ip:{addr}`。
4. **NugetConfiguration.vb**：沿用 `intValue` clamp 模式注册 `register-email-limit`（默认 3）、`register-ip-limit`（默认 20）、`reset-email-limit`（默认 3）、`reset-ip-limit`（默认 20）、`rate-window-seconds`（默认 600）。
5. **xConsole Program.vb**：在 `package` 命令组新增 `owner` 子命令，分派到新的 `packageOwnerAction`：

- `package owner list <id>`：打印该包的全部属主（package_owners 条目 + 任一版本上传者，标注来源）。
- `package owner add <id> <email>`：校验 `PackageIdExists`；email 未注册时提示 note（沿用 setUserFlagAction 模式）；写入 `package_owners`。
- `package owner remove <id> <email>`：校验存在并 `confirm()`（支持 `--yes` 跳过）后删除。
- 同步更新 `UsageText` 帮助文本与未知 action 提示。

## 性能与可靠性

- 限流判定 O(1) 均摊（队列惰性出队），无 IO、无锁争用热点；进程内不落库，重启后窗口清零（限流目标是防轰炸而非审计）。
- `GetPackageUploaders`/`GetPackageOwners` 沿用现有"小表全读 + 内存过滤"模式，上传本身已是重操作，额外开销可忽略。
- 全部为定点插入/替换（Service.vb 约 100KB，使用 replace_in_file 定点修改），不改既有方法签名，不影响 xGet / nuget push 合法流程。