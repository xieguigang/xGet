# Remote VB.NET LSP (VS Code 客户端)

这个 VS Code 扩展把 `src/LSP/lsp.ts` 封装成了可用的插件，通过 TCP 连接到
`src/LSP/languageserver` 实现的远程 VB.NET 脚本语言服务器，为 `.vb` / `.vbs`
脚本提供智能提示（补全、悬停、签名帮助）。

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

## 先启动语言服务器

扩展连接前，语言服务器必须已经在监听对应端口：

```powershell
# 在 languageserver 项目编译产物目录
languageserver.exe --data <nuget数据目录> --port 8080
```

`--data` 指向包含 `nuget` 库的目录（找不到时会自动尝试 `<data>/db` 布局并打印友好提示）。
如果服务器未启动，扩展会在连接时报错——此时先启动服务器再重新加载窗口即可。
