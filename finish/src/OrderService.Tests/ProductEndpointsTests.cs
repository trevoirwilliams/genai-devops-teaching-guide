using Xunit;

namespace OrderService.Tests;

public sealed class ProductEndpointsTests
{
    [Fact]
    public void GetProductNames_ReturnsExpectedProducts()
    {
        var products = ProductEndpoints.GetProductNames();

        Assert.NotNull(products);
        Assert.Equal(5, products.Count);
        Assert.Equal("Laptop", products[0]);
        Assert.Equal("Keyboard", products[1]);
        Assert.Equal("Mouse", products[2]);
        Assert.Equal("Monitor", products[3]);
        Assert.Equal("Headset", products[4]);
    }
}
