using System.Net.Http;

namespace FactorioLibrary.Internal;

internal static class Shared
{
    private static readonly Lazy<HttpClient> httpClient = new(() =>
    {
        HttpClient client = new();
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/148.0.0.0 Safari/537.36");
        return client;
    });

    internal static HttpClient HttpClient => httpClient.Value;
}