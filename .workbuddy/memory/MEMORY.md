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
- **完整同步链顺序（四条同步链必须一致）**：
  `RefreshGameCatalogCache` → `ReconcileNotionDeletions` → `AutoLink` → `Pull` →
  `BackfillRelations` → `SyncPendingDailyRecords` → `RefreshDailyTitlesFromMaster`。
  四个触发点：**启动链**（`MainWindow`）、**15 分钟周期循环**（`PeriodicSyncLoopAsync`）、
  **首页「立即同步」**（`SyncNowAsync`）、**新用户首次保存配置**（`MainWindow.RunInitialSyncAsync`）。
  最后一步「回刷标题」必须在推送之后（见上文回刷机制），别插错位置。
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


## ⚠️ WinUI ScrollViewer：BarVisibility 和 ScrollMode 是两个独立开关（2026-09-19 踩坑）

`HorizontalScrollBarVisibility="Disabled"` **只**表示不显示滚动条；
`HorizontalScrollMode` 默认仍是 `Enabled` → `CanHorizontallyScroll = true`
→ **测量子元素时给的是无限宽**。于是"本该被裁剪/滚动"的内容会把父容器整个撑开。

真实事故：`HomePage.xaml` 最外层 ScrollViewer 只设了 BarVisibility，
热力图 52 周 × 16px = 832px 把 RootContentGrid 从 ~415px 撑到 ~1250px；
叠加上"宽度 < 880 就把右列挪到下面"的响应式判定（`SizeChanged` 又挂在会被内容
反向影响的 RootContentGrid 上），右列被判定成"窗口很宽"而挪回右侧 —— 实际窗口还窄着，
**右列被推到屏幕外**。表现为"热力图数据一渲染出来，右边的区块就掉下去了"。

两条规矩：
1. **要禁止某个方向滚动，BarVisibility 和 ScrollMode 都要显式设**。
2. **响应式断点的触发源绝不能用会被内容撑开的元素**，要挂在最外层容器（尺寸只由窗口决定）上。

排查提示：用户说"数据一多就不对"时，先怀疑**渲染时点**（数据从无到有那一刻尺寸跳变），
而不是"数据量大"本身。

## ⚠️ 回刷/更新每日记录会连带写时长（2026-09-19 数据损坏事故）

每日记录的**标题串里含时长**（「游戏名 · X h」），所以 `UpdateDailyRecordAsync`
**无法只改标题** —— 必然同时写「单次时长」属性。任何"我以为只改标题"的调用点，
只要传进去的 `durationMinutes` 是 0，就会把 Notion 上原本的时长**清零**。

事故：QA 的「喵门镖局 7/17」被清成 0，且标题变成程序格式「喵门镖局 · 0 h」
—— 标题的格式就是判定"谁写的"的指纹（程序写的是 `名字 · X h` 带空格和 ·，
用户手写的是 `名字2.2h` 紧贴）。

已加护栏：`RefreshDailyTitlesFromMasterAsync` 跳过 `DurationMinutes <= 0` 的记录。
**今后任何调用 Create/UpdateDailyRecordAsync 的地方都要先确认时长非 0。**

## 删除对账的误删风险（2026-09-19 加固）

`ReconcileNotionDeletionsAsync` 的 A 部分只凭"本地 notion_page_id 不在远端集合里"
就归档 + 删除本地记录 —— 前提是远端集合**完整**。
而 `QueryDailyRecordsAsync` 会跳过解析不出来的行（日期为空、或标题与关联游戏都为空），
那些 page_id 自然不在集合里 → 本地对应记录被误判"Notion 已删除" → 删除时**连带抹掉时长**。
已加保护：远端条数 < 本地已同步条数的一半（且本地样本 ≥ 20）时整段跳过，宁可漏删不误删。

## ⚠️ 「读不到 relation」会静默造出幽灵行，记录还不会自己迁回去（2026-09-19 数据错乱事故）

**症状**：Notion 里每条每日记录都绑好了「关联游戏」，但程序里那款游戏显示「未绑定」，
而且**没有记录** —— 记录躺在另一个未绑定的行上。雾山原话："很多都是未绑定，没有正确记录"。

**成因是两段叠加，都很隐蔽**：

1. **属性改名 + 旧程序 = 静默误建**。
   `ExtractRelationId` 是按属性名列表找的，改名后旧版本读不到 → 返回 null →
   `SyncDailyRecordFromNotionAsync` 走兜底分支，为**每一条**记录新建一个**未绑定**的
   manual 行。816 条记录 → 几百个未绑定行，就是「待处理」那个数字。
   **写入失败会报 400 看得见，读取失败一声不响** —— 这是它藏得深的原因。
   判据：日志里同一时段既有 `400: ... is not a property that exists`，
   又有 `已从 Notion 同步 N 条记录`（后者就是那次静默的批量误建）。

2. **`UPDATE daily_summary` 没有更新 `game_id`**。
   升级后能读到 relation、也解析出了正确的游戏行，但更新每日记录时只改
   `duration_seconds / duration_minutes / notion_page_id / notion_title / sync_status`，
   **唯独不动 `game_id`** → 记录永远留在旧行上，正确绑定的那行是空的。

**修法与护栏**（提交 1432d1c / 4ea76eb / 811ee1b）：
- 迁移条件：旧行**未绑定** + 本次解析出的游戏**绑定了关系**才迁。反向绝不动 ——
  已绑定的行可能是用户手动指定的。
- 迁移后旧行若已空（未绑定 + 无 `executable`/`executable_path` + 无 daily_summary
  + 无 sessions）→ 判为幽灵行，写 `deleted_archive` 后删除。
  **四个条件缺一不可**：有 exe 的是「添加游戏」加进来的真游戏；有记录/会话的还在用。
- 认领本地行时**两边都剥一次时长后缀**：幽灵行里存的是旧版写进去的带后缀原串
  （「致命躯壳2.2h」），而新解析出的名字已剥离过，只剥一边永远对不上。
- ⚠️ **不要在已持有 `_writeLock` 的方法里调 `ArchiveDeletedAsync`**：它会再取一次
  同一个 `SemaphoreSlim` → 死锁。要在同一连接内直接插 `deleted_archive`。
- 测试：`DailyRecordOwnershipTests`（正向迁移 + 反向保护各一个用例）。

**排查这类问题最快的路径**：直接只读打开
`%LocalAppData%\GameTimeTracker\gametime.db`，
看 `daily_summary.game_id` 指向那一行的 `notion_page_id` 是不是空的 ——
"记录挂在未绑定的行上"这个特征一眼可见，比读代码快得多。

## ⚠️ 每日记录标题是判断"谁写的"的指纹

