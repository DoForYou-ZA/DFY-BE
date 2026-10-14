using DoForYou.API.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace DoForYou.Tests;

public class RunnerPayoutCalculationTests
{
    [Theory]
    [InlineData(200.00, 30.00)]
    [InlineData(100.03, 15.00)]
    [InlineData(50.00, 7.50)]
    public void CalculateCommission_RoundsToTwoCurrencyDecimals(decimal budget, decimal expectedCommission)
    {
        var service = new EscrowService(null!, NullLogger<EscrowService>.Instance);

        Assert.Equal(expectedCommission, service.CalculateCommission(budget));
    }

    [Fact]
    public void CreatorBudgetOfTwoHundred_LeavesRunnerPayoutOfOneHundredSeventy()
    {
        var service = new EscrowService(null!, NullLogger<EscrowService>.Instance);
        var creatorBudget = 200.00m;

        var commission = service.CalculateCommission(creatorBudget);
        var runnerPayout = decimal.Round(creatorBudget - commission, 2, MidpointRounding.AwayFromZero);

        Assert.Equal(30.00m, commission);
        Assert.Equal(170.00m, runnerPayout);
    }
}
