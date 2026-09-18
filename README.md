# GameTimeTracker

Windows 后台自动统计游戏时长 + Notion 同步的桌面应用（WinUI 3）。

## 功能

- 常驻托盘，自动检测你正在玩的游戏并计时，跨午夜自动拆分
- 双向同步 Notion：本地记录推到「每日时长表」，Notion 里的手动记录 / 总表条目 / 封面自动拉回
- 双向删除：任一侧删除，另一侧跟着对账（有确认框，Notion 侧进回收站可恢复）
- 游玩热力图、今日 Top 3、最近记录、深浅色主题

## 使用

1. 到 [Releases](../../releases) 下载 `GameTimeTracker-vX.X.X-win-x64.zip`，解压到任意目录（如 `D:\Tools\GameTimeTracker`，**不要放 Program Files**，程序需要在自身目录读写数据）
2. 运行 `GameTimeTracker.App.exe`，无需安装任何运行时
3. 按下面步骤配置 Notion，之后同步全自动

> 遇到 bug？把 `data\logs\app.log` 发给开发者即可定位（日志自动轮转，不会无限变大）。

## Notion 配置

### 1. 创建 Integration 并拿到 Token

1. 打开 [notion.so/my-integrations](https://www.notion.so/my-integrations) → **New integration**，类型选 **Internal**，名字随意
2. 创建后在 **Secrets** 页复制 **Internal Access Secret**（`ntn_` 或 `secret_` 开头），这就是程序设置页要填的 Token

### 2. 把两个数据库连接给 Integration

在**游戏总表**和**每日时长表**两个数据库页面，右上角 `···` → **Connections** → 搜索并添加你刚创建的 Integration。漏了这步程序会拿到空数据。

**数据库 ID 的位置**：在浏览器打开数据库页面，URL 中 `notion.so/` 后面那串 32 位字符（去掉连字符后填入程序）就是数据库 ID。

### 3. 数据库需要的属性

**每日时长表**（程序会写入，属性名需一致）：

| 属性 | 类型 | 说明 |
|---|---|---|
| 游戏名称 | Title | 程序写入「游戏名 · X min」 |
| 日期 | Date | 必需，拉取时按天对账 |
| 时长 | Number | 分钟数 |
| 游戏 | Relation → 游戏总表 | 建议配置，用于自动关联总表条目 |

**游戏总表**（程序读取，用于识别游戏和拉封面）：

| 属性 | 类型 | 说明 |
|---|---|---|
| 游戏名称 | Title | 必需 |
| 封面 | URL / Files（或直接用页面 Cover） | 用于界面背景与图标 |
| 别名 | 多选或文本 | 可选，辅助标题匹配 |
| 游戏标识 | 多选或文本 | 可选，填 `steam:appid`（如 `steam:3112010`）可精确匹配运行中的 Steam 游戏 |

## 从源码构建

依赖：Windows 10/11、[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)（Windows App SDK 随 NuGet 自动还原）。

```powershell
dotnet build GameTimeTracker.slnx
dotnet run --project src/GameTimeTracker.App
```

## License

MIT，见 [LICENSE](LICENSE)。
