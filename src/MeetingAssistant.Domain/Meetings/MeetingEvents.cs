using MeetingAssistant.SharedKernel;

namespace MeetingAssistant.Domain.Meetings;

/// <summary>
/// Реплика записана во встречу.
/// </summary>
public sealed record UtteranceRecorded(Utterance Utterance) : IDomainEvent;

/// <summary>
/// Реплика даёт повод разбудить сессию; поднимается после <see cref="UtteranceRecorded"/> той же реплики.
/// </summary>
public sealed record SessionWakeRequested(int UtteranceNumber, IReadOnlyList<WakeReason> Reasons, DateTimeOffset At)
    : IDomainEvent;

/// <summary>
/// Встреча остановлена; <see cref="LastUtteranceNumber"/> — 0, если реплик не было.
/// </summary>
public sealed record MeetingStopped(int LastUtteranceNumber, DateTimeOffset At) : IDomainEvent;
