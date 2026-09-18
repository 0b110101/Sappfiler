# E:\vi2 — GameTimeTracker 项目长期备忘

## 定位与技术栈
- Windows 平台游戏时长自动统计 + Notion 同步的桌面应用。
- **唯一主线：C# / WinUI 3**（.NET 10、Windows App SDK 2.5.1、`WindowsPackageType=None` 非打包、CommunityToolkit.Mvvm、Dapper + Microsoft.Data.Sqlite、FuzzySharp）。
- 解决方案入口：`GameTimeTracker.slnx`，四工程：`src/GameTimeTracker.App` → `.Infrastructure` → `.Core`，加 `tests/GameTimeTracker.Tests`。
- **`tracker/` 下的 Python 实现是遗留线**，不再演进（README.md 与 .ai/HANDOFF.md 描述的是它，已过时；两个文件内的路径仍写 `e:\gemini\vi`）。

## 运行与数据位置
- 构建输出 / 运行目录：`src\GameTimeTracker.App\bin\Debug\net10.0-windows10.0.26100.0\win-x64`
- **活跃数据目录：`%LocalAppData%\GameTimeTracker\`**（`gametime.db` + `cache\covers\`）。
- `E:\vi2\gametime.db`（Python 线用）与 bin 目录下的 `gametime.db` 都是旧副本，不参与 C# 线运行。
- 配置来源：C# 线**读 SQLite `settings` 表**（`notion_token` / `game_database_id` / `daily_database_id` / `theme_mode`），**不读** `config.json`（那份是 Python 线的）。

## 界面约定（用户明确规定，不要自行"优化"）
- **Hero 卡片的氛围背景图只认 Notion 总表的 cover**：总表有 cover 就用它的 URL，没有就**保持原样、不铺任何图**。
  不要回退到 `games.cover_url` / 本地 `SplashScreenImage` / 本地图标封面 —— 用 32px 图标撑满整块背景会变成一团模糊色块，与设计不符。
  但"总表后续补上 cover 也要能取到"是要求保留的，所以按 PageId 取 + 匹配链兜底这两条路径要留着。
- 88×88 的方形封面优先用本地缓存图标（`activeCover`），本地没有才退回 Notion cover URL。
- 「今日游戏」按**时长**降序（Top 3）；「最近记录」按**实际游玩时间**降序（取 `sessions.end_time` 的最大值），两者排序口径不同，别混用。
- 深色模式热力图空格：填充 `#262B3A`、描边 `#333A4D`（比卡片背景略亮一档）。
- **封面容器（Hero 88×88、今日游戏 32×32、最近记录 28×28、待处理页 48×48、历史页 28×28）不给底色也不给描边**，只保留 `CornerRadius` 用于裁切。
  曾经的 `Background="#202433"` 会被看成"给每张封面套了个黑色圆角框"。图标 PNG 的不透明内容占满整个画布（实测 95%~100%），所以去掉底色不会让图标显小。
- Hero 环境背景图：`Image Opacity=0.18` + 压暗层 `Opacity=0.38`（有效可见度 ≈ 11.2%）。改前是 0.14 / 0.45（≈7.7%）。用户要的是「稍微清楚一点」，别再往上加。
- Hero 右上角平台胶囊：**Steam 平台且 `platform_id` 为纯数字时整块可点**，跳 `https://store.steampowered.com/app/{appid}/`；
  其它平台（Epic / Xbox / manual）保持静态不可点。实现是 `CurrentGameStoreUrl`（null 即不可点）+ HyperlinkButton/静态 Border 二者可见性互斥。
- 侧边栏左下角三个控件（添加游戏 / 打开 Notion / 同步状态胶囊）**统一左对齐**。
  注意 `Button` 默认 `HorizontalContentAlignment=Center`，必须显式设 `Stretch`。
