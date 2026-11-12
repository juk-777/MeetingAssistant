using MeetingAssistant.Application.Abstractions.Messaging;

namespace MeetingAssistant.Application.Meetings.Refine;

/// <summary>
/// Распознать звук законченной встречи вторым распознавателем во второй транскрипт рядом с первым.
/// </summary>
/// <param name="Directory">Каталог встречи.</param>
public sealed record RefineMeeting(string Directory) : ICommand;
