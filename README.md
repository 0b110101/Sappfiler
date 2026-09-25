# Sappfiler

[![Release](https://img.shields.io/github/v/release/0b110101/Sappfiler?style=flat-square&color=blue)](https://github.com/0b110101/Sappfiler/releases)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011%20x64-informational?style=flat-square)](https://github.com/0b110101/Sappfiler)
[![Framework](https://img.shields.io/badge/.NET-10.0-purple?style=flat-square)](https://dotnet.microsoft.com/)
[![UI Framework](https://img.shields.io/badge/UI-WinUI%203-0078D7?style=flat-square)](https://learn.microsoft.com/windows/apps/winui/winui3/)
[![License: CC BY-NC-SA 4.0](https://img.shields.io/badge/License-CC_BY--NC--SA_4.0-orange.svg?style=flat-square)](https://creativecommons.org/licenses/by-nc-sa/4.0/)

Windows 后台自动追踪游戏时长，并无缝双向同步至 Notion 数据库的现代化桌面应用。

---

## 目录

- [💡 为什么需要 Sappfiler](#-为什么需要-sappfiler)
- [✨ 核心特性](#-核心特性)
- [🚀 快速开始](#-快速开始)
- [🔗 Notion 关联配置指南（核心）](#-notion-关联配置指南核心)
  - [1. 创建 Notion 内部集成并获取 Token](#1-创建-notion-内部集成并获取-token)
  - [2. 为两个数据库授权连接（关键步骤）](#2-为两个数据库授权连接关键步骤)
  - [3. 获取 Database ID](#3-获取-database-id)
  - [4. 数据库结构定义与属性配置](#4-数据库结构定义与属性配置)
  - [5. 在总表中自动汇总累计时长（Formula 公式）](#5-在总表中自动汇总累计时长formula-公式)
- [🎮 游戏识别与映射机制](#-游戏识别与映射机制)
- [🔄 版本更新与数据安全](#-版本更新与数据安全)
- [⛔ 项目边界与安全性原则（What it doesn't do）](#-项目边界与安全性原则what-it-doesnt-do)
- [🛠️ 技术栈](#️-技术栈)
- [📦 从源码构建](#-从源码构建)
- [📄 开源协议与非商业条款](#-开源协议与非商业条款)
- [🙏 致谢](#-致谢)

---

## 💡 为什么需要 Sappfiler

- **跨平台时长专精记录痛点**：现有网站与App多以成就为锚点，而不是游玩时长记录。
- **Notion 个人数据库的优势与门槛**：Notion 是搭建个人全能游戏库与自由看板的绝佳工具，但手工打卡耗时费力、极易遗漏，且容易因记账负担过重而放弃。
- **自动化无感解决方案**：Sappfiler 在 Windows 后台低开销静默常驻，全自动识别当前运行的游戏进程并精确记录时长，自动处理跨午夜拆分，并将每日明细实时同步至你的专属 Notion 数据库，真正实现「玩游戏零打扰，查数据全自动」。

---

## ✨ 核心特性

- **零感心跳监控**：轻量级进程检测，每 5 秒进行一次增量心跳累加；突发断电或强退进程最多仅丢失 5 秒统计，数据极其稳健。
- **午夜自动拆分**：跨越午夜 00:00 的连续游玩进程，自动切分归属自然日，保证每日统计精确无偏差。
- **Notion 双向增量同步**：
  - **同步时机明确**：同步 Notion 的时间点为**启动程序时**以及**结束游戏时**（常驻后台期间亦包含定期增量心跳对账）。
  - 自动向「每日时长表」推送打卡记录，并建立与「游戏总表」的 Relation 关联。
  - 双向状态对账与防漏同步：支持本地与 Notion 任意一侧的数据删除核验，本地更具备已删除归档兜底保护。
  - 绑定状态全自动标记：每日记录自动标定 `已绑定` 与 `未绑定` 状态，未绑定游戏后续关联总表时自动回填历史明细。
- **多平台广泛兼容**：原生支持 Steam、Epic Games、GOG Galaxy、Ubisoft Connect、EA Desktop、Xbox PC、WeGame 等主流平台，并支持通过可执行文件（.exe）手动添加任意游戏。
- **离线优先与数据自决**：数据完全持久化于本地 SQLite 数据库，即便无网络或未配置 Notion 亦可离线统计，提供 GitHub 风格年度热力图、时长排行、游戏活跃度、游戏探索等丰富离线可视化视图。
- **原生 WinUI 3 体验**：基于 Windows App SDK 与 WinUI 3 构建，支持 Mica 云母透明材质与系统级深浅色主题自适应，界面流畅细腻。

---

## 🚀 快速开始

1. 前往 [Releases](https://github.com/0b110101/Sappfiler/releases) 下载最新发行包 `Sappfiler-vX.X.X-win-x64.zip`。
2. **解压至任意可写目录**（例如 `D:\Tools\Sappfiler`，请勿置于 `C:\Program Files` 以免受 UAC 写入权限限制）。
3. 双击运行 `Sappfiler.exe`，程序将常驻于系统托盘。
4. 打开程序「设置」界面，按照下方指南配置 Notion，保存后即可开启全自动同步。

> 💡 **系统要求**：Windows 10 1809（Build 17763）或更高版本 / Windows 11（x64 架构）。程序为独立自包含打包，无需用户安装任何额外的 .NET 或 WinUI 运行时。

---

⚠️ 数据安全提示：首次使用前，建议提前备份 Notion 总表，以防配置错误或 API 异常导致数据变更。

## 🔗 Notion 关联配置指南（核心）

Notion 同步需建立两个 Database：**「游戏总表」**（管理你的全部游戏库藏）与**「每日时长表」**（记录每天各游戏的游玩明细）。

> 🎁 **快速开箱：提供官方模板直接套用**
> 如果你不想手动逐个创建属性与配置视图，推荐直接复制官方预设模板：
> 👉 [每日游戏时长表模板（点击复制至个人工作区）](https://app.notion.com/p/3e6c4ab26378803d95dfe0523d4e8eff?v=f45c4ab2637883a1a6f988dd80d5b450&source=copy_link)（进入后点击页面右上角 **Duplicate** 即可一键复制到你的工作区）。

```
┌─────────────────────────────────┐       Relation 关联       ┌─────────────────────────────────┐
│           游戏总表               │ ◄─────────────────────────┤          每日时长表              │
│  - 游戏名称 (Title)             │                           │  - 游戏动态 (Title)             │
│  - 关联 (Relation)              │                           │  - 日期 (Date)                  │
│  - 累计时长 (Formula 自动汇总)   │ ────────────────────────► │  - 时长 (Number, 小时)          │
└─────────────────────────────────┘      Formula 反向汇总     │  - 绑定状态 (Select)            │
                                                              │  - 关联游戏 (Relation)          │
                                                              └─────────────────────────────────┘
```

### 1. 创建 Notion 内部集成并获取 Token

1. 登录 Notion 并访问 [Notion My Integrations](https://www.notion.so/my-integrations)。
2. 点击 **New integration**：
   - **Name**：填写自定义名称（例如 `Sappfiler`）。
   - **Type**：必须选择 **Internal**。
   - **Associated workspace**：选择存放你游戏数据库的工作区。
3. 创建完成后，复制 **Internal Integration Secret**（格式为 `ntn_...` 或 `secret_...`）。此密钥即为客户端设置中的 `Notion Token`。

[![Notion Integration Token Setup](https://img.shields.io/badge/Tutorial_Step_1-Notion_Integration_Token-blue?style=for-the-badge)](#)

### 2. 为两个数据库授权连接（关键步骤）

> ⚠️ **权限说明**：Notion API 采用严格授权机制，集成默认无法读取任何未授权页面。若未授权，程序将返回 `403 Forbidden`。

针对**「游戏总表」**和**「每日时长表」**两个数据库，分别执行：
1. 在浏览器或客户端中将该数据库**展开为完整全页（Full Page）**。
2. 点击页面右上角的 **`···`** 菜单。
3. 滚动至底部选择 **Connections**（连接）→ **Connect to**（添加连接）。
4. 在搜索框中找到第 1 步创建的集成名称（如 `Sappfiler`），点击授权关联。

[![Notion Connect Step](https://img.shields.io/badge/Tutorial_Step_2-Connect_Integration_To_Database-orange?style=for-the-badge)](#)

### 3. 获取 Database ID

打开数据库全页，观察浏览器地址栏 URL：

```
https://www.notion.so/<workspace>/1a2b3c4d5e6f7890abcdef1234567890?v=...
                                  └────────── 32 位 Database ID ──────────┘
```

- 复制链接中 `notion.so/` 之后的 **32 位字符串**（带或不带连字符均可，程序内部会自动归一化解析）。
- 分别获取两张表的 ID，填入客户端设置中的「游戏总表 Database ID」与「每日时长表 Database ID」。

[![Notion Database ID](https://img.shields.io/badge/Tutorial_Step_3-Get_Database_IDs-lightgrey?style=for-the-badge)](#)

### 4. 数据库结构定义与属性配置

请确保 Notion 数据库中创建的属性名称与数据类型严格符合下表（名称不匹配会导致 Notion API 报 400 错误）：

#### A. 游戏总表（Game Catalog Database）
用于程序识别游戏名称与平台别名：

| 属性名 | 字段类型 (Type) | 必需 | 说明 |
|---|---|---|---|
| `游戏名称` | **Title** | ✅ | 游戏主标题，每日打卡与历史回刷将引用此名称（动态兼容任意 Title 属性命名） |
| `关联` | **Relation** | ✅ | **关联到「每日游戏时长表」**（在每日表创建 Relation 时开启双向关联即可生成），总表时长汇总与历史明细回溯的核心依据 |
| `别名` | Multi-select 或 Text | 可选 | 辅助匹配别名（如带括号的发布年 `Valheim (2020)`） |
| `游戏标识` | Multi-select 或 Text | 可选 | 填入 `steam:appid`（例如 `steam:3112010`）实现 100% 精确映射 |

> 💡 **页面封面与图标说明**：
> 客户端中展示的游戏封面与图标直接读取自总表对应游戏页面的 **Cover（页面封面）** 与 **Page Icon（页面图标）**，**无需在数据库中额外新建「封面」属性**（本地游戏未运行时亦可正常展示游戏封面）。

#### B. 每日时长表（Daily Records Database）
用于程序写入每天的游戏打卡数据（可直接套用上方官方预设模板）：

| 属性名 | 字段类型 (Type) | 必需 | 说明 |
|---|---|---|---|
| `游戏动态` | **Title** | ✅ | 程序自动写入，格式为 `游戏名 · X.X h` |
| `日期` | **Date** | ✅ | 记录游玩归属日期（UTC 归一化） |
| `时长` | **Number** | ✅ | **单位为小时**，保留 2 位小数（例如 0.50 表示 30 分钟） |
| `绑定状态` | **Select** | ✅ | 程序自动写入 `已绑定` 或 `未绑定`（选项无需提前手工创建） |
| `关联游戏` | **Relation** | ✅ | 关联到「游戏总表」，用于支持总表时长聚合统计 |

[![Notion Database Schema](https://img.shields.io/badge/Tutorial_Step_4-Database_Schema_Properties-success?style=for-the-badge)](#)

### 5. 在总表中自动汇总累计时长（Formula 公式）

无需复杂的第三方自动化流程，利用 Notion 现代 **Formula 2.0** 即可实现自动求和：

1. **建立双向关联**：
   - 打开「每日时长表」，点击 `关联游戏` 属性表头 → **Edit property**。
   - 开启 **Show on 游戏总表** 开关。
   - 此时「游戏总表」中会自动出现反向关联属性（通常命名为 `每日时长表`）。
2. **添加求和公式**：
   - 在「游戏总表」中添加一个属性，类型选择 **Formula**，填入以下公式：
     ```notion
     prop("每日时长表").map(current.prop("时长")).sum()
     ```
   - *（注：请将 `prop("每日时长表")` 替换为你总表中关联属性的实际名称；保存后即可实时显示该游戏的所有历史累计小时数。）*
3. **（可选）叠加原有手工维护的历史时长**：
   - 若总表中已有一列手工历史游玩时间（如 `历史时长`，单位为小时），可通过公式无缝合并：
     ```notion
     prop("历史时长") + prop("每日时长表").map(current.prop("时长")).sum()
     ```

[![Notion Formula Rollup](https://img.shields.io/badge/Tutorial_Step_5-Formula_Sum_Setup-blueviolet?style=for-the-badge)](#)

---

## 🎮 游戏识别与映射机制

1. **自动化分级匹配**：
   - **Level 1（确定性）**：通过可执行文件与 Steam 安装清单直接比对 Steam AppID，若总表包含匹配的 `steam:appid` 或封面内嵌 AppID，实现 100% 自动绑定。
   - **Level 1.5（Steam 中文名自动化匹配）**：对于 Steam 平台游戏，若本地识别到的为英文名且初次未能匹配，程序会自动根据 Steam AppID 调用 Steam 官方 API 请求对应的中文游戏名称进行二次确定性匹配，完美兼容用户在总表中填写 Steam 页面中文名的情况。
   - **Level 2（名称归一化）**：程序自动滤除进程名与总表名中的特殊符号、空格与常见版本后缀（如 `Deluxe Edition`、`Remastered`、`GOTY` 等）进行高精度比对。
   - **Level 3（待处理列表）**：对于多重重名或低确定度命中，程序不妄作推断，统一收录至「待处理」列表由用户确认，杜绝错误关联。
2. **一键绑定与历史回刷**：
   - 在客户端「待处理」页面，用户可一键将本地游戏绑定到 Notion 总表条目。
   - 绑定生效后，程序会自动回溯修补本地及 Notion 云端历史记录，将记录标题、关联 Relation 与 `绑定状态`（置为 `已绑定`）自动回填更新。

---

## 🔄 版本更新与数据安全

- **数据与程序分离架构**：
  - 本地 SQLite 核心数据、自定义别名及封面缓存均存储于系统目录：
    `%LocalAppData%\Sappfiler\` (或在客户端设置中自定义位置)
  - 运行日志存放于 `<程序安装目录>\data\logs\app.log`，具备 2MB 自动滚卷轮转机制。
- **平滑升级（零丢失风险）**：
  1. 右键系统托盘图标，选择 **退出**（务必完全退出程序以释放文件锁定）。
  2. 下载新版本压缩包，直接解压并**覆盖替换**原安装目录中的所有文件。
  3. 重新启动客户端即可。升级过程绝不影响本地存储的个人历史数据。

---

## ⛔ 项目边界与安全性原则（What it doesn't do）

为保障系统稳定性与用户账号安全，本项目严格遵守以下技术原则：
- **🚫 绝不注入任何游戏进程**：不使用任何 DLL 注入、API Hook 或驱动级监测技术，仅依赖 Windows 官方进程快照与性能计数器。
- **🛡️ 反作弊与安全性兼容说明**：
  本项目采用纯 Windows 标准用户态只读 API，运行机制等同于 Windows 自带的任务管理器。
  > ⚠️ **注意**：这不等于跟所有第三方反作弊都能兼容；如果你对某款游戏的反作弊环境要求特别严，建议自己评估过再用。
- **🚫 无第三方中间服务器**：客户端直连 Notion 官方 HTTPS API，不存在任何中转服务器或收集个人数据的后门，Token 与游戏历史绝不离开本地环境。
- **🚫 非在线社交对战平台**：专注服务于单机、联机全平台玩家的个人数字化生活记录与离线聚合分析。

---

## 🛠️ 技术栈

- **Language & Runtime**: C# 13 / [.NET 10](https://dotnet.microsoft.com/)
- **UI Framework**: [WinUI 3](https://learn.microsoft.com/windows/apps/winui/winui3/) / [Windows App SDK](https://learn.microsoft.com/windows/apps/windows-app-sdk/)
- **Architecture**: MVVM + High-Performance Event-Driven Sync
- **Local Persistence**: SQLite + [Dapper](https://github.com/DapperLib/Dapper)
- **Visuals & Charts**: WinUI Native Canvas / Path Vector Graphics
- **System Integration**: Win32 Interop, Shell NotifyIcon (Tray), Mica Backdrop

---

## 📦 从源码构建

### 开发环境需求
- Windows 10 (1809+) 或 Windows 11
- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Visual Studio 2022 (v17.12+) 或 VS Code（配合 C# Dev Kit）

### 构建与运行
```powershell
# 克隆仓库
git clone https://github.com/0b110101/Sappfiler.git
cd Sappfiler

# 还原并编译
dotnet build Sappfiler.slnx

# 启动应用程序
dotnet run --project src/Sappfiler.App
```

### 本地发布打包
运行根目录下的一键自动化打包脚本：
```powershell
.\publish.cmd
```
产物将输出至 `dist/Sappfiler-vX.X.X-win-x64/` 及对应 `.zip` 压缩包。

---

## 📄 开源协议与非商业条款

本项目采用 **[CC BY-NC-SA 4.0 (知识共享 署名-非商业性使用-相同方式共享 4.0 国际许可协议)](LICENSE)** 进行开源保护。

### 核心权益与限制说明
- ✅ **个人自用免费**：允许任何个人出于非商业目的免费下载、安装、使用本软件。
- ✅ **源码透明可审计**：源码完全开放，供玩家与技术同行审计安全性（零内存读写、零 DLL 注入）及本地数据流转。
- ❌ **严格禁止任何商业行为（Non-Commercial）**：
  - **严禁任何个人、团队或机构将本项目源代码、编译产物（.exe / .zip）或二次修改版本用于任何盈利性商业活动**。
  - 严禁行为包括但不限于：打包倒卖、上架收费软件分发平台、网盘付费下载、植入商业广告/流氓推广、捆绑第三方商业插件、提供付费托管服务等。
- 🔄 **相同方式共享（Share-Alike）**：任何基于本项目二次开发的代码分发，必须沿用相同的 CC BY-NC-SA 4.0 协议开源，并保留原始项目与作者署名。

完整法律文本请参阅仓库根目录下的 [LICENSE](LICENSE) 文件。

---

## 🙏 致谢

- SAMK
- UNICORN
- [Google Gemini](https://deepmind.google/technologies/gemini/)
- [OpenAI ChatGPT](https://openai.com/)
- [Anthropic Claude](https://www.anthropic.com/)
- [DeepSeek](https://www.deepseek.com/)
