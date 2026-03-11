using Xunit;

namespace OrderService.Tests;

public sealed class UserEndpointsTests
{
    [Fact]
    public void GetSampleUsers_ReturnsExpectedUsers()
    {
        var users = UserEndpoints.GetSampleUsers();

        Assert.NotNull(users);
        Assert.Equal(3, users.Count);

        var firstUser = users[0];
        Assert.Equal(1, firstUser.Id);
        Assert.Equal("Alice Johnson", firstUser.Name);
        Assert.Equal("alice.johnson@example.com", firstUser.Email);
    }
}
