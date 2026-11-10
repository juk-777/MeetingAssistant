namespace MeetingAssistant.SharedKernel;

/// <summary>
/// Ожидаемый отказ сценария. <see cref="Code"/> — контракт для проверок, <see cref="Description"/> — для человека.
/// </summary>
public sealed record Error(string Code, string Description)
{
    public static readonly Error None = new(string.Empty, string.Empty);
}
