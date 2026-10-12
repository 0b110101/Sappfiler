# 日统计口径契约（`daily_summary` / 场次）

> 版本：**count-rule-v1**（2026-10-11）
> 唯一实现：`src/Sappfiler.Core/Services/DailyCountRule.cs`
> 数据库侧包装：`SqliteRepository.ComputeSessionCountForAccountingDate`、`ApplyUnifiedSessionCount`
> 迁移台账：`daily_count_migrations`（migration 012），迁移服务见 `SqliteRepository.DailyCountMigration.cs`

本文件是**长期维护文档**：任何修改「场次」或「日时长」口径的改动都必须先更新这里，
并在 `DailyCountRule.Version` 上递增版本号。

## 1. 事实与派生

| 层 | 表 | 性质 |
|---|---|---|
| 真实事实 | `sessions` | 不可变游玩事实（秒数唯一来源 `duration_seconds`，结束时刻 = `start_time + duration_seconds`） |
| 真实事实 | `daily_duration_adjustments` | 外部（Notion / 手工）对某 (设备, 游戏, 统计日, 来源) 的**声明**；可被撤销 |
| 派生缓存 | `daily_summary` | **不是事实**。由上述两类事实派生，可被重算覆盖；旧值不代表真实游玩 |

铁律：**不得把 `daily_summary` 的旧值反向伪造成 Session 或 Adjustment**；
也不得为了凑数字去改写 Session 时长。

## 2. 场次（`session_count`）规则

1. **门槛**：Session 时长 ≥ 60 秒才算一场（`<60s` 不计数；其秒数仍计入时长）。
2. **归属**：场次归 Session **起始时刻的会计日**（`AccountingDateHelper.GetAccountingDateString`）。
   跨日 Session 只在起始日计 1 次，**不在第二天重复计**。
3. **时长与场次解耦**：时长仍按既有跨日拆分规则分摊到各会计日
   （`GameSessionManager.ComputeDailyDeltas`，"段和必须严格等于 Session 总秒数"）；
   场次归属规则**不影响**时长分摊。
4. **声明 bonus**：某统计日的有效 Adjustment 声明秒数之和 > 60 秒时，额外计 1 次。
5. **残差 bonus**：来源不明的历史残差 > 0 时**不归零**；它与声明秒数**相加后只判定一次**
   （`DailyCountRule.HasDeclarationBonus(declaration, residual)`）——
   因此 **声明与残差永远不会各自触发、也不会重复计数**。
6. **不设 manualBonus**：旧版 `daily_summary.duration > 本地会话秒和 + 60 ? +1` 的启发式已废弃；
   它既不能重算、也会制造"当天有时长却没有会话支撑"的伪场次。
7. **幂等**：同一份事实重复重算必须得到完全相同的值（`Compute` 纯函数，无隐藏状态）。
8. **会计日来源**：`settings.daily_cutoff_hour`（未配置 = 24 = 自然日，经 `NormalizeCutoffHour` 归一）。

## 3. 时长（`duration_seconds`）规则

- 期望值 = `Σ 会话派生秒数（按会计日拆分） + Σ 该日有效声明秒数`。
- 存值 **低于** 期望值 ⇒ 视为缺算，可上调（并置 `sync_status='pending'`）。
- 存值 **高于** 期望值 ⇒ 视为**来源不明的历史残差**，**原样保留**并报告，
  绝不在重算时静默抹掉（C 类保护语义）。
- 秒数变更只在**显式授权**的迁移/修复中发生；本契约与迁移服务默认**不改秒数**。
- **删除一条每日记录**（`DeletePlayRecordAsync`，2026-10-11 粒度重写）：删除粒度是
  **`(游戏, 会计日, 会话分片)`**，不是整条 Session —— 跨日会话的秒数被拆分到最多两个会计日，
  删整条会话会误删另一个会计日的有效时长。实现写入 `daily_play_exclusions`
  （同步实体 `DailyPlayExclusion`），统计唯一实现 `SumSessionDerivedSeconds` / 场次口径会跳过这些分片；
  **删除之后新产生的会话不受影响**。`historical_residual` 是独立事实、**不撤**，
  故删除后该日若仍有它 ⇒ 结果标记 `FullyDeleted = false`（界面显示"部分删除"，绝不谎报完全成功）。
