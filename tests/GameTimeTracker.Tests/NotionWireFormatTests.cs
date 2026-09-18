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
            .GetProperty("properties").GetProperty("单次时长").GetProperty("number")
            .GetDouble();

        number.Should().BeApproximately(expectedHours, 0.001,
            $"「单次时长」的单位是小时（1 位小数），{durationMinutes} 分钟应为 {expectedHours} 小时");
    }

    [Fact]
    public async Task CreateDailyRecord_ShouldSendHours_ConsistentWithTitle()
    {
        // 标题后缀和「单次时长」数值必须表达同一个时长。
        // 曾出现过标题是小时、数值是分钟的不一致，所以这里把两者放一起比。
        var (client, handler) = MakeClient();

        await client.CreateDailyRecordAsync("db-1", "2026-09-19", "测试游戏", 90, gamePageId: null);

        using var doc = JsonDocument.Parse(handler.RequestBodies[0]);
        var props = doc.RootElement.GetProperty("properties");

        var title = props.GetProperty("游戏动态").GetProperty("title")[0].GetProperty("text").GetProperty("content").GetString();
        var hours = props.GetProperty("单次时长").GetProperty("number").GetDouble();

        title.Should().Be("测试游戏 · 1.5 h");
        hours.Should().Be(1.5);
        title.Should().Contain($"{hours} h", "标题里的小时数应与数值属性一致");
    }

    [Fact]
    public async Task UpdateDailyRecord_ShouldSendDurationInHours()
    {
        var (client, handler) = MakeClient("""{"id":"page-1"}""");

        await client.UpdateDailyRecordAsync("page-1", 120, gamePageId: null, gameName: "测试游戏");

        handler.RequestBodies.Should().ContainSingle();
        using var doc = JsonDocument.Parse(handler.RequestBodies[0]);
        doc.RootElement.GetProperty("properties").GetProperty("单次时长").GetProperty("number")
            .GetDouble().Should().Be(2.0, "120 分钟 = 2 小时");
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

    [Fact]
    public async Task QueryDailyRecords_ShouldTreatLegacyMinutesValueAsMinutes()
    {
        // 历史行里存的是分钟（如 120）。判别规则：单日时长不可能超过 24 小时，
        // 所以 > 24 的值必然是旧格式，按分钟直接用。
        const string response = """
        {
          "results": [
            {
              "id": "daily-legacy",
              "properties": {
                "游戏动态": {
                  "type": "title",
                  "title": [ { "plain_text": "老游戏 · 2.0 h", "text": { "content": "老游戏 · 2.0 h" } } ]
                },
                "日期": { "type": "date", "date": { "start": "2026-09-18" } },
                "单次时长": { "type": "number", "number": 120 }
              }
            }
          ],
          "has_more": false
        }
        """;
        var (client, _) = MakeClient(response);

        var records = await client.QueryDailyRecordsAsync("db-1");

        records[0].DurationMinutes.Should().Be(120, ">24 的值是旧的分钟格式，不应再乘 60");
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
            var hours = sent.RootElement.GetProperty("properties")
                .GetProperty("单次时长").GetProperty("number").GetDouble();

            // 把刚写出去的小时值当成远端数据读回来
            var readResponse = $$"""
            {
              "results": [
                {
                  "id": "p",
                  "properties": {
                    "游戏动态": {
                      "type": "title",
                      "title": [ { "plain_text": "往返测试", "text": { "content": "往返测试" } } ]
                    },
                    "日期": { "type": "date", "date": { "start": "2026-09-19" } },
                    "单次时长": { "type": "number", "number": {{hours}} }
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
}
