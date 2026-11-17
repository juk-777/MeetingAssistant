using MeetingAssistant.SharedKernel;

namespace MeetingAssistant.Domain.Meetings;

/// <summary>
/// Речь канала в момент наблюдения: докуда дошёл звук канала и с какого момента в нём идёт речь.
/// </summary>
public sealed record SpeechActivity
{
    public SpeechActivity(TimeSpan position, TimeSpan? speakingSince)
    {
        if (position < TimeSpan.Zero || speakingSince < TimeSpan.Zero || speakingSince > position)
        {
            throw new DomainException(
                MeetingErrors.InvalidSpeechActivity,
                $"Речь канала должна начаться не раньше записи и не позже позиции звука: {position}, {speakingSince}.");
        }

        Position = position;
        SpeakingSince = speakingSince;
    }

    /// <summary>
    /// Сколько звука канала обработано от начала записи.
    /// </summary>
    public TimeSpan Position { get; }

    /// <summary>
    /// Начало идущей речи от начала записи; <see langword="null"/> — канал молчит.
    /// </summary>
    public TimeSpan? SpeakingSince { get; }
}