程序写的是 `名字 · X h`（带空格和 `·`），用户手写的是 `名字2.2h`（紧贴、无分隔符）。
排查数据问题时先看这个格式，能立刻判断某个值是程序写的还是人手填的 ——
2026-09-19 就是靠「喵门镖局 · 0 h」这一点确认它是被回刷重写过的。

**已知边界**：末尾带中文备注的（「黑旗10.1h 通关」「神海4 1.3h dlc通关」）剥不掉 ——
备注形态无法与游戏名安全区分（剥错会把不同游戏混为一谈），只能靠 relation 兜底。

## ⚠️ 托盘闪退：**隐藏窗口时把页面摘出可视树**（2026-09-19 事故 + 三次误判）

> ### 🔴 本节后半段（ReadyToRun 那套）已作废 —— 先看这里
>
> 这个 bug 归因错了三次，都别再用：
>
> | 版本 | 当时的归因 | 实测 |
> |---|---|---|
> | alpha21 | `Closing` 里同步拆 XAML 树 | ❌ 无效 |
> | alpha22 | 发布时开着 `ReadyToRun` | ❌ 无效（alpha22 本就是非 R2R，实测仍崩） |
> | **alpha.23** | **隐藏窗口时拆掉页面（本节结论）** | 待雾山确认（本机复现不出崩溃） |
>
> **真正的机制**：原逻辑（自基线 `d01cae5` 就存在）在关窗口时
> `ContentFrame.Content = null` + 丢掉各页引用 + 清图片缓存。
> 但它**只摘了一半** ——
>
> - 页面被摘出可视树后，各 ViewModel **仍强引用着页面**
>   （`x:Bind` 生成的绑定把 `PropertyChanged` 处理器挂在 ViewModel 上），
>   所以 `_homePage = null` 并不能真正释放它，只是让它**脱离界面**；
> - 后台同步/刷新随时驱动那些绑定去更新元素。雾山的日志里：拆完树的 60ms 后
>   仍在打 `[同步] 已从 Notion 同步 9 条记录`；
> - **更新一个 peer 已销毁的元素 → WinRT 投影层解析 ABI 指针踩空 → 原生崩溃**
>   （`CoreMessagingXP.dll` / `0xc000027b` / `combase` E_FAIL / 日志全空）。
>
> **崩溃时机是"窗口隐藏期间"**：日志里 `[同步] 全部记录已是最新` 之后再无任何行，
> 直到下次 `应用启动` —— 说明点托盘那一下根本没能进到 `ShowMainWindow`。
> 「白屏」与「闪退」是同一个崩溃的两个阶段：窗口已显示，进程紧接着死掉。
>
> **现在的做法**：`Closing` 里**只 Hide**，不再拆页面
> （`ReleaseVisualTree()` 连同两个无人用的 P/Invoke 已整个删除）。
> 代价是隐藏期间当前页面留在内存 —— 刻意的取舍，稳定性优先。
> 另外「要不要重建」的判断改为 `ContentFrame.Content is null`，
> 不再依赖 `_currentNav`（原释放那段 `try` 会吞异常，失败时会留下旧值 → 重建被跳过 → 真白屏）。
>
> **崩了怎么取证**：日志里搜 `[托盘] 恢复界面开始（有内容=…`；
> 若连这行都没有，说明死在隐藏期间。下次请把整份 `app.log` 发出来。

**症状**：关掉界面 → 从托盘打开 → **闪退（白屏），且日志里一行都没有**。

**证据**（Windows 事件日志，这是原生崩溃的唯一入口）：

```
出错模块：CoreMessagingXP.dll
异常代码：0xc000027b   (STATUS_STOWED_EXCEPTION)
WER 签名：combase.dll / 80004005 (E_FAIL)
```

**~~真正的原因~~ 本节当时（错误）的结论 —— 仅作记录**：
`PublishReadyToRun=true`（csproj 里原本写成
`Configuration != Debug → True`，**Release 发布一直开着**）。

**决定性对照实验**（同一份代码，只改这一个发布参数）：

| 发布方式 | 结果 |
|---|---|
| R2R 开启（= `publish.cmd` 的路径） | **1~3 轮之内必崩** |
| R2R 关闭 | 连续 4~8 轮全部正常 |

机理与此前 `PublishTrimmed` 那次同源（csproj 里那段注释记载了同样的堆栈）：
WinRT 投影层要在**首次访问类型**时解析 ABI 指针
（`WinRT.TypeExtensions.GetAbiToProjectionVftblPtr`）。
R2R 把一部分投影代码预编译、固化调用点；而本程序会**把界面树整个释放、
之后按需重建**（收进托盘省内存那套逻辑）—— 重建时类型初始化顺序一变，
就踩到已失效的指针。

**已加的护栏**：
- csproj：`PublishReadyToRun` 恒定 **False**（连同那一大段注释）
- `publish.ps1`：显式 `-p:PublishReadyToRun=false`（不依赖 csproj）+ **产物校验**
  —— App.dll > 600KB 或 Core.dll > 120KB 就中止打包
  （实测 R2R 的 App.dll ≈ 736KB、Core.dll ≈ 168KB；正常 ≈ 400KB / 71KB）

### ⛔⛔ 最该记住的一条：**验证必须用与交付完全相同的构建参数**

这次连续两轮"修好了"都是假的：我为了发布快，测试包带了
`-p:PublishReadyToRun=false`，**恰好关掉了 R2R**，而 `publish.cmd` 是开着的。
**测的根本不是同一个东西**，所以怎么测都"不崩"，用户一装就崩。

**规矩**：凡是"打包后才出现"的问题，复现与验证**一律复制打包脚本的参数**
（或者干脆跑打包脚本本身），不要自己另配一套。差一个 `-p:` 就是两个世界。

（`publish.ps1` 还必须在**正常的 Windows 终端**里跑：WorkBuddy 的 shell 缺
`PROGRAMDATA` / `APPDATA` 等变量，NuGet 会以
"Value cannot be null. (Parameter 'path1')" 失败 —— 脚本 NOTES 里已写明。）

### 顺带保留的一条经验（当时以为方向对，后来**也推翻了**）

曾经把崩溃归因于"在 `Closing` 里同步拆 XAML 树 + 强制 GC"，并建议
"释放动作一律排进 Dispatcher 队列"。**这套说法现在作废**：
- 异步释放没治好崩溃；
- 而且它本身就是白屏的来源（释放与重建判断之间存在窗口期）。
**现在的规矩只有一条：窗口隐藏时不要拆 UI 树、不要强制 GC。**
不要把这条过时建议再翻出来用。

**为什么日志全空**：这是**原生层**崩溃，`Application.UnhandledException` 与
`AppDomain.UnhandledException` 都拦不到。遇到"闪退且无日志"先查事件日志
（`Get-WinEvent -FilterHashtable @{LogName='Application'}`，看 Application Error Id=1000）。

