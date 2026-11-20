using System.Runtime.Versioning;
using MeetingAssistant.Infrastructure.Screen;
using Shouldly;

namespace MeetingAssistant.Infrastructure.IntegrationTests.Screen;

/// <summary>
/// Отказы захвата экрана до начала встречи. Сам снимок тестами не покрыт: GDI и X11 проверяются живым проходом.
/// </summary>
public sealed class ScreenCaptureTests
{
    [Fact]
    public void Open_WhenMonitorNumberBelowOne_Fails()
    {
        new ScreenCapture().Open(0).Error.Code.ShouldBe(ScreenCaptureErrors.NoMonitorCode);
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public void Open_OnLinuxWithMonitorNumber_FailsBeforeTouchingDisplay()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Захват X11 — только Linux.");

        X11ScreenSource.Open(1, waylandDisplay: null).Error.Code.ShouldBe(ScreenCaptureErrors.MonitorChoiceUnsupportedCode);
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public void Open_OnLinuxUnderWayland_Fails()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Захват X11 — только Linux.");

        X11ScreenSource.Open(null, "wayland-0").Error.Code.ShouldBe(ScreenCaptureErrors.WaylandCode);
    }
}
