# Project Handover — Sappfiler

> **新 Agent 的第一份读物。** 本文件只做三件事：告诉你**按什么顺序读**、**红线在哪**、**下一步做什么**。
> 详细技术细节一律**引用**其它文档，不在这里复制（避免两个真相源）。
>
> 最后更新：**2026-10-03**　·　维护者：接手 Agent　·　上一份（GameTimeTracker 时代，已作废）见 `.ai/HANDOVER-LEGACY-GameTimeTracker.md`

---

## 0. 阅读顺序（照着走，别跳）

| # | 读什么 | 得到什么 |
|---|---|---|
| 1 | **本文件** | 红线、纪律、下一步 |
| 2 | `README.md` | 产品定位与用户使用流程（**已重写为准的版本**） |
| 3 | `项目接管报告-2026-10-03.md` | 项目全貌、核心流程、模块、**已知问题清单** |
| 4 | `.ai/多设备同步-方案确认-v1.md` | 多设备同步的**为什么**、冻结契约、各 Phase 实测结论 |
| 5 | 按第 2 节选分支，再读源码 | 具体实现 |

⚠️ **`.gitignore` 会骗你**：`tests/`、`logs/`、`cache/`、`config.json`、`dist/`、`.ai/`（除方案文档）等
都被排除，`list_dir` / `search_*` 这类工具**第一次扫描看不到它们**。
复核根目录必须用 shell（`Get-ChildItem -Force`），否则会得出"项目只有 3 个工程"的错误结论。

---

## 1. 一句话说清项目

**Sappfiler**（WinUI 3 托盘常驻）：Windows 后台**零打扰自动统计游戏时长**，
再把每日明细同步到用户自己的笔记平台（**Notion / Obsidian / 思源笔记**）。
本地 SQLite 是唯一权威源；笔记同步是**旁路能力**。

> 铁律：**本地计时可靠性 > 同步成功率**。任何同步 / 网络 / 后端问题都不允许影响
> 游戏监听与 5 秒心跳（`DeviceSyncEngine` 因此设计成**绝不向外抛异常**）。

---

## 2. 三条开发线（先确认你在哪条分支上工作）

| 分支 | 版本号 | 内容 | 状态 |
|---|---|---|---|
| `main` | `0.3.2`（**已发布**） | 本地计时 + 三后端笔记同步 | 正式交付线 |
| `feature/multi-backend-sync` | `0.4.0-alpha.1` | 三后端（Notion / Obsidian / 思源） | 开发中，未合并 |
| `feature/multi-device-sync` | `0.4.0-beta.1` | 多设备同步（Outbox / 协议 / 冲突） | **2a / 2b / 2c 完成，2d 进行中** |

- 三条线**互不影响**：改动前先想清楚"这条改动属于哪条线、会不会污染另外两条的合并"。
- 已验证基线（2026-10-03）：`main` 203/203、`multi-backend-sync` 225/225、`multi-device-sync` **262/262**，
  三者构建均 0 警告 0 错误。**任何改动后都要跑这三条分支的对照验证。**
- 多设备同步的路线图：`2d` 原子接线 → `2e` Identity/Conflict Resolution → `2f` Cloudflare → `2g` WebDAV → UI。

---

## 3. 版本号规则（2026-10-03 起）

- **feature 分支不占正式版本号**，用预发布标识区分特性线：`alpha` = 多后端，`beta` = 多设备。
- 正式版本号**只在 main 发布时确定**（当前已发布 = `0.3.2`）。
- 改版本必须**同时**改两处，否则 `publish.ps1` 会直接中止打包：
  - 仓库根 `Directory.Build.props`（`VersionPrefix` / `VersionSuffix` / `FileVersion` / `AssemblyVersion`）
  - `src/Sappfiler.App/Package.appxmanifest` 的 `Version`（= `FileVersion`）

---

## 4. 各 Phase 的冻结契约（细节在方案文档，这里只给指针）

| Phase | 冻结了什么 | 文档位置 |
|---|---|---|
| 2a | Outbox 语义（payload 快照 / 入队顺序 / ACK 才完成 / 崩溃复位 InFlight）、墓碑、游标按 Backend 隔离 | 方案文档 附录 B |
| 2b | `games.global_id`（migration 002）、`SyncableGame` 规则、去重的 identity 继承 + 墓碑 + 原子事务 | 方案文档 附录 C/D |
| 2c | 幂等 `change_id`、游标与数据同事务、退避、死信、**Deferred 台账（Deferred ≠ Completed）** | 方案文档 附录 E |

