# Remote VB.NET LSP (VS Code 客户端)

这个 VS Code 扩展把 `src/LSP/lsp.ts` 封装成了可用的插件，通过 TCP 连接到
`src/LSP/languageserver` 实现的远程 VB.NET 脚本语言服务器，为 `.vb` / `.vbs`
脚本提供智能提示（补全、悬停、签名帮助）。此外，扩展还内置了一个**最小 DAP
调试适配器**（类型 `vbnet`），可直接调用 `vbs.exe` 宿主执行 VB 脚本并查看输出。

## 工作原理

扩展本身不做任何分析，只负责：

1. 把 `.vb` / `.vbs` 文件注册为 `vbnet` 语言。
2. 以 TCP 连接到配置好的语言服务器（默认 `127.0.0.1:8080`）。
3. 把 VS Code 的 LSP 请求（completion / hover / signatureHelp 等）转发给服务器。

真正的 API 文档来自 `NugetStore` 里的 nuget 文档数据库，由语言服务器加载。

## 构建

需要 Node.js (LTS) 与 npm。

```powershell
cd src\LSP\vscode-client
npm install
npm run compile
```

编译产物在 `out/extension.js`。

## 测试（F5 调试，最简单）

1. 用 VS Code 打开 `src/LSP/vscode-client` 这个文件夹（作为工作区）。
2. 先启动语言服务器（见下文）。
3. 按 `F5` 启动“扩展开发宿主”（Extension Development Host）。
4. 在宿主窗口里打开任意 `.vb` 文件，输入 `Imports System.` 或 `StringBuilder.` 等，
   即可看到补全 / 悬停 / 签名帮助。

## 安装为正式插件

```powershell
npm install -g @vscode/vsce
vsce package            # 生成 remote-vbnet-lsp-0.1.0.vsix
code --install-extension remote-vbnet-lsp-0.1.0.vsix
```

或者把本文件夹（含 `out/`、`node_modules/`、`package.json`）整体复制到
`%USERPROFILE%\.vscode\extensions\remote-vbnet-lsp`，重启 VS Code 即可。

## 配置

| 设置 | 默认 | 说明 |
|------|------|------|
| `myRemoteLsp.host` | `127.0.0.1` | 语言服务器地址 |
| `myRemoteLsp.port` | `8080` | 语言服务器端口 |
| `myRemoteLsp.vbsExecutable` | `G:\GCModeller\src\runtime\sciBASIC#\.nuget\net10.0\vbs.exe` | 调试时执行 VB 脚本所用的 `vbs.exe` 宿主路径 |

## 调试 VB.NET 脚本

本扩展除了提供 LSP 智能提示外，还内置了一个**最小 DAP 调试适配器**（类型 `vbnet`），
可以通过 `vbs.exe` 宿主直接执行 `.vb` / `.vbs` 脚本并查看其控制台输出。
该适配器目前是**骨架版本**：已打通「启动 → 执行 → 捕获输出 → 终止」主链路，
但**断点、单步、变量查看尚未实现**（在 UI 中会提示“不支持”）。

### 前置条件

- 已按上文 `npm install` + `npm run compile` 编译（会同时生成 `out/debugAdapter.js`）。
- `vbs.exe` 宿主就位（默认取设置 `myRemoteLsp.vbsExecutable`，
  默认值指向 `G:\GCModeller\src\runtime\sciBASIC#\.nuget\net10.0\vbs.exe`；
  如路径不同，请在 VS Code 设置里改，或在 `launch.json` 里用 `vbsPath` 覆盖）。

### 使用步骤

1. 在 VS Code 中打开要调试的 `.vb` 脚本（例如 `tutorials\VBS\scripts` 下的文件）。
2. 打开“运行和调试”面板（`Ctrl+Shift+D`），点击“创建一个 launch.json 文件”，
   在弹出的列表里选择 **VB.NET**；或手动添加如下配置：

   ```json
   {
     "version": "0.2.0",
     "configurations": [
       {
         "type": "vbnet",
         "request": "launch",
         "name": "VB.NET: Launch ${fileBasename}",
         "program": "${file}",
         "vbsPath": "${config:myRemoteLsp.vbsExecutable}",
         "args": []
       }
     ]
   }
   ```

   其中 `program` 是要执行的脚本（`${file}` 表示当前打开的文件），
   `vbsPath` 留空则使用上面的默认设置，`args` 是传给脚本的命令行参数。

3. 按 `F5` 启动调试：扩展会调起 `vbs.exe` 执行脚本，脚本的 stdout / stderr 会实时显示
   在 **Debug Console** 中；脚本结束（或你点击“停止”）后调试会话自动终止。

### 已知限制（待补全）

- 设置断点、单步执行、查看调用栈 / 局部变量：尚未实现，后续会在
  `src/debugAdapter.ts` 中补充 `setBreakpointsRequest` / `nextRequest` /
  `stackTraceRequest` / `variablesRequest` 等逻辑，并依赖 `vbs.exe` 宿主提供
  调试钩子或 .NET CLR 级 attach 支持。

## 先启动语言服务器

扩展连接前，语言服务器必须已经在监听对应端口：

```powershell
# 在 languageserver 项目编译产物目录
languageserver.exe --data <nuget数据目录> --port 8080
```

`--data` 指向包含 `nuget` 库的目录（找不到时会自动尝试 `<data>/db` 布局并打印友好提示）。
如果服务器未启动，扩展会在连接时报错——此时先启动服务器再重新加载窗口即可。
