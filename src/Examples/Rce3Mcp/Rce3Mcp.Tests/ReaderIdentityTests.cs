namespace Rce3Mcp.Tests;

public sealed class ReaderIdentityTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("rce3-reader-").FullName;

    [Fact]
    public void SavedIdentitySurvivesReloadAndSeparateInstallationsDiffer()
    {
        var id = ReaderIdentity.LoadOrCreate(_directory);
        Assert.NotEqual(Guid.Empty, id);
        Assert.Equal(id.ToString("D"), File.ReadAllText(Path.Combine(_directory, "readerid")));
        Assert.Equal(id, ReaderIdentity.LoadOrCreate(_directory));
        var other = Directory.CreateDirectory(Path.Combine(_directory, "other")).FullName;
        Assert.NotEqual(id, ReaderIdentity.LoadOrCreate(other));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a guid")]
    public void InvalidIdentityIsNotOverwritten(string value)
    {
        var path = Path.Combine(_directory, "readerid");
        File.WriteAllText(path, value);
        Assert.Throws<InvalidDataException>(() => ReaderIdentity.LoadOrCreate(_directory));
        Assert.Equal(value, File.ReadAllText(path));
    }

    [Fact]
    public void InaccessibleIdentityDoesNotFallBackToANewId()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "readerid"));
        Assert.Throws<UnauthorizedAccessException>(() => ReaderIdentity.LoadOrCreate(_directory));
    }

    [Fact]
    public async Task ConcurrentFirstStartsUseTheSameCompleteIdentity()
    {
        var ids = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => ReaderIdentity.LoadOrCreate(_directory))));
        Assert.All(ids, id => Assert.Equal(ids[0], id));
        Assert.Equal(ids[0], ReaderIdentity.LoadOrCreate(_directory));
        Assert.Single(Directory.GetFiles(_directory));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
