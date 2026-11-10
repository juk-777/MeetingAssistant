using MeetingAssistant.Application.Abstractions.Messaging;

namespace MeetingAssistant.Application.Meetings.Stop;

/// <summary>
/// Остановить текущую встречу.
/// </summary>
public sealed record StopMeeting : ICommand;
