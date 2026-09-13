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

## 用户需求（原文）

「在这里 nuget 服务器所依赖的 jsql 存储似乎并没有在系统空闲的时候将 wal 日志数据落盘到数据表文件中，请帮助我看看 jsql 的源代码，帮助我看看是什么原因导致的，并尝试做修复」

## 澄清结果（用户已确认）

- **授权修改工作区之外的依赖源码**：可直接修改 `G:\JSql\src\JSql` 与 Core 中 `Data\Repository\TextStore` 的源码并重新构建验证。
- **修复策略（全选）**：
  1. 修复空闲合并调度 —— 让空闲 30s 自动 checkpoint 真正生效，并让失败可见（日志/异常不再静默）；
  2. 修复并发下的合并失败 —— `Merge` 遇到未完成的读枚举改为延后重试，而不是抛异常导致永久失效；
  3. xDoc 侧定时 CHECKPOINT —— Nuget 控制器增加低频定时任务显式执行 CHECKPOINT 作为兜底；
  4. xDoc 侧可调阈值 —— Nuget 构造 `SqlEngine` 时传入自定义 `StorageOptions`（如空闲 10s、操作数阈值）。

## 已观察到的现象（实测）

在 `dist/tmp/e2e-data/db/nuget/`（服务运行数分钟、含长时间空闲）：

| 文件 | 长度 |
| --- | --- |
| `package_doc_activity.jsonl` / `.wal` | 0 / 2062 |
| `packages.jsonl` / `.wal` | 0 / 836 |
| `package_api_docs.jsonl` / `.wal` | 0 / 194726 |
| `package_activity.jsonl` / `.wal` | 0 / 123 |
| `users.jsonl` / `.wal` | 0 / 303 |
| `*.idx` | 32（仅文件头） |

WAL 内容为连续 splice 记录（`{"op":"sp","pos":1,"del":1,"l":[...]}` 中 `visits` 递增到 21）。
即：**写入只落 WAL，从未合并回数据文件，数据文件恒为 0 字节**；重启靠 WAL 重放可恢复（不丢数据），但 WAL 只增不减。

## 核心功能（本次交付）

1. 可复现、可观测的 WAL checkpoint 诊断手段（把当前被丢弃的失败信息暴露出来）；
2. 修复 JSql 空闲合并调度，使 checkpoint 在空闲后真正执行；
3. 修复 `Merge()` 在存在未完成读枚举时抛异常导致永久失效的问题；
4. 在 xDoc（Nuget 服务）侧提供显式定时 CHECKPOINT 兜底与可配置的落盘阈值；
5. 构建 + 端到端验证：空闲后数据文件非空、WAL 被清空、并发读写下不卡死、重启后数据一致。


## 技术栈

- 语言/运行时：VB.NET / `net10.0`。
- 涉及三个代码库（JSql 与 Core 位于工作区之外，**用户已授权修改**）：
  - `G:\JSql\src\JSql\JSql.vbproj`（存储/引擎层，RootNamespace `JSql`，命名空间 `JSql.Engine`、`JSql.Storage`）；
  - `G:\GCModeller\src\runtime\sciBASIC#\Microsoft.VisualBasic.Core\src\Core.vbproj` 的 `Data\Repository\TextStore`（`TextLineStore.vb`、`WAL.vb`）；
  - `g:/xDoc`（`src/Nuget/NugetStore.vb`、`Service.vb`、`NugetConfiguration.vb`）。
- 构建/验证：`dotnet build`；复现可用 `G:\JSql\test\test.vbproj`、`G:\JSql\src\Repl\Repl.vbproj`，或 xDoc 自带 `test` 子命令机制（`test/Program.vb` 分派）。

## 现状：机制其实是完整的

