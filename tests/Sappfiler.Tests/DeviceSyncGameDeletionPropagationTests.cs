using FluentAssertions;
using Xunit;

namespace GameTimeTracker.Tests;

/// <summary>
/// C5 闭环回归网（2026-10-07 用户批准）：「删除 Game ≠ 删除历史事实」+ 删除事实跨设备传播。
///
/// <para>
/// 产品规则：用户删除 G1 ⇒ 本地 <c>status = archived</c> + 写墓碑 + 入队 Delete 变更；
/// 其他设备收到 Delete ⇒ 同样归档（**不物理删除**）、保留各自的历史 Session / daily_summary。
/// </para>
///
/// <para>
/// 用户点名必须钉死的两条：
/// ① 设备 B 已有 G1 + 历史 Session，B 拉取 Delete 后，B 的 sessions / daily_summary
///    **数量与时长一字不变**（只准改 status）；
/// ② 新设备拉取到 Delete，**不能因为本地没有该 Game 而重建一个"正常" Game**。
/// </para>
/// </summary>
public class DeviceSyncGameDeletionPropagationTests
{
    private static (WebDavTestRig A, WebDavTestRig B) TwoDevices()
    {
        var server = new FakeWebDavServer();
        return (new WebDavTestRig(server), new WebDavTestRig(server));
    }

    private static async Task<int> PushGameWithHistoryAsync(WebDavTestRig a)
    {
        var gameId = (int)a.InsertGame("Alpha", @"E:\games\alpha.exe", "g-alpha");
        a.InsertSession(gameId, "s-a", 600);

        (await a.EnableAsync()).Ok.Should().BeTrue();
        (await a.BackfillAsync()).Done.Should().BeTrue();
        await a.SyncAsync();

        return gameId;
    }

    // ==================== ① 删除只改 status，绝不改 B 的历史数据 ====================

    [Fact]
    public async Task RemoteDelete_ArchivesTheGame_AndLeavesTheReceiversHistoryUntouched()
    {
        var (a, b) = TwoDevices();
        using var _a = a;
        using var _b = b;

        var aGameId = await PushGameWithHistoryAsync(a);

        // B 拉一次，拿到 G1（同一 global_id）
        (await b.EnableAsync()).Ok.Should().BeTrue();
        await b.SyncAsync();

        var bGameId = b.Scalar<int>("SELECT id FROM games WHERE global_id = 'g-alpha';");
        bGameId.Should().BeGreaterThan(0, "B 必须先真的拥有 G1");

        // B 自己的历史事实：一条 Session + 一条 daily_summary（本机产生，与 A 无关）
        b.InsertSession(bGameId, "s-b", 1200);
        b.Exec(
            """
            INSERT INTO daily_summary (date, game_id, duration_seconds, duration_minutes, session_count, sync_status)
            VALUES ('2026-09-20', @g, 1200, 20, 1, 'pending');
            """,
            new { g = bGameId });

        const string sessionsSql = "SELECT id, game_id, duration_seconds, start_time FROM sessions ORDER BY id;";
        const string dailySql =
            "SELECT id, date, game_id, duration_seconds, duration_minutes, session_count FROM daily_summary ORDER BY id;";

        var sessionsBefore = b.Dump(sessionsSql);
        var dailyBefore = b.Dump(dailySql);
        var sessionCountBefore = b.Scalar<int>("SELECT COUNT(*) FROM sessions;");
        var dailySecondsBefore = b.Scalar<int>("SELECT COALESCE(SUM(duration_seconds), 0) FROM daily_summary;");

        // A 删除 G1（归档 + 墓碑 + Delete 变更入队）
        await a.Repo.DeleteGameAsync(aGameId);
        await a.SyncAsync();

        // B 收到 Delete
        await b.SyncAsync();

        // ① B 的 G1 变成「已删除（归档）」
        b.Scalar<string>("SELECT LOWER(status) FROM games WHERE global_id = 'g-alpha';")
            .Should().Be("archived", "远端删除在接收端只改变 status，不做物理删除");

        // ② B 的历史事实**一字未改**（数量与时长都不准变）
        b.Scalar<int>("SELECT COUNT(*) FROM sessions;").Should().Be(sessionCountBefore);
        b.Scalar<int>("SELECT COALESCE(SUM(duration_seconds), 0) FROM daily_summary;").Should().Be(dailySecondsBefore);
        b.Dump(sessionsSql).Should().Be(sessionsBefore, "删除 Game 绝不能动历史 Session（C5 最核心的回归保护）");
        b.Dump(dailySql).Should().Be(dailyBefore, "删除 Game 绝不能动历史 daily_summary（C5 最核心的回归保护）");

        // ③ 归档后不再出现在正常 Game 作用域
        (await b.Repo.GetAllGamesAsync()).Should().NotContain(g => g.GlobalId == "g-alpha",
            "已删除的 Game 不得再出现在正常 Game 列表里");
    }

    // ==================== ② 新设备拉到 Delete：不得重建"正常 Game" ====================

    [Fact]
    public async Task FreshDevice_PullingTheDelete_DoesNotRecreateANormalGame()
    {
        var server = new FakeWebDavServer();
        using var a = new WebDavTestRig(server);
        using var c = new WebDavTestRig(server);   // 全新设备（此前没有 G1）

        var aGameId = await PushGameWithHistoryAsync(a);

        await a.Repo.DeleteGameAsync(aGameId);
        await a.SyncAsync();

        // 新设备从零开始同步：会先拉到历史 Create，再拉到 Delete
        (await c.EnableAsync()).Ok.Should().BeTrue();
        await c.SyncAsync();

        (await c.Repo.GetAllGamesAsync()).Should().NotContain(g => g.GlobalId == "g-alpha",
            "墓碑/删除事实必须压住陈旧 Create —— 新设备不得重建一个『正常』的 G1");

        var status = c.Scalar<string>("SELECT LOWER(status) FROM games WHERE global_id = 'g-alpha';");
        if (!string.IsNullOrEmpty(status))
        {
            status.Should().Be("archived", "若该行确实落地了，也只能是历史归档形态，绝不能是正常 Game");
        }
    }
}
