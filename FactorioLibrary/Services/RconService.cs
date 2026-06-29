using System.Collections.Concurrent;
using System.Net;
using CoreRCON;

namespace FactorioLibrary.Services
{
    public class RconService
    {
        private readonly ConcurrentDictionary<int, RCON> _activeConnections = new();

        /// <summary>
        /// Sends a command to the Factorio server via RCON.
        /// </summary>
        public async Task<string> SendCommandAsync(int instanceId, int port, string password, string command)
        {
            try
            {
                if (!_activeConnections.TryGetValue(instanceId, out RCON? rcon))
                {
                    rcon = new RCON(IPAddress.Loopback, (ushort)port, password);
                    rcon.OnDisconnected += () => _activeConnections.TryRemove(instanceId, out _);
                    await rcon.ConnectAsync();
                    _activeConnections.TryAdd(instanceId, rcon);
                }

                return await rcon.SendCommandAsync(command);
            }
            catch (Exception ex)
            {
                // If connection fails, remove it so we can try again next time
                _activeConnections.TryRemove(instanceId, out _);
                return $"Error: {ex.Message}";
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
