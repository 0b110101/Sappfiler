# 最小必要优化报告

审计对象：GameTimeTracker（C# / WinUI 3 主线）
审计时间：2026-09-18
审计范围（明确声明，便于你判断覆盖度）：

| 范围 | 文件 |
|---|---|
| 同步链路（本次会话持续改动过） | `Infrastructure/Notion/NotionServices.cs`、`Infrastructure/Database/SqliteRepository.cs`、`Core/Interfaces/Interfaces.cs`、`Core/Models/DomainModels.cs` |
| UI 删除/同步入口（本次会话改动过） | `App/MainWindow.xaml.cs`、`App/ViewModels/ViewModels.cs`、`App/Dialogs/DeleteConfirmDialog.cs`、`App/Views/{HistoryPage,MappingsPage,PendingPage}.xaml(.cs)` |
| 测试 | `tests/GameTimeTracker.Tests/*.cs` |
| 工程级死文件 | 仓库根 `Platforms/` |

未在本轮范围内的（没动，也没审）：`App/Converters`、`App/Controls`、`App/Tray`、`Platforms`（src 内正式版）、`tracker/`（Python 遗留线）等。

> 说明：仓库当前**不是 git 仓库**（无 `.git`），所以下面的 diff 是按实际改动手工整理成的 unified diff 格式，不是 `git diff` 的输出。

---

## 0. Unified diff

### 0.1 功能变更：拉取优先（你明确要求的那一条）

```diff
--- a/src/GameTimeTracker.Infrastructure/Notion/NotionServices.cs
+++ b/src/GameTimeTracker.Infrastructure/Notion/NotionServices.cs
@@ class NotionSyncService
     private readonly IGameMatcher _matcher;
 
+    /// <summary>
+    /// 上一次拉取是否成功。默认视为成功，避免在"还没拉过"时误挡上传。
+    /// 用途：上传前必须先拿到 Notion 现有记录 —— 用户可能在使用本程序之前
+    /// 就已经手工记录过时长，那些记录必须先把 notion_page_id 认回来，
+    /// 否则本地会为同一个 (日期, 游戏) 再建一条，Notion 里就出现重复行。
+    /// 拉取失败时宁可这轮不上传（下一轮网络恢复后会补），也不要冒重号的风险。
+    /// </summary>
+    private bool _lastPullSucceeded = true;
+
     public event EventHandler<string>? SyncStatusChanged;
@@ public async Task<int> PullDailyRecordsFromNotionAsync()
             SyncStatusChanged?.Invoke(this, "正在从 Notion 拉取每日记录...");
             var records = await _client.QueryDailyRecordsAsync(_config.DailyDatabaseId);
+            _lastPullSucceeded = true;                     // 查得回来才算这一轮拉取成功
             int synced = 0;
@@ public async Task<int> PullDailyRecordsFromNotionAsync()   // catch 分支
         catch (Exception ex)
         {
+            _lastPullSucceeded = false;                    // 拉不到现状 → 标记失败，供上传前检查
             SyncStatusChanged?.Invoke(this, $"拉取记录失败: {ex.Message}");
             return 0;
         }
@@ public async Task<int> SyncPendingDailyRecordsAsync()
-        // 1. Pull down from Notion first to avoid overwriting or missing records
+        // 1. 先拉取 Notion 现有记录。
+        //    用户可能在用本程序之前就手工记过时长，这些记录要把 notion_page_id 认回来；
+        //    否则下面按"本地没有 notion_page_id 就新建"的判断会在 Notion 里造出重复行。
         await PullDailyRecordsFromNotionAsync();
 
+        if (!_lastPullSucceeded)
+        {
+            // 拿不到 Notion 的现状就不上传：宁可这轮空跑，也不冒险产生重复记录。
+            SyncStatusChanged?.Invoke(this, "拉取 Notion 记录失败，本次跳过上传（下轮自动重试）");
+            return 0;
+        }
+
         SyncStatusChanged?.Invoke(this, "正在同步本地记录到 Notion...");
```

### 0.2 删除弃用：9 个不可达的重复文件

