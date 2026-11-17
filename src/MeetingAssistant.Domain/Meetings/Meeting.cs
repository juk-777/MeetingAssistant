using MeetingAssistant.SharedKernel;

namespace MeetingAssistant.Domain.Meetings;

/// <summary>
/// Встреча: выдаёт сквозные номера реплик, копит речь с последнего события, решает, будить ли сессию, и следит за
/// концом ответа собеседников.
/// </summary>
public sealed class Meeting : AggregateRoot<Guid>
{
    private int _lastUtteranceNumber;
    private TimeSpan _speechSinceLastWake;

    // Идущий ответ собеседников: начало первой фразы, конец последней и её номер; нет фраз с прошлого конца — null.
    private TimeSpan? _answerStart;
    private TimeSpan _answerEnd;
    private int _answerUtteranceNumber;

    private Meeting(Guid id, string name, Glossary glossary, DateTimeOffset startedAt)
    {
        Id = id;
        Name = name;
        Glossary = glossary;
        StartedAt = startedAt;
    }

    public string Name { get; }

    public Glossary Glossary { get; }

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset? StoppedAt { get; private set; }

    public bool IsRunning => StoppedAt is null;

    public int LastUtteranceNumber => _lastUtteranceNumber;

    public static Meeting Start(string? name, Glossary glossary, DateTimeOffset startedAt)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException(MeetingErrors.NameRequired, "У встречи должно быть имя.");
        }

        return new Meeting(Guid.CreateVersion7(startedAt), name.Trim(), glossary, startedAt);
    }

    /// <summary>
    /// Записывает реплику со следующим номером и поднимает событие пробуждения, если есть повод.
    /// </summary>
    public Utterance Record(Channel channel, SpeechSpan span, string text, DateTimeOffset recognizedAt)
    {
        EnsureRunning();

        var utterance = new Utterance(_lastUtteranceNumber + 1, channel, span, text, recognizedAt);
        _lastUtteranceNumber = utterance.Number;
        _speechSinceLastWake += span.Duration;

        Raise(new UtteranceRecorded(utterance));

        if (channel == Channel.Others)
        {
            _answerStart ??= span.Start;
            _answerEnd = span.Start + span.Duration;
            _answerUtteranceNumber = utterance.Number;
        }

        IReadOnlyList<WakeReason> reasons = WakePolicy.Evaluate(utterance.Text, Glossary, _speechSinceLastWake);

        if (reasons.Count > 0)
        {
            _speechSinceLastWake = TimeSpan.Zero;
            Raise(new SessionWakeRequested(utterance.Number, reasons, recognizedAt));
        }

        return utterance;
    }

    /// <summary>
    /// Сверяет речь каналов с идущим ответом собеседников и поднимает <see cref="AnswerEnded"/>, если он кончился.
    /// </summary>
    public void Observe(SpeechActivity me, SpeechActivity others, DateTimeOffset at)
    {
        EnsureRunning();

        if (_answerStart is not { } answerStart)
        {
            return;
        }

        AnswerOutcome outcome = AnswerEndPolicy.Evaluate(answerStart, _answerEnd, me, others);

        if (outcome == AnswerOutcome.Continues)
        {
            return;
        }

        _answerStart = null;

        if (outcome == AnswerOutcome.Ended)
        {
            Raise(new AnswerEnded(_answerUtteranceNumber, at));
        }
    }

    public void Stop(DateTimeOffset at)
    {
        if (!IsRunning)
        {
            throw new DomainException(MeetingErrors.AlreadyStopped, "Встреча уже остановлена.");
        }

        StoppedAt = at;
        Raise(new MeetingStopped(_lastUtteranceNumber, at));
    }

    private void EnsureRunning()
    {
        if (!IsRunning)
        {
            throw new DomainException(MeetingErrors.Stopped, "Встреча остановлена и реплик не принимает.");
        }
    }
}
