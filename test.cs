using System;
using System.Net.Http;
using System.Threading.Tasks;

class Program {
    static async Task Main() {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
        var url = "https://mods.factorio.com/download/Age-of-Production/6a4161fc1d736e221b3ac892?username=test&token=test";
        var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        Console.WriteLine((int)response.StatusCode);
        Console.WriteLine(await response.Content.ReadAsStringAsync());
    }
}