**身份边界（最容易搞混，务必记牢）**：

```
Sappfiler Game
  └── global_id        ← 多设备同步身份（墓碑、去重取舍只看它）
        ├── Notion Page ID     ← Provider 的远端身份
        ├── Obsidian path      ← Provider 的远端身份
        └── SiYuan BlockId     ← Provider 的远端身份
```

全局原则：**同步协议只认 `global_id`；`notion_page_id` 等 Provider 身份绝不参与同步层的删除/取舍判断。**

---

## 5. 不要做的事情（红线）

1. **不要重构现有架构**（尤其 `NotionServices.cs` / `SqliteRepository.cs` 不要拆分）。
2. **不要动 5 秒 heartbeat 与游戏监听核心**；同步是旁路，不能反过来影响本地计时。
3. **不要实现"通用 LWW"**（`if (remote.updated_at > local.updated_at)` 套所有实体）。
   Game / Session / GameMapping / Tombstone 的冲突语义不同，跨设备冲突一律归 **2e**。
4. **不要用本地 INTEGER id 做同步身份**，协议里只能出现 `global_id`。
5. **不要让业务写入与 Outbox 入队分离事务**（否则会出现"本地有、云上永远没有"）。
6. **不要用"先推游标再写数据"的 Pull 顺序**（崩溃即永久丢数据）。
7. **不要为修一个函数而顺手改整个模块**；改动前先确认影响范围。
8. **不要在 feature 分支上改 main 的版本号**。
9. **不要删除**：删除链路的 4 道护栏、面包屑日志（`[托盘]` / `[导航]`）、
   `catch{}` 静默设计、`DailyRecordTitle` 唯一性与旧格式兼容、`ReleaseVisualTree`（**已被删除，不要加回来**）。
10. **不要猜测设计意图**。拿不准的记为 `«需要向用户确认»`，不要自行改。

---

## 6. 已知问题 / 缺口（详见接管报告第 6、7 节）

| 缺口 | 影响 | 归属 |
|---|---|---|
| `DeleteGameAsync`（UI 手删 / Notion 驱动删除）**不写墓碑** | 云端 identity 可能永久残留 | 双向删除阶段 |
| 跨设备 Game 业务键冲突（同 `platform+platform_id` 不同 `global_id`） | 目前只记 Deferred，不合并 | **2e** |
| `sync_deferred_changes` 台账**只写入、无重新处理机制** | 诊断可查、暂不能自动修复 | **2e** |
| `cache/covers` 无淘汰机制 | 磁盘单调增长 | 未排期 |
| `error` 记录无限重试（Notion 线） | 无效请求空转 | 未排期 |
| `tracker/` Python 遗留线**源码已丢失，只剩 `.pyc`** | 无法再维护旧版 | 待确认是否清理 |
| **`tests/` 被 `.gitignore` 排除** | **克隆下来的仓库没有测试**，新 Agent 无法验证改动 | 待用户决定 |
| 思源 `QueryDailyRowIdAsync` 忽略 `gameBlockId` + `keyID` 疑似错列 | 思源写入正确性存疑 | 需真机验证 |

---

## 7. 提交与验证纪律（每次改动照做）

1. **先定位**相关代码 → **理解现有实现** → **判断影响范围**，再动手。
2. **最小改动**：只改必须改的；新增文件优先（尤其多设备线，同步代码集中在新文件里，减少与另一条 feature 的合并冲突）。
3. 改完必须：**构建 0 警告 0 错误** + **跑测试全绿** + **对照验证另外两条分支没被污染**。
4. 提交信息写清：**改了什么 / 为什么 / 未做什么（边界）/ 验证结果**。
5. 汇报格式：改了什么 → 是否测试 → 测试结果 → 是否需要用户裁定。
6. 与现有架构冲突时**先报告冲突**，不要强行实现。

---

## 8. 换 Agent 时的交接清单

- [ ] 读本文件 → `README.md` → `项目接管报告-2026-10-03.md` → `.ai/多设备同步-方案确认-v1.md`
- [ ] `git branch -a -vv` 确认三条分支与当前所在分支
- [ ] 跑三条分支的构建 + 测试，核对基线（203 / 225 / 262）
- [ ] 确认 `tests/` 是否随包提供（若不提供，先向用户要，否则无法验证）
- [ ] 读 `.ai/多设备同步-方案确认-v1.md` 最后一节，确认"待用户裁定"的项
- [ ] 输出一份自己的《项目接管报告》，等用户确认理解正确后再动手
