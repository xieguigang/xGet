@echo off
rem ============================================================================
rem  启动远程 VB.NET 脚本 LSP 服务器 (languageserver.exe)
rem
rem  与 dist\run.cmd (nuget 服务器) 共享同一个 JSql 数据库
rem  (dist\data\db\nuget)。JSql 底层已支持多进程同时访问：nuget 服务器
rem  以独占写者身份运行(语句间释放锁)，LSP 以只读 SharedRead 读者身份打开
rem  同一目录，二者可并发共存，无需再复制快照。
rem
rem  前置条件：先运行 dist\run.cmd 启动 nuget 服务器(必须，否则数据库不存在)。
rem  可用设置：--port <n> 修改端口(默认 8080，需与 VS Code 客户端 lsp.ts 一致)。
rem ============================================================================

setlocal
set DIST=%~dp0
set BIN=%DIST%bin

rem 1) 检查 nuget 数据库是否已存在(需先启动 nuget 服务器)
if not exist "%DIST%data\db\nuget" (
    echo [ERROR] 未找到 nuget 数据库: %DIST%data\db\nuget
    echo         请先运行 dist\run.cmd 启动 nuget 服务器，并用 xGet 上传程序包。
    exit /b 1
)

rem 2) 结束可能正在运行的旧 LSP 实例
taskkill /IM languageserver.exe /F >nul 2>&1

rem 3) 启动 LSP 服务器(默认端口 8080)
rem    --data 指向 dist\data，LSP 会自动定位到 dist\data\db 下的 nuget 表。
rem    以只读共享锁(SharedRead)打开，与运行中的 nuget 写者并发共存。
"%BIN%\languageserver.exe" --data "%DIST%data" --port 8080
