namespace Youtube.Tests;

public class YoutubeOptionsTests
{
    private const string ArgumentFeed = "https://example.test/7d3e84f2-21be-4d69-beb6-52c65f0d55a6";
    private const string EnvironmentFeed = "https://example.test/17639bf4-d760-4e76-ab0e-f7cc1cd6f14c";

    [Fact]
    public void ArgumentsOverrideEnvironmentVariables()
    {
        var environment = EnvironmentValues(EnvironmentFeed, "environment-key");

        var succeeded = YoutubeOptions.TryCreate(
            [ArgumentFeed, "argument-key"],
            environment,
            out var options,
            out var problem);

        Assert.True(succeeded, problem);
        Assert.Equal(ArgumentFeed, options!.FeedUri.AbsoluteUri);
        Assert.Equal("argument-key", options.Authorization);
    }

    [Fact]
    public void MissingArgumentsFallBackIndependently()
    {
        var environment = EnvironmentValues(EnvironmentFeed, "environment-key");

        Assert.True(YoutubeOptions.TryCreate([], environment, out var environmentOptions, out var firstProblem), firstProblem);
        Assert.Equal(EnvironmentFeed, environmentOptions!.FeedUri.AbsoluteUri);
        Assert.Equal("environment-key", environmentOptions.Authorization);

        Assert.True(YoutubeOptions.TryCreate([ArgumentFeed], environment, out var mixedOptions, out var secondProblem), secondProblem);
        Assert.Equal(ArgumentFeed, mixedOptions!.FeedUri.AbsoluteUri);
        Assert.Equal("environment-key", mixedOptions.Authorization);
    }

    [Fact]
    public void EmptyAuthorizationArgumentSelectsOpenFeed()
    {
        var environment = EnvironmentValues(EnvironmentFeed, "environment-key");

        var succeeded = YoutubeOptions.TryCreate(
            [ArgumentFeed, string.Empty],
            environment,
            out var options,
            out var problem);

        Assert.True(succeeded, problem);
        Assert.Null(options!.Authorization);
    }

    [Fact]
    public void TrailingSlashIsRemoved()
    {
        var succeeded = YoutubeOptions.TryCreate(
            [ArgumentFeed + "/"],
            _ => null,
            out var options,
            out var problem);

        Assert.True(succeeded, problem);
        Assert.Equal(ArgumentFeed, options!.FeedUri.AbsoluteUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-url")]
    [InlineData("ftp://example.test/7d3e84f2-21be-4d69-beb6-52c65f0d55a6")]
    [InlineData("https://example.test/not-a-guid")]
    [InlineData("https://example.test/7d3e84f2-21be-4d69-beb6-52c65f0d55a6?key=value")]
    [InlineData("https://example.test/7d3e84f2-21be-4d69-beb6-52c65f0d55a6#fragment")]
    public void InvalidFeedIsRejected(string feed)
    {
        Assert.False(YoutubeOptions.TryCreate([feed], _ => null, out var options, out var problem));
        Assert.Null(options);
        Assert.NotEmpty(problem);
    }

    [Fact]
    public void ExcessArgumentsAreRejectedWithoutEchoingSecrets()
    {
        Assert.False(YoutubeOptions.TryCreate(
            [ArgumentFeed, "secret-key", "extra"],
            _ => null,
            out _,
            out var problem));

        Assert.DoesNotContain("secret-key", problem, StringComparison.Ordinal);
    }

    private static Func<string, string?> EnvironmentValues(string feed, string authorization)
    {
        return name => name switch
        {
            YoutubeOptions.FeedEnvironmentVariable => feed,
            YoutubeOptions.AuthorizationEnvironmentVariable => authorization,
            _ => null
        };
    }
}