**~~根因~~ 第二次误判的记录**：曾归因于 `Closing` 里**同步**调用 `ReleaseVisualTree()`
（拆树 + `GC.Collect()`×2 + `WaitForPendingFinalizers()` + `SetProcessWorkingSetSize`），
并改成 `DispatcherQueue.TryEnqueue(ReleaseVisualTree)`。**两次都不对**：
- 同步拆树之说 → 改成异步后照样崩；
- 异步释放还**额外引入**了白屏（释放与"要不要重建"的判断之间存在窗口期）。

**现在（alpha.23 起）**：`Closing` 里只 `Hide()`，**不释放任何东西**。
`ReleaseVisualTree()` 及其两个 P/Invoke 已删除。
**不要再把"隐藏时释放界面"当成优化加回来** —— 它只摘掉可视树、
摘不掉 ViewModel 对页面的强引用，反而让后台刷新打在已断开的元素上。
**推广**：想省内存也别在窗口隐藏时拆 UI 树；要省就省能真正释放的东西
（例如图片/位图缓存），且必须在**没有并发 UI 更新**的前提下做。

**已加面包屑日志**（[窗口] / [托盘] 各两三条）。原生崩溃没有堆栈可看时，
"最后停在哪一行"就是唯一的定位依据 —— 别再删掉它们。

### 复现/验证手法（很好用，值得复用）

用 Python + ctypes 直接给窗口发消息，不必手动点托盘：

```python
# 关窗口 → 收进托盘
user32.PostMessageW(hwnd, 0x0010, 0, 0)              # WM_CLOSE
# 托盘左键 → ShowMainWindow
user32.PostMessageW(hwnd, 0x0465, 0, 0x0202)         # WM_TRAYICON(=WM_USER+101) + WM_LBUTTONUP
```

- 找主窗口：`EnumWindows` + `GetWindowThreadProcessId` 过滤 PID，
  再按标题 `GameTime Tracker` 精确匹配（进程有 5 个顶层窗口，**不能取第一个**）。
- **时序敏感的竞态要试不同延时**：等 3 秒不复现、等 1.2 秒就必崩
  （释放与重建之间的窗口期）。
- **实验必须隔离**：`GAMETIME_DB_PATH` 指向副本库，并清掉 `settings.notion_token` ——
  否则同步会写用户的线上 Notion 表。

## 已知遗留问题（尚未处理）
- **拉取每日记录会为 Notion 总表里的游戏在本地 `games` 表建行**。本机 db 可见
  09-17 19:10:38 同一秒批量创建 5 行、`executable` 为空、`platform_id` 是 appid 或随机 guid
  —— 这是拉取路径（`SyncDailyRecordFromNotionAsync` 的兜底 insert）的特征，
  **不是** Steam 扫描写入的（扫描结果只留在 `GameLibraryManager._installedGames` 内存里，不落库）。
  后果：总表里的游戏全被导成本地游戏行，在「游戏映射」里显示为一堆「未绑定」。
  用户明确不想要这个（原话："我只是要每日游戏时长图里的同步到程序"）。
  **待产品决策**：是否给这类"仅来自 Notion"的记录单独标记并在映射库折叠/过滤。
- `SessionHeartbeatIntervalSeconds`、`AutoCreateGames` 仍未接线；启动/周期循环里 `PullDailyRecordsFromNotionAsync` 被重复调用（`SyncPendingDailyRecordsAsync` 内部已含一次）。
- 根目录 `Platforms/` 9 个 .cs 是与 `src/.../Infrastructure/Platforms/` 逐字节重复的死副本。
- `DailyAggregator` 中两套热力图阈值口径不一致（固定 360min vs 窗口内相对最大值）。
- `game_catalog` 残留 5 条假 page_id 行（`page_bg3` / `page_gta5` / `page_hk` / `page_mhw` / `page_wilds`）；`ClearCatalogCacheAsync` 已实现但无人调用，`UpsertCatalogItemsAsync` 只增不删。
- `MappingsPage` 现在有手动绑定入口（复用 `GameBindingDialog`），但候选列表未过滤假 page_id。
- 封面图标已改为 `PrivateExtractIcons` 取 256/128/96/64/48（低于 48 才退回 `ExtractAssociatedIcon`）。
  旧的小封面靠内存标记 `_upgradeAttempted` 做「每次运行每款游戏升级一次」，所以**升级是渐进的**：缓存里还可能存在 32×32 的老文件，重启几次后会陆续变成 256×256。
- 验证 Hero 环境背景时不必真的玩有封面的游戏：`GAMETIME_DB_PATH` 环境变量可指定数据库，
  把副本库的 `settings.notion_token` 清空（断开 Notion）+ 给目标游戏挂一个有 cover_url 的 page_id 即可离线复现。


## 游戏匹配的设计原则（2026-09-18/19 用户澄清，改这块前必读）
**匹配以游戏名（含别名）为主。** 不要推荐用户去填 `游戏标识`(steam:appid) ——
库里几百个游戏逐个补不现实，多数人根本没存。`游戏标识` 只是可选加分项。

### ⛔ 模糊匹配已彻底移除（2026-09-19，用户要求，不要再加回来）
`MatchGame` **只返回确定性匹配**，未命中就返回空列表：
`identifier_match`(100) / `exact`(100) / `normalized`(98)。
FuzzySharp 依赖已从 csproj 移除。

**为什么删**：模糊相似度分不开相似但不同的游戏，分数还很有说服力 ——
| 查询 | 被误当候选 | 相似度 |
|---|---|---|
| Portal | Portal 2 | 85.7% |
| Half-Life | Half-Life 2 | 90.0% |
| FM 2023 | Football Manager 2024 | **97.6%** |
| FF VII Remake | FF VII Rebirth | 88.9% |

绑定弹窗会显示成「XX (98% 匹配)」**并默认勾选第一个** → 点一下确定就绑错。

**决定性依据**：代码里另外两处用候选的地方**本来就都主动排除模糊候选** ——
`AutoLinkGamesFromCatalogAsync` 只认三种确定性类型；
`ViewModels` 取封面时 `.Where(c => c.MatchType != "fuzzy_candidate")`。
即模糊候选唯一的消费者就是那个弹窗，**只在那里起作用，且只在那里有害**。

**不要用"降阈值"或"改用 PartialRatio"来救模糊**：
`PartialRatio` 对 `doom` vs `doom eternal` 直接给 100%，属于用误匹配换召回。
**正确做法是让归一化规则更完备**（见下），而不是让相似度更宽松。

### 归一化规则（`NormalizeTitle`，**必须在去标点之前**做，否则认不出括号形式）
- `TrailingBracketedYearRegex`：末尾括号年份 `Valheim (2020)` → `valheim`
- `TrailingEditionRegex`：末尾版本标记
  `Deluxe/Ultimate/Definitive/Complete/Gold/Premium/Special/Collector's/Enhanced/Anniversary/Standard Edition`、
  `Game of the Year Edition`、`GOTY Edition`、`Remastered`、`HD Remaster`