- `SqlEngine.SetStorage`（`Engine/SqlEngine.vb` L72-82）会 `New IdleMergeScheduler(provider.Sessions, Storage)` 并 `_scheduler.Start()`。
- `SqlEngine.Execute`（L88-100）用 `CheckpointScheduler.EnterBusy()` / `Finally ExitBusy()` 包裹每条语句。
- `StorageOptions`（`Storage/StorageOptions.vb`）默认 `MergeIdleSeconds = 30`、`MergeAfterOperations = 2000`、`FsyncEachWrite = False`。
- `IdleMergeScheduler.OnTick`（L83-131）：`CompareExchange` 互斥 → `_busy > 0` 则 Return → `MergeOverloaded()` → `IdleSeconds < MergeIdleSeconds` 则 Return → `_pool.MergeAll()`。
- `TableSessionPool.MergeAll()`（L135-150）逐 session：`If force OrElse session.HasPendingChanges Then session.Merge()`。
- `TextLineStore.Merge()`（Core，L509-525）才是真正落盘：清空前写 `mb/md` 标记 → 追加或全量重写 → `_wal.ClearLog()` + `ResetPendingState()`。
- 还有人工入口：`CHECKPOINT [TABLE t]`（`Engine/Executor.vb` L908-916）、`SqlEngine.MergeAll(force)`、`SqlEngine.MergeTable(db, table)`、`SHOW STORAGE`（`TextFileStorage.DescribeStorage` L303-323，可看 Pending/WalBytes/DataBytes）。

## 已确认的根因线索（复现阶段需证实/证伪）

### 线索 1（已确认）：失败完全静默 —— 这是「看不到任何现象」的直接原因

全仓搜索 `AddHandler.*Info` 只命中两条传播链：`TextTableSession`→`_store.Info`、`TableSessionPool`→`session.Info`。
`IdleMergeScheduler.Info`（`checkpoint failed: ...`、`checkpoint: merged N table(s)`）与 `TableSessionPool.Info`（`merge failed for <table>: ...`）**没有任何最终订阅者**，
`RaiseEvent` 在无订阅者时是空操作，所以 checkpoint 的异常与成功信息都被永久丢弃。

### 线索 2（已确认）：`Merge()` 在并发读时直接抛异常，且异常被吞掉后进入死循环

`TextLineStore.Merge()` L512-514：

```vb
If _activeReaders > 0 Then
    Throw New InvalidOperationException("存在未完成的 ReadLines() 枚举，请先完成枚举再合并。")
End If
```

`ReadLines()` / `ReadLines(start, count)` 是 `Iterator`，用 `Interlocked.Increment/Decrement(_activeReaders)` 计数。
一旦某次 tick 撞上正在进行的读枚举：异常 → 被 `MergeAll` 的 `Catch` 吞掉 → `merged = 0` → `OnTick` 调用 `Touch()` **重置空闲计时** → 30s 后重试，
如果读枚举长时间存活或计数泄漏，就变成「永远静默失败」，正好对应「数据文件恒为 0、WAL 一直涨」。

### 线索 3（待证实）：空闲窗口是否真的达成

需用 `IdleMergeScheduler.IdleSeconds` / `BusyCount` / `LastMergeTime` 打点确认。
已知干扰项：xDoc 的 `PackageClusterAnalysis` 定时器每 `cluster-interval`（默认 30）分钟执行一次 SQL 会 `Touch()`；理论上不阻止 30s 空闲，但需实测排除其它调用点。

### 线索 4（待证实）：合并路径本身是否抛错

需确认走的是 `MergeFastAppend`（`_baseLineCount = 0` 或纯追加 → True）还是 `MergeFullRewrite`，
以及 `WriteIndexFile` / `SwapFile` / `AppendMergeBegin` 是否抛异常（同样会被静默吞掉）。

### 线索 5（已确认）：宿主从不 Dispose

`NugetStore` 里 `Private ReadOnly engine As SqlEngine`，`New SqlEngine(databaseDirectory)`（**未传 StorageOptions**），全仓无 Dispose 调用；
Fluteway 结束进程时不会走 `Sessions.DisposeAll()` 的最终合并。数据不丢（WAL 重放），但 WAL 只增不减。

## 实现方案（按用户全选的四项策略）

### 阶段 A：先「复现 + 可观测化」，再动手改逻辑（关键，避免盲目修改）

在 xDoc 的 `test` 工程里新增一个诊断子命令（沿用 `test/Program.vb` 的 `Select Case command` 分派风格）：

- 在临时目录 `New SqlEngine(root, New StorageOptions With {.MergeIdleSeconds = 3, .MergeAfterOperations = 5, .Verbose = True})`；
- **订阅** `engine.CheckpointScheduler.Info` 与 `engine.Sessions.Info`（把静默信息打印出来）；
- `CREATE DATABASE / USE / CREATE TABLE` 后插入若干行，并做一次 `UPDATE`（让表不再是纯追加布局）；
- 每 2s 打印一次：`IdleSeconds`、`BusyCount`、`LastMergeTime`、`.jsonl` 大小、`.jsonl.wal` 大小；
- 最后执行一次 `CHECKPOINT` 并再次打印，用于区分「调度没触发」与「合并本身失败」。

