using MeetingAssistant.Application.Meetings.Screen;
using MeetingAssistant.SharedKernel;

namespace MeetingAssistant.Infrastructure.Screen;

/// <summary>
/// Захват монитора встречи.
/// </summary>
public interface IScreenCapture
{
    /// <summary>
    /// Проверяет, что монитор можно снимать, и отдаёт его источник. Отказ — до начала встречи: ОС не Windows и не
    /// Linux, монитора с номером нет, на Linux — Wayland, нет дисплея X или <c>libX11</c>, задан номер монитора.
    /// </summary>
    /// <param name="monitor">Номер монитора с 1, где 1 — основной; <see langword="null"/> — основной.</param>
    Result<IScreenSource> Open(int? monitor);
}

/// <summary>
/// Снимок монитора: в Windows — GDI, в Linux — X11.
/// </summary>
internal sealed class ScreenCapture : IScreenCapture
{
    public Result<IScreenSource> Open(int? monitor)
    {
        if (monitor < 1)
        {
            return Result.Failure<IScreenSource>(ScreenCaptureErrors.NoMonitor(monitor.Value, available: 0));
        }

        if (OperatingSystem.IsWindows())
        {
            return GdiScreenSource.Open(monitor ?? 1);
        }

        return OperatingSystem.IsLinux()
            ? X11ScreenSource.Open(monitor, Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
            : Result.Failure<IScreenSource>(ScreenCaptureErrors.Unsupported);
    }
}