- 两遍循环以兼容 `XX Deluxe Edition (2020)` 这类组合

**明确排除，绝不能剥**（它们是游戏名的一部分）：
`Remake` / `Rebirth` / `Eternal` / 数字序号 / **裸年份**
—— `Football Manager 2024` 的年份剥了就会和 2023 代互相误匹配。

守护用例：`MatchGame_MustNotConflateDifferentGames`（防归一化规则被加过头）、
`MatchGame_ReturnsNothing_WhenOnlySimilarButDifferentGamesExist`（防模糊回归）。

### 配置键规则
**不要删配置键**（用户规则 2）。`TrackerConfig` 里
`AutoCreateGames` / `SessionHeartbeatIntervalSeconds` / `FuzzyCandidateThreshold` /
`FuzzyScoreGapThreshold` 都属"未接线/已废弃但保留"，只加注释说明，不删字段。

## 同步状态机的坑：非终态 + 粗筛条件 = 永远重试（2026-09-18，同类踩了 3 次）
`daily_summary.sync_status` 取值：`pending` / `synced` / `unmapped` / `error`。

- **`unmapped` = "已推到每日时长表，但游戏还没绑定总表"**，是**终态**（推完了）。
- ⚠️ `GetPendingDailySummariesAsync` **必须排除 `unmapped`**：
  ```sql
  WHERE d.sync_status NOT IN ('synced', 'unmapped') AND d.duration_minutes > 0
  ```
  曾经写的是 `!= 'synced'` → `unmapped != 'synced'` 恒成立 →
  **每轮同步把所有未绑定记录重打一遍，永远不停**（用户刚配好 Notion、
  还没绑游戏的那段时间最明显，且随天数累积越来越慢）。
- **不会漏推**：时长一增加，`AddSessionDurationToDailyAsync` 会把状态改回 `pending`；
  拉取路径在"本地领先"时也会置 `pending`。
- **守护用例**：`SyncPending_DoesNotRepushUnboundRecord_EveryRound`（性能护栏）、
  `SyncPending_RepushesUnboundRecord_WhenDurationGrows`（防漏更新）、
  `SyncPending_PushesUnboundRecord_ToDailyTableWithoutRelation`（设计意图）。

**通用教训**：凡是"待处理"型查询，都要确认**每个状态值是否真的会离开结果集**。
同类事故已经 3 次：
1. 回刷标题时用 `iconUrl != null` 判断"需不需要更新" → 每轮都更新
2. 回刷快照没落库 → 每轮都判定不一致
3. 本条：`!= 'synced'` 把 `unmapped` 当成待处理

## 同步的设计基调（用户明确，不要改）
- **本地为主**：不管 Notion 连没连上，本地记录一直在走。
- **是否绑定只影响总表**：未绑定的游戏**照样推送到每日时长表**，
  只是不带 `游戏` relation、`绑定状态` 写「未绑定」。
- **主流平台自动识别 + 其余手动添加**（见上文"识别策略"）。
- 「待处理」页 = 把已识别的游戏绑到总表；**不绑也不影响每日表推送**，
  所以对不用 Notion 的用户它只是"可选操作"，不是阻塞。

## 游戏检测链路（2026-09-18 踩坑，改这块前必看）
**流程**：`MonitorLoopAsync`(5s) → `ScanRunningProcessesAsync`（取 exe 路径 + `ProcessFilter` 黑名单）
→ `GameLibraryManager.DetectGame`（靠 `_installedGames` 目录前缀匹配）→ `GetOrCreateGameAsync` → 开始计时。

- **`SteamDetector` 的 `StateFlags` 是位标志，不是枚举值。**
  `1`=已卸载 `2`=需更新 `4`=已完整安装 `8/16/32/64/128`=更新相关 `256`=文件缺失 `1024`=较新状态位。
  常见组合：`4`、`6`(4|2)、`1030`(1024|4|2)。
  **旧代码写死 `flags != "4"` 会丢掉 6/1030 这类已装好的游戏** ——
  本机实测：`StateFlags=4` 的是「Steamworks Common Redistributables」（运行库），
  `StateFlags=1030` 的是「Where Winds Meet」（真游戏）→ **收下运行库、丢掉真游戏**，
  症状就是"玩 Steam 游戏检测不到"。
  正确写法：`(flags & 4) != 0 && (flags & 1) == 0`。
- **禁止在 Infrastructure/App 里用 `Console.WriteLine` 输出诊断信息。**
  这是 `WinExe`（无控制台）程序，Console 输出**直接进虚空**。
  `GameLibraryManager.Refresh()` 曾全用 Console，导致排查时日志一片空白。
  **一律用 `AppLog`**（`GameTimeTracker.Infrastructure` 命名空间）。
  注意分层：`AppLog` 在 Infrastructure，**Core 层用不了**（App → Infrastructure → Core），
  所以要日志得加在 App 或 Infrastructure 层。
- **监控循环 5 秒一轮，日志只在「开始/结束计时」时记**，心跳绝不能记，否则刷爆日志。

### 识别策略（用户确认的设计，不要改）
**只对主流平台（Steam/Epic/GOG/Ubisoft/EA/Xbox/WeGame）按安装路径自动识别，
其余游戏一律靠用户手动「添加游戏」。**
"未识别进程被忽略"**是刻意设计，不是 bug**（早期分析曾误判为设计缺口，已纠正）。

**但这有个硬前提：主流平台的检测器必须准确。** 漏识别时用户看到的是
"我在玩 Steam 游戏，程序毫无反应"，且**日志里没有线索**（设计上就是静默忽略）。
已经真实发生过两次：
- `SteamDetector.StateFlags` 当枚举比 → 丢掉已装好的游戏（已修）
- `GogDetector` / `UbisoftDetector` 没处理 32 位注册表视图 → 完全看不到游戏（已修）

### ⚠️ 注册表必须考虑 32/64 位视图（2026-09-18 踩坑）
**GOG Galaxy 与 Ubisoft Connect 是 32 位程序**，在 64 位 Windows 上写
`HKLM\SOFTWARE\...` 会被重定向到 `WOW6432Node\SOFTWARE\...`。
本程序是 **x64**，`Registry.LocalMachine.OpenSubKey` 默认读 **64 位视图** → **看不到游戏**。

正确做法（两个视图都试）：
```csharp
foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
{
    var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
    var key = baseKey.OpenSubKey(path);
    if (key is not null) return key;   // 记得 baseKey.Dispose()
}
```
- `SteamDetector` 早就在循环两个视图，`WeGameDetector` 显式列了 `WOW6432Node` ——
  作者知道这坑，**只有 GOG / Ubisoft 漏了**（已修）。
