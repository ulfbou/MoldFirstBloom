using System.Text.Json;
using Microsoft.JSInterop;

namespace Mold.App.Services;

public sealed class BrowserStorage(IJSRuntime js)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async ValueTask<T?> GetAsync<T>(string key)
    {
        try
        {
            var json = await js.InvokeAsync<string?>("moldStorage.get", key);
            return string.IsNullOrWhiteSpace(json)
                ? default
                : JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (Exception exception) when (exception is JSException or JsonException)
        {
            return default;
        }
    }

    public async ValueTask SetAsync<T>(string key, T value)
    {
        try
        {
            var json = JsonSerializer.Serialize(value, JsonOptions);
            await js.InvokeVoidAsync("moldStorage.set", key, json);
        }
        catch (JSException)
        {
            // Persistence is a platform adapter. Failure must not alter gameplay.
        }
    }
}
