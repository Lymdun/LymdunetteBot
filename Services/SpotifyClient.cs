using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using LymdunetteBot.Models;
using Microsoft.Extensions.Configuration;

namespace LymdunetteBot.Services;

internal interface ISpotifySearch {
    Task<IReadOnlyList<SpotifyTrack>> SearchTracksAsync(string query, int offset, CancellationToken cancellationToken);
    Task<IReadOnlyList<SpotifyArtist>> SearchArtistsAsync(string query, CancellationToken cancellationToken);
}

/// <summary>
/// Minimal Spotify Web API client using the client-credentials flow (no user login).
/// Requires SPOTIFY_CLIENT_ID and SPOTIFY_CLIENT_SECRET in the environment / .env file.
/// </summary>
public sealed class SpotifyClient(HttpClient httpClient, IConfiguration configuration) : ISpotifySearch {
    const string TOKEN_URL = "https://accounts.spotify.com/api/token";
    const string API_URL = "https://api.spotify.com/v1";
    const string MARKET = "FR";

    // Development-mode Spotify apps cannot request more than 10 search results per page.
    public const int SEARCH_PAGE_SIZE = 10;

    readonly string? clientId = configuration["SPOTIFY_CLIENT_ID"];
    readonly string? clientSecret = configuration["SPOTIFY_CLIENT_SECRET"];
    readonly SemaphoreSlim tokenLock = new(1, 1);
    string? accessToken;
    DateTimeOffset accessTokenExpiry;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(clientId) && !string.IsNullOrWhiteSpace(clientSecret);

    public async Task<IReadOnlyList<SpotifyTrack>> SearchTracksAsync(string query, int offset, CancellationToken cancellationToken) {
        SpotifySearchResponse? response = await SearchAsync(query, "track", offset, cancellationToken);
        return response?.Tracks?.Items?.OfType<SpotifyTrack>().ToList() ?? [];
    }

    public async Task<IReadOnlyList<SpotifyArtist>> SearchArtistsAsync(string query, CancellationToken cancellationToken) {
        SpotifySearchResponse? response = await SearchAsync(query, "artist", 0, cancellationToken);
        return response?.Artists?.Items?.OfType<SpotifyArtist>().ToList() ?? [];
    }

    async Task<SpotifySearchResponse?> SearchAsync(string query, string type, int offset, CancellationToken cancellationToken) {
        string url = $"{API_URL}/search?q={Uri.EscapeDataString(query)}&type={type}&market={MARKET}"
                     + $"&limit={SEARCH_PAGE_SIZE}&offset={offset}";

        for (int attempt = 0; ; attempt++) {
            string token = await GetAccessTokenAsync(forceRefresh: attempt > 0, cancellationToken);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                continue; // token revoked or expired early: refresh once

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<SpotifySearchResponse>(cancellationToken);
        }
    }

    async Task<string> GetAccessTokenAsync(bool forceRefresh, CancellationToken cancellationToken) {
        if (!IsConfigured)
            throw new InvalidOperationException("Spotify credentials are not configured.");

        await tokenLock.WaitAsync(cancellationToken);
        try {
            if (!forceRefresh && accessToken != null && DateTimeOffset.UtcNow < accessTokenExpiry)
                return accessToken;

            using var request = new HttpRequestMessage(HttpMethod.Post, TOKEN_URL) {
                Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("grant_type", "client_credentials")])
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}")));

            using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            SpotifyTokenResponse? token = await response.Content.ReadFromJsonAsync<SpotifyTokenResponse>(cancellationToken);
            if (string.IsNullOrEmpty(token?.AccessToken))
                throw new InvalidOperationException("Spotify returned an empty access token.");

            accessToken = token.AccessToken;
            accessTokenExpiry = DateTimeOffset.UtcNow.AddSeconds(Math.Max(token.ExpiresIn - 60, 30));
            return accessToken;
        } finally {
            tokenLock.Release();
        }
    }
}