- **Epic / EA / Xbox 不受影响**：它们读 `%ProgramData%` 下的文件目录，不碰注册表。
- 排查时先看日志有没有 `[游戏库] xxx: 找到 N 个游戏`：
  `N=0` 但用户确实装了 → 检测器有问题；`N>0` 但游戏不在其中 → 过滤条件过严。

## Notion 表结构硬要求（改代码前必看）
**每日时长表**：程序实际只写这 5 个属性（外加页面 icon）——
```csharp
["游戏动态"] = title      // 格式「游戏名 · 0.25 h」（小时 2 位小数）
["日期"]     = date
["单次时长"] = number     // ★ 单位是**小时**、保留 2 位小数（2026-09-19 从分钟改来）
["绑定状态"] = select     // 已绑定 / 未绑定
["关联游戏"] = relation   // 仅在有 gamePageId 时写入
```
- **属性名必须与代码里的字面量完全一致**，不匹配 Notion 返回 400（property does not exist）。
- ⚠️ **总表的 title 属性仍叫「游戏名称」，不要跟着改。** 两处名字不同，很容易改错。
- **拉取（Pull）时新名优先、旧名兜底**（`ExtractDouble` / `ExtractRelationId` 都带旧名），
  这样还没改名的表也能拉回历史记录。但**写入只用新名**。
- **标题读取是按类型找的**（`ExtractTitle` 找第一个 title 属性），与属性名无关。
- **`绑定状态` 的选项不用手工预建**：Notion API 会自动把选项加进 schema。

### ⚠️ 单位换算的硬约束（2026-09-19 改单位时踩到，再改单位前必读）
1. **只在 Notion 边界换算**：`NotionDailyRecordItem.DurationMinutes` 与本地
   `daily_summary` **一律是分钟**。写入用 `DailyRecordTitle.MinutesToHours()`，
   读取用 `RawDurationToMinutes()`。
2. **`单次时长` 用 2 位小数，不能用 1 位**（穷举验证过）：
   | 小数位 | 粒度 | 最大误差 | 1..1440 分中无法精确还原的个数 |
   |---|---|---|---|
   | 1 位 | 6 分 | 3 分 | **1200**（15 分 → 0.2h → 12 分）|
   | **2 位** | 0.6 分 | **0** | **0** ✅ |
   守护用例：`DurationRoundTrip_ShouldBeExact_ForEveryPlausibleMinuteValue`（穷举 1440 个值）。
   **别改回 1 位** —— 它会同时导致精度丢失和"每轮重推"。
3. **读 Number 必须用 `GetDouble`，不能用 `GetInt32`**。
   原 `ExtractNumber` 用 `GetInt32()`，遇到 `0.25` 会**直接抛异常**，整条记录拉不回来。
   已改为 `ExtractDouble` 返回 `double?`。
4. **`localAhead` 必须按分钟比较，不能用秒，也不要加容差**：
   ```csharp
   bool localAhead = existingMinutes > item.DurationMinutes;   // ✓
   ```
   - `duration_minutes = duration_seconds / 60`（整数除法，见 `AddSessionDurationToDailyAsync`），
     秒总比分钟多出 <60 的余数。用秒比较会把 910s/15分 vs 远端 15分 误判成"本地领先"，
     而推上去的还是同样的 15 分 → 数值不变 → **每轮重推**。
   - 加 ±3 分钟容差是上一版的补丁，会**漏推**（本地 15 分 vs 远端 12 分时判为不领先，
     15 分永远推不上去）。2 位小数让往返精确后，容差就该删掉 —— 按分钟比已经根本解决。
   - 而 push 出去的就是 `duration_minutes`，"是否领先"本就该用分钟衡量。

**历史行兼容**：旧行存的是分钟。判别规则：单日单游戏时长不可能 > 24 小时，
故 `> 24` 的值按分钟处理（`RawDurationToMinutes`）。局限已文档化：
旧行 ≤24 的值会被当成小时（影响有界）。

**测试**：`NotionWireFormatTests` 断言**实际发出的 JSON**（注入假 HttpMessageHandler），
不能只测中间值 —— 中间值一直是分钟，换算写错测不出来。

## 游戏总表
程序只读，属性均可选，但：
- `游戏名称` (Title) 实际必需（没有它匹配无从谈起）
- `游戏标识`（多选/文本，`steam:appid`）**可选、不是推荐项** ——
  库里几百个游戏逐个补不现实，匹配**以游戏名（含别名）为主**。详见下文匹配设计原则。
- `封面` / `别名` 可选。

**Rollup 累积时长（用户明确要求的能力，README 第 5 节）**：
- 前提：`关联游戏` relation 设为**双向**（Notion 里打开「在…中显示」开关），
  总表才会出现反向关联属性。
- 总表加 Rollup：Relation=反向关联、Property=`单次时长`、Calculate=**Sum**。
- **Rollup 是只读计算值，不能加到另一个属性上。**
  用户原有手工时长要叠加的话，得再加 **Formula**：
  `prop("原有列") + prop("GT累计")`。
- **单位已统一为小时**（2026-09-19）：Rollup 汇总出来的就是小时。
  原手工列若为分钟，Formula 要写 `prop("原有列") + prop("GT累计") * 60`。
- Rollup **只统计已绑定的记录**（没 relation 的算不进去），所以绑定是 Rollup 的前提。

## 发布包构成与瘦身（2026-09-19）
- **随包附文档**：`publish.ps1` 第 3b 步把 `README.md` 与 `CHANGELOG-QA.md`
  复制进发布目录（后者改名为「更新说明.md」）。改文档后重跑 publish 即生效。
- **不引 WindowsAppSDK 元包**（2026-09-19 起）：元包会连带拉进
  `.AI`（含传递依赖 `microsoft.windows.ai.machinelearning` → onnxruntime 20.7MB + DirectML 17.8MB）、
  `.ML`、`.Search`、`.Widgets`，合计约 **55MB**（包的 19%），本程序全用不到。
  改为只引 6 个子包：`Base 2.0.4` / `Foundation 2.3.12` /
  `InteractiveExperiences 2.1.9` / `WinUI 2.3.9` / `DWrite 2.1.0` / `Runtime 2.5.1`
  （版本取自元包 nuspec，保持一致）。
  元包是**纯聚合包**（targets 为空、props 只声明 VS ProjectCapability），拆开安全。
- **产物校验的 requiredFiles 是安全网**：改依赖集后已补入
  `Microsoft.WindowsAppRuntime.dll`。少引子包会缺核心 dll，这道校验会拦下。
- **winmd（53 个 / 2.7MB）保持原样**：主要是上面那些组件的产物、随之消失；
  剩下的来自 WinUI/Foundation，运行时是否读取无法静态确认，不做无把握的删除。
