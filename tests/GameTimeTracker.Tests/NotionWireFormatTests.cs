using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using GameTimeTracker.Infrastructure.Notion;

namespace GameTimeTracker.Tests;

/// <summary>
/// 断言**实际发到 Notion 的 JSON**，而不是只测内存里的中间值。
/// 起因：「单次时长」的单位从分钟改成小时（1 位小数，2026-09-19）时，
/// 中间模型的 DurationMinutes 一直是分钟，只有 Notion 边界做换算 ——
/// 如果只测中间值，换算写错也发现不了。
/// </summary>
public class NotionWireFormatTests
{
    /// <summary>捕获请求体并按需返回固定响应，避免联网。</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly string _responseBody;
        public List<string> RequestBodies { get; } = new();

        public CapturingHandler(string responseBody) => _responseBody = responseBody;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json")
            };
        }
    }

    private static (NotionClient Client, CapturingHandler Handler) MakeClient(string responseBody = """{"id":"page-1"}""")
    {
        var handler = new CapturingHandler(responseBody);
        // maxRetries=1：失败不重试，测试跑得快
        return (new NotionClient("token", new HttpClient(handler), maxRetries: 1), handler);
    }

    [Theory]
    [InlineData(42, 0.7)]       // 42 分钟 = 0.70 小时（除得尽）
    [InlineData(60, 1)]         // 1 小时
    [InlineData(63, 1.05)]      // 63 分钟 = 1.05 小时（1 位小数会变 1.1，丢精度）
    [InlineData(600, 10)]       // 10 小时
    [InlineData(5, 0.08)]       // 5 分钟 = 0.0833… → 0.08（1 位小数会变 0.1）
    [InlineData(15, 0.25)]      // 15 分钟 = 0.25 小时 —— 1 位小数会变 0.2 并读回 12 分
    [InlineData(1, 0.02)]       // 1 分钟 = 0.0166… → 0.02
    public async Task CreateDailyRecord_ShouldSendDurationInHours(int durationMinutes, double expectedHours)
    {
        var (client, handler) = MakeClient();

        await client.CreateDailyRecordAsync("db-1", "2026-09-19", "测试游戏", durationMinutes, gamePageId: null);

        handler.RequestBodies.Should().ContainSingle();
        using var doc = JsonDocument.Parse(handler.RequestBodies[0]);
        var number = doc.RootElement
            .GetProperty("properties").GetProperty("时长").GetProperty("number")
            .GetDouble();

        number.Should().BeApproximately(expectedHours, 0.001,
            $"「时长」的单位是小时（2 位小数），{durationMinutes} 分钟应为 {expectedHours} 小时");
    }

    [Fact]
    public async Task CreateDailyRecord_ShouldSendHours_ConsistentWithTitle()
    {
        // 标题后缀和「时长」数值必须表达同一个时长。
        // 曾出现过标题是小时、数值是分钟的不一致，所以这里把两者放一起比。
        var (client, handler) = MakeClient();

        await client.CreateDailyRecordAsync("db-1", "2026-09-19", "测试游戏", 90, gamePageId: null);

        using var doc = JsonDocument.Parse(handler.RequestBodies[0]);
        var props = doc.RootElement.GetProperty("properties");

        var title = props.GetProperty("游戏动态").GetProperty("title")[0].GetProperty("text").GetProperty("content").GetString();
        var hours = props.GetProperty("时长").GetProperty("number").GetDouble();

        title.Should().Be("测试游戏 · 1.5 h");
        hours.Should().Be(1.5);
        title.Should().Contain($"{hours} h", "标题里的小时数应与数值属性一致");
    }

    [Fact]
    public async Task UpdateDailyRecord_ShouldSendDurationInHours()
    {
        var (client, handler) = MakeClient("""{"id":"page-1"}""");

        await client.UpdateDailyRecordAsync("page-1", 120, gamePageId: null, gameName: "测试游戏", writeDuration: true);

        handler.RequestBodies.Should().ContainSingle();
        using var doc = JsonDocument.Parse(handler.RequestBodies[0]);
        doc.RootElement.GetProperty("properties").GetProperty("时长").GetProperty("number")
            .GetDouble().Should().Be(2.0, "120 分钟 = 2 小时");
        doc.RootElement.GetProperty("properties").GetProperty("绑定状态").GetProperty("select").GetProperty("name")
            .GetString().Should().Be("未绑定", "未绑定的游戏更新时必须明确写入「未绑定」状态");
    }

    [Fact]
    public async Task UpdateDailyRecord_WhenBound_ShouldSendBoundStatus()
    {
        var (client, handler) = MakeClient("""{"id":"page-1"}""");

        await client.UpdateDailyRecordAsync("page-1", 120, gamePageId: "master-page-1", gameName: "测试游戏", writeDuration: true);

        handler.RequestBodies.Should().ContainSingle();
        using var doc = JsonDocument.Parse(handler.RequestBodies[0]);
        var props = doc.RootElement.GetProperty("properties");
        props.GetProperty("绑定状态").GetProperty("select").GetProperty("name")
            .GetString().Should().Be("已绑定", "已绑定的游戏更新时应写入「已绑定」状态");
        props.GetProperty("关联游戏").GetProperty("relation")[0].GetProperty("id")
            .GetString().Should().Be("master-page-1");
    }

    [Fact]
    public async Task UpdateDailyBindingStatus_ShouldOnlyPatchBindingStatus()
    {
        var (client, handler) = MakeClient("""{"id":"page-1"}""");

        await client.UpdateDailyBindingStatusAsync("page-1", "未绑定");

        handler.RequestBodies.Should().ContainSingle();
        using var doc = JsonDocument.Parse(handler.RequestBodies[0]);
        var props = doc.RootElement.GetProperty("properties");
        props.GetProperty("绑定状态").GetProperty("select").GetProperty("name")
            .GetString().Should().Be("未绑定");
        props.TryGetProperty("时长", out _).Should().BeFalse("单独更新绑定状态绝不能携带时长");
        props.TryGetProperty("单次时长", out _).Should().BeFalse("单独更新绑定状态绝不能携带单次时长");
        props.TryGetProperty("游戏动态", out _).Should().BeFalse("单独更新绑定状态绝不能携带标题");
    }

    [Fact]
    public async Task QueryDailyRecords_ShouldReadFractionalHours_WithoutThrowing()
    {
        // 关键回归点：读 Number 属性原先用的是 GetInt32，
        // 遇到 0.7 这种小数会直接抛异常，整条记录拉不回来。
        // ⚠️ title 数组里必须带 `plain_text` —— NotionClient.ExtractTitle 读的是它，
        //    不是 text.content。真实 Notion API 两个字段都会返回，
        //    夹具若只给 text.content，标题会被解析成空串、整条记录被跳过。
        const string response = """
        {
          "results": [
            {
              "id": "daily-1",
              "properties": {
                "游戏动态": {
                  "type": "title",
                  "title": [ { "plain_text": "测试游戏 · 0.7 h", "text": { "content": "测试游戏 · 0.7 h" } } ]
                },
                "日期": { "type": "date", "date": { "start": "2026-09-19" } },
                "单次时长": { "type": "number", "number": 0.7 }
              }
            }
          ],
          "has_more": false
        }
        """;
        var (client, _) = MakeClient(response);

        var records = await client.QueryDailyRecordsAsync("db-1");

        records.Should().ContainSingle();
        records[0].DurationMinutes.Should().Be(42, "0.7 小时应换算回 42 分钟");
        records[0].GameTitle.Should().Be("测试游戏");
    }

    [Theory]
    // 标题写着 min / 分 → 值是分钟（历史行。程序早期写的就是这种格式）
    [InlineData("老游戏 · 120 min", 120, 120)]
    [InlineData("老游戏 (42分)", 42, 42)]
    // ★ 雾山 2026-09-19 报的严重错误：旧行 5 分钟被读成了 5 小时（放大 60 倍）
    [InlineData("老游戏 · 5 min", 5, 5)]
    // 标题写着 h → 值是小时
    [InlineData("老游戏 · 2 h", 2, 120)]
    [InlineData("老游戏 · 0.08 h", 0.08, 5)]
    // 用户手写的：他按小时写
    [InlineData("黑旗10.1h", 10.1, 606)]
    [InlineData("致命躯壳 45min", 45, 45)]
    // 标题里没有单位 → 只能按数量级猜（单日不可能超过 24 小时，>24 视为分钟）
    [InlineData("Half-Life 2", 300, 300)]
    [InlineData("Half-Life 2", 2, 120)]
    public async Task QueryDailyRecords_ShouldConvertDurationByTitleUnit(
        string title, double number, int expectedMinutes)
    {
        // 单位**必须优先看标题**，不能只看数值大小 ——
        // 只看大小会把旧行里的 5（分钟）读成 5 小时，整整放大 60 倍。
        //
        // 标题里一直带着单位：程序写的「… · 0.08 h」、早期的「… · 42 min」「… (42分)」、
        // 用户手写的「黑旗10.1h」／「致命躯壳 45min」，所以可以确定性地判。
        var response = $$"""
        {
          "results": [
            {
              "id": "p1",
              "properties": {
                "游戏动态": {
                  "type": "title",
                  "title": [ { "plain_text": "{{title}}", "text": { "content": "{{title}}" } } ]
                },
                "日期": { "type": "date", "date": { "start": "2026-09-19" } },
                "单次时长": { "type": "number", "number": {{number.ToString(System.Globalization.CultureInfo.InvariantCulture)}} }
              }
            }
          ],
          "has_more": false
        }
        """;
        var (client, _) = MakeClient(response);

        var records = await client.QueryDailyRecordsAsync("db-1");

        records.Should().ContainSingle();
        records[0].DurationMinutes.Should().Be(expectedMinutes);
    }

    [Fact]
    public async Task QueryDailyRecords_ShouldFlagMinuteUnit_SoInflatedRowsCanBeRepaired()
    {
        // 拉取时要把"单位是分钟"这件事报给仓储 ——
        // 仓储靠它识别并修复历史上被放大 60 倍的本地行（见 SyncDailyRecordFromNotionAsync）。
        const string response = """
        {
          "results": [
            {
              "id": "p1",
              "properties": {
                "游戏动态": {
                  "type": "title",
                  "title": [ { "plain_text": "老游戏 · 5 min", "text": { "content": "老游戏 · 5 min" } } ]
                },
                "日期": { "type": "date", "date": { "start": "2026-09-19" } },
                "单次时长": { "type": "number", "number": 5 }
              }
            }
          ],
          "has_more": false
        }
        """;
        var (client, _) = MakeClient(response);

        var records = await client.QueryDailyRecordsAsync("db-1");

        records[0].DurationUnitIsMinutes.Should().BeTrue();

        // 标题写 h 的必须为 false，否则会把正常行误判成"需要修复"
        const string responseHours = """
        {
          "results": [
            {
              "id": "p2",
              "properties": {
                "游戏动态": {
                  "type": "title",
                  "title": [ { "plain_text": "老游戏 · 2 h", "text": { "content": "老游戏 · 2 h" } } ]
                },
                "日期": { "type": "date", "date": { "start": "2026-09-19" } },
                "单次时长": { "type": "number", "number": 2 }
              }
            }
          ],
          "has_more": false
        }
        """;
        var (client2, _) = MakeClient(responseHours);
        (await client2.QueryDailyRecordsAsync("db-1"))[0].DurationUnitIsMinutes.Should().BeFalse();
    }

    [Fact]
    public async Task DurationRoundTrip_ShouldBeExact_ForEveryPlausibleMinuteValue()
    {
        // 这是"2 位小数"这个选择的核心依据，所以穷举验证：
        // 1..1440 分钟逐个写进表、再读回来，必须**一个不差**。
        //
        // 1 位小数做不到这件事 —— 粒度 6 分钟，1440 个值里 1200 个往返不回来
        // （15 分 → 0.2h → 12 分），并会连带引发"本地永远领先 → 每轮重推"。
        var mismatches = new List<string>();

        for (int minutes = 1; minutes <= 1440; minutes++)
        {
            var (writeClient, writeHandler) = MakeClient();
            await writeClient.CreateDailyRecordAsync("db-1", "2026-09-19", "往返测试", minutes, null);
            using var sent = JsonDocument.Parse(writeHandler.RequestBodies[0]);
            var sentProps = sent.RootElement.GetProperty("properties");
            var hours = sentProps.GetProperty("时长").GetProperty("number").GetDouble();
            // 连标题一起取出来：生产路径判单位靠的就是标题末尾的「h」，
            // 用真实标题读回来才算真的走完整链路（否则只是在测那条回退分支）
            var writtenTitle = sentProps.GetProperty("游戏动态")
                .GetProperty("title")[0].GetProperty("text").GetProperty("content").GetString();

            // 把刚写出去的小时值当成远端数据读回来
            var readResponse = $$"""
            {
              "results": [
                {
                  "id": "p",
                  "properties": {
                    "游戏动态": {
                      "type": "title",
                      "title": [ { "plain_text": "{{writtenTitle}}", "text": { "content": "{{writtenTitle}}" } } ]
                    },
                    "日期": { "type": "date", "date": { "start": "2026-09-19" } },
                    "单次时长": { "type": "number", "number": {{hours.ToString(System.Globalization.CultureInfo.InvariantCulture)}} }
                  }
                }
              ],
              "has_more": false
            }
            """;
            var (readClient, _) = MakeClient(readResponse);
            var back = (await readClient.QueryDailyRecordsAsync("db-1"))[0].DurationMinutes;

            if (back != minutes) mismatches.Add($"{minutes} 分 → {hours} h → {back} 分");
        }

        mismatches.Should().BeEmpty(
            $"2 位小数应能精确还原 1..1440 全部分钟值，实际有 {mismatches.Count} 个不一致："
            + string.Join("、", mismatches.Take(5)));
    }

    /// <summary>
    /// 用户手工写的标题形如「致命躯壳2.2h」—— 时长紧贴名字、没有 · 分隔。
    /// 旧后缀正则只认「名字 · 2.2 h」和「名字 (42分)」，于是一整串被当成游戏名；
    /// 拉取时为每条这样的记录新建一个**永远绑不上总表**的游戏行，
    /// 「待处理」的数字和「映射库全是未绑定」就是这么来的（2026-09-19 用户反馈）。
    /// </summary>
    [Fact]
    public async Task QueryDailyRecords_ShouldStripHandwrittenDurationSuffix()
    {
        const string response = """
        {
          "results": [
            {
              "id": "d1",
              "properties": {
                "游戏动态": { "type": "title", "title": [ { "plain_text": "致命躯壳2.2h", "text": { "content": "致命躯壳2.2h" } } ] },
                "日期": { "type": "date", "date": { "start": "2026-07-16" } },
                "单次时长": { "type": "number", "number": 2.2 }
              }
            },
            {
              "id": "d2",
              "properties": {
                "游戏动态": { "type": "title", "title": [ { "plain_text": "不思议迷宫 · 0.7 h", "text": { "content": "不思议迷宫 · 0.7 h" } } ] },
                "日期": { "type": "date", "date": { "start": "2026-07-16" } },
                "单次时长": { "type": "number", "number": 0.7 }
              }
            },
            {
              "id": "d3",
              "properties": {
                "游戏动态": { "type": "title", "title": [ { "plain_text": "三国志11", "text": { "content": "三国志11" } } ] },
                "日期": { "type": "date", "date": { "start": "2026-07-16" } },
                "单次时长": { "type": "number", "number": 1 }
              }
            }
          ],
          "has_more": false
        }
        """;
        var (client, _) = MakeClient(response);

        var records = await client.QueryDailyRecordsAsync("db-1");

        records.Should().HaveCount(3);
        records[0].GameTitle.Should().Be("致命躯壳", "手写的紧贴时长后缀必须剥掉");
        records[1].GameTitle.Should().Be("不思议迷宫", "程序写的「· X h」后缀照旧要剥");
        records[2].GameTitle.Should().Be("三国志11", "以数字结尾的真名不能被误剥（数字后必须紧跟 h/min）");
    }
}
