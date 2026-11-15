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
    /// Шаблон пишет человек: без возврата сопоставление линейно по длине фразы и не может остановить
    /// распознавание встречи, а то, чего этот режим не умеет, отказывает при разборе — до встречи, а не во время.
    /// </summary>
    private const RegexOptions TermOptions =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;

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
                terms.Add(new Regex(pattern, TermOptions));
            }
            catch (ArgumentException exception)
            {
                return Result.Failure<Glossary>(new Error(
                    InvalidPatternCode,
                    $"Строка {lineNumber} словаря — не регулярное выражение: {exception.Message}"));
            }
            catch (NotSupportedException exception)
            {
                return Result.Failure<Glossary>(new Error(
                    InvalidPatternCode,
                    $"Строка {lineNumber} словаря — обратные ссылки, проверки вперёд и назад и атомарные группы "
                    + $"не поддерживаются: {exception.Message}"));
            }
        }

        return Result.Success(new Glossary(terms));
    }

    /// <summary>
    /// Есть ли в тексте хотя бы один термин.
    /// </summary>
    public bool Matches(string text) => _terms.Any(term => term.IsMatch(text));
}
