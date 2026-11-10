using MeetingAssistant.Domain.Meetings;

namespace MeetingAssistant.Application.Meetings;

/// <summary>
/// Распознаёт фразу целиком: 16 кГц, моно, float.
/// </summary>
public interface ISpeechRecognizer
{
    Task<string> Recognize(ReadOnlyMemory<float> samples, CancellationToken cancellationToken);
}

/// <summary>
/// Каталог встречи на диске.
/// </summary>
public interface IMeetingStorage
{
    Task Create(Meeting meeting, CancellationToken cancellationToken);
}

/// <summary>
/// Встреча этого процесса и её след для скилла (<c>current.json</c>).
/// </summary>
public interface ICurrentMeeting
{
    Meeting? Meeting { get; }

    Task Set(Meeting meeting, CancellationToken cancellationToken);
}

/// <summary>
/// Дозапись реплик в транскрипт встречи.
/// </summary>
public interface ITranscriptWriter
{
    Task Append(Utterance utterance, CancellationToken cancellationToken);
}

/// <summary>
/// Дозапись событий, которые будят сессию, в журнал событий встречи.
/// </summary>
public interface IWakeEventWriter
{
    Task AppendWake(SessionWakeRequested wake, CancellationToken cancellationToken);

    Task AppendEnd(MeetingStopped stopped, CancellationToken cancellationToken);
}