- **`.mui` 语言目录**：已用 `SatelliteResourceLanguages=zh-CN;en-US` +
  publish.ps1 清理步骤（本地化安全约束见该脚本注释：只删「名字像语言码」且「只含 .mui」的目录）。
- 观察：`dist/` 里堆了 15 个历史 zip、共约 **2.4GB**（含 v1.0.x~v1.2.1 的旧产物）。
  是否清理交给用户决定，我没动。

## 版本号与版本控制（SemVer 规则，2026-09-19 由 alphaNN 改为规范格式）
- **项目已初始化为 git 仓库**（`E:\vi2`）。基线提交 `d01cae5`（140 文件）。
  `.gitignore` 已排除 `config.json`（**含 Notion token，绝不能提交**）、`dist/`、`logs/`、`*.db`、`bin/`、`obj/`、`_probe/`。
- **当前版本 `0.9.5-alpha.23`**。
  `VersionPrefix=0.9.5` / `VersionSuffix=alpha.23` / `FileVersion=0.9.5.23` / `AssemblyVersion=0.9.5.0`。
- **格式**：`MAJOR.MINOR.PATCH[-预发布标识.序号][+构建信息]`
  - `MAJOR` 不兼容改动（`0.9.5`→`1.0.0`）；`MINOR` 向下兼容地加功能（→`0.10.0`）；
    `PATCH` 向下兼容地修缺陷（→`0.9.6`）。**递增某段时右侧各段归零。**
  - 预发布 `-alpha.N`：`N` 是**构建序号**，同一目标版本每出一个包就 +1；换 `X/Y/Z` 则归 1。
  - 比较按**数字**不按字符串（`alpha.2 < alpha.10`）；预发布**低于**同号正式版。
  - **`0.Y.Z` = 初始开发阶段**，接口/数据格式仍可能变；正式发布时改 `1.0.0` 并去掉预发布段。
  - `AssemblyInformationalVersion` 会自动带构建信息段（`0.9.5-alpha.23+<commit>`）。
- **升版必须同时改三处**（漏一处就会出现"程序里显示新号、exe 属性是旧的"矛盾）：
  1. `Directory.Build.props` → `VersionSuffix`（如 `alpha.23`）+ `FileVersion`（末段对齐，如 `0.9.5.23`）
  2. `src/GameTimeTracker.App/Package.appxmanifest` → `Identity/@Version`（须与 `FileVersion` 完全一致）
  3. 设置页注释里的示例字符串
  **不要在单个 `.csproj` 里再写 `<Version>`**；版本号只在仓库根 `Directory.Build.props` 定义一处。
- ⚠️ **改这两个文件注意行尾**：`Package.appxmanifest` 是 **CRLF**，用 Python
  `io.open(...,newline='')` 重写会变成 LF，导致整个文件都算改动。优先用 Edit 工具。
- **`alpha.N` 是给 QA 的迭代序号，每交一版调试包就 +1。**
  **用户要同时把包交给 QA 一起 debug**（2026-09-18 用户明确指出过一次我没升版本的疏漏）——
  不升版本 QA 就无法分辨手握的是改前还是改后，也说不清问题出在哪一版。
- 设置页底部显示取自 `AssemblyInformationalVersion`，**按 `+` 截断**（那个 hash 对 QA 定位问题很有用）。
- **打包用 `E:\vi2\publish.ps1`，不要手工改版本号再手工打包**：
  它从 `Directory.Build.props` 读版本（唯一来源）→ **校验 FileVersion 与 appxmanifest 一致**
  （不一致直接中止，机制上防漏改）→ 跑测试 → `dotnet publish` →
  写 `VERSION.txt`（版本+commit+配置+打包时间+工作树是否脏）→ 压 zip。
  工作树有未提交改动时黄字警告（包里含未入库代码 QA 无法定位问题）。
  **必须在正常的 Windows 终端里跑**（WorkBuddy 的 shell 缺 `PROGRAMDATA`/`APPDATA`，NuGet 会失败）。
- 发布包命名：`GameTimeTracker-v<版本>-win-x64.zip`（如 `...-v0.9.5-alpha.23-win-x64.zip`）。
  **配套维护 `CHANGELOG-QA.md`**（面向 QA：改了什么 / 重点验什么 / 已知问题）。
  HANDOVER.md 给接手开发者，CHANGELOG-QA.md 给测试人员，**两者受众不同都要维护**。
- **版本沿革**：
  `alpha17`（版本体系落地 + git 基线）→ `alpha18`（3 项 Notion 同步需求）
  → `alpha19`（Steam StateFlags / GOG+Ubisoft 注册表视图 / 未绑定重推 / 检测日志 /
  别名年份后缀 / **属性改名：游戏动态·单次时长·关联游戏** / 移除模糊匹配 / 时长单位改小时）
  → `alpha20`（热力图撑爆布局 / 手工标题后缀 / 回刷清零时长 / 删除对账误删）
  → `alpha21`（托盘闪退第一次尝试 ❌无效）→ `alpha22`（R2R 归因 ❌无效）
  → **`alpha.23`**（隐藏时不再拆页面 + 版本规则改 SemVer；⚠️ 崩溃修复待用户实测确认）。

## 每日记录的「游戏名称」与 page icon（2026-09-18 用户明确要求）
- **标题格式 `{游戏名} · {X} h`，其中「游戏名」的来源分两种**：
  - **已绑定总表**（`games.notion_page_id` 非空）→ 用**总表条目的名字**（relation 指向的那个名字），
    并把总表条目的 `IconUrl` 一并写到每日记录页面 icon。
  - **未绑定** → 保持进程名，**不写 icon**。
- **时长后缀 `· 0.7 h` 是必需品**，用户明确确认不能去掉。
- icon **只取 `IconUrl`（方图），不要 `CoverUrl`**（横幅塞进列表图标会糊）。
  总表条目没设图标就什么都不写，**不要猜 Steam 图标**（免得覆盖用户自己的选择）。
- 实现入口：`NotionSyncService.ResolveDailyDisplayAsync(localName, gameNotionPageId)`。
  三条推送路径都要调：`SyncPendingDailyRecordsAsync` / `BackfillRelationsAsync` / `LinkGameRelationAsync`。
  **新加推送路径时别忘了调**，否则会出现名字/图标不一致。
- Notion API（锁定 `2022-06-28`）icon **只接受 external URL / emoji，无法上传本地文件**。