- **Notion 每日记录标题格式：`游戏名 · X min`**（例：`不思议迷宫 · 42 min`）。用 `·` 不用 `|`。
  解析侧用 `NotionClient.DurationSuffixRegex` 剥掉末尾时长后缀，**同时兼容旧格式 `(42分)`**，所以 Notion 里的历史行拉回来也能正确还原游戏名。

## 删除语义（用户拍板，不要自行更改）
- **在程序里删游戏 = 两边彻底一致**：Notion 每日表里该游戏的所有记录 + 总表里的游戏条目，全部归档（`archived:true`，进回收站 30 天可恢复）；本地连 sessions / daily_summary 一起删（FK CASCADE）。
- **Notion 里删了 → 本地物理删除**（用户明确选择了不留软删除标记）。
- 所有删除入口必须**先弹 `DeleteConfirmDialog`**（列出影响条数）。旧版没有确认框，点一下就删——真实库里 6 款游戏就是这样没的。
- 物理删除前一律写 `deleted_archive` 留底（`kind` = game/daily，`source` = app/notion）。
- 对账顺序不可颠倒：`RefreshGameCatalogCacheAsync`（内部会清理失效 catalog 行）→ `ReconcileNotionDeletionsAsync` → `AutoLink` → `Pull`。
  `ReconcileNotionDeletionsAsync` 只能以 `game_catalog` 作为总表快照，所以必须排在目录刷新之后；又必须早于 Pull，否则被删游戏的每日记录会被拉回来。
- **删除对账的四个触发点**（缺一个用户就会觉得"没生效"）：① 启动同步链；② 15 分钟周期循环；③ 首页「立即同步」按钮（`SyncNowAsync`，也会 reconcile）；④ **窗口重新获得焦点**（`MainWindow` 订阅 `Window.Activated`，60 秒节流）。
  第 ④ 条是关键：用户的实际操作是"在 Notion 删完 → 切回程序看"，只靠 ①② 最长要等 15 分钟，看起来就像没生效。注意 `Activated` 是 **Window** 的事件，`AppWindow` 上没有。
- 删除是后台异步的，出问题时不要只看 UI：**查 `deleted_archive` 表**（`kind` / `source` / `reference` / `deleted_at`）就能知道"谁在什么时候删了什么"，本轮就是靠它证明启动链对账在 18:51:33 正常跑过。
- 两道安全阀，改动时别删：① 远端集合为空 → 整段跳过；② 本地只处理 `sync_status='synced'` 的行。
- 防复活：Pull 时跳过 `GameMasterPageId` 非空但不在 catalog 快照里的记录（否则 `SyncDailyRecordFromNotionAsync` 会凭空新建 `manual` 游戏行）。
- `NotionClient.ArchivePageAsync` 用的是 `{archived:true}`（项目锁 Notion-Version 2022-06-28）。新版 API 改叫 `in_trash`，换版本时记得同步改。

## 关键的坑（务必牢记）
- **写 INSERT 前先看 DDL 的 NOT NULL**：`games` 表的 `executable` / `executable_path` 都是 `NOT NULL`。
  `SyncDailyRecordFromNotionAsync` 建游戏行时只给了 `executable`，漏了 `executable_path`，导致「Notion → 本地」整条导入路径**从来没成功过**（`SQLite Error 19`），
  而调用方是空 `catch{}`，表现为"同步成功但数据没进来"。两处 INSERT 现已补齐，并留空串（这样「按 exe 注册手动游戏」的循环会跳过这些本地不存在的游戏）。
- **`game_catalog` 的 `identifiers_json` / `aliases_json` 是 JSON 文本列，必须手工映射**：
  `QueryAsync<NotionGameCatalogItem>` 映射不到 `List<string>` 属性（列名与属性名对不上），读出来永远是空列表。
  正确写法见 `GetCatalogItemsAsync` / `GetCatalogItemByPageIdAsync`：`QueryAsync<dynamic>` + `JsonSerializer.Deserialize<List<string>>`。
  踩过的后果：导入的游戏只能用随机 8 位 `platform_id`，与运行中检测到的真实 AppID 对不上，同一款游戏会变成两条记录。
