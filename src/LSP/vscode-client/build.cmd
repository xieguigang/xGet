@echo off
rem ============================================================================
rem  将 src\LSP\vscode-client 编译并打包为 VS Code 插件 (.vsix)
rem
rem  步骤：
rem    1) npm install       安装依赖 (含运行时依赖 vscode-languageclient)
rem    2) npm run compile   用 tsc 把 src/extension.ts 编译到 out/extension.js
rem    3) vsce package      打包成 .vsix 插件
rem
rem  前置条件：已安装 Node.js (LTS) 与 npm，且能访问 npm 源。
rem  安装插件：code --install-extension remote-vbnet-lsp-0.1.0.vsix
rem ============================================================================

setlocal
set CLIENT=%~dp0
cd /d "%CLIENT%"

echo [1/3] 安装依赖 (npm install)...
call npm install
if errorlevel 1 (
    echo [ERROR] npm install 失败。
    exit /b 1
)

echo [2/3] 编译 TypeScript (npm run compile)...
call npm run compile
if errorlevel 1 (
    echo [ERROR] 编译失败，请检查 src/extension.ts。
    exit /b 1
)

echo [3/3] 打包为 .vsix (vsce package)...
call npx --yes @vscode/vsce package --no-yarn
if errorlevel 1 (
    echo [ERROR] vsce package 失败。
    exit /b 1
)

echo.
echo 完成! 生成的插件文件：
dir /b *.vsix