```diff
--- a/Platforms/EaDetector.cs
+++ /dev/null
@@
--- a/Platforms/EpicDetector.cs
+++ /dev/null
@@
--- a/Platforms/GameLibraryManager.cs
+++ /dev/null
@@
--- a/Platforms/GogDetector.cs
+++ /dev/null
@@
--- a/Platforms/IPlatformDetector.cs
+++ /dev/null
@@
--- a/Platforms/SteamDetector.cs
+++ /dev/null
@@
--- a/Platforms/UbisoftDetector.cs
+++ /dev/null
@@
--- a/Platforms/WeGameDetector.cs
+++ /dev/null
@@
--- a/Platforms/XboxDetector.cs
+++ /dev/null
@@
```

（9 个文件内容约 55 KB，未逐行贴出。删除前已打包备份：
`.workbuddy/deadcode-backup/root-Platforms-duplicates.tgz`，md5 清单在同一目录的 `root-Platforms.md5`。）

### 0.3 删除弃用：未使用的 using

```diff
--- a/tests/GameTimeTracker.Tests/NotionDeletionSyncTests.cs
+++ b/tests/GameTimeTracker.Tests/NotionDeletionSyncTests.cs
@@ -1,7 +1,6 @@
 using FluentAssertions;
-using GameTimeTracker.Core.Interfaces;     // 该文件已不再直接引用任何接口类型（FakeNotionClient 已抽成独立文件）
 using GameTimeTracker.Core.Models;
 using GameTimeTracker.Infrastructure.Database;
 using GameTimeTracker.Infrastructure.Notion;
 using Microsoft.Data.Sqlite;
 using Xunit;
```

---

## 1. 修改后的完整代码

本轮**只有一处逻辑新增**（0.1 的拉取优先守卫，共 +12 行）、一处 using 删除、9 个文件删除。
没有改写任何既有函数体，因此被改动文件的"完整代码"与改动前仅差上述行；
完整文件请直接看仓库（`src/GameTimeTracker.Infrastructure/Notion/NotionServices.cs`）。
为避免把 1600+ 行原文复制到报告里造成阅读负担，这里只给出**改动后的关键片段**：

```csharp
// NotionSyncService 字段区
private readonly IGameMatcher _matcher;

/// <summary>
/// 上一次拉取是否成功。默认视为成功，避免在"还没拉过"时误挡上传。
/// 用途：上传前必须先拿到 Notion 现有记录 —— 用户可能在使用本程序之前
/// 就已经手工记录过时长，那些记录必须先把 notion_page_id 认回来，
/// 否则本地会为同一个 (日期, 游戏) 再建一条，Notion 里就出现重复行。
/// 拉取失败时宁可这轮不上传（下一轮网络恢复后会补），也不要冒重号的风险。
/// </summary>
private bool _lastPullSucceeded = true;

public bool IsNotionConfigured => _config.IsNotionConfigured;

public event EventHandler<string>? SyncStatusChanged;
```

```csharp
// SyncPendingDailyRecordsAsync（开头部分）
if (!_config.IsNotionConfigured) return 0;

// 1. 先拉取 Notion 现有记录。
//    用户可能在用本程序之前就手工记过时长，这些记录要把 notion_page_id 认回来；
//    否则下面按"本地没有 notion_page_id 就新建"的判断会在 Notion 里造出重复行。
await PullDailyRecordsFromNotionAsync();

if (!_lastPullSucceeded)
{
    // 拿不到 Notion 的现状就不上传：宁可这轮空跑，也不冒险产生重复记录。
    SyncStatusChanged?.Invoke(this, "拉取 Notion 记录失败，本次跳过上传（下轮自动重试）");
    return 0;
}

SyncStatusChanged?.Invoke(this, "正在同步本地记录到 Notion...");
```

---

## 2. 变更摘要表

| 位置 | 类型 | 修改前 | 修改后 | 理由 |
|---|---|---|---|---|
| `NotionServices.cs` 字段区 | 可读性 + 功能 | 无该字段 | 新增 `_lastPullSucceeded`，含 5 行说明注释 | 让"拉取是否成功"成为可检查的事实；注释解释为什么必须拉取优先 |
| `PullDailyRecordsFromNotionAsync` try 首行 | 功能 | 无 | `_lastPullSucceeded = true;` | 只有查询真的返回，才算本轮拉取成功 |
| `PullDailyRecordsFromNotionAsync` catch | 功能 | 无 | `_lastPullSucceeded = false;` | 把"拉取失败"这一事实保留下来给调用方 |
| `SyncPendingDailyRecordsAsync` 上传前 | 功能（新增守卫） | 拉取失败也照常上传 | 拉取失败则跳过本轮上传并提示 | 防止为"Notion 里已存在但没拉下来的记录"再造一条，产生重复行 |
| `SyncPendingDailyRecordsAsync` 注释 | 可读性 | `// 1. Pull down from Notion first to avoid overwriting or missing records` | 中文说明 + 说明"手工记录的时长要先认回来" | 原注释没说清为什么要先拉 |
| `NotionDeletionSyncTests.cs` 文件头 | 删除弃用 | `using GameTimeTracker.Core.Interfaces;` | 删除 | 该 using 是给当时内嵌的 `FakeNotionClient` 用的，抽出独立文件后已无任何引用 |
| 仓库根 `Platforms/*.cs`（9 个） | 删除弃用 | 9 个源文件 | 已删除（已备份） | 不在任何 csproj/slnx 内，编译不到；7 个与 src 内正式版逐字节相同，2 个是正式版的旧子集 |

