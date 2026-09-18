# 项目交接文档 (HANDOFF.md)

## 1. 项目目标与硬约束

### 项目目标
构建一个运行于 Windows 平台的轻量级游戏时间统计与管理桌面应用（GameTime Tracker），基于 WinUI 3 (Windows App SDK unpackaged) + .NET 10 + SQLite + Notion API。
具备如下核心能力：
1. 后台自动扫描与检测运行中的游戏进程（Steam / Epic / Xbox Game Pass / GOG / EA / Ubisoft / WeGame 及手动添加的无平台游戏）。
2. 本地 SQLite 精确记录游戏会话时长、跨日切分计算、每日汇总数据与 52 周（1年）贡献热力图生成。
3. 双向/单向同步数据到 Notion 数据库（游戏总表 `Game Database` 与每日记录表 `Daily Database`），维持 Relation 关联合法性。
4. 现代流畅的 WinUI 3 UI 界面：自适应响应式布局、深色/浅色主题无缝切换、游戏图标与背景图动态抽取、秒级流畅读秒与里程碑段位体系。

### 硬约束
1. **进程与命令执行**：
   - 执行 PowerShell 命令时，只要退出码为 0 或 1 即判定结束，不可反复重复执行相同构建/测试命令。
   - 文件扫描单次最多读取 10 个文件，禁止无限递归全盘扫描。
2. **应用类型限制**：
   - 本项目配置为 `WindowsPackageType=None`（Unpackaged WinUI 3 桌面应用），不可使用 MSIX 专有的沙盒 API。
   - 在 XAML 中，`file:///` URI 无法直接被 `BitmapImage(Uri)` 安全解码加载，必须通过流方式（如 `InMemoryRandomAccessStream` + `SetSource`）加载本地图片文件。
3. **UI 线程调度约束**：
   - 禁止在 `DispatcherQueue.TryEnqueue` 内部调用 `.GetAwaiter().GetResult()` 或 `.Result` 等任何同步阻塞异步任务的代码，避免在 WinUI 消息泵引发 UI 线程死锁。
4. **配色与热力图规范（深色模式）**：
   - 背景色：`#1A1B2E`
   - 主文字色：`#CCCCFF`
   - 热力图色阶必须严格执行：
     - Heat0（无记录）：填充 `#1E1E2E`，边框 `#2A2A45`（粗细 1px，圆角 3px）
     - Heat1（<30min）：填充 `#1E2040`，无边框
     - Heat2（30~60min）：填充 `#3333AA`，无边框
     - Heat3（1~2h）：填充 `#6666CC`，无边框
     - Heat4（2~4h）：填充 `#9999DD`，无边框
     - Heat5（>4h）：填充 `#CCCCFF`，无边框
     - 今日聚焦（Today）：边框粗细 1.5px，高亮颜色 `#4F8FFF`

---

## 2. 当前状态：已完成 / 进行中 / 未开始

### 已完成
1. **底层核心与数据库体系**：
   - SQLite 数据库初始化、版本自动迁移（已包含 `games.cover_url`、`game_catalog.cover_url`）。
   - 会话记录切分管理器 `GameSessionManager`：支持跨午夜（00:00:00）自动切分前后日记录。
   - 数据聚合器 `DailyAggregator`：已实现 52 周（364天）热力图矩阵计算、连续游玩天数（Streak）、较昨日/较上周差值百分比计算。
2. **平台检测器集合**：
   - `SteamDetector`、`EpicDetector`、`GogDetector`、`UbisoftDetector`、`EaDetector`、`WeGameDetector`、`XboxDetector`。
   - `GameLibraryManager` 聚合检测与手动添加（`AddManualGame`）。
3. **Notion 双表同步与关联维护**：
   - `NotionSyncService` 支持游戏总表拉取、每日时长记录同步、每日记录向游戏总表的 Relation 双向回填绑定。
