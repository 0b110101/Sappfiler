using FluentAssertions;
using GameTimeTracker.Core.Services;
using Xunit;

namespace GameTimeTracker.Tests;

/// <summary>
/// 存档位置解析：默认 %LocalAppData%\GameTimeTracker，
/// 引导文件（data_location.txt）指向的自定义目录优先（目录必须存在）。
/// </summary>
public class AppPathsTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), $"gtpaths_{Guid.NewGuid():N}");

    public AppPathsTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch { }
    }

    [Fact]
    public void Resolve_UsesDefault_WhenBootstrapIsEmpty()
    {
        AppPaths.ResolveDataDir(null).Should().Be(AppPaths.DefaultDataDir);
        AppPaths.ResolveDataDir("").Should().Be(AppPaths.DefaultDataDir);
        AppPaths.ResolveDataDir("   ").Should().Be(AppPaths.DefaultDataDir);
    }

    [Fact]
    public void Resolve_UsesCustom_WhenDirectoryExists()
    {
        AppPaths.ResolveDataDir(_tmp).Should().Be(_tmp);
    }

    [Fact]
    public void Resolve_FallsBackToDefault_WhenCustomDirDoesNotExist()
    {
        var missing = Path.Combine(_tmp, "not-created-yet");
        AppPaths.ResolveDataDir(missing).Should().Be(AppPaths.DefaultDataDir,
            "引导指向的目录不存在时不能贸然使用（可能是被删了）");
    }

    [Fact]
    public void Bootstrap_WriteReadClear_RoundTrip()
    {
        AppPaths.ClearCustomDataDir();
        AppPaths.ReadBootstrapFile().Should().BeNull("初始状态不应有自定义位置");

        AppPaths.SetCustomDataDir(_tmp);
        AppPaths.ReadBootstrapFile().Should().Be(_tmp);

        AppPaths.ClearCustomDataDir();
        AppPaths.ReadBootstrapFile().Should().BeNull();
    }
}
