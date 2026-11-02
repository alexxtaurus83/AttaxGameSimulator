using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Attax.Server.Contracts.Serialization;
using Newtonsoft.Json;

namespace Attax.UserSimulator;

public static class ServerJson
{
    public static readonly JsonSerializerSettings Settings = AttaxJsonSettings.Create();
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static StringContent ToJsonContent(object? value)
    {
        var json = value == null ? "{}" : JsonConvert.SerializeObject(value, Settings);
        return new StringContent(json, Utf8NoBom, "application/json");
    }

    public static async Task<T?> ReadJsonAsync<T>(HttpContent content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var json = await content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }
        return JsonConvert.DeserializeObject<T>(json, Settings);
    }

    public static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        return await ReadJsonAsync<T>(response.Content, ct);
    }

    public static async Task<HttpResponseMessage> PostJsonAsync(
        HttpClient client,
        string requestUri,
        object? value,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        using var content = ToJsonContent(value);
        return await client.PostAsync(requestUri, content, ct);
    }
}
