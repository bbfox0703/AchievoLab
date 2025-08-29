using System;
using System.Net.Http;

namespace CommonUtilities;

/// <summary>
/// Provides a shared <see cref="HttpClient"/> instance for the entire application.
/// </summary>
public static class HttpClientProvider
{
    private static readonly Lazy<HttpClient> _client = new(() =>
    {
        var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        if (!http.DefaultRequestHeaders.Contains("User-Agent"))
        {
            http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        }
        return http;
    });

    /// <summary>
    /// Gets the shared <see cref="HttpClient"/> instance.
    /// </summary>
    public static HttpClient Shared => _client.Value;
}

