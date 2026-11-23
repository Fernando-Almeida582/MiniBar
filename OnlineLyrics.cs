using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace MeuPlayer;

internal static class OnlineLyrics
{
    private static readonly Lazy<HttpClient> Client = new(() =>
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            Timeout = TimeSpan.FromSeconds(7),
            MaxResponseContentBufferSize = 1024 * 1024
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MiniBar/1.5.3 (+https://github.com/Fernando-Almeida582/MiniBar)");
        return client;
    });

    internal record Result(
        [property: JsonPropertyName("syncedLyrics")] string? Synced,
        [property: JsonPropertyName("instrumental")] bool Instrumental);

    internal static Uri LookupUri(string title, string artist, string? album, double duration)
    {
        const string endpoint = "https://lrclib.net/api/get";
        string query = $"track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}";
        if (!string.IsNullOrWhiteSpace(album)) query += $"&album_name={Uri.EscapeDataString(album)}";
        if (duration is > 0 and <= 86_400)
            query += "&duration=" + Math.Round(duration).ToString(CultureInfo.InvariantCulture);
        return new Uri(endpoint + "?" + query);
    }

    internal static async Task<Result?> Find(string title, string artist, string? album, double duration, CancellationToken token)
    {
        using var response = await Client.Value.GetAsync(LookupUri(title, artist, album, duration), token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var content = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<Result>(content, cancellationToken: token).ConfigureAwait(false);
    }
}
