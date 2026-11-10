namespace MeetingAssistant.Domain.Meetings;

/// <summary>
/// Повод разбудить сессию Claude Code.
/// </summary>
public enum WakeReason
{
    /// <summary>
    /// Во фразе слово-обращение к сессии.
    /// </summary>
    Address = 1,

    /// <summary>
    /// Фраза — вопрос не короче порога слов.
    /// </summary>
    Question = 2,

    /// <summary>
    /// Во фразе термин из словаря встречи.
    /// </summary>
    Term = 3,

    /// <summary>
    /// Накопилась речь без события.
    /// </summary>
    AccumulatedSpeech = 4,
}
