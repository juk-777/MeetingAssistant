using MeetingAssistant.SharedKernel;

namespace MeetingAssistant.Domain.Meetings;

/// <summary>
/// Реплика встречи: распознанная фраза одного канала со сквозным номером.
/// </summary>
/// <param name="Number">Сквозной номер с 1 в порядке окончания распознавания.</param>
/// <param name="Channel">Канал, в котором прозвучала фраза.</param>
/// <param name="Span">Начало и длительность речи от старта записи.</param>
/// <param name="Text">Распознанный текст, непустой.</param>
/// <param name="RecognizedAt">Момент окончания распознавания.</param>
public sealed record Utterance(int Number, Channel Channel, SpeechSpan Span, string Text, DateTimeOffset RecognizedAt)
{
    public string Text { get; } = string.IsNullOrWhiteSpace(Text)
        ? throw new DomainException(MeetingErrors.EmptyUtterance, "Реплика без текста не записывается.")
        : Text;
}
