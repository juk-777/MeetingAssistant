using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace MeetingAssistant.Infrastructure.Screen;

/// <summary>
/// PNG из точек RGB: 8 бит на канал, без прозрачности, строки без фильтра, сжатие zlib.
/// </summary>
/// <remarks>
/// Снимок экрана — единственная картинка, которую пишет приложение: зависимость ради одного формата не нужна.
/// </remarks>
internal static class PngImage
{
    private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    private const byte TrueColor = 2;
    private const byte NoFilter = 0;

    /// <param name="width">Ширина в точках.</param>
    /// <param name="height">Высота в точках.</param>
    /// <param name="rgb">Точки построчно сверху вниз, по три байта.</param>
    public static byte[] Encode(int width, int height, ReadOnlySpan<byte> rgb)
    {
        int stride = width * 3;

        if (width < 1 || height < 1 || rgb.Length != stride * height)
        {
            throw new ArgumentException($"Картинка {width}×{height} — это {stride * height} байт, а не {rgb.Length}.", nameof(rgb));
        }

        using var png = new MemoryStream();
        png.Write(Signature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8;
        header[9] = TrueColor;
        WriteChunk(png, "IHDR", header);

        using (var pixels = new MemoryStream())
        {
            using (var zlib = new ZLibStream(pixels, CompressionLevel.Optimal, leaveOpen: true))
            {
                for (int row = 0; row < height; row++)
                {
                    zlib.WriteByte(NoFilter);
                    zlib.Write(rgb.Slice(row * stride, stride));
                }
            }

            WriteChunk(png, "IDAT", pixels.GetBuffer().AsSpan(0, (int)pixels.Length));
        }

        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream png, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        png.Write(number);

        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        png.Write(typeBytes);
        png.Write(data);

        uint crc = Crc32.Update(Crc32.Initial, typeBytes);
        crc = Crc32.Update(crc, data);
        BinaryPrimitives.WriteUInt32BigEndian(number, crc ^ Crc32.Initial);
        png.Write(number);
    }

    /// <summary>
    /// CRC-32 кусков PNG (многочлен 0xEDB88320, как в zlib).
    /// </summary>
    private static class Crc32
    {
        public const uint Initial = 0xFFFFFFFF;

        private static readonly uint[] Table = BuildTable();

        public static uint Update(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (byte value in data)
            {
                crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
            }

            return crc;
        }

        private static uint[] BuildTable()
        {
            uint[] table = new uint[256];

            for (uint index = 0; index < table.Length; index++)
            {
                uint value = index;

                for (int bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0 ? 0xEDB88320 ^ (value >> 1) : value >> 1;
                }

                table[index] = value;
            }

            return table;
        }
    }
}