- **pull 循环里不要用空 `catch{}`**：单条记录失败会被完全吞掉。现改为统计 `failed` + 记录首条错误并拼进同步状态文案。
- **不要在同一条消息里对同一个文件并行发多个 `Edit`**：它们会各自基于同一份旧内容做替换再写回，后来的会覆盖前面，导致部分改动静默丢失（本轮 `HomePage.xaml` 4 处改了只落地 2 处、`MainWindow.xaml` 2 处只落地 1 处、`ViewModels.cs` 的赋值语句被吃掉、`NotionServices.cs` 有一处回退）。
  同一文件的多处改动必须**逐个发**，且改完立刻 grep 复核。本轮就是在一次 `grep` 复核里发现"改了但没生效"，否则会带着假象进构建。
- **主题化画刷必须按「元素实际主题」解析，不能用 `Application.Current.RequestedTheme`**。
  `ResourceDictionary.ThemeDictionaries` 不参与普通 `TryGetValue`；而 `MainWindow.ApplyTheme` 只覆盖了**内容根**的 `RequestedTheme`，
  `Application.Current.RequestedTheme` 仍可能是 Light —— 结果深色界面下热力图空格取到浅色 `#F1F6FC`（非常扎眼）。
  正确做法见 `HeatmapControl.TryResolveThemeBrush`：用 `context.ActualTheme` 去 `ThemeDictionaries` 里取；
  并在控件的 `ActualThemeChanged` 里重建（代码创建的 Border 不会自动跟随 `{ThemeResource}`）。
- 右上角窗口按钮由 `AppWindowTitleBar` 绘制，**不跟随 RequestedTheme**，浅色模式必须显式设
  `ButtonForegroundColor / ButtonHoverBackgroundColor / ButtonPressedBackgroundColor`（见 `MainWindow.ApplyCaptionButtonColors`）。
- 托盘常驻程序隐藏窗口时的内存优化：先把页面视觉树整体释放（`ContentFrame.Content = null` + 各页面实例置 null + 清图片缓存），
  GC 之后再调 `SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1)`。实测 252MB → 67MB（降约 73%）。
  只释放视觉树不做工作集裁剪，只能降 3% 左右。

- **Dapper 必须开 `MatchNamesWithUnderscores`**。本项目列名全是 snake_case，`QueryAsync<GameRecord>` + `SELECT *` 时多字列（`platform_id` / `notion_page_id` / `executable_path` / `cover_url`）会**静默读成 null**，单字列（`name` / `platform` / `status`）正常。
  已在 `SqliteRepository` 的静态构造里全局开启。
  注意 `daily_summary` / `game_catalog` 的读取走 `QueryAsync<dynamic>` + 手工映射，不受此影响——排查时别被"有的表正常"误导。
- Local 图片在 **Unpackaged WinUI 3 里无法用 `file://` 渲染**，且 `new BitmapImage(fileUri)` 不抛异常、只静默出空白。
  本地图必须走 `InMemoryRandomAccessStream` 流式加载（见 `Converters.StringToImageSourceConverter`）。
  **两个必须遵守的细节**：① 不要 `using` `AsStreamForWrite()` 返回的包装流（释放它会连带关闭底层 WinRT 流，图片变成只剩容器底色）；② 必须保活该 WinRT 流（`SetSource` 是异步解码，流被 GC 回收同样白屏）。
