using MeetingAssistant.Application.Abstractions.Messaging;
using MeetingAssistant.Domain.Meetings;

namespace MeetingAssistant.Application.Meetings.Recognize;

/// <summary>
/// Распознать законченную фразу канала и записать её репликой текущей встречи.
/// </summary>
/// <param name="Channel">Канал фразы.</param>
/// <param name="Span">Начало и длительность фразы от старта записи.</param>
/// <param name="Samples">Звук фразы: 16 кГц, моно, float.</param>
public sealed record RecognizeSpeech(Channel Channel, SpeechSpan Span, ReadOnlyMemory<float> Samples) : ICommand;
