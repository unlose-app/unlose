# unlose

**[English](README.md) | [简体中文](README.zh-CN.md)**

> **Unlose what your AI agent deleted. When the hook fails, the snapshot holds.**
> （AI 删掉的东西，unlose 都能找回来。当钩子失效时，快照依然在。）

unlose 是 Windows 上 AI Agent 操作事故的最后一道安全网。AI Agent 开始工作前，unlose 悄悄为你做全盘快照；当它删掉、改坏或加密了你的文件，你拖动时间轴，文件就回来了。

**不是拦截器。是时光机。**

![状态：early alpha / 开发者预览](https://img.shields.io/badge/status-early%20alpha%20%2F%20developer%20preview-orange)

---

## 为什么有 unlose

### 问题：AI 现在已经有权删你的文件

AI 编程工具（Claude Code、Cursor、Copilot、Gemini CLI……）会在你的机器上执行真实操作：改写代码、重排目录、批量处理文件。它们很强大——但也会犯错：误解指令、含糊的命令，或者被诱导执行破坏性操作。

这不是假设。2026-07-10，知名创始人 Matt Shumer 的 Mac 被 AI 助手清空了（`rm -rf` 用户目录）——AI 在 **4 轮内绕过了所有命令拦截器**：`rm` → `unlink`/`find -delete` → 批量删文件 → 底层 API 调用。即使是最专业的重度用户也无法幸免。

### 真相：拦截是一场打不赢的猫鼠游戏

AI 越聪明，绕过拦截的能力就越强。堵住 `rm -rf`，它会找到另一种方式做同样的事。**世上没有任何拦截器能堵住所有破坏路径。**

因此，唯一无法绕过的防御是：**在 AI 动手之前，完整保存你的数据。**

### unlose 的答案：先保护，再干活

```
AI 启动 ──► unlose 自动快照 ──► AI 开始干活（删除 / 修改 / 加密）
                                   │
                             unlose：回到快照，文件回来了
```

unlose 不猜测 AI 要做什么。它只保证一件事：**无论 AI 做了什么，你都有一份"AI 碰它之前"的干净副本。**

---

## unlose 与拦截器有什么不同

| | 拦截器（如 DCG） | unlose |
|---|---|---|
| 角色 | 第一道防线：拦截危险命令 | **最后一道防线：快照安全网** |
| 思路 | 猜测并拦截破坏路径 | 不猜测，只保存 |
| 能绕过吗？ | 能——GPT-5.6 事件已证明 | **不能——快照在删除之前就已存在** |

> **拦截器挡子弹。unlose 给时光机。两个都用。**

**不是监视器。** 与 AI 记忆工具（Rewind、Recall）不同，unlose 从不录制你的屏幕、音频、按键或文件内容供人审查。它只保留**文件系统快照**——某个时间点的数据副本。只有你自己能打开它们。这是时光机，不是监控。

**原生 Windows，免 WSL、免钩子。** Windows 服务 + WPF 桌面界面 + CLI + MCP Server，一个 MSI 装完。

---

## 核心能力

### 🛡️ 自动快照——你什么都不用做

| 触发方式 | 时机 |
|---|---|
| 定时 | 默认每天 3 个定点时刻（08:00 / 13:00 / 18:00），可切换为按间隔（6/12/24/48h）|
| **AI 会话前** | 识别 30+ 款主流 AI Agent 启动，动手前自动快照 |
| 还原前兜底 | 每次还原前自动快照，还原失败也不会损失当前状态 |
| 手动 / CLI / MCP | 一键快照，或从命令行、AI 工具本身触发 |

### 🕰️ 沉浸式还原——像拨钟一样找回文件

- **双栏时间轴**：历史快照 vs 当前状态，底部时间轴任意切换时间点
- **四色 Diff**：被删 / 被改 / 新增的文件一目了然，带修改时间
- **行级 Diff**：文本文件精确到行——哪些行被删（红）、哪些是新增（绿），带行号和 +X/-Y 统计
- **懒加载文件树**：再深的目录结构也能完整浏览

### 🎯 挑拣恢复

- 勾选一个或多个文件/目录（勾目录含子树），恢复到**你指定的新目录**
- **绝不覆盖当前文件**——零风险试恢复
- 也支持整卷恢复（执行前二次确认）

### 🤖 识别 30+ 款 AI Agent——AI 自己知道"动手前先备份"

- 内置 30+ 款主流 Agent 识别（Claude Code、Cursor、Copilot、Gemini CLI、Kimi、Qwen、Codex、DeepSeek……）——新装的工具零配置生效
- **全局记忆注入（独有）**：向你的 `~/AGENTS.md` 和已安装 Agent 的全局记忆文件写入防护指令——"会话前快照、危险操作前快照、用 unlose 恢复"。**AI 自己会读。** 原文保留、注入块明确标记、卸载零残留

### 📊 真实状态与事件日志——看得见，才信得过

- 主界面四个真实状态：**保护中 / 已暂停 / 存储不足挂起 / 离线**——绝无假的"一切安全"
- 事件日志 6 类可筛选（Agent 会话 / 系统还原 / 快照事件 / 存储告警 / 保护状态 / 全部）——每一行都是真的
- 存储卡片：真实磁盘用量 + 自动快照状态

### ⚡ 配置热重载——保存即生效

快照间隔、保护卷、低空间阈值、Agent 清单……在设置页保存，服务**立即热重载**，无需重启。设置页上每一个控件都是真的。

### 🔗 无缝接入 AI 工具链

- **CLI**（`unlose.exe`）：snapshot / status / list / restore，严格退出码（0/1/2）——脚本友好
- **MCP Server**：AI 工具通过 MCP 直接调用快照能力
- **Skill 文件**：服务运行时自动把 `unlose-snapshot` skill 写入已检测到的 Agent 技能目录——教 AI "动手前先快照"，零配置

---

## 快速开始

```powershell
# 1. 安装（MSI——注册 Windows 服务，随系统自启）
#    src/Installer/bin/Release/unlose-setup-x64.msi  （安装到 C:\Program Files\unlose\）

# 2. 拍一张快照
unlose snapshot --label "开工前先保护"

# 3. 查看状态与快照列表
unlose status
unlose list-snapshots

# 4. 还原
unlose restore-snapshot <id>
```

无需配置：服务后台运行，AI 启动时自动快照，保护默认开启。打开主界面即可看到保护状态和全部历史时间点。

**从源码构建**：需要 .NET 8 SDK，Windows 10/11 x64。

```powershell
dotnet build src/Unlose.sln -c Release
dotnet test src/Unlose.Tests    # 147 个单元测试
```

---

## 实测数据，不是口号

- **147/147 单元测试**全部通过（保留策略、还原语义、IPC 契约、配置热重载、记忆注入……）
- **16/16 UI 自动化测试**（FlaUI）全部通过
- **虚拟机 e2e**：post-install 16/16、快照还原 10/10、还原点调度 5/5
- **真实快照还原验证**：真实宿主机上逐字节 SHA256 一致的还原，含整卷恢复与勒索模拟恢复
- **热部署流水线**：3 轮 DEPLOY_OK
- **测试平台**：Windows 11 x64（VirtualBox）；Agent 识别 30+/30+；Pester 安装/e2e 脚本在 `tests/`（完整 VM 测试环境为内部资产）

## 技术要点

- **快照引擎**：Windows VSS 卷影副本——保护卷的完整时间点记录，无文件系统过滤驱动
- **挂载与还原**：卷影副本经符号链接挂载（`%ProgramData%\unlose\mounts\`），robocopy 复制文件/目录；整卷回滚带 `/purge` 语义（防勒索）
- **路径安全**：还原请求经穿越防护（拒绝绝对路径与 `..` 段）
- **存储**：SQLite（`%ProgramData%\unlose\`）；保留策略 24h 全留 → 7 天渐疏 → 30 天清理；重要快照可 🔒 永久保留
- **形态**：Windows 服务（崩溃自动重启）+ WPF 桌面界面 + CLI + MCP Server，MSI 安装
- **语言**：C# / .NET 8，界面中英双语

## 我们刻意不做

- ❌ **拦截命令**——打不赢的猫鼠游戏。那是拦截器（DCG 等）的事
- ❌ **贩卖恐惧**——只陈述事实与真实事件，绝不夸大
- ❌ **复杂配置**——保护默认开启，唯一的操作是"恢复"

## 路线图

- 异地备份（外置盘 / NAS 同步、增量、加密）——代码就绪，端到端验证中
- macOS 支持（Windows 产品市场验证之后）

## 许可证与商标

代码基于 **Apache License 2.0** 开源——见 [LICENSE](LICENSE) 与 [NOTICE](NOTICE)。

**unlose** 名称与 Logo 是本项目商标——可用范围见 [TRADEMARK.md](TRADEMARK.md)。**Fork 可以，请换名。** 如果你分发衍生作品，请起自己的名字——社区会感谢你。

## 关于作者

快照/还原是作者深耕 25 年的领域——2000 年就开始做"还原点触发"（US7039830B2、US7120835B2）。unlose 是同一个答案在 AI Agent 时代的第二次实现——这次 Apache-2.0 全开源，每一句声明都能在源码里查到。

## 企业版

需要集中管控、合规审计或私有化部署？**unlose Enterprise** 已在规划——[开 issue](https://github.com/unlose-app/unlose/issues) 或[发邮件](mailto:maintainers@unlose.dev)表达兴趣。

---

*unlose —— 你的 AI Agent 删掉的一切，unlose 都记得。*
