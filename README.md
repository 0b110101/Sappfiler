# Sappfiler

[![Release](https://img.shields.io/github/v/release/0b110101/Sappfiler?style=flat-square&color=blue)](https://github.com/0b110101/Sappfiler/releases)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011%20x64-informational?style=flat-square)](https://github.com/0b110101/Sappfiler)
[![Framework](https://img.shields.io/badge/.NET-10.0-purple?style=flat-square)](https://dotnet.microsoft.com/)
[![UI Framework](https://img.shields.io/badge/UI-WinUI%203-0078D7?style=flat-square)](https://learn.microsoft.com/windows/apps/winui/winui3/)
[![License: CC BY-NC-SA 4.0](https://img.shields.io/badge/License-CC_BY--NC--SA_4.0-orange.svg?style=flat-square)](https://creativecommons.org/licenses/by-nc-sa/4.0/)

Windows 后台自动追踪游戏时长，并无缝同步至 Notion、Obsidian、思源笔记等多后端笔记平台的现代化桌面应用。

---

## 目录

- [💡 为什么需要 Sappfiler](#-为什么需要-sappfiler)
- [✨ 核心特性](#-核心特性)
- [🚀 快速开始](#-快速开始)
- [📝 多笔记后端同步指南](#-多笔记后端同步指南)
  - [1. Notion 关联配置指南](#1-notion-关联配置指南)
  - [2. Obsidian 同步指南](#2-obsidian-同步指南)
  - [3. 思源笔记 (SiYuan) 同步指南](#3-思源笔记-siyuan-同步指南)
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
- **笔记个人数据库的优势与门槛**：Notion、Obsidian、思源笔记是搭建个人全能游戏库与自由看板的绝佳工具，但手工打卡耗时费力、极易遗漏，且容易因记账负担过重而放弃。
- **自动化无感解决方案**：Sappfiler 在 Windows 后台低开销静默常驻，全自动识别当前运行的游戏进程并精确记录时长，自动处理跨午夜拆分，并将每日明细实时同步至你的专属 Notion、Obsidian、思源笔记，真正实现「玩游戏零打扰，查数据全自动」。

---

## ✨ 核心特性

- **零感心跳监控**：轻量级进程检测，每 5 秒进行一次增量心跳累加；突发断电或强退进程最多仅丢失 5 秒统计，数据极其稳健。
- **午夜自动拆分**：跨越午夜 00:00 的连续游玩进程，自动切分归属自然日，保证每日统计精确无偏差。
- **多平台笔记后端并行同步（v0.4.0 核心突破）**：
  - **SQLite 单一权威真实源**：本地 SQLite 作为不可撼动的中央事实源，笔记平台为单向消费端，绝不覆盖本地更领先的真实游玩数据。
  - **失败隔离调度中心 (SyncOrchestrator)**：多后端并发执行，某一平台（如 Notion 临时网络波动）失败绝不阻塞其它平台（如 Obsidian、思源）同步。
  - **Notion 深度集成**：自动推送打卡记录、建立 Relation 关联、双向对账核验与历史标题规范化。
  - **Obsidian 原生支持**：
    - 支持「文件直写模式」（免插件直接写 Vault）与「Local REST API 插件模式」。
    - 采用临时文件原子替换（`.sappfiler.tmp`），彻底避免 Obsidian 频繁文件监控与多进程写冲突。
    - 标记区间增量更新（`<!-- ⚠️` 与 `<!-- Sappfiler End -->`），100% 完整保留用户手写的游玩随笔与感想。
  - **思源笔记 (SiYuan) 内核对接**：直连官方本地内核 API (`127.0.0.1:6806`)，支持自动创建日历归档文档与 Block 幂等更新。
- **多平台广泛兼容**：原生支持 Steam、Epic Games、GOG Galaxy、Ubisoft Connect、EA Desktop、Xbox PC、WeGame 等主流平台，并支持通过可执行文件（.exe）手动添加任意游戏。
- **离线优先与数据自决**：数据完全持久化于本地 SQLite 数据库，即便无网络或未配置云笔记亦可离线统计，提供 GitHub 风格年度热力图、时长排行、游戏活跃度、游戏探索等丰富离线可视化视图。
- **原生 WinUI 3 体验**：基于 Windows App SDK 与 WinUI 3 构建，支持 Mica 云母透明材质与系统级深浅色主题自适应，界面流畅细腻。

---

## 🚀 快速开始

1. 前往 [Releases](https://github.com/0b110101/Sappfiler/releases) 下载最新发行包 `Sappfiler-vX.X.X-win-x64.zip`。
2. **解压至任意可写目录**（例如 `D:\Tools\Sappfiler`，请勿置于 `C:\Program Files` 以免受 UAC 写入权限限制）。
3. 双击运行 `Sappfiler.exe`，程序将常驻于系统托盘。
4. 打开程序「设置」界面，按照下方指南配置 Notion，保存后即可开启全自动同步。

> 💡 **系统要求**：Windows 10 1809（Build 17763）或更高版本 / Windows 11（x64 架构）。程序为独立自包含打包，无需用户安装任何额外的 .NET 或 WinUI 运行时。

---

⚠️ 数据安全提示：首次使用前，建议提前备份笔记数据，以防配置错误或 API 异常导致数据变更。

## 📝 多笔记后端同步指南

### 1. Notion 关联配置指南

Notion 同步需建立两个 Database：**「游戏总表」**（管理你的全部游戏库藏）与**「每日时长表」**（记录每天各游戏的游玩明细）。

> 🎁 **快速开箱：提供官方模板直接套用**
> 如果你不想手动逐个创建属性与配置视图，推荐直接复制官方预设模板：
> 👉 [每日游戏时长表模板（点击复制至个人工作区）](https://rhinestone-viscose-ba3.notion.site/3e6c4ab26378803d95dfe0523d4e8eff?v=f45c4ab2637883a1a6f988dd80d5b450)（进入后点击页面右上角 **Duplicate** 即可一键复制到你的工作区）。

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

### 2. Obsidian 同步指南

Sappfiler 原生支持将每日游玩统计与游戏独立笔记同步至你的 Obsidian 本地知识库。Obsidian 无需依赖任何复杂第三方数据库插件，纯基于 **Markdown 文件 + YAML Frontmatter + `[[wikilink]]` 双向链接** 运行。

#### 1. Vault 内部目录结构推荐

当你开启 Obsidian 同步后，Sappfiler 会在你的 Vault 根目录下自动建立以下结构：

```text
你的 Obsidian 库 (Vault)/
└── Sappfiler/
    ├── Daily/                  # 每日游玩打卡归档
    │   ├── 2026-09-25.md
    │   └── 2026-09-26.md
    └── Games/                  # 游戏专属卡片/笔记
        ├── Baldur's Gate 3.md
        └── Cyberpunk 2077.md
```

#### 2. 每日打卡笔记 (`Daily/YYYY-MM-DD.md`) 与双链联动

每日笔记由 Sappfiler 自动生成，核心特性如下：

1. **自动聚合统计 (Frontmatter)**：顶部 YAML 记录当日所有游戏的总时长与总款数：
   ```yaml
   ---
   generated_by: sappfiler
   date: 2026-09-26
   total_duration: 3h 55m
   game_count: 2
   ---
   ```
2. **`[[wikilink]]` 替代外键关联**：表格中的游戏名称会自动渲染为 Obsidian 内部双向链接（例如 `[[Baldur's Gate 3]]`）：
   - 点击即可一键跳转至对应的游戏独立笔记；
   - 在游戏笔记右侧开启 **反向链接 (Backlinks)** 面板，即可清晰查阅该游戏在历史所有自然日中的出场记录。
3. **用户手写随笔 100% 保护**：自动生成内容包裹在以下保护标记内：
   ```markdown
   <!-- ⚠️ 以下内容由 Sappfiler 自动生成，手动修改将在下次同步时被覆盖 -->
   ## 游戏时长
   | 游戏 | 时长 | 次数 |
   |------|------|------|
   | [[Baldur's Gate 3]] | 2h 35m | 3 |
   <!-- Sappfiler End -->
   ```
   **你可以在该标记区间下方任意记录当天的游玩心得、通关心得或贴入截图**。后续每次同步更新时，标记外的内容将得到完整保留，绝不覆盖！

#### 3. 游戏独立笔记 (`Games/游戏名.md`) 与预计算 Frontmatter

因为 Obsidian 原生没有数据库 Rollup 功能，**Sappfiler 会在本地 SQLite 中自动预计算累计时长并回写至游戏笔记的 Frontmatter 中**：

```yaml
---
generated_by: sappfiler
game_id: 42
name: "Baldur's Gate 3"
platform: Steam
platform_id: "1086940"
total_playtime: 126h 30m
total_hours: 126.5
last_played: 2026-09-26
total_sessions: 87
---
```

- **自定义属性保护**：你可以在 Frontmatter 中随时添加自定义 Properties（如 `rating: 9.5`、`status: 通关`、`tags: [CRPG, 必玩]` 等），Sappfiler 同步时仅更新自身管理的统计字段，你的自定义属性会完整保留。
- **正文笔记区**：在正文标记下方，你可以随意书写长篇评测、配装方案、MOD 清单等个人知识资产。

#### 4. 在 Sappfiler 中配置连接

- **文件直写模式（首推，开箱即用）**：
  1. 打开 Sappfiler「设置」→「Obsidian 同步设置」。
  2. 开启同步开关，模式保持为 **文件直写模式**。
  3. 点击「浏览」或直接输入你的 Obsidian 库（Vault）根目录绝对路径（如 `D:\Obsidian\MyVault`）。
  4. 点击「测试连接」，提示成功后点击「保存 Obsidian 设置」即可。
- **Local REST API 模式（进阶）**：
  1. 在 Obsidian 社区插件市场中搜索并安装启用 `Local REST API` 插件。
  2. 在插件设置中生成或复制 `API Key`，查看端口号（默认 `27124`）。
  3. 在 Sappfiler 设置中选择 **Local REST API 模式**，填入端口与 Key，点击测试连接保存。

---

### 3. 思源笔记 (SiYuan) 同步指南

Sappfiler 通过直连思源笔记官方本地内核 HTTP API (`http://127.0.0.1:6806`) 实现全自动化同步。支持 **原生数据库 (Attribute View) 模式** 与 **纯文档分层模式** 两种构建方式。

#### 方案 A：原生数据库 (Attribute View) 模式（推荐，与 Notion 体验完全一致）

思源笔记原生支持属性视图数据库（Attribute View），支持 **关联 (Relation)** 与 **汇总 (Rollup)**，能够获得与 Notion 相同的结构化游戏数据库体验。

##### 1. 创建思源数据库结构

1. 在思源中新建一个笔记本（例如 `游戏库`）。
2. 在任意文档中输入 `/数据库` 或 `/属性视图`，分别创建两个数据库 Block：
   - **① 游戏总表数据库**：
     - `游戏名`（主列 / 文本）
     - `平台`（单选 Select，如 Steam / Epic / GOG 等）
     - `每日打卡`（关联 Relation 列，关联至下方的「每日打卡表」）
     - `总时长`（汇总 Rollup 列，选择关联 `每日打卡`，汇总字段选择 `时长`，计算选择 `求和 Sum`）
   - **② 每日打卡数据库**：
     - `日期`（文本或日期列，记录 `YYYY-MM-DD`）
     - `时长`（数字 Number 列，记录单日游玩小时数）
     - `启动次数`（数字 Number 列）
     - `关联游戏`（关联 Relation 列，关联至上述「游戏总表」）

##### 2. 获取数据库 Block ID

- 在思源中，鼠标悬浮在数据库块左上角的图标（六个点）上，在弹出菜单中点击 **复制块 ID**（格式类似 `20260926200000-abcdefg`）。
- 分别获取两张表的 Block ID。

##### 3. 在 Sappfiler 中完成配置

1. 打开思源笔记客户端 → 设置 → 关于 → 复制 **API Token**。
2. 打开 Sappfiler「设置」→「思源笔记 (SiYuan) 同步设置」：
   - 开启同步开关，填入 API Token。
   - 点击 **刷新列表**，从下拉框中选择存放上述数据库的笔记本。
   - 分别将复制的 Block ID 粘贴到 **「游戏总表数据库 Block ID」** 与 **「每日打卡数据库 Block ID」** 输入框中。
   - 点击「测试连接」提示成功后，点击「保存思源设置」。
3. **全自动联动效果**：每次游戏退出后，Sappfiler 会自动向「每日打卡」数据库插入今日游玩时长并关联总表，思源的 Rollup 列会自动实时聚合出游戏总时长！

#### 方案 B：纯文档分层模式（极简开箱即用，无需配置数据库）

如果你希望以纯 Markdown 文档形式阅读归档，无需手动创建数据库 Block：

1. 仅需在 Sappfiler 设置中输入思源的 **API Token**，点击「刷新列表」选择目标笔记本。
2. 将 **「游戏总表数据库 Block ID」** 与 **「每日打卡数据库 Block ID」** 保持**留空**。
3. 设定根文档路径（默认为 `/Sappfiler`），点击保存即可。
4. Sappfiler 会自动在指定笔记本内创建分级文档树：
   - `/Sappfiler/游戏记录/YYYY-MM/YYYY-MM-DD`（每日文档，自动生成当日全部游戏的游玩明细表格）
   - `/Sappfiler/游戏/{游戏名称}`（独立游戏档案，包含封面图、平台、创建时间及个人游玩笔记占位）

---

## 🎮 游戏识别与多端映射机制 (Game Identity Mapping)

Sappfiler 采用**「本地逻辑游戏（Single Source of Truth）+ 多端独立映射（game_mappings）」**的统一身份体系。无论你同时使用 Notion、Obsidian 还是思源笔记，每个后端都独立记录映射关系，完美支持不同平台间不同的命名偏好与自定义别名：

1. **若笔记库中已有大量游戏数据，如何自动识别？**
   - **自动化分级置信度匹配**：
     - **Level 1（外部平台 ID 确定性）**：通过 Steam AppID 比对。Notion 关联属性、Obsidian 笔记属性或思源列中若有记录，实现 100% 自动对接。
     - **Level 2（精确名称匹配）**：完全匹配笔记文件名、Notion 标题或思源主属性名称。
     - **Level 3（别名与异名穿透）**：
       - **Obsidian 原生别名支持**：自动解析现有笔记 YAML Frontmatter 中的 `aliases:` 或 `alias:` 列表。无论你的笔记叫《艾尔登法环》还是《Elden Ring》，只要别名包含即自动命中！
       - **Steam 官方本地化别名**：对于 Steam 游戏，程序自动请求 Steam 官方 API 获取中文/区域本地化名称参与二次比对。
     - **Level 4（归一化模糊匹配）**：滤除特殊符号、标点、空格与常见版本后缀（如 `Deluxe Edition`、`Remastered`、`GOTY`）比对。
   - **安全策略（防错绑）**：
     - 只有在候选条目**唯一且无歧义**（唯一确定性命中）时，Sappfiler 才会执行静默自动绑定（`CanAutoBind = true`）。
     - 若出现同名冲突、多项匹配或低置信度推测，程序绝不擅自关联或新建，而是收录至「待处理」列表由你手动点选确认。

2. **Obsidian 原生资产保护与命名空间隔离**：
   - **字段安全隔离**：在写入已有游戏笔记时，Sappfiler 严格仅在 `sappfiler-*` 专属属性命名空间（如 `sappfiler-total-duration`、`sappfiler-last-played`）更新程序统计指标。
   - **用户资产不可动摇**：你手动维护的 `genre`、`rating`、`status`、`aliases` 及正文笔记内容**绝不被覆盖或修改**。
   - **双链安全跳转**：每日时长打卡自动渲染为 `| [[{笔记路径}|{游戏显示名}]] |` 格式，支持自定义显示名称与跨目录双链精准跳转。

3. **映射管理与安全换绑**：
   - 在客户端「映射管理」页面，直观呈现每款游戏在 Notion / Obsidian / 思源笔记 三大后端的独立绑定状态、匹配方式与置信度证据。
   - 可随时针对任一后端独立进行**一键绑定、解绑或换绑**。
   - **数据安全底线**：解绑与换绑**绝不会删除本地游玩历史或打卡记录**，仅更新元数据映射关系。

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
