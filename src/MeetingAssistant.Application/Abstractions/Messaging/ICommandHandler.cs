using MeetingAssistant.SharedKernel;

namespace MeetingAssistant.Application.Abstractions.Messaging;

/// <summary>
/// Обработчик команды без значения.
/// </summary>
public interface ICommandHandler<in TCommand>
    where TCommand : ICommand
{
    Task<Result> Handle(TCommand command, CancellationToken cancellationToken);
}

/// <summary>
/// Обработчик команды со значением.
/// </summary>
public interface ICommandHandler<in TCommand, TResponse>
    where TCommand : ICommand<TResponse>
    where TResponse : notnull
{
    Task<Result<TResponse>> Handle(TCommand command, CancellationToken cancellationToken);
}
