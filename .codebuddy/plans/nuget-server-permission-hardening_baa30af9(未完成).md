---
name: nuget-server-permission-hardening
overview: 为 src\Nuget NuGet 服务器实施三项安全加固：(1) 上传接口增加包所有权/命名空间校验，禁止非属主用户为他人包 id 发布新版本；(2) 两个统计重建管理端点要求 official 管理员权限；(3) 注册与密钥重置端点增加按 IP + email 的滑动窗口限流，超限返回 429。
todos:
  - id: store-uploader-query
    content: 在 NugetStore.vb 新增 GetPackageUploaders 方法：内存 OrdinalIgnoreCase 过滤 package_uploaders 表，返回指定包 id 的全部去重上传者 email
    status: pending
  - id: upload-ownership-check
    content: 在 Service.vb 新增 isPackageOwner 并于 uploadPackage 中接入：用 ResolvePackageId 归一化，已存在的包 id 非属主推送返回 403
    status: pending
    dependencies:
      - store-uploader-query
  - id: stats-admin-guard
    content: 为 /api/stats/rebuild 与 /api/stats/clusters/rebuild 追加 IsUserOfficial 校验，非管理员返回 403
    status: pending
  - id: rate-limit-config
    content: 新建 RateLimiter.vb 滑动窗口限流器，并在 NugetConfiguration.vb 注册 register/reset 的 email、ip 限流与窗口秒数参数
    status: pending
  - id: wire-rate-limit
    content: 在 RegisterUser 与 RequestSecretReset 接入 IP+email 双维度限流，超限返回 429 并记录 warning 日志
    status: pending
    dependencies:
      - rate-limit-config
  - id: build-verify
    content: 运行 lint 与 dotnet build 验证 Nuget.vbproj 编译通过且无回归
    status: pending
    dependencies:
      - upload-ownership-check
      - stats-admin-guard
      - wire-rate-limit
---

## 需求概述

针对已审查确认的 3 项安全漏洞，对 g:/xDoc/src/Nuget NuGet 服务器进行权限加固，不改变任何现有正常功能与协议行为。

## 核心修复内容

1. **严重 - 上传接口包所有权/命名空间校验**：`uploadPackage` 当前认证通过后直接 `AddPackage + RecordUploader`，任何已注册用户可为他人已存在的包 id 发布新版本（供应链冒充）。需补充归属校验：包 id 已存在时，仅允许该包任一版本的上传者或 `official` 管理员推送新版本，否则返回 403；全新包 id 首发保持开放（公共仓库惯例）。
2. **高危 - 管理端点权限提升**：`/api/stats/rebuild` 与 `/api/stats/clusters/rebuild`（触发 UMAP+KMeans 全量重计算）目前仅需任意用户 TOTP 即可调用，存在越权与 DoS 风险。需在认证之后追加 `official` 管理员校验，非管理员返回 403。
3. **高危 - 注册/重置限流**：`/api/register` 与 `/api/reset` 无需认证且无任何速率限制，可被用于邮箱轰炸与资源耗尽。需实现按 **IP + email** 双维度滑动窗口限流（同 email 10 分钟内最多 3 次、同 IP 10 分钟内最多 N 次，N 可配置），超限返回 **429 Too Many Requests**。

## 技术方案

### 技术栈

- 现有项目：VB.NET（net10.0），Fluteway httpd 框架反射加载模块，JSql 自研文档数据库存储。
- 不引入任何第三方依赖；限流器使用 .NET 内置 `ConcurrentDictionary` 实现进程内滑动窗口。

### 已验证的实现依据

- `HttpRequest.Remote As String` 已暴露客户端 IP（Flute/HttpMessage/HttpRequest.vb:152），可直接使用。
- `HTTP_RFC.RFC_TOO_MANY_REQUEST = 429`（sciBASIC HTTP_RFC.vb:460）与 `RFC_FORBIDDEN` 均已存在，`res.WriteError(...)` 即可返回对应状态码。
- `package_uploaders` 表按 `package_id + version + email` 记录上传者；JSql 字符串比较区分大小写，包 id/email 比较必须在内存中做 `OrdinalIgnoreCase`（NugetStore.vb 中 GetPackageUploader 注释已确认此约定）。
- `setPackageFlag` 的 `canManagePackage`（Service.vb:1004-1013）是现成的归属校验模式：`owner 匹配 OrElse store.IsUserOfficial(email)`，新增逻辑须与其风格一致。
- `ResolvePackageId(id)` 已用于把用户输入 id 归一化为存储拼写（Service.vb:961），上传校验需同样使用，避免大小写绕过。

### 实现要点

1. **NugetStore.vb 新增 `GetPackageUploaders(packageId)`**：读取 `package_uploaders` 全表后在内存中按 `OrdinalIgnoreCase` 过滤，返回该包 id 所有版本的不重复上传者 email 列表；遵循现有 `SyncLock sync + query/esc` 模式。不新增数据库表。
2. **Service.vb 新增 `isPackageOwner(packageId, email)`**：`official` 或属主（任一版本上传者，忽略大小写）。
3. **uploadPackage 归属校验**：在 `PackageExists(metadata.Id, metadata.Version)` 检查之前/同时，用 `ResolvePackageId` 归一化 id；若该 id 已存在于库中且 `Not isPackageOwner(...)`，返回 `RFC_FORBIDDEN`（403），日志沿用现有 `$"...".warning()` 风格。校验放在所有内容校验之后、`AddPackage` 之前，被拒上传不留半成品（现有"final location after validation"注释约定保持）。
4. **统计重建端点加管理员门禁**：在两个端点的 `auth.Authenticate` 通过后追加 `store.IsUserOfficial(email)` 判断，非管理员返回 403。
5. **RateLimiter.vb（新文件）**：线程安全滑动窗口计数器，`ConcurrentDictionary(Of String, ConcurrentQueue(Of Long))` 按键记录时间戳，`TryAcquire(key)` 判断窗口内次数；惰性清理过期队列防止内存膨胀；键格式 `email:{addr}` 与 `ip:{addr}`。
6. **NugetConfiguration.vb 注册限流参数**（沿用 `intValue` clamp 模式 + `FromConfig` 接线）：`register-email-limit`（默认 3）、`register-ip-limit`（默认 20）、`reset-email-limit`（默认 3）、`reset-ip-limit`（默认 20）、`rate-window-seconds`（默认 600）。
7. **端点接入**：`RegisterUser` 与 `RequestSecretReset` 在读取 email 后、黑名单/存在性检查之前做限流判定（注意不泄露账号存在性：无论 email 是否已注册都计入并统一响应），超限返回 `RFC_TOO_MANY_REQUEST`（429）。

### 性能与可靠性

- 限流判定为 O(1) 均摊（队列惰性出队），无 IO、无锁争用热点；不落库，重启后窗口清零（可接受：限流目标是防轰炸而非审计）。
- `GetPackageUploaders` 沿用现有"小表全读 + 内存过滤"模式（与 `GetPackageUploader` 一致），上传本身已是重操作，额外开销可忽略。
- 修改全部为定点插入/替换，不改既有方法签名，不影响 xGet 客户端合法流程（合法属主上传、管理员重建、正常注册重置均不受影响）。