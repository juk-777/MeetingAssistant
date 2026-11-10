using System.Collections.Concurrent;
using MeetingAssistant.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace MeetingAssistant.Application.Abstractions.Events;

/// <summary>
/// Доставляет события по одному и ждёт всех подписчиков события, прежде чем перейти к следующему:
/// порядок записи в файлы встречи — порядок подъёма событий в агрегате.
/// </summary>
internal sealed class DomainEventDispatcher(IServiceProvider serviceProvider) : IDomainEventDispatcher
{
    private static readonly ConcurrentDictionary<Type, Invoker> Invokers = new();

    public async Task Dispatch(IReadOnlyList<IDomainEvent> domainEvents, CancellationToken cancellationToken)
    {
        foreach (IDomainEvent domainEvent in domainEvents)
        {
            Invoker invoker = Invokers.GetOrAdd(domainEvent.GetType(), CreateInvoker);
            await invoker.Invoke(serviceProvider, domainEvent, cancellationToken);
        }
    }

    private static Invoker CreateInvoker(Type eventType) =>
        Activator.CreateInstance(typeof(Invoker<>).MakeGenericType(eventType)) as Invoker
        ?? throw new InvalidOperationException($"Не удалось создать доставщика события {eventType}.");

    private abstract class Invoker
    {
        public abstract Task Invoke(IServiceProvider services, IDomainEvent domainEvent, CancellationToken cancellationToken);
    }

    private sealed class Invoker<TEvent> : Invoker
        where TEvent : IDomainEvent
    {
        public override async Task Invoke(
            IServiceProvider services,
            IDomainEvent domainEvent,
            CancellationToken cancellationToken)
        {
            foreach (IDomainEventSubscriber<TEvent> subscriber in services.GetServices<IDomainEventSubscriber<TEvent>>())
            {
                await subscriber.Handle((TEvent)domainEvent, cancellationToken);
            }
        }
    }
}
