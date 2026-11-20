using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using MeetingAssistant.Application.Meetings.Screen;
using MeetingAssistant.Infrastructure.IntegrationTests.Storage;
using MeetingAssistant.Infrastructure.Screen;
using MeetingAssistant.Infrastructure.Storage;
using Shouldly;

namespace MeetingAssistant.Infrastructure.IntegrationTests.Screen;

public sealed class ScreenFilesTests : IDisposable
{
    private readonly StorageHarness _harness = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Write_WhenScreenChanged_WritesImageThenLogLine()
    {
        await _harness.Start("Экран");
        IScreenWriter writer = _harness.Service<IScreenWriter>();
        ScreenFrame frame = Gradient(40, 30);

        await writer.Write(frame, new TimeSpan(1, 2, 3), Token);
        _harness.Time.Advance(TimeSpan.FromMilliseconds(1_081));
        await writer.Write(frame, TimeSpan.FromSeconds(75.9), Token);

        string[] lines = await File.ReadAllLinesAsync(Path.Combine(_harness.MeetingFolder, ScreenFiles.LogFileName), Token);
        lines.ShouldBe(["1767261600.000\t01-02-03.png", "1767261601.081\t00-01-15.png"]);
        (int width, int height, byte[] rgb) = Decode(await File.ReadAllBytesAsync(ImagePath("01-02-03.png"), Token));
        (width, height).ShouldBe((40, 30));
        rgb.ShouldBe(frame.Rgb.ToArray());
    }

    [Fact]
    public async Task Write_WhenImageNotWritten_WritesNoLogLine()
    {
        await _harness.Start("Экран");
        string directory = Path.Combine(_harness.MeetingFolder, ScreenFiles.DirectoryName);
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, "00-00-05.png"));

        // Занятое каталогом имя: отказ записи снимка, тип исключения у ОС свой.
        Exception? failure = await Record.ExceptionAsync(
            () => _harness.Service<IScreenWriter>().Write(Gradient(4, 4), TimeSpan.FromSeconds(5), Token));

        failure.ShouldNotBeNull();

        File.Exists(Path.Combine(_harness.MeetingFolder, ScreenFiles.LogFileName)).ShouldBeFalse();
    }

    [Fact]
    public async Task Write_WhenScreenLargerThanModelTakes_WritesDownscaledImage()
    {
        await _harness.Start("Экран");

        await _harness.Service<IScreenWriter>().Write(Gradient(3136, 1764), TimeSpan.Zero, Token);

        (int width, int height, _) = Decode(await File.ReadAllBytesAsync(ImagePath("00-00-00.png"), Token));
        (width, height).ShouldBe((ScreenImage.MaxSide, 882));
    }

    [Theory]
    [InlineData(3840, 2160, ScreenImage.MaxSide, 882)]
    [InlineData(1000, 3000, 523, ScreenImage.MaxSide)]
    [InlineData(ScreenImage.MaxSide, 900, ScreenImage.MaxSide, 900)]
    [InlineData(800, 600, 800, 600)]
    public void Downscale_KeepsProportionsWithinMaxSide(int width, int height, int expectedWidth, int expectedHeight)
    {
        ScreenFrame scaled = ScreenImage.Downscale(new ScreenFrame(width, height, new byte[width * height * 3]));

        (scaled.Width, scaled.Height, scaled.Rgb.Length).ShouldBe((expectedWidth, expectedHeight, expectedWidth * expectedHeight * 3));
    }

    [Fact]
    public void Downscale_WhenHalving_AveragesCoveredPoints()
    {
        // Шахматка чёрных и белых точек вдвое меньше — серый; красная полоса слева — красный.
        const int width = ScreenImage.MaxSide * 2;
        const int height = 4;
        byte[] rgb = new byte[width * height * 3];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                Span<byte> point = rgb.AsSpan((y * width + x) * 3, 3);

                if (x < 2)
                {
                    point[0] = 255;
                }
                else if ((x + y) % 2 == 0)
                {
                    point.Fill(255);
                }
            }
        }

        ScreenFrame scaled = ScreenImage.Downscale(new ScreenFrame(width, height, rgb));

        scaled.Rgb.Span[..3].ToArray().ShouldBe(new byte[] { 255, 0, 0 });
        scaled.Rgb.Span.Slice(3, 3).ToArray().ShouldBe(new byte[] { 128, 128, 128 });
    }

    public void Dispose() => _harness.Dispose();

    private string ImagePath(string name) => Path.Combine(_harness.MeetingFolder, ScreenFiles.DirectoryName, name);

    private static ScreenFrame Gradient(int width, int height)
    {
        byte[] rgb = new byte[width * height * 3];

        for (int point = 0; point < width * height; point++)
        {
            rgb[point * 3] = (byte)point;
            rgb[point * 3 + 1] = (byte)(point / width * 7);
            rgb[point * 3 + 2] = (byte)(255 - point % 256);
        }

        return new ScreenFrame(width, height, rgb);
    }

    /// <summary>
    /// Разбор PNG независимо от кодировщика: сигнатура, CRC каждого куска (побитовый расчёт, без таблицы), IHDR,
    /// строки IDAT без фильтра.
    /// </summary>
    private static (int Width, int Height, byte[] Rgb) Decode(byte[] png)
    {
        png[..8].ShouldBe(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        int width = 0, height = 0;
        using var compressed = new MemoryStream();
        int offset = 8;

        while (offset < png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            string type = Encoding.ASCII.GetString(png, offset + 4, 4);
            ReadOnlySpan<byte> data = png.AsSpan(offset + 8, length);
            BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + length)).ShouldBe(Crc(png.AsSpan(offset + 4, length + 4)), type);

            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                data[8..13].ToArray().ShouldBe(new byte[] { 8, 2, 0, 0, 0 });
            }
            else if (type == "IDAT")
            {
                compressed.Write(data);
            }

            offset += length + 12;
        }

        compressed.Position = 0;
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);
        byte[] rows = raw.ToArray();
        rows.Length.ShouldBe(height * (width * 3 + 1));
        byte[] rgb = new byte[width * height * 3];

        for (int row = 0; row < height; row++)
        {
            rows[row * (width * 3 + 1)].ShouldBe((byte)0);
            Array.Copy(rows, row * (width * 3 + 1) + 1, rgb, row * width * 3, width * 3);
        }

        return (width, height, rgb);
    }

    private static uint Crc(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;

        foreach (byte value in data)
        {
            crc ^= value;

            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ (0xEDB88320 & (0 - (crc & 1)));
            }
        }

        return ~crc;
    }
}
