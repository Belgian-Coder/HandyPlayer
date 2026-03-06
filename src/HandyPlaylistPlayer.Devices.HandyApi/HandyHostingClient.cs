using System.Net.Http.Json;
using System.Security.Cryptography;
using HandyPlaylistPlayer.Core.Interfaces;
using HandyPlaylistPlayer.Devices.HandyApi.Models;
using Microsoft.Extensions.Logging;

namespace HandyPlaylistPlayer.Devices.HandyApi;

public class HandyHostingClient(
    IScriptCacheService cacheService,
    ILogger<HandyHostingClient> logger) : IScriptHostingService
{
    private static readonly HttpClient HostingHttpClient = new()
    {
        BaseAddress = new Uri("https://www.handyfeeling.com/api/hosting/v2/"),
        Timeout = TimeSpan.FromSeconds(30)
    };

    public async Task<(string Url, string Sha256)> UploadScriptAsync(string filePath, CancellationToken ct = default)
    {
        var fileBytes = await File.ReadAllBytesAsync(filePath, ct);
        var sha256 = ComputeSha256(fileBytes);

        // Check cache first
        var cachedUrl = await cacheService.GetUrlBySha256Async(sha256);
        if (cachedUrl != null)
        {
            logger.LogInformation("Script found in cache: {Sha256}", sha256);
            return (cachedUrl, sha256);
        }

        // Upload
        logger.LogInformation("Uploading script to hosting service: {Path}", filePath);

        using var content = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        content.Add(fileContent, "file", Path.GetFileName(filePath));

        using var request = new HttpRequestMessage(HttpMethod.Post, "upload") { Content = content };
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await HostingHttpClient.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("Script hosting upload failed: HTTP {Status} — {Body}", (int)response.StatusCode, errorBody);
            response.EnsureSuccessStatusCode();
        }

        var result = await response.Content.ReadFromJsonAsync<HandyHostingUploadResponse>(ct)
            ?? throw new InvalidOperationException("Empty hosting response");

        // Cache
        await cacheService.UpsertAsync(sha256, result.Url);
        logger.LogInformation("Script uploaded and cached: {Url}", result.Url);

        return (result.Url, sha256);
    }

    public async Task<string?> GetCachedUrlAsync(string sha256, CancellationToken ct = default)
    {
        return await cacheService.GetUrlBySha256Async(sha256);
    }

    private static string ComputeSha256(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexStringLower(hash);
    }
}
