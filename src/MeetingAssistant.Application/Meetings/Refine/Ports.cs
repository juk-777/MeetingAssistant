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
    /// Сколько звука распознаётся за ту же цену, что и меньший кусок: столько речи стоит подавать за вызов.
    /// </summary>
    TimeSpan Window { get; }

    /// <summary>
    /// Готовит модель до первого куска: кладёт на место, если её нет; отказ — модель не получена.
    /// </summary>
    Task<Result> Prepare(CancellationToken cancellationToken);

    /// <summary>
    /// Распознаёт кусок звука (16 кГц, моно, float) в отрезки текста со временем от начала куска.
    /// </summary>
    Task<IReadOnlyList<RecognizedSegment>> Recognize(ReadOnlyMemory<float> samples, CancellationToken cancellationToken);
}

/// <summary>
/// Отрезок распознанного текста.
/// </summary>
/// <param name="Start">Начало от начала куска.</param>
/// <param name="End">Конец от начала куска.</param>
/// <param name="Text">Текст; пустой — отрезок без речи.</param>
public sealed record RecognizedSegment(TimeSpan Start, TimeSpan End, string Text);

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
