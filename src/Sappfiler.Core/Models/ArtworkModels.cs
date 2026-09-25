namespace GameTimeTracker.Core.Models;

public enum ArtworkSource
{
    Notion,
    SteamLocal,
    SteamRemote,
    EpicLocal,
    EALocal,
    UbisoftLocal,
    XboxLocal,
    GameDirectory,
    Cache,
    Default
}

public enum ArtworkType
{
    LibraryHeader,      // 920×430 横向卡片素材（最契合）
    StoreHeader,        // 460×215 商店横版图
    DirectoryBanner,    // 游戏安装目录提取的 banner / header / splash
    NotionCover,        // 用户在 Notion 总表配置的封面
    DefaultPlaceholder  // 默认兜底占位
}

public sealed class GameArtwork
{
    public string FilePathOrUrl { get; init; } = string.Empty;
    public bool IsLocalFile { get; init; }
    public ArtworkSource Source { get; init; }
    public ArtworkType Type { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
}
