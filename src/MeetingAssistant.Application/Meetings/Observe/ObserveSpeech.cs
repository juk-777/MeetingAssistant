using MeetingAssistant.Application.Abstractions.Messaging;
using MeetingAssistant.Domain.Meetings;

namespace MeetingAssistant.Application.Meetings.Observe;

/// <summary>
/// Сверить речь каналов с идущим ответом собеседников текущей встречи.
/// </summary>
/// <param name="Me">Речь канала «Я».</param>
/// <param name="Others">Речь канала «Собеседники».</param>
public sealed record ObserveSpeech(SpeechActivity Me, SpeechActivity Others) : ICommand;
