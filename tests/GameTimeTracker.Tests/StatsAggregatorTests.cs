using System;
using System.Collections.Generic;
using FluentAssertions;
using GameTimeTracker.Core.Models;
using GameTimeTracker.Core.Services;
using Xunit;

namespace GameTimeTracker.Tests;

public class StatsAggregatorTests
{
    [Fact]
    public void ComputePeriodRange_Month_ShouldCalculateCorrectStartAndEnd()
    {
        var date = new DateTime(2026, 9, 20);
        var range = StatsAggregator.ComputePeriodRange(date, StatsPeriodMode.Month);

        range.StartDate.Should().Be(new DateTime(2026, 9, 1));
        range.EndDate.Should().Be(new DateTime(2026, 9, 30));
        range.PrevStartDate.Should().Be(new DateTime(2026, 8, 1));
        range.DisplayTitle.Should().Be("2026年9月");
        range.PeriodComparisonLabel.Should().Be("较上月");
    }

    [Fact]
    public void ComputePeriodRange_Quarter_ShouldCalculateCorrectRange()
    {
        var date = new DateTime(2026, 8, 15);
        var range = StatsAggregator.ComputePeriodRange(date, StatsPeriodMode.Quarter);

        range.StartDate.Should().Be(new DateTime(2026, 7, 1));
        range.EndDate.Should().Be(new DateTime(2026, 9, 30));
        range.DisplayTitle.Should().Be("2026年第3季度");
        range.PeriodComparisonLabel.Should().Be("较上季度");
    }

    [Fact]
    public void AggregatePeriod_ShouldComputeMetricsAndGenreDistributionCorrectly()
    {
        var range = StatsAggregator.ComputePeriodRange(new DateTime(2026, 9, 20), StatsPeriodMode.Month);

        var summaries = new List<DailySummary>
        {
            // Current period: 2026-09
            new() { Date = "2026-09-10", GameId = 1, GameName = "博德之门3", DurationMinutes = 120, SessionCount = 2 },
            new() { Date = "2026-09-15", GameId = 1, GameName = "博德之门3", DurationMinutes = 180, SessionCount = 3 },
            new() { Date = "2026-09-15", GameId = 2, GameName = "怪物猎人：世界", DurationMinutes = 60, SessionCount = 1 },

            // Previous period: 2026-08
            new() { Date = "2026-08-20", GameId = 1, GameName = "博德之门3", DurationMinutes = 150, SessionCount = 2 }
        };

        var genresMap = new Dictionary<string, List<string>>
        {
            { "博德之门3", new List<string> { "角色扮演", "回合制策略" } },
            { "怪物猎人：世界", new List<string> { "动作冒险", "角色扮演" } }
        };

        var result = StatsAggregator.AggregatePeriod(range, summaries, genresMap);

        // Current total = 120 + 180 + 60 = 360 min = 6h
        result.TotalMinutes.Should().Be(360);
        result.TotalDurationText.Should().Be("6h");
        result.TotalSessions.Should().Be(6); // 2 + 3 + 1

        // Top game is 博德之门3 (300 min = 5h)
        result.TopGame.Should().NotBeNull();
        result.TopGame!.Name.Should().Be("博德之门3");
        result.TopGame.TotalMinutes.Should().Be(300);
        result.TopGame.DurationText.Should().Be("5h");

        // Genre distribution:
        // "角色扮演" is in both games (2 games count)
        // "回合制策略" in 1 game
        // "动作冒险" in 1 game
        result.GenreStats.Should().NotBeEmpty();
        var rpg = result.GenreStats[0];
        rpg.Name.Should().Be("角色扮演");
        rpg.GameCount.Should().Be(2);
        rpg.Ratio.Should().Be(1.0); // max count
    }

    [Fact]
    public void ComputePeriodRange_Year_ShouldCalculateCorrectRange()
    {
        var date = new DateTime(2026, 9, 20);
        var range = StatsAggregator.ComputePeriodRange(date, StatsPeriodMode.Year);

        range.StartDate.Should().Be(new DateTime(2026, 1, 1));
        range.EndDate.Should().Be(new DateTime(2026, 12, 31));
        range.PrevStartDate.Should().Be(new DateTime(2025, 1, 1));
        range.PrevEndDate.Should().Be(new DateTime(2025, 12, 31));
        range.DisplayTitle.Should().Be("2026年");
        range.PeriodComparisonLabel.Should().Be("较上年");
    }

    [Fact]
    public void ComputePeriodRange_PrevNextMonth_ShouldNavigateCorrectly()
    {
        var date = new DateTime(2026, 9, 1);
        var nextMonth = date.AddMonths(1);
        var range10 = StatsAggregator.ComputePeriodRange(nextMonth, StatsPeriodMode.Month);
        range10.StartDate.Should().Be(new DateTime(2026, 10, 1));
        range10.EndDate.Should().Be(new DateTime(2026, 10, 31));
        range10.DisplayTitle.Should().Be("2026年10月");

        var range11 = StatsAggregator.ComputePeriodRange(nextMonth.AddMonths(1), StatsPeriodMode.Month);
        range11.DisplayTitle.Should().Be("2026年11月");

        var range12 = StatsAggregator.ComputePeriodRange(nextMonth.AddMonths(2), StatsPeriodMode.Month);
        range12.DisplayTitle.Should().Be("2026年12月");
    }