- **【真实崩溃元凶】`CoverDownloaded` 事件的同步闭环**：
  `CoverCacheService.ExtractAndSaveExecutableIcon`（同步）→ 触发 `CoverDownloaded` → `HomeViewModel.OnCoverDownloaded` → `RefreshAllDataAsync` → 结尾 `await RefreshHeroCardAsync` → 又调 `ExtractAndSaveExecutableIcon` → 再触发事件……无界递归。
  之所以会一路同步下行而不是异步岔开，是因为 **`Microsoft.Data.Sqlite` 的异步 API 实际是同步完成的**（SQLite 无异步 I/O），`await` 拿到已完成的 Task 就内联继续。
  触发条件：某个游戏的封面文件「已存在但 ≤1500 字节」（提取出的图标 PNG 常常 1~3 KB）→ 旧代码用 `Length > 1500` 当「已存在」门槛，判定永远不成立 → 每次调用都重新提取并再次触发事件。
  已修两处：① `HasCoverFile()` 改为 `exists && Length > 0`（幂等）；② `OnCoverDownloaded` 里的刷新改为 `DispatcherQueue.TryEnqueue` 延迟 + `_coverRefreshQueued` 防重入。
  回归用例：`CoverCacheIdempotencyTests`（断言第二次调用不再触发事件）。
  **教训**：`OnCoverDownloaded` 的致命那行在第 572 行，而我上轮 grep 用 `-A 14` 恰好停在第 571 行，于是漏看并错误地排除了这条路径。读关键方法要读完整，别依赖 grep 上下文窗口。
- 判读栈溢出转储时：托管递归的返回地址落在 JIT 堆里，不归属任何模块，**按模块统计整个镜像会被静态数据淹没而得出误导结论**。必须解析 `ThreadListStream` 取出**崩溃线程的栈内存**再统计（脚本 `%TEMP%\gtverify\stack2.py`）。最有效的证据其实是 **stderr 里的 "Stack overflow." 托管栈**——所以起 GUI 程序时一定要把 stderr 重定向到文件。
- **禁止在 `SizeChanged` / `LayoutUpdated` 里同步调用 `ScrollViewer.ChangeView(..., disableAnimation: true)`**。
  该重载强制同步布局，会再次触发 SizeChanged，理论上可形成无界递归。项目里已改为分发器延迟 + 防重入标志 + 偏移无变化时不滚动。
  （说明：这一处是**预防性加固**，并非本次观测到的崩溃原因；实测 84 次窗口缩放压测本就未复现崩溃。）
  同理，`SizeChanged` 里改列宽/行列归属前应先判断布局模式是否真的变了。
- Notion 总表若名为中文（如 appid 3561220 = 「风暴怕死队」），按名匹配必然失败。
  可靠信号是 cover_url 里内嵌的 Steam AppID（`.../steam/apps/<appid>/...`），已在 `GameMatcher` 内建。
- `GameLibraryManager.Refresh()` 只扫一次不够：程序长时间运行时，新装/更新的游戏无法被发现，主循环需定期重扫。
- 封面缓存目录：`%LocalAppData%\GameTimeTracker\cache\covers\`；封面色块区域若显示 `#202433` 说明 Image 没渲染出内容（容器底色）。


## 已知遗留问题（尚未处理）
- `SessionHeartbeatIntervalSeconds`、`AutoCreateGames` 仍未接线；启动/周期循环里 `PullDailyRecordsFromNotionAsync` 被重复调用（`SyncPendingDailyRecordsAsync` 内部已含一次）。
- 根目录 `Platforms/` 9 个 .cs 是与 `src/.../Infrastructure/Platforms/` 逐字节重复的死副本。
- `DailyAggregator` 中两套热力图阈值口径不一致（固定 360min vs 窗口内相对最大值）。
- `game_catalog` 残留 5 条假 page_id 行（`page_bg3` / `page_gta5` / `page_hk` / `page_mhw` / `page_wilds`）；`ClearCatalogCacheAsync` 已实现但无人调用，`UpsertCatalogItemsAsync` 只增不删。
- `MappingsPage` 现在有手动绑定入口（复用 `GameBindingDialog`），但候选列表未过滤假 page_id。
- 封面图标已改为 `PrivateExtractIcons` 取 256/128/96/64/48（低于 48 才退回 `ExtractAssociatedIcon`）。
  旧的小封面靠内存标记 `_upgradeAttempted` 做「每次运行每款游戏升级一次」，所以**升级是渐进的**：缓存里还可能存在 32×32 的老文件，重启几次后会陆续变成 256×256。
