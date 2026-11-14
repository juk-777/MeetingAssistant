using MeetingAssistant.Infrastructure.Audio;
using MeetingAssistant.Infrastructure.Storage;
using MeetingAssistant.SharedKernel;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Channel = MeetingAssistant.Domain.Meetings.Channel;

namespace MeetingAssistant.Infrastructure.IntegrationTests.Audio;

public sealed class LiveCaptureTests
{
    private const string NotLinux = "Захват Linux; на Windows захват проверяется живым проходом.";

    private readonly FakeTimeProvider _time = new();

    [Fact]
    public void Open_WhenMeetingsRootRelative_FailsBeforeTouchingDevices()
    {
        var pulse = new FakePulse(_time);

        Result<OpenedCapture> capture = Capture("meetings", pulse).Open();

        capture.Error.Code.ShouldBe(StorageErrors.MeetingsRootNotAbsoluteCode);
        pulse.Opened.ShouldBeEmpty();
    }

    [Fact]
    public void Open_OnLinux_OpensDefaultSourceForMeAndDefaultMonitorForOthers()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), NotLinux);

        Result<OpenedCapture> capture = Capture(Path.GetTempPath(), new FakePulse(_time)).Open();

        capture.IsSuccess.ShouldBeTrue();
        var source = capture.Value.Source.ShouldBeOfType<LiveAudioSource>();
        source.Devices.Cast<PulseDeviceStream>().ToDictionary(device => device.Channel, device => device.Device).ShouldBe(
            new Dictionary<Channel, string> { [Channel.Me] = "@DEFAULT_SOURCE@", [Channel.Others] = "@DEFAULT_MONITOR@" },
            ignoreOrder: true);
    }

    [Fact]
    public void Open_OnLinux_WhenSoundLibraryMissing_FailsWithoutOpeningDevices()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), NotLinux);
        var pulse = new FakePulse(_time) { Available = false };

        Result<OpenedCapture> capture = Capture(Path.GetTempPath(), pulse).Open();

        capture.Error.Code.ShouldBe(AudioErrors.NoSoundLibraryCode);
        pulse.Opened.ShouldBeEmpty();
    }

    [Fact]
    public void Open_OnLinux_WhenServerRefusesMonitor_FreesMicrophoneAndFailsWithServerReason()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), NotLinux);
        var pulse = new FakePulse(_time) { RefusedDevice = "@DEFAULT_MONITOR@" };

        Result<OpenedCapture> capture = Capture(Path.GetTempPath(), pulse).Open();

        capture.Error.Code.ShouldBe(AudioErrors.NoDeviceCode);
        capture.Error.Description.ShouldContain(FakePulse.Refusal);
        pulse.Freed.Select(pulse.DeviceOf).ShouldBe(["@DEFAULT_SOURCE@"]);
    }

    [Fact]
    public void Open_WhenNeitherWindowsNorLinux_FailsWithCaptureUnsupported()
    {
        Assert.SkipWhen(
            OperatingSystem.IsWindows() || OperatingSystem.IsLinux(),
            "Свойство ОС без захвата; на Windows и Linux захват есть.");

        Result<OpenedCapture> capture = Capture(Path.GetTempPath(), new FakePulse(_time)).Open();

        capture.Error.Code.ShouldBe(AudioErrors.CaptureUnsupportedCode);
    }

    private LiveCapture Capture(string root, FakePulse pulse) =>
        new(Options.Create(new StorageOptions { MeetingsRoot = root }), new MeetingDirectory(), pulse, _time);
}
