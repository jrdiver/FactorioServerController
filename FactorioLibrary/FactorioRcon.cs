using System.Net.Sockets;
using System.Text;

namespace FactorioLibrary;

public class FactorioRcon(string host, int port, string password) : IDisposable
{
    private TcpClient? client;
    private NetworkStream? stream;
    private int packetId = 1;

    public async Task<bool> ConnectAsync()
    {
        client = new();
        await client.ConnectAsync(host, port);
        stream = client.GetStream();

        // Send Auth (Type 3)
        int authId = packetId;
        await SendPacketAsync(3, password);

        // Wait for Auth Response
        while (true)
        {
            (int Id, int Type, string Body) response = await ReceivePacketAsync();
            if (response.Id == -1)
                throw new("Authentication rejected by server (Invalid password or RCON bug).");
            if (response.Id == authId)
                break; // Auth successful
        }

        return true;
    }

    public async Task<string> SendCommandAsync(string command)
    {
        if (stream == null) return "Not connected to RCON.";

        try
        {
            int cmdId = packetId;
            await SendPacketAsync(2, command);

            while (true)
            {
                (int Id, int Type, string Body) response = await ReceivePacketAsync();
                if (response.Id == cmdId)
                    return response.Body;
            }
        }
        catch (Exception ex)
        {
            return $"Error sending command: {ex.Message}";
        }
    }

    private async Task SendPacketAsync(int type, string body)
    {
        if (stream == null) throw new InvalidOperationException("Not connected.");

        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
        int packetSize = 10 + bodyBytes.Length; // 4 (ID) + 4 (Type) + body + 1 (null) + 1 (null)

        using MemoryStream ms = new();
        await using BinaryWriter writer = new(ms);

        writer.Write(packetSize);
        writer.Write(packetId);
        writer.Write(type);
        writer.Write(bodyBytes);
        writer.Write((byte)0); // Null terminator for body
        writer.Write((byte)0); // Null terminator for empty string

        byte[] data = ms.ToArray();
        await stream.WriteAsync(data, 0, data.Length);

        packetId++; // Increment ID for the next packet
    }

    private async Task<(int Id, int Type, string Body)> ReceivePacketAsync()
    {
        if (stream == null) throw new InvalidOperationException("Not connected.");

        byte[] sizeBuffer = new byte[4];
        int sizeBytesRead = 0;
        while (sizeBytesRead < 4)
        {
            int read = await stream.ReadAsync(sizeBuffer, sizeBytesRead, 4 - sizeBytesRead);
            if (read == 0) throw new EndOfStreamException("Connection closed while reading packet size.");
            sizeBytesRead += read;
        }

        int size = BitConverter.ToInt32(sizeBuffer, 0);
        byte[] packetData = new byte[size];

        int totalRead = 0;
        while (totalRead < size)
        {
            int read = await stream.ReadAsync(packetData, totalRead, size - totalRead);
            if (read == 0) throw new EndOfStreamException("Connection closed while reading packet data.");
            totalRead += read;
        }

        int id = BitConverter.ToInt32(packetData, 0);
        int type = BitConverter.ToInt32(packetData, 4);
        string body = Encoding.UTF8.GetString(packetData, 8, size - 10); // exclude ID, Type, and two null bytes

        return (id, type, body);
    }

    public void Dispose()
    {
        stream?.Dispose();
        client?.Dispose();
    }
}