4. **UI 主界面与布局规范**：
   - 响应式双栏布局（左侧 Hero 卡片 + 指标卡 + 热力图 + 最近记录；右侧今日游戏 Top 3 + 同步状态 + 305px 固定高度里程碑卡片）。
   - 侧边栏菜单悬停与激活态修正，待处理红点数字垂直水平居中。
   - 历史记录与主页表格游戏超长名称字符截断（`CharacterEllipsis`）修复。
   - 热力图右上角标题修改为“游戏时长记录 X 天”（无背景色）。
   - 深色模式完整调色板生效，设置界面精简为“切换专属深色星空主题”。
5. **本地图片加载机制修复**：
   - `StringToImageSourceConverter` 已升级为支持通过内存流 `InMemoryRandomAccessStream` + `SetSource` 解码本地图片，彻底解决 WinUI 3 unpackaged 无法读取 `file:///` 本地图片的问题。

### 进行中
1. **“正在游玩”区块（Hero Card）在多游戏切换时的封面与背景图展示**：
   - 根因已定位（WinUI 3 unpackaged 本地图片流加载问题 + 游戏切换时 session 取值与背景图 fallback 机制）。
   - `Converters.cs` 已修，需确保 `CoverCacheService.cs`、`ViewModels.cs`、`XboxDetector.cs`（XboxGames 下 Content 目录识别）协同生效并由用户在实际游戏中启动多款游戏实机验证。

### 未开始
1. 开机自启服务在极低配置机器上的内存与 CPU 长期驻留压测（目前为每 5 秒扫描一次）。
2. 应用安装包单文件打包发布与签名（目前以 Unpackaged 调试模式运行）。

---

## 3. 关键决策及理由

| 决策点 | 采用方案 | 理由 |
| :--- | :--- | :--- |
| **本地图片转换器** | 使用 `InMemoryRandomAccessStream` + `bmp.SetSource` 替代 `new BitmapImage(Uri)` | WinUI 3 Unpackaged 模式下直接给 `BitmapImage` 传入 `file:///` 本地磁盘路径会被安全上下文拦截并静默失败，只有网络链接（`http/https`）或流式加载才能成功渲染。 |
| **异步数据与 UI 隔离** | 所有数据库查询、文件 IO、Notion Catalog 匹配均在 `_dispatcherQueue.TryEnqueue` 外 `await` 执行 | 避免在 UI 消息循环内调用 `.GetAwaiter().GetResult()` 引发典型 sync-over-async 线程死锁，确保 UI 读秒与渲染高响应。 |
| **活动会话选择策略** | `_sessionManager.GetActiveSessions().OrderByDescending(s => s.StartTime).FirstOrDefault()` | 原 `FirstOrDefault()` 依赖哈希字典迭代顺序，当关闭一款游戏紧接着打开另一款游戏时，可能拿到旧会话，按启动时间倒序能保证聚焦最新会话。 |
| **全景背景图回退** | `Notion Cover -> 本地 SplashScreenImage -> 本地高分辨率图标 Cover -> Null` | 部分游戏在 Notion 表中没有配置网络封面（如 Xbox 或本地游戏），回退到本地游戏目录的 `SplashScreenImage.png` 或图标能确保所有游戏启动时 Hero 卡片背景均具备氛围感。 |
| **热力图渲染架构** | 7 行 x 52 列动态 XAML 网格，末尾自动滚动对齐今天 | 符合 GitHub 贡献图习惯，避免单月按钮切换的断层感，同时满足用户从左向右查阅全年游戏活跃度的需求。 |

---

## 4. 目录与关键文件：路径 + 作用

