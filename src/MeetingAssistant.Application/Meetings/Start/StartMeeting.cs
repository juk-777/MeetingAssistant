using MeetingAssistant.Application.Abstractions.Messaging;

namespace MeetingAssistant.Application.Meetings.Start;

/// <summary>
/// Начать встречу с именем и словарём; возвращает идентификатор встречи.
/// </summary>
/// <param name="Name">Имя встречи, из него строится каталог.</param>
/// <param name="GlossaryLines">Строки словаря: шаблон на строку, пустые пропускаются.</param>
public sealed record StartMeeting(string Name, IReadOnlyList<string> GlossaryLines) : ICommand<Guid>;
