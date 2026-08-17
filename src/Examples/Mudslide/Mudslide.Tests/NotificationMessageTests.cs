using System.Text;

namespace Mudslide.Tests;

public class NotificationMessageTests
{
    [Fact]
    public void ExactPrefixReturnsRemainingMultilineText()
    {
        var body = Encoding.UTF8.GetBytes("notify: first\r\nsecond");

        Assert.True(NotificationMessage.TryParse(body, out var notification));
        Assert.Equal("first\r\nsecond", notification);
    }

    [Theory]
    [InlineData("Notify: text")]
    [InlineData(" notify: text")]
    [InlineData("notify:text")]
    [InlineData("notify:")]
    [InlineData("notify: ")]
    [InlineData("ordinary text")]
    public void NonMatchingMessagesAreIgnored(string message)
    {
        Assert.False(NotificationMessage.TryParse(Encoding.UTF8.GetBytes(message), out var notification));
        Assert.Empty(notification);
    }

    [Fact]
    public void MalformedUtf8IsIgnored()
    {
        Assert.False(NotificationMessage.TryParse([0xc3, 0x28], out var notification));
        Assert.Empty(notification);
    }

    [Fact]
    public void MudslideTextNormalizesEveryLineEnding()
    {
        Assert.Equal(
            "first\\nsecond\\nthird\\nfourth",
            NotificationMessage.ToMudslideText("first\r\nsecond\rthird\nfourth"));
    }

    [Fact]
    public void UnicodeIsPreserved()
    {
        var body = Encoding.UTF8.GetBytes("notify: Zażółć gęślą jaźń");

        Assert.True(NotificationMessage.TryParse(body, out var notification));
        Assert.Equal("Zażółć gęślą jaźń", notification);
    }
}