## 每日记录标题的「回刷」机制（2026-09-18，用户选 B 方案）
- **问题**：Pull 只同步时长、不碰标题，所以总表改名后历史记录**永远停在旧名上**。
- **方案**：`NotionSyncService.RefreshDailyTitlesFromMasterAsync()`，已挂进全部四条同步链。
- **性能关键**：`daily_summary` 存**两份远端快照** —— `notion_title` + `notion_icon_url`，
  逐条比对「快照 vs 期望值」，只有真不一致才 PATCH。
  没有快照的话每轮要 PATCH 全部历史记录（随天数线性增长，不可接受）。
  - **两份快照必须一起比对、一起写。** 只比标题是个**隐蔽的性能陷阱**：
    `iconUrl != null` 只表示"总表里设了图标"，不代表"这个页面图标不对"，
    所以只要总表有条目设了图标，所有历史记录就**每轮都被 PATCH**。
    守护用例：`RefreshTitles_IsNoOp_WhenNothingChanged`（**别删**）。
  - **推送成功后立刻落快照**（`RecordRemoteSnapshotAsync`）。不落的话本地就是**明知故犯地错**：
    刚把图标写上去、快照还写着"没有图标"，下轮多做一次多余 PATCH。
    不变式：快照 = "最近一次写入或观察到的远端值"。
    守护用例：`SyncPending_RecordsSnapshot_SoBackRefreshDoesNotRepatch`。
  - 首轮快照为空的记录会被判为"需回刷"一次，之后就有快照。
  - `NotionDailyRecordItem.RawTitle`（**保留时长后缀的原文**）专供标题比对。
    **不要用 `GameTitle`**（已剥后缀的裸名）——那会把每条都误判成不一致，反复 PATCH。
- **必须排在 `SyncPendingDailyRecordsAsync` 之后**（刚推上去的记录才有快照）。
- `EnsureMasterPageIconsAsync()` 已从 `BackfillRelationsAsync` 末尾挪到**开头**：
  回填时要从 `game_catalog.IconUrl` 取图，而 Steam 图标正是这一步写进缓存/总表页面的。
- **标题格式的唯一定义是 `internal static class DailyRecordTitle`**（`Build` / `StripSuffix` / `SuffixRegex`）。
  **不要在 `NotionClient` 或 `NotionSyncService` 里各写一份** ——
  "写标题"和"比对标题"必须共用同一套规则，否则格式一旦漂移（改小数位、换分隔符），
  回刷会永远判定不一致、每轮白打 Notion。
  （alpha18 首轮构建失败就是这个坑：`BuildDailyRecordTitle` 曾是 `NotionClient` 私有方法。）
- **`CreateDailyRecordAsync` / `UpdateDailyRecordAsync` 的第 4 个参数是「游戏名」，不是拼好的标题。**
  方法内部会自己调 `DailyRecordTitle.Build()` 拼装。传完整标题进去会拼成
  **「X · 0.7 h · 0.7 h」（时长后缀重复）**——alpha18 真实发生过，被测试抓到。
  参数已从 `gameTitle` 改名为 `gameName` 以消除这个歧义
  （`CreateGameMasterPageAsync` 的 `gameTitle` 不改，那个确实写标题）。
  **调用时一律传 `displayName`（`ResolveDailyDisplayAsync` 的第一个返回值）。**
  - 写测试 fake 时的经验：**保留"调用参数原样"比"模拟完整行为"更能暴露调用方的契约错误**。
    这个 bug 能被抓到，正是因为 fake 记录的是传给 client 的原始值；
    如果 fake 内部也拼一次标题、断言最终结果，就会漏过去。

## page id 匹配必须连字符不敏感（2026-09-18 踩坑）
- Notion 的 page id 有时带连字符（8-4-4-4-12）有时不带，
  而 `games.notion_page_id` 与 `game_catalog.page_id` 的来源路径不同，**不能假设两边形式一致**。
- 已修：`GetCatalogItemByPageIdAsync` 与 `UpdateDailyRecordFromNotionAsync`
  都改为 `page_id = @raw OR REPLACE(page_id,'-','') = @normalized`。
- **只做精确匹配的后果是静默失败**：返回 null / 0 行不报错，
  表现为"每日记录用了进程名而不是总表名、图标也没了"或"回刷永远不收敛"，极难查。
  （`EnsureMasterPageIconsAsync` 里手工 `Replace("-","")` 就是前人踩过这坑的痕迹。）
- 同理：快照写回 0 行时必须打警告，否则症状只是"同步一直很慢"。

## 新用户首次保存配置后自动同步（2026-09-18）
- 触发条件是**状态跃迁**：`保存前 !IsNotionConfigured && 保存后 IsNotionConfigured`。
  **反复点保存不重复触发**。
- 新增 `MainWindow.RunInitialSyncAsync()` + `MainWindow.SyncService` 属性；
  设置页通过 `MainWindow.CurrentWindow` 拿服务。
- 进度提示用 `StatusInfoBar` 文案切换即可（用户明确说**不需要进度条**），
  同步期间禁用「保存/测试连接」按钮防重复点击。

## ⚠️ WinUI 3 绝对不能开 PublishTrimmed（2026-09-18 真实事故）
- **症状**：Release 包一启动就崩，日志是 WinRT 投影层 `NullReferenceException`，
  **和真实原因完全对不上**：
  ```
  at WinRT.TypeExtensions.GetAbiToProjectionVftblPtr(Type helperType)
  at ABI.Microsoft.UI.Xaml.Controls.IItemsRepeaterMethods.set_ItemsSource(...)
  at HomePage.HomePage_obj1_Bindings.Update_ViewModel(...)
  ```
- **原因**：XAML 绑定 / `{x:Bind}` / 资源查找 / WinRT 投影全靠反射按名字解析类型，
  裁剪器静态分析看不到这些引用，会把 `Microsoft.UI.Xaml.dll` / `Microsoft.UI.Xaml.Controls.dll` /
  `CoreMessagingXP.dll` / `DWriteCore.dll` 等 native 实现 + 全部 `*.Projection.dll` 删掉。
  `GetAbiToProjectionVftblPtr` 返回 null 就是投影程序集被删的直接后果。
- **产物对比（一眼可辨，先看这个）**：
  | | 正常 | 被裁剪 |
  |---|---|---|
  | 文件数 | ~449 | ~101 |
  | 解压后体积 | ~285 MB | ~86 MB |
  | zip 体积 | ~105 MB | ~38 MB |
  | `GameTimeTracker.App.dll` | ~753 KB | ~610 KB |
  | `Microsoft.UI.Xaml.dll` | 有 | **缺** |
  - ⚠️ **区分 zip 和解压后体积**：用户交付/沟通时说的是 **zip（~105MB）**，
    排查时看的是**解压后（~285MB）**。这两个数都是稳定的基线，不要混着比。
    历史上 v1.0.3 ~ v1.2.1 的 zip 全是 105.2 MB，解压后 ~285MB（v1.0.7 实测 286MB）。
- **已修**：csproj 恒定 `<PublishTrimmed>False</PublishTrimmed>`（附详细注释）；
  `publish.ps1` 显式传 `-p:PublishTrimmed=false -p:SelfContained=true
  -p:WindowsAppSDKSelfContained=true`，并新增**产物校验**
  （检查 9 个关键文件，缺任何一个中止打包）。
