using System.Text.Json;
using Microsoft.JSInterop;

namespace TabletUI.Services;

public sealed record StorageReadResult(ConnectionSettings? Settings, string? Error);

public sealed class ConnectionStorage(IJSRuntime js)
{
    public const string StorageKey = "TabletUI.connection.v1";

    public async Task<StorageReadResult> LoadAsync()
    {
        try
        {
            var value = await js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            if (value is null)
            {
                return new(null, null);
            }

            var saved = JsonSerializer.Deserialize<ConnectionSettings>(value);
            if (saved is { Version: 1, RCE3_FEED: not null, RCE3_AUTH: not null }
                && ConnectionSettings.TryCreate(saved.RCE3_FEED, saved.RCE3_AUTH, out var settings, out _))
            {
                return new(settings, null);
            }

            return new(null, "Saved connection is invalid. Enter the connection again or forget it.");
        }
        catch (JsonException)
        {
            return new(null, "Saved connection is invalid. Enter the connection again or forget it.");
        }
        catch (JSException)
        {
            return new(null, "Browser storage is unavailable. You can still connect for this session.");
        }
    }

    public async Task<string?> SaveAsync(ConnectionSettings settings)
    {
        try
        {
            await js.InvokeVoidAsync("localStorage.setItem", StorageKey, JsonSerializer.Serialize(settings));
            return null;
        }
        catch (JSException)
        {
            return "Connection works for this session, but the browser could not save it.";
        }
    }

    public async Task<string?> ForgetAsync()
    {
        try
        {
            await js.InvokeVoidAsync("localStorage.removeItem", StorageKey);
            return null;
        }
        catch (JSException)
        {
            return "Disconnected, but saved settings could not be removed. Clear this site's data in browser settings.";
        }
    }
}
