using System.Collections.Concurrent;
using System.Net;

namespace FactorioLibrary.Services
{
    public class RconService
    {
        private string GetHost()
        {
            if (Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") != "true")
                return "127.0.0.1";

            try
            {
                // First try host.docker.internal (works on Windows/Mac Docker Desktop)
                if (Dns.GetHostAddresses("host.docker.internal").Length > 0)
                    return "host.docker.internal";
            }
            catch { }

            try
            {
                // On native Linux docker, host.docker.internal doesn't exist by default.
                // However, the Docker Host machine is always the default gateway of the container's network!
                IPAddress? gateway = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                    .SelectMany(n => n.GetIPProperties().GatewayAddresses)
                    .Select(g => g.Address)
                    .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

                if (gateway != null)
                    return gateway.ToString();
            }
            catch { }

            return "172.17.0.1"; // Fallback to standard Docker bridge gateway
        }

        private readonly ConcurrentDictionary<int, FactorioRcon> activeConnections = new();

        /// <summary>
        /// Sends a command to the Factorio server via RCON.
        /// </summary>
        public async Task<string> SendCommandAsync(int instanceId, int port, string password, string command)
        {
            try
            {
                if (!activeConnections.TryGetValue(instanceId, out FactorioRcon? rcon))
                {
                    rcon = new(GetHost(), port, password);
                    await rcon.ConnectAsync();
                    activeConnections.TryAdd(instanceId, rcon);
                }

                string response = await rcon.SendCommandAsync(command);
                if (response.StartsWith("Error"))
                    throw new(response);

                return response;
            }
            catch
            {
                // If the connection drops or errors, remove it from the pool and try one more time
                if (activeConnections.TryRemove(instanceId, out FactorioRcon? oldRcon))
                    oldRcon.Dispose();

                try
                {
                    FactorioRcon freshRcon = new(GetHost(), port, password);
                    await freshRcon.ConnectAsync();
                    activeConnections.TryAdd(instanceId, freshRcon);
                    return await freshRcon.SendCommandAsync(command);
                }
                catch (Exception ex2)
                {
                    activeConnections.TryRemove(instanceId, out _);
                    return $"Error: {ex2.Message}";
                }
            }
        }

        /// <summary>
        /// Gets the current online players.
        /// </summary>
        public async Task<List<string>> GetOnlinePlayersAsync(int instanceId, int port, string password)
        {
            List<string> players = [];
            string response = await SendCommandAsync(instanceId, port, password, "/players online");

            // Factorio response format: "Online players (1): \n username"
            // Or "Online players (0):"
            if (response.Contains("Error:"))
                return players;

            string[] lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 1; i < lines.Length; i++)
            {
                string playerName = lines[i].Trim().Split(' ')[0]; // just get the username
                if (!string.IsNullOrEmpty(playerName))
                    players.Add(playerName);
            }

            return players;
        }
    }
}
