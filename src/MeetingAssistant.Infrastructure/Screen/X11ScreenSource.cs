using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MeetingAssistant.Application.Meetings.Screen;
using MeetingAssistant.SharedKernel;

namespace MeetingAssistant.Infrastructure.Screen;

/// <summary>
/// Снимок экрана X целиком через <c>libX11</c>: корневое окно, все мониторы одним кадром.
/// </summary>
/// <remarks>
/// Проверяется только живым проходом на Linux с X11. Дисплей открывается на каждый снимок: раз в секунду это
/// дешевле, чем держать соединение и следить за его обрывом.
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed partial class X11ScreenSource : IScreenSource
{
    private const string Library = "libX11.so.6";
    private const int ZPixmap = 2;
    private const int LeastSignificantFirst = 0;

    // Смещения полей XImage на 64-битной платформе: поля, которые заполняет Xlib, читаются из памяти, а не
    // структурой — компилятор считал бы их неприсвоенными.
    private const int WidthOffset = 0;
    private const int HeightOffset = 4;
    private const int DataOffset = 16;
    private const int ByteOrderOffset = 24;
    private const int BytesPerLineOffset = 44;
    private const int BitsPerPixelOffset = 48;
    private const int RedMaskOffset = 56;
    private const int GreenMaskOffset = 64;
    private const int BlueMaskOffset = 72;

    private X11ScreenSource()
    {
    }

    /// <param name="monitor">Номер монитора: в X снимается весь экран, номер — отказ.</param>
    /// <param name="waylandDisplay">Значение <c>WAYLAND_DISPLAY</c>.</param>
    public static Result<IScreenSource> Open(int? monitor, string? waylandDisplay)
    {
        if (monitor is not null)
        {
            return Result.Failure<IScreenSource>(ScreenCaptureErrors.MonitorChoiceUnsupported);
        }

        // Под Wayland корневое окно XWayland показывает только окна X-приложений: снимок был бы пустым.
        if (!string.IsNullOrEmpty(waylandDisplay))
        {
            return Result.Failure<IScreenSource>(ScreenCaptureErrors.Wayland);
        }

        string? display = Environment.GetEnvironmentVariable("DISPLAY");

        if (string.IsNullOrEmpty(display))
        {
            return Result.Failure<IScreenSource>(ScreenCaptureErrors.NoDisplay(display));
        }

        if (!NativeLibrary.TryLoad(Library, out _))
        {
            return Result.Failure<IScreenSource>(ScreenCaptureErrors.NoX11Library);
        }

        nint connection = XOpenDisplay(0);

        if (connection == 0)
        {
            return Result.Failure<IScreenSource>(ScreenCaptureErrors.NoDisplay(display));
        }

        _ = XCloseDisplay(connection);
        return Result.Success<IScreenSource>(new X11ScreenSource());
    }

    public ScreenFrame Capture()
    {
        nint connection = XOpenDisplay(0);

        if (connection == 0)
        {
            throw new InvalidOperationException("Дисплей X не открылся.");
        }

        try
        {
            int screen = XDefaultScreen(connection);
            int width = XDisplayWidth(connection, screen);
            int height = XDisplayHeight(connection, screen);
            nint image = XGetImage(connection, XRootWindow(connection, screen), 0, 0, (uint)width, (uint)height, nuint.MaxValue, ZPixmap);

            if (image == 0)
            {
                throw new InvalidOperationException("Снимок экрана X не получен.");
            }

            try
            {
                return Convert(image);
            }
            finally
            {
                _ = XFree(Marshal.ReadIntPtr(image, DataOffset));
                _ = XFree(image);
            }
        }
        finally
        {
            _ = XCloseDisplay(connection);
        }
    }

    private static ScreenFrame Convert(nint image)
    {
        int width = Marshal.ReadInt32(image, WidthOffset);
        int height = Marshal.ReadInt32(image, HeightOffset);
        int bytesPerLine = Marshal.ReadInt32(image, BytesPerLineOffset);

        if (Marshal.ReadInt32(image, BitsPerPixelOffset) != 32 || Marshal.ReadInt32(image, ByteOrderOffset) != LeastSignificantFirst)
        {
            throw new InvalidOperationException("Экран X не в 32-битном цвете: такой снимок meeting не разбирает.");
        }

        Channel red = Channel.From((ulong)Marshal.ReadInt64(image, RedMaskOffset));
        Channel green = Channel.From((ulong)Marshal.ReadInt64(image, GreenMaskOffset));
        Channel blue = Channel.From((ulong)Marshal.ReadInt64(image, BlueMaskOffset));
        byte[] line = new byte[bytesPerLine];
        byte[] rgb = new byte[width * height * 3];
        nint data = Marshal.ReadIntPtr(image, DataOffset);

        for (int y = 0; y < height; y++)
        {
            Marshal.Copy(data + y * bytesPerLine, line, 0, bytesPerLine);

            for (int x = 0; x < width; x++)
            {
                uint pixel = BitConverter.ToUInt32(line, x * 4);
                int offset = (y * width + x) * 3;
                rgb[offset] = red.Of(pixel);
                rgb[offset + 1] = green.Of(pixel);
                rgb[offset + 2] = blue.Of(pixel);
            }
        }

        return new ScreenFrame(width, height, rgb);
    }

    [LibraryImport(Library)]
    private static partial nint XOpenDisplay(nint name);

    [LibraryImport(Library)]
    private static partial int XCloseDisplay(nint display);

    [LibraryImport(Library)]
    private static partial int XDefaultScreen(nint display);

    [LibraryImport(Library)]
    private static partial nuint XRootWindow(nint display, int screen);

    [LibraryImport(Library)]
    private static partial int XDisplayWidth(nint display, int screen);

    [LibraryImport(Library)]
    private static partial int XDisplayHeight(nint display, int screen);

    [LibraryImport(Library)]
    private static partial nint XGetImage(
        nint display,
        nuint drawable,
        int x,
        int y,
        uint width,
        uint height,
        nuint planeMask,
        int format);

    [LibraryImport(Library)]
    private static partial int XFree(nint data);

    /// <summary>
    /// Цветовой канал точки по маске X: сдвиг до младшего бита, 8 бит.
    /// </summary>
    private readonly record struct Channel(uint Mask, int Shift)
    {
        public static Channel From(ulong mask) =>
            new((uint)mask, mask == 0 ? 0 : System.Numerics.BitOperations.TrailingZeroCount(mask));

        public byte Of(uint pixel) => (byte)((pixel & Mask) >> Shift);
    }
}
