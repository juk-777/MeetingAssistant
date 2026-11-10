namespace MeetingAssistant.Application.Abstractions.Messaging;

/// <summary>
/// Команда сценария без возвращаемого значения.
/// </summary>
public interface ICommand;

/// <summary>
/// Команда сценария, возвращающая <typeparamref name="TResponse"/>.
/// </summary>
public interface ICommand<TResponse>;
