using FactorioSharp.Rcon;

namespace FactorioLibrary;

public class FactorioConnector
{
    public async Task GetServerInfo()
    {
        using FactorioRconClient client = new("10.0.0.109", 27015);
        bool connected = await client.ConnectAsync("XVlBzgbaiCMRAjWwhTHctcuA");

        Console.WriteLine("Connected: " + connected);

        string? mapString = await client.ReadAsync(g => g.Game.GetMapExchangeString());

        Console.WriteLine($"Map string: {mapString}");
    }
}