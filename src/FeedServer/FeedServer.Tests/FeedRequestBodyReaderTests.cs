using System.Text;
using FeedServer;

namespace FeedServer.Tests;

public class FeedRequestBodyReaderTests
{
    [Fact]
    public async Task ReadAsync_AllowsBodyAtConfiguredLimit()
    {
        using var body = new MemoryStream(Encoding.UTF8.GetBytes("12345"));

        var read = await FeedRequestBodyReader.ReadAsync(body, maxBytes: 5, CancellationToken.None);

        Assert.Equal("12345", Encoding.UTF8.GetString(read));
    }

    [Fact]
    public async Task ReadAsync_RejectsBodyOverConfiguredLimit()
    {
        using var body = new MemoryStream(Encoding.UTF8.GetBytes("123456"));

        await Assert.ThrowsAsync<MessageTooLargeException>(
            () => FeedRequestBodyReader.ReadAsync(body, maxBytes: 5, CancellationToken.None));
    }

    [Fact]
    public void OptionsRejectUnsafeBodyLimits()
    {
        Assert.False(FeedServerOptions.IsValid(new FeedServerOptions
        {
            MaxMessageSizeBytes = 0
        }));

        Assert.False(FeedServerOptions.IsValid(new FeedServerOptions
        {
            MaxMessageSizeBytes = (long)int.MaxValue + 1
        }));
    }

    [Fact]
    public void OptionsRejectUnsafeSlowClientLimits()
    {
        Assert.False(FeedServerOptions.IsValid(new FeedServerOptions
        {
            RequestHeadersTimeout = TimeSpan.Zero
        }));

        Assert.False(FeedServerOptions.IsValid(new FeedServerOptions
        {
            MinRequestBodyDataRateBytesPerSecond = 0
        }));

        Assert.False(FeedServerOptions.IsValid(new FeedServerOptions
        {
            MinRequestBodyDataRateGracePeriod = TimeSpan.Zero
        }));
    }
}
