using FluentAssertions;
using Xunit;

namespace GameTimeTracker.Tests;

/// <summary>
/// E1 回归网（2026-10-07 用户批准）：**删除冲突**。
///
/// <para>
/// 场景：设备 B 删除了 G1（本地墓碑）；设备 A 在 B 离线期间继续游玩 G1 并产生新的会话事实 S4。
/// B 重新上线同步时，发现：
/// <list type="bullet">
/// <item>G1 的陈旧 Create 被本地墓碑压住（不复活）✓</item>
/// <item>S4 指向的 Game 在本地缺失，**但存在该 Game 的墓碑** ⇒ 不是"依赖还没到"，</item>
/// </list>
/// ⇒ 必须归入 <c>Deferred(DeletionConflict)</c>：两侧事实都保留、不丢 S4、不删墓碑、不改不可变事实。
/// </para>
/// </summary>
public class DeviceSyncDeletionConflictTests
{
    [Fact]
    public async Task OfflineCreatedSession_ForADeletedGame_IsDeferredAsDeletionConflict()
    {
        var server = new FakeWebDavServer();
        using var a = new WebDavTestRig(server);
        using var b = new WebDavTestRig(server);

        // ---- A：既有 Game G1 + 一条会话（稍后推给 B）----
        var aGameId = (int)a.InsertGame("Alpha", @"E:\games\alpha.exe", "g-alpha");
        a.InsertSession(aGameId, "s-alpha", 600);
        (await a.EnableAsync()).Ok.Should().BeTrue();
        (await a.BackfillAsync()).Done.Should().BeTrue();

        // ---- B：曾经拥有同一个 G1，然后**用户删除**它（产生真实墓碑）----
        var bGameId = (int)b.InsertGame("Alpha", @"E:\games\alpha.exe", "g-alpha");
        (await b.EnableAsync()).Ok.Should().BeTrue();
        await b.Repo.DeleteGameAsync(bGameId);

        b.Scalar<int>(
                "SELECT COUNT(*) FROM sync_tombstones WHERE entity_type = 'Game' AND entity_global_id = 'g-alpha';")
            .Should().Be(1, "B 的删除必须产生真实墓碑（C5）");

        // 隔离关注点：清掉 B 自己那份删除推送，别让本测试受它影响
        b.Exec("DELETE FROM sync_queue;");

        // ---- A 推送（Game Create + Session），B 拉取 ----
        await a.SyncAsync();
        await b.SyncAsync();

        // ① 会话**没有落地**（Game 已删除），但也**没有丢**：留在 Deferred 台账里
        b.Scalar<int>("SELECT COUNT(*) FROM sessions WHERE global_id = 's-alpha';").Should().Be(0);
        b.Scalar<int>(
                "SELECT COUNT(*) FROM sync_deferred_changes WHERE reason = 'deletion_conflict';")
            .Should().BeGreaterThan(0, "必须显式分类为删除冲突");

        // ② 不得退化成"依赖还没到"（那会被反复重试 / 静默复活）
        b.Scalar<int>(
                "SELECT COUNT(*) FROM sync_deferred_changes WHERE reason = 'dependency_missing' " +
                "AND entity_global_id = 's-alpha';")
            .Should().Be(0, "删除冲突不能被当作 DependencyMissing");

        // ③ 墓碑仍在（删除事实不被覆盖）
        b.Scalar<int>(
                "SELECT COUNT(*) FROM sync_tombstones WHERE entity_global_id = 'g-alpha';")
            .Should().Be(1);

        // ④ Game 未复活：不出现在正常 Game 作用域
        (await b.Repo.GetAllGamesAsync()).Should().NotContain(g => g.GlobalId == "g-alpha");

        // ⑤ 会话事实完整保留在台账里（不丢、不改写）
        b.Scalar<int>(
                "SELECT COUNT(*) FROM sync_deferred_changes " +
                "WHERE reason = 'deletion_conflict' AND payload LIKE '%s-alpha%';")
            .Should().BeGreaterThan(0, "S4 的不可变事实必须原样留在台账中，等用户显式处理");
    }
}