- **删除范围（产品语义）**：删除的是**删除时刻本机已知的、归属于该会计日的已结束会话分片**。
  其它设备上尚未同步到本机的分片不在范围内 —— 同步到达后会**正常计入**该日（此前未同步的合法时长，
  不是"数据复活"）；进行中的会话不在范围内，结束游戏后可**再次删除**完成清理。
  删除**不是**"永久禁止该日出现时长"：之后真正发生的新游玩照常记录、同步与统计
  （这也是 `deleted_archive.kind='play-record'` **不**参与自愈审计"有意删除"判定的原因 ——
  有新事实时该日应重新建行，且建出来的值不含被作废分片）。

## 4. 写入路径（必须全部走同一规则）

| 路径 | 位置 | 说明 |
|---|---|---|
| 心跳落时长 | `SqliteRepository.AddSessionDurationToDailyAsync` | 只落秒数；场次统一由 `ApplyUnifiedSessionCount` 重算 |
| 会话结束（含跨日尾段）| `EndSessionWithOutboxAsync` → `ApplyDailyDurationDelta` + `RecomputeSessionCount` | 同一事务 |
| 遗留结束接口 | `SqliteRepository.EndSessionAsync` | 同样走统一规则（不再手写 manualBonus） |
| 远端会话落地 | `ApplyRemoteSession` | 新会话插入后 `RecomputeSessionCount` |
| 远端声明落地/撤销 | `ApplyRemoteDailyAdjustment`、`RevokeLocalDailyAdjustmentAsync` | 只动声明事实 + 重算该 (game, 日) |
| 时长增量 | `ApplyDailyDurationDelta` | 只落秒数 + 统一重算场次 |
| 身份合并/去重 | `MigrateGameDataToCanonical` | **不再相加场次**，改为按 canonical 事实重算 |
| 一致性审计/修复 | `ReconcileDailySummariesCore` | 场次默认只报告（`applyCounts` 显式开启才写） |
| 历史场次迁移 | `DailyCountMigration`（本契约配套） | 默认 dry-run，显式 apply，带台账与回滚 |
| **删除一条每日记录** | `DeletePlayRecordAsync` | **单事务**：`(game, 日, 会话)` 分片作废（`daily_play_exclusions`）+ 撤回该日声明（残差除外）+ 墓碑/入队 + 重算**所有受影响日期**；重算跳过被作废分片，且场次同口径 |

## 5. 旧计数迁移的分类（数据驱动，禁止按日期/名称硬编码）

| 分类 | 判定（可由数据验证） | 自动迁移 |
|---|---|---|
| `recomputable` | 秒数与事实一致、残差=0、无特殊信号 | ✅ |
| `legacy_manual_bonus` | `存值 = 会话数 + 1` 且 `存秒 > 当日会话秒 + 60`（旧 manualBonus 签名） | ✅ |
| `default_count_residue` | `存值 = 1` 且无声明支撑且 `会话数 ≠ 1`（INSERT 默认值残留） | ✅ |
| `cross_day_accounting` | 该日有"起始在其它日、但有时长落入本日"的会话 | ✅ |
| `adjustment_counting` | bonus 项生效导致与纯会话计数不同 | ✅ |
| `unknown_residual` | **残差 > 0**（有存量但无事实支撑） | ❌ 保守保留 |
| `orphan_or_archived` | 游戏已归档 / 事实的 game 行不存在（孤儿） | ❌ 只报告 |

安全闸门（替代旧的"两端 mismatch 必须为 0"）：
每条自动记录必须有**确定目标值 + 可解释原因**；未知残差/孤儿/身份冲突不自动覆盖；
秒数不变；逐行可对账；幂等；可回滚（台账含 before/after）。

## 6. 变更流程

1. 修改 `DailyCountRule`（并递增 `Version`）；
2. 更新本文件的规则表；
3. 补 `DeviceSyncDailyCountContractTests` 对应用例；
4. 全量测试 + Release 构建；
5. 真实库迁移一律先 `PlanDailyCountMigrationAsync`（dry-run）出清单，人工确认后才 `Apply`。
