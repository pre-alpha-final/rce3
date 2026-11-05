using System.Text;

namespace Rce3Mcp.Tests;

public sealed class HistoryTests
{
    [Fact]
    public void TextBinaryAndMetadataArePreservedAndReadsDoNotConsume()
    {
        var time = new RetryTimeProvider();
        var history = new MessageHistory(new(), time);
        history.Add(Encoding.UTF8.GetBytes("hello\r\n世界"), "text/custom; charset=utf-8; mode=exact", 13);
        history.Add([0, 255, 128], "application/custom; mode=exact", 3);
        history.Add(null, "application/large", null);
        var page = history.Read();
        Assert.Equal(3, page.Messages.Count);
        Assert.Equal("hello\r\n世界", page.Messages[0].Body);
        Assert.Equal("text/custom; charset=utf-8; mode=exact", page.Messages[0].ContentType);
        Assert.Equal(time.GetUtcNow(), page.Messages[0].ReceivedAt);
        Assert.Equal("base64", page.Messages[1].Encoding);
        Assert.Equal(new byte[] { 0, 255, 128 }, Convert.FromBase64String(page.Messages[1].Body!));
        Assert.True(page.Messages[2].BodyOmitted);
        Assert.Null(page.Messages[2].ByteLength);
        Assert.Equal(page.Messages, history.Read().Messages);
    }

    [Fact]
    public void LatestWindowAndCursorPagesAreChronologicalAndIndependent()
    {
        var history = new MessageHistory(new(), TimeProvider.System);
        var start = history.Read().NextCursor;
        for (byte i = 0; i < 5; i++) history.Add([i], "application/octet-stream", 1);
        Assert.Equal(new[] { "\u0003", "\u0004" }, history.Read(limit: 2).Messages.Select(message => message.Body));
        var first = history.Read(start, 2);
        Assert.Equal(new[] { "\u0000", "\u0001" }, first.Messages.Select(message => message.Body));
        Assert.Equal(first.Messages, history.Read(start, 2).Messages);
        var second = history.Read(first.NextCursor, 2);
        Assert.Equal(new[] { "\u0002", "\u0003" }, second.Messages.Select(message => message.Body));
        var end = history.Read(second.NextCursor, 2);
        Assert.Single(end.Messages);
        Assert.Empty(history.Read(end.NextCursor).Messages);
        Assert.Equal(end.NextCursor, history.Read(end.NextCursor).NextCursor);
    }

    [Theory]
    [InlineData(2, 100)]
    [InlineData(100, 2)]
    public void CountAndByteBoundsEvictOldestAndReportGaps(int capacity, int bytes)
    {
        var history = new MessageHistory(new() { HistoryCapacity = capacity, MaxHistoryBytes = bytes, MaxMessageSizeBytes = 1 }, TimeProvider.System);
        var start = history.Read().NextCursor;
        for (byte i = 0; i < 4; i++) history.Add([i], "x/test", 1);
        var page = history.Read(start);
        Assert.Equal(2, page.Messages.Count);
        Assert.True(page.HistoryGap);
        Assert.True(history.Read().HistoryGap);
        Assert.Equal(2, history.Statistics().EvictedCount);
        Assert.False(history.Read(page.Messages[0].Cursor).HistoryGap);
    }

    [Fact]
    public void InvalidStaleFutureCursorsAndLimitsFailClearly()
    {
        var history = new MessageHistory(new(), TimeProvider.System);
        Assert.Contains("malformed", Assert.Throws<ArgumentException>(() => history.Read("bad")).Message);
        var stale = new MessageHistory(new(), TimeProvider.System).Read().NextCursor;
        Assert.Contains("restarted", Assert.Throws<ArgumentException>(() => history.Read(stale)).Message);
        var future = history.Read().NextCursor[..^1] + "1";
        Assert.Contains("ahead", Assert.Throws<ArgumentException>(() => history.Read(future)).Message);
        Assert.Throws<ArgumentException>(() => history.Read(limit: 0));
        Assert.Throws<ArgumentException>(() => history.Read(limit: 101));
    }

    [Fact]
    public async Task ConcurrentPublishingAndReadersGetConsistentSnapshots()
    {
        var history = new MessageHistory(new() { HistoryCapacity = 100 }, TimeProvider.System);
        var start = history.Read().NextCursor;
        await Task.WhenAll(Enumerable.Range(0, 100).Select(i => Task.Run(() =>
        {
            history.Add(Encoding.UTF8.GetBytes(i.ToString()), "text/plain", i.ToString().Length);
            var page = history.Read(start, 100);
            Assert.Equal(page.Messages.Count, page.Messages.Select(message => message.Cursor).Distinct().Count());
        })));
        Assert.Equal(100, history.Read(start, 100).Messages.Count);
        Assert.Equal(0, history.Statistics().EvictedCount);
    }
}
