using Xunit;

namespace OrderService.Tests;

public sealed class CartEndpointsTests
{
    [Fact]
    public void GetSampleCart_ReturnsExpectedItemsAndLineTotals()
    {
        var cartItems = CartEndpoints.GetSampleCart();

        Assert.NotNull(cartItems);
        Assert.Equal(3, cartItems.Count);

        var firstItem = cartItems[0];
        Assert.Equal("Laptop", firstItem.ProductName);
        Assert.Equal(1, firstItem.Quantity);
        Assert.Equal(999.00m, firstItem.UnitPrice);
        Assert.Equal(999.00m, firstItem.LineTotal);

        var secondItem = cartItems[1];
        Assert.Equal("Mouse", secondItem.ProductName);
        Assert.Equal(2, secondItem.Quantity);
        Assert.Equal(24.99m, secondItem.UnitPrice);
        Assert.Equal(49.98m, secondItem.LineTotal);
    }
}
