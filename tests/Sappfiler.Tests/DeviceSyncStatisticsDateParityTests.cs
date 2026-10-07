using System.Globalization;
using FluentAssertions;
using GameTimeTracker.Core.Services;
using Xunit;

namespace GameTimeTracker.Tests;

/// <summary>
/// C2 回归网（2026-10-07 用户批准修复）：**远端落地的统计日必须与本机路径完全一致**。
///
/// <para>
/// 缺陷：`ApplyRemoteSession` 原先用 <c>startTime[..10]</c>（本地**日历日**）把整段时长塞进一天，
/// 而本机「心跳 + 结束会话」路径走的是 <c>GameSessionManager.ComputeDailyDeltas</c> →
/// <c>AccountingDateHelper</c>（按 <c>settings.daily_cutoff_hour</c> 结算，跨分界点还会拆成两笔）。
/// ⇒ cutoff ≠ 24 时，**同一台机器**上同一条 Session 会被记到不同的统计日。
/// </para>
///
/// <para>
/// 修复口径：远端路径逐字复用同一个纯函数（不改跨日算法、不改 immutable fact、不改 Schema/API/wire）。
/// 本文件按用户点名的覆盖矩阵组织：cutoff = 00:00(→24) / 24 / 28 / 30 + 跨越统计边界 + 本机/远端同 fact 一致。
/// </para>
/// </summary>
public class DeviceSyncStatisticsDateParityTests
{
    private const int TenMinutes = 600;

    private static (WebDavTestRig A, WebDavTestRig B) TwoDevices()
    {
        var server = new FakeWebDavServer();
        return (new WebDavTestRig(server), new WebDavTestRig(server));
    }

