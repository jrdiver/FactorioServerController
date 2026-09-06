using System;
using System.IO;
using System.Text;

byte[] bodyBytes = Encoding.UTF8.GetBytes("test");
using (MemoryStream ms = new MemoryStream())
using (BinaryWriter writer = new BinaryWriter(ms))
{
    writer.Write(bodyBytes);
    Console.WriteLine(BitConverter.ToString(ms.ToArray()));
}
