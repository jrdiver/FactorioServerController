namespace FactorioLibrary.Internal;

internal static class Shared
{
    private static readonly Lazy<HttpClient> LazyHttpClient = new(() =>
    {
        HttpClient client = new();
        client.DefaultRequestHeaders.Add("User-Agent", "Jrdiver's Factorio Server Controller");
        return client;
    });

    internal static HttpClient HttpClient => LazyHttpClient.Value;
}