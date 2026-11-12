using MeetingAssistant.Application.Abstractions.Messaging;

namespace MeetingAssistant.Application.Meetings.Refine;

/// <summary>
/// Распознать звук законченной встречи вторым распознавателем во второй транскрипт рядом с первым.
/// </summary>
/// <param name="Directory">Каталог встречи.</param>
/// <param name="Progress">Ход для человека: после каждой фразы.</param>
public sealed record RefineMeeting(string Directory, IProgress<RefinePosition> Progress) : ICommand;

/// <summary>
/// Докуда дошло уточнение.
/// </summary>
/// <param name="Position">До какого места записи распознано.</param>
/// <param name="Duration">Длина записи.</param>
/// <param name="Utterances">Сколько строк записано.</param>
public sealed record RefinePosition(TimeSpan Position, TimeSpan Duration, int Utterances);
