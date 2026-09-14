---
name: Shared multi-process DB access for Nuget + LSP
overview: 应用已修复的 JSql 多进程共享访问能力，让 Nuget 服务器（写者）与远程 LSP 服务器（只读读者）能同时访问同一数据库 dist\data\db\nuget。修改两个项目打开数据库的代码以启用共享访问，重新编译并部署到 dist\bin，然后启动双服务器做联调测试。
todos:
  - id: enable-nuget-mp
    content: 在 NugetConfiguration.vb 的 CreateStorageOptions 增加 MultiProcessAccess=True
    status: completed
  - id: enable-lsp-sharedread
    content: 在 Program.vb 增加 Imports JSql.Storage 并以 SharedRead+MultiProcessAccess 打开 NugetStore
    status: completed
  - id: rebuild-deploy
    content: 停止运行中的服务器后重建并部署 Nuget 与 languageserver 到 dist/bin
    status: completed
    dependencies:
      - enable-nuget-mp
      - enable-lsp-sharedread
  - id: update-run-lsp
    content: 删除 dist/lspdb 快照，并将 run_lsp.cmd 改为直接以 --data dist\data 启动 LSP
    status: completed
    dependencies:
      - rebuild-deploy
  - id: start-test
    content: 先后启动 nuget 与 LSP 服务器，验证 LSP 在 nuget 运行下加载类型索引并响应补全
    status: completed
    dependencies:
      - update-run-lsp
---

## 用户需求

用户已修复 JSql 数据库底层代码，使数据库文件现在可被多个进程同时访问。需求分两部分：

1. 同时更新 `src\LSP\languageserver` 与 `src\Nuget` 两个项目的数据库文件访问代码，应用共享（多进程）访问模式。
2. 启动 nuget 服务器与 LSP 服务器，让两者同时访问 `g:\xDoc\dist\data\db\nuget` 进行联调测试。

## 核心目标

- Nuget 服务器作为写者，在保留单进程写入语义的同时，开启多进程访问以便读者可交替读取。
- LSP 服务器作为只读读者，以前文 JSql 提供的 `SharedRead` 锁模式打开同一数据库，与写者并发共存。
- 两个服务器能同时运行：nuget 服务器继续可服务（端口 80），LSP 能加载类型索引并响应补全（端口 8080）。

## 技术栈与既有模式

- 语言/框架：VB.NET（.NET 10）；持久层为项目内 `JSql`（文本 JSONL 存储引擎）。
- 数据库打开入口统一为 `NugetStore(databaseDirectory As String, Optional options As StorageOptions = Nothing)`（`src/Nuget/NugetStore.vb:162`）。
- JSql 已暴露共享访问开关（`g:/JSql/src/JSql/Storage/StorageOptions.vb`）：
- `MultiProcessAccess As Boolean = False`：开启后每条语句结束即释放表级文件锁，允许多进程交替。
- `LockMode As TextStoreLockMode`（`g:/GCModeller/src/runtime/sciBASIC#/Microsoft.VisualBasic.Core/src/Data/Repository/TextStore/TextStoreOptions.vb`）：`Exclusive=0`（写者，与读者互斥）、`SharedRead=1`（多个只读实例可同时持有，仅限只读）、`None=2`（不加锁）。
- `LockConflictPolicy`（默认 Wait）、`LockWaitTimeoutMs=5000`、由 `CreateStoreOptions()` 在 `MultiProcessAccess+Wait` 时启用超时重试。
- 部署：`Nuget.vbproj` 与 `languageserver.vbproj` 均通过后置生成事件把产物复制到 `g:/xDoc/dist/bin`；两项目都以 `<ProjectReference>` 引用 `JSql.vbproj`，重建即包含 JSql 修复。

## 实现方案

### 1. Nuget 写者开启多进程访问

在 `src/Nuget/NugetConfiguration.vb` 的 `CreateStorageOptions()`（第 205 行）增加 `.MultiProcessAccess = True`。锁模式保持默认 `Exclusive`（写者语义），配合 `MultiProcessAccess` 即可在每条语句之间释放锁，允许 LSP 读者在间隙获取 `SharedRead` 锁。其余选项（Merge/Fsync）保持不变。

### 2. LSP 读者以 SharedRead 打开在线库

在 `src/LSP/languageserver/Program.vb`：

- 增加 `Imports JSql.Storage`。
- 将第 45 行 `store = New NugetStore(dbDir)` 改为构造带选项的 `StorageOptions`：`MultiProcessAccess = True`、`LockMode = TextStoreLockMode.SharedRead`，再 `New NugetStore(dbDir, options)`。
- 该选项使 LSP 在读取 API 文档索引与成员明细时以只读共享锁访问，与运行中的 nuget 服务器互不阻塞。

### 3. 重建与部署

- 因之前 `dist\bin\*.dll` 会被运行中的 `Fluteway.exe`/`languageserver.exe` 锁定导致生成失败，重建前必须先停止这两个进程（`taskkill /IM Fluteway.exe /F`、`taskkill /IM languageserver.exe /F`）。
- 依次 `dotnet build src\Nuget\Nuget.vbproj -c Debug` 与 `dotnet build src\LSP\languageserver\languageserver.vbproj -c Debug`，两者均会自动部署到 `dist\bin` 并重建 JSql。

### 4. 调整运行脚本与清理旧快照

- 删除此前为规避独占锁而生成的 `g:/xDoc/dist/lspdb` 快照目录。
- 更新 `g:/xDoc/dist/run_lsp.cmd`：去除 robocopy 快照逻辑，直接以 `--data g:\xDoc\dist\data --port 8080` 启动 `languageserver.exe`（其 `ResolveDatabaseDirectory` 会自动定位到 `dist\data\db`）。

## 性能与可靠性考量

- `MultiProcessAccess` 在语句间释放锁会引入少量锁竞争开销，但 Nuget 服务器为单写者、LSP 为只读，二者负载互补，实测影响可忽略。
- LSP 启动时的 `ApiIndex.Build()` 会做一次较大的 `ReadApiDocIndex` 读取；若恰逢 nuget 服务器正在写包，LSP 会在 `LockWaitTimeoutMs=5000` 内等待而非立即失败，保证健壮性。
- 仅作用于文本后端（JSONL），与既有数据文件布局兼容，无需迁移。

## 目录结构（将要修改/创建/删除的文件）

project-root/
├── src/
│   ├── Nuget/
│   │   └── NugetConfiguration.vb   # [MODIFY] CreateStorageOptions() 增加 .MultiProcessAccess = True
│   └── LSP/
│       └── languageserver/
│           └── Program.vb          # [MODIFY] Imports JSql.Storage；用 SharedRead + MultiProcessAccess 打开 NugetStore
└── dist/
├── run.cmd                    # 不变，启动 nuget 服务器（端口 80）
├── run_lsp.cmd                # [MODIFY] 去掉快照复制，直接以 --data dist\data 启动 LSP
└── lspdb/                     # [DELETE] 删除旧的独占锁变通快照