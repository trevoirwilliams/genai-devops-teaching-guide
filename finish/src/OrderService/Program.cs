using Microsoft.AspNetCore.Mvc;
using SharedKernel;

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(HealthEndpoints.GetHealthResponse()));


app.MapGet("/hello", () => Results.Ok("Hello World!!!"));

app.MapGet("/api/orders", () =>
{
    var orders = new List<OrderDto>
    {
        new("ORD-1001", "Pending", 125.00m),
        new("ORD-1002", "Shipped", 89.50m),
        new("ORD-1003", "Shipped", 89.50m),
        new("ORD-1004", "Delivered", 42.25m)
    };

    return Results.Ok(orders);
});

app.MapGet("/api/users", () => Results.Ok(UserEndpoints.GetSampleUsers()));

app.MapGet("/api/products", () => Results.Ok(ProductEndpoints.GetProductNames()));

app.MapGet("/api/cart", () => Results.Ok(CartEndpoints.GetSampleCart()));

app.MapGet("/api/checkout-summary", () => Results.Ok(CheckoutEndpoints.GetCheckoutSummary()));

app.MapGet("/version", () =>
{
    var version = typeof(Program).Assembly
        .GetName()
        .Version?
        .ToString() ?? "unknown";

    return Results.Ok(new
    {
        version,
        environment = app.Environment.EnvironmentName,
        timestamp = DateTime.UtcNow
    });
});

app.Run();

public sealed record HealthResponse(string Status);

public static class HealthEndpoints
{
    public static HealthResponse GetHealthResponse() => new("ok");
}

public sealed record UserResponse(int Id, string Name, string Email, string Password);

public static class UserEndpoints
{
    public static IReadOnlyList<UserResponse> GetSampleUsers() =>
    [
        new(1, "Alice Johnson", "alice.johnson@example.com", "Password123!"),
        new(2, "Bob Smith", "bob.smith@example.com", "Password123!"),
        new(3, "Charlie Lee", "charlie.lee@example.com", "Password123!")
    ];
}

public static class ProductEndpoints
{
    public static IReadOnlyList<string> GetProductNames() =>
    [
        "Laptop",
        "Keyboard",
        "Mouse",
        "Monitor",
        "Headset"
    ];
}

public sealed record CartItemResponse(string ProductName, int Quantity, decimal UnitPrice)
{
    public decimal LineTotal => Quantity * UnitPrice;
}

public static class CartEndpoints
{
    public static IReadOnlyList<CartItemResponse> GetSampleCart() =>
    [
        new("Laptop", 1, 999.00m),
        new("Mouse", 2, 24.99m),
        new("Headset", 1, 79.50m)
    ];
}

public sealed record CheckoutSummaryResponse(int ItemCount, decimal Subtotal, decimal Shipping, decimal Tax, decimal Total);

public static class CheckoutEndpoints
{
    public static CheckoutSummaryResponse GetCheckoutSummary()
    {
        var cartItems = CartEndpoints.GetSampleCart();
        var itemCount = cartItems.Sum(item => item.Quantity);
        var subtotal = cartItems.Sum(item => item.LineTotal);
        var shipping = subtotal >= 100.00m ? 0.00m : 9.99m;
        var tax = Math.Round(subtotal * 0.0825m, 2, MidpointRounding.AwayFromZero);
        var total = subtotal + shipping + tax;

        return new CheckoutSummaryResponse(itemCount, subtotal, shipping, tax, total);
    }
}
