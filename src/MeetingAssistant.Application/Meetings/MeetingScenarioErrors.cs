using MeetingAssistant.Domain.Meetings;
using MeetingAssistant.SharedKernel;

namespace MeetingAssistant.Application.Meetings;

/// <summary>
/// Ожидаемые отказы сценариев встречи.
/// </summary>
public static class MeetingScenarioErrors
{
    public static readonly Error AlreadyRunning = new("Meeting.AlreadyRunning", "Встреча уже идёт.");

    public static readonly Error NotRunning = new("Meeting.NotRunning", "Встреча не идёт.");

    public static readonly Error NameRequired = new(MeetingErrors.NameRequired, "У встречи должно быть имя.");

    public const string AudioSourceFailedCode = "Meeting.AudioSourceFailed";

    public static Error AudioSourceFailed(string reason) =>
        new(AudioSourceFailedCode, $"Звук пропал, встреча остановлена: {reason}");
}
