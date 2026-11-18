using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Dignite.Vault.Parse.Ocr.VisionLlm;

/// <summary>
/// An image whose decoded size has nothing to do with its file: a PNG of one grey level compresses about a
/// thousandfold, so a few hundred kilobytes can declare hundreds of megapixels.
/// </summary>
internal static class DecompressionBomb
{
    /// <summary>
    /// An 8-bit greyscale PNG of <paramref name="width"/> × <paramref name="height"/> black pixels: 20,000 × 20,000 is
    /// 389 KB.
    /// </summary>
    public static byte[] Png(int width, int height)
    {
        var pixels = new MemoryStream();
        using (var zlib = new ZLibStream(pixels, CompressionLevel.Optimal, leaveOpen: true))
        {
            // Each row is a filter-type byte (0, none) followed by one byte a pixel.
            var row = new byte[width + 1];
            for (var y = 0; y < height; y++)
            {
                zlib.Write(row);
            }
        }

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;  // bits per sample
        header[9] = 0;  // greyscale
        // compression, filter and interlace methods: 0

        var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", pixels.ToArray());
        WriteChunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    /// <summary>
    /// The most memory this process has had resident so far. A decoder allocates its bitmap natively, outside the
    /// managed heap, so <see cref="GC.GetAllocatedBytesForCurrentThread"/> does not see it; the resident peak does.
    /// </summary>
    public static long PeakWorkingSet()
    {
        using var process = Process.GetCurrentProcess();
        return process.PeakWorkingSet64;
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

    // PNG's CRC (ISO 3309, polynomial 0xEDB88320), bit by bit: the chunks here are a few hundred kilobytes at most.
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
