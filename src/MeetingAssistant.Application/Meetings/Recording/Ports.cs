using MeetingAssistant.Domain.Meetings;
using MeetingAssistant.SharedKernel;

namespace MeetingAssistant.Application.Meetings.Recording;

/// <summary>
/// Звук, который принимают детектор речи и распознаватель: 16 кГц, моно, float.
/// </summary>
public static class SpeechAudio
{
    public const int SampleRate = 16_000;

    public static TimeSpan Duration(long sampleCount) =>
        TimeSpan.FromTicks(sampleCount * (TimeSpan.TicksPerSecond / SampleRate));
}

/// <summary>
/// Кусок звука одного канала; куски канала идут подряд от начала записи.
/// </summary>
/// <param name="Channel">Канал куска.</param>
/// <param name="Samples">Звук: 16 кГц, моно, float.</param>
public sealed record AudioChunk(Channel Channel, ReadOnlyMemory<float> Samples);

/// <summary>
/// Законченная фраза канала.
/// </summary>
/// <param name="Start">Начало фразы от начала записи канала.</param>
/// <param name="Samples">Звук фразы: 16 кГц, моно, float.</param>
public sealed record DetectedPhrase(TimeSpan Start, ReadOnlyMemory<float> Samples);

/// <summary>
/// Источник звука встречи: запись с диска или живой захват.
/// </summary>
/// <remarks>
/// Потерю устройства источник сообщает <see cref="AudioSourceException"/>.
/// </remarks>
public interface IAudioSource
{
    IAsyncEnumerable<AudioChunk> Read(CancellationToken cancellationToken);
}

/// <summary>
/// Режет звук одного канала на фразы по паузам.
/// </summary>
public interface IVoiceActivityDetector : IDisposable
{
    IReadOnlyList<DetectedPhrase> Accept(ReadOnlyMemory<float> samples);

    /// <summary>
    /// Отдаёт фразу, которую оборвал конец звука.
    /// </summary>
    IReadOnlyList<DetectedPhrase> Flush();
}

/// <summary>
/// Детектор на канал: у каждого канала своё состояние паузы и речи.
/// </summary>
public interface IVoiceActivityDetectorFactory
{
    IVoiceActivityDetector Create();
}

/// <summary>
/// Записывает текущую встречу из источника звука.
/// </summary>
public interface IMeetingRecorder
{
    /// <summary>
    /// Распознаёт фразы источника до его конца, отмены или потери устройства, затем останавливает встречу.
    /// </summary>
    Task<Result> Record(IAudioSource source, CancellationToken cancellationToken);
}
