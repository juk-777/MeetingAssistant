using MeetingAssistant.SharedKernel;

namespace MeetingAssistant.Domain.Meetings;

/// <summary>
/// Отрезок речи от начала записи: начало фразы и её длительность.
/// </summary>
public sealed record SpeechSpan
{
    public SpeechSpan(TimeSpan start, TimeSpan duration)
    {
        if (start < TimeSpan.Zero || duration <= TimeSpan.Zero)
        {
            throw new DomainException(
                MeetingErrors.InvalidSpeechSpan,
                $"Отрезок речи должен начинаться не раньше записи и иметь длительность: {start}, {duration}.");
        }

        Start = start;
        Duration = duration;
    }

    public TimeSpan Start { get; }

    public TimeSpan Duration { get; }
}
