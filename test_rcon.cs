using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

async Task Test()
{
    var client = new TcpClient();
    await client.ConnectAsync("127.0.0.1", 27018);
    var stream = client.GetStream();
    
    // Auth packet
    int packetId = 1;
    int type = 3;
    string body = "testpassword"; // We don't know the password, but even with a wrong one it should return Auth Failed, not disconnect!
    
    byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
    int packetSize = 10 + bodyBytes.Length;
    using MemoryStream ms = new();
    using BinaryWriter writer = new(ms);
    writer.Write(packetSize);
    writer.Write(packetId);
    writer.Write(type);
    writer.Write(bodyBytes);
    writer.Write((byte)0);
    writer.Write((byte)0);
    
    byte[] data = ms.ToArray();
    Console.WriteLine($"Sending {data.Length} bytes: {BitConverter.ToString(data)}");
    await stream.WriteAsync(data, 0, data.Length);
    
    // Wait for response
    byte[] buffer = new byte[1024];
    int read = await stream.ReadAsync(buffer, 0, buffer.Length);
    Console.WriteLine($"Read {read} bytes");
    if (read > 0) {
        Console.WriteLine($"Response: {BitConverter.ToString(buffer, 0, read)}");
    }
}
await Test();
