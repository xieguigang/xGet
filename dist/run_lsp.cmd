@echo off
rem ============================================================================
rem  启动远程 VB.NET 脚本 LSP 服务器 (languageserver.exe)
rem
rem  原理与 dist\run.cmd (nuget 服务器) 类似，但多了一个关键处理：
rem  nuget 服务器(Fluteway)会以“独占锁”打开同一个 JSql 数据库
rem  (dist\data\db\nuget\*.lock)，若 LSP 直接读写该目录会被锁冲突。
rem  因此这里先从 dist\data\db\nuget 复制一份“只读快照”(排除 *.lock 锁文件)
rem  到 dist\lspdb，再让 LSP 使用该快照，从而避免文件锁冲突。
rem  每次运行都会刷新快照，所以上传新包后重新执行本脚本即可生效。
rem
rem  前置条件：先运行 dist\run.cmd 启动 nuget 服务器并完成包上传。
rem  可用设置：--port <n> 修改端口(默认 8080，需与 VS Code 客户端 lsp.ts 一致)。
rem ============================================================================

setlocal
set DIST=%~dp0
set BIN=%DIST%bin
set SNAP=%DIST%lspdb\nuget

rem 1) 检查 nuget 数据库是否已存在(需先启动 nuget 服务器并上传包)
if not exist "%DIST%data\db\nuget" (
    echo [ERROR] 未找到 nuget 数据库: %DIST%data\db\nuget
    echo         请先运行 dist\run.cmd 启动 nuget 服务器，并用 xGet 上传程序包。
    exit /b 1
)

rem 2) 结束可能正在运行的旧 LSP 实例，释放对快照的锁
taskkill /IM languageserver.exe /F >nul 2>&1

rem 3) 刷新数据库快照(排除锁文件)
if exist "%SNAP%" rmdir /S /Q "%SNAP%"
mkdir "%SNAP%"
robocopy "%DIST%data\db\nuget" "%SNAP%" /E /XF *.lock /NFL /NDL /NJH /NJS

rem 4) 启动 LSP 服务器(默认端口 8080)
"%BIN%\languageserver.exe" --data "%DIST%lspdb" --port 8080
