using MeetingAssistant.Application.Meetings;
using MeetingAssistant.Application.Meetings.Recording;
using MeetingAssistant.Infrastructure.Audio;
using MeetingAssistant.Infrastructure.Speech;
using MeetingAssistant.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MeetingAssistant.Infrastructure;

/// <summary>
/// Регистрация реализаций портов: файлы встречи, распознавание, звук.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<StorageOptions>()
            .Bind(configuration.GetSection(StorageOptions.Section))
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.MeetingsRoot),
                $"Не задан корень встреч: {StorageOptions.Section}:{nameof(StorageOptions.MeetingsRoot)}.")
            .ValidateOnStart();

        services.AddOptions<SpeechOptions>()
            .Bind(configuration.GetSection(SpeechOptions.Section))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<SpeechOptions>, SpeechOptionsValidator>();

        services.AddSingleton<ISpeechRecognizer, GigaAmSpeechRecognizer>();
        services.AddSingleton<IVoiceActivityDetectorFactory, SileroVoiceActivityDetectorFactory>();

        services.AddSingleton<MeetingDirectory>();
        services.AddSingleton<IMeetingStorage, MeetingFolderStorage>();
        services.AddSingleton<ICurrentMeeting, CurrentMeetingFile>();
        services.AddSingleton<ITranscriptWriter, TranscriptFile>();
        services.AddSingleton<IWakeEventWriter, EventsFile>();

        services.AddSingleton<ILiveCapture, LiveCapture>();

        return services;
    }
}
