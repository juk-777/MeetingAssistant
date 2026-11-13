using MeetingAssistant.Application.Meetings.Recording;
using MeetingAssistant.Domain.Meetings;

namespace MeetingAssistant.Application.Meetings.Refine;

/// <summary>
/// Подряд идущие фразы одного канала, склеенные в один кусок звука, и карта куска на время записи.
/// </summary>
/// <remarks>
/// Распознаватель берёт за окно ту же цену, что за меньший кусок: фраза по отдельности платила бы окно целиком.
/// Тишина между фразами в кусок не входит, поэтому время внутри куска переводится на время записи по фразам.
/// </remarks>
internal sealed class SpeechGroup
{
    // Метки распознавателя округлены, и отрезок может выйти без длины; длина в транскрипт не пишется.
    private static readonly TimeSpan MinDuration = TimeSpan.FromMilliseconds(1);

    private readonly List<DetectedPhrase> _phrases = [];

    public bool IsEmpty => _phrases.Count == 0;

    /// <summary>
    /// Сколько речи в куске.
    /// </summary>
    public TimeSpan Speech { get; private set; }

    /// <summary>
    /// Конец последней фразы в записи.
    /// </summary>
    public TimeSpan End => IsEmpty ? TimeSpan.Zero : _phrases[^1].Start + Duration(_phrases[^1]);

    /// <summary>
    /// Фраза помещается в окно; в пустой кусок помещается любая.
    /// </summary>
    public bool Fits(DetectedPhrase phrase, TimeSpan window) => IsEmpty || Speech + Duration(phrase) <= window;

    public void Add(DetectedPhrase phrase)
    {
        _phrases.Add(phrase);
        Speech += Duration(phrase);
    }

    public void Clear()
    {
        _phrases.Clear();
        Speech = TimeSpan.Zero;
    }

    public float[] Samples()
    {
        var samples = new float[_phrases.Sum(phrase => phrase.Samples.Length)];
        int offset = 0;

        foreach (DetectedPhrase phrase in _phrases)
        {
            phrase.Samples.Span.CopyTo(samples.AsSpan(offset));
            offset += phrase.Samples.Length;
        }

        return samples;
    }

    /// <summary>
    /// Отрезок куска — отрезком записи: начало и конец переводятся каждый по своей фразе.
    /// </summary>
    public SpeechSpan ToRecording(RecognizedSegment segment)
    {
        TimeSpan start = ToRecording(segment.Start, isEnd: false);
        TimeSpan end = ToRecording(segment.End, isEnd: true);

        return new SpeechSpan(start, end - start > MinDuration ? end - start : MinDuration);
    }

    // Конец, попавший ровно на стык фраз, принадлежит предыдущей фразе, начало — следующей.
    private TimeSpan ToRecording(TimeSpan inGroup, bool isEnd)
    {
        TimeSpan passed = TimeSpan.Zero;

        foreach (DetectedPhrase phrase in _phrases)
        {
            TimeSpan duration = Duration(phrase);

            if (inGroup < passed + duration || (isEnd && inGroup == passed + duration))
            {
                return phrase.Start + (inGroup > passed ? inGroup - passed : TimeSpan.Zero);
            }

            passed += duration;
        }

        // Метка за концом куска (округление распознавателя) — конец последней фразы.
        return End;
    }

    private static TimeSpan Duration(DetectedPhrase phrase) => SpeechAudio.Duration(phrase.Samples.Length);
}
