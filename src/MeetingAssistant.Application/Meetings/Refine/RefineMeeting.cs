using MeetingAssistant.Application.Abstractions.Messaging;

namespace MeetingAssistant.Application.Meetings.Refine;

/// <summary>
/// Распознать звук законченной встречи вторым распознавателем во второй транскрипт рядом с первым.
/// </summary>
/// <param name="Directory">Каталог встречи.</param>
/// <param name="Progress">Ход для человека: после каждого распознанного куска.</param>
public sealed record RefineMeeting(string Directory, IProgress<RefinePosition> Progress) : ICommand;

/// <summary>
/// Докуда дошло уточнение.
/// </summary>
/// <param name="Position">Дальнее место записи, до которого распознано; назад не идёт.</param>
/// <param name="Duration">Длина записи.</param>
/// <param name="Utterances">Сколько строк записано.</param>
/// <param name="Speech">Сколько речи распознано: сумма длин фраз.</param>
/// <param name="Elapsed">Сколько идёт распознавание, без подготовки модели.</param>
public sealed record RefinePosition(TimeSpan Position, TimeSpan Duration, int Utterances, TimeSpan Speech, TimeSpan Elapsed);
