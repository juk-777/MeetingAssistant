namespace MeetingAssistant.SharedKernel;

/// <summary>
/// Реакция на доменное событие вне агрегата.
/// </summary>
public interface IDomainEventSubscriber<in TEvent>
    where TEvent : IDomainEvent
{
    Task Handle(TEvent domainEvent, CancellationToken cancellationToken);
}
