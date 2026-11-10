namespace MeetingAssistant.SharedKernel;

/// <summary>
/// Корень агрегата — единственная точка входа к своим детям; копит доменные события до их отправки.
/// </summary>
public abstract class AggregateRoot<TId> : Entity<TId>
    where TId : struct
{
    private readonly List<IDomainEvent> _domainEvents = [];

    /// <summary>
    /// Отдаёт накопленные события в порядке подъёма и очищает очередь.
    /// </summary>
    public IReadOnlyList<IDomainEvent> DequeueDomainEvents()
    {
        IDomainEvent[] events = [.. _domainEvents];
        _domainEvents.Clear();
        return events;
    }

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);
}
