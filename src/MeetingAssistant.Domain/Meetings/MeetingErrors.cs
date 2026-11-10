namespace MeetingAssistant.Domain.Meetings;

/// <summary>
/// Коды нарушений инвариантов встречи. Тесты и вызывающий код опираются на код, а не на текст сообщения.
/// </summary>
public static class MeetingErrors
{
    public const string NameRequired = "Meeting.NameRequired";
    public const string Stopped = "Meeting.Stopped";
    public const string AlreadyStopped = "Meeting.AlreadyStopped";
    public const string EmptyUtterance = "Meeting.EmptyUtterance";
    public const string InvalidSpeechSpan = "Meeting.InvalidSpeechSpan";
}
