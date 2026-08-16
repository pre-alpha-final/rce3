using FeedServer;
using Microsoft.AspNetCore.Http;

namespace FeedServer.Tests;

public class FeedAuthorizationKeyTests
{
    [Fact]
    public void TryRead_RejectsPresentEmptyAuthorizationValue()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = string.Empty;

        var succeeded = FeedAuthorizationKey.TryRead(context.Request, out var key, out var problem);

        Assert.False(succeeded);
        Assert.Null(key);
        Assert.Contains("must not be empty", problem, StringComparison.OrdinalIgnoreCase);
    }
}
