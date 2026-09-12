using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.Tests;

public sealed class UiBatchCostBudgetTests
{
    [Fact]
    public void LargeHexSegments_AreLimitedToOnePerBatchEvenWith400ItemCatchUp()
    {
        var budget = new UiBatchCostBudget(32 * 1024);
        var taken = 0;
        while (taken < 400 && budget.HasRoom)
        {
            budget.Add(64 * 1024 * 3L);
            taken++;
        }
        Assert.Equal(1, taken);
    }

    [Fact]
    public void MixedItems_KeepOversizedItemWholeAndLeaveFollowingItemsForNextTick()
    {
        var budget = new UiBatchCostBudget(100);
        var costs = new long[] { 20, 30, 200, 10 };
        var taken = new List<long>();
        foreach (var cost in costs)
        {
            if (!budget.HasRoom) break;
            budget.Add(cost);
            taken.Add(cost);
        }
        Assert.Equal(new long[] { 20, 30, 200 }, taken);
    }

    [Fact]
    public void SmallPackets_StillAllowNormal40ItemBatch()
    {
        var budget = new UiBatchCostBudget(32 * 1024);
        for (var i = 0; i < 40; i++)
        {
            Assert.True(budget.HasRoom);
            budget.Add(100);
        }
    }

    [Fact]
    public void ExtremeCost_DoesNotOverflowAndFreshBudgetMakesProgress()
    {
        var budget = new UiBatchCostBudget(32 * 1024);
        budget.Add(long.MaxValue);
        Assert.False(budget.HasRoom);
        Assert.True(new UiBatchCostBudget(32 * 1024).HasRoom);
    }
}
