namespace MeetingAssistant.Application.Meetings.Recording;

/// <summary>
/// Источник звука потерял устройство: звука больше не будет, а записанное цело.
/// </summary>
/// <remarks>
/// Отдельный тип, а не любое исключение источника: конвейер останавливает встречу штатно только на потере
/// устройства, и дефект кода не выдаёт себя за неё.
/// </remarks>
public sealed class AudioSourceException : Exception
{
    public AudioSourceException()
    {
    }

    public AudioSourceException(string message)
        : base(message)
    {
    }

    public AudioSourceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
