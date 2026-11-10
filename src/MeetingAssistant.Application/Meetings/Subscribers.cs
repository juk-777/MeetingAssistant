using MeetingAssistant.Domain.Meetings;
using MeetingAssistant.SharedKernel;

namespace MeetingAssistant.Application.Meetings;

internal sealed class WriteUtteranceToTranscript(ITranscriptWriter transcriptWriter)
    : IDomainEventSubscriber<UtteranceRecorded>
{
    public Task Handle(UtteranceRecorded domainEvent, CancellationToken cancellationToken) =>
        transcriptWriter.Append(domainEvent.Utterance, cancellationToken);
}

internal sealed class WriteWakeEvent(IWakeEventWriter wakeEventWriter)
    : IDomainEventSubscriber<SessionWakeRequested>
{
    public Task Handle(SessionWakeRequested domainEvent, CancellationToken cancellationToken) =>
        wakeEventWriter.AppendWake(domainEvent, cancellationToken);
}

internal sealed class WriteEndEvent(IWakeEventWriter wakeEventWriter)
    : IDomainEventSubscriber<MeetingStopped>
{
    public Task Handle(MeetingStopped domainEvent, CancellationToken cancellationToken) =>
        wakeEventWriter.AppendEnd(domainEvent, cancellationToken);
}
