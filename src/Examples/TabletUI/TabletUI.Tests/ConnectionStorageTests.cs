using System.Text.Json;
using Microsoft.JSInterop;
using TabletUI.Services;

namespace TabletUI.Tests;

public sealed class ConnectionStorageTests
{
    [Theory]
    [InlineData("https://example.test/01234567-89ab-cdef-0123-456789abcdef")]
    [InlineData("http://localhost:5137/01234567-89ab-cdef-0123-456789abcdef")]
    [InlineData(" https://example.test/base/0123456789abcdef0123456789abcdef/ ")]
    [InlineData("https://example.test/%7B01234567-89ab-cdef-0123-456789abcdef%7D")]
    public void Valid_settings_preserve_raw_auth_and_normalize_feed(string feed)
    {
        const string auth = "  test-only raw key  ";
        Assert.True(ConnectionSettings.TryCreate(feed, auth, out var settings, out var error));
        Assert.NotNull(settings);
        Assert.Equal(1, settings.Version);
        Assert.Equal(auth, settings.RCE3_AUTH);
        Assert.Equal(new Uri(feed.Trim()).AbsoluteUri.TrimEnd('/'), settings.RCE3_FEED);
        Assert.Equal(string.Empty, error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a URL")]
    [InlineData("/01234567-89ab-cdef-0123-456789abcdef")]
    [InlineData("ftp://example.test/01234567-89ab-cdef-0123-456789abcdef")]
    [InlineData("https://user:password@example.test/01234567-89ab-cdef-0123-456789abcdef")]
    [InlineData("https://example.test/01234567-89ab-cdef-0123-456789abcdef?key=value")]
    [InlineData("https://example.test/01234567-89ab-cdef-0123-456789abcdef#fragment")]
    [InlineData("https://example.test/not-a-guid")]
    [InlineData("https://example.test/")]
    [InlineData("https://example.test/01234567-89ab-cdef-0123-456789abcdef/admin")]
    public void Invalid_feed_is_rejected(string feed)
    {
        Assert.False(ConnectionSettings.TryCreate(feed, "", out var settings, out var error));
        Assert.Null(settings);
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("key\rvalue")]
    [InlineData("key\nvalue")]
    public void Blank_or_multiline_auth_is_rejected(string escapedAuth)
    {
        var auth = escapedAuth;
        Assert.False(ConnectionSettings.TryCreate(ConnectionHarness.Feed, auth, out var settings, out var error));
        Assert.Null(settings);
        Assert.Contains("authorization", error);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Empty_auth_is_valid_for_an_open_feed()
    {
        Assert.True(ConnectionSettings.TryCreate(ConnectionHarness.Feed, "", out var settings, out _));
        Assert.Equal("", settings!.RCE3_AUTH);
    }

    [Fact]
    public async Task Missing_storage_is_not_an_error()
    {
        var js = new StorageJsRuntime();
        var loaded = await new ConnectionStorage(js).LoadAsync();
        Assert.Null(loaded.Settings);
        Assert.Null(loaded.Error);
        Assert.Equal("localStorage.getItem", Assert.Single(js.Calls).Identifier);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  test-only persisted raw key  ")]
    public async Task Save_persists_versioned_settings_and_new_service_loads_them(string auth)
    {
        var js = new StorageJsRuntime();
        var settings = new ConnectionSettings(1, ConnectionHarness.Feed, auth);
        Assert.Null(await new ConnectionStorage(js).SaveAsync(settings));
        var call = Assert.Single(js.Calls);
        Assert.Equal("localStorage.setItem", call.Identifier);
        Assert.Equal(ConnectionStorage.StorageKey, call.Arguments[0]);
        using var json = JsonDocument.Parse(Assert.IsType<string>(call.Arguments[1]));
        Assert.Equal(1, json.RootElement.GetProperty("Version").GetInt32());
        Assert.Equal(settings.RCE3_FEED, json.RootElement.GetProperty("RCE3_FEED").GetString());
        Assert.Equal(auth, json.RootElement.GetProperty("RCE3_AUTH").GetString());
        var loaded = await new ConnectionStorage(js).LoadAsync();
        Assert.Equal(settings, loaded.Settings);
        Assert.Null(loaded.Error);
        Assert.All(js.Calls, c => Assert.Equal(ConnectionStorage.StorageKey, c.Arguments[0]));
    }

    [Fact]
    public async Task Forget_removes_only_connection_and_next_load_is_empty()
    {
        var js = new StorageJsRuntime();
        js.Items["unrelated"] = "keep";
        var storage = new ConnectionStorage(js);
        await storage.SaveAsync(new(1, ConnectionHarness.Feed, "test-only key"));
        Assert.Null(await storage.ForgetAsync());
        Assert.Equal("keep", js.Items["unrelated"]);
        Assert.False(js.Items.ContainsKey(ConnectionStorage.StorageKey));
        Assert.Equal("localStorage.removeItem", js.Calls[1].Identifier);
        Assert.Null((await storage.LoadAsync()).Settings);
        Assert.Null(await storage.ForgetAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"Version\":2,\"RCE3_FEED\":\"FEED\",\"RCE3_AUTH\":\"\"}")]
    [InlineData("{\"Version\":1,\"RCE3_FEED\":null,\"RCE3_AUTH\":\"\"}")]
    [InlineData("{\"Version\":1,\"RCE3_FEED\":\"FEED\",\"RCE3_AUTH\":null}")]
    [InlineData("{\"Version\":1,\"RCE3_FEED\":\"invalid\",\"RCE3_AUTH\":\"\"}")]
    [InlineData("{\"Version\":1,\"RCE3_FEED\":\"FEED\",\"RCE3_AUTH\":\" \"}")]
    [InlineData("{\"Version\":1,\"RCE3_FEED\":\"FEED\",\"RCE3_AUTH\":\"key\\nvalue\"}")]
    public async Task Corrupt_or_invalid_saved_settings_are_not_used_or_silently_overwritten(string value)
    {
        var js = new StorageJsRuntime();
        value = value.Replace("\"FEED\"", JsonSerializer.Serialize(ConnectionHarness.Feed));
        js.Items[ConnectionStorage.StorageKey] = value;
        var loaded = await new ConnectionStorage(js).LoadAsync();
        Assert.Null(loaded.Settings);
        Assert.Contains("invalid", loaded.Error);
        Assert.Equal(value, js.Items[ConnectionStorage.StorageKey]);
        Assert.Equal("localStorage.getItem", Assert.Single(js.Calls).Identifier);
    }

    [Fact]
    public async Task Unavailable_storage_returns_useful_errors_without_exposing_js_details()
    {
        const string secret = "test-only private JS exception";
        var storage = new ConnectionStorage(new StorageJsRuntime { Failure = new JSException(secret) });
        var loaded = await storage.LoadAsync();
        Assert.Null(loaded.Settings);
        Assert.Contains("unavailable", loaded.Error);
        var saveError = await storage.SaveAsync(new(1, ConnectionHarness.Feed, "test-only key"));
        Assert.Contains("could not save", saveError);
        var forgetError = await storage.ForgetAsync();
        Assert.Contains("could not be removed", forgetError);
        Assert.DoesNotContain(secret, loaded.Error + saveError + forgetError);
    }
}

internal sealed class StorageJsRuntime : IJSRuntime
{
    public Dictionary<string, string> Items { get; } = [];
    public List<(string Identifier, object?[] Arguments)> Calls { get; } = [];
    public JSException? Failure { get; init; }

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
        InvokeAsync<TValue>(identifier, CancellationToken.None, args);

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args)
    {
        token.ThrowIfCancellationRequested();
        var arguments = args ?? [];
        Calls.Add((identifier, arguments));
        if (Failure is not null) { return ValueTask.FromException<TValue>(Failure); }
        var key = Assert.IsType<string>(arguments[0]);
        object? result = null;
        switch (identifier)
        {
            case "localStorage.getItem":
                Assert.Single(arguments);
                result = Items.GetValueOrDefault(key);
                break;
            case "localStorage.setItem":
                Assert.Equal(2, arguments.Length);
                Items[key] = Assert.IsType<string>(arguments[1]);
                break;
            case "localStorage.removeItem":
                Assert.Single(arguments);
                Items.Remove(key);
                break;
            default: throw new InvalidOperationException($"Unexpected JS call: {identifier}");
        }
        return ValueTask.FromResult(result is null ? default! : (TValue)result);
    }
}
