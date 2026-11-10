using MeetingAssistant.Application.Abstractions.Messaging;
using MeetingAssistant.SharedKernel;
using Microsoft.Extensions.Logging;

namespace MeetingAssistant.Application.Abstractions.Behaviors;

/// <summary>
/// Пишет в журнал начало и исход команды. Распознавание идёт по фразе, поэтому успех — Debug,
/// ожидаемый отказ — Warning с кодом.
/// </summary>
internal static partial class LoggingDecorator
{
    internal sealed class CommandHandler<TCommand>(
        ICommandHandler<TCommand> innerHandler,
        ILogger<CommandHandler<TCommand>> logger) : ICommandHandler<TCommand>
        where TCommand : ICommand
    {
        public async Task<Result> Handle(TCommand command, CancellationToken cancellationToken)
        {
            string commandName = typeof(TCommand).Name;
            LogProcessing(logger, commandName);

            Result result = await innerHandler.Handle(command, cancellationToken);
            LogOutcome(logger, commandName, result);

            return result;
        }
    }

    internal sealed class CommandHandler<TCommand, TResponse>(
        ICommandHandler<TCommand, TResponse> innerHandler,
        ILogger<CommandHandler<TCommand, TResponse>> logger) : ICommandHandler<TCommand, TResponse>
        where TCommand : ICommand<TResponse>
        where TResponse : notnull
    {
        public async Task<Result<TResponse>> Handle(TCommand command, CancellationToken cancellationToken)
        {
            string commandName = typeof(TCommand).Name;
            LogProcessing(logger, commandName);

            Result<TResponse> result = await innerHandler.Handle(command, cancellationToken);
            LogOutcome(logger, commandName, result);

            return result;
        }
    }

    private static void LogOutcome(ILogger logger, string commandName, Result result)
    {
        if (result.IsSuccess)
        {
            LogCompleted(logger, commandName);
        }
        else
        {
            LogFailed(logger, commandName, result.Error.Code, result.Error.Description);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Processing command {Command}")]
    private static partial void LogProcessing(ILogger logger, string command);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Completed command {Command}")]
    private static partial void LogCompleted(ILogger logger, string command);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Command {Command} failed with {ErrorCode}: {ErrorDescription}")]
    private static partial void LogFailed(ILogger logger, string command, string errorCode, string errorDescription);
}