    /// <summary>把「本地墙上时间」换算成与之对应的真实 UTC ISO（跟着测试机时区走，避免时区抖动）。</summary>
    private static string LocalToUtcIso(string localWallClock)
        => DateTime.SpecifyKind(
                DateTime.ParseExact(localWallClock, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                DateTimeKind.Local)
            .ToUniversalTime()
            .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>在 A 上造一条历史会话（指定本地墙上开始时刻），设好 B 的跨日设置后同步过去。</summary>
    private static async Task PushOneSessionAsync(
        WebDavTestRig a, WebDavTestRig b, string localStart, int durationSeconds, int receiverCutoffHour)
    {
        var gameId = a.InsertGame("Alpha", @"E:\games\alpha.exe", "g-alpha");
        var sessionId = a.InsertSession(gameId, "s-alpha", durationSeconds);

        // 播种行内部用的是固定 fixture 时刻：这里改成"指定的本地墙上时刻"，
        // start/end/last_heartbeat 写本地墙上时间，started_at_utc/ended_at_utc 写对应的真实 UTC。
        var endLocal = DateTime.ParseExact(localStart, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            .AddSeconds(durationSeconds)
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        a.Exec(
            """
            UPDATE sessions
            SET start_time = @s, end_time = @e, last_heartbeat = @s,
                started_at_utc = @su, ended_at_utc = @eu
            WHERE id = @id;
            """,
            new { s = localStart, e = endLocal, su = LocalToUtcIso(localStart), eu = LocalToUtcIso(endLocal), id = sessionId });

        (await a.EnableAsync()).Ok.Should().BeTrue();
        (await a.BackfillAsync()).Done.Should().BeTrue();
        await a.SyncAsync();

        // 接收端（B）用自己的跨日设置派生 daily_summary
        b.Exec("INSERT OR REPLACE INTO settings(key, value) VALUES('daily_cutoff_hour', @v);",
            new { v = receiverCutoffHour.ToString(CultureInfo.InvariantCulture) });

        (await b.EnableAsync()).Ok.Should().BeTrue();
        await b.SyncAsync();
    }

    // ==================== ① cutoff 矩阵：24（=00:00 归一到 24） / 28 / 30 ====================

    [Theory]
    [InlineData(0, "2026-09-20 20:00:00", "2026-09-20")]   // 00:00 ⇒ 归一到 24 ⇒ 日历日
    [InlineData(24, "2026-09-20 20:00:00", "2026-09-20")]  // 24 ⇒ 日历日
    [InlineData(28, "2026-09-20 03:30:00", "2026-09-19")]  // 28（次日 04:00）⇒ 凌晨 03:30 归前一天
    [InlineData(30, "2026-09-20 05:00:00", "2026-09-19")]  // 30（次日 06:00）⇒ 05:00 归前一天
    [InlineData(28, "2026-09-20 10:00:00", "2026-09-20")]  // 28 ⇒ 白天不受影响
    public async Task RemoteSession_LandsOnTheSameAccountingDate_AsTheCutoffRequires(
        int cutoffHour, string localStart, string expectedDate)
    {
        var (a, b) = TwoDevices();
        using var _a = a;
        using var _b = b;

        await PushOneSessionAsync(a, b, localStart, TenMinutes, cutoffHour);

        b.Scalar<int>("SELECT COUNT(*) FROM daily_summary;").Should().Be(1,
            "这条会话没有跨分界点 ⇒ 只应派生一行");
        b.Scalar<string>("SELECT date FROM daily_summary;").Should().Be(expectedDate,
            $"cutoff={cutoffHour} 时必须按 AccountingDateHelper 的会计日归属，而不是日历日");
        b.Scalar<int>("SELECT duration_seconds FROM daily_summary;").Should().Be(TenMinutes,
            "归属日期变了，但时长一分不少");
    }

    // ==================== ② 跨分界点：拆成两笔、分别归入两个统计日 ====================

    [Fact]
    public async Task RemoteSession_CrossingCutoffBoundary_SplitsIntoBothAccountingDays()
    {
        var (a, b) = TwoDevices();
        using var _a = a;
        using var _b = b;

        // 本地 03:30 → 04:30（1 小时），cutoff=28 ⇒ 分界点 04:00
        // 前段 1800s 归 09-19，后段 1800s 归 09-20
        await PushOneSessionAsync(a, b, "2026-09-20 03:30:00", 3600, 28);

        b.Scalar<int>("SELECT COUNT(*) FROM daily_summary;").Should().Be(2,
            "跨越跨日分界点的会话必须拆成两笔，与本机 heartbeat+end 路径口径一致");

        b.Scalar<int>("SELECT duration_seconds FROM daily_summary WHERE date = '2026-09-19';")
            .Should().Be(1800);
        b.Scalar<int>("SELECT duration_seconds FROM daily_summary WHERE date = '2026-09-20';")
            .Should().Be(1800);

        b.Scalar<int>("SELECT SUM(duration_seconds) FROM daily_summary;").Should().Be(3600,
            "拆分只是归属不同，总时长必须守恒");
    }

    // ==================== ③ 本机 / 远端同 fact ⇒ 统计日完全一致 ====================

    [Theory]
    [InlineData(24)]
    [InlineData(28)]
    [InlineData(30)]
    public async Task RemoteLanding_MatchesTheLocalFormula_ForTheSameFacts(int cutoffHour)
    {
        var (a, b) = TwoDevices();
        using var _a = a;
        using var _b = b;

        const string localStart = "2026-09-20 03:30:00";
        const int duration = 3600;

        await PushOneSessionAsync(a, b, localStart, duration, cutoffHour);

        // 本机路径用的就是这个纯函数（GameSessionManager.ComputeDailyDeltas → AccountingDateHelper）
        var start = DateTime.ParseExact(localStart, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var expected = GameSessionManager.ComputeDailyDeltas(
            start, start.AddSeconds(duration), duration, cutoffHour);

        var actual = b.Scalar<string>(
            "SELECT GROUP_CONCAT(date || '=' || duration_seconds, ',') FROM (SELECT date, duration_seconds FROM daily_summary ORDER BY date);");

        var expectedText = string.Join(",", expected
            .OrderBy(d => d.Date)
            .Select(d => $"{d.Date}={d.DurationSeconds}"));

        actual.Should().Be(expectedText,
            "远端落地的统计日与拆分必须与本机路径逐字一致（同机同 fact ⇒ 同结果）");
    }

    // ==================== ④ 幂等：重复 Pull 不改变归属、也不翻倍 ====================

    [Fact]
    public async Task RepeatedPull_KeepsTheSameAccountingDate_AndDoesNotDouble()
    {
        var (a, b) = TwoDevices();
        using var _a = a;
        using var _b = b;

        await PushOneSessionAsync(a, b, "2026-09-20 03:30:00", TenMinutes, 28);

        var before = b.Dump("SELECT date || '=' || duration_seconds FROM daily_summary ORDER BY date;");

        b.Exec("UPDATE sync_state SET cursor = '';");   // 强迫重新拉取同一批
        await b.SyncAsync();

        b.Dump("SELECT date || '=' || duration_seconds FROM daily_summary ORDER BY date;")
            .Should().Be(before, "重复 Pull 既不改变统计日，也不重复累计");
    }
}
