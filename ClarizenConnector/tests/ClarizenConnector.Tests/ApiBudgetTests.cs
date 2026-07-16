using ClarizenConnector.Clarizen;

namespace ClarizenConnector.Tests;

public class ApiBudgetTests
{
    [Fact]
    public void Consume_DecrementsRemaining()
    {
        var now = new DateTime(2026, 7, 12, 10, 0, 0, DateTimeKind.Utc);
        var budget = new ApiBudget(10, callsPerMinute: 6000, utcNow: () => now);
        Assert.Equal(10, budget.Remaining);
        budget.Consume();
        budget.Consume();
        Assert.Equal(8, budget.Remaining);
        Assert.Equal(2, budget.Used);
    }

    [Fact]
    public void Consume_ExhaustedBudget_Throws()
    {
        var now = new DateTime(2026, 7, 12, 10, 0, 0, DateTimeKind.Utc);
        var budget = new ApiBudget(3, callsPerMinute: 6000, utcNow: () => now);
        budget.Consume();
        budget.Consume();
        budget.Consume();
        Assert.Throws<ClarizenQuotaExceededException>(() => budget.Consume());
    }

    [Fact]
    public void Budget_ResetsAtUtcMidnight()
    {
        var now = new DateTime(2026, 7, 12, 23, 59, 0, DateTimeKind.Utc);
        var budget = new ApiBudget(2, callsPerMinute: 6000, utcNow: () => now);
        budget.Consume();
        budget.Consume();
        Assert.False(budget.HasBudget());

        now = new DateTime(2026, 7, 13, 0, 1, 0, DateTimeKind.Utc);  // next UTC day
        Assert.True(budget.HasBudget());
        Assert.Equal(2, budget.Remaining);
        budget.Consume();  // must not throw
        Assert.Equal(1, budget.Remaining);
    }

    [Fact]
    public void ResetsAtUtc_IsNextMidnight()
    {
        var now = new DateTime(2026, 7, 12, 15, 30, 0, DateTimeKind.Utc);
        var budget = new ApiBudget(100, utcNow: () => now);
        Assert.Equal(new DateTime(2026, 7, 13, 0, 0, 0), budget.ResetsAtUtc);
    }

    [Fact]
    public void MinInterval_FromCallsPerMinute()
    {
        var budget = new ApiBudget(100_000, callsPerMinute: 120);
        Assert.Equal(0.5, budget.MinInterval.TotalSeconds, 6);
    }

    [Fact]
    public void Consume_PacesBurstsBeyondPerMinuteRate()
    {
        var now = new DateTime(2026, 7, 12, 10, 0, 0, DateTimeKind.Utc);
        var budget = new ApiBudget(1000, callsPerMinute: 60, utcNow: () => now);

        var first = budget.Consume();
        Assert.Equal(TimeSpan.Zero, first);

        // Second immediate call must be pushed out by ~1s (60/min).
        var second = budget.Consume();
        Assert.Equal(1.0, second.TotalSeconds, 3);
    }

    [Fact]
    public void HasBudget_ChecksRequestedAmount()
    {
        var now = new DateTime(2026, 7, 12, 10, 0, 0, DateTimeKind.Utc);
        var budget = new ApiBudget(5, callsPerMinute: 6000, utcNow: () => now);
        budget.Consume();
        Assert.True(budget.HasBudget(4));
        Assert.False(budget.HasBudget(5));
    }
}
