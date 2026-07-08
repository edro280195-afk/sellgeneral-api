using EntregasApi.Services;
using Xunit;

namespace EntregasApi.Tests;

public class TandaWeekCalculatorTests
{
    [Theory]
    [InlineData(-2, 1)]
    [InlineData(0, 1)]
    [InlineData(6, 1)]
    [InlineData(7, 2)]
    [InlineData(13, 2)]
    [InlineData(14, 3)]
    public void CalculateCurrentWeek_UsesSevenDayWindowsFromStartDate(
        int daysAfterStart,
        int expectedWeek)
    {
        var startDate = new DateTime(2026, 7, 1, 15, 30, 0, DateTimeKind.Utc);
        var now = startDate.Date.AddDays(daysAfterStart).AddHours(10);

        var currentWeek = TandaWeekCalculator.CalculateCurrentWeek(startDate, now);

        Assert.Equal(expectedWeek, currentWeek);
    }

    [Fact]
    public void CalculateClampedCurrentWeek_DoesNotExceedTotalWeeks()
    {
        var startDate = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        var now = startDate.AddDays(70);

        var currentWeek = TandaWeekCalculator.CalculateClampedCurrentWeek(
            startDate,
            totalWeeks: 4,
            utcNow: now);

        Assert.Equal(4, currentWeek);
    }
}
