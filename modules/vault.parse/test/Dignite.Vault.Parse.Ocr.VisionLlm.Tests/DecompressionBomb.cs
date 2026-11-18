using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Dignite.Vault.Parse.Ocr.VisionLlm;

/// <summary>
/// An image whose decoded size has nothing to do with its file: the header declares the size, and decoding allocates
/// it before reading a pixel.
/// </summary>
internal static class DecompressionBomb
{
    /// <summary>
    /// A PNG whose header declares <paramref name="width"/> × <paramref name="height"/> RGB pixels, with pixel data for
    /// one row only. SkiaSharp reads the header (libpng accepts up to 1,000,000 a side) and decodes RGB to four bytes a
    /// pixel, so 100,000 × 100,000 is a file of under a kilobyte that would take 40 GB to decode. No machine running the
    /// tests allocates that: code that decodes before checking the size fails, whatever ran before it in the process.
    /// </summary>
    public static byte[] HeaderOnlyPng(int width, int height)
    {
        var pixels = new MemoryStream();
        using (var zlib = new ZLibStream(pixels, CompressionLevel.Optimal, leaveOpen: true))
        {
            // One row: a filter-type byte (0, none) and three bytes a pixel.
            zlib.Write(new byte[1 + 3 * width]);
        }

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;  // bits per sample
        header[9] = 2;  // RGB
        // compression, filter and interlace methods: 0

        var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", pixels.ToArray());
        WriteChunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    private static void WriteChunk(Stream png, string type, byte[] data)
    {
        var typeBytes = Encoding.ASCII.GetBytes(type);
        Span<byte> number = stackalloc byte[4];

        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        png.Write(number);
        png.Write(typeBytes);
        png.Write(data);

        var crc = Crc32(Crc32(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(number, crc);
        png.Write(number);
    }

    // PNG's CRC (ISO 3309, polynomial 0xEDB88320), bit by bit: the chunks here are a few hundred bytes.
    private static uint Crc32(uint crc, byte[] data)
    {
        foreach (var b in data)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return crc;
    }
}
