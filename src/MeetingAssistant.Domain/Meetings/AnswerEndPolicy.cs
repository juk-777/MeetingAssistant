namespace MeetingAssistant.Domain.Meetings;

/// <summary>
/// Конец ответа собеседника: по паузе и речи «Я», а не по фразам — фраза ответа длится до 20 с.
/// </summary>
/// <remarks>
/// Пороги — константы домена, их задаёт замер на записях собеседований (docs/measurements.md, «Конец ответа
/// кандидата»).
/// </remarks>
public static class AnswerEndPolicy
{
    /// <summary>
    /// Ответ короче — реплика, а не ответ: подсказка по нему не нужна.
    /// </summary>
    public static readonly TimeSpan MinAnswer = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Тишина обоих каналов после конца ответа, после которой ответ кончился.
    /// </summary>
    public static readonly TimeSpan SilenceAfterAnswer = TimeSpan.FromSeconds(2.5);

    /// <summary>
    /// Речь «Я» короче — поддакивание: ответ не прерывает.
    /// </summary>
    public static readonly TimeSpan MinOwnSpeech = TimeSpan.FromSeconds(1);

    /// <param name="answerStart">Начало первой фразы ответа.</param>
    /// <param name="answerEnd">Конец последней фразы ответа.</param>
    /// <param name="me">Речь канала «Я».</param>
    /// <param name="others">Речь канала «Собеседники».</param>
    public static AnswerOutcome Evaluate(TimeSpan answerStart, TimeSpan answerEnd, SpeechActivity me, SpeechActivity others)
    {
        if (others.SpeakingSince is not null)
        {
            return AnswerOutcome.Continues;
        }

        TimeSpan position = me.Position < others.Position ? me.Position : others.Position;
        bool complete = answerEnd - answerStart >= MinAnswer;

        // Речь «Я» считается, только если началась после ответа: эхо собеседника в микрофоне без наушников начинается
        // вместе с его фразой и детектором отпускается в тот же момент, что и сама фраза.
        if (me.SpeakingSince is { } since)
        {
            if (since < answerEnd || position - since < MinOwnSpeech)
            {
                return AnswerOutcome.Continues;
            }

            return complete ? AnswerOutcome.Ended : AnswerOutcome.Interrupted;
        }

        return complete && position - answerEnd >= SilenceAfterAnswer ? AnswerOutcome.Ended : AnswerOutcome.Continues;
    }
}

/// <summary>
/// Что стало с ответом собеседника в момент наблюдения.
/// </summary>
public enum AnswerOutcome
{
    /// <summary>
    /// Ответ идёт или ждёт продолжения.
    /// </summary>
    Continues = 1,

    /// <summary>
    /// Ответ кончился — повод для подсказки.
    /// </summary>
    Ended = 2,

    /// <summary>
    /// «Я» заговорил после реплики короче ответа: реплика не ответ, отсчёт заново.
    /// </summary>
    Interrupted = 3,
}
