namespace MeetingAssistant.Domain.Meetings;

/// <summary>
/// Канал записи: роль говорящего задаёт источник звука, а не разметка голосов.
/// </summary>
public enum Channel
{
    /// <summary>
    /// Микрофон — тот, кто ведёт запись.
    /// </summary>
    Me = 1,

    /// <summary>
    /// Системный звук — собеседники на созвоне.
    /// </summary>
    Others = 2,
}
