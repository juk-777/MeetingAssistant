using MeetingAssistant.Application.Abstractions.Behaviors;
using MeetingAssistant.Application.Abstractions.Events;
using MeetingAssistant.Application.Abstractions.Messaging;
using MeetingAssistant.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeetingAssistant.Application;

/// <summary>
/// Регистрация сценариев: обработчики команд и подписчики событий находятся сканированием сборки.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        // Декораторы — открытые generic-реализации тех же интерфейсов: без фильтра сканирование регистрирует
        // их ещё и обработчиками, и на команду приходится два обработчика.
        services.Scan(scan => scan.FromAssembliesOf(typeof(DependencyInjection))
            .AddClasses(classes => classes.AssignableTo(typeof(ICommandHandler<>)).Where(IsClosed), publicOnly: false)
                .AsImplementedInterfaces()
                .WithTransientLifetime()
            .AddClasses(classes => classes.AssignableTo(typeof(ICommandHandler<,>)).Where(IsClosed), publicOnly: false)
                .AsImplementedInterfaces()
                .WithTransientLifetime()
            .AddClasses(classes => classes.AssignableTo(typeof(IQueryHandler<,>)).Where(IsClosed), publicOnly: false)
                .AsImplementedInterfaces()
                .WithTransientLifetime()
            .AddClasses(classes => classes.AssignableTo(typeof(IDomainEventSubscriber<>)).Where(IsClosed), publicOnly: false)
                .AsImplementedInterfaces()
                .WithTransientLifetime());

        services.TryDecorate(typeof(ICommandHandler<>), typeof(LoggingDecorator.CommandHandler<>));
        services.TryDecorate(typeof(ICommandHandler<,>), typeof(LoggingDecorator.CommandHandler<,>));

        services.AddTransient<IDomainEventDispatcher, DomainEventDispatcher>();
        services.TryAddSingleton(TimeProvider.System);

        return services;
    }

    private static bool IsClosed(Type type) => !type.IsGenericTypeDefinition;
}