### 核心库 (src/GameTimeTracker.Core)
- [src/GameTimeTracker.Core/Models/DomainModels.cs](file:///E:/gemini/vi/src/GameTimeTracker.Core/Models/DomainModels.cs)：核心实体定义（`GameRecord`、`GameSession`、`DailySummary`、`GameCatalogItem`、`ActivityHeatmapResult` 等）。
- [src/GameTimeTracker.Core/Interfaces/Interfaces.cs](file:///E:/gemini/vi/src/GameTimeTracker.Core/Interfaces/Interfaces.cs)：仓储层、同步服务、平台检测器接口抽象。
- [src/GameTimeTracker.Core/Services/DailyAggregator.cs](file:///E:/gemini/vi/src/GameTimeTracker.Core/Services/DailyAggregator.cs)：热力图色阶划分、Streak 计算、今日数据统计纯逻辑。
- [src/GameTimeTracker.Core/Services/GameSessionManager.cs](file:///E:/gemini/vi/src/GameTimeTracker.Core/Services/GameSessionManager.cs)：游戏会话生命周期维护（启动、心跳、结束、跨午夜时长拆分）。

### 基础设施层 (src/GameTimeTracker.Infrastructure)
- [src/GameTimeTracker.Infrastructure/Database/SqliteRepository.cs](file:///E:/gemini/vi/src/GameTimeTracker.Infrastructure/Database/SqliteRepository.cs)：SQLite 数据库交互实现与 DDL 表迁移。
- [src/GameTimeTracker.Infrastructure/Notion/NotionServices.cs](file:///E:/gemini/vi/src/GameTimeTracker.Infrastructure/Notion/NotionServices.cs)：Notion API 通信，包括属性解析、封面提取、双表关系建立。
- [src/GameTimeTracker.Infrastructure/Covers/CoverCacheService.cs](file:///E:/gemini/vi/src/GameTimeTracker.Infrastructure/Covers/CoverCacheService.cs)：可执行文件桌面图标提取、Steam CDN 封面下载、本地封面缓存与全景背景查找。
- [src/GameTimeTracker.Infrastructure/Platforms/GameLibraryManager.cs](file:///E:/gemini/vi/src/GameTimeTracker.Infrastructure/Platforms/GameLibraryManager.cs)：多平台聚合检索与内存快速匹配。
- [src/GameTimeTracker.Infrastructure/Platforms/XboxDetector.cs](file:///E:/gemini/vi/src/GameTimeTracker.Infrastructure/Platforms/XboxDetector.cs)：Xbox Game Pass 及 WindowsApps 游戏检测。

### 表现层应用 (src/GameTimeTracker.App)
- [src/GameTimeTracker.App/App.xaml](file:///E:/gemini/vi/src/GameTimeTracker.App/App.xaml)：全局深浅主题资源字典（热力图 6 级色阶、强调色、卡片背景色等）。
- [src/GameTimeTracker.App/MainWindow.xaml.cs](file:///E:/gemini/vi/src/GameTimeTracker.App/MainWindow.xaml.cs)：主窗口框架、托盘图标服务、5 秒进程监控主循环 `MonitorLoopAsync`。
- [src/GameTimeTracker.App/ViewModels/ViewModels.cs](file:///E:/gemini/vi/src/GameTimeTracker.App/ViewModels/ViewModels.cs)：`HomeViewModel`（Hero 卡片状态、数据异步刷新、1秒平滑读秒计时器）。
- [src/GameTimeTracker.App/Converters/Converters.cs](file:///E:/gemini/vi/src/GameTimeTracker.App/Converters/Converters.cs)：`StringToImageSourceConverter`（支持 Web URL 与本地图片流解码）。
- [src/GameTimeTracker.App/Controls/HeatmapControl.xaml](file:///E:/gemini/vi/src/GameTimeTracker.App/Controls/HeatmapControl.xaml)：热力图控件 XAML 模板及图例。
- [src/GameTimeTracker.App/Controls/HeatmapControl.xaml.cs](file:///E:/gemini/vi/src/GameTimeTracker.App/Controls/HeatmapControl.xaml.cs)：热力图矩阵动态生成与着色控制。
- [src/GameTimeTracker.App/Views/HomePage.xaml](file:///E:/gemini/vi/src/GameTimeTracker.App/Views/HomePage.xaml)：主仪表盘视图（正在游玩卡片、统计卡、热力图、今日游戏、里程碑）。

---

## 5. 公共接口、数据格式、不能改的东西

### 1. 数据库关键表结构 (不可随意更改字段语义)
- `games`：
  - `id` (INTEGER PK)
  - `platform` (TEXT: "steam" / "xbox" / "epic" / "manual" 等)
  - `platform_id` (TEXT)
  - `name` (TEXT)
  - `executable` (TEXT)
  - `executable_path` (TEXT)
  - `notion_page_id` (TEXT)
  - `cover_url` (TEXT)
  - `status` (TEXT: "active" / "ignored" / "pending")
- `daily_summaries`：
  - `date` (TEXT: "yyyy-MM-dd")
  - `game_id` (INTEGER)
  - `duration_minutes` (INTEGER)
  - `notion_page_id` (TEXT)
  - `sync_status` (TEXT: "pending" / "synced" / "failed")

### 2. 公共接口契约
- `IDatabaseRepository.GetCatalogItemByPageIdAsync(string pageId)` -> 返回 `GameCatalogItem?`。
- `DailyAggregator.CalculateHeatmapLevel(int minutes, int maxMinutes)` -> 返回 `0..5` 整数。
- `CoverCacheService.GetCoverPath(string platform, string platformId)` -> 返回本地封面绝对路径字符串。

### 3. 不能擅自改动的业务逻辑
1. **进程检测降级机制**：主流平台扫描失败时，必须先比对 SQLite 本地库路径（`GetGameByPathOrExeAsync`），不得轻易将已有游戏判定为未知。
2. **今日游戏展示上限**：右侧栏“今日游戏”必须仅展示前 3 项（`Take(3)`），将垂直空间留给 305px 高度的连续游玩里程碑卡片。
3. **未同步/已同步颜色规范**：已同步为绿色，未同步为橙色，不可混淆。

---

## 6. 已尝试但失败的方案

1. **直接将本地文件路径转换成 `file:///` URI 传入 `new BitmapImage(uri)`**：
   - *现象*：网络图片（如 Squeakross 的 Steam URL）能正常显示，但所有本地文件（如从可执行文件提取的图标、保存在 cache 目录的图片）一律显示空白。
   - *失败原因*：WinUI 3 Unpackaged 模式沙盒策略限制，XAML 内部无法通过 `file:///` 协议加载不受信任的任意绝对路径。
   - *正确解法*：使用 `File.OpenRead` 读入流，通过 `InMemoryRandomAccessStream` 转换为 `IRandomAccessStream`，调用 `bmp.SetSource(memStream)` 加载。
2. **在 `_dispatcherQueue.TryEnqueue` 内部调用 `.GetAwaiter().GetResult()` 查询数据库**：
   - *现象*：程序运行一段时间或触发数据刷新时 UI 偶尔无响应卡死，后续属性通知全部失效。
   - *失败原因*：WinUI 3 UI 调度线程同步阻塞等待 SQLite 异步 Task，引发典型 SynchronizationContext 线程死锁。
   - *正确解法*：在进入 `TryEnqueue` 之前前置执行所有 `await` 异步数据获取，进入 UI 线程后只执行同步属性设值。
3. **在横向 `StackPanel` 中直接给 `TextBlock` 设置 `TextTrimming="CharacterEllipsis"`**：
   - *现象*：游戏名过长时直接向右无限拉伸，文字遮盖后面的列，省略号未生效。
   - *失败原因*：横向 `StackPanel` 为子元素分配无限宽度，使得 `TextBlock` 永远无法测量出边界。
   - *正确解法*：使用两列 `Grid`（`Auto` + `*`）包裹，约束 `TextBlock` 在列宽内渲染截断。

---

## 7. 下一步任务，按优先级排序

### 优先级 P0（必须立即完成验证）
1. **多款游戏无缝切换验证**：
   - 启动本地非 Steam 游戏（如 `Heroes of Might and Magic- Olden Era` 或任意手动添加游戏），验证 Hero 卡片图标是否正常加载显示。
   - 验证关闭游戏后再启动另一款游戏时，Hero 卡片是否立即切换为最新启动的游戏。
2. **全景背景图回退生效检查**：
   - 当启动未在 Notion 中上传 cover 的本地游戏时，确认背景是否自动平滑回退到该游戏自带的 `SplashScreenImage.png` 或高分辨率图标。

### 优先级 P1（体验与健壮性优化）
1. **Xbox 平台 `Content` 目录下的 `MicrosoftGame.config` 识别优化**：
   - 在 `XboxDetector.cs` 中增加对 `Path.Combine(gameDir, "Content", "MicrosoftGame.config")` 的直接识别，使 Game Pass PC 游戏即使不在根目录也能解析出精准游戏名称与主执行文件。
2. **Notion 双向关系同步状态实时联动**：
   - 当用户在设置页面点击“立即同步”或后台 15 分钟定时同步完成后，右侧栏的“同步状态”文本与时间戳实时刷新。

---

## 8. 测试 / 运行 / 构建命令

所有命令必须在项目根目录 `E:\gemini\vi` 执行（PowerShell 环境）：

```powershell
# 1. 还原与全量编译项目
& "C:\Program Files\dotnet\dotnet.exe" build GameTimeTracker.slnx

# 2. 运行自动化单元测试套件（验证纯逻辑与聚合计算）
& "C:\Program Files\dotnet\dotnet.exe" test GameTimeTracker.slnx --no-build

# 3. 本地启动应用进行实机测试
& "src\GameTimeTracker.App\bin\Debug\net10.0-windows10.0.26100.0\win-x64\GameTimeTracker.App.exe"
```

---

## 9. 待确认问题

1. **Notion 封面 URL 访问权限**：
   - 用户 Notion 游戏总表中的 cover 如果是用户直接上传至 Notion 的文件（形如 `https://prod-files-secure.s3.us-west-2.amazonaws.com/...`），其 AWS S3 预签名链接通常存在 1 小时过期机制。
   - *待确认*：是否需要在 `RefreshGameCatalogCacheAsync` 时将该图片自动下载并固化到本地 `cache/covers/` 目录以防外链失效。
2. **同名多平台进程去重**：
   - 如果用户同时启动了启动器进程与游戏本体进程（父子进程），当前基于主窗口标题与进程扫描过滤，需确认特定游戏是否存在双会话问题。

---

## 10. 最近关键 diff 或必要代码片段

### 片段 1: `StringToImageSourceConverter` 本地图片与网络图片兼容加载
文件：[src/GameTimeTracker.App/Converters/Converters.cs](file:///E:/gemini/vi/src/GameTimeTracker.App/Converters/Converters.cs)
```csharp
public class StringToImageSourceConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string path && !string.IsNullOrWhiteSpace(path))
        {
            try
            {
                // 1. 网络 URL 或应用资源
                if (Uri.TryCreate(path, UriKind.Absolute, out var uri))
                {
                    if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == "ms-appx")
                    {
                        return new BitmapImage(uri);
                    }
                }

                // 2. 本地绝对路径文件（通过内存流加载，避免 WinUI 3 Unpackaged 安全限制与文件独占锁定）
                if (File.Exists(path))
                {
                    var bmp = new BitmapImage();
                    using var fileStream = File.OpenRead(path);
                    var memStream = new InMemoryRandomAccessStream();
                    using (var outStream = memStream.AsStreamForWrite())
                    {
                        fileStream.CopyTo(outStream);
                    }
                    memStream.Seek(0);
                    bmp.SetSource(memStream);
                    return bmp;
                }
            }
            catch
            {
                return null;
            }
        }
        return null;
    }
}
```

### 片段 2: Hero 卡片多源背景与封面全异步前置检索
文件：[src/GameTimeTracker.App/ViewModels/ViewModels.cs](file:///E:/gemini/vi/src/GameTimeTracker.App/ViewModels/ViewModels.cs)
```csharp
// 保证拿到最新启动的活动会话
var active = _sessionManager.GetActiveSessions().OrderByDescending(s => s.StartTime).FirstOrDefault();
GameRecord? activeGame = null;
string? activeCover = null;
string? activeHeroBg = null;

if (active != null)
{
    activeGame = await _repo.GetGameByIdAsync(active.GameId);
    if (activeGame != null)
    {
        if (!string.IsNullOrEmpty(activeGame.ExecutablePath) && File.Exists(activeGame.ExecutablePath))
        {
            try { _coverCache.ExtractAndSaveExecutableIcon(activeGame.ExecutablePath, activeGame.Platform, activeGame.PlatformId); } catch { }
        }

        var localCover = _coverCache.GetCoverPath(activeGame.Platform, activeGame.PlatformId);
        if (File.Exists(localCover) && new FileInfo(localCover).Length > 0)
        {
            activeCover = localCover;
        }

        // 优先级 1: Notion 绑定页面的 Catalog Cover
        if (!string.IsNullOrEmpty(activeGame.NotionPageId))
        {
            var catItem = await _repo.GetCatalogItemByPageIdAsync(activeGame.NotionPageId);
            if (catItem != null && !string.IsNullOrEmpty(catItem.CoverUrl))
            {
                activeHeroBg = catItem.CoverUrl;
                activeCover ??= catItem.CoverUrl;
            }
        }

        // 优先级 2: Catalog 名称匹配
        if (activeHeroBg == null)
        {
            var catalogItems = await _repo.GetCatalogItemsAsync();
            var matched = catalogItems.FirstOrDefault(c => string.Equals(c.Name, activeGame.Name, StringComparison.OrdinalIgnoreCase));
            if (matched != null && !string.IsNullOrEmpty(matched.CoverUrl))
            {
                activeHeroBg = matched.CoverUrl;
                activeCover ??= matched.CoverUrl;
            }
        }

        // 优先级 3: 游戏自带全景背景或高分辨率图标回退
        activeHeroBg ??= activeCover;
    }
}

// 调度进入 UI 线程：纯设值，零阻塞
_dispatcherQueue.TryEnqueue(() =>
{
    CurrentGameCoverPath = activeCover;
    CurrentGameHeroBackgroundUrl = activeHeroBg;
    // ...
});
```

### 片段 3: 深色模式热力图精确色阶字典
文件：[src/GameTimeTracker.App/App.xaml](file:///E:/gemini/vi/src/GameTimeTracker.App/App.xaml)
```xml
<!-- Dark Theme Resources -->
<ResourceDictionary x:Key="Dark">
    <SolidColorBrush x:Key="WindowBackgroundBrush" Color="#1A1B2E" />
    <SolidColorBrush x:Key="HeatmapLevel0Brush" Color="#1E1E2E" />
    <SolidColorBrush x:Key="HeatmapLevel0BorderBrush" Color="#2A2A45" />
    <SolidColorBrush x:Key="HeatmapLevel1Brush" Color="#1E2040" />
    <SolidColorBrush x:Key="HeatmapLevel2Brush" Color="#3333AA" />
    <SolidColorBrush x:Key="HeatmapLevel3Brush" Color="#6666CC" />
    <SolidColorBrush x:Key="HeatmapLevel4Brush" Color="#9999DD" />
    <SolidColorBrush x:Key="HeatmapLevel5Brush" Color="#CCCCFF" />
    <!-- ... -->
</ResourceDictionary>
```
