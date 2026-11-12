using MeetingAssistant.Application.Meetings.Recording;
using MeetingAssistant.Domain.Meetings;
using MeetingAssistant.SharedKernel;

namespace MeetingAssistant.Application.Meetings.Refine;

/// <summary>
/// Звук законченной встречи.
/// </summary>
/// <param name="Audio">Звук каналов от начала записи, без темпа реального времени.</param>
/// <param name="Duration">Длина записи.</param>
public sealed record RecordedMeeting(IAudioSource Audio, TimeSpan Duration);

/// <summary>
/// Встречи, записанные на диск.
/// </summary>
public interface IRecordedMeetings
{
    /// <summary>
    /// Звук встречи из каталога; отказ — нет каталога, звука или запись не закончена.
    /// </summary>
    Task<Result<RecordedMeeting>> Open(string directory, CancellationToken cancellationToken);
}

/// <summary>
/// Распознаватель уточнения — второй, не тот, что пишет встречу вживую.
/// </summary>
public interface IRefineRecognizer
{
    /// <summary>
    /// Готовит модель до первой фразы: кладёт на место, если её нет; отказ — модель не получена.
    /// </summary>
    Task<Result> Prepare(CancellationToken cancellationToken);

    /// <summary>
    /// Распознаёт фразу целиком: 16 кГц, моно, float.
    /// </summary>
    Task<string> Recognize(ReadOnlyMemory<float> samples, CancellationToken cancellationToken);
}

/// <summary>
/// Второй транскрипт встречи.
/// </summary>
public interface IRefinedTranscripts
{
    Task<IRefinedTranscriptDraft> Begin(string directory, CancellationToken cancellationToken);
}

/// <summary>
/// Второй транскрипт, который пишется: виден готовым только после <see cref="Complete"/>.
/// </summary>
/// <remarks>
/// Освобождение без <see cref="Complete"/> бросает написанное, прежний готовый транскрипт остаётся.
/// </remarks>
public interface IRefinedTranscriptDraft : IAsyncDisposable
{
    Task Append(Utterance utterance, CancellationToken cancellationToken);

    Task Complete(CancellationToken cancellationToken);
}
