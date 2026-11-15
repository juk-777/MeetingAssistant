using System.Runtime.Versioning;
using MeetingAssistant.Application.Meetings.Recording;
using MeetingAssistant.Domain.Meetings;
using MeetingAssistant.Infrastructure.Storage;
using MeetingAssistant.SharedKernel;
using Microsoft.Extensions.Options;
using NAudio.CoreAudioApi;

namespace MeetingAssistant.Infrastructure.Audio;

/// <summary>
/// Живой захват встречи: микрофон — «Я», системный звук — «Собеседники», запись звука в каталог встречи.
/// </summary>
public interface ILiveCapture
{
    /// <summary>
    /// Открывает устройства по умолчанию. Отказ — до начала встречи: корень встреч не абсолютный, ОС не Windows и не
    /// Linux, нет устройства или клиента сервера звука, формат устройства не принят.
    /// </summary>
    Result<OpenedCapture> Open();

    /// <summary>
    /// WAV встречи в каталоге текущей встречи.
    /// </summary>
    string AudioFile { get; }

    /// <summary>
    /// Источник, звук которого пишется в <see cref="AudioFile"/>.
    /// </summary>
    IAudioSource RecordToMeeting(IAudioSource source);
}

/// <summary>
/// Открытый захват.
/// </summary>
/// <param name="Source">Звук обоих каналов.</param>
/// <param name="Microphone">Имя устройства записи («Я»).</param>
/// <param name="SystemSound">Имя устройства вывода, чей звук снимается («Собеседники»).</param>
public sealed record OpenedCapture(IAudioSource Source, string Microphone, string SystemSound);

/// <summary>
/// Захват на устройствах по умолчанию: в Windows — WASAPI, в Linux — сервер звука через <c>libpulse-simple</c>.
/// </summary>
internal sealed class LiveCapture(
    IOptions<StorageOptions> storageOptions,
    MeetingDirectory meetingDirectory,
    IPulseSimple pulse,
    TimeProvider timeProvider) : ILiveCapture
{
    public const string AudioFileName = "audio.wav";

    public Result<OpenedCapture> Open()
    {
        // Скилл ищет current.json в своём корне: корень от текущего каталога дал бы встречу, которую он не найдёт.
        string root = storageOptions.Value.MeetingsRoot;

        if (!Path.IsPathFullyQualified(root))
        {
            return Result.Failure<OpenedCapture>(StorageErrors.MeetingsRootNotAbsolute(root));
        }

        if (OperatingSystem.IsWindows())
        {
            return OpenDefaultDevices();
        }

        return OperatingSystem.IsLinux()
            ? OpenSoundServer()
            : Result.Failure<OpenedCapture>(AudioErrors.CaptureUnsupported);
    }

    public string AudioFile => Path.Combine(meetingDirectory.Path, AudioFileName);

    public IAudioSource RecordToMeeting(IAudioSource source) => new WavRecordingAudioSource(source, AudioFile);

    [SupportedOSPlatform("windows")]
    private Result<OpenedCapture> OpenDefaultDevices()
    {
        using var enumerator = new MMDeviceEnumerator();

        if (!enumerator.TryGetDefaultAudioEndpoint(DataFlow.Capture, Role.Console, out MMDevice? microphone))
        {
            return Result.Failure<OpenedCapture>(AudioErrors.NoDevice("записи (микрофона)"));
        }

        if (!enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Console, out MMDevice? speakers))
        {
            microphone.Dispose();
            return Result.Failure<OpenedCapture>(AudioErrors.NoDevice("вывода (динамиков или наушников)"));
        }

        var me = new WasapiDeviceStream(Channel.Me, microphone, loopback: false);
        var others = new WasapiDeviceStream(Channel.Others, speakers, loopback: true);
        Result<LiveAudioSource> source = LiveAudioSource.Open([me, others], timeProvider);

        if (source.IsFailure)
        {
            microphone.Dispose();
            speakers.Dispose();
            return Result.Failure<OpenedCapture>(source.Error);
        }

        return Result.Success(new OpenedCapture(source.Value, me.DeviceName, others.DeviceName));
    }

    private Result<OpenedCapture> OpenSoundServer()
    {
        if (!pulse.Load())
        {
            return Result.Failure<OpenedCapture>(AudioErrors.NoSoundLibrary);
        }

        Result<PulseDeviceStream> me = PulseDeviceStream.Open(Channel.Me, pulse, timeProvider);

        if (me.IsFailure)
        {
            return Result.Failure<OpenedCapture>(me.Error);
        }

        Result<PulseDeviceStream> others = PulseDeviceStream.Open(Channel.Others, pulse, timeProvider);

        if (others.IsFailure)
        {
            me.Value.Dispose();
            return Result.Failure<OpenedCapture>(others.Error);
        }

        Result<LiveAudioSource> source = LiveAudioSource.Open([me.Value, others.Value], timeProvider);

        if (source.IsFailure)
        {
            me.Value.Dispose();
            others.Value.Dispose();
            return Result.Failure<OpenedCapture>(source.Error);
        }

        return Result.Success(new OpenedCapture(source.Value, me.Value.DeviceName, others.Value.DeviceName));
    }
}
