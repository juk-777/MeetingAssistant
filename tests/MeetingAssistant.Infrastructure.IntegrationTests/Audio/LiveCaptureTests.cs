using MeetingAssistant.Infrastructure.Audio;
using MeetingAssistant.Infrastructure.Storage;
using MeetingAssistant.SharedKernel;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace MeetingAssistant.Infrastructure.IntegrationTests.Audio;

public sealed class LiveCaptureTests
{
    [Fact]
    public void Open_WhenMeetingsRootRelative_FailsBeforeTouchingDevices()
    {
        Result<OpenedCapture> capture = Capture("meetings").Open();

        capture.Error.Code.ShouldBe(StorageErrors.MeetingsRootNotAbsoluteCode);
    }

    [Fact]
    public void Open_WhenNotWindows_FailsWithCaptureUnsupported()
    {
        // На Windows этот вызов открыл бы настоящие устройства: путь проверяется живым проходом.
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Свойство не-Windows; на Windows захват проверяется живым проходом.");

        Result<OpenedCapture> capture = Capture(Path.GetTempPath()).Open();

        capture.Error.Code.ShouldBe(AudioErrors.CaptureUnsupportedCode);
    }

    private static LiveCapture Capture(string root) =>
        new(Options.Create(new StorageOptions { MeetingsRoot = root }), new MeetingDirectory(), new FakeTimeProvider());
}