判定标准：空闲超过 `MergeIdleSeconds` 后 `.jsonl` 应非空且 `.wal` 被截断为 0。

### 阶段 B：修复 JSql 空闲合并调度（策略 1）

- 让失败可见：`SqlEngine` 增加诊断出口（例如把 `IdleMergeScheduler.Info` / `TableSessionPool.Info` 转发到一个可订阅的 `SqlEngine.Info` 事件，或在 `StorageOptions.Verbose = True` 时写 `Console.Error`），使 checkpoint 的成功/失败不再被吞掉。
- 修复「空闲窗口被反复重置」：`OnTick` 中因异常/`merged = 0` 时不应简单 `Touch()` 吞掉重试语义，应区分「无待合并数据」（可重置）与「合并失败」（保留失败计数、缩短重试间隔、输出诊断）。
- 修复后必须保证：空闲 `MergeIdleSeconds` 后 `MergeAll()` 真正执行，`.wal` 被清空。
- 同时确认 `Start()` / `_busy` 计数在并发 SQL 下不会永久卡住（`EnterBusy`/`ExitBusy` 已用 `Try/Finally`，需实测 `BusyCount` 归零）。

### 阶段 C：修复并发下的合并失败（策略 2）

修改 Core 的 `TextLineStore.Merge()`（`G:\GCModeller\...\TextStore\TextLineStore.vb` L509-525）：

- 把 `_activeReaders > 0` 的 `Throw InvalidOperationException` 改为**延后重试**语义（例如 `Merge()` 返回「有读者，稍后重试」的结果，或由 `MergeAll` 下一 tick 重试且**不重置空闲计时**），避免一次并发读就让 checkpoint 永久失效；
- 保持崩溃安全：仍然坚持「先 WAL、后内存」与 `mb/md` 标记，不得绕过 `ClearLog/ResetPendingState`；
- 保持 `Merge()` 对 `_gate` 的加锁语义，不引入新的锁竞争。

> 注意：`TextLineStore` / `WAL` 属于 Core，被 GCModeller 其它工程共享，改动要保持向后兼容（公共 API 行为不变，仅把「抛异常」改成「可重试」）。

### 阶段 D：xDoc 侧兜底（策略 3 + 4）

- `NugetStore.New(databaseDirectory)`：改为接受可选 `StorageOptions`（或由 `NugetConfiguration` 提供 `db-merge-idle-seconds` / `db-merge-operations`，复用现有 `intValue(name, fallback, min, max)` 助手），构造 `New SqlEngine(dir, options)`；默认建议空闲 10s、操作数阈值沿用或调小。
- `Service.Mount` 中新增一个低频后台定时器（与 `analysisTimer` 同样的写法：字段保存以防 GC），周期执行 `engine.MergeAll(force:=False)`（等价于 `CHECKPOINT`），并把结果写入日志（沿用现有 `.info()` / `.debug()` / `App.LogException` 约定）。
- 该兜底独立于 JSql 修复，即使调度器未来再次失效也能保证 WAL 不会无限增长。

## 性能与可靠性

- checkpoint 只在空闲窗口或操作阈值触发，单次成本为「追加新字节」（`MergeFastAppend`，O(新增字节)）或全量重写（O(表大小)），与现有设计一致，不引入额外 N+1。
- 延后重试必须有上界/退避，避免每 1s 空转刷日志；诊断输出需限量（避免日志风暴）。
- 合并本身必须保持崩溃安全：先写 WAL、`mb`/`md` 标记、原子换文件；任何改动不得削弱这一点。
- 宿主兜底定时器周期应显著大于 `MergeIdleSeconds`（例如 1~5 分钟），避免与 JSql 调度器互相竞争。

## 目录结构

### JSql（`G:\JSql`，工作区外，已授权）
```
src/JSql/Storage/IdleMergeScheduler.vb   # [MODIFY] 失败/成功诊断不再静默；修复空闲窗口被反复重置；区分「无数据」与「合并失败」
src/JSql/Storage/TableSessionPool.vb     # [MODIFY] MergeAll/MergeOverloaded 的异常与结果可被外部观测（Info 事件/返回值）
src/JSql/Engine/SqlEngine.vb             # [MODIFY] 转发 checkpoint 诊断（新增 Info 出口或 Verbose 输出）；必要时暴露 Checkpoint() 便捷方法
src/JSql/Storage/StorageOptions.vb       # [MODIFY-可选] 诊断开关/默认阈值调整
```

