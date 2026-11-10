using System.Text.RegularExpressions;
using MeetingAssistant.SharedKernel;

namespace MeetingAssistant.Domain.Meetings;

/// <summary>
/// Словарь встречи: шаблоны терминов, на которые сессию стоит разбудить.
/// </summary>
public sealed class Glossary
{
    public const string InvalidPatternCode = "Glossary.InvalidPattern";

    /// <summary>
    /// Предел на сопоставление одного шаблона: шаблон пишет человек, и катастрофический возврат
    /// не должен останавливать распознавание встречи.
    /// </summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    public static readonly Glossary Empty = new([]);

    private readonly IReadOnlyList<Regex> _terms;

    private Glossary(IReadOnlyList<Regex> terms)
    {
        _terms = terms;
    }

    public int Count => _terms.Count;

    /// <summary>
    /// Разбирает строки словаря: шаблон на строку, пустые строки пропускаются.
    /// </summary>
    public static Result<Glossary> Create(IEnumerable<string> lines)
    {
        var terms = new List<Regex>();
        var lineNumber = 0;

        foreach (string line in lines)
        {
            lineNumber++;
            string pattern = line.Trim();

            if (pattern.Length == 0)
            {
                continue;
            }

            try
            {
                terms.Add(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout));
            }
            catch (ArgumentException exception)
            {
                return Result.Failure<Glossary>(new Error(
                    InvalidPatternCode,
                    $"Строка {lineNumber} словаря — не регулярное выражение: {exception.Message}"));
            }
        }

        return Result.Success(new Glossary(terms));
    }

    /// <summary>
    /// Есть ли в тексте хотя бы один термин. Шаблон, не уложившийся в предел, считается несовпавшим.
    /// </summary>
    public bool Matches(string text)
    {
        foreach (Regex term in _terms)
        {
            try
            {
                if (term.IsMatch(text))
                {
                    return true;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // Зависший шаблон не будит сессию и не роняет запись; остальные шаблоны проверяются дальше.
            }
        }

        return false;
    }
}
