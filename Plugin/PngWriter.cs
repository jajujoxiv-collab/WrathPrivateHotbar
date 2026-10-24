using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace WrathPrivateHotbar;

// Tiny dependency-free PNG encoder for RGBA8 game icons.
internal static class PngWriter
{
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    public static void WriteRgba(string filename, int width, int height, byte[] rgba)
    {
        if (width <= 0 || height <= 0 || width > 1024 || height > 1024 ||
            rgba.Length < checked(width * height * 4))
            throw new InvalidDataException("Icon pixel dimensions do not match RGBA buffer.");

        using var stream = File.Create(filename);
        stream.Write(Signature);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), height);
        header[8] = 8; // bit depth
        header[9] = 6; // RGBA
        WriteChunk(stream, "IHDR", header);

        using var packed = new MemoryStream();
        using (var compressor = new ZLibStream(packed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var stride = width * 4;
            for (var y = 0; y < height; y++)
            {
                compressor.WriteByte(0); // PNG filter: None
                compressor.Write(rgba, y * stride, stride);
            }
        }
        WriteChunk(stream, "IDAT", packed.ToArray());
        WriteChunk(stream, "IEND", Array.Empty<byte>());
    }

    private static void WriteChunk(Stream stream, string type, byte[] payload)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, payload.Length);
        stream.Write(len);
        var tag = Encoding.ASCII.GetBytes(type);
        stream.Write(tag);
        stream.Write(payload);
        uint crc = 0xFFFFFFFF;
        foreach (var b in tag) crc = Update(crc, b);
        foreach (var b in payload) crc = Update(crc, b);
        BinaryPrimitives.WriteUInt32BigEndian(len, ~crc);
        stream.Write(len);
    }

    private static uint Update(uint value, byte input)
    {
        value ^= input;
        for (var i = 0; i < 8; i++)
            value = (value & 1) == 1 ? (value >> 1) ^ 0xEDB88320u : value >> 1;
        return value;
    }
}
