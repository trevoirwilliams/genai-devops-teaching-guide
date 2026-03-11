using Xunit;

namespace OrderService.Tests;

public sealed class CheckoutEndpointsTests
{
    [Fact]
    public void GetCheckoutSummary_ReturnsExpectedTotals()
    {
        var summary = CheckoutEndpoints.GetCheckoutSummary();

        Assert.Equal(4, summary.ItemCount);
        Assert.Equal(1128.48m, summary.Subtotal);
        Assert.Equal(0.00m, summary.Shipping);
        Assert.Equal(93.10m, summary.Tax);
        Assert.Equal(1221.58m, summary.Total);
    }
}