### Core TextStore（`G:\GCModeller\...\Data\Repository\TextStore\`，工作区外，已授权）
```
TextLineStore.vb                         # [MODIFY] Merge() 在 _activeReaders > 0 时改为延后重试（不再抛异常导致永久失效）
WAL.vb                                   # [只读参考] 确认 ClearLog/ResetPendingState/AppendMergeBegin/Done 语义不变
```

### xDoc（工作区内）
```
test/WalCheckTest.vb                     # [NEW] walcheck 复现/诊断子命令（订阅 Info、打点空闲与文件大小）
test/Program.vb                          # [MODIFY] 注册 walcheck 子命令
src/Nuget/NugetStore.vb                  # [MODIFY] 构造 SqlEngine 时传入自定义 StorageOptions；提供 Checkpoint() 供控制器调用
src/Nuget/NugetConfiguration.vb          # [MODIFY] 新增 db-merge-idle-seconds / db-merge-operations / db-checkpoint-minutes 配置键
src/Nuget/Service.vb                     # [MODIFY] Mount 中新增低频 CHECKPOINT 定时器（字段保存防 GC），并记录日志
README.md                                # [MODIFY] 说明 WAL/checkpoint 行为、配置项与运维建议
```

## 关键代码结构（现状，改动基线）

`IdleMergeScheduler.OnTick` 的关键片段（L110-125）：

```vb
Dim idleSeconds As Double = IdleSeconds

If idleSeconds < _options.MergeIdleSeconds Then
    Return
End If

Dim merged As Integer = _pool.MergeAll()

If merged > 0 Then
    _lastMerge = Date.UtcNow
    RaiseEvent Info("checkpoint: merged " & merged & " table(s) after " & CInt(idleSeconds) & "s idle")
Else
    ' nothing to merge, the next idle window starts over
    Touch()          ' ← 合并失败也被当作「没有可合并数据」而重置空闲计时
End If
```

`TextLineStore.Merge()` 的关键片段（L509-515）：

```vb
Public Sub Merge()
    SyncLock _gate
        EnsureOpen()
        If _activeReaders > 0 Then
            Throw New InvalidOperationException("存在未完成的 ReadLines() 枚举，请先完成枚举再合并。")  ' ← 并发读即失败
        End If
        If _wal.PendingOperationCount = 0 AndAlso _wal.Length = 0 Then Return
```

## 验证步骤

1. `dotnet build G:\JSql\src\JSql\JSql.vbproj` 与 Core 通过；
2. 运行 `test walcheck`：应看到空闲后 `data > 0`、`wal = 0`，并看到 checkpoint 诊断输出；并发读写场景下不出现永久失败；
3. 运行 `G:\JSql\test\test.vbproj`（若有存储相关用例）确保不回归；
4. xDoc 端到端：启动 Fluteway + Nuget（`--data` 指向临时目录）→ 上传包、访问文档页产生写入 → 静置 > 配置阈值 → 检查 `db/nuget/*.jsonl` 非空且 `*.wal` 为 0；再用 `SHOW STORAGE` 或文件大小复核；
5. 重启服务后数据一致（WAL 重放路径仍正确）。


## Agent Extensions

### SubAgent

- **code-explorer**
- Purpose: 在修改 JSql / Core 之前，全面核对 `TextLineStore.Merge`、`_activeReaders`、`IdleMergeScheduler`、`TableSessionPool.Info` 在三个代码库（JSql、Core、xDoc）中的全部调用点与依赖方，确认把「抛异常」改为「延后重试」不会影响其它使用者（Core 被 GCModeller 大量工程共享）。
- Expected outcome: 得到精确的调用点/影响面清单，确保改动向后兼容、不破坏既有行为。

### Skill

- **playwright-cli**
- Purpose: 修复后在端到端验证阶段，用浏览器访问 Nuget 站点（首页、包详情页、/docs 文档页）产生真实写入与并发读，配合服务端日志确认 checkpoint 定时生效且页面功能无回归。
- Expected outcome: 得到可核验的页面访问结果，确认在真实读写压力下 WAL 能正常落盘且站点功能正常。
