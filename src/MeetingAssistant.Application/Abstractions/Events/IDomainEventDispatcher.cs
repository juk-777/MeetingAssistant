using MeetingAssistant.SharedKernel;

namespace MeetingAssistant.Application.Abstractions.Events;

/// <summary>
/// Доставляет события агрегата подписчикам в порядке подъёма.
/// </summary>
public interface IDomainEventDispatcher
{
    Task Dispatch(IReadOnlyList<IDomainEvent> domainEvents, CancellationToken cancellationToken);
}