---

## 3. 删除清单

| 删了什么 | 为什么确定可删（证据） | 如何验证 |
|---|---|---|
| `Platforms/EaDetector.cs`、`EpicDetector.cs`、`GogDetector.cs`、`IPlatformDetector.cs`、`SteamDetector.cs`、`UbisoftDetector.cs`、`WeGameDetector.cs` | ① 全仓库 `grep -rn "Platforms" --include=*.csproj --include=*.slnx` 只命中 `<Platforms>x86;x64;ARM64</Platforms>`（MSBuild 属性，不是文件引用）；② 与 `src/GameTimeTracker.Infrastructure/Platforms/` 下同名文件 **md5 完全一致** → 零信息量 | 删除后 `dotnet build` 0 错误 0 警告；`dotnet test` 59/59 通过 |
| `Platforms/GameLibraryManager.cs`、`Platforms/XboxDetector.cs` | 同上第 ① 条；② 与 src 正式版 diff 后确认是**旧版本**：src 版多出 `MatchExe()` / `DetectGame()` 实现和全盘符 Xbox 目录扫描，根副本的独有行只有 `[SupportedOSPlatform("windows")]`、`var commonDirs = new[]` 等被取代的写法 | 同上；差异证据：`diff -u Platforms/X.cs src/.../X.cs` 分别为 79 / 25 行差异，方向全是"src 更全" |
| `tests/…/NotionDeletionSyncTests.cs` 的 `using GameTimeTracker.Core.Interfaces;` | 该命名空间只含接口类型（`IDatabaseRepository`/`INotionClient`/…），文件内 `grep "IDatabaseRepository\|INotionClient\|IGameMatcher\|IProcessMonitor"` 命中 0 次；内嵌的 `FakeNotionClient`（唯一使用者）已抽到 `FakeNotionClient.cs` | `dotnet build` 0 错误（C# 会因缺少 using 立即报 CS0246，构建通过即证明未使用） |

**删除前已备份**：`.workbuddy/deadcode-backup/root-Platforms-duplicates.tgz`（含 9 个文件原样），
md5 清单同步存于 `.workbuddy/deadcode-backup/root-Platforms.md5`，可随时核对或还原。

---

## 4. 待确认项（一律**未删**）