    [Fact]
    public void AggregatePeriod_GenreStats_ShouldGroupIntoTop5PlusOther()
    {
        var range = StatsAggregator.ComputePeriodRange(new DateTime(2026, 9, 20), StatsPeriodMode.Month);

        // 7 different games each with a unique genre
        var summaries = new List<DailySummary>();
        var genresMap = new Dictionary<string, List<string>>();
        for (int i = 1; i <= 7; i++)
        {
            var name = $"Game {i}";
            summaries.Add(new DailySummary { Date = "2026-09-10", GameId = i, GameName = name, DurationMinutes = i * 10, SessionCount = 1 });
            genresMap[name] = new List<string> { $"Genre {i}" };
        }

        var result = StatsAggregator.AggregatePeriod(range, summaries, genresMap);
        result.GenreStats.Should().HaveCount(6); // Top 5 + "其他"
        result.GenreStats.Last().Name.Should().Be("其他");
        result.GenreStats.Last().GameCount.Should().Be(2);
        result.GenreStats.Last().ColorHex.Should().Be("#94A3B8");
        result.GenreStats.Last().SubTagsText.Should().Contain("Genre 2").And.Contain("Genre 1");
        result.TrendPoints.First().DateLabel.Should().Be("9/1");
        result.TrendPoints.Last().DateLabel.Should().Be("9/30");
    }

    [Fact]
    public void AggregatePeriod_DailyTrendPoint_ShouldFlagIsTodayCorrectly()
    {
        var today = DateTime.Today;
        var range = StatsAggregator.ComputePeriodRange(today, StatsPeriodMode.Month);

        var summaries = new List<DailySummary>
        {
            new() { Date = today.ToString("yyyy-MM-dd"), GameId = 1, GameName = "博德之门3", DurationMinutes = 60, SessionCount = 1 }
        };

        var result = StatsAggregator.AggregatePeriod(range, summaries, null);
        var todayPoint = System.Linq.Enumerable.FirstOrDefault(result.TrendPoints, p => p.Date.Date == today.Date);
        todayPoint.Should().NotBeNull();
        todayPoint!.IsToday.Should().BeTrue();

        var otherPoints = System.Linq.Enumerable.Where(result.TrendPoints, p => p.Date.Date != today.Date);
        otherPoints.Should().OnlyContain(p => !p.IsToday);
    }

    [Fact]
    public void AggregatePeriod_DonutSlices_ShouldContainTop5GamesWithoutOther()
    {
        var range = StatsAggregator.ComputePeriodRange(new DateTime(2026, 9, 20), StatsPeriodMode.Month);
        var summaries = new List<DailySummary>();
        for (int i = 1; i <= 8; i++)
        {
            summaries.Add(new DailySummary
            {
                Date = "2026-09-10",
                GameId = i,
                GameName = $"Game {i}",
                DurationMinutes = i * 60, // 60, 120, 180, 240, 300, 360, 420, 480 => total = 2160
                SessionCount = 1
            });
        }

        var result = StatsAggregator.AggregatePeriod(range, summaries, null);
        result.DonutSlices.Should().HaveCount(5);
        result.DonutSlices.Should().NotContain(s => s.Name == "其他游戏" || s.Name == "其他");
        result.DonutSlices[0].Name.Should().Be("Game 8");
        // Percentage is relative to total minutes (2160)
        result.DonutSlices[0].Percentage.Should().BeApproximately(480.0 / 2160.0, 0.001);
    }

    [Fact]
    public void AggregatePeriod_QuarterMode_ShouldGenerateWeeklyTrendPoints()
    {
        // 2026 Q3: 2026-07-01 to 2026-09-30
        var range = StatsAggregator.ComputePeriodRange(new DateTime(2026, 8, 15), StatsPeriodMode.Quarter);
        var summaries = new List<DailySummary>
        {
            new() { Date = "2026-07-08", GameId = 1, GameName = "博德之门3", DurationMinutes = 120, SessionCount = 2 },
            new() { Date = "2026-07-10", GameId = 2, GameName = "艾尔登法环", DurationMinutes = 60, SessionCount = 1 }
        };

        var result = StatsAggregator.AggregatePeriod(range, summaries, null);
        // Q3 2026 has 14 weekly intervals
        result.TrendPoints.Should().HaveCount(14);
        result.TrendPoints[0].FullDateTitle.Should().Be("7月1日–7月5日");
        result.TrendPoints[1].FullDateTitle.Should().Be("7月6日–7月12日");
        result.TrendPoints[1].DurationMinutes.Should().Be(180);
        result.TrendPoints[1].GameCount.Should().Be(2);
        result.TrendPoints[1].TooltipText.Should().Contain("7月6日–7月12日")
            .And.Contain("总游戏时长 3h")
            .And.Contain("游戏 2 款");
    }

    [Fact]
    public void AggregatePeriod_YearMode_ShouldGenerateExactly12MonthlyTrendPoints()
    {
        var range = StatsAggregator.ComputePeriodRange(new DateTime(2026, 9, 20), StatsPeriodMode.Year);
        var summaries = new List<DailySummary>
        {
            new() { Date = "2026-07-15", GameId = 1, GameName = "黑神话：悟空", DurationMinutes = 300, SessionCount = 3 }
        };

        var result = StatsAggregator.AggregatePeriod(range, summaries, null);
        result.TrendPoints.Should().HaveCount(12);
        result.TrendPoints[0].DateLabel.Should().Be("1月");
        result.TrendPoints[6].DateLabel.Should().Be("7月");
        result.TrendPoints[6].FullDateTitle.Should().Be("2026年7月");
        result.TrendPoints[6].DurationMinutes.Should().Be(300);
        result.TrendPoints[6].GameCount.Should().Be(1);
        result.TrendPoints[6].TooltipText.Should().Contain("2026年7月")
            .And.Contain("总游戏时长 5h")
            .And.Contain("游戏 1 款");
    }
}
