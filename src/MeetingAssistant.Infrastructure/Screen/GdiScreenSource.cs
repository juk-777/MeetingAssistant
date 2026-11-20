using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MeetingAssistant.Application.Meetings.Screen;
using MeetingAssistant.SharedKernel;

namespace MeetingAssistant.Infrastructure.Screen;

/// <summary>
/// Снимок монитора Windows через GDI: копия экрана монитора в свою картинку.
/// </summary>
/// <remarks>
/// Проверяется только живым проходом. Процесс объявляет себя DPI-aware по мониторам до первого вызова: иначе на
/// масштабе больше 100 % Windows отдаёт координаты и снимок в масштабированных точках — уменьшенную копию
/// с размытым текстом.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed partial class GdiScreenSource : IScreenSource
{
    private const uint SourceCopy = 0x00CC0020;
    private const uint RgbColors = 0;
    private const uint MonitorPrimary = 1;
    private const int BitmapHeaderSize = 40;
    private const int MonitorInfoSize = 40;
    private static readonly nint PerMonitorAwareV2 = -4;

    private readonly Bounds _bounds;

    private GdiScreenSource(Bounds bounds)
    {
        _bounds = bounds;
    }

    /// <param name="monitor">Номер монитора с 1: основной, затем остальные в порядке Windows.</param>
    public static Result<IScreenSource> Open(int monitor)
    {
        // Отказ значит, что режим уже задан (манифестом или прошлым вызовом), — снимок от этого не меняется.
        _ = SetProcessDpiAwarenessContext(PerMonitorAwareV2);

        List<(Bounds Bounds, bool Primary)> monitors = Monitors();
        List<Bounds> ordered = [.. monitors.Where(found => found.Primary).Select(found => found.Bounds),
            .. monitors.Where(found => !found.Primary).Select(found => found.Bounds)];

        return monitor <= ordered.Count
            ? Result.Success<IScreenSource>(new GdiScreenSource(ordered[monitor - 1]))
            : Result.Failure<IScreenSource>(ScreenCaptureErrors.NoMonitor(monitor, ordered.Count));
    }

    public unsafe ScreenFrame Capture()
    {
        int width = _bounds.Right - _bounds.Left;
        int height = _bounds.Bottom - _bounds.Top;
        nint screen = GetDC(0);
        nint memory = CreateCompatibleDC(screen);
        nint bitmap = CreateCompatibleBitmap(screen, width, height);
        nint previous = SelectObject(memory, bitmap);

        try
        {
            if (bitmap == 0 || !BitBlt(memory, 0, 0, width, height, screen, _bounds.Left, _bounds.Top, SourceCopy))
            {
                throw new Win32Exception(
                    Marshal.GetLastPInvokeError(),
                    $"Снимок монитора {width}×{height} не скопирован: экран заблокирован или недоступен.");
            }

            // BITMAPINFO: заголовок (размер, ширина, высота, плоскости и бит на точку) и место под одну запись
            // палитры, которую 32-битная картинка не использует. Отрицательная высота — строки сверху вниз.
            uint* info = stackalloc uint[BitmapHeaderSize / sizeof(uint) + 1];
            new Span<uint>(info, BitmapHeaderSize / sizeof(uint) + 1).Clear();
            info[0] = BitmapHeaderSize;
            info[1] = (uint)width;
            info[2] = unchecked((uint)-height);
            info[3] = 1 | (32 << 16);
            byte[] bgra = new byte[width * height * 4];

            fixed (byte* bits = bgra)
            {
                if (GetDIBits(memory, bitmap, 0, (uint)height, bits, info, RgbColors) != height)
                {
                    throw new Win32Exception("Точки снимка монитора не прочитаны.");
                }
            }

            byte[] rgb = new byte[width * height * 3];

            for (int point = 0; point < width * height; point++)
            {
                rgb[point * 3] = bgra[point * 4 + 2];
                rgb[point * 3 + 1] = bgra[point * 4 + 1];
                rgb[point * 3 + 2] = bgra[point * 4];
            }

            return new ScreenFrame(width, height, rgb);
        }
        finally
        {
            _ = SelectObject(memory, previous);
            _ = DeleteObject(bitmap);
            _ = DeleteDC(memory);
            _ = ReleaseDC(0, screen);
        }
    }

    private static unsafe List<(Bounds Bounds, bool Primary)> Monitors()
    {
        var monitors = new List<(Bounds Bounds, bool Primary)>();
        GCHandle handle = GCHandle.Alloc(monitors);

        try
        {
            _ = EnumDisplayMonitors(0, 0, &OnMonitor, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        return monitors;
    }

    // MONITORINFO читается из байтов: поля структуры, которые заполняет Windows, компилятор считал бы
    // неприсвоенными. Раскладка: размер, границы монитора (4 int), рабочая область (4 int), флаги.
    [UnmanagedCallersOnly]
    private static unsafe int OnMonitor(nint monitor, nint deviceContext, nint clip, nint data)
    {
        uint* info = stackalloc uint[MonitorInfoSize / sizeof(uint)];
        info[0] = MonitorInfoSize;

        if (GetMonitorInfoW(monitor, info))
        {
            var bounds = new Bounds((int)info[1], (int)info[2], (int)info[3], (int)info[4]);
            var monitors = (List<(Bounds Bounds, bool Primary)>)GCHandle.FromIntPtr(data).Target!;
            monitors.Add((bounds, (info[9] & MonitorPrimary) != 0));
        }

        return 1;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessDpiAwarenessContext(nint value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool EnumDisplayMonitors(
        nint deviceContext,
        nint clip,
        delegate* unmanaged<nint, nint, nint, nint, int> callback,
        nint data);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetMonitorInfoW(nint monitor, uint* info);

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint window);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint window, nint deviceContext);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint deviceContext);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleBitmap(nint deviceContext, int width, int height);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint deviceContext, nint gdiObject);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BitBlt(
        nint target,
        int x,
        int y,
        int width,
        int height,
        nint source,
        int sourceX,
        int sourceY,
        uint operation);

    [LibraryImport("gdi32.dll")]
    private static unsafe partial int GetDIBits(
        nint deviceContext,
        nint bitmap,
        uint start,
        uint lines,
        byte* bits,
        uint* info,
        uint usage);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint gdiObject);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint deviceContext);

    private readonly record struct Bounds(int Left, int Top, int Right, int Bottom);
}
