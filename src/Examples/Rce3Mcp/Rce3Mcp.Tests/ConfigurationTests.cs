using Microsoft.Extensions.Configuration;

namespace Rce3Mcp.Tests;

public sealed class ConfigurationTests
{
    private const string Feed = "http://localhost:5137/00000000-0000-0000-0000-000000000000";
    private static IConfiguration Config(params (string Key, string? Value)[] values) => new ConfigurationBuilder()
        .AddInMemoryCollection(values.Select(pair => new KeyValuePair<string, string?>(pair.Key, pair.Value))).Build();

    [Fact]
    public void LoadsSharedVariablesAndPreservesRawAuthorization()
    {
        var connection = FeedConnection.Load(Config(("RCE3_FEED", Feed + "/"), ("RCE3_AUTH", "raw test value")));
        Assert.Equal(Feed, connection.FeedUri.AbsoluteUri);
        Assert.Equal("raw test value", connection.Authorization);
        Assert.Null(FeedConnection.Load(Config(("RCE3_FEED", Feed), ("RCE3_AUTH", ""))).Authorization);
        Assert.Null(FeedConnection.Load(Config(("RCE3_FEED", Feed))).Authorization);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://localhost/not-a-feed")]
    [InlineData("ftp://localhost/00000000-0000-0000-0000-000000000000")]
    [InlineData(Feed + "?key=secret")]
    [InlineData(Feed + "#fragment")]
    [InlineData("http://user:secret@localhost/00000000-0000-0000-0000-000000000000")]
    public void RejectsInvalidFeedWithoutEchoingValue(string? value)
    {
        var error = Assert.Throws<ArgumentException>(() => FeedConnection.Load(Config(("RCE3_FEED", value))));
        Assert.DoesNotContain("secret", error.Message);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("secret\r\ninjected")]
    public void RejectsInvalidAuthorizationWithoutEchoingValue(string value)
    {
        var error = Assert.Throws<ArgumentException>(() => FeedConnection.Load(Config(("RCE3_FEED", Feed), ("RCE3_AUTH", value))));
        Assert.DoesNotContain("secret", error.Message);
    }

    [Fact]
    public void LimitsHaveDefaultsAndSupportConfiguration()
    {
        var defaults = McpOptions.Load(Config());
        Assert.Equal(100, defaults.HistoryCapacity);
        Assert.Equal(8 * 1024 * 1024, defaults.MaxHistoryBytes);
        Assert.Equal(1024 * 1024, defaults.MaxMessageSizeBytes);
        var options = McpOptions.Load(Config(("Rce3Mcp:HistoryCapacity", "5"), ("Rce3Mcp:MaxHistoryBytes", "20"), ("Rce3Mcp:MaxMessageSizeBytes", "10")));
        Assert.Equal(5, options.HistoryCapacity);
        Assert.Equal(20, options.MaxHistoryBytes);
        Assert.Equal(10, options.MaxMessageSizeBytes);
    }

    [Theory]
    [InlineData("HistoryCapacity", "0")]
    [InlineData("MaxHistoryBytes", "0")]
    [InlineData("MaxMessageSizeBytes", "0")]
    [InlineData("MaxMessageSizeBytes", "999999999999")]
    [InlineData("HistoryCapacity", "not-an-integer")]
    public void RejectsInvalidLimits(string key, string value) => Assert.Throws<ArgumentException>(() => McpOptions.Load(Config(("Rce3Mcp:" + key, value))));

    [Fact]
    public void KestrelEndpointConfigurationCannotBypassLoopbackRestriction()
    {
        Assert.Throws<ArgumentException>(() => Program.CreateApplication([], builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RCE3_FEED"] = Feed,
                ["urls"] = "http://localhost:5138",
                ["Kestrel:Endpoints:Public:Url"] = "http://0.0.0.0:5138"
            });
        }));
    }

    [Theory]
    [InlineData("http://0.0.0.0:5138")]
    [InlineData("http://example.com:5138")]
    [InlineData("http://localhost:5138/path")]
    [InlineData("http://localhost:5138?query")]
    public void RejectsNonLocalOrMalformedListenerBeforeReadingIdentity(string url)
    {
        Assert.Throws<ArgumentException>(() => Program.CreateApplication([], builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["RCE3_FEED"] = Feed, ["urls"] = url });
        }));
    }
}