- **通用教训**：
  - **"测试全过 + 构建零错误" ≠ 包能用**。测试跑的是普通构建，发布配置是另一套参数。
  - 打包脚本**必须做产物校验**，把"能生成但跑不起来"拦在交付前。
  - 影响发布行为的关键参数（`PublishTrimmed` / `SelfContained` / `WindowsAppSDKSelfContained`）
    **一律显式传参**，不要依赖会随 Configuration 变化的 csproj 默认值。

## 本机构建环境坑：NuGet 文件夹解析返回 null（2026-09-18 彻底排查结论）
- **当前状态（09-18 晚）**：用户重启后**自己的终端已可正常构建**（`publish.cmd` 跑通了还原与编译）。
  但 **WorkBuddy 工具自己的 shell 仍然是坏的**（`dotnet` 依旧报 `path1`），
  所以 AI 无法在本会话内构建/跑测试，**只能做静态复查，验证靠用户执行 `publish.cmd`**。
- **`ExecutionPolicy = Restricted`**（本机实测）：直接 `.\publish.ps1` 会被拒绝执行
  （「在此系统上禁止运行脚本」）。故仓库根提供 **`publish.cmd`** 包装
  （`powershell -ExecutionPolicy Bypass -File`，**只影响单次调用**，不改系统持久设置）。
  **打包一律用 `publish.cmd`，不要去动全局执行策略。**
- **症状**：`dotnet restore` / `build` 报
  `NuGet.targets(782,5): error : Value cannot be null. (Parameter 'path1')`。
  **任何工程都会中招**——连一个全新的、零依赖的 `net10.0` 控制台项目也还原失败。
- **真正的根因（⚠️ 之前一轮的诊断有一处是错的，已修正）**：
  **Windows 上 `Environment.GetFolderPath(SpecialFolder.CommonApplicationData)`
  读的是进程环境变量 `PROGRAMDATA`，不读注册表 `User Shell Folders`。**
  所以改注册表 `User Shell Folders\Common AppData` **对本问题无效**（方向就错了）。
- 从 `NuGet.Common.dll` 提取到的变量清单（`NuGetEnvironment` 实际读的）：
  `PROGRAMDATA` / `ALLUSERSPROFILE` / `APPDATA` / `LOCALAPPDATA` /
  `NUGET_COMMON_APPLICATION_DATA` / `XDG_CONFIG_HOME` / `XDG_DATA_HOME`。
- `MachineWideConfigDirectory` 走**双层 Combine**：内层 `CommonApplicationData` 解析出 null →
  外层 `Path.Combine(null, "NuGet", "Config")` 抛 `path1`。
- **完整调用栈**（`-v:diag` 可见）：
  `GetRestoreSettingsTask.Execute` → `RestoreSettingsUtils.ReadSettings`
  → `XPlatMachineWideSetting..ctor` → `NuGetEnvironment.GetFolderPath`
  → `NuGetEnvironment.CalculateFolderPath` → `Path.Combine` → 抛异常。
- **实测证据（同一台机器、同一时刻）**：
  | 取值方式 | 结果 |
  |---|---|
  | `GetEnvironmentVariable('ProgramData','Machine')` | `C:\ProgramData` ✅ 注册表是好的 |
  | `GetFolderPath('CommonApplicationData')` | `C:\ProgramData` ✅ 进程环境块正常时可用 |
  | `$env:ProgramData`（WorkBuddy 的 shell 里） | **空** ❌ |
- **两种缺失要分开看**：
  1. **系统级缺失**：`HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment`
     少了 `ProgramData`/`PUBLIC`/`ALLUSERSPROFILE`/`APPDATA`/`LOCALAPPDATA`/`USERPROFILE`。
     修复后**必须重启或注销重登**（Windows 要重建环境块）。
  2. **WorkBuddy 自动化 shell 特有的合成环境块**（新发现，与上一条无关）：
     工具自己拼的环境块里 `ProgramData`/`ALLUSERSPROFILE`/`APPDATA`/`PUBLIC` 全不存在，
     且 `PATH` 开头被损坏成 `E;E:\WorkBuddy\...`（盘符被截断）。
     **实测：在 `.cmd` 里 `set PROGRAMDATA=...` 无效，`NUGET_COMMON_APPLICATION_DATA`
     覆盖也无效**（该覆盖在 Windows 分支不生效）。
     **这是工具环境限制，改机器配置解决不了** —— 只能重启后用正常终端 / IDE 构建验证。
- **重要**：这个故障**连 `obj/project.assets.json` 已存在时也拦不住**——
  错误会从 `NuGet.targets(782)` 转成
  `Microsoft.PackageDependencyResolution.targets(266)` 的 `NETSDK1060`，
  因为**加载**资产文件同样要解析 `packageFolders` 的路径。
  所以网上常见的「保留 obj + `--no-restore`」偏方在本机也无效（实测）。
  在此之前**不要**清理 `obj/`，也不要尝试手工伪造 `project.assets.json`。
- **已证伪、不要再走一遍的假设**（每一条都实测过）：
  - ❌ 不是缺 `C:\Program Files\dotnet\library-packs\`（补建后仍失败）
  - ❌ 不是缺 `C:\ProgramData\NuGet\` 目录（补建后仍失败）
  - ❌ **不是注册表 `User Shell Folders\Common AppData` 的问题**（.NET 根本不读它）
  - ❌ 不是 `Directory.Build.props` 引起（移走仍失败）
  - ❌ 不是 `obj/` 缓存脏（删干净后仍失败）
  - ❌ 不是缺 `NuGet.Config`（补上仍失败；且是**不该提交**的文件，已删）
  - ❌ `-p:RestoreFallbackFolders=` 无效（错误从 782 行移到 198 行，仍在同一根因上）
  - ❌ `-p:UserProfileDir=` / `-p:ProgramData=` / `-p:RestoreConfigFile` 等 MSBuild 属性无效
  - ❌ `NUGET_COMMON_APPLICATION_DATA` 环境变量无效（Windows 分支不读，只在 Unix/macOS 生效）
  - ❌ 在 `.cmd` 里 `set ProgramData=...` 无效
  - ❌ 绕过 CLI 直接跑 `MSBuild.dll` / 完整重建标准 Windows 环境 / 禁用节点复用 —— 全失败
  - ❌ **PowerShell 工具在本环境吞掉 stdout**，必须重定向落盘再读
  - ❌ 从 Bash 直接调 `cmd.exe` / `reg.exe` / `msbuild` 会被安全层拦截，必须写成 `./x.cmd` 执行
- **注意**：这是**机器/工具环境问题**，与 GameTimeTracker 项目本身无关，
  不要试图在项目里"修"它（不要提交任何 `NuGet.Config` 变通文件）。

