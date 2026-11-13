using System.Text.RegularExpressions;

namespace MeetingAssistant.Domain.Meetings;

/// <summary>
/// Правила пробуждения сессии: по фразе и накопленной речи решает, есть ли повод.
/// </summary>
/// <remarks>
/// Пороги — константы домена, а не настройки: их меняет замер, и меняются они вместе с тестами.
/// </remarks>
public static partial class WakePolicy
{
    /// <summary>
    /// Минимум слов во фразе с «?», чтобы она считалась вопросом: короткие «да?», «почему?» обычно риторические.
    /// </summary>
    public const int MinQuestionWords = 3;

    /// <summary>
    /// Сколько речи без события будит сессию, чтобы она ловила и утверждения, а не только вопросы.
    /// </summary>
    public static readonly TimeSpan AccumulatedSpeechLimit = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Поводы в порядке записи: обращение, вопрос, термин; накопленная речь — только если других нет.
    /// </summary>
    public static IReadOnlyList<WakeReason> Evaluate(string text, Glossary glossary, TimeSpan accumulatedSpeech)
    {
        var reasons = new List<WakeReason>(3);

        if (AddressWord().IsMatch(text))
        {
            reasons.Add(WakeReason.Address);
        }

        if (text.Contains('?', StringComparison.Ordinal) && Word().Count(text) >= MinQuestionWords)
        {
            reasons.Add(WakeReason.Question);
        }

        if (glossary.Matches(text))
        {
            reasons.Add(WakeReason.Term);
        }

        if (reasons.Count == 0 && accumulatedSpeech >= AccumulatedSpeechLimit)
        {
            reasons.Add(WakeReason.AccumulatedSpeech);
        }

        return reasons;
    }

    // Одно слово, которое распознавание пишет верно: «клод» GigaAM не выдаёт (docs/decisions.md).
    [GeneratedRegex(@"\bагент\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AddressWord();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Word();
}