| 内容 | 位置 | 为什么不删 | 我的建议 |
|---|---|---|---|
| `ClearCatalogCacheAsync()` | `Interfaces.cs:55` + `SqliteRepository.cs:1020` | 接口成员 + 实现，**0 个调用点**；删除会改动公共接口（你的规则 2/6 要求先停手确认）。它的职责已被本轮新增的 `DeleteCatalogItemsNotInAsync` 取代 | 确认后可连同实现一起删（3 行） |
| `HasCover(string platform, string platformId)` | `CoverCacheService.cs:231` | **0 个引用**（含测试），但属 public 成员 | 建议删；因涉及公共 API，等你点头 |
| `GetSplashBackgroundPath(string? exePath)` | `CoverCacheService.cs:177` | 生产代码 0 引用（Hero 的本地 splash 回退在上一轮按你的设计约定移除了），但 **`CoreTests.cs:359/371` 有测试依赖** —— 按你的规则 3「无测试依赖才可删」，不能删 | 若要清理，需同时删掉那条测试；需你确认 |
| `BoolToOpacityConverter` | `App.xaml:73` 注册 + `Converters.cs:158` | 除 App.xaml 的注册外**没有任何 XAML 使用**；但删除要动 App.xaml 资源键 + 删 public 类型 | 建议删注册与类型；等你确认 |
| `TrackerConfig.AutoCreateGames`、`SessionHeartbeatIntervalSeconds` | `DomainModels.cs` | 声明了但全项目无人读取（未接线）。**你的规则 2 明确禁止改配置键**，所以不删 | 要么接线、要么单独一轮删除 |
| `MappingsPage.OnNavigatedTo` 的 `case IDatabaseRepository repoOnly:` | `MappingsPage.xaml.cs:33` | 当前唯一调用点传的是 2 元组 `(_repo, _syncService)`，该分支看似不可达；但"参数匹配是否可能命中"受未来调用方影响，**不可达性无法充分证明** | 保守保留 |
| `PendingPage.OnNavigatedTo` 两个分支都执行 `_matcher = new GameMatcher();` | `PendingPage.xaml.cs:39,46` | 重复赋值，但把它提到 switch 之外属于"调整控制流"（结构性改动） | 若同意，可合并为一行（行为不变） |
| `PullDailyRecordsFromNotionAsync` 在一轮同步里被调用两次 | `MainWindow.xaml.cs` 启动链 + `SyncPendingDailyRecordsAsync` 内部 | 看似重复 IO，但**顺序有语义**：启动链里那次必须在 `BackfillRelationsAsync` 之前完成，而 `SyncPendingDailyRecordsAsync` 内部那次是"上传前必须拉"的守卫 | 保留；如要优化，应改成显式传入"是否已拉取"参数（改接口，需确认） |
| 根目录 `logo.svg`、`achievements_completed.svg` | 仓库根 | `achievements_completed.svg` 与 `App/Assets/` 同名文件**逐字节相同**，`logo.svg` 则**内容不同**（说明它可能是另一个版本的设计源文件）；两者都不被代码引用（XAML 用的是 `ms-appx:///Assets/...`） | 我倾向删除根目录这两个，但设计源文件可能有保留价值 —— 请你看一眼再定 |
| `tracker/`、`requirements.txt`、`config.example.json`、`config.json` | 仓库根 | Python 遗留线的运行时数据/配置；`config.json` 内**含真实 Notion token**（安全敏感），不属于本轮的"死代码"范畴 | `config.json` 建议尽快从工作区移除并轮换 token；`tracker/` 是否保留由你决定 |
| 三处重复的 `game_catalog` → 模型映射代码 | `GetCatalogItemsAsync`、`GetCatalogItemByPageIdAsync`、`SyncDailyRecordFromNotionAsync` | 属"消除明显重复"，但做法是**提取函数**（你的规则 4 归为结构性改动） | 建议提取 `MapCatalogItem(dynamic)`；本次未执行 |

---

## 5. 验证建议

先跑这两条（覆盖本次改动的行为面）：

```bash
dotnet build GameTimeTracker.slnx          # 期望：0 警告 0 错误（也验证删除 using 后仍可编译）
dotnet test  GameTimeTracker.slnx --no-build   # 期望：59/59 通过
```

拉取优先这条改动**已有针对性测试**（`NotionPullImportTests`）：

```bash
dotnet test GameTimeTracker.slnx --filter "FullyQualifiedName~NotionPullImportTests"
```

- `SyncPending_SkipsUpload_WhenPullFails` —— 断言拉取失败时 `CreateDailyRecordAsync` **一次都没被调用**（即不会造重复行）。
- `SyncPending_UploadsPendingRecord_WhenPullSucceeds` —— 正向对照：拉取正常时仍会正常上传 1 条。

**人工确认建议**（自动化测试没有覆盖到的部分）：
1. 删除的 9 个文件只影响"编译不到的死代码"，无需人工验证；若想复核，`dotnet build` 已足够。
2. `_lastPullSucceeded` 是**实例字段**，多次并发调用（周期循环 + 手动"立即同步"同时触发）时的读值理论上可能被对方覆盖。现有代码本来就没有对同步做并发保护（改动前也一样），所以**并发行为与改动前一致**；如果你打算加并发保护，那是另一件事，需单独确认。

**没有现成测试覆盖、且本轮未改动因而未验证的部分**：
- `MainWindow` 的窗口激活触发对账（`OnMainWindowActivated`）只有我上一轮的手工隔离测试（插入孤儿记录 → 触发窗口失焦/获得焦点 → 记录被删除），**没有自动化测试**。
- `DeleteConfirmDialog` 的文案与按钮样式只有截图验证，**没有自动化测试**。
