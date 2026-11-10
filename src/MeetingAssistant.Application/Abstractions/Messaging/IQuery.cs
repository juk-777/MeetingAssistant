namespace MeetingAssistant.Application.Abstractions.Messaging;

/// <summary>
/// Запрос без побочных эффектов, возвращающий <typeparamref name="TResponse"/>.
/// </summary>
public interface IQuery<TResponse>;