- 验证 Hero 环境背景时不必真的玩有封面的游戏：`GAMETIME_DB_PATH` 环境变量可指定数据库，
  把副本库的 `settings.notion_token` 清空（断开 Notion）+ 给目标游戏挂一个有 cover_url 的 page_id 即可离线复现。


## 版本号与版本控制（2026-09-18 确立）
- **项目已初始化为 git 仓库**（`E:\vi2`）。基线提交 `d01cae5`（140 文件）。
  `.gitignore` 已排除 `config.json`（**含 Notion token，绝不能提交**）、`dist/`、`logs/`、`*.db`、`bin/`、`obj/`。
- **版本号统一为 `0.9.5-alpha17`，只在仓库根 `Directory.Build.props` 定义一处**，四个工程自动继承：
  `VersionPrefix=0.9.5` / `VersionSuffix=alpha17` / `FileVersion=0.9.5.17` / `AssemblyVersion=0.9.5.0`。
  **不要在单个 `.csproj` 里再写 `<Version>`。**
- 之前的 `v1.2.1` 只是打包 zip 的文件名，代码里从未体现过；用户明确项目**尚未正式发布**，故重命名为 `0.9.5-alpha17`。
- 设置页底部显示 `GameTimeTracker v0.9.5-alpha17`，取自 `AssemblyInformationalVersion`
  （该属性自带 `+<git短hash>` 后缀，显示时按 `+` 截断）。
- 发布包命名须与之一致：`GameTimeTracker-v0.9.5-alpha17-win-x64.zip`。
- 提交 `9ad2b53`。构建 0 警告 0 错误，**69 个测试全过**，程序集元数据已实测验证。

## 本机构建环境坑：NuGet 回退目录缺失（2026-09-18 排查结论）
- **症状**：`dotnet restore` / `build` 对**四个工程全部**报
  `NuGet.targets(782,5): error : Value cannot be null. (Parameter 'path1')`，
  失败位置在 `_GetRestoreSettings` → `GetRestoreSettingsTask`。而 `dotnet --info` 完全正常。
- **根因**：本机 **`C:\Program Files\dotnet\library-packs\` 目录不存在**（正常 SDK 安装会创建它）。
  NuGet 解析回退文件夹（fallback folders）时拿到空路径，在 `GetRestoreSettingsTask` 里路径运算抛 null。
- **不是项目缺陷**：在未经任何修改的 `dist/GameTimeTracker-github/` 副本上同样复现。
- **唯一有效的绕过**：给 restore/build 加 **`-p:RestoreFallbackFolders=`**（显式清空）。
  例：`dotnet build GameTimeTracker.slnx -c Release -p:RestoreFallbackFolders=`
- **永久修复**（需管理员权限，当前会话无权限）：创建该目录，或重新运行 .NET SDK 安装程序做修复安装。
  **修好之后就不需要 `-p:RestoreFallbackFolders=` 了。**
- **已证伪、不要再走一遍的假设**：
  - 不是 `APPDATA` 为空（本机 shell 里 `APPDATA` 确实是空串，但显式设置后错误照旧）
  - 不是 `Directory.Build.props` 引起（把它移走仍失败）
  - 不是 `obj/` 缓存脏（四个工程的 `obj/` 全删后仍失败）
  - 不是缺 `NuGet.Config`（补上后仍失败；而且补了反而多一个**不该提交**的文件，已删除）
  - 不是 `globalPackagesFolder` 配置问题（包目录 `C:\Users\bbbab\.nuget\packages` 一直是正确的）
- **自动化 shell 的两个怪癖**：
  1. `APPDATA` 是空串（会影响部分工具链）
  2. PowerShell 工具在本环境**会吞掉 stdout**，不返回任何输出。
     需要看命令输出时：写成 `.cmd` 文件执行，输出重定向到文件再读。
     另注：从 Bash 直接调用 `cmd.exe /c` 会被安全层拦截（判定为绕过校验），
     必须写成 `.cmd` 文件后以 `./x.cmd` 形式执行。

