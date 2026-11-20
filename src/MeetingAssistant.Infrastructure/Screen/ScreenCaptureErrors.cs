using MeetingAssistant.SharedKernel;

namespace MeetingAssistant.Infrastructure.Screen;

/// <summary>
/// Отказы захвата экрана: все — до начала встречи.
/// </summary>
public static class ScreenCaptureErrors
{
    public const string UnsupportedCode = "Screen.Unsupported";
    public const string NoMonitorCode = "Screen.NoMonitor";
    public const string WaylandCode = "Screen.Wayland";
    public const string NoDisplayCode = "Screen.NoDisplay";
    public const string MonitorChoiceUnsupportedCode = "Screen.MonitorChoiceUnsupported";

    public static readonly Error Unsupported =
        new(UnsupportedCode, "Снимок экрана есть только в Windows и Linux X11: запустите meeting start там.");

    public static readonly Error Wayland = new(
        WaylandCode,
        "Сессия Wayland: снимок экрана там идёт через системный запрос разрешения, его meeting пока не умеет. " +
        "Запустите встречу без --screen или в сессии X11.");

    public static readonly Error NoX11Library = new(
        NoDisplayCode,
        "Нет libX11.so.6 — через неё meeting снимает экран. Пакет: libx11-6 в Debian и Ubuntu, libX11 в Fedora и Arch.");

    public static readonly Error MonitorChoiceUnsupported = new(
        MonitorChoiceUnsupportedCode,
        "В Linux снимается весь экран X сразу, номер монитора не поддерживается: запустите с --screen без номера.");

    public static Error NoMonitor(int monitor, int available) =>
        new(NoMonitorCode, $"Нет монитора {monitor}: мониторов {available}, номера с 1, основной — 1.");

    public static Error NoDisplay(string? display) => new(
        NoDisplayCode,
        string.IsNullOrEmpty(display)
            ? "Не задан DISPLAY: снимок экрана в Linux идёт с дисплея X, запустите meeting start в графической сессии."
            : $"Дисплей X {display} не открылся: снимок экрана недоступен.");
}
