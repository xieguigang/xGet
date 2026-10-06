---
name: fix-verify-failed-page
overview: 修复 /api/verify 失败页渲染为空的问题：RenderVerifyFailed 错误复用了 verify-success.html（其中无 {{reason}} 占位符，且成功页占位符在失败路径下被 DocTemplate 清空），导致无效/过期/已用 token 的激活链接显示为空白成功页。新建独立的 verify-failed.html 模板并切换 RenderVerifyFailed 的模板引用。
design:
  architecture:
    framework: html
  styleKeywords:
    - 单列居中卡片
    - 内联样式
    - 状态徽标
    - 错误提示页
  fontSystem:
    fontFamily: Segoe UI
    heading:
      size: 24px
      weight: 600
    subheading:
      size: 14px
      weight: 500
    body:
      size: 14px
      weight: 400
  colorSystem:
    primary:
      - "#DC2626"
      - "#062E9A"
    background:
      - "#F5F7FB"
      - "#FFFFFF"
    text:
      - "#1F2937"
      - "#6B7280"
      - "#9CA3AF"
    functional:
      - "#DC2626"
      - "#16A34A"
todos:
  - id: create-verify-failed-template
    content: 新建 dist/template/verify-failed.html 失败页模板（含 reason/server 占位符与重新注册引导）
    status: completed
  - id: switch-failed-template
    content: 修改 MailService.RenderVerifyFailed 改用 verify-failed.html 模板名
    status: completed
  - id: build-and-verify
    content: 重新编译 Nuget 项目并自查：三种失败场景（token 缺失/无效/过期）与成功场景的页面渲染均含有效内容
    status: completed
---

## 产品概述

修复 NuGet 服务器邮箱激活流程中 `/api/verify` 失败页面渲染为空白的问题，使无效、已使用或已过期的验证链接能够正确显示包含错误原因的失败提示页。

## 问题描述

用户报告访问 `https://nuget.scibasic.net/api/verify?token=...` 时页面为空。

## 根因

- `MailService.RenderVerifyFailed`（src/Nuget/MailService.vb L227-233）错误地复用了成功页模板 `verify-success.html` 来渲染失败页。
- 该模板中没有 `{{reason}}` 占位符；同时失败路径的 values 中不存在成功页所需的 `{{email}}`/`{{payload}}`/`{{activate_command}}`/`{{server}}` 占位符。
- `DocTemplate.Render`（src/Readership/DocTemplate.vb）将未知占位符替换为空字符串，导致页面渲染为「Email verified ✓」成功样式但全部内容空白。
- 验证 token 为一次性（验证成功后即从 `pending_registrations` 表删除），因此重复点击、过期或无效 token 均会走失败分支并触发此 bug。

## 核心功能

- 新建独立的失败页模板 `dist/template/verify-failed.html`（红色错误图标、失败标题、`{{reason}}` 错误原因、`{{server}}` 用于重新注册引导链接）。
- `RenderVerifyFailed` 改用 `verify-failed.html` 模板，内置 fallback 模板保持不变。
- 修复后无需重新编译（模板为部署期文件），但需将新模板部署到服务器的 template 目录。

## 技术栈

- 后端：VB.NET net10.0（Nuget 模块，与现有代码一致）
- 模板机制：复用现有 `DocTemplate.Render` 的 `{{placeholder}}` 替换（未知占位符替换为空字符串）
- 部署：模板文件位于 `dist/template/`，由 `NugetConfiguration.TemplateDirectory` 指向，纯部署期文件

## 实现方案

1. 新建 `dist/template/verify-failed.html`：单列居中卡片布局（内联 CSS，与 verify-success.html 风格一致），红色感叹号圆形图标 + 「Verification failed」标题 + `{{reason}}` 错误原因段落 + 「重新注册」引导（链接到 `{{server}}` 服务首页）。
2. 修改 `MailService.RenderVerifyFailed`：`renderTemplate(templateDirectory, "verify-failed.html", values, fallbackVerifyFailed)`，仅改动模板名字符串一行，fallback 常量不动。
3. 防御性考虑：模板文件读取失败或不存在时自动回退到 `fallbackVerifyFailed`（现有机制，无需额外改动）。

## 关键代码位置

- `src/Nuget/MailService.vb` L227-233（RenderVerifyFailed，仅改模板名）
- `dist/template/verify-success.html`（现有成功页模板，作为样式参照）
- `src/Nuget/MailService.vb` L348（fallbackVerifyFailed，含 `{{reason}}`，保持不变）
- `src/Nuget/Service.vb` L565-585（VerifyEmail 三个失败分支，无需改动）

## 设计方案

新建 `verify-failed.html` 错误页模板，风格与现有 `verify-success.html` 完全一致（内联 CSS，邮件客户端与浏览器兼容）：

- 页面：浅灰背景（#F5F7FB）居中单列卡片（白色，圆角 12px，浅阴影），最大宽度 640px。
- 图标：52px 红色（#DC2626）圆形，白色感叹号字符。
- 标题：「Verification failed」（#1F2937，24px，600 字重）。
- 错误原因：`{{reason}}` 段落（#6B7280，14px），展示具体失败原因（链接无效 / 已使用 / 已过期）。
- 引导区：说明重新注册的操作方式，附 `{{server}}` 服务首页链接；提示邮件验证链接有效期为 30 分钟。
- 页脚：灰色小字（#9CA3AF）免责说明。

无新增脚本与样式文件，全部内联，保持与验证邮件模板一致的轻量部署方式。