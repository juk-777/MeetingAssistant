namespace MeetingAssistant.SharedKernel;

/// <summary>
/// Нарушение доменного инварианта. <see cref="Code"/> стабилен и пригоден для проверок, текст сообщения — нет.
/// </summary>
public sealed class DomainException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
